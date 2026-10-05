#!/usr/bin/env python3
"""Summarize and compare BenchmarkDotNet JSON reports (`*-report-full.json`) produced by eng/run-benchmarks.ps1.

  benchmark-summary.py summarize --results DIR --output FILE.md [--json FILE.json] [--manifest manifest.json]
                                  [--baselines Method1,Method2,...]
  benchmark-summary.py compare --baseline DIR --candidate DIR --output FILE.md [--json FILE.json] [--threshold 10]

`summarize` reports, for every scenario row, the mean, the 99.9% confidence half-width, the standard deviation, the
coefficient of variation, the spread between per-launch means, allocation per operation and Gen0 collections. Nothing is
hidden or averaged across scenarios. With `--baselines`, a Ratio column divides each mean by the mean of the first listed
method that exists in the same class and parameter combination (a derived figure without an error bar of its own).

`compare` pairs rows of two runs of the *same* harness by full benchmark name and flags every mean regression larger
than the threshold (default 10 percent, a trigger for investigation even when the row is noisy) and every allocation
increase. It never decides acceptance: the flags are inputs to a recorded human decision. Rows present on one side only
are listed, never fabricated.
"""

from __future__ import annotations

import argparse
import glob
import json
import math
import os
import sys


def fail(message: str) -> "None":
    print(f"benchmark-summary: {message}", file=sys.stderr)
    sys.exit(2)


def num(value):
    """A finite float, or None when BenchmarkDotNet reports NaN (for example one measured iteration has no variance)."""
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return number if math.isfinite(number) else None


def label(method: str, parameters: str) -> str:
    return f"{method}({parameters})" if parameters else method


def load_rows(directory: str) -> dict:
    paths = sorted(glob.glob(os.path.join(directory, "*-report-full.json")))
    if not paths:
        paths = sorted(glob.glob(os.path.join(directory, "*-report-full-compressed.json")))
    rows: dict = {}
    for path in paths:
        with open(path, encoding="utf-8") as handle:
            report = json.load(handle)
        for bench in report.get("Benchmarks", []):
            stats, memory = bench["Statistics"], bench.get("Memory") or {}
            if not stats or stats.get("N", 0) == 0:
                continue
            per_launch: dict = {}
            for item in bench.get("Measurements") or []:
                if item.get("IterationMode") == "Workload" and item.get("IterationStage") == "Result":
                    per_launch.setdefault(item["LaunchIndex"], []).append(item["Nanoseconds"] / item["Operations"])
            launch_means = {k: sum(v) / len(v) for k, v in per_launch.items() if v}
            spread = None
            if len(launch_means) >= 2:
                spread = (max(launch_means.values()) / min(launch_means.values()) - 1.0) * 100.0
            mean = num(stats["Mean"])
            if mean is None:
                continue
            interval = stats.get("ConfidenceInterval") or {}
            margin = num(interval.get("Margin"))
            if margin is None and num(stats.get("StandardError")) is not None:
                margin = 3.291 * num(stats.get("StandardError"))
            std_dev = num(stats.get("StandardDeviation"))
            operations = memory.get("TotalOperations") or 0
            gen0 = memory.get("Gen0Collections", 0)
            rows[bench["FullName"]] = {
                "fullName": bench["FullName"],
                "class": bench["FullName"].rsplit(".", 1)[0].replace("Meridian.Benchmarks.", ""),
                "method": bench.get("Method") or bench["FullName"].rsplit(".", 1)[-1],
                "parameters": bench.get("Parameters", "") or "",
                "meanNs": mean,
                "errorNs": margin,
                "stdDev": std_dev,
                "cvPercent": std_dev / mean * 100.0 if std_dev is not None and mean else None,
                "launchSpreadPercent": spread,
                "launches": len(launch_means),
                "n": stats["N"],
                "allocatedBytesPerOp": memory.get("BytesAllocatedPerOperation"),
                "gen0Per1000Ops": (gen0 / operations * 1000.0) if operations else None,
            }
    return rows


def fmt_ns(value) -> str:
    if value is None:
        return "-"
    if value >= 1_000_000:
        return f"{value / 1_000_000:,.3f} ms"
    if value >= 10_000:
        return f"{value / 1000:,.2f} us"
    return f"{value:,.2f} ns"


def pct(value) -> str:
    return "-" if value is None else f"{value:.1f}%"


def sort_key(row: dict):
    return (row["class"], row["method"], row["parameters"])


def summarize(args) -> None:
    rows = load_rows(args.results)
    if not rows:
        fail(f"no benchmark reports found in {args.results}")
    ordered = sorted(rows.values(), key=sort_key)
    baselines = [name for name in (args.baselines or "").split(",") if name]
    reference: dict = {}
    if baselines:
        for row in ordered:
            key = (row["class"], row["parameters"])
            if row["method"] in baselines and (key not in reference or baselines.index(row["method"]) < baselines.index(reference[key]["method"])):
                reference[key] = row
    for row in ordered:
        base = reference.get((row["class"], row["parameters"]))
        row["ratioToBaseline"] = None if base is None else row["meanNs"] / base["meanNs"]
        row["baselineMethod"] = None if base is None else base["method"]
    lines = ["# Benchmark summary", ""]
    if args.manifest:
        with open(args.manifest, encoding="utf-8") as handle:
            manifest = json.load(handle)
        lines += [
            f"Profile: {manifest.get('profile')} | filter: `{manifest.get('filter')}` | status: {manifest.get('status')}",
            f"Window: {manifest.get('window', {}).get('name')} | run id: {manifest.get('runId')}",
            "",
        ]
    lines += [
        "Mean, 99.9% confidence half-width, standard deviation (across measured iterations), coefficient of variation,",
        "launch spread (largest per-launch mean over smallest, minus one), allocation per operation, Gen0 per 1000 ops.",
    ]
    if baselines:
        lines += ["Ratio = mean divided by the mean of the baseline method of the same class and parameters (derived; no error bar)."]
    lines += [
        "",
        "| Scenario | Mean | +- 99.9% CI | StdDev | CV | Launch spread | Ratio | Alloc B/op | Gen0/1k ops |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    current = None
    for row in ordered:
        if row["class"] != current:
            current = row["class"]
            lines.append(f"| **{current}** | | | | | | | | |")
        gen0 = "-" if row["gen0Per1000Ops"] is None else f"{row['gen0Per1000Ops']:.2f}"
        alloc = "-" if row["allocatedBytesPerOp"] is None else str(row["allocatedBytesPerOp"])
        ratio = "-" if row["ratioToBaseline"] is None else (
            "1.00 (baseline)" if row["method"] == row["baselineMethod"] else f"{row['ratioToBaseline']:.2f}")
        lines.append(
            f"| {label(row['method'], row['parameters'])} | {fmt_ns(row['meanNs'])} | {fmt_ns(row['errorNs'])} | "
            f"{fmt_ns(row['stdDev'])} | {pct(row['cvPercent'])} | {pct(row['launchSpreadPercent'])} | {ratio} | {alloc} | {gen0} |")
    lines += ["", f"Rows: {len(ordered)}"]
    spreads = [r["launchSpreadPercent"] for r in ordered if r["launchSpreadPercent"] is not None]
    if spreads:
        lines.append(f"Median launch spread: {sorted(spreads)[len(spreads) // 2]:.1f}% | worst: {max(spreads):.1f}%")
    with open(args.output, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump({"rows": ordered}, handle, indent=2)
    print(f"summarized {len(ordered)} rows -> {args.output}")


def compare(args) -> None:
    baseline, candidate = load_rows(args.baseline), load_rows(args.candidate)
    if not baseline:
        fail(f"no benchmark reports found in baseline {args.baseline}")
    if not candidate:
        fail(f"no benchmark reports found in candidate {args.candidate}")
    paired = sorted(set(baseline) & set(candidate), key=lambda name: sort_key(baseline[name]))
    out_rows = []
    for name in paired:
        a, b = baseline[name], candidate[name]
        delta = (b["meanNs"] / a["meanNs"] - 1.0) * 100.0
        alloc_delta = None
        if a["allocatedBytesPerOp"] is not None and b["allocatedBytesPerOp"] is not None:
            alloc_delta = b["allocatedBytesPerOp"] - a["allocatedBytesPerOp"]
        flags = []
        if delta > args.threshold:
            flags.append(f"mean>+{args.threshold:g}%")
        if alloc_delta is not None and alloc_delta > 0:
            flags.append("alloc+")
        notes = []
        if alloc_delta is not None and alloc_delta < 0:
            notes.append("alloc-")
        out_rows.append({
            "fullName": name, "class": a["class"], "method": a["method"], "parameters": a["parameters"],
            "baselineMeanNs": a["meanNs"], "candidateMeanNs": b["meanNs"], "deltaPercent": delta,
            "baselineErrorNs": a["errorNs"], "candidateErrorNs": b["errorNs"],
            "baselineCvPercent": a["cvPercent"], "candidateCvPercent": b["cvPercent"],
            "baselineAlloc": a["allocatedBytesPerOp"], "candidateAlloc": b["allocatedBytesPerOp"],
            "allocDelta": alloc_delta, "flags": flags, "notes": notes,
        })
    unpaired = {
        "baselineOnly": sorted(label(baseline[n]["method"], baseline[n]["parameters"]) for n in set(baseline) - set(candidate)),
        "candidateOnly": sorted(label(candidate[n]["method"], candidate[n]["parameters"]) for n in set(candidate) - set(baseline)),
    }
    lines = [
        "# Paired benchmark comparison", "",
        f"Baseline: `{args.baseline}`", f"Candidate: `{args.candidate}`", "",
        f"Flags: mean regression above {args.threshold:g}% (investigate even when noisy) and any allocation increase.",
        "Only rows measured by the identical harness may be compared; the flags are not an acceptance decision.", "",
        "| Row | Baseline | Candidate | Delta | Baseline CV | Candidate CV | Alloc B/op (base -> cand) | Flags |",
        "|---|---:|---:|---:|---:|---:|---:|---|",
    ]
    for row in out_rows:
        alloc = "-" if row["allocDelta"] is None else f"{row['baselineAlloc']} -> {row['candidateAlloc']} ({row['allocDelta']:+d})"
        flag_text = ", ".join(row["flags"] + row["notes"])
        lines.append(
            f"| {row['class']}.{label(row['method'], row['parameters'])} | {fmt_ns(row['baselineMeanNs'])} | "
            f"{fmt_ns(row['candidateMeanNs'])} | {row['deltaPercent']:+.1f}% | {pct(row['baselineCvPercent'])} | "
            f"{pct(row['candidateCvPercent'])} | {alloc} | {flag_text} |")
    flagged = [r for r in out_rows if r["flags"]]
    lines += ["", f"Paired rows: {len(out_rows)}; flagged: {len(flagged)}; "
              f"baseline only: {len(unpaired['baselineOnly'])}; candidate only: {len(unpaired['candidateOnly'])}"]
    for row in flagged:
        lines.append(f"- {row['class']}.{label(row['method'], row['parameters'])}: {row['deltaPercent']:+.1f}% mean, "
                     f"alloc {row['allocDelta'] if row['allocDelta'] is not None else 'n/a'}, flags {', '.join(row['flags'])}")
    for side, names in unpaired.items():
        if names:
            lines.append(f"- {side}: {', '.join(names)}")
    with open(args.output, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump({"threshold": args.threshold, "rows": out_rows, "unpaired": unpaired}, handle, indent=2)
    print(f"compared {len(out_rows)} rows ({len(flagged)} flagged) -> {args.output}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    s = sub.add_parser("summarize")
    s.add_argument("--results", required=True)
    s.add_argument("--output", required=True)
    s.add_argument("--json")
    s.add_argument("--manifest")
    s.add_argument("--baselines", help="comma separated method names; the first one present in a class and parameter group is its ratio baseline")
    s.set_defaults(func=summarize)
    c = sub.add_parser("compare")
    c.add_argument("--baseline", required=True)
    c.add_argument("--candidate", required=True)
    c.add_argument("--output", required=True)
    c.add_argument("--json")
    c.add_argument("--threshold", type=float, default=10.0)
    c.set_defaults(func=compare)
    args = parser.parse_args()
    if not math.isfinite(getattr(args, "threshold", 10.0)):
        fail("threshold must be finite")
    args.func(args)


if __name__ == "__main__":
    main()

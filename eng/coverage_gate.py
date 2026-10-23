#!/usr/bin/env python3
"""Coverage measurement, ratchet and hot-path gate over Cobertura files written by coverlet.collector.

Measurement
  Every repository source file under ``src/`` is measured from the union of all supplied Cobertura documents (one per
  test project and run). A line is covered when any method instance recorded a hit. A branch unit is one jump condition
  of one method on one line; it is covered when any run took it. The aggregate is computed from counts, never from a
  mean of percentages. Nothing is excluded except build output (obj/, bin/): a file that no run touched still counts
  with its uncovered lines. ``uncoveredLines`` and ``partialBranchLines`` list the gaps per file.

Gate (``check``)
  * Ratchet: no baseline file may fall below its recorded line or branch floor, and the aggregate floors hold. Floors
    are rounded down to one decimal so unchanged coverage never regresses. ``baseline`` only ever raises floors. A file
    (or the aggregate) whose measurement has no lines or branches where the baseline records a non-zero floor fails.
  * Hot paths: every file listed under ``hotPaths`` must reach the line and branch targets (90 / 80; a baseline may
    raise them, never lower them). There are no waivers: a baseline that declares any fails as invalid input. A hot path
    that is absent from the coverage data, or present without measured lines, fails, so the gate cannot be satisfied by
    dropping a file from the measurement.
  * A baseline file missing from the measurement fails; new files are reported (and must be added to the baseline).

Exit codes: 0 PASS, 1 FAIL, 2 invalid baseline or input.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

SCHEMA = "meridian-coverage-baseline/1"
DEFAULT_TARGETS = {"line": 90.0, "branch": 80.0}
METRICS = ("line", "branch")


class BaselineError(Exception):
    """The baseline or its waivers are malformed (exit code 2)."""


def _percent(covered: int, total: int):
    return None if total == 0 else 100.0 * covered / total


def _floor1(value):
    return None if value is None else math.floor(value * 10 + 1e-9) / 10


def _relative(filename: str, repo: str):
    path = filename.replace("\\", "/")
    prefix = repo.replace("\\", "/").rstrip("/") + "/"
    if not path.startswith(prefix):
        return None
    relative = path[len(prefix):]
    # Build output (obj/ and bin/, which includes generator-injected attribute sources) is not repository source.
    if "/obj/" in relative or "/bin/" in relative:
        return None
    return relative if relative.startswith("src/") and relative.endswith(".cs") else None


def _resolve(filename: str, sources: list[str]) -> str:
    """coverlet writes class filenames relative to the project source roots listed in <sources>."""
    normalized = filename.replace("\\", "/")
    if normalized.startswith("/") or (len(normalized) > 2 and normalized[1] == ":"):
        return normalized
    roots = [s.replace("\\", "/").rstrip("/") for s in sources]
    for root in roots:
        if Path(root, normalized).is_file():
            return f"{root}/{normalized}"
    return f"{roots[0]}/{normalized}" if roots else normalized


def measure(documents: list[Path], repo: str) -> dict:
    lines: dict[str, dict[int, bool]] = {}
    # (line, class, method, signature, condition) -> [targets, best taken fraction]. Taking the best fraction over runs
    # is conservative: two runs that took different targets of one jump are not added together.
    branches: dict[str, dict[tuple, list]] = {}
    for document in documents:
        root = ET.parse(document).getroot()
        sources = [e.text.strip() for e in root.iterfind("./sources/source") if e.text and e.text.strip()]
        for cls in root.iter("class"):
            relative = _relative(_resolve(cls.get("filename", ""), sources), repo)
            if relative is None:
                continue
            file_lines = lines.setdefault(relative, {})
            file_branches = branches.setdefault(relative, {})
            methods = cls.findall("./methods/method")
            units = [(m.get("name"), m.get("signature"), m.findall("./lines/line")) for m in methods]
            if not methods:
                units = [("", "", cls.findall("./lines/line"))]
            for name, signature, line_elements in units:
                for line in line_elements:
                    number = int(line.get("number"))
                    file_lines[number] = file_lines.get(number, False) or int(line.get("hits", "0")) > 0
                    conditions = line.findall("./conditions/condition")
                    if not conditions:
                        continue
                    declared = line.get("condition-coverage", "")
                    total = int(declared.rsplit("/", 1)[1].rstrip(")")) if "/" in declared else 2 * len(conditions)
                    targets = total / len(conditions)
                    for condition in conditions:
                        key = (number, cls.get("name"), name, signature, condition.get("number"))
                        fraction = float(condition.get("coverage", "0%").strip().rstrip("%") or 0) / 100.0
                        entry = file_branches.setdefault(key, [targets, 0.0])
                        entry[1] = max(entry[1], fraction)
    files = {}
    totals = {"lines": [0, 0], "branches": [0, 0]}
    for path in sorted(lines):
        covered_lines = sum(1 for hit in lines[path].values() if hit)
        branch_total = round(sum(t for t, _ in branches[path].values()))
        branch_covered = round(sum(t * f for t, f in branches[path].values()))
        files[path] = {
            "lines": {"covered": covered_lines, "total": len(lines[path]), "percent": _percent(covered_lines, len(lines[path]))},
            "branches": {"covered": branch_covered, "total": branch_total, "percent": _percent(branch_covered, branch_total)},
            "uncoveredLines": sorted(n for n, hit in lines[path].items() if not hit),
            "partialBranchLines": sorted({k[0] for k, (t, f) in branches[path].items() if f < 1.0}),
        }
        totals["lines"][0] += covered_lines
        totals["lines"][1] += len(lines[path])
        totals["branches"][0] += branch_covered
        totals["branches"][1] += branch_total
    return {"files": files, "aggregate": {k: {"covered": v[0], "total": v[1], "percent": _percent(*v)} for k, v in totals.items()}}


def _current(entry: dict, metric: str):
    return entry["lines" if metric == "line" else "branches"]["percent"]


def _validated_targets(baseline: dict) -> dict:
    if baseline.get("waivers"):
        raise BaselineError("coverage waivers are not accepted: a hot path must meet its targets with real tests")
    targets = {**DEFAULT_TARGETS, **baseline.get("targets", {})}
    for metric in METRICS:
        if not isinstance(targets[metric], (int, float)) or targets[metric] < DEFAULT_TARGETS[metric]:
            raise BaselineError(f"the {metric} target {targets[metric]} is below the required {DEFAULT_TARGETS[metric]}")
    return targets


MAX_TOLERANCE = 5.0


def check(baseline: dict, measured: dict) -> dict:
    targets = _validated_targets(baseline)
    # Coverage of timing-dependent code differs by a few lines between identical runs; the baseline may declare a small
    # tolerance (percentage points) for the ratchet floors. It never applies to the hot-path targets.
    tolerance = float(baseline.get("tolerance", 0.0))
    if not 0.0 <= tolerance <= MAX_TOLERANCE:
        raise BaselineError(f"tolerance must be between 0 and {MAX_TOLERANCE} percentage points")
    files = measured["files"]
    regressions, hot_failures, missing = [], [], []
    for path, floors in sorted(baseline.get("files", {}).items()):
        if path not in files:
            missing.append(path)
            continue
        for metric in METRICS:
            floor, current = floors.get(metric), _current(files[path], metric)
            if floor is not None and ((current is None and floor > 0) or (current is not None and current + tolerance + 1e-9 < floor)):
                regressions.append({"file": path, "metric": metric, "floor": floor, "current": None if current is None else round(current, 2)})
    for metric in METRICS:
        floor = baseline.get("aggregate", {}).get(metric)
        current = measured["aggregate"]["lines" if metric == "line" else "branches"]["percent"]
        if floor is not None and ((current is None and floor > 0) or (current is not None and current + tolerance + 1e-9 < floor)):
            regressions.append({"file": "aggregate", "metric": metric, "floor": floor, "current": None if current is None else round(current, 2)})
    hot_report = []
    for path in baseline.get("hotPaths", []):
        if path not in files:
            hot_failures.append({"file": path, "metric": "present", "percent": None, "target": None})
            continue
        row = {"file": path}
        if files[path]["lines"]["total"] == 0:
            hot_failures.append({"file": path, "metric": "line", "percent": None, "target": targets["line"]})
        for metric in METRICS:
            percent = _current(files[path], metric)
            row[metric] = None if percent is None else round(percent, 2)
            if percent is not None and percent + 1e-9 < targets[metric]:
                hot_failures.append({"file": path, "metric": metric, "percent": round(percent, 2), "target": targets[metric]})
        hot_report.append(row)
    new_files = sorted(set(files) - set(baseline.get("files", {})))
    failed = bool(regressions or hot_failures or missing)
    return {"status": "FAIL" if failed else "PASS", "ratchetRegressions": regressions, "hotPathFailures": hot_failures,
            "missingFiles": missing, "newFiles": new_files, "hotPaths": hot_report, "targets": targets,
            "aggregate": measured["aggregate"]}


def raise_baseline(existing: dict, measured: dict, hot_paths: list[str] | None = None) -> dict:
    """Return a baseline whose floors are max(existing, measured rounded down). Hot paths are preserved; waivers are refused."""
    _validated_targets(existing)
    kept_hot = list(existing.get("hotPaths", []))
    updated = {"schema": SCHEMA, "tolerance": existing.get("tolerance", 0.0), "targets": {**DEFAULT_TARGETS, **existing.get("targets", {})},
               "aggregate": dict(existing.get("aggregate", {})), "files": {p: dict(f) for p, f in existing.get("files", {}).items()},
               "hotPaths": kept_hot + [p for p in (hot_paths or []) if p not in kept_hot]}
    for path, entry in measured["files"].items():
        floors = updated["files"].setdefault(path, {"line": 0.0, "branch": None})
        for metric in METRICS:
            current = _floor1(_current(entry, metric))
            if current is not None:
                floors[metric] = max(floors.get(metric) or 0.0, current)
    for metric in METRICS:
        current = _floor1(measured["aggregate"]["lines" if metric == "line" else "branches"]["percent"])
        if current is not None:
            updated["aggregate"][metric] = max(updated["aggregate"].get(metric) or 0.0, current)
    updated["files"] = dict(sorted(updated["files"].items()))
    return updated


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def render(result: dict) -> str:
    lines = [f"COVERAGE GATE {result['status']}"]
    for item in result["ratchetRegressions"]:
        lines.append(f"  ratchet: {item['file']} {item['metric']} {item['current']}% < floor {item['floor']}%")
    for item in result["hotPathFailures"]:
        lines.append(f"  hot path: {item['file']} {item['metric']} {item['percent']} (target {item['target']})")
    for item in result["missingFiles"]:
        lines.append(f"  missing from coverage data: {item}")
    if result["newFiles"]:
        lines.append(f"  new files (add to the baseline): {len(result['newFiles'])}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("measure", "check", "baseline"):
        sub = commands.add_parser(name)
        sub.add_argument("--repo", required=True)
        sub.add_argument("--output", type=Path)
        sub.add_argument("documents", nargs="*", type=Path)
        if name == "check":
            sub.add_argument("--baseline", dest="baseline", type=Path, required=True)
        if name == "baseline":
            sub.add_argument("--existing", dest="baseline", type=Path)
            sub.add_argument("--hot-path", action="append", default=[])
    args = parser.parse_args(argv)
    if not args.documents:
        print("no Cobertura documents supplied; a coverage gate cannot pass without measurements", file=sys.stderr)
        return 1 if args.command == "check" else 2
    try:
        measured = measure(args.documents, args.repo)
        if args.command == "measure":
            text = json.dumps(measured, indent=2) + "\n"
            args.output.write_text(text, encoding="utf-8") if args.output else print(text)
            return 0
        existing = {}
        if args.baseline and args.baseline.is_file():
            existing = json.loads(args.baseline.read_text(encoding="utf-8"))
        if args.command == "baseline":
            text = json.dumps(raise_baseline(existing, measured, args.hot_path), indent=2) + "\n"
            args.output.write_text(text, encoding="utf-8") if args.output else print(text)
            return 0
        result = check(existing, measured)
        result["repo"] = args.repo
        result["baselineSha256"] = _sha256(args.baseline)
        result["documents"] = [{"path": str(d), "sha256": _sha256(d)} for d in args.documents]
    except (BaselineError, ET.ParseError, OSError, ValueError) as error:
        print(f"coverage gate input error: {error}", file=sys.stderr)
        return 2
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(render(result))
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())

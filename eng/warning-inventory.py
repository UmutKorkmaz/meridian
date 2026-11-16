#!/usr/bin/env python3
"""Summarise MSBuild compiler/analyzer warnings per project and per diagnostic code, and enforce a ratchet baseline.

Commands
  inventory  Parse one or more MSBuild console logs (any verbosity that prints ``file(line,col): warning CODE: ...``)
             and write a JSON inventory: totals, per-code counts, per-project counts and per-project code sets.
  baseline   Write ``eng/warning-baseline.json`` (the committed ratchet) from the same logs.
  check      Compare logs with the committed baseline. Fails when a project gains a warning code, a code gains
             unique locations in a project, a project appears that the baseline does not know, or a warning line is
             present that this parser cannot read. Fewer warnings never fail; lower the baseline to keep the gain.

The counted unit is a *unique location*: project + file + line + column + code. Repetitions caused by multi-targeting
and by MSBuild printing a warning more than once do not inflate the count. Line numbers are part of the key so a
location moved within a file by an unrelated edit is still a different location, but only the per-project/per-code
*counts* are compared, so unrelated edits that shift lines never fail the ratchet.

The ratchet never relaxes severities, never adds NoWarn and cannot be satisfied by deleting the warning from the log
(an empty parse of a non-empty baseline is a failure).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

SCHEMA_VERSION = 1

WARNING = re.compile(
    r"^(?P<file>.+?)\((?P<line>\d+),(?P<col>\d+)\): warning (?P<code>[A-Za-z]+[0-9]+): (?P<message>.*?) "
    r"\[(?P<project>[^\]]+?)(?:::TargetFramework=(?P<tfm>[^\]]+))?\]\s*$"
)
# Warnings that carry no source location (for example NETSDK/NU/MSB). They are still counted, keyed by message.
LOCATIONLESS = re.compile(
    r"^(?:.*?: )?warning (?P<code>[A-Za-z]+[0-9]+): (?P<message>.*?)(?: \[(?P<project>[^\]]+?)(?:::TargetFramework=(?P<tfm>[^\]]+))?\])?\s*$"
)
LOOSE_WARNING = re.compile(r"\bwarning\s+[A-Za-z]{2,}[0-9]+\b")
# Normal-verbosity MSBuild output prefixes node lines with e.g. "29>"; its final
# summary repeats the same diagnostic without that logger prefix.
MSBUILD_NODE_PREFIX = re.compile(r"^[0-9]+>")


def without_node_prefix(raw: str) -> str:
    return MSBUILD_NODE_PREFIX.sub("", raw.strip(), count=1).strip()


def relative(path: str, repo: str) -> str:
    prefix = repo.rstrip("/") + "/"
    return path[len(prefix):] if path.startswith(prefix) else path


def parse(logs: list[Path], repo: str):
    """Return (unique, unparsed). ``unique`` maps (project, file, line, col, code) to {'message', 'tfms'}."""
    unique: dict[tuple, dict] = {}
    unparsed: list[str] = []
    for log in logs:
        for raw in log.read_text(encoding="utf-8", errors="replace").splitlines():
            line = without_node_prefix(raw)
            match = WARNING.match(line)
            if match:
                key = (
                    relative(match["project"], repo),
                    relative(match["file"], repo),
                    int(match["line"]),
                    int(match["col"]),
                    match["code"],
                )
                entry = unique.setdefault(key, {"message": re.sub(r"\s*\(https?://[^)]*\)\s*$", "", match["message"]).strip(), "tfms": set()})
                entry["tfms"].add(match["tfm"] or "none")
                continue
            if not LOOSE_WARNING.search(line):
                continue
            loose = LOCATIONLESS.match(line)
            if loose and loose["project"]:
                key = (relative(loose["project"], repo), "", 0, 0, loose["code"])
                entry = unique.setdefault(key, {"message": loose["message"].strip(), "tfms": set()})
                entry["tfms"].add(loose["tfm"] or "none")
                continue
            # MSBuild's own "N Warning(s)" summary line never matches LOOSE_WARNING; anything else is unreadable.
            unparsed.append(line)
    return unique, unparsed


def summarise(unique: dict) -> dict:
    by_code: dict[str, int] = defaultdict(int)
    by_project: dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))
    for (project, _file, _line, _col, code) in unique:
        by_code[code] += 1
        by_project[project][code] += 1
    projects = {
        project: {"total": sum(codes.values()), "codes": dict(sorted(codes.items()))}
        for project, codes in sorted(by_project.items())
    }
    return {
        "total": sum(by_code.values()),
        "byCode": dict(sorted(by_code.items())),
        "projects": projects,
    }


def inventory_document(unique: dict, unparsed: list[str], logs: list[Path]) -> dict:
    document = {"schemaVersion": SCHEMA_VERSION, "logs": [str(l) for l in logs], "unit": "unique project+file+line+col+code"}
    document.update(summarise(unique))
    document["unparsedLines"] = len(unparsed)
    document["locations"] = [
        {
            "project": project,
            "file": file,
            "line": line,
            "column": col,
            "code": code,
            "message": entry["message"],
            "tfms": sorted(entry["tfms"]),
        }
        for (project, file, line, col, code), entry in sorted(unique.items())
    ]
    return document


def baseline_document(unique: dict, scope: str) -> dict:
    summary = summarise(unique)
    return {
        "schemaVersion": SCHEMA_VERSION,
        "scope": scope,
        "unit": "unique project+file+line+col+code (multi-target and duplicate prints collapsed)",
        "rule": (
            "A project may not gain a warning code, may not exceed its per-code count, and no new project may "
            "appear with warnings. Fewer warnings pass; lower this file in the same change that removes them."
        ),
        "total": summary["total"],
        "byCode": summary["byCode"],
        "projects": summary["projects"],
    }


def check(unique: dict, unparsed: list[str], baseline: dict) -> tuple[list[str], dict]:
    failures: list[str] = []
    if unparsed:
        failures.extend(f"unparsed warning line (output format changed?): {line[:200]}" for line in unparsed[:20])
        failures.append(f"{len(unparsed)} unparsed warning line(s); the ratchet would not see them")
    summary = summarise(unique)
    if not unique and baseline["total"]:
        failures.append(
            f"no warnings were parsed but the baseline records {baseline['total']}; refusing to treat a vanished "
            "warning set as success (build output missing or its format changed)"
        )
    known = baseline["projects"]
    for project, current in summary["projects"].items():
        allowed = known.get(project)
        if allowed is None:
            failures.append(f"NEW project with warnings: {project} ({current['total']})")
            continue
        for code, count in current["codes"].items():
            limit = allowed["codes"].get(code)
            if limit is None:
                failures.append(f"NEW code {code} in {project} ({count})")
            elif count > limit:
                failures.append(f"INCREASE {code} in {project}: {count} > baseline {limit}")
    improved = []
    for project, allowed in known.items():
        current = summary["projects"].get(project, {"codes": {}})
        for code, limit in allowed["codes"].items():
            count = current["codes"].get(code, 0)
            if count < limit:
                improved.append({"project": project, "code": code, "baseline": limit, "current": count})
    return failures, {"improved": improved, "current": summary}


BUILT = re.compile(r"^\s*(?P<name>[A-Za-z0-9_.\-]+) -> ")


def built_project_names(logs: list[Path]) -> set[str]:
    built = set()
    for log in logs:
        for raw in log.read_text(encoding="utf-8", errors="replace").splitlines():
            match = BUILT.match(without_node_prefix(raw))
            if match:
                built.add(match["name"])
    return built


def solution_projects(solution: Path) -> list[str]:
    """Every C# project a solution build compiles (solution folders are not projects)."""
    text = solution.read_text(encoding="utf-8-sig")
    return sorted(m.replace("\\", "/") for m in re.findall(r'^Project\("[^"]*"\)\s*=\s*"[^"]*",\s*"([^"]+\.csproj)"', text, re.MULTILINE))


def missing_built_projects(logs: list[Path], baseline: dict, solution: Path | None = None) -> list[str]:
    """Projects with no ``Name -> output`` line: they were not built, so absent warnings prove nothing.

    With a solution every project in it is required (a project without warnings can still gain one); without, only the
    baseline projects are.
    """
    required = solution_projects(solution) if solution else list(baseline["projects"])
    return sorted({Path(project).stem for project in required} - built_project_names(logs))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("inventory", "baseline", "check"):
        command = sub.add_parser(name)
        command.add_argument("--repo", required=True, help="repository root used to relativise paths")
        command.add_argument("--output", help="JSON file to write (inventory/baseline) or report (check)")
        command.add_argument("logs", nargs="+", type=Path)
        if name == "baseline":
            command.add_argument("--scope", default="Release build of Meridian.sln, every target framework")
        if name == "check":
            command.add_argument("--baseline", required=True, type=Path)
            command.add_argument(
                "--require-built-projects",
                action="store_true",
                help="fail unless every required project has a 'Name -> output' build line in the logs (rejects partial logs; "
                "it cannot detect an incremental build, which the producer must exclude with --no-incremental)",
            )
            command.add_argument("--solution", type=Path, help="with --require-built-projects: require every project of this solution")
    args = parser.parse_args(argv)

    unique, unparsed = parse(args.logs, args.repo)
    if args.command == "inventory":
        document = inventory_document(unique, unparsed, args.logs)
        if args.output:
            Path(args.output).write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        print(f"INVENTORY total={document['total']} codes={len(document['byCode'])} projects={len(document['projects'])} unparsed={len(unparsed)}")
        return 0 if not unparsed else 1
    if args.command == "baseline":
        if unparsed:
            print(f"FAIL {len(unparsed)} unparsed warning line(s); refusing to write a baseline that hides them")
            return 1
        document = baseline_document(unique, args.scope)
        Path(args.output or "eng/warning-baseline.json").write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        print(f"WROTE baseline total={document['total']} projects={len(document['projects'])}")
        return 0

    baseline = json.loads(args.baseline.read_text(encoding="utf-8"))
    failures, report = check(unique, unparsed, baseline)
    report["missingProjects"] = []
    report["repo"] = args.repo
    if args.require_built_projects:
        report["missingProjects"] = missing_built_projects(args.logs, baseline, args.solution)
        scope = "solution" if args.solution else "baseline"
        failures.extend(f"{scope} project not built in this log: {name}" for name in report["missingProjects"])
    if args.output:
        Path(args.output).write_text(json.dumps(dict(report, failures=failures), indent=2) + "\n", encoding="utf-8")
    for failure in failures:
        print(f"FAIL {failure}")
    if failures:
        return 1
    print(f"PASS warnings total={report['current']['total']} baseline={baseline['total']} improved={len(report['improved'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

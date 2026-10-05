#!/usr/bin/env python3
"""Documentation checks used by eng/verify-docs.ps1 (standard library only).

Subcommands:
  links     Crawl generated HTML (plus toc.json) and fail on broken internal paths/fragments.
  snippets  Require every C# fence in the given Markdown files to be a compiled region or
            explicitly illustrative, and compare compiled fences with their source region.
  warnings  Normalize MSBuild warnings and compare them with a committed baseline.
  surface   Compare DocFX API metadata produced from different target frameworks.
  mdlinks   Check relative links in repository Markdown (README files) with exact case.
  docfx-log Summarize a DocFX structured JSON log.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

EXTERNAL_SCHEMES = ("http:", "https:", "mailto:", "data:", "javascript:", "tel:", "ftp:")


class _PageParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.ids: set[str] = set()
        self.duplicates: set[str] = set()
        self.refs: list[str] = []

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if values.get("id"):
            if values["id"] in self.ids:
                self.duplicates.add(values["id"])
            self.ids.add(values["id"])
        if tag == "a" and values.get("name"):
            self.ids.add(values["name"])
        if tag in ("a", "link", "area") and values.get("href") is not None:
            self.refs.append(values["href"])
        if tag in ("img", "script", "iframe", "source") and values.get("src") is not None:
            self.refs.append(values["src"])
        if tag == "meta" and values.get("name") in ("docfx:tocrel", "docfx:navrel") and values.get("content"):
            self.refs.append(values["content"])


def _exact_case_exists(root: Path, relative: str) -> str | None:
    """Return None when the path exists with exact case, else a reason."""
    current = root
    parts = [p for p in relative.split("/") if p not in ("", ".")]
    for index, part in enumerate(parts):
        if not current.is_dir():
            return "missing-target"
        names = os.listdir(current)
        if part in names:
            current = current / part
            continue
        if part.lower() in (n.lower() for n in names):
            return "case-mismatch"
        return "missing-target"
    return None


def _toc_hrefs(data) -> list[str]:
    found = []
    if isinstance(data, dict):
        for key in ("href", "topicHref", "tocHref"):
            if isinstance(data.get(key), str):
                found.append(data[key])
        for value in data.values():
            found.extend(_toc_hrefs(value))
    elif isinstance(data, list):
        for value in data:
            found.extend(_toc_hrefs(value))
    return found


def cmd_links(args) -> int:
    root = Path(args.root).resolve()
    prefix = args.prefix
    if prefix is not None and not (prefix.startswith("/") and prefix.endswith("/")):
        raise SystemExit("--prefix must start and end with '/'")
    pages: dict[str, _PageParser] = {}
    sources: dict[str, list[str]] = {}
    for path in sorted(root.rglob("*")):
        rel = path.relative_to(root).as_posix()
        if path.suffix.lower() in (".html", ".htm"):
            parser = _PageParser()
            parser.feed(path.read_text(encoding="utf-8", errors="replace"))
            pages[rel] = parser
            sources[rel] = parser.refs
        elif path.name == "toc.json":
            sources[rel] = _toc_hrefs(json.loads(path.read_text(encoding="utf-8")))
    broken, external, internal = [], set(), 0
    generated_duplicates = []
    for page, parser in pages.items():
        generated = args.generated_api_dir is not None and args.generated_api_dir in Path(page).parts[:-1]
        for duplicate in sorted(parser.duplicates):
            item = {"page": page, "href": "#" + duplicate, "reason": "duplicate-id"}
            # DocFX's ManagedReference template repeats the overload-group anchor on each overload; that is
            # template output, recorded separately. Authored pages must never repeat an id.
            (generated_duplicates if generated else broken).append(item)
    for page, refs in sources.items():
        page_dir = Path(page).parent.as_posix()
        for ref in refs:
            ref = ref.strip()
            if not ref:
                continue
            lowered = ref.lower()
            if lowered.startswith(EXTERNAL_SCHEMES) or ref.startswith("//"):
                if lowered.startswith(("http:", "https:")):
                    external.add(ref)
                continue
            internal += 1
            parts = urlsplit(ref)
            path_part = unquote(parts.path)
            fragment = unquote(parts.fragment)
            if path_part.startswith("/"):
                if prefix is not None:
                    if not path_part.startswith(prefix):
                        broken.append({"page": page, "href": ref, "reason": "outside-prefix"})
                        continue
                    target = path_part[len(prefix):]
                else:
                    target = path_part.lstrip("/")
            elif path_part == "":
                target = page
            else:
                target = os.path.normpath(os.path.join(page_dir, path_part)).replace(os.sep, "/")
                if target.startswith(".."):
                    broken.append({"page": page, "href": ref, "reason": "outside-root"})
                    continue
            if target in ("", ".") or path_part.endswith("/"):
                target = (target.rstrip("/") + "/index.html").lstrip("/") if target not in ("", ".") else "index.html"
            reason = _exact_case_exists(root, target)
            if reason is None and (root / target).is_dir():
                target = target.rstrip("/") + "/index.html"
                reason = _exact_case_exists(root, target)
            if reason:
                broken.append({"page": page, "href": ref, "reason": reason})
                continue
            if fragment:
                parsed = pages.get(target)
                if parsed is None or fragment not in parsed.ids:
                    broken.append({"page": page, "href": ref, "reason": "missing-fragment"})
    report = {
        "root": str(root),
        "prefix": prefix,
        "pages": len(pages),
        "internalLinks": internal,
        "broken": broken,
        "generatedApiDuplicateIds": generated_duplicates,
        "external": sorted(external),
    }
    Path(args.report).write_text(json.dumps(report, indent=2), encoding="utf-8")
    if not pages:
        print("FAIL no HTML pages under " + str(root))
        return 1
    if broken:
        for item in broken[:50]:
            print(f"BROKEN {item['reason']}: {item['page']} -> {item['href']}")
        return 1
    print(f"PASS links pages={len(pages)} internal={internal} broken=0 external={len(external)} prefix={prefix}")
    return 0


REGION = re.compile(r"^\s*#region\s+(\S+)\s*$")
ENDREGION = re.compile(r"^\s*#endregion\b")
FENCE = re.compile(r"^(\s*)(`{3,}|~{3,})\s*([A-Za-z0-9#+-]*)")
SNIPPET = re.compile(r"^\s*<!--\s*snippet:\s*([A-Za-z0-9_.-]+)\s*-->\s*$")
ILLUSTRATIVE = re.compile(r"^\s*<!--\s*illustrative:\s*(.+?)\s*-->\s*$")
CSHARP = {"csharp", "cs", "c#"}


def _normalize(lines: list[str]) -> list[str]:
    lines = [l.rstrip() for l in lines]
    while lines and not lines[0]:
        lines.pop(0)
    while lines and not lines[-1]:
        lines.pop()
    indents = [len(l) - len(l.lstrip(" ")) for l in lines if l.strip()]
    cut = min(indents) if indents else 0
    return [l[cut:] for l in lines]


def _regions(source: Path) -> dict[str, list[str]]:
    regions, current, buffer = {}, None, []
    for line in source.read_text(encoding="utf-8").splitlines():
        start = REGION.match(line)
        if start:
            if current:
                raise SystemExit(f"Nested region {start.group(1)} inside {current} in {source}")
            current, buffer = start.group(1), []
            continue
        if ENDREGION.match(line) and current:
            if current in regions:
                raise SystemExit(f"Duplicate region {current} in {source}")
            regions[current] = _normalize(buffer)
            current = None
            continue
        if current:
            buffer.append(line)
    if current:
        raise SystemExit(f"Unclosed region {current} in {source}")
    return regions


def cmd_snippets(args) -> int:
    source = Path(args.source)
    regions = _regions(source)
    failures, compiled, illustrative, used = [], 0, 0, set()
    for doc in args.docs:
        doc = Path(doc)
        lines = doc.read_text(encoding="utf-8").splitlines()
        index = 0
        while index < len(lines):
            match = FENCE.match(lines[index])
            if not match:
                index += 1
                continue
            marker, lang = match.group(2), match.group(3).lower()
            end = index + 1
            while end < len(lines) and not lines[end].strip().startswith(marker[0] * len(marker)):
                end += 1
            body = lines[index + 1:end]
            previous = lines[index - 1] if index > 0 else ""
            where = f"{doc}:{index + 1}"
            if lang in CSHARP:
                snippet, note = SNIPPET.match(previous), ILLUSTRATIVE.match(previous)
                if snippet:
                    name = snippet.group(1)
                    used.add(name)
                    if name not in regions:
                        failures.append({"location": where, "region": name, "reason": "missing-region"})
                    elif _normalize(body) != regions[name]:
                        failures.append({"location": where, "region": name, "reason": "differs-from-region",
                                         "expected": regions[name], "actual": _normalize(body)})
                    else:
                        compiled += 1
                elif note:
                    text = note.group(1)
                    fixture = re.search(r"fixture:\s*(\S+)", text)
                    reason = text.split(";")[0].strip()
                    if not reason or reason.startswith("fixture:"):
                        failures.append({"location": where, "reason": "missing-illustrative-reason"})
                    elif fixture and not _fixture_exists(fixture.group(1), doc, source):
                        failures.append({"location": where, "fixture": fixture.group(1), "reason": "missing-fixture"})
                    else:
                        illustrative += 1
                else:
                    failures.append({"location": where, "reason": "unclassified-fence"})
            index = end + 1
    report = {
        "source": str(source),
        "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "documents": [str(d) for d in args.docs],
        "regions": sorted(regions),
        "unusedRegions": sorted(set(regions) - used),
        "compiled": compiled,
        "illustrative": illustrative,
        "failures": failures,
    }
    Path(args.report).write_text(json.dumps(report, indent=2), encoding="utf-8")
    for failure in failures:
        print(f"FAIL {failure['reason']}: {failure['location']} {failure.get('region', '')}")
    if failures:
        return 1
    print(f"PASS snippets compiled={compiled} illustrative={illustrative} documents={len(args.docs)}")
    return 0


def _fixture_exists(fixture: str, doc: Path, source: Path) -> bool:
    for base in (Path.cwd(), doc.parent, source.parent):
        if (base / fixture).exists():
            return True
    return False


WARNING = re.compile(
    r"^(?P<file>.+?)\((?P<line>\d+),(?P<col>\d+)\): warning (?P<code>[A-Z]+[0-9]+): (?P<message>.*?) "
    r"\[(?P<project>[^\]]+?)(?:::TargetFramework=(?P<tfm>[^\]]+))?\]\s*$"
)


def _relative(path: str, repo: str) -> str:
    repo = repo.rstrip("/") + "/"
    return path[len(repo):] if path.startswith(repo) else path


def _parse_warnings(logs: list[str], repo: str):
    unique = {}
    for log in logs:
        for raw in Path(log).read_text(encoding="utf-8", errors="replace").splitlines():
            match = WARNING.match(raw.strip())
            if not match:
                continue
            item = {
                "project": _relative(match["project"], repo),
                "file": _relative(match["file"], repo),
                "code": match["code"],
                "message": re.sub(r"\s*\(https?://[^)]*\)\s*$", "", match["message"]).strip(),
            }
            key = (item["project"], item["file"], item["code"], item["message"])
            entry = unique.setdefault(key, dict(item, tfms=set(), lines=set()))
            entry["tfms"].add(match["tfm"] or "none")
            entry["lines"].add(int(match["line"]))
    return unique


def cmd_warnings(args) -> int:
    unique = _parse_warnings(args.logs, args.repo)
    items = [dict(v, tfms=sorted(v["tfms"]), lines=sorted(v["lines"])) for _, v in sorted(unique.items())]
    keyed = {(i["project"], i["file"], i["code"], i["message"]): i for i in items}
    if args.write_baseline:
        baseline = {
            "schemaVersion": 1,
            "scope": args.scope,
            "key": "project + file + diagnostic code + message (line numbers and TFM repetitions ignored)",
            "warnings": [{k: i[k] for k in ("project", "file", "code", "message")} for i in items],
            "explained": [],
        }
        Path(args.write_baseline).write_text(json.dumps(baseline, indent=2) + "\n", encoding="utf-8")
        print(f"WROTE baseline unique={len(items)}")
        return 0
    baseline = json.loads(Path(args.baseline).read_text(encoding="utf-8"))
    known = {(w["project"], w["file"], w["code"], w["message"]) for w in baseline["warnings"]}
    explained = baseline.get("explained", [])
    new, explained_hits = [], []
    for key, item in keyed.items():
        if key in known:
            continue
        rule = next((e for e in explained if e["code"] == item["code"] and e["file"] == item["file"]), None)
        (explained_hits if rule else new).append(dict(item, reason=rule["reason"]) if rule else item)
    resolved = [dict(zip(("project", "file", "code", "message"), k)) for k in sorted(known - set(keyed))]
    by_code: dict[str, int] = {}
    for item in items:
        by_code[item["code"]] = by_code.get(item["code"], 0) + 1
    report = {
        "logs": [str(l) for l in args.logs],
        "baseline": str(args.baseline),
        "unique": len(items),
        "byCode": dict(sorted(by_code.items())),
        "new": new,
        "explained": explained_hits,
        "resolved": resolved,
        "warnings": items,
    }
    if args.report:
        Path(args.report).write_text(json.dumps(report, indent=2), encoding="utf-8")
    for item in new:
        print(f"NEW {item['code']} {item['file']}: {item['message']}")
    if new:
        return 1
    print(f"PASS warnings unique={len(items)} new=0 explained={len(explained_hits)} resolved={len(resolved)}")
    return 0


def _split_metadata(text: str) -> tuple[str, str]:
    """Split a DocFX ManagedReference YAML file into its declared items and its cross-reference table."""
    match = re.search(r"^references:\s*$", text, flags=re.M)
    return (text, "") if not match else (text[:match.start()], text[match.start():])


def cmd_surface(args) -> int:
    dirs = [Path(d) for d in args.dirs]
    digests = []
    for directory in dirs:
        files = {}
        for path in sorted(directory.rglob("*.yml")):
            text = path.read_text(encoding="utf-8")
            for other in dirs:
                text = text.replace(str(other.resolve()), "<input>").replace(str(other), "<input>")
            items, references = _split_metadata(text)
            files[path.relative_to(directory).as_posix()] = (hashlib.sha256(items.encode()).hexdigest(), hashlib.sha256(references.encode()).hexdigest())
        digests.append(files)
    differences, reference_tables = [], []
    reference = digests[0]
    for directory, files in zip(dirs[1:], digests[1:]):
        for name in sorted(set(reference) | set(files)):
            mine, theirs = reference.get(name), files.get(name)
            if mine is None or theirs is None or mine[0] != theirs[0]:
                differences.append({"file": name, "reference": str(dirs[0]), "other": str(directory),
                                    "kind": "missing" if theirs is None else "added" if mine is None else "changed"})
            elif mine[1] != theirs[1]:
                reference_tables.append({"file": name, "other": str(directory)})
    report = {
        "directories": [str(d) for d in dirs],
        "files": len(reference),
        "comparison": "declared items (everything before the top-level 'references:' table) must be identical",
        "differences": differences,
        "referenceTableOnlyDifferences": reference_tables,
    }
    Path(args.report).write_text(json.dumps(report, indent=2), encoding="utf-8")
    if not reference:
        print("FAIL empty API metadata")
        return 1
    if differences:
        for item in differences[:50]:
            print(f"DIFF {item['kind']} {item['file']} ({item['other']})")
        return 1
    print(f"PASS surface files={len(reference)} declared items identical across {len(dirs)} inputs; "
          f"reference-table-only differences={len(reference_tables)}")
    return 0

MD_LINK = re.compile(r"!?\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")


def cmd_mdlinks(args) -> int:
    broken, checked, external = [], 0, set()
    for doc in args.docs:
        doc = Path(doc)
        in_fence = False
        for number, line in enumerate(doc.read_text(encoding="utf-8").splitlines(), 1):
            if FENCE.match(line):
                in_fence = not in_fence
                continue
            if in_fence:
                continue
            for match in MD_LINK.finditer(line):
                target = match.group(1)
                lowered = target.lower()
                if lowered.startswith(("http:", "https:")):
                    external.add(target)
                    continue
                if lowered.startswith(EXTERNAL_SCHEMES) or lowered.startswith("xref:") or target.startswith("#"):
                    continue
                path_part = unquote(urlsplit(target).path)
                resolved = os.path.normpath(os.path.join(doc.parent.resolve(), path_part))
                checked += 1
                root = Path(resolved).anchor
                reason = _exact_case_exists(Path(root), os.path.relpath(resolved, root).replace(os.sep, "/"))
                if reason:
                    broken.append({"document": str(doc), "line": number, "href": target, "reason": reason})
    report = {"documents": [str(d) for d in args.docs], "checked": checked, "broken": broken, "external": sorted(external),
              "note": "Fragments of relative Markdown links are not checked here; the generated-site crawl checks rendered anchors."}
    Path(args.report).write_text(json.dumps(report, indent=2), encoding="utf-8")
    for item in broken:
        print(f"BROKEN {item['reason']}: {item['document']}:{item['line']} -> {item['href']}")
    if broken:
        return 1
    print(f"PASS markdown links checked={checked} broken=0 external={len(external)} documents={len(args.docs)}")
    return 0


def cmd_docfx_log(args) -> int:
    counts, entries = {}, []
    for raw in Path(args.log).read_text(encoding="utf-8").splitlines():
        if not raw.strip():
            continue
        entry = json.loads(raw)
        severity = entry.get("severity", "unknown")
        counts[severity] = counts.get(severity, 0) + 1
        if severity in ("warning", "error"):
            entries.append({k: entry.get(k) for k in ("severity", "code", "message", "file", "line")})
    report = {"log": str(args.log), "counts": counts, "warningsAndErrors": entries}
    Path(args.report).write_text(json.dumps(report, indent=2), encoding="utf-8")
    if entries:
        for entry in entries[:50]:
            print(f"DOCFX {entry['severity']} {entry.get('code')}: {entry.get('message')}")
        return 1
    print(f"PASS docfx log warnings=0 errors=0 ({args.log})")
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    links = sub.add_parser("links")
    links.add_argument("--root", required=True)
    links.add_argument("--report", required=True)
    links.add_argument("--prefix")
    links.add_argument("--generated-api-dir", help="directory name whose generated API pages may repeat DocFX overload-group ids")
    links.set_defaults(func=cmd_links)
    snippets = sub.add_parser("snippets")
    snippets.add_argument("--source", required=True)
    snippets.add_argument("--report", required=True)
    snippets.add_argument("docs", nargs="+")
    snippets.set_defaults(func=cmd_snippets)
    warnings = sub.add_parser("warnings")
    warnings.add_argument("--repo", required=True)
    warnings.add_argument("--baseline")
    warnings.add_argument("--write-baseline")
    warnings.add_argument("--scope", default="Release clean build of packable runtime projects, all target frameworks")
    warnings.add_argument("--report")
    warnings.add_argument("logs", nargs="+")
    warnings.set_defaults(func=cmd_warnings)
    surface = sub.add_parser("surface")
    surface.add_argument("--report", required=True)
    surface.add_argument("dirs", nargs="+")
    surface.set_defaults(func=cmd_surface)
    mdlinks = sub.add_parser("mdlinks")
    mdlinks.add_argument("--report", required=True)
    mdlinks.add_argument("docs", nargs="+")
    mdlinks.set_defaults(func=cmd_mdlinks)
    log = sub.add_parser("docfx-log")
    log.add_argument("--log", required=True)
    log.add_argument("--report", required=True)
    log.set_defaults(func=cmd_docfx_log)
    args = parser.parse_args(argv)
    if args.command == "warnings" and not (args.baseline or args.write_baseline):
        parser.error("warnings requires --baseline or --write-baseline")
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())

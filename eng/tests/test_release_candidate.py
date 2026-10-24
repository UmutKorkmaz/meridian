"""E5 release tools: exact candidate preparation, bundle verification and read-only promotion planning.

The tests build tiny real ZIP/nupkg archives in temporary directories. Promotion logic runs against an in-memory
transport, authorization verifier and signature verifier that exist ONLY in this file; the command line cannot select
them. A guard makes every real socket connection fail, and promotion runs with process creation disabled, so no
network request, build or pack can happen. The optional checks against the root's frozen probe feed and the downloaded
2.1.3 baseline archives only READ those files and skip when that local scratch evidence is absent.

What this proves: orchestration, integrity, archive, dependency, receipt and recovery logic. It is NOT a release,
publication, provider, CI, clean-copy, signature-trust or E4/E6/E7 acceptance proof.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import shutil
import socket
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
import urllib.error
import urllib.request
import warnings
import zipfile
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))
import release_candidate as rc  # noqa: E402

PWSH = os.environ.get("PWSH") or shutil.which("pwsh")
SCRATCH = ROOT / ".superpowers/sdd/2026-10-02-meridian-evolution/codex-claude-opus-20261023"
PROBE_FEED = SCRATCH / "e5-toolprobe-feed"
PROBE_VERSION = "3.0.0-toolprobe.20261023.1"
BASELINE_FEED = SCRATCH / "baseline-feed"
REPOSITORY = "UmutKorkmaz/meridian"
FIXTURE_COMMIT = hashlib.sha1(b"meridian-e5-fixture-source").hexdigest()
FIXTURE_TREE = hashlib.sha1(b"meridian-e5-fixture-tree").hexdigest()
OTHER_COMMIT = hashlib.sha1(b"meridian-e5-some-other-commit").hexdigest()
RELEASE_WORKFLOW = ROOT / ".github/workflows/release.yml"
RUNBOOK = ROOT / "docs/release-runbook.md"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_sha256(path: Path) -> str:
    return sha256(Path(path).read_bytes())


def zip_bytes(entries, prefix: bytes = b"") -> bytes:
    """entries: (name, bytes) or (ZipInfo, bytes). Duplicate names are written deliberately when a test asks for them."""
    buffer = io.BytesIO()
    buffer.write(prefix)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data in entries:
                archive.writestr(name, data)
    return buffer.getvalue()


def zip_entries(data: bytes):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        return [(info.filename, archive.read(info)) for info in archive.infolist()]


def repository_sign(data: bytes, name: str = ".signature.p7s") -> bytes:
    """Fixture stand-in for NuGet repository signing: copy every entry unchanged and append one stored signature entry."""
    source = zipfile.ZipFile(io.BytesIO(data))
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as target:
        for info in source.infolist():
            target.writestr(info, source.read(info))
        signature = zipfile.ZipInfo(name)
        signature.compress_type = zipfile.ZIP_STORED
        target.writestr(signature, b"fixture-repository-signature")
    return out.getvalue()


RELS = b'<?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships" />'
CONTENT_TYPES = (b'<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                 b'<Default Extension="nuspec" ContentType="application/octet" /></Types>')


def nuspec_xml(package_id, version, dependencies=(), symbols=False, commit=None, repository=REPOSITORY) -> bytes:
    deps = "".join('<dependency id="%s" version="%s" exclude="Build,Analyzers" />' % pair for pair in dependencies)
    groups = '<dependencies><group targetFramework="net8.0">%s</group></dependencies>' % deps
    package_types = '<packageTypes><packageType name="SymbolsPackage" /></packageTypes>' if symbols else ""
    source = ('<repository type="git" url="https://github.com/%s" commit="%s" />' % (repository, commit)) if commit else ""
    text = ('\ufeff<?xml version="1.0" encoding="utf-8"?>\n<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
            "<metadata><id>%s</id><version>%s</version>%s%s%s</metadata></package>" % (package_id, version, package_types, source, groups))
    return text.encode("utf-8")


def payload(label: str, size: int = 96) -> bytes:
    seed = hashlib.sha256(label.encode()).digest()
    return (seed * (size // len(seed) + 1))[:size]


# id, kind, internal dependencies (dependency-first inventory order), API baseline, external dependencies
PACKAGES = [
    ("Fixture.Core", "runtime", [], "2.1.3", [("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.9")]),
    ("Fixture.Caching", "runtime", ["Fixture.Core"], None, [("Microsoft.Extensions.Caching.Memory", "10.0.9")]),
    ("Fixture.Redis", "runtime", ["Fixture.Caching", "Fixture.Core"], None, [("StackExchange.Redis", "2.7.27")]),
    ("Fixture.Generators", "analyzer", [], None, []),
]


def package_entries(package_id, version, kind, dependencies, commit, symbols=False):
    psmdcp = "package/services/metadata/core-properties/%s.psmdcp" % hashlib.md5((package_id + str(symbols)).encode()).hexdigest()
    if symbols:
        return [("_rels/.rels", RELS), (package_id + ".nuspec", nuspec_xml(package_id, version, dependencies, True, commit)),
                ("lib/net8.0/%s.pdb" % package_id, payload(package_id + "pdb")), ("[Content_Types].xml", CONTENT_TYPES),
                (psmdcp, b"<core />")]
    entries = [("_rels/.rels", RELS), (package_id + ".nuspec", nuspec_xml(package_id, version, dependencies, False, commit)),
               ("LICENSE", b"MIT fixture license\n"), ("README.md", ("# %s\n" % package_id).encode())]
    if kind == "runtime":
        entries.append(("lib/net8.0/%s.dll" % package_id, payload(package_id + "dll")))
    else:
        entries.append(("analyzers/dotnet/cs/%s.dll" % package_id, payload(package_id + "analyzer")))
    entries += [("[Content_Types].xml", CONTENT_TYPES), (psmdcp, b"<core />")]
    return entries


class FixtureSource:
    """Fixture source identity (no git writes): a tar of the fixture repository presented as commit FIXTURE_COMMIT."""

    def __init__(self, repo: Path, commit=FIXTURE_COMMIT, tree=FIXTURE_TREE, dirty=False, head=None, later=None):
        self.repo, self.commit, self.tree_id, self.dirty, self.head = repo, commit, tree, dirty, head or commit
        self.later = later or {}
        self.calls = 0

    def state(self):
        self.calls += 1
        if self.calls > 1 and self.later:
            return {"head": self.later.get("head", self.head), "dirty": self.later.get("dirty", self.dirty), "status": ["fixture"]}
        return {"head": self.head, "dirty": self.dirty, "status": [" M fixture"] if self.dirty else []}

    def tree(self, commit):
        return self.tree_id

    def export(self, commit, destination: Path):
        records = []
        with tarfile.open(destination, "w", format=tarfile.PAX_FORMAT, pax_headers={"comment": commit}) as archive:
            for path in sorted(p for p in self.repo.rglob("*") if p.is_file()):
                relative = path.relative_to(self.repo).as_posix()
                data = path.read_bytes()
                info = tarfile.TarInfo(relative)
                info.size = len(data)
                info.mode = 0o644
                archive.addfile(info, io.BytesIO(data))
                blob = hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()
                records.append({"path": relative, "type": "file", "mode": "100644", "size": len(data), "sha256": sha256(data), "gitBlob": blob})
        records.sort(key=lambda record: record["path"])
        return records, []


def passing_gate(policy, feed, evidence, version, *, source_commit, repo, expected_manifest_sha256):
    manifest = Path(feed) / ("package-manifest.%s.json" % version)
    record = json.loads(manifest.read_text(encoding="utf-8-sig"))
    return {"schema": "meridian-candidate-summary/2", "overall": "PASS", "exitCode": 0, "wholeCandidate": True,
            "releaseEligible": True, "mode": "aggregate", "scope": "candidate",
            "candidate": {"version": version, "packageManifestSha256": file_sha256(manifest), "packedSourceCommit": record["sourceCommit"],
                          "packages": [{"id": p["id"], "sha256": p["sha256"], "symbolsSha256": p["symbolsSha256"]} for p in record["packages"]]},
            "lanes": [{"id": "fixture/lane", "state": "PASS", "reasons": []}], "failures": []}


def gate_returning(mutate):
    def evaluator(policy, feed, evidence, version, **kwargs):
        summary = passing_gate(policy, feed, evidence, version, **kwargs)
        mutate(summary)
        return summary
    return evaluator


def passing_deprecations(repo: Path, output: Path):
    ledger = repo / "docs/deprecations.md"
    candidate = next((output.parent.parent / "packages").glob("package-manifest.*.json"))
    validated = json.loads((output.parent / "e4-candidate-summary.json").read_text())
    results = rc.deprecation_consumer_results(output.parent / "e4", [row["id"] for row in validated["lanes"] if row["state"] == "PASS"])
    document = rc._load_candidate_gate().deprecation_report(repo, file_sha256(candidate), json.loads(results.read_text()),
                                                          file_sha256(results), json.loads(candidate.read_text())["candidateVersion"])
    output.write_text(json.dumps(document), encoding="utf-8")
    return rc.validate_deprecation_output(output, 0, ledger, candidate=candidate, consumer_results=results, repo=repo)


def write_json(path: Path, document) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return path


class World:
    """A fixture repository, a once-packed feed and the evidence inputs for one candidate version."""

    def __init__(self, base: Path, version="3.0.0", release_preparation=True, packages=PACKAGES):
        self.base, self.version, self.release_preparation, self.packages = base, version, release_preparation, packages
        self.repo, self.feed, self.inputs, self.out = base / "repo", base / "feed", base / "inputs", base / "out"
        for directory in (self.repo / "eng", self.repo / "docs", self.feed, self.inputs, self.out):
            directory.mkdir(parents=True)
        inventory = {"schemaVersion": 1, "packages": [
            {"id": pid, "kind": kind, "project": "src/%s/%s.csproj" % (pid, pid), "versionSource": "Version",
             "targetFrameworks": ["net8.0"] if kind == "runtime" else ["netstandard2.0"], "baselineVersion": baseline,
             "projectDependencies": deps, "directPackageDependencies": [{"id": i, "version": v} for i, v in external]}
            for pid, kind, deps, baseline, external in packages], "consumers": []}
        from eng.tests.test_lifecycle_integration import install_retained_source, consumer_contract
        install_retained_source(self.repo)
        inventory["consumers"].append(consumer_contract([package[0] for package in packages]))
        write_json(self.repo / "eng/packages.json", inventory)
        write_json(self.repo / "eng/required-checks.json", {
            "schema": "meridian-required-checks/1", "policyVersion": 1, "packageInventory": "eng/packages.json",
            "toolchains": {"pinnedCompatibility": {"buildSdk": "10.0.300",
                "lanes": [{"framework": "net10.0", "sdk": "10.0.300", "runtime": "10.0.8"}]}},
            "countProfiles": {"fixture": {"net10.0": [{"suite": "Fixture.Tests", "minimum": 1}]}},
            "infrastructureChecks": [{"check": "candidate gate"}],
            "lanes": [{"id": "fixture/lane", "check": "fixture lane", "kind": "generic", "framework": "net10.0",
                       "countsProfile": "fixture", "ci": {"workflow": "ci.yml"}},
                      {"id": "fixture/local", "check": "local only lane", "kind": "generic", "framework": "net10.0",
                       "countsProfile": "fixture", "ci": None},
                      {"id": "fixture/consumers", "check": "fixture lifecycle consumers", "kind": "package-consumers",
                       "framework": "net10.0", "sdk": "10.0.300", "host": {"os": "linux", "arch": "x64"},
                       "consumerScope": "all", "ci": None}]})
        (self.repo / "CHANGELOG.md").write_text("# Changelog\n\n## %s\n" % version, encoding="utf-8")
        (self.repo / "LICENSE").write_text("MIT fixture\n", encoding="utf-8")
        self.source = FixtureSource(self.repo)
        self.build_feed()
        self.write_inputs()

    @property
    def manifest_path(self) -> Path:
        return self.feed / ("package-manifest.%s.json" % self.version)

    def build_feed(self, mutate_record=None, mutate_entries=None, record_version=None, nuspec_version=None):
        for path in self.feed.iterdir():
            path.unlink()
        records = []
        for pid, kind, deps, baseline, external in self.packages:
            version = nuspec_version or self.version
            dependencies = list(external) + [(d, version) for d in deps]
            entries = package_entries(pid, version, kind, dependencies, FIXTURE_COMMIT)
            if mutate_entries:
                entries = mutate_entries(pid, entries)
            nupkg = self.feed / ("%s.%s.nupkg" % (pid, self.version))
            nupkg.write_bytes(zip_bytes(entries))
            snupkg = None
            if kind == "runtime":
                snupkg = self.feed / ("%s.%s.snupkg" % (pid, self.version))
                snupkg.write_bytes(zip_bytes(package_entries(pid, version, kind, dependencies, FIXTURE_COMMIT, symbols=True)))
            records.append({"id": pid, "kind": kind, "version": record_version or self.version, "assemblyName": pid,
                            "package": nupkg.name, "sha256": file_sha256(nupkg),
                            "symbols": snupkg.name if snupkg else None, "symbolsSha256": file_sha256(snupkg) if snupkg else None,
                            "license": {"expression": "MIT", "file": "LICENSE"},
                            "dependencies": [{"id": i, "version": v} for i, v in external] + [{"id": d, "version": self.version} for d in deps],
                            "assets": [n for n, _ in entries], "binaries": [], "analyzers": [], "baselineVersion": baseline})
        record = {"schemaVersion": 1, "candidateVersion": self.version, "manifestSha256": file_sha256(self.repo / "eng/packages.json"),
                  "sourceCommit": FIXTURE_COMMIT, "sourceDirty": False, "packages": records, "buildTooling": []}
        if mutate_record:
            mutate_record(record)
        self.manifest_path.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")

    def write_inputs(self):
        manifest_sha = file_sha256(self.manifest_path)
        # Real collector and evaluator over synthetic raw data. No process or
        # remote backend is needed, and no recorded PASS substitutes for parsing.
        import lane_collect
        policy = json.loads((self.repo / "eng/required-checks.json").read_text())
        raw = self.inputs / "raw"
        raw.mkdir(exist_ok=True)
        codebase = "/fixture/tests/Fixture.Tests/bin/Release/net10.0/Fixture.Tests.dll"
        (raw / "Fixture.Tests.trx").write_text(
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
            '<UnitTestResult testId="00000000-0000-0000-0000-000000000001" executionId="10000000-0000-0000-0000-000000000001" '
            'testName="Fixture.Tests.Cases.CaseOne" outcome="Passed"/></Results><TestDefinitions>'
            '<UnitTest id="00000000-0000-0000-0000-000000000001" storage="' + codebase + '">'
            '<Execution id="10000000-0000-0000-0000-000000000001"/><TestMethod codeBase="' + codebase + '" '
            'className="Fixture.Tests.Cases" name="CaseOne"/></UnitTest></TestDefinitions><TestEntries>'
            '<TestEntry testId="00000000-0000-0000-0000-000000000001" executionId="10000000-0000-0000-0000-000000000001"/>'
            '</TestEntries><ResultSummary outcome="Completed">'
            '<Counters total="1" executed="1" passed="1" failed="0" notExecuted="0"/></ResultSummary></TestRun>')
        write_json(raw / "lane-toolchain.json", {
            "dotnetPath": "/fixture/dotnet", "dotnetRoot": None, "sdkVersion": "10.0.300",
            "sdks": ["10.0.300 [/fixture/sdk]"],
            "runtimes": ["Microsoft.NETCore.App 10.0.8 [/fixture/shared/Microsoft.NETCore.App]"]})
        for lane in policy["lanes"]:
            lane_raw = raw
            if lane["kind"] == "package-consumers":
                from eng.tests.test_lifecycle_integration import write_consumer_raw
                lane_raw = self.inputs / "raw-consumers"
                write_consumer_raw(lane_raw, self.repo, self.feed, self.version, [package[0] for package in self.packages])
            result = lane_collect.build_lane_result(policy, lane, [lane_raw], self.feed, self.version, FIXTURE_COMMIT,
                "local", None, {"os": "linux", "arch": "x64"}, 0, {}, {}, toolchain={}, repo=self.repo)
            lane_collect.write_lane(self.inputs / "e4", lane, result, [lane_raw])
        write_json(self.inputs / "e6.json", {"schema": "meridian-release-receipt/1", "kind": "e6-maintenance", "verdict": "PASS",
                                             "sourceCommit": FIXTURE_COMMIT, "candidateVersion": self.version, "reviewedAtUtc": "2026-10-23T00:00:00Z"})
        write_json(self.inputs / "final-review.json", {"schema": "meridian-release-receipt/1", "kind": "final-review", "verdict": "PASS",
                                                       "sourceCommit": FIXTURE_COMMIT, "candidateVersion": self.version,
                                                       "packageManifestSha256": manifest_sha, "reviewedAtUtc": "2026-10-23T00:00:00Z",
                                                       "wholeCandidateReleaseEligible": True, "reviewedE4": {
                                                           "policySha256": file_sha256(self.repo / "eng/required-checks.json"),
                                                           "laneReceiptSha256": {lane["id"]: file_sha256(self.inputs / "e4/lanes" /
                                                               lane["id"].replace("/", "__") / "lane-result.json") for lane in policy["lanes"]}}})
        write_json(self.inputs / "protection.json", {
            "atUtc": "2026-10-23T10:36:04Z", "command": ["gh", "api", "repos/UmutKorkmaz/Meridian/branches/main/protection"], "exit": 0,
            "data": {"required_status_checks": {"strict": True, "contexts": ["candidate gate", "fixture lane"]},
                     "required_pull_request_reviews": {"required_approving_review_count": 1}}, "error": ""})
        write_json(self.inputs / "rulesets.json", {"atUtc": "2026-10-23T10:36:04Z", "command": ["gh", "api", "repos/UmutKorkmaz/Meridian/rulesets"],
                                                   "exit": 0, "data": [{"id": 1, "name": "release tags", "target": "tag", "enforcement": "active"}], "error": ""})
        write_json(self.inputs / "availability.json", {"schema": "meridian-remote-availability/1", "retrievedAtUtc": "2026-10-23T10:36:06Z",
                                                       "packages": [{"id": "Fixture.Core", "status": "present", "versions": ["1.0.0", "2.1.3"]}] +
                                                       [{"id": p[0], "status": "absent"} for p in self.packages[1:]]})
        (self.inputs / "release-notes.md").write_text("# Meridian %s\n\nFixture release notes.\n" % self.version, encoding="utf-8")

    def prepare(self, name="candidate", **overrides):
        arguments = dict(version_text=self.version, candidate_dir=self.out / name, feed=self.feed, repo=self.repo,
                         gate_evidence=self.inputs / "e4", release_preparation=self.release_preparation,
                         availability=self.inputs / "availability.json", e6_receipt=self.inputs / "e6.json",
                         final_review_receipt=self.inputs / "final-review.json", governance_protection=self.inputs / "protection.json",
                         governance_rulesets=self.inputs / "rulesets.json", release_notes=self.inputs / "release-notes.md",
                         repository=REPOSITORY, source_provider=self.source, gate_evaluator=rc.default_gate_evaluator,
                         deprecation_runner=passing_deprecations)
        arguments.update(overrides)
        return rc.prepare_candidate(**arguments)


def row(report, row_id):
    matches = [r for r in report["rows"] if r["id"] == row_id]
    assert len(matches) == 1, (row_id, report["rows"])
    return matches[0]


class NetworkGuardMixin:
    def guard_network(self):
        def refuse(*args, **kwargs):
            raise AssertionError("a real network connection was attempted")
        for target in ("socket.socket.connect", "socket.create_connection", "urllib.request.urlopen"):
            patcher = mock.patch(target, side_effect=refuse)
            patcher.start()
            self.addCleanup(patcher.stop)


class TempMixin:
    def temp(self) -> Path:
        directory = tempfile.mkdtemp(prefix="meridian-e5-")
        self.addCleanup(shutil.rmtree, directory, ignore_errors=True)
        return Path(directory)


# --------------------------------------------------------------------------------------------------------------------
# Versions and release intent
# --------------------------------------------------------------------------------------------------------------------

class VersionTests(unittest.TestCase):
    def test_canonical_nuget_semver_parse(self):
        accepted = ["3.0.0", "0.0.0", "3.0.0-probe.1", "3.0.0-rc.1-x", "2147483647.0.0", "3.0.0-0", "3.0.0-alpha.0.1"]
        rejected = ["03.0.0", "3.00.0", "3.0.00", "3.0", "3.0.0.0", "3.0.0+build", "v3.0.0", " 3.0.0", "3.0.0 ", "3.0.0\n",
                    "3.0.0-", "3.0.0-alpha..1", "3.0.0-alpha.01", "2147483648.0.0", "3.0.0-al_pha", "", None, "\u0663.0.0"]
        for text in accepted:
            with self.subTest(text=text):
                self.assertEqual(rc.parse_version(text).text, text)
        for text in rejected:
            with self.subTest(text=text):
                with self.assertRaises(rc.VersionError):
                    rc.parse_version(text)

    def test_nuget_precedence_and_case_insensitive_identity(self):
        v = rc.parse_version
        self.assertGreater(rc.compare_versions(v("3.0.0"), v("2.1.3")), 0)
        self.assertLess(rc.compare_versions(v("3.0.0-alpha"), v("3.0.0")), 0)
        self.assertLess(rc.compare_versions(v("3.0.0-alpha.2"), v("3.0.0-alpha.10")), 0)
        self.assertLess(rc.compare_versions(v("3.0.0-1"), v("3.0.0-alpha")), 0)
        self.assertLess(rc.compare_versions(v("3.0.0-alpha"), v("3.0.0-alpha.1")), 0)
        self.assertEqual(rc.compare_versions(v("3.0.0-RC.1"), v("3.0.0-rc.1")), 0)
        self.assertTrue(v("3.0.0").is_stable)
        self.assertFalse(v("3.0.0-rc.1").is_stable)

    def test_stable_identity_requires_explicit_release_preparation_intent(self):
        rc.check_release_intent(rc.parse_version("3.0.0"), True)
        rc.check_release_intent(rc.parse_version("3.0.0-probe.1"), False)
        with self.assertRaises(rc.UsageError):
            rc.check_release_intent(rc.parse_version("3.0.0"), False)
        with self.assertRaises(rc.UsageError):
            rc.check_release_intent(rc.parse_version("3.0.0-probe.1"), True)

    def test_release_class_follows_the_accepted_major_rationale(self):
        baseline = rc.parse_version("2.1.3")
        self.assertEqual(rc.classify_release(rc.parse_version("3.0.0"), baseline), "major")
        self.assertEqual(rc.classify_release(rc.parse_version("2.2.0"), baseline), "minor")
        self.assertEqual(rc.classify_release(rc.parse_version("2.1.4"), baseline), "patch")
        self.assertEqual(rc.classify_release(rc.parse_version("2.1.3"), baseline), "not-greater")
        self.assertEqual(rc.release_class_problems(rc.parse_version("3.0.0"), baseline), [])
        self.assertTrue(rc.release_class_problems(rc.parse_version("2.2.0"), baseline))
        self.assertTrue(rc.release_class_problems(rc.parse_version("4.0.0"), baseline), "skipping a major line must be rejected")
        self.assertTrue(rc.release_class_problems(rc.parse_version("2.1.3"), baseline))


# --------------------------------------------------------------------------------------------------------------------
# Archives, nuspec, signature boundary, dependency graph, bundle paths
# --------------------------------------------------------------------------------------------------------------------

class ArchiveTests(unittest.TestCase):
    def clean(self):
        return zip_bytes(package_entries("Fixture.Core", "3.0.0", "runtime", [], FIXTURE_COMMIT))

    def test_clean_archive_records_every_entry_path_length_and_hash(self):
        data = self.clean()
        result = rc.inspect_zip_bytes(data, "fixture")
        self.assertEqual(result["problems"], [])
        expected = [{"path": n, "length": len(b), "sha256": sha256(b)} for n, b in zip_entries(data)]
        self.assertEqual(sorted(result["entries"], key=lambda e: e["path"]), sorted(expected, key=lambda e: e["path"]))

    def test_ambiguous_or_unsafe_archives_are_rejected(self):
        base = package_entries("Fixture.Core", "3.0.0", "runtime", [], FIXTURE_COMMIT)
        link = zipfile.ZipInfo("lib/net8.0/link.dll")
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        cases = {
            "duplicate": base + [("README.md", b"second")],
            "case-duplicate": base + [("readme.md", b"other")],
            "traversal": base + [("../evil.dll", b"x")],
            "absolute": base + [("/abs.dll", b"x")],
            "backslash": base + [("lib\\net8.0\\x.dll", b"x")],
            "drive": base + [("C:/x.dll", b"x")],
            "directory-entry": base + [("lib/", b"")],
            "empty-segment": base + [("lib//x.dll", b"x")],
            "dot-segment": base + [("lib/./x.dll", b"x")],
            "symlink": base + [(link, b"/etc/passwd")],
        }
        for label, entries in cases.items():
            with self.subTest(case=label):
                self.assertTrue(rc.inspect_zip_bytes(zip_bytes(entries), label)["problems"], label)
        self.assertTrue(rc.inspect_zip_bytes(zip_bytes(base, prefix=b"MZ-prepended"), "prefix")["problems"])
        self.assertTrue(rc.inspect_zip_bytes(b"not a zip", "garbage")["problems"])

    def test_nuspec_identity_and_dependency_groups(self):
        parsed = rc.parse_nuspec(nuspec_xml("Fixture.Redis", "3.0.0", [("Fixture.Core", "3.0.0"), ("StackExchange.Redis", "2.7.27")], commit=FIXTURE_COMMIT))
        self.assertEqual((parsed["id"], parsed["version"]), ("Fixture.Redis", "3.0.0"))
        self.assertEqual(parsed["dependencies"], [{"targetFramework": "net8.0", "dependencies": [
            {"id": "Fixture.Core", "version": "3.0.0"}, {"id": "StackExchange.Redis", "version": "2.7.27"}]}])
        self.assertEqual(parsed["repository"]["commit"], FIXTURE_COMMIT)
        with self.assertRaises(rc.ArchiveError):
            rc.parse_nuspec(b'<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "b">]><package><metadata><id>&a;</id></metadata></package>')
        with self.assertRaises(rc.ArchiveError):
            rc.parse_nuspec(b"<package><metadata><version>1.0.0</version></metadata></package>")

    @unittest.skipUnless(PROBE_FEED.is_dir(), "root's frozen probe feed is local scratch evidence (read-only, optional)")
    def test_real_once_packed_probe_archives_inspect_cleanly_read_only(self):
        before = {p.name: file_sha256(p) for p in PROBE_FEED.iterdir()}
        nupkgs = sorted(PROBE_FEED.glob("*.nupkg"))
        self.assertEqual(len(nupkgs), 8)
        for path in nupkgs + sorted(PROBE_FEED.glob("*.snupkg")):
            with self.subTest(archive=path.name):
                result = rc.inspect_zip_bytes(path.read_bytes(), path.name)
                self.assertEqual(result["problems"], [])
                nuspecs = [e for e in result["entries"] if e["path"].endswith(".nuspec") and "/" not in e["path"]]
                self.assertEqual(len(nuspecs), 1)
        self.assertEqual(before, {p.name: file_sha256(p) for p in PROBE_FEED.iterdir()}, "probe feed must stay byte-identical")


class NuGetSignatureBoundaryTests(unittest.TestCase):
    def approved(self):
        return zip_bytes(package_entries("Fixture.Core", "3.0.0", "runtime", [], FIXTURE_COMMIT))

    def test_only_the_exact_root_signature_entry_may_be_added(self):
        approved = self.approved()
        downloaded = repository_sign(approved)
        result = rc.compare_nuget_archives(approved, downloaded)
        self.assertTrue(result["payloadEqual"], result)
        self.assertEqual(result["signatureEntry"], "present")
        self.assertEqual(result["uploadSha256"], sha256(approved))
        self.assertEqual(result["downloadSha256"], sha256(downloaded))
        self.assertNotEqual(result["uploadSha256"], result["downloadSha256"])

    def test_nested_or_differently_cased_signature_names_are_rejected(self):
        approved = self.approved()
        for name in ("lib/.signature.p7s", ".Signature.p7s", ".SIGNATURE.P7S", "package/.signature.p7s"):
            with self.subTest(name=name):
                result = rc.compare_nuget_archives(approved, repository_sign(approved, name))
                self.assertFalse(result["payloadEqual"], result)

    def test_changed_nuspec_or_payload_is_rejected_even_when_signed(self):
        approved = self.approved()
        changed_nuspec = [(n, b.replace(b"<version>3.0.0</version>", b"<version>3.0.1</version>") if n.endswith(".nuspec") else b)
                          for n, b in zip_entries(approved)]
        same_length_dll = [(n, b[:-1] + bytes([b[-1] ^ 1]) if n.endswith(".dll") else b) for n, b in zip_entries(approved)]
        removed = [(n, b) for n, b in zip_entries(approved) if n != "README.md"]
        added = zip_entries(approved) + [("lib/net8.0/extra.dll", b"x")]
        for label, entries in (("nuspec", changed_nuspec), ("dll", same_length_dll), ("removed", removed), ("added", added)):
            with self.subTest(case=label):
                self.assertFalse(rc.compare_nuget_archives(approved, repository_sign(zip_bytes(entries)))["payloadEqual"])

    def test_approved_archive_must_not_itself_carry_a_signature(self):
        signed = repository_sign(self.approved())
        self.assertFalse(rc.compare_nuget_archives(signed, signed)["payloadEqual"])

    def test_unsigned_download_is_payload_equal_but_reports_no_signature(self):
        approved = self.approved()
        result = rc.compare_nuget_archives(approved, approved)
        self.assertTrue(result["payloadEqual"])
        self.assertEqual(result["signatureEntry"], "absent")

    def test_github_assets_require_whole_file_equality(self):
        data = self.approved()
        self.assertTrue(rc.compare_release_asset(data, data)["equal"])
        self.assertFalse(rc.compare_release_asset(data, repository_sign(data))["equal"])

    @unittest.skipUnless((BASELINE_FEED / "meridian.mediator.2.1.3.nupkg").is_file(), "downloaded 2.1.3 baseline is local scratch evidence")
    def test_real_repository_signed_archive_differs_from_its_payload_only_by_the_root_signature(self):
        signed = (BASELINE_FEED / "meridian.mediator.2.1.3.nupkg").read_bytes()
        names = [n for n, _ in zip_entries(signed)]
        self.assertIn(".signature.p7s", names)
        # Fixture construction: the payload of the real signed archive without its signature entry (never a release artifact).
        unsigned = zip_bytes([(n, b) for n, b in zip_entries(signed) if n != ".signature.p7s"])
        result = rc.compare_nuget_archives(unsigned, signed)
        self.assertTrue(result["payloadEqual"], result)
        self.assertEqual(result["signatureEntry"], "present")


class DependencyAndPathTests(unittest.TestCase):
    def test_dependency_first_order_and_cycles(self):
        order, problems = rc.dependency_order(["A", "B", "C"], {"A": set(), "B": {"A"}, "C": {"A", "B"}})
        self.assertEqual((order, problems), (["A", "B", "C"], []))
        _, problems = rc.dependency_order(["A", "B"], {"A": {"B"}, "B": {"A"}})
        self.assertTrue(any("cycle" in p for p in problems), problems)
        _, problems = rc.dependency_order(["B", "A"], {"A": set(), "B": {"A"}})
        self.assertTrue(any("dependency-first" in p for p in problems), problems)
        _, problems = rc.dependency_order(["A"], {"A": {"Missing"}})
        self.assertTrue(problems)

    def test_bundle_paths_must_be_relative_contained_and_portable(self):
        for good in ("candidate.json", "packages/Fixture.Core.3.0.0.nupkg", "evidence/e4/lanes/x__y/lane-result.json"):
            self.assertIsNone(rc.bundle_path_problem(good), good)
        for bad in ("", "/abs", "../x", "a/../b", "a//b", "a/./b", "a\\b", "C:/x", "a/", "con.txt", "a/NUL", "x\u0001",
                    "trailing.", "trailing ", "a:b", "a*b"):
            with self.subTest(path=bad):
                self.assertIsNotNone(rc.bundle_path_problem(bad))


# --------------------------------------------------------------------------------------------------------------------
# Evidence rows: availability, E7 validator, governance, receipts, E4 gate
# --------------------------------------------------------------------------------------------------------------------

class AvailabilityTests(TempMixin, unittest.TestCase):
    NUGET_STATUS_SHAPE = [
        {"id": "Fixture.Core", "http": "200", "stableVersions": ["1.0.0", "2.1.3"], "readAtUtc": "2026-10-23T10:36:06Z"},
        {"id": "Fixture.Caching", "http": "404", "readAtUtc": "2026-10-23T10:36:06Z"},
    ]

    def snapshot(self, document):
        return rc.load_availability(write_json(self.temp() / "availability.json", document))

    def test_existing_snapshot_shape_allows_the_proposed_major_and_is_marked_stale_able(self):
        result = rc.availability_row(self.snapshot(self.NUGET_STATUS_SHAPE), ["Fixture.Core", "Fixture.Caching"],
                                     rc.parse_version("3.0.0"), rc.parse_version("2.1.3"))
        self.assertEqual(result["state"], "pass", result)
        self.assertTrue(result["details"]["staleAble"])
        self.assertEqual(result["details"]["highestPublishedStable"], "2.1.3")

    def test_published_version_collision_fails(self):
        document = [dict(self.NUGET_STATUS_SHAPE[0], stableVersions=["2.1.3", "3.0.0"]), self.NUGET_STATUS_SHAPE[1]]
        result = rc.availability_row(self.snapshot(document), ["Fixture.Core", "Fixture.Caching"], rc.parse_version("3.0.0"), rc.parse_version("2.1.3"))
        self.assertEqual(result["state"], "fail")

    def test_missing_or_incomplete_snapshot_is_blocked(self):
        result = rc.availability_row(None, ["Fixture.Core"], rc.parse_version("3.0.0"), rc.parse_version("2.1.3"))
        self.assertEqual(result["state"], "blocked")
        partial = rc.availability_row(self.snapshot(self.NUGET_STATUS_SHAPE[:1]), ["Fixture.Core", "Fixture.Caching"],
                                      rc.parse_version("3.0.0"), rc.parse_version("2.1.3"))
        self.assertEqual(partial["state"], "blocked")
        prerelease = rc.availability_row(self.snapshot(self.NUGET_STATUS_SHAPE), ["Fixture.Core", "Fixture.Caching"],
                                         rc.parse_version("3.0.0-rc.1"), rc.parse_version("2.1.3"))
        self.assertEqual(prerelease["state"], "blocked", "stable-only listings cannot prove a prerelease identity is free")

    def test_candidate_must_exceed_highest_published_stable(self):
        document = [dict(self.NUGET_STATUS_SHAPE[0], stableVersions=["2.1.3", "3.1.0"]), self.NUGET_STATUS_SHAPE[1]]
        result = rc.availability_row(self.snapshot(document), ["Fixture.Core", "Fixture.Caching"], rc.parse_version("3.0.0"), rc.parse_version("2.1.3"))
        self.assertEqual(result["state"], "fail")

    @unittest.skipUnless((SCRATCH / "nuget-status/summary.json").is_file(), "root's read-only NuGet snapshot is local scratch evidence")
    def test_real_read_only_snapshot_leaves_proposed_3_0_0_unoccupied(self):
        inventory = json.loads((ROOT / "eng/packages.json").read_text(encoding="utf-8"))
        ids = [p["id"] for p in inventory["packages"]]
        result = rc.availability_row(rc.load_availability(SCRATCH / "nuget-status/summary.json"), ids, rc.parse_version("3.0.0"), rc.parse_version("2.1.3"))
        self.assertEqual(result["state"], "pass", result)
        self.assertTrue(result["details"]["staleAble"])


@unittest.skipUnless(PWSH, "PowerShell is required for the E7 validator contract")
class DeprecationValidatorContractTests(TempMixin, unittest.TestCase):
    PASSING = """param([string] $Ledger, [string] $Output, [string] $Candidate, [string] $Authorization)
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Ledger).Hash.ToLowerInvariant()
$doc = [ordered]@{ schema='SCHEMA'; checkedAtUtc=[DateTime]::UtcNow.ToString('o'); clock='system-utc'; ledgerSha256=$hash; policyVersion=1;
  overall='OVERALL'; entries=@([ordered]@{ id='fixture-alias'; proposedAction='retain'; state='STATE'; failedPrerequisites=@() }) }
$doc | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
exit EXIT
"""

    def repo(self, script=None, **values):
        repo = self.temp()
        (repo / "docs").mkdir()
        (repo / "docs/deprecations.md").write_text("# fixture ledger\n", encoding="utf-8")
        if script is not None:
            (repo / "eng").mkdir()
            text = script
            for key, value in dict(dict(SCHEMA="meridian-deprecation-validation/1", OVERALL="PASS", STATE="retain_pass", EXIT="0"), **values).items():
                text = text.replace(key, value)
            (repo / "eng/verify-deprecations.ps1").write_text(text, encoding="utf-8")
        return repo

    def run_validator(self, repo):
        output = self.temp() / "deprecation-validation.json"
        return rc.run_deprecation_validator(repo, output), output

    def test_missing_validator_blocks(self):
        result, output = self.run_validator(self.repo())
        self.assertEqual(result["state"], "blocked")
        self.assertFalse(output.exists())

    def test_missing_pwsh_blocks(self):
        repo = self.repo(self.PASSING)
        result = rc.run_deprecation_validator(repo, self.temp() / "out.json", pwsh="")
        self.assertEqual(result["state"], "blocked")

    def test_passing_validator_contract(self):
        result, output = self.run_validator(self.repo(self.PASSING))
        self.assertEqual(result["state"], "pass", result)
        self.assertEqual(result["details"]["ledgerSha256"], sha256(b"# fixture ledger\n"))
        self.assertTrue(output.is_file())

    def test_failing_or_inconsistent_validator_output_fails(self):
        variants = {"exit-1": dict(OVERALL="FAIL", STATE="removal_ineligible", EXIT="1"),
                    "exit-0-but-fail": dict(OVERALL="FAIL"),
                    "ineligible-state": dict(STATE="removal_ineligible"),
                    "wrong-schema": dict(SCHEMA="other/1"),
                    "invalid-input": dict(EXIT="2")}
        for label, values in variants.items():
            with self.subTest(case=label):
                result, _ = self.run_validator(self.repo(self.PASSING, **values))
                self.assertEqual(result["state"], "fail", result)

    def test_ledger_hash_mismatch_fails(self):
        script = self.PASSING.replace("$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Ledger).Hash.ToLowerInvariant()", "$hash = '%s'" % ("0" * 64))
        result, _ = self.run_validator(self.repo(script))
        self.assertEqual(result["state"], "fail")

    def test_validator_without_output_fails(self):
        result, _ = self.run_validator(self.repo("exit 0\n"))
        self.assertEqual(result["state"], "fail")


class GovernanceAndReceiptTests(TempMixin, unittest.TestCase):
    REQUIRED = ["candidate gate", "fixture lane"]

    def envelope(self, data, exit_code=0, command="repos/UmutKorkmaz/Meridian/branches/main/protection"):
        return write_json(self.temp() / "protection.json", {"atUtc": "2026-10-23T10:36:04Z", "command": ["gh", "api", command],
                                                            "exit": exit_code, "data": data, "error": ""})

    def test_actual_snapshot_shape_without_required_checks_or_reviews_is_blocked(self):
        data = {"required_signatures": {"enabled": False}, "enforce_admins": {"enabled": False}, "allow_force_pushes": {"enabled": False}}
        result = rc.governance_row(self.envelope(data), None, REPOSITORY, self.REQUIRED)
        self.assertEqual(result["state"], "blocked")
        text = " ".join(result["reasons"])
        self.assertIn("required status checks", text)
        self.assertIn("approving review", text)

    def test_effective_governance_passes_and_wrong_repository_fails(self):
        data = {"required_status_checks": {"strict": True, "contexts": ["candidate gate"], "checks": [{"context": "fixture lane"}]},
                "required_pull_request_reviews": {"required_approving_review_count": 1}}
        self.assertEqual(rc.governance_row(self.envelope(data), None, REPOSITORY, self.REQUIRED)["state"], "pass")
        missing_one = dict(data, required_status_checks={"contexts": ["candidate gate"]})
        self.assertEqual(rc.governance_row(self.envelope(missing_one), None, REPOSITORY, self.REQUIRED)["state"], "blocked")
        other = self.envelope(data, command="repos/someone/else/branches/main/protection")
        self.assertEqual(rc.governance_row(other, None, REPOSITORY, self.REQUIRED)["state"], "fail")
        self.assertEqual(rc.governance_row(self.envelope(data, exit_code=1), None, REPOSITORY, self.REQUIRED)["state"], "blocked")
        self.assertEqual(rc.governance_row(None, None, REPOSITORY, self.REQUIRED)["state"], "blocked")

    def test_release_receipts_bind_source_version_and_artifacts(self):
        evidence = self.temp()
        lane = write_json(evidence / "lanes/fixture__lane/lane-result.json", {"fixture": "reviewed bytes"})
        summary = {"schema": "meridian-candidate-summary/2", "overall": "PASS", "exitCode": 0, "wholeCandidate": True,
                   "releaseEligible": True, "mode": "aggregate", "scope": "candidate", "policy": {"sha256": "c" * 64, "laneCount": 1},
                   "lanes": [{"id": "fixture/lane", "state": "PASS"}]}
        base = {"schema": "meridian-release-receipt/1", "kind": "final-review", "verdict": "PASS", "sourceCommit": FIXTURE_COMMIT,
                "candidateVersion": "3.0.0", "packageManifestSha256": "a" * 64, "wholeCandidateReleaseEligible": True,
                "reviewedE4": {"policySha256": "c" * 64, "laneReceiptSha256": {"fixture/lane": file_sha256(lane)}}}

        def check(document, kind="final-review", require_manifest=True):
            path = write_json(self.temp() / "receipt.json", document) if document is not None else None
            return rc.release_receipt_row("final-review", path, kind, FIXTURE_COMMIT, "3.0.0", "a" * 64, require_manifest,
                e4_summary=summary, e4_evidence=evidence, e4_policy_sha256="c" * 64)["state"]

        self.assertEqual(check(base), "pass")
        self.assertEqual(check(None), "blocked")
        self.assertEqual(check(dict(base, sourceCommit=OTHER_COMMIT)), "fail")
        self.assertEqual(check(dict(base, verdict="APPROVE-WITH-CONCERNS")), "fail")
        self.assertEqual(check(dict(base, candidateVersion="3.0.1")), "fail")
        self.assertEqual(check(dict(base, packageManifestSha256="b" * 64)), "fail")
        self.assertEqual(check({k: v for k, v in base.items() if k != "packageManifestSha256"}), "fail")
        self.assertEqual(check(dict(base, kind="e6-maintenance")), "fail")
        for approval in (False, None, 1):
            self.assertEqual(check(dict(base, wholeCandidateReleaseEligible=approval)), "blocked")
        for scope in (None, {}, {"policySha256": "d" * 64},
                      {"policySha256": "c" * 64, "laneReceiptSha256": {"fixture/lane": "0" * 64}}):
            self.assertEqual(check(dict(base, reviewedE4=scope)), "fail")


class GateRowTests(TempMixin, unittest.TestCase):
    def summary(self, **changes):
        world_feed = self.temp()
        manifest = world_feed / "package-manifest.3.0.0.json"
        manifest.write_text(json.dumps({"sourceCommit": FIXTURE_COMMIT, "packages": [{"id": "A", "sha256": "1" * 64, "symbolsSha256": None}]}))
        summary = passing_gate({}, world_feed, None, "3.0.0", source_commit=FIXTURE_COMMIT, repo=None, expected_manifest_sha256=None)
        for key, value in changes.items():
            summary[key] = value
        return summary, file_sha256(manifest)

    def judge(self, summary, manifest_sha):
        return rc.judge_gate_summary(summary, "3.0.0", manifest_sha, FIXTURE_COMMIT, {"A": ("1" * 64, None)})

    def test_consistent_whole_candidate_pass(self):
        summary, digest = self.summary()
        self.assertEqual(self.judge(summary, digest)["state"], "pass")

    def test_scoped_ineligible_or_misbound_summaries_fail(self):
        for label, change in (("scoped", {"wholeCandidate": False, "overall": "SCOPED-PASS"}), ("ineligible", {"releaseEligible": False}),
                              ("schema", {"schema": "meridian-candidate-summary/1"})):
            with self.subTest(case=label):
                summary, digest = self.summary(**change)
                self.assertEqual(self.judge(summary, digest)["state"], "fail")
        summary, digest = self.summary()
        summary["candidate"]["packageManifestSha256"] = "f" * 64
        self.assertEqual(self.judge(summary, digest)["state"], "fail")
        summary, digest = self.summary()
        summary["candidate"]["packedSourceCommit"] = OTHER_COMMIT
        self.assertEqual(self.judge(summary, digest)["state"], "fail")
        summary, digest = self.summary()
        summary["candidate"]["packages"][0]["sha256"] = "2" * 64
        self.assertEqual(self.judge(summary, digest)["state"], "fail")

    def test_missing_lanes_only_is_blocked_but_failed_or_skipped_lanes_fail(self):
        summary, digest = self.summary(overall="FAIL", releaseEligible=False, exitCode=1,
                                       lanes=[{"id": "a", "state": "MISSING", "reasons": ["no lane evidence was supplied"]}],
                                       failures=["a [MISSING]: no lane evidence was supplied"])
        self.assertEqual(self.judge(summary, digest)["state"], "blocked")
        summary, digest = self.summary(overall="FAIL", releaseEligible=False, exitCode=1,
                                       lanes=[{"id": "a", "state": "FAIL", "reasons": ["mandatory case skipped"]}],
                                       failures=["a [FAIL]: mandatory case skipped"])
        self.assertEqual(self.judge(summary, digest)["state"], "fail")

    def copy_probe(self, clean_claims):
        feed = self.temp() / "feed"
        shutil.copytree(PROBE_FEED, feed)
        manifest = feed / ("package-manifest.%s.json" % PROBE_VERSION)
        if clean_claims:
            # Fixture-only claim edits in a TEMPORARY copy; the frozen probe feed itself is never modified.
            record = json.loads(manifest.read_text(encoding="utf-8-sig"))
            record["sourceDirty"] = False
            record["manifestSha256"] = file_sha256(ROOT / "eng/packages.json")
            manifest.write_text(json.dumps(record, indent=2), encoding="utf-8")
        return feed, manifest

    @unittest.skipUnless(PROBE_FEED.is_dir(), "root's frozen probe feed is local scratch evidence (read-only, optional)")
    def test_real_candidate_gate_rejects_the_dirty_probe(self):
        feed, manifest = self.copy_probe(clean_claims=False)
        record = json.loads(manifest.read_text(encoding="utf-8-sig"))
        result, _ = rc.evaluate_e4_gate(ROOT, ROOT / "eng/required-checks.json", feed, self.temp(), PROBE_VERSION,
                                        record["sourceCommit"], file_sha256(manifest),
                                        {p["id"]: (p["sha256"], p["symbolsSha256"]) for p in record["packages"]})
        self.assertEqual(result["state"], "fail", result)
        self.assertTrue(any("uncommitted" in r for r in result["reasons"]), result["reasons"])

    @unittest.skipUnless(PROBE_FEED.is_dir(), "root's frozen probe feed is local scratch evidence (read-only, optional)")
    def test_real_candidate_gate_without_lane_evidence_is_blocked(self):
        feed, manifest = self.copy_probe(clean_claims=True)
        record = json.loads(manifest.read_text(encoding="utf-8-sig"))
        result, summary = rc.evaluate_e4_gate(ROOT, ROOT / "eng/required-checks.json", feed, self.temp(), PROBE_VERSION,
                                              record["sourceCommit"], file_sha256(manifest),
                                              {p["id"]: (p["sha256"], p["symbolsSha256"]) for p in record["packages"]})
        self.assertEqual(summary["overall"], "FAIL")
        self.assertEqual(result["state"], "blocked", result)


class GitSourceTests(TempMixin, unittest.TestCase):
    def git(self, *args):
        return subprocess.run(["git", "-C", str(ROOT)] + list(args), capture_output=True, text=True, check=True,
                              env=dict(os.environ, GIT_OPTIONAL_LOCKS="0")).stdout

    def test_state_reports_head_and_dirtiness_like_git_status(self):
        state = rc.GitSourceProvider(ROOT).state()
        self.assertEqual(state["head"], self.git("rev-parse", "HEAD").strip())
        porcelain = self.git("status", "--porcelain=v1", "--untracked-files=all")
        self.assertEqual(state["dirty"], bool(porcelain.strip()))
        self.assertEqual(state["count"], len([line for line in porcelain.splitlines() if line]), "the full count, not a truncated listing")

    def test_source_archive_of_head_is_verified_against_every_git_blob(self):
        head = self.git("rev-parse", "HEAD").strip()
        destination = self.temp() / "source.tar"
        records, problems = rc.GitSourceProvider(ROOT).export(head, destination)
        self.assertEqual(problems, [])
        blobs = [line for line in self.git("ls-tree", "-r", "--full-tree", head).splitlines() if " blob " in line]
        self.assertEqual(len(records), len(blobs))
        self.assertTrue(all(len(r["sha256"]) == 64 and len(r["gitBlob"]) == 40 for r in records))
        with tarfile.open(destination) as archive:
            self.assertEqual(archive.pax_headers.get("comment"), head)


# --------------------------------------------------------------------------------------------------------------------
# Preparation
# --------------------------------------------------------------------------------------------------------------------

class PrepareTests(NetworkGuardMixin, TempMixin, unittest.TestCase):
    def setUp(self):
        self.guard_network()

    def world(self, **kwargs) -> World:
        return World(self.temp(), **kwargs)

    def test_prepares_the_once_packed_feed_without_any_process_or_rebuild(self):
        world = self.world()
        feed_before = {p.name: file_sha256(p) for p in world.feed.iterdir()}
        with mock.patch("subprocess.Popen", side_effect=AssertionError("no process may start with injected seams")):
            code, report = world.prepare()
        self.assertEqual(code, rc.EXIT_OK, json.dumps(report, indent=2))
        self.assertEqual(report["status"], "prepared")
        bundle = Path(report["candidateDirectory"])
        manifest = bundle / "candidate.json"
        raw = manifest.read_bytes()
        self.assertEqual(report["candidateJsonSha256"], sha256(raw))
        self.assertNotIn(report["candidateJsonSha256"].encode(), raw, "candidate.json must not contain its own digest")
        self.assertEqual(feed_before, {p.name: file_sha256(p) for p in world.feed.iterdir()}, "the feed must not change")
        for name, digest in feed_before.items():
            self.assertEqual(file_sha256(bundle / "packages" / name), digest, "adopted bytes must be identical")
        candidate = json.loads(raw)
        self.assertEqual(candidate["schema"], rc.CANDIDATE_SCHEMA)
        self.assertEqual(candidate["version"]["canonical"], "3.0.0")
        self.assertEqual(candidate["version"]["releaseClass"], "major")
        self.assertEqual(candidate["source"]["commit"], FIXTURE_COMMIT)
        self.assertEqual(candidate["source"]["tree"], FIXTURE_TREE)
        self.assertEqual(candidate["dependencyOrder"], ["Fixture.Core", "Fixture.Caching", "Fixture.Redis", "Fixture.Generators"])
        listed = {f["path"] for f in candidate["files"]}
        on_disk = {p.relative_to(bundle).as_posix() for p in bundle.rglob("*") if p.is_file()} - {"candidate.json"}
        self.assertEqual(listed, on_disk)
        for item in candidate["files"]:
            self.assertEqual(file_sha256(bundle / item["path"]), item["sha256"])
            self.assertEqual((bundle / item["path"]).stat().st_size, item["size"])
            self.assertIn(item["role"], rc.FILE_ROLES)
        core = candidate["packages"][0]
        self.assertTrue(all(set(e) == {"path", "length", "sha256"} for e in core["package"]["entries"]))
        self.assertEqual(core["package"]["nuspec"]["id"], "Fixture.Core")
        self.assertIsNone(candidate["packages"][3]["symbols"], "analyzer packages carry no symbols")
        self.assertTrue(all(r["state"] == "pass" for r in candidate["evidencePolicy"]["rows"]))
        keys = [t["key"] for t in candidate["targets"]]
        self.assertEqual(keys[:3], ["source:" + FIXTURE_COMMIT, "tag:v3.0.0", "release:v3.0.0"])
        nuget = [k for k in keys if k.startswith("nuget:")]
        self.assertEqual(nuget, ["nuget:Fixture.Core/3.0.0", "nuget:Fixture.Caching/3.0.0", "nuget:Fixture.Redis/3.0.0", "nuget:Fixture.Generators/3.0.0"])
        self.assertLess(max(keys.index(k) for k in nuget), min(keys.index(k) for k in keys if k.startswith("symbols:")))
        self.assertEqual(len([k for k in keys if k.startswith("symbols:")]), 3)
        self.assertEqual(keys[-2:], ["consumer-verification", "site"])
        anchor = json.loads(Path(report["approvalAnchor"]).read_text())
        self.assertEqual(anchor["candidateJsonSha256"], sha256(raw))
        self.assertFalse(Path(report["approvalAnchor"]).resolve().is_relative_to(bundle.resolve()) if hasattr(Path, "is_relative_to")
                         else str(Path(report["approvalAnchor"]).resolve()).startswith(str(bundle.resolve()) + os.sep))
        self.assertTrue(rc.verify_bundle(manifest, sha256(raw))["ok"])

    def test_stable_needs_intent_and_invalid_versions_are_usage_errors(self):
        world = self.world()
        for version, intent in (("3.0.0", False), ("3.0.0-rc.1", True), ("3.0", True), ("3.0.0+meta", True)):
            with self.subTest(version=version, intent=intent):
                code, report = world.prepare(name="c-" + version.replace("+", "_"), version_text=version, release_preparation=intent)
                self.assertEqual(code, rc.EXIT_USAGE, report)
                self.assertFalse((world.out / ("c-" + version.replace("+", "_"))).exists())
        self.assertEqual(sorted(p.name for p in world.out.iterdir()), [], "no staging, quarantine or lock may remain")

    def test_occupied_candidate_directory_is_refused_untouched(self):
        world = self.world()
        occupied = world.out / "candidate"
        occupied.mkdir()
        (occupied / "keep.txt").write_text("previous attempt")
        code, report = world.prepare()
        self.assertEqual(code, rc.EXIT_FAIL, report)
        self.assertEqual([p.name for p in occupied.iterdir()], ["keep.txt"])

    def test_missing_receipts_and_governance_block_and_quarantine(self):
        world = self.world()
        code, report = world.prepare(e6_receipt=None, final_review_receipt=None, governance_protection=None, release_notes=None)
        self.assertEqual(code, rc.EXIT_BLOCKED, report)
        self.assertEqual(report["status"], "blocked")
        for row_id in ("e6-maintenance", "final-review", "governance", "release-notes"):
            self.assertEqual(row(report, row_id)["state"], "blocked", row_id)
        self.assertEqual(row(report, "package-adoption")["state"], "pass")
        self.assertFalse((world.out / "candidate").exists(), "a blocked attempt never becomes the candidate directory")
        quarantine = Path(report["quarantineDirectory"])
        self.assertTrue((quarantine / "preparation-report.json").is_file())
        self.assertFalse((quarantine / "candidate.json").exists())
        self.assertNotIn("candidateJsonSha256", report)
        self.assertTrue(report["blockers"])

    def test_interrupted_attempt_is_quarantined_not_left_resumable(self):
        world = self.world()

        def broken(repo, output):
            raise RuntimeError("injected interruption")
        code, report = world.prepare(deprecation_runner=broken)
        self.assertEqual(code, rc.EXIT_FAIL, report)
        self.assertTrue(any("interrupted" in f for f in report["failures"]))
        self.assertFalse((world.out / "candidate").exists())
        self.assertEqual([p.name for p in world.out.iterdir() if ".staging-" in p.name or p.name.endswith(".lock")], [])
        self.assertTrue((Path(report["quarantineDirectory"]) / "preparation-report.json").is_file())

    def test_actual_governance_snapshot_shape_blocks(self):
        world = self.world()
        write_json(world.inputs / "protection.json", {"atUtc": "2026-10-23T10:36:04Z", "command": ["gh", "api", "repos/UmutKorkmaz/Meridian/branches/main/protection"],
                                                      "exit": 0, "data": {"enforce_admins": {"enabled": False}}, "error": ""})
        code, report = world.prepare()
        self.assertEqual(code, rc.EXIT_BLOCKED)
        self.assertEqual(row(report, "governance")["state"], "blocked")

    @unittest.skipUnless(PWSH, "PowerShell is required for the E7 validator call")
    def test_missing_e7_validator_blocks_preparation(self):
        world = self.world()
        arguments = dict(deprecation_runner=None)
        code, report = world.prepare(**arguments)
        self.assertEqual(code, rc.EXIT_BLOCKED, report)
        self.assertEqual(row(report, "e7-lifecycle")["state"], "blocked")
        self.assertFalse((world.out / "candidate").exists())

    def test_e4_rows(self):
        world = self.world()
        code, report = world.prepare(name="no-gate", gate_evidence=None)
        self.assertEqual((code, row(report, "e4-aggregate")["state"]), (rc.EXIT_BLOCKED, "blocked"))

        def missing_only(summary):
            summary.update(overall="FAIL", releaseEligible=False, exitCode=1, lanes=[{"id": "fixture/lane", "state": "MISSING", "reasons": ["no lane evidence was supplied"]}],
                           failures=["fixture/lane [MISSING]: no lane evidence was supplied"])
        code, report = world.prepare(name="missing", gate_evaluator=gate_returning(missing_only))
        self.assertEqual((code, row(report, "e4-aggregate")["state"]), (rc.EXIT_BLOCKED, "blocked"))

        def skipped(summary):
            summary.update(overall="FAIL", releaseEligible=False, exitCode=1, lanes=[{"id": "fixture/lane", "state": "FAIL", "reasons": ["mandatory case skipped"]}],
                           failures=["fixture/lane [FAIL]: mandatory case skipped"])
        code, report = world.prepare(name="skipped", gate_evaluator=gate_returning(skipped))
        self.assertEqual((code, row(report, "e4-aggregate")["state"]), (rc.EXIT_FAIL, "fail"))

        def altered(summary):
            summary["candidate"]["packageManifestSha256"] = "e" * 64
        code, report = world.prepare(name="altered", gate_evaluator=gate_returning(altered))
        self.assertEqual((code, row(report, "e4-aggregate")["state"]), (rc.EXIT_FAIL, "fail"))

    def test_source_identity_failures(self):
        world = self.world()
        cases = {"dirty": FixtureSource(world.repo, dirty=True), "head": FixtureSource(world.repo, head=OTHER_COMMIT),
                 "changed-during": FixtureSource(world.repo, later={"dirty": True})}
        for label, source in cases.items():
            with self.subTest(case=label):
                code, report = world.prepare(name=label, source_provider=source)
                self.assertEqual(code, rc.EXIT_FAIL, report)
                reasons = row(report, "source-identity")
                self.assertEqual(reasons["state"], "fail")
                changed = any("changed during preparation" in r for r in reasons["reasons"])
                self.assertEqual(changed, label == "changed-during", reasons["reasons"])
                self.assertFalse((world.out / label).exists())

    def feed_case(self, label, **build):
        world = self.world()
        world.build_feed(**build)
        world.write_inputs()
        code, report = world.prepare()
        self.assertEqual(code, rc.EXIT_FAIL, (label, report))
        self.assertEqual(row(report, "package-adoption")["state"], "fail", label)
        return world, report

    def test_feed_and_manifest_integrity_failures(self):
        self.feed_case("dirty-pack", mutate_record=lambda r: r.update(sourceDirty=True))
        self.feed_case("dirty-unknown", mutate_record=lambda r: r.update(sourceDirty=None))
        self.feed_case("inventory", mutate_record=lambda r: r.update(manifestSha256="c" * 64))
        self.feed_case("other-commit", mutate_record=lambda r: r.update(sourceCommit=OTHER_COMMIT))
        self.feed_case("missing-package", mutate_record=lambda r: r["packages"].pop())
        self.feed_case("duplicate-identity", mutate_record=lambda r: r["packages"].append(dict(r["packages"][0])))
        self.feed_case("record-version", record_version="3.0.1")
        self.feed_case("nuspec-identity", nuspec_version="3.0.1")

        def wrong_dependency(record):
            record["packages"][1]["dependencies"][-1]["version"] = "2.9.9"
        self.feed_case("record-dependencies", mutate_record=wrong_dependency)

        def signed(pid, entries):
            return entries + [(".signature.p7s", b"x")] if pid == "Fixture.Core" else entries
        self.feed_case("approved-signature", mutate_entries=signed)

        def internal_version(pid, entries):
            if pid != "Fixture.Caching":
                return entries
            return [(n, nuspec_xml(pid, "3.0.0", [("Fixture.Core", "2.9.9")], commit=FIXTURE_COMMIT)) if n.endswith(".nuspec") else (n, b) for n, b in entries]
        self.feed_case("internal-dependency-version", mutate_entries=internal_version)

    def test_tampered_and_unlisted_feed_files(self):
        world = self.world()
        archive = world.feed / "Fixture.Caching.3.0.0.nupkg"
        archive.write_bytes(archive.read_bytes() + b"tamper")
        code, report = world.prepare(name="tampered")
        self.assertEqual((code, row(report, "package-adoption")["state"]), (rc.EXIT_FAIL, "fail"))
        world = self.world()
        (world.feed / "Unlisted.Package.3.0.0.nupkg").write_bytes(zip_bytes([("x", b"y")]))
        code, report = world.prepare(name="unlisted")
        self.assertEqual((code, row(report, "package-adoption")["state"]), (rc.EXIT_FAIL, "fail"))
        world = self.world()
        os.symlink(world.feed / "Fixture.Core.3.0.0.nupkg", world.feed / "link.nupkg")
        code, report = world.prepare(name="symlink")
        self.assertEqual((code, row(report, "package-adoption")["state"]), (rc.EXIT_FAIL, "fail"))

    def test_availability_and_release_class(self):
        world = self.world()
        write_json(world.inputs / "availability.json", {"schema": "meridian-remote-availability/1", "retrievedAtUtc": "2026-10-23T00:00:00Z",
                                                        "packages": [{"id": "Fixture.Core", "status": "present", "versions": ["2.1.3", "3.0.0"]}] +
                                                        [{"id": p[0], "status": "absent"} for p in PACKAGES[1:]]})
        code, report = world.prepare(name="collision")
        self.assertEqual((code, row(report, "remote-availability")["state"]), (rc.EXIT_FAIL, "fail"))
        code, report = world.prepare(name="no-snapshot", availability=None)
        self.assertEqual((code, row(report, "remote-availability")["state"]), (rc.EXIT_BLOCKED, "blocked"))
        minor = self.world(version="2.2.0")
        code, report = minor.prepare()
        self.assertEqual((code, row(report, "release-identity")["state"]), (rc.EXIT_FAIL, "fail"))

    def test_receipt_bound_to_another_commit_fails(self):
        world = self.world()
        document = json.loads((world.inputs / "e6.json").read_text())
        document["sourceCommit"] = OTHER_COMMIT
        write_json(world.inputs / "e6.json", document)
        code, report = world.prepare()
        self.assertEqual((code, row(report, "e6-maintenance")["state"]), (rc.EXIT_FAIL, "fail"))

    def test_local_only_final_review_cannot_prepare_a_complete_candidate(self):
        world = self.world()
        document = json.loads((world.inputs / "final-review.json").read_text())
        document.update(wholeCandidateReleaseEligible=False, localPass=2, foreignMissing=1)
        write_json(world.inputs / "final-review.json", document)
        code, report = world.prepare()
        self.assertEqual(row(report, "e4-aggregate")["state"], "pass")
        self.assertEqual((code, row(report, "final-review")["state"]), (rc.EXIT_BLOCKED, "blocked"))
        self.assertFalse((world.out / "candidate/candidate.json").exists())

    def test_whole_candidate_review_must_bind_the_actual_policy_and_lane_receipts(self):
        world = self.world()
        document = json.loads((world.inputs / "final-review.json").read_text())
        document.update(wholeCandidateReleaseEligible=True, reviewedE4={
            "policySha256": "0" * 64, "laneReceiptSha256": {"fixture/lane": "0" * 64}})
        write_json(world.inputs / "final-review.json", document)
        code, report = world.prepare()
        self.assertEqual(row(report, "e4-aggregate")["state"], "pass")
        self.assertEqual((code, row(report, "final-review")["state"]), (rc.EXIT_FAIL, "fail"))

    def test_prerelease_probe_preparation_without_intent(self):
        world = self.world(version="3.0.0-probe.1", release_preparation=False)
        write_json(world.inputs / "availability.json", {"schema": "meridian-remote-availability/1", "retrievedAtUtc": "2026-10-23T00:00:00Z",
                                                        "packages": [{"id": "Fixture.Core", "status": "present", "versions": ["2.1.3"]}] +
                                                        [{"id": p[0], "status": "absent"} for p in PACKAGES[1:]]})
        code, report = world.prepare()
        self.assertEqual(code, rc.EXIT_OK, report)
        candidate = json.loads((Path(report["candidateDirectory"]) / "candidate.json").read_text())
        self.assertEqual(candidate["version"]["kind"], "prerelease")
        self.assertFalse(candidate["version"]["releasePreparationIntent"])

    @unittest.skipUnless(PROBE_FEED.is_dir(), "root's frozen probe feed is local scratch evidence (read-only, optional)")
    def test_real_probe_feed_and_dirty_worktree_never_prepare(self):
        before = {p.name: file_sha256(p) for p in PROBE_FEED.iterdir()}
        out = self.temp()
        code, report = rc.prepare_candidate(version_text=PROBE_VERSION, candidate_dir=out / "probe", feed=PROBE_FEED, repo=ROOT,
                                            gate_evidence=self.temp(), release_preparation=False)
        self.assertEqual(code, rc.EXIT_FAIL, json.dumps(report, indent=2))
        self.assertEqual(row(report, "package-adoption")["state"], "fail", "sourceDirty true is never release evidence")
        self.assertNotEqual(row(report, "e4-aggregate")["state"], "pass")
        self.assertFalse((out / "probe").exists())
        self.assertEqual(before, {p.name: file_sha256(p) for p in PROBE_FEED.iterdir()}, "probe feed must stay byte-identical")


# --------------------------------------------------------------------------------------------------------------------
# Bundle verification
# --------------------------------------------------------------------------------------------------------------------

class PreparedBundleMixin(TempMixin):
    def prepared(self, **kwargs):
        world = World(self.temp(), **kwargs)
        code, report = world.prepare()
        self.assertEqual(code, rc.EXIT_OK, report)
        bundle = Path(report["candidateDirectory"])
        return world, bundle, bundle / "candidate.json", report["candidateJsonSha256"]

    @staticmethod
    def writable(path: Path):
        os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)

    def rewrite(self, manifest: Path, mutate) -> str:
        candidate = json.loads(manifest.read_text())
        mutate(candidate)
        self.writable(manifest)
        data = (json.dumps(candidate, indent=2, sort_keys=True) + "\n").encode()
        manifest.write_bytes(data)
        return sha256(data)


class VerifyTests(NetworkGuardMixin, PreparedBundleMixin, unittest.TestCase):
    def setUp(self):
        self.guard_network()

    def assert_rejected(self, manifest, digest, needle):
        result = rc.verify_bundle(manifest, digest)
        self.assertFalse(result["ok"], result)
        self.assertTrue(any(needle in p for p in result["problems"]), (needle, result["problems"]))

    def test_exact_digest_and_byte_changes(self):
        _, bundle, manifest, digest = self.prepared()
        self.assertTrue(rc.verify_bundle(manifest, digest)["ok"])
        self.assert_rejected(manifest, "0" * 64, "digest")
        self.writable(manifest)
        manifest.write_bytes(manifest.read_bytes() + b" ")
        self.assert_rejected(manifest, digest, "digest")

    def test_reapproved_bundle_cannot_substitute_a_local_only_final_review(self):
        _, bundle, manifest, _ = self.prepared()
        receipt = bundle / "evidence/receipts/final-review.json"
        document = json.loads(receipt.read_text())
        document["wholeCandidateReleaseEligible"] = False
        self.writable(receipt)
        receipt.write_text(json.dumps(document) + "\n")

        def update_integrity(candidate):
            for item in candidate["files"]:
                if item["path"] == "evidence/receipts/final-review.json":
                    item.update(sha256=file_sha256(receipt), size=receipt.stat().st_size)

        digest = self.rewrite(manifest, update_integrity)
        self.assert_rejected(manifest, digest, "final-review")

    def test_tampered_missing_unlisted_and_symlinked_files(self):
        _, bundle, manifest, digest = self.prepared()
        target = bundle / "packages/Fixture.Redis.3.0.0.nupkg"
        self.writable(target)
        original = target.read_bytes()
        target.write_bytes(original + b"x")
        self.assert_rejected(manifest, digest, "Fixture.Redis.3.0.0.nupkg")
        target.write_bytes(original)
        self.assertTrue(rc.verify_bundle(manifest, digest)["ok"])
        target.unlink()
        self.assert_rejected(manifest, digest, "missing")
        target.write_bytes(original)
        (bundle / "evidence/unlisted.txt").write_text("x")
        self.assert_rejected(manifest, digest, "unlisted")
        (bundle / "evidence/unlisted.txt").unlink()
        os.symlink(bundle / "metadata", bundle / "evidence/link")
        self.assert_rejected(manifest, digest, "symbolic link")

    def test_unsafe_duplicate_or_case_colliding_allowlist_paths(self):
        _, bundle, manifest, _ = self.prepared()
        original = manifest.read_bytes()

        def add(path):
            def mutate(candidate):
                first = dict(candidate["files"][0])
                first["path"] = path
                candidate["files"].append(first)
            return mutate
        for label, path in (("traversal", "../outside.json"), ("absolute", "/etc/passwd"), ("case", None), ("duplicate", None)):
            with self.subTest(case=label):
                self.writable(manifest)
                manifest.write_bytes(original)
                existing = json.loads(original)["files"][0]["path"]
                if label == "case":
                    path = existing.upper()
                elif label == "duplicate":
                    path = existing
                digest = self.rewrite(manifest, add(path))
                self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])

    def test_archive_entries_and_nuspec_identity_are_rechecked(self):
        _, bundle, manifest, _ = self.prepared()
        target = bundle / "packages/Fixture.Caching.3.0.0.nupkg"
        entries = [(n, nuspec_xml("Fixture.Caching", "3.0.1", commit=FIXTURE_COMMIT) if n.endswith(".nuspec") else b) for n, b in zip_entries(target.read_bytes())]
        changed = zip_bytes(entries)
        self.writable(target)
        target.write_bytes(changed)

        def mutate(candidate):
            for item in candidate["files"]:
                if item["path"] == "packages/Fixture.Caching.3.0.0.nupkg":
                    item["sha256"], item["size"] = sha256(changed), len(changed)
            for package in candidate["packages"]:
                if package["id"] == "Fixture.Caching":
                    package["package"]["sha256"], package["package"]["size"] = sha256(changed), len(changed)
        digest = self.rewrite(manifest, mutate)
        result = rc.verify_bundle(manifest, digest)
        self.assertFalse(result["ok"])
        self.assertTrue(any("entries" in p or "nuspec" in p for p in result["problems"]), result["problems"])

    def test_duplicate_identity_and_cycles(self):
        _, _, manifest, _ = self.prepared()
        original = manifest.read_bytes()
        digest = self.rewrite(manifest, lambda c: c["packages"].append(dict(c["packages"][0])))
        self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])
        self.writable(manifest)
        manifest.write_bytes(original)

        def cycle(candidate):
            candidate["packages"][0]["internalDependencies"] = ["Fixture.Redis"]
        digest = self.rewrite(manifest, cycle)
        result = rc.verify_bundle(manifest, digest)
        self.assertFalse(result["ok"])

    def test_malformed_but_digest_matching_manifests_fail_cleanly(self):
        _, bundle, manifest, _ = self.prepared()
        original = manifest.read_bytes()
        mutations = {
            "directory-path": lambda c: c["files"].append({"path": "packages", "sha256": "0" * 64, "size": 0, "role": "package"}),
            "package-not-object": lambda c: c["packages"][0].update(package="not-an-object"),
            "targets-not-list": lambda c: c.update(targets={"bad": True}),
            "notes-unlisted": lambda c: [t.update(notes="metadata/missing.md") for t in c["targets"] if t["kind"] == "github-release"],
            "rows-not-list": lambda c: c["evidencePolicy"].update(rows="pass"),
        }
        for label, mutate in mutations.items():
            with self.subTest(case=label):
                self.writable(manifest)
                manifest.write_bytes(original)
                digest = self.rewrite(manifest, mutate)
                result = rc.verify_bundle(manifest, digest)
                self.assertFalse(result["ok"], label)
                self.assertTrue(result["problems"], label)

    def test_non_passing_evidence_row_is_rejected(self):
        _, _, manifest, _ = self.prepared()

        def block(candidate):
            candidate["evidencePolicy"]["rows"][0]["state"] = "blocked"
        digest = self.rewrite(manifest, block)
        self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])


# --------------------------------------------------------------------------------------------------------------------
# Promotion: injected transport, authorization and signature verification (test API only)
# --------------------------------------------------------------------------------------------------------------------

def nuget_key(package_id, version):
    return "%s/%s" % (package_id.lower(), version.lower())


class FakeTransport(rc.WriteTransport):
    """In-memory GitHub + NuGet for orchestration tests. It never opens a connection; uploads are repository-signed."""

    name = "injected-test-fake"

    def __init__(self, commits=(FIXTURE_COMMIT,)):
        self.commits = set(commits)
        self.tags, self.releases, self.nuget, self.symbols = {}, {}, {}, {}
        self.calls, self.behaviors, self.hidden_reads = [], {}, {}
        self.allow_writes = True
        self.next_id = 100

    WRITES = {"create_tag", "create_release", "upload_release_asset", "push_nuget", "push_symbols"}

    def writes(self):
        return [c for c in self.calls if c[0] in self.WRITES]

    def _write(self, method, key, apply):
        if not self.allow_writes:
            raise AssertionError("write attempted during a read-only plan: %s %s" % (method, key))
        self.calls.append((method, key))
        queue = self.behaviors.get((method, key)) or []
        behavior = queue.pop(0) if queue else None
        if behavior == "timeout-before":
            raise rc.TransportTimeout("injected lost request")
        if behavior == "error":
            raise rc.TransportError("injected provider rejection")
        result = apply()
        if behavior == "timeout-after":
            raise rc.TransportTimeout("injected lost response")
        return result

    def _hidden(self, key):
        remaining = self.hidden_reads.get(key, 0)
        if remaining:
            self.hidden_reads[key] = remaining - 1
            return True
        return False

    def source_commit(self, repository, commit):
        self.calls.append(("source_commit", commit))
        return {"state": "present" if commit in self.commits else "absent"}

    def tag(self, repository, tag):
        self.calls.append(("tag", tag))
        return {"state": "present", "commit": self.tags[tag]} if tag in self.tags else {"state": "absent"}

    def release(self, repository, tag):
        self.calls.append(("release", tag))
        release = self.releases.get(tag)
        if release is None:
            return {"state": "absent"}
        return {"state": "present", "id": release["id"], "tag": tag,
                "body": release["body"], "name": release["name"], "prerelease": release["prerelease"],
                "draft": release.get("draft", False),
                "assets": [{"name": n, "size": len(b), "id": n, "release": tag} for n, b in sorted(release["assets"].items())]}

    def release_asset(self, repository, asset):
        self.calls.append(("release_asset", asset["name"]))
        return self.releases[asset["release"]]["assets"][asset["name"]]

    def nuget_versions(self, package_id):
        self.calls.append(("nuget_versions", package_id))
        versions = [k.split("/", 1)[1] for k in self.nuget if k.split("/", 1)[0] == package_id.lower() and not self._hidden(k)]
        return {"state": "present", "versions": versions} if versions else {"state": "absent"}

    def nuget_package(self, package_id, version):
        key = nuget_key(package_id, version)
        self.calls.append(("nuget_package", key))
        if key not in self.nuget or self._hidden(key):
            return None
        return self.nuget[key]

    def create_tag(self, repository, tag, commit):
        return self._write("create_tag", tag, lambda: self.tags.__setitem__(tag, commit) or {"ref": "refs/tags/" + tag})

    def create_release(self, repository, tag, name, body, prerelease):
        def apply():
            self.next_id += 1
            self.releases[tag] = {"id": self.next_id, "assets": {}, "body": body, "prerelease": prerelease, "name": name}
            return {"id": self.next_id}
        return self._write("create_release", tag, apply)

    def upload_release_asset(self, repository, release_id, name, data):
        def apply():
            release = [r for r in self.releases.values() if r["id"] == release_id][0]
            if name in release["assets"]:
                raise rc.TransportConflict("asset already exists")
            release["assets"][name] = bytes(data)
            return {"id": name}
        return self._write("upload_release_asset", name, apply)

    def push_nuget(self, package_id, version, data):
        key = nuget_key(package_id, version)

        def apply():
            if key in self.nuget:
                raise rc.TransportConflict("409 package version already exists")
            self.nuget[key] = repository_sign(bytes(data))
            return {"status": 201}
        return self._write("push_nuget", key, apply)

    def push_symbols(self, package_id, version, data):
        key = nuget_key(package_id, version)

        def apply():
            if key in self.symbols:
                raise rc.TransportConflict("409 symbols already exist")
            self.symbols[key] = bytes(data)
            return {"status": 201}
        return self._write("push_symbols", key, apply)


class FakeVerifier(rc.TrustedAuthorizationVerifier):
    name = "injected-test-verifier"

    def __init__(self, override=None):
        self.override = override or {}
        self.requests = []

    def verify(self, request):
        self.requests.append(dict(request))
        grant = dict(request)
        grant.update({"grantId": "test-grant-%d" % len(self.requests), "verifier": self.name})
        grant.update(self.override)
        return grant


def trusted_signature(data):
    return {"trusted": True, "tool": "injected-test-signature-verifier"}


def lifecycle_pass(snapshot_dir):
    return {"state": "pass", "reasons": [], "tool": "injected-test-lifecycle-revalidation"}


class PromotionMixin(NetworkGuardMixin, PreparedBundleMixin):
    def setUp(self):
        self.guard_network()
        self.world, self.bundle, self.manifest, self.digest = self.prepared()
        self.receipts = self.temp() / "promotion-receipts.jsonl"

    def plan(self, transport, **kwargs):
        arguments = dict(transport=transport, receipts_path=self.receipts if self.receipts.exists() else None,
                         release_preparation=True, signature_verifier=trusted_signature)
        arguments.update(kwargs)
        return rc.plan_promotion(self.manifest, self.digest, **arguments)

    def execute(self, transport, manifest=None, digest=None, **kwargs):
        arguments = dict(transport=transport, authorization_verifier=FakeVerifier(), receipts_path=self.receipts,
                         release_preparation=True, signature_verifier=trusted_signature, lifecycle_revalidator=lifecycle_pass,
                         sleep=lambda seconds: None, requery_attempts=3)
        arguments.update(kwargs)
        with mock.patch("subprocess.Popen", side_effect=AssertionError("promotion must never start a process (build/pack)")):
            return rc.execute_promotion(manifest or self.manifest, digest or self.digest, **arguments)

    def bundle_bytes(self, name):
        return (self.bundle / "packages" / name).read_bytes()


class PromotionPlanTests(PromotionMixin, unittest.TestCase):
    def test_fresh_remote_plan_is_read_only_and_dependency_ordered(self):
        transport = FakeTransport()
        transport.allow_writes = False
        code, plan = self.plan(transport)
        self.assertEqual(code, rc.EXIT_OK, json.dumps(plan, indent=2))
        self.assertEqual(plan["overall"], "plan-ready")
        self.assertEqual(plan["schema"], rc.PLAN_SCHEMA)
        self.assertFalse(plan["executionAvailable"])
        actions = [(s["key"], s["action"]) for s in plan["steps"]]
        self.assertEqual(actions[0], ("source:" + FIXTURE_COMMIT, "none"))
        self.assertEqual(actions[1], ("tag:v3.0.0", "create-tag"))
        self.assertEqual(actions[2], ("release:v3.0.0", "create-release"))
        pushes = [k for k, a in actions if a == "push-package"]
        self.assertEqual(pushes, ["nuget:Fixture.Core/3.0.0", "nuget:Fixture.Caching/3.0.0", "nuget:Fixture.Redis/3.0.0", "nuget:Fixture.Generators/3.0.0"])
        self.assertTrue(all(a == "upload-asset" for k, a in actions if k.startswith("asset:")))
        self.assertIn(("consumer-verification", "external"), actions)
        self.assertIn(("site", "not-authorized"), actions)
        self.assertEqual(transport.writes(), [])
        self.assertFalse(self.receipts.exists(), "a plan never writes receipts")

    def test_wrong_digest_or_lost_bundle_stops(self):
        transport = FakeTransport()
        code, plan = rc.plan_promotion(self.manifest, "1" * 64, transport=transport, release_preparation=True)
        self.assertEqual(code, rc.EXIT_FAIL)
        self.assertEqual(transport.calls, [])
        os.chmod(self.bundle / "packages/Fixture.Core.3.0.0.nupkg", stat.S_IRUSR | stat.S_IWUSR)
        (self.bundle / "packages/Fixture.Core.3.0.0.nupkg").unlink()
        code, plan = self.plan(transport)
        self.assertEqual(code, rc.EXIT_FAIL)
        self.assertEqual(transport.calls, [], "remote state is not read for a corrupted bundle")

    def test_stop_conditions(self):
        elsewhere = FakeTransport()
        elsewhere.tags["v3.0.0"] = OTHER_COMMIT
        code, plan = self.plan(elsewhere)
        self.assertEqual((code, plan["overall"]), (rc.EXIT_FAIL, "stopped"))
        self.assertTrue(any("elsewhere" in s or OTHER_COMMIT in s for s in plan["stops"]), plan["stops"])
        unrecorded = FakeTransport()
        unrecorded.tags["v3.0.0"] = FIXTURE_COMMIT
        self.assertEqual(self.plan(unrecorded)[0], rc.EXIT_FAIL, "a tag at S without a recorded promotion needs disposition")
        collision = FakeTransport()
        collision.nuget[nuget_key("Fixture.Caching", "3.0.0")] = repository_sign(self.bundle_bytes("Fixture.Caching.3.0.0.nupkg"))
        code, plan = self.plan(collision)
        self.assertEqual(code, rc.EXIT_FAIL, "an existing NuGet version on a new attempt is a reused identity, even with equal bytes")
        release = FakeTransport()
        release.releases["v3.0.0"] = {"id": 7, "assets": {}, "body": "", "prerelease": False, "name": "v3.0.0"}
        self.assertEqual(self.plan(release)[0], rc.EXIT_FAIL)

    def test_offline_or_unobservable_remote_is_blocked(self):
        code, plan = self.plan(None)
        self.assertEqual((code, plan["overall"]), (rc.EXIT_BLOCKED, "blocked"))

        class Timeouts(FakeTransport):
            def tag(self, repository, tag):
                raise rc.TransportTimeout("injected timeout")
        code, plan = self.plan(Timeouts())
        self.assertEqual((code, plan["overall"]), (rc.EXIT_BLOCKED, "blocked"))

    def test_stable_plan_requires_intent(self):
        code, _ = self.plan(FakeTransport(), release_preparation=False)
        self.assertEqual(code, rc.EXIT_USAGE)

    def test_plan_output_must_stay_outside_the_bundle(self):
        with self.assertRaises(rc.UsageError):
            rc.check_outside_bundle(self.bundle / "plan.json", self.bundle)


class PromotionExecutionTests(PromotionMixin, unittest.TestCase):
    def assert_no_writes(self, transport):
        self.assertEqual(transport.writes(), [])

    def test_cli_execution_fails_closed_without_a_trusted_verifier(self):
        self.assertEqual(rc.main(["execute", "--manifest", str(self.manifest), "--approved-sha256", self.digest, "--release-preparation",
                                  "--receipts", str(self.receipts)]), rc.EXIT_BLOCKED)
        approval = write_json(self.temp() / "approval.json", {"approved": True, "candidateSha256": self.digest, "repository": REPOSITORY})
        self.assertEqual(rc.main(["execute", "--manifest", str(self.manifest), "--approved-sha256", self.digest, "--release-preparation",
                                  "--receipts", str(self.receipts), "--authorization", str(approval)]), rc.EXIT_BLOCKED)
        self.assertEqual(rc.main(["execute", "--manifest", str(self.manifest), "--approved-sha256", "2" * 64, "--release-preparation"]), rc.EXIT_FAIL)
        self.assertFalse(self.receipts.exists())

    def test_self_authored_or_misbound_authorization_is_rejected_before_any_write(self):
        approval = {"approved": True, "candidateSha256": self.digest, "repository": REPOSITORY, "version": "3.0.0"}
        for label, verifier in (("json", approval), ("digest", FakeVerifier({"candidateSha256": "0" * 64})),
                                ("repository", FakeVerifier({"repository": "someone/else"})), ("targets", FakeVerifier({"targets": ["tag:v3.0.0"]})),
                                ("commit", FakeVerifier({"sourceCommit": OTHER_COMMIT})), ("version", FakeVerifier({"version": "3.0.1"}))):
            with self.subTest(case=label):
                transport = FakeTransport()
                code, result = self.execute(transport, authorization_verifier=verifier)
                self.assertEqual(code, rc.EXIT_BLOCKED, result)
                self.assert_no_writes(transport)

    def test_source_absent_from_the_remote_blocks_before_recording_an_authorized_promotion(self):
        transport = FakeTransport(commits=())
        code, plan = self.plan(transport)
        self.assertEqual(plan["steps"][0]["action"], "push-source")
        code, result = self.execute(transport)
        self.assertEqual((code, result["status"]), (rc.EXIT_BLOCKED, "blocked"))
        self.assert_no_writes(transport)
        events, _ = rc.ReceiptStream(self.receipts).read()
        self.assertFalse([e for e in events if e.get("event") == "authorized"], "no authorized promotion may be recorded")

    def test_missing_lifecycle_revalidation_blocks(self):
        transport = FakeTransport()
        code, _ = self.execute(transport, lifecycle_revalidator=None)
        self.assertEqual(code, rc.EXIT_BLOCKED)
        self.assert_no_writes(transport)

    def test_complete_promotion_uses_only_bundle_bytes_and_chains_receipts(self):
        transport = FakeTransport()
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_OK, json.dumps(result, indent=2))
        self.assertEqual(result["status"], "promoted-pending-consumer-verification")
        self.assertEqual(transport.tags["v3.0.0"], FIXTURE_COMMIT)
        assets = transport.releases["v3.0.0"]["assets"]
        for name in ("Fixture.Core.3.0.0.nupkg", "Fixture.Core.3.0.0.snupkg", "package-manifest.3.0.0.json"):
            self.assertEqual(assets[name], self.bundle_bytes(name), "GitHub assets must be the exact bundle bytes")
        self.assertEqual(assets["candidate.json"], self.manifest.read_bytes())
        for pid, *_ in PACKAGES:
            remote = transport.nuget[nuget_key(pid, "3.0.0")]
            self.assertTrue(rc.compare_nuget_archives(self.bundle_bytes("%s.3.0.0.nupkg" % pid), remote)["payloadEqual"])
        order = [c[1] for c in transport.calls if c[0] == "push_nuget"]
        self.assertEqual(order, [nuget_key(p[0], "3.0.0") for p in PACKAGES])
        first_symbols = min(i for i, c in enumerate(transport.calls) if c[0] == "push_symbols")
        last_package = max(i for i, c in enumerate(transport.calls) if c[0] == "push_nuget")
        self.assertGreater(first_symbols, last_package)
        events, problems = rc.ReceiptStream(self.receipts).read()
        self.assertEqual(problems, [])
        self.assertEqual(events[0]["event"], "authorized")
        self.assertTrue(all(e["candidateSha256"] == self.digest for e in events))
        self.assertTrue(all(e["sequence"] == i + 1 for i, e in enumerate(events)))
        final = rc.final_states(events)
        self.assertEqual(final["nuget:Fixture.Core/3.0.0"], "verified")
        self.assertEqual(final["symbols:Fixture.Core/3.0.0"], "present-unverified", "symbols have no retrievable remote archive proof")
        verified = [e for e in events if e.get("state") == "verified" and str(e.get("target", "")).startswith("nuget:")]
        self.assertTrue(all(e["uploadSha256"] != e["downloadSha256"] for e in verified), "upload and download hashes are kept separately")
        self.assertEqual(result["targets"]["consumer-verification"], "external")

    def test_existing_version_and_wrong_tag_stop_before_any_write(self):
        collision = FakeTransport()
        collision.nuget[nuget_key("Fixture.Redis", "3.0.0")] = repository_sign(self.bundle_bytes("Fixture.Redis.3.0.0.nupkg"))
        code, result = self.execute(collision)
        self.assertEqual((code, result["status"]), (rc.EXIT_FAIL, "stopped"))
        self.assert_no_writes(collision)
        elsewhere = FakeTransport()
        elsewhere.tags["v3.0.0"] = OTHER_COMMIT
        code, result = self.execute(elsewhere, receipts_path=self.temp() / "r.jsonl")
        self.assertEqual((code, result["status"]), (rc.EXIT_FAIL, "stopped"))
        self.assert_no_writes(elsewhere)
        self.assertEqual(elsewhere.tags["v3.0.0"], OTHER_COMMIT, "a tag elsewhere is never moved")

    def test_partial_upload_resumes_only_absent_identities_with_the_same_bytes(self):
        transport = FakeTransport()
        transport.behaviors[("push_nuget", nuget_key("Fixture.Caching", "3.0.0"))] = ["timeout-before"]
        code, result = self.execute(transport)
        self.assertEqual((code, result["status"]), (rc.EXIT_BLOCKED, "uncertain"), result)
        pushed = [c[1] for c in transport.calls if c[0] == "push_nuget"]
        self.assertEqual(pushed, [nuget_key("Fixture.Core", "3.0.0"), nuget_key("Fixture.Caching", "3.0.0")], "dependents wait")
        code, plan = self.plan(transport, receipts_path=self.receipts)
        caching = [s for s in plan["steps"] if s["key"] == "nuget:Fixture.Caching/3.0.0"][0]
        self.assertEqual(caching["action"], "requery-then-retry-same-bytes")
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_OK, json.dumps(result, indent=2))
        pushed = [c[1] for c in transport.calls if c[0] == "push_nuget"]
        self.assertEqual(pushed.count(nuget_key("Fixture.Core", "3.0.0")), 1, "verified identities are never pushed again")
        self.assertEqual(pushed.count(nuget_key("Fixture.Caching", "3.0.0")), 2)
        self.assertEqual(len([c for c in transport.calls if c[0] == "create_tag"]), 1)
        for pid, *_ in PACKAGES:
            self.assertTrue(rc.compare_nuget_archives(self.bundle_bytes("%s.3.0.0.nupkg" % pid), transport.nuget[nuget_key(pid, "3.0.0")])["payloadEqual"])
        events, problems = rc.ReceiptStream(self.receipts).read()
        self.assertEqual(problems, [])
        retry = [e for e in events if e.get("target") == "nuget:Fixture.Caching/3.0.0" and e.get("state") == "requested"]
        self.assertEqual(retry[1]["retryOfSequence"], retry[0]["sequence"], "the original intent is preserved")

    def test_lost_response_is_reconciled_from_remote_truth(self):
        transport = FakeTransport()
        transport.behaviors[("push_nuget", nuget_key("Fixture.Core", "3.0.0"))] = ["timeout-after"]
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_OK, json.dumps(result, indent=2))
        events, _ = rc.ReceiptStream(self.receipts).read()
        core = [e["state"] for e in events if e.get("target") == "nuget:Fixture.Core/3.0.0"]
        self.assertEqual(core[:2], ["requested", "uncertain"])
        self.assertEqual(core[-1], "verified")
        self.assertEqual(len([c for c in transport.calls if c == ("push_nuget", nuget_key("Fixture.Core", "3.0.0"))]), 1)

    def test_indexing_delay_is_uncertain_and_resume_verifies_without_republishing(self):
        transport = FakeTransport()
        key = nuget_key("Fixture.Core", "3.0.0")
        transport.hidden_reads[key] = 50
        code, result = self.execute(transport)
        self.assertEqual((code, result["status"]), (rc.EXIT_BLOCKED, "uncertain"))
        transport.hidden_reads[key] = 0
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_OK, json.dumps(result, indent=2))
        self.assertEqual(len([c for c in transport.calls if c == ("push_nuget", key)]), 1)

    def test_concurrent_promotion_lock_blocks_without_remote_access(self):
        transport = FakeTransport()
        lock = Path(str(self.receipts) + ".lock")
        lock.write_text('{"pid": 1}')
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_BLOCKED)
        self.assertEqual(transport.calls, [])
        self.assertTrue(lock.exists(), "another holder's lock is never removed")

    def test_resume_rejects_changed_remote_bytes_and_corrupted_or_mixed_receipts(self):
        transport = FakeTransport()
        self.assertEqual(self.execute(transport)[0], rc.EXIT_OK)
        good_remote = dict(transport.nuget)
        other = zip_bytes(package_entries("Fixture.Core", "3.0.0", "runtime", [], OTHER_COMMIT))
        transport.nuget[nuget_key("Fixture.Core", "3.0.0")] = repository_sign(other)
        writes = len(transport.writes())
        code, result = self.execute(transport)
        self.assertEqual((code, result["status"]), (rc.EXIT_FAIL, "stopped"))
        self.assertEqual(len(transport.writes()), writes)
        transport.nuget = good_remote
        lines = self.receipts.read_bytes().splitlines(keepends=True)
        tampered = lines[1].replace(b'"requested"', b'"verified"') if b'"requested"' in lines[1] else lines[1][:-2] + b' }\n'
        self.receipts.write_bytes(lines[0] + tampered + b"".join(lines[2:]))
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_FAIL, result)
        self.assertEqual(len(transport.writes()), writes)
        _, other_bundle, other_manifest, other_digest = self.prepared(version="3.0.0")
        self.receipts.write_bytes(b"".join(lines))
        code, result = self.execute(transport, manifest=other_manifest, digest=other_digest)
        self.assertEqual(code, rc.EXIT_FAIL, "a receipt stream of another candidate is never mixed")

    def test_lost_bundle_after_authorization_stops(self):
        transport = FakeTransport()
        target = self.bundle / "packages/Fixture.Redis.3.0.0.nupkg"
        os.chmod(target, stat.S_IRUSR | stat.S_IWUSR)
        target.write_bytes(b"corrupted")
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_FAIL)
        self.assertEqual(transport.calls, [])


class HttpReadTransportTests(NetworkGuardMixin, unittest.TestCase):
    class Response:
        def __init__(self, status, body):
            self.status, self._body = status, body

        def read(self):
            return self._body

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    class Opener:
        def __init__(self, routes):
            self.routes, self.requests = routes, []

        def open(self, request, timeout=None):
            self.requests.append(request)
            outcome = self.routes.get(request.full_url)
            if outcome is None or outcome == 404:
                raise urllib.error.HTTPError(request.full_url, 404, "Not Found", {}, None)
            if outcome == "timeout":
                raise socket.timeout("timed out")
            return HttpReadTransportTests.Response(200, json.dumps(outcome).encode() if not isinstance(outcome, bytes) else outcome)

    def setUp(self):
        self.guard_network()

    def transport(self, routes, token=None):
        opener = self.Opener(routes)
        return rc.HttpReadTransport(opener=opener, token=token), opener

    def test_reads_tag_release_and_nuget_state_with_get_only(self):
        api = "https://api.github.com/repos/UmutKorkmaz/meridian"
        routes = {api + "/git/ref/tags/v3.0.0": {"object": {"type": "tag", "sha": "t" * 40}},
                  api + "/git/tags/" + "t" * 40: {"object": {"type": "commit", "sha": FIXTURE_COMMIT}},
                  api + "/releases/tags/v3.0.0": {"id": 9, "assets": [{"id": 1, "name": "a.nupkg", "size": 3,
                                                                       "browser_download_url": "https://github.com/UmutKorkmaz/meridian/releases/download/v3.0.0/a.nupkg"}]},
                  "https://github.com/UmutKorkmaz/meridian/releases/download/v3.0.0/a.nupkg": b"abc",
                  "https://api.nuget.org/v3-flatcontainer/fixture.core/index.json": {"versions": ["2.1.3", "3.0.0"]},
                  "https://api.nuget.org/v3-flatcontainer/fixture.core/3.0.0/fixture.core.3.0.0.nupkg": b"pkg",
                  api + "/commits/" + FIXTURE_COMMIT: {"sha": FIXTURE_COMMIT}}
        transport, opener = self.transport(routes, token="secret-token")
        self.assertEqual(transport.tag(REPOSITORY, "v3.0.0"), {"state": "present", "commit": FIXTURE_COMMIT})
        release = transport.release(REPOSITORY, "v3.0.0")
        self.assertEqual((release["state"], release["assets"][0]["name"]), ("present", "a.nupkg"))
        self.assertEqual(transport.release_asset(REPOSITORY, release["assets"][0]), b"abc")
        self.assertEqual(transport.nuget_versions("Fixture.Core"), {"state": "present", "versions": ["2.1.3", "3.0.0"]})
        self.assertEqual(transport.nuget_package("Fixture.Core", "3.0.0"), b"pkg")
        self.assertEqual(transport.source_commit(REPOSITORY, FIXTURE_COMMIT), {"state": "present"})
        self.assertEqual(transport.tag(REPOSITORY, "v9.9.9"), {"state": "absent"})
        self.assertEqual(transport.nuget_versions("Absent.Package"), {"state": "absent"})
        self.assertTrue(all(r.get_method() == "GET" for r in opener.requests))
        for request in opener.requests:
            authorized = request.get_header("Authorization") is not None
            self.assertEqual(authorized, request.host == "api.github.com", request.full_url)

    def test_timeouts_and_errors_map_to_transport_states(self):
        transport, _ = self.transport({"https://api.github.com/repos/UmutKorkmaz/meridian/git/ref/tags/v3.0.0": "timeout"})
        with self.assertRaises(rc.TransportTimeout):
            transport.tag(REPOSITORY, "v3.0.0")

    def test_token_never_follows_a_cross_host_redirect(self):
        handler = rc._StripCrossHostAuthorization()
        original = urllib.request.Request("https://api.github.com/repos/UmutKorkmaz/meridian/releases/assets/1",
                                          headers={"Authorization": "Bearer secret-token", "Accept": "application/octet-stream"}, method="GET")
        moved = handler.redirect_request(original, None, 302, "Found", {}, "https://objects.githubusercontent.com/blob")
        self.assertIsNone(moved.get_header("Authorization"))
        same = handler.redirect_request(original, None, 301, "Moved", {}, "https://api.github.com/repositories/1/releases/assets/1")
        self.assertEqual(same.get_header("Authorization"), "Bearer secret-token")

    def test_the_command_line_transport_has_no_write_capability(self):
        for name in ("create_tag", "create_release", "upload_release_asset", "push_nuget", "push_symbols"):
            self.assertFalse(hasattr(rc.HttpReadTransport, name), name)
        self.assertFalse(issubclass(rc.HttpReadTransport, rc.WriteTransport))
        self.assertFalse([n for n in dir(rc) if "Fake" in n or "fake" in n.lower()], "no fake transport/verifier ships in the module")


class ReceiptStreamTests(TempMixin, unittest.TestCase):
    def test_append_only_hash_chain_detects_edits_truncation_and_partial_lines(self):
        path = self.temp() / "receipts.jsonl"
        stream = rc.ReceiptStream(path)
        for index in range(3):
            stream.append({"candidateSha256": "a" * 64, "event": "target", "target": "t", "state": "requested", "n": index})
        events, problems = stream.read()
        self.assertEqual((len(events), problems), (3, []))
        self.assertEqual(events[1]["previousSha256"], sha256(path.read_bytes().splitlines(keepends=True)[0]))
        original = path.read_bytes()
        lines = original.splitlines(keepends=True)
        path.write_bytes(lines[0] + lines[2])
        self.assertTrue(stream.read()[1], "a removed line breaks the chain")
        path.write_bytes(original + b'{"partial": ')
        self.assertTrue(stream.read()[1], "a partial trailing line is corruption, never silently truncated")
        path.write_bytes(original.replace(b'"n":1', b'"n":7'))
        self.assertTrue(stream.read()[1])


# --------------------------------------------------------------------------------------------------------------------
# Command line and PowerShell wrappers (exit-code propagation)
# --------------------------------------------------------------------------------------------------------------------

class CommandLineTests(NetworkGuardMixin, PreparedBundleMixin, unittest.TestCase):
    def setUp(self):
        self.guard_network()

    def test_verify_and_offline_plan_exit_codes(self):
        _, bundle, manifest, digest = self.prepared()
        self.assertEqual(rc.main(["verify", "--manifest", str(manifest), "--expected-sha256", digest]), rc.EXIT_OK)
        self.assertEqual(rc.main(["verify", "--manifest", str(manifest), "--expected-sha256", "9" * 64]), rc.EXIT_FAIL)
        self.assertEqual(rc.main(["verify", "--manifest", str(manifest), "--expected-sha256", "not-a-digest"]), rc.EXIT_USAGE)
        output = self.temp() / "plan.json"
        self.assertEqual(rc.main(["plan", "--manifest", str(manifest), "--approved-sha256", digest, "--offline", "--release-preparation",
                                  "--output", str(output)]), rc.EXIT_BLOCKED)
        plan = json.loads(output.read_text())
        self.assertEqual(plan["overall"], "blocked")
        self.assertEqual(plan["transport"], "offline")
        self.assertEqual(rc.main(["plan", "--manifest", str(manifest), "--approved-sha256", digest, "--offline"]), rc.EXIT_USAGE)
        self.assertEqual(rc.main(["plan", "--manifest", str(manifest), "--approved-sha256", digest, "--offline", "--release-preparation",
                                  "--output", str(bundle / "plan.json")]), rc.EXIT_USAGE)
        with self.assertRaises(SystemExit) as raised:
            rc.main(["prepare"])
        self.assertEqual(raised.exception.code, rc.EXIT_USAGE)

    def test_compare_nuget_command(self):
        directory = self.temp()
        approved = zip_bytes(package_entries("Fixture.Core", "3.0.0", "runtime", [], FIXTURE_COMMIT))
        (directory / "a.nupkg").write_bytes(approved)
        (directory / "b.nupkg").write_bytes(repository_sign(approved))
        (directory / "c.nupkg").write_bytes(repository_sign(approved, "lib/.signature.p7s"))
        self.assertEqual(rc.main(["compare-nuget", "--approved", str(directory / "a.nupkg"), "--downloaded", str(directory / "b.nupkg")]), rc.EXIT_OK)
        self.assertEqual(rc.main(["compare-nuget", "--approved", str(directory / "a.nupkg"), "--downloaded", str(directory / "c.nupkg")]), rc.EXIT_FAIL)


@unittest.skipUnless(PWSH, "PowerShell is required for the wrapper exit-code contract")
class WrapperTests(PreparedBundleMixin, unittest.TestCase):
    def pwsh(self, script, *args, timeout=300):
        return subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-File", str(ROOT / "eng" / script)] + [str(a) for a in args],
                              capture_output=True, text=True, timeout=timeout)

    def test_prepare_wrapper_argument_and_intent_exit_codes(self):
        out, feed = self.temp(), self.temp()
        cases = [(["-CandidateVersion", "3.0.0", "-EvidenceDirectory", out / "a", "-Feed", feed], rc.EXIT_USAGE),
                 (["-CandidateVersion", "3.0.0-rc.1", "-ReleasePreparation", "-EvidenceDirectory", out / "b", "-Feed", feed], rc.EXIT_USAGE),
                 (["-CandidateVersion", "3.0.0", "-ReleasePreparation", "-EvidenceDirectory", out / "c"], rc.EXIT_USAGE),
                 (["-CandidateVersion", "3.0", "-ReleasePreparation", "-EvidenceDirectory", out / "d", "-Feed", feed], rc.EXIT_USAGE)]
        for arguments, expected in cases:
            with self.subTest(arguments=arguments):
                result = self.pwsh("prepare-release.ps1", *arguments)
                self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        self.assertEqual(list(out.iterdir()), [])
        occupied = out / "occupied"
        occupied.mkdir()
        result = self.pwsh("prepare-release.ps1", "-CandidateVersion", "3.0.0-rc.1", "-EvidenceDirectory", occupied, "-Feed", feed)
        self.assertEqual(result.returncode, rc.EXIT_FAIL, result.stdout + result.stderr)

    def test_promote_wrapper_exit_codes(self):
        _, bundle, manifest, digest = self.prepared()
        output = self.temp() / "plan.json"
        cases = [(["-Manifest", manifest, "-ApprovedManifestSha256", "3" * 64, "-ReleasePreparation", "-Offline"], rc.EXIT_FAIL),
                 (["-Manifest", manifest, "-ApprovedManifestSha256", "short", "-ReleasePreparation", "-Offline"], rc.EXIT_USAGE),
                 (["-Manifest", manifest, "-ReleasePreparation", "-Offline"], rc.EXIT_USAGE),
                 (["-Manifest", manifest, "-ApprovedManifestSha256", digest, "-ReleasePreparation", "-Execute"], rc.EXIT_BLOCKED),
                 (["-Manifest", self.temp() / "lost.json", "-ApprovedManifestSha256", digest, "-ReleasePreparation", "-Offline"], rc.EXIT_FAIL),
                 (["-Manifest", manifest, "-ApprovedManifestSha256", digest, "-ReleasePreparation", "-Offline", "-Output", output], rc.EXIT_BLOCKED)]
        for arguments, expected in cases:
            with self.subTest(arguments=arguments):
                result = self.pwsh("promote-release.ps1", *arguments)
                self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        self.assertEqual(json.loads(output.read_text())["overall"], "blocked")

    @unittest.skipUnless(PROBE_FEED.is_dir(), "root's frozen probe feed is local scratch evidence (read-only, optional)")
    def test_prepare_wrapper_with_the_real_probe_feed_fails_and_quarantines(self):
        before = {p.name: file_sha256(p) for p in PROBE_FEED.iterdir()}
        out = self.temp()
        result = self.pwsh("prepare-release.ps1", "-CandidateVersion", PROBE_VERSION, "-EvidenceDirectory", out / "probe",
                           "-Feed", PROBE_FEED, "-GateEvidence", self.temp())
        self.assertEqual(result.returncode, rc.EXIT_FAIL, result.stdout + result.stderr)
        self.assertFalse((out / "probe").exists())
        self.assertEqual(len([p for p in out.iterdir() if ".quarantine-" in p.name]), 1)
        self.assertEqual(before, {p.name: file_sha256(p) for p in PROBE_FEED.iterdir()})


# --------------------------------------------------------------------------------------------------------------------
# Release workflow and runbook (structural; the workflow has not executed on GitHub)
# --------------------------------------------------------------------------------------------------------------------

class ReleaseWorkflowTextTests(unittest.TestCase):
    """Text-level checks that run without PyYAML (Python 3.9 hosts)."""

    def setUp(self):
        self.text = RELEASE_WORKFLOW.read_text(encoding="utf-8")
        self.code = "\n".join(line for line in self.text.splitlines() if not line.lstrip().startswith("#"))

    def test_no_tag_push_trigger_and_no_publisher(self):
        self.assertNotRegex(self.code, r"(?m)^\s*push:")
        self.assertNotRegex(self.code, r"(?m)^\s*tags:")
        self.assertIn("workflow_dispatch:", self.code)
        for forbidden in ("dotnet pack", "dotnet build", "dotnet test", "nuget push", "--skip-duplicate", "NUGET_API_KEY", "secrets.",
                          "softprops/", "attest-build-provenance", "gh release", "git push", "git tag", "pack-packages.ps1",
                          "setup-dotnet", "CycloneDX", "-Execute", "id-token", "attestations"):
            self.assertNotIn(forbidden, self.code, forbidden)
        self.assertNotRegex(self.code, r"(?m)^\s*(contents|packages|actions|id-token|attestations|deployments|statuses):\s*write")
        self.assertIn("promote-release.ps1", self.code)
        self.assertIn("ApprovedManifestSha256", self.code)

    def test_states_that_execution_needs_unavailable_trusted_authorization(self):
        self.assertIn("trusted", self.text.lower())
        self.assertIn("authorization", self.text.lower())
        self.assertNotIn("github.event.", self.code)


class ReleaseWorkflowStructureTests(unittest.TestCase):
    def setUp(self):
        try:
            import yaml
        except ImportError:  # pragma: no cover - required infrastructure on the tested host, a failure rather than a skip
            self.fail("PyYAML is required for the structural release workflow tests")
        self.workflow = yaml.safe_load(RELEASE_WORKFLOW.read_text(encoding="utf-8"))

    def triggers(self):
        return self.workflow.get("on", self.workflow.get(True))

    def steps(self):
        return [step for job in self.workflow["jobs"].values() for step in job.get("steps", [])]

    def test_manual_dispatch_only_with_read_permissions(self):
        self.assertEqual(set(self.triggers()), {"workflow_dispatch"})
        inputs = self.triggers()["workflow_dispatch"]["inputs"]
        self.assertTrue(inputs["approved_manifest_sha256"]["required"])
        self.assertEqual(self.workflow["permissions"], {"contents": "read"})
        for name, job in self.workflow["jobs"].items():
            permissions = job.get("permissions") or {}
            self.assertTrue(set(permissions) <= {"contents", "actions"}, (name, permissions))
            self.assertTrue(all(value == "read" for value in permissions.values()), (name, permissions))

    def test_actions_and_artifact_handling(self):
        uses = [step["uses"] for step in self.steps() if "uses" in step]
        allowed = {"actions/checkout@v6", "actions/download-artifact@v4", "actions/upload-artifact@v4"}
        self.assertTrue(set(uses) <= allowed, uses)
        self.assertIn("actions/upload-artifact@v4", uses)
        upload = [s for s in self.steps() if str(s.get("uses", "")).startswith("actions/upload-artifact")][0]
        self.assertIs(upload["with"]["overwrite"], True)
        runs = "\n".join(str(s.get("run", "")) for s in self.steps())
        self.assertIn("promote-release.ps1", runs)
        self.assertNotIn("${{", runs, "inputs reach run blocks only through env")
        self.assertNotIn("-Execute", runs)


class RunbookTests(unittest.TestCase):
    def test_runbook_documents_the_operator_procedure_and_unavailable_pieces(self):
        text = RUNBOOK.read_text(encoding="utf-8")
        for phrase in ("prepare-release.ps1", "promote-release.ps1", "-ReleasePreparation", "candidate.json", ".signature.p7s",
                       "uploadSha256", "downloadSha256", "symbols", "rollback", "uncertain", "idempotency", "Tag present elsewhere",
                       "Exit codes", "source commit S", "receipt commit R", "trusted authorization", "Not yet available",
                       "E4d", "governance", "quarantine", "--skip-duplicate", "dependency"):
            self.assertIn(phrase.lower(), text.lower(), phrase)


if __name__ == "__main__":
    unittest.main()

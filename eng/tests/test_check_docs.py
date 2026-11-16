"""Behavioral tests for eng/check-docs.py (links, snippet regions, warning ratchet, API surfaces)."""

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "eng" / "check-docs.py"


def run(*args):
    return subprocess.run(
        [sys.executable, "-B", str(TOOL), *map(str, args)],
        cwd=ROOT,
        capture_output=True,
        text=True,
    )


def write(path: Path, text: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


class LinkCheckTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "site"
        write(
            self.root / "index.html",
            '<a href="guide/">guide</a><a href="api/Type-1.html#Meridian_Type_1_Map__1">member</a>'
            '<a href="#top">self</a><a id="top"></a><a href="https://example.test/x">ext</a>'
            '<link rel="stylesheet" href="public/main.css"><script src="public/main.js"></script>'
            '<meta name="docfx:tocrel" content="toc.html">',
        )
        write(self.root / "guide" / "index.html", '<a href="../api/Space%20Name.html">space</a><img src="../public/logo.svg">')
        write(self.root / "api" / "Type-1.html", '<h2 id="Meridian_Type_1_Map__1">Map</h2>')
        write(self.root / "api" / "Space Name.html", "<p>ok</p>")
        write(self.root / "public" / "main.css", "")
        write(self.root / "public" / "main.js", "")
        write(self.root / "public" / "logo.svg", "<svg/>")
        write(self.root / "toc.html", '<a href="index.html">home</a>')
        write(self.root / "toc.json", json.dumps({"items": [{"name": "Home", "href": "index.html"}]}))
        self.report = Path(self.temp.name) / "links.json"

    def tearDown(self):
        self.temp.cleanup()

    def check(self, *extra):
        result = run("links", "--root", self.root, "--report", self.report, *extra)
        data = json.loads(self.report.read_text()) if self.report.exists() else None
        return result, data

    def test_valid_site_passes_and_inventories_external_links(self):
        result, data = self.check()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(data["broken"], [])
        self.assertGreaterEqual(data["internalLinks"], 9)
        self.assertEqual(data["external"], ["https://example.test/x"])
        self.assertGreaterEqual(data["pages"], 4)

    def test_missing_file_fails(self):
        write(self.root / "guide" / "index.html", '<a href="../missing.html">missing</a>')
        result, data = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertEqual([b["reason"] for b in data["broken"]], ["missing-target"])

    def test_wrong_case_path_fails_even_on_case_insensitive_disks(self):
        write(self.root / "guide" / "index.html", '<a href="../API/type-1.html">case</a>')
        result, data = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["broken"][0]["reason"], "case-mismatch")

    def test_missing_member_fragment_fails(self):
        write(self.root / "guide" / "index.html", '<a href="../api/Type-1.html#Meridian_Type_1_Missing">bad member</a>')
        result, data = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["broken"][0]["reason"], "missing-fragment")

    def test_missing_same_page_fragment_fails(self):
        write(self.root / "toc.html", '<a href="#nowhere">x</a>')
        result, data = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["broken"][0]["reason"], "missing-fragment")

    def test_encoded_path_must_decode_to_existing_file(self):
        write(self.root / "guide" / "index.html", '<a href="../api/Space%2520Name.html">double encoded</a>')
        result, data = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["broken"][0]["reason"], "missing-target")

    def test_toc_json_and_meta_targets_are_checked(self):
        write(self.root / "toc.json", json.dumps({"items": [{"name": "Gone", "href": "gone.html"}]}))
        result, data = self.check()
        self.assertEqual(result.returncode, 1)
        self.assertTrue(data["broken"][0]["page"].endswith("toc.json"))

    def test_root_absolute_link_must_stay_inside_deployment_prefix(self):
        write(self.root / "guide" / "index.html", '<a href="/index.html">root</a>')
        result, data = self.check("--prefix", "/meridian/")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["broken"][0]["reason"], "outside-prefix")
        write(self.root / "guide" / "index.html", '<a href="/meridian/index.html">root</a>')
        result, _ = self.check("--prefix", "/meridian/")
        self.assertEqual(result.returncode, 0, result.stdout)

    def test_duplicate_ids_fail_on_authored_pages_and_are_recorded_for_generated_api_pages(self):
        write(self.root / "toc.html", '<h2 id="part">a</h2><a id="part"></a>')
        result, data = self.check("--generated-api-dir", "api")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["broken"][0]["reason"], "duplicate-id")
        write(self.root / "toc.html", '<a href="index.html">home</a>')
        write(self.root / "api" / "Type-1.html", '<h2 id="Meridian_Type_1_Map__1">Map</h2><a id="Meridian_Type_1_Map_"></a><a id="Meridian_Type_1_Map_"></a>')
        result, data = self.check("--generated-api-dir", "api")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(data["generatedApiDuplicateIds"][0]["href"], "#Meridian_Type_1_Map_")
        result, data = self.check()
        self.assertEqual(result.returncode, 1)

    def test_empty_site_cannot_pass_vacuously(self):
        empty = Path(self.temp.name) / "empty"
        empty.mkdir()
        result = run("links", "--root", empty, "--report", self.report)
        self.assertEqual(result.returncode, 1)


class SnippetCheckTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        base = Path(self.temp.name)
        self.source = base / "Program.cs"
        write(
            self.source,
            "static void Demo()\n{\n    #region demo\n    var x = 1;\n    Console.WriteLine(x);  \n    #endregion\n}\n"
            "#region type\npublic sealed class T { }\n#endregion\n",
        )
        self.doc = base / "doc.md"
        self.report = base / "snippets.json"

    def tearDown(self):
        self.temp.cleanup()

    def check(self, text):
        write(self.doc, text)
        result = run("snippets", "--source", self.source, "--report", self.report, self.doc)
        return result, json.loads(self.report.read_text())

    def test_marked_fence_matching_dedented_region_passes(self):
        result, data = self.check(
            "<!-- snippet: demo -->\n```csharp\nvar x = 1;\nConsole.WriteLine(x);\n```\n"
            "<!-- snippet: type -->\n```csharp\npublic sealed class T { }\n```\n```bash\nls\n```\n"
        )
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual((data["compiled"], data["illustrative"], data["failures"]), (2, 0, []))

    def test_changed_fence_fails(self):
        result, data = self.check("<!-- snippet: demo -->\n```csharp\nvar x = 2;\nConsole.WriteLine(x);\n```\n")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["failures"][0]["reason"], "differs-from-region")

    def test_unmarked_csharp_fence_fails(self):
        result, data = self.check("```csharp\nvar y = 3;\n```\n")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["failures"][0]["reason"], "unclassified-fence")

    def test_unknown_region_fails(self):
        result, data = self.check("<!-- snippet: nope -->\n```csharp\nvar x = 1;\n```\n")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["failures"][0]["reason"], "missing-region")

    def test_illustrative_fence_requires_reason_and_existing_fixture(self):
        result, data = self.check("<!-- illustrative: needs a database; fixture: no/such/file.cs -->\n```csharp\nx();\n```\n")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(data["failures"][0]["reason"], "missing-fixture")
        result, data = self.check(f"<!-- illustrative: partial fragment; fixture: {self.source.name} -->\n```csharp\nx();\n```\n")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(data["illustrative"], 1)


class WarningRatchetTests(unittest.TestCase):
    LINE = (
        "/repo/src/Meridian.Mediator/Behaviors/AuditBehavior.cs({line},13): warning {code}: {message} "
        "[/repo/src/Meridian.Mediator/Meridian.Mediator.csproj::TargetFramework={tfm}]\n"
    )

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def log(self, name, *entries):
        path = self.base / name
        path.write_text("".join(self.LINE.format(line=l, code=c, message=m, tfm=t) for l, c, m, t in entries))
        return path

    def test_line_moves_and_tfm_repeats_do_not_create_new_warnings(self):
        before = self.log("before.log", (64, "CA1848", "Use LoggerMessage", "net8.0"))
        baseline = self.base / "baseline.json"
        result = run("warnings", "--repo", "/repo", "--write-baseline", baseline, before)
        self.assertEqual(result.returncode, 0, result.stderr)
        after = self.log("after.log", (70, "CA1848", "Use LoggerMessage", "net8.0"), (70, "CA1848", "Use LoggerMessage", "net10.0"))
        report = self.base / "report.json"
        result = run("warnings", "--repo", "/repo", "--baseline", baseline, "--report", report, after)
        self.assertEqual(result.returncode, 0, result.stdout)
        data = json.loads(report.read_text())
        self.assertEqual((data["unique"], data["new"], data["resolved"]), (1, [], []))

    def test_new_unexplained_warning_fails_and_explained_one_passes(self):
        baseline = self.base / "baseline.json"
        run("warnings", "--repo", "/repo", "--write-baseline", baseline, self.log("b.log", (1, "CA1848", "m", "net8.0")))
        after = self.log("a.log", (1, "CA1848", "m", "net8.0"), (9, "CS1591", "Missing XML comment for X", "net8.0"))
        report = self.base / "r.json"
        result = run("warnings", "--repo", "/repo", "--baseline", baseline, "--report", report, after)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(report.read_text())["new"][0]["code"], "CS1591")
        data = json.loads(baseline.read_text())
        data["explained"] = [{"code": "CS1591", "file": "src/Meridian.Mediator/Behaviors/AuditBehavior.cs", "reason": "tracked"}]
        baseline.write_text(json.dumps(data))
        result = run("warnings", "--repo", "/repo", "--baseline", baseline, "--report", report, after)
        self.assertEqual(result.returncode, 0, result.stdout)


class WarningRatchetFailsClosedTests(unittest.TestCase):
    """A vanished or unparseable warning set must never read as success."""

    LINE = "/repo/src/A.cs(1,1): warning CA1848: Use LoggerMessage [/repo/src/A.csproj::TargetFramework=net8.0]\n"

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.baseline = self.base / "baseline.json"
        (self.base / "seed.log").write_text(self.LINE)
        self.assertEqual(run("warnings", "--repo", "/repo", "--write-baseline", self.baseline, self.base / "seed.log").returncode, 0)

    def tearDown(self):
        self.temp.cleanup()

    def check(self, text):
        log = self.base / "run.log"
        log.write_text(text)
        return run("warnings", "--repo", "/repo", "--baseline", self.baseline, "--report", self.base / "r.json", log)

    def test_empty_log_fails_when_the_baseline_is_not_empty(self):
        result = self.check("")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("FAIL", result.stdout)

    def test_unparseable_warning_lines_fail(self):
        result = self.check(self.LINE + "src/B.cs: warning CS1591 in a new unsupported output format\n")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("unparsed", result.stdout)

    def test_log_that_contains_no_warning_lines_still_passes_for_an_empty_baseline(self):
        self.baseline.write_text(json.dumps({"schemaVersion": 1, "warnings": [], "explained": []}))
        result = self.check("Build succeeded.\n")
        self.assertEqual(result.returncode, 0, result.stdout)


class DocfxLogFailsClosedTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def check(self, text, *extra):
        log = self.base / "docfx.json"
        log.write_text(text)
        return run("docfx-log", "--log", log, "--report", self.base / "r.json", *extra)

    def test_empty_log_fails(self):
        result = self.check("")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("FAIL", result.stdout)

    def test_unparseable_line_fails(self):
        self.assertEqual(self.check("not json\n").returncode, 1)

    def test_clean_log_with_required_completion_marker_passes_and_missing_marker_fails(self):
        text = json.dumps({"severity": "info", "message": "Creating output..."}) + "\n"
        self.assertEqual(self.check(text, "--require-message", "Creating output").returncode, 0)
        result = self.check(text, "--require-message", "XRef map exported")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("XRef map exported", result.stdout)

    def test_warning_still_fails(self):
        text = json.dumps({"severity": "warning", "code": "InvalidFileLink", "message": "x"}) + "\n"
        self.assertEqual(self.check(text).returncode, 1)


class PackageEvidenceBindingTests(unittest.TestCase):
    """Reused verify-packages evidence must be bound to the exact feed packages and consumer source."""

    CANDIDATE = "2.1.4-t.1"

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.feed = self.base / "feed"
        self.evidence = self.base / "evidence"
        self.repo = self.base / "repo"
        (self.feed).mkdir()
        self.evidence.mkdir()
        (self.feed / "Meridian.Mapping.2.1.4-t.1.nupkg").write_bytes(b"nupkg-bytes")
        write(self.repo / "tests/consumers/Doc/Program.cs", "// region source\n")
        self.sha_pkg = hashlib.sha256(b"nupkg-bytes").hexdigest()
        self.sha_src = hashlib.sha256(b"// region source\n").hexdigest()
        self.save()

    def tearDown(self):
        self.temp.cleanup()

    def save(self, pkg_sha=None, src_sha=None, with_sources=True):
        sbom = {"candidateVersion": self.CANDIDATE, "distributedPackages": [
            {"id": "Meridian.Mapping", "package": "Meridian.Mapping.2.1.4-t.1.nupkg", "sha256": pkg_sha or self.sha_pkg}]}
        (self.evidence / "sbom-inventory.json").write_text(json.dumps(sbom))
        row = {"consumer": "Meridian.DocumentationConsumer", "variant": "migrated", "framework": "net8.0"}
        if with_sources:
            row["sourceFiles"] = [{"source": "tests/consumers/Doc/Program.cs", "file": "Program.cs", "sha256": src_sha or self.sha_src}]
        (self.evidence / "consumer-results.json").write_text(json.dumps([
            {"consumer": "Meridian.LegacyConsumer", "variant": "baseline", "framework": "net8.0"}, row]))

    def check(self):
        return run("evidence", "--evidence", self.evidence, "--feed", self.feed, "--repo", self.repo,
                   "--consumer", "Meridian.DocumentationConsumer", "--report", self.base / "e.json")

    def test_matching_evidence_passes(self):
        result = self.check()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_feed_package_hash_mismatch_fails(self):
        self.save(pkg_sha="0" * 64)
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("Meridian.Mapping", result.stdout)

    def test_edited_consumer_source_fails(self):
        write(self.repo / "tests/consumers/Doc/Program.cs", "// region source edited after the evidence run\n")
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("Program.cs", result.stdout)

    def test_evidence_without_recorded_source_hashes_fails(self):
        self.save(with_sources=False)
        result = self.check()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("sourceFiles", result.stdout)


class SurfaceComparisonTests(unittest.TestCase):
    def test_identical_surfaces_pass_and_differences_fail(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            for tfm in ("net8.0", "net9.0"):
                write(base / tfm / "A.yml", f"uid: A\nassemblies:\n- Meridian\n# input {base / tfm}\n")
            report = base / "surface.json"
            result = run("surface", "--report", report, base / "net8.0", base / "net9.0")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            write(base / "net9.0" / "B.yml", "uid: B\n")
            result = run("surface", "--report", report, base / "net8.0", base / "net9.0")
            self.assertEqual(result.returncode, 1)
            self.assertEqual(json.loads(report.read_text())["differences"][0]["file"], "B.yml")

    def test_reference_table_only_differences_are_reported_but_do_not_fail(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            write(base / "net8.0" / "A.yml", "items:\n- uid: A\nreferences:\n- uid: B\n")
            write(base / "net10.0" / "A.yml", "items:\n- uid: A\nreferences:\n- uid: B\n- uid: C\n")
            report = base / "surface.json"
            result = run("surface", "--report", report, base / "net8.0", base / "net10.0")
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertEqual(json.loads(report.read_text())["referenceTableOnlyDifferences"][0]["file"], "A.yml")
            write(base / "net10.0" / "A.yml", "items:\n- uid: A\n  syntax: changed\nreferences:\n- uid: B\n")
            self.assertEqual(run("surface", "--report", report, base / "net8.0", base / "net10.0").returncode, 1)


class MarkdownLinkTests(unittest.TestCase):
    def test_relative_markdown_links_must_resolve_with_exact_case(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            write(base / "docs" / "guide.md", "x")
            write(base / "README.md", "[g](docs/guide.md#part) [web](https://example.test) [api](xref:A.B) [self](#top)\n```md\n[ignored](nope.md)\n```\n")
            report = base / "md.json"
            result = run("mdlinks", "--report", report, base / "README.md")
            self.assertEqual(result.returncode, 0, result.stdout)
            data = json.loads(report.read_text())
            self.assertEqual((data["checked"], data["broken"]), (1, []))
            write(base / "README.md", "[g](Docs/guide.md) [m](missing.md)\n")
            result = run("mdlinks", "--report", report, base / "README.md")
            self.assertEqual(result.returncode, 1)
            self.assertEqual(sorted(b["reason"] for b in json.loads(report.read_text())["broken"]), ["case-mismatch", "missing-target"])


if __name__ == "__main__":
    unittest.main()

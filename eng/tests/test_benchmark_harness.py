"""Behavioral tests for the E3 benchmark harness: semantic preflight, the run script contract and the result tools.

The harness assertions are executable proofs, not text mirrors: the preflight is run against the real scenario classes,
a sabotaged scenario must make it fail, the run script must refuse unsafe invocations before any timing, and the summary
tool is exercised on synthetic BenchmarkDotNet JSON with known statistics. No test measures performance.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "benchmarks" / "Meridian.Benchmarks"
DLL = PROJECT / "bin" / "Release" / "net10.0" / "Meridian.Benchmarks.dll"
SCRIPT = ROOT / "eng" / "run-benchmarks.ps1"
SUMMARY = ROOT / "eng" / "benchmark-summary.py"
PWSH = os.environ.get("MERIDIAN_TEST_PWSH") or shutil.which("pwsh") or "/opt/homebrew/bin/pwsh"

# Scenario families that the E3 evidence must cover. Each entry is the class name; the preflight must run it.
REQUIRED_SCENARIOS = {
    "E3MappingFlat", "E3MappingNested", "E3MappingCollectionPayload", "E3MappingDeep", "E3MappingReferences",
    "E3MediatorDispatch", "E3MediatorScopeExisting", "E3MediatorScopePerOperation", "E3MediatorPipelines",
    "E3MediatorCacheBehavior", "E3MediatorIdempotencyBehavior", "E3MediatorMetrics",
    "E3NotificationPublish", "E3NotificationTaskWhenAll",
    "E3StartupMapping", "E3StartupMediator", "E3ColdMapping", "E3ColdMediator",
}


def run(command, **kwargs):
    return subprocess.run(command, cwd=ROOT, capture_output=True, text=True, **kwargs)


def setUpModule():
    build = run(["dotnet", "build", str(PROJECT), "-c", "Release", "-v:q", "-nologo"])
    if build.returncode != 0:
        raise AssertionError("benchmark project does not build:\n" + build.stdout[-4000:] + build.stderr[-2000:])
    if not DLL.exists():
        raise AssertionError(f"missing benchmark assembly {DLL}")


class PreflightTests(unittest.TestCase):
    def test_selftest_rejects_every_class_of_mismatch(self):
        result = run(["dotnet", str(DLL), "--e3-preflight-selftest"])
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        match = re.search(r"SELFTEST PASS rejected=(\d+)", result.stdout)
        self.assertIsNotNone(match, result.stdout + result.stderr)
        # value mismatch, null mismatch, count mismatch, aliased instance, default-valued leaf, broken reference
        # identity, collapsed distinct identity, a throwing scenario preflight and a benchmark type BenchmarkDotNet would reject.
        self.assertGreaterEqual(int(match.group(1)), 10)

    def test_preflight_runs_every_required_scenario_and_passes(self):
        result = run(["dotnet", str(DLL), "--e3-preflight"])
        self.assertEqual(0, result.returncode, result.stdout[-6000:] + result.stderr[-2000:])
        passed = set(re.findall(r"^PREFLIGHT ok (\w+)", result.stdout, re.MULTILINE))
        self.assertEqual(set(), REQUIRED_SCENARIOS - passed, "scenarios missing from preflight")
        self.assertNotIn("PREFLIGHT FAIL", result.stdout)

    def test_preflight_selected_by_class_runs_only_that_class(self):
        result = run(["dotnet", str(DLL), "--e3-preflight", "E3MappingFlat"])
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        passed = set(re.findall(r"^PREFLIGHT ok (\w+)", result.stdout, re.MULTILINE))
        self.assertEqual({"E3MappingFlat"}, passed)

    def test_preflight_unknown_class_is_an_error(self):
        result = run(["dotnet", str(DLL), "--e3-preflight", "NoSuchScenarioClass"])
        self.assertNotEqual(0, result.returncode)


class RunScriptContractTests(unittest.TestCase):
    def script(self, *arguments):
        return run([PWSH, "-NoProfile", "-NonInteractive", "-File", str(SCRIPT), *arguments])

    def test_filter_is_required(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.script("-OutputDirectory", str(Path(temp) / "out"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Filter", result.stdout + result.stderr)

    def test_filter_that_matches_zero_scenarios_fails_before_timing(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / "out"
            result = self.script("-Filter", "*NoSuchScenarioAnywhere*", "-OutputDirectory", str(out))
            self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("matches zero", result.stdout + result.stderr)
            manifest = json.loads((out / "manifest.json").read_text())
            self.assertEqual("failed", manifest["status"])
            self.assertFalse((out / "bdn").exists(), "no timing may start for a zero-match filter")

    def test_nonempty_output_directory_is_refused_and_left_untouched(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / "out"
            out.mkdir()
            (out / "keep.txt").write_text("precious")
            result = self.script("-Filter", "*E3MappingFlat*", "-OutputDirectory", str(out))
            self.assertNotEqual(0, result.returncode)
            self.assertIn("not empty", result.stdout + result.stderr)
            self.assertEqual("precious", (out / "keep.txt").read_text())

    def test_output_directory_inside_source_trees_is_refused(self):
        for tree in ("src", "benchmarks", "eng", "tests", "docs"):
            with self.subTest(tree=tree):
                target = ROOT / tree / "e3-run-output-must-not-exist"
                result = self.script("-Filter", "*E3MappingFlat*", "-OutputDirectory", str(target))
                self.assertNotEqual(0, result.returncode)
                self.assertIn("overlaps", result.stdout + result.stderr)
                self.assertFalse(target.exists())

    def test_filter_cannot_smuggle_command_line_switches(self):
        with tempfile.TemporaryDirectory() as temp:
            result = self.script("-Filter", "--job", "-OutputDirectory", str(Path(temp) / "out"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("invalid", (result.stdout + result.stderr).lower())

    def test_smoke_run_writes_manifest_raw_results_and_summary(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / "out"
            result = self.script(
                "-Filter", "*E3MappingFlat*", "-OutputDirectory", str(out), "-Profile", "Smoke",
                "-WindowName", "unit-test-smoke-window")
            self.assertEqual(0, result.returncode, result.stdout[-5000:] + result.stderr[-2000:])
            manifest = json.loads((out / "manifest.json").read_text())
            self.assertEqual("passed", manifest["status"])
            self.assertEqual(1, manifest["schemaVersion"])
            self.assertEqual("Smoke", manifest["profile"])
            self.assertEqual("unit-test-smoke-window", manifest["window"]["name"])
            self.assertEqual("*E3MappingFlat*", manifest["filter"])
            self.assertTrue(manifest["preflight"]["passed"])
            for key in ("sdk", "runtimeVersion", "os", "cpu", "memoryBytes", "powerSource", "loadAverageBefore"):
                self.assertIn(key, manifest["host"], key)
            for key in ("gitHead", "productSourceSha256", "harnessSha256", "assemblies", "lane"):
                self.assertIn(key, manifest["candidate"], key)
            self.assertEqual("project-reference", manifest["candidate"]["lane"])
            competitors = {entry["id"]: entry for entry in manifest["competitors"]}
            self.assertEqual({"Mapster", "Riok.Mapperly", "AutoMapper", "MediatR"}, set(competitors))
            self.assertEqual("MIT", competitors["Mapster"]["license"])
            self.assertEqual("Apache-2.0", competitors["Riok.Mapperly"]["license"])
            self.assertEqual("MIT", competitors["AutoMapper"]["license"])
            self.assertEqual("Apache-2.0", competitors["MediatR"]["license"])
            self.assertEqual("14.0.0", competitors["AutoMapper"]["version"])
            self.assertTrue(competitors["AutoMapper"]["historical"])
            self.assertTrue(competitors["MediatR"]["historical"])
            self.assertFalse(competitors["Mapster"]["historical"])
            self.assertTrue(list((out / "bdn" / "results").glob("*E3MappingFlat-report-full.json")))
            self.assertTrue((out / "summary.md").exists())
            self.assertIn("Smoke", (out / "summary.md").read_text())


def bdn_report(rows, title="Meridian.Benchmarks.E3Fixture"):
    benchmarks = []
    for name, launch_means, alloc, gen0 in rows:
        measurements = []
        for launch, mean in enumerate(launch_means, start=1):
            for iteration in range(1, 6):
                measurements.append({
                    "IterationMode": "Workload", "IterationStage": "Result", "LaunchIndex": launch,
                    "IterationIndex": iteration, "Operations": 1000, "Nanoseconds": mean * 1000 * (1 + 0.01 * (iteration - 3)),
                })
        flat = [m["Nanoseconds"] / m["Operations"] for m in measurements]
        mean_all = sum(flat) / len(flat)
        sd = (sum((x - mean_all) ** 2 for x in flat) / (len(flat) - 1)) ** 0.5
        benchmarks.append({
            "FullName": f"Meridian.Benchmarks.E3Fixture.{name}", "DisplayInfo": f"E3Fixture.{name}: E3-fixture",
            "Method": name, "Parameters": "",
            "Statistics": {"Mean": mean_all, "StandardError": sd / len(flat) ** 0.5, "StandardDeviation": sd,
                           "Median": mean_all, "N": len(flat), "Min": min(flat), "Max": max(flat)},
            "Memory": {"Gen0Collections": gen0, "Gen1Collections": 0, "Gen2Collections": 0,
                       "TotalOperations": 1_000_000, "BytesAllocatedPerOperation": alloc},
            "Measurements": measurements,
        })
    return {"Title": title, "HostEnvironmentInfo": {"BenchmarkDotNetVersion": "0.15.8"}, "Benchmarks": benchmarks}


class SummaryToolTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def write_results(self, name, rows):
        directory = self.root / name
        directory.mkdir(parents=True)
        (directory / "Meridian.Benchmarks.E3Fixture-report-full.json").write_text(json.dumps(bdn_report(rows)))
        return directory

    def tool(self, *arguments):
        return run([sys.executable, "-B", str(SUMMARY), *map(str, arguments)])

    def test_summarize_reports_mean_variance_launch_spread_and_allocation(self):
        results = self.write_results("run", [("Steady", [100.0, 101.0], 128, 5), ("Drifting", [100.0, 150.0], 64, 0)])
        output = self.root / "summary.md"
        result = self.tool("summarize", "--results", results, "--output", output, "--json", self.root / "summary.json")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        rows = {row["method"]: row for row in json.loads((self.root / "summary.json").read_text())["rows"]}
        self.assertEqual({"Steady", "Drifting"}, set(rows))
        self.assertEqual(128, rows["Steady"]["allocatedBytesPerOp"])
        self.assertLess(rows["Steady"]["launchSpreadPercent"], 2.0)
        self.assertGreater(rows["Drifting"]["launchSpreadPercent"], 40.0)
        self.assertAlmostEqual(rows["Steady"]["cvPercent"], rows["Steady"]["stdDev"] / rows["Steady"]["meanNs"] * 100, places=6)
        text = output.read_text()
        self.assertIn("Steady", text)
        self.assertIn("launch spread", text.lower())

    def test_compare_flags_mean_regressions_over_ten_percent_and_any_allocation_increase(self):
        base = self.write_results("base", [
            ("Same", [100.0, 100.0], 100, 1), ("Slower", [100.0, 100.0], 100, 1), ("Fatter", [100.0, 100.0], 100, 1),
            ("Faster", [100.0, 100.0], 200, 1), ("OnlyBase", [100.0, 100.0], 100, 1)])
        cand = self.write_results("cand", [
            ("Same", [101.0, 101.0], 100, 1), ("Slower", [112.0, 112.0], 100, 1), ("Fatter", [100.0, 100.0], 124, 1),
            ("Faster", [60.0, 60.0], 100, 1), ("OnlyCand", [100.0, 100.0], 100, 1)])
        result = self.tool("compare", "--baseline", base, "--candidate", cand, "--output", self.root / "cmp.md",
                           "--json", self.root / "cmp.json")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        report = json.loads((self.root / "cmp.json").read_text())
        flags = {row["method"]: row["flags"] for row in report["rows"]}
        self.assertEqual([], flags["Same"])
        self.assertEqual(["mean>+10%"], flags["Slower"])
        self.assertEqual(["alloc+"], flags["Fatter"])
        self.assertEqual([], flags["Faster"])
        self.assertEqual(["OnlyBase"], report["unpaired"]["baselineOnly"])
        self.assertEqual(["OnlyCand"], report["unpaired"]["candidateOnly"])
        self.assertEqual(4, len(report["rows"]))

    def test_compare_rejects_empty_result_directories(self):
        empty = self.root / "empty"
        empty.mkdir()
        other = self.write_results("other", [("A", [1.0], 0, 0)])
        result = self.tool("compare", "--baseline", empty, "--candidate", other, "--output", self.root / "x.md")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("no benchmark", (result.stdout + result.stderr).lower())


if __name__ == "__main__":
    unittest.main()

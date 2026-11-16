"""Counterexamples confirmed in the final Claude review; no product builds."""

import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

from eng.tests import test_candidate_gate as fixtures

gate = fixtures.gate
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


class FinalEvidenceBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix="meridian-e4-final-"))
        self.addCleanup(shutil.rmtree, self.directory, ignore_errors=True)

    def document(self):
        root = ET.fromstring(fixtures.real_trx("Core.Tests", "net10.0", passed=2))
        previous = root.find(NS + "TestEntries")
        if previous is not None:
            root.remove(previous)
        self.entries(root)
        return root

    def entries(self, root):
        entries = ET.SubElement(root, NS + "TestEntries")
        for result in root.findall(NS + "Results/" + NS + "UnitTestResult"):
            ET.SubElement(entries, NS + "TestEntry", testId=result.get("testId"), executionId=result.get("executionId"))

    def parse(self, root):
        path = self.directory / "run.trx"
        ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
        return gate.parse_trx(path)

    def test_aborted_all_passed_results_never_count_as_a_completed_run(self):
        root = self.document()
        summary = root.find(NS + "ResultSummary")
        summary.set("outcome", "Failed")
        infos = ET.SubElement(summary, NS + "RunInfos")
        ET.SubElement(infos, NS + "RunInfo", outcome="Error").text = "Test host process crashed; run aborted."
        result = self.parse(root)
        self.assertEqual(result["passed"], 2)
        self.assertTrue(result["problems"], result)

    def test_one_theory_definition_can_own_multiple_uniquely_linked_results(self):
        root = self.document()
        definitions = root.find(NS + "TestDefinitions")
        definitions.remove(definitions[1])
        results = root.findall(NS + "Results/" + NS + "UnitTestResult")
        results[1].set("testId", results[0].get("testId"))
        root.remove(root.find(NS + "TestEntries"))
        self.entries(root)
        result = self.parse(root)
        self.assertEqual(result["problems"], [], result)
        self.assertEqual(result["passed"], 2)

    def test_missing_duplicate_or_unrelated_test_entries_are_rejected(self):
        for mutation in ("missing", "duplicate", "unrelated"):
            root = self.document()
            entries = root.find(NS + "TestEntries")
            if mutation == "missing":
                entries.remove(entries[1])
            elif mutation == "duplicate":
                ET.SubElement(entries, NS + "TestEntry", **entries[0].attrib)
            else:
                entries[1].set("testId", "not-a-result")
            with self.subTest(mutation=mutation):
                self.assertTrue(self.parse(root)["problems"])

    def test_unreferenced_test_definitions_are_not_silently_accepted(self):
        root = self.document()
        definition = root.find(NS + "TestDefinitions")[0]
        clone = ET.fromstring(ET.tostring(definition))
        clone.set("id", "unused-definition")
        root.find(NS + "TestDefinitions").append(clone)
        self.assertTrue(self.parse(root)["problems"])

    def test_malformed_toolchain_lists_are_reported_before_iteration(self):
        path = self.directory / "lane-toolchain.json"
        for value in ("10.0.300 [/sdk]", {}, None):
            path.write_text(json.dumps({"sdkVersion": "10.0.300", "sdks": value, "runtimes": []}))
            problems = []
            gate._derive_toolchain([path], problems)
            with self.subTest(value=value):
                self.assertTrue(problems)

    def test_real_child_plan_supports_two_csv_exclusions_without_reading_the_feed(self):
        executable = shutil.which("pwsh")
        self.assertTrue(executable, "PowerShell is required")
        excluded = ["Meridian.RedisIdempotencyConsumer", "Meridian.EfTransactionsConsumer"]
        process = subprocess.run([executable, "-NoProfile", "-File", str(fixtures.ROOT / "eng/verify-packages.ps1"),
                                  "-Feed", str(self.directory / "missing-feed"), "-CandidateVersion", "3.0.0-probe.1",
                                  "-ExcludeConsumerIds", ",".join(excluded), "-PlanOnly"],
                                 capture_output=True, text=True, timeout=30)
        self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
        plan = json.loads(process.stdout)
        self.assertEqual(plan["omittedIds"], excluded)
        self.assertNotIn(excluded[0], plan["selectedIds"])
        self.assertNotIn(excluded[1], plan["selectedIds"])
        self.assertFalse(plan["executionProven"])
        self.assertFalse((self.directory / "missing-feed").exists())

    def test_native_collection_ignores_sdk_intermediate_and_dsym_binary_copies(self):
        for rid in ("linux-x64", "osx-arm64"):
            f = fixtures.Fixture(self)
            lane = f.lane("native/linux-x64/net10.0")
            lane["rid"] = rid
            lane["host"] = dict(gate.RID_HOSTS[rid])
            raw = f.native_raw()
            for executable in list(raw.rglob("publish/Meridian.*Consumer")):
                work = executable.parent.parent
                for relative in ("bin/Release/native", "publish/app.dSYM/Contents/Resources/DWARF"):
                    duplicate = work / relative / executable.name
                    duplicate.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(executable, duplicate)
            result = f.collect(lane["id"], raw)
            with self.subTest(rid=rid):
                self.assertEqual(result["limitations"], [], result)
                self.assertEqual(f.evaluate(only={lane["id"]})["overall"], "SCOPED-PASS")

    def test_native_mapping_receipt_cannot_bind_a_differently_named_package(self):
        f = fixtures.Fixture(self)
        lane_id = "native/linux-x64/net10.0"
        raw = f.native_raw(mapping_package={"file": f"/feed/Other.Package.{fixtures.VERSION}.nupkg",
                                            "sha256": f.packages["Meridian.Mapping"]})
        f.collect(lane_id, raw)
        summary = f.evaluate(only={lane_id})
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("archive other than the candidate" in item for item in summary["failures"]))

    def test_null_or_wrongly_typed_lane_collections_fail_without_crashing(self):
        for field, value in (("services", None), ("services", [{"name": []}]), ("services", [{"name": "redis"}, {"name": "redis"}]),
                             ("limitations", None), ("limitations", {}), ("limitations", [3])):
            f = fixtures.Fixture(self)
            lane_id = "runtime/linux/net8.0"
            f.collect(lane_id, f.runtime_raw())
            f.mutate(lane_id, lambda row: row.update({field: value}))
            with self.subTest(field=field, value=value):
                self.assertEqual(f.evaluate(only=[lane_id])["overall"], "FAIL")

    def test_package_manifest_requires_explicit_boolean_clean_source(self):
        for value in (None, 0, "", []):
            f = fixtures.Fixture(self)
            path = f.feed / f"package-manifest.{fixtures.VERSION}.json"
            data = json.loads(path.read_text())
            if value is None:
                data.pop("sourceDirty")
            else:
                data["sourceDirty"] = value
            path.write_text(json.dumps(data))
            with self.subTest(value=value):
                self.assertTrue(gate.inspect_feed(f.feed, fixtures.VERSION)["problems"])

    def test_non_string_baseline_ids_fail_without_unhashable_membership_errors(self):
        f = fixtures.Fixture(self)
        lane_id = "consumers/linux/net10.0"
        raw = f.consumer_raw()
        path = raw / "baseline-hashes.json"
        rows = json.loads(path.read_text())
        rows[0]["id"] = []
        path.write_text(json.dumps(rows))
        result = f.collect(lane_id, raw)
        self.assertTrue(result["limitations"])
        self.assertEqual(f.evaluate(only=[lane_id])["overall"], "FAIL")

    def test_all_combined_producer_paths_preserve_a_negative_crash_exit(self):
        runner = str(fixtures.ROOT / "eng/verify-candidate.ps1").replace("'", "''")
        directory = str(self.directory).replace("'", "''")
        script = r"""
$ErrorActionPreference='Stop'
$PSScriptRoot=Split-Path -Parent 'RUNNER'
$ast = [Management.Automation.Language.Parser]::ParseFile('RUNNER', [ref]$null, [ref]$null)
foreach ($name in @('Merge-ProducerExit', 'Invoke-Lane')) {
    $definition = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    if ($definition) { Invoke-Expression ($definition.Extent.Text.Replace('$PSScriptRoot', "'DIRECTORY'")) }
}
function Save-PreviousAttempt {}
function Get-LaneEnvironment { @{} }
function Write-LaneToolchain {}
function Get-Dotnet { 'dotnet' }
function Get-TestSuites { [pscustomobject]@{suite='Fixture.Tests'} }
function Invoke-DotnetTests { -1073741819 }
function Invoke-Native { if ($script:call++ -eq 0) { -1073741819 } else { 0 } }
$policyRepo='DIRECTORY'; $repo=$policyRepo; $Feed=$policyRepo; $python='python'; $releaseArguments=@()
$policy = [pscustomobject]@{coverage=@{baseline='baseline.json'}; warnings=@{baseline='baseline.json'; solution='fixture.sln'}}
$answers=@()
foreach ($kind in @('runtime-tests','provider','native-aot','coverage','warning-ratchet')) {
    $script:call=0
    # Runtime/provider tests are the first producer; the following samples succeed.
    if ($kind -in @('runtime-tests','provider')) { $script:call=1 }
    $raw=Join-Path 'DIRECTORY' $kind
    $null=New-Item -ItemType Directory -Force (Join-Path $raw 'cov')
    $lane=[pscustomobject]@{id=$kind; kind=$kind; countsProfile='http';framework='net10.0';rid='linux-x64';sdk='10.0.300'}
    $answers += [ordered]@{kind=$kind; exit=(Invoke-Lane $policy $lane $raw '3.0.0-probe.1')}
}
ConvertTo-Json -InputObject $answers -Compress
""".replace("RUNNER", runner).replace("DIRECTORY", directory)
        process = subprocess.run([shutil.which("pwsh"), "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=30)
        self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
        for row in json.loads(process.stdout):
            with self.subTest(kind=row["kind"]):
                self.assertEqual(row["exit"], -1073741819)

    def test_docker_context_inspection_and_container_calls_use_the_same_environment(self):
        environment = {"DOCKER_CONTEXT": "fixture-local", "PATH": "/fixture/bin"}
        docker = fixtures.FakeDocker("6379")
        observed = []

        def run(arguments, **kwargs):
            observed.append(kwargs.get("env"))
            self.assertEqual(arguments[0], "docker")
            return docker(arguments[1:])

        with mock.patch.object(fixtures.collect.subprocess, "run", side_effect=run):
            result = fixtures.collect.resolve_container_image("6379", environment=environment)
        self.assertNotIn("error", result, result)
        self.assertTrue(observed)
        self.assertTrue(all(value == environment for value in observed), observed)


if __name__ == "__main__":
    unittest.main()

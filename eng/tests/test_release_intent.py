"""Stable package identity needs explicit preparation intent at every orchestration boundary."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
PWSH = os.environ.get("PWSH") or shutil.which("pwsh")


class ReleaseIntentTests(unittest.TestCase):
    def test_shared_guard_accepts_only_canonical_versions_and_explicit_stable_intent(self):
        controls = [("3.0.0-probe.1", False, True), ("3.0.0", True, True),
                    ("3.0.0", False, False), ("03.0.0", True, False),
                    ("3.00.0", True, False), ("3.0.00", True, False),
                    ("3.0", True, False), ("3.0.0.0", True, False),
                    ("3.0.0+build", True, False), ("v3.0.0", True, False),
                    ("3.0.0-alpha.01", False, False), ("3.0.0-", False, False),
                    ("3.0.0-alpha..1", False, False), ("2147483648.0.0", True, False),
                    ("3.0.0-probe.1", True, False), ("3.0.0\n", True, False),
                    ("3.0.0-probe.1\n", False, False)]
        payload = json.dumps(controls).replace("'", "''")
        module = str(ROOT / "eng/package-inventory.psm1").replace("'", "''")
        script = "Import-Module '" + module + "' -Force -DisableNameChecking; " + """
$results = @()
foreach ($case in (ConvertFrom-Json -InputObject 'PAYLOAD')) {
    $accepted = $true
    try { Assert-CandidateVersion -CandidateVersion $case[0] -ReleasePreparation ([bool] $case[1]) }
    catch { $accepted = $false }
    $results += [ordered]@{ version=$case[0]; intent=$case[1]; accepted=$accepted }
}
ConvertTo-Json -InputObject $results -Depth 4
""".replace("PAYLOAD", payload)
        self.assertTrue(PWSH, "PowerShell is required")
        result = subprocess.run([PWSH, "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        rows = json.loads(result.stdout)
        self.assertEqual(len(rows), len(controls))
        for row, case in zip(rows, controls):
            with self.subTest(version=case[0], intent=case[1]):
                self.assertEqual(row["accepted"], case[2], row)

    def test_stable_consumer_plan_requires_explicit_intent_before_feed_or_build_access(self):
        with tempfile.TemporaryDirectory(prefix="meridian-release-intent-") as directory:
            missing_feed = Path(directory) / "must-not-be-read"
            command = [PWSH, "-NoProfile", "-File", str(ROOT / "eng/verify-packages.ps1"),
                       "-Feed", str(missing_feed), "-CandidateVersion", "3.0.0", "-PlanOnly"]
            denied = subprocess.run(command, capture_output=True, text=True, timeout=90)
            self.assertNotEqual(denied.returncode, 0)
            allowed = subprocess.run(command + ["-ReleasePreparation"], capture_output=True, text=True, timeout=90)
            self.assertEqual(allowed.returncode, 0, allowed.stdout + allowed.stderr)
            plan = json.loads(allowed.stdout)
            self.assertFalse(plan["executionProven"])
            self.assertFalse(missing_feed.exists())

    def test_missing_entrypoint_cannot_disappear_from_the_metadata_verdict(self):
        with tempfile.TemporaryDirectory(prefix="meridian-missing-entrypoint-") as directory:
            root = Path(directory)
            (root / "eng").mkdir()
            names = ("pack-packages.ps1", "verify-packages.ps1", "verify-candidate.ps1", "verify-docs.ps1",
                     "run-aot-smoke.ps1", "test-ef10-compatibility.ps1", "prepare-release.ps1", "promote-release.ps1")
            for name in names[1:]:
                shutil.copyfile(ROOT / "eng" / name, root / "eng" / name)
            with mock.patch(__name__ + ".ROOT", root):
                with self.assertRaises(AssertionError):
                    self.test_every_package_entrypoint_has_the_same_explicit_intent_parameter()

    def test_empty_success_output_cannot_satisfy_the_entrypoint_metadata_contract(self):
        empty = subprocess.CompletedProcess([], 0, "[]", "")
        with mock.patch.object(subprocess, "run", return_value=empty):
            with self.assertRaises(AssertionError):
                self.test_every_package_entrypoint_has_the_same_explicit_intent_parameter()

    def test_actual_in_process_inspection_calls_forward_the_named_switch(self):
        # Execute the three exact pack invocation ASTs with a child binding seam that aborts before any build.
        # This proves PowerShell's script-call binding, not a successful stable build or release.
        source_paths = [str(ROOT / "eng/verify-packages.ps1"), str(ROOT / "eng/run-aot-smoke.ps1")]
        payload = json.dumps(source_paths).replace("'", "''")
        extract = """
$ErrorActionPreference = 'Stop'
$rows = @()
foreach ($path in (ConvertFrom-Json -InputObject 'PAYLOAD')) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref] $tokens, [ref] $errors)
    if ($errors.Count) { throw 'Invalid script syntax' }
    foreach ($call in $ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Ampersand -and
        $node.Extent.Text.Contains("'pack-packages.ps1'")
    }, $true)) { $rows += [ordered]@{ path=$path; command=$call.Extent.Text } }
}
ConvertTo-Json -InputObject $rows
""".replace("PAYLOAD", payload)
        result = subprocess.run([PWSH, "-NoProfile", "-Command", extract], capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = json.loads(result.stdout)
        self.assertEqual(len(calls), 3)
        with tempfile.TemporaryDirectory(prefix="meridian-script-switch-") as directory:
            root = Path(directory)
            (root / "pack-packages.ps1").write_text("""param(
[string] $Configuration = 'Release', [string] $OutputDirectory, [string] $CandidateVersion,
[string] $Manifest, [switch] $InspectOnly, [switch] $ReleasePreparation)
Write-Host "BOUND-INTENT=$([bool] $ReleasePreparation)"
throw 'STOP: binding-only fixture; no package inspection or production is performed.'
""")
            for index, call in enumerate(calls):
                with self.subTest(path=call["path"], index=index):
                    harness = root / ("call-" + str(index) + ".ps1")
                    harness.write_text("""$ErrorActionPreference = 'Stop'
$Feed = $PSScriptRoot
$CandidateVersion = '3.0.0'
$manifestPath = Join-Path $PSScriptRoot 'unused.json'
$ReleasePreparation = $true
$releaseArguments = @('-ReleasePreparation')
""" + call["command"] + "\n")
                    run = subprocess.run([PWSH, "-NoProfile", "-File", str(harness)], capture_output=True, text=True, timeout=90)
                    self.assertNotEqual(run.returncode, 0, "The child must stop before any real producer can run")
                    self.assertIn("BOUND-INTENT=True", run.stdout, run.stdout + run.stderr)

    def test_every_package_entrypoint_has_the_same_explicit_intent_parameter(self):
        names = ("pack-packages.ps1", "verify-packages.ps1", "verify-candidate.ps1", "verify-docs.ps1",
                 "run-aot-smoke.ps1", "test-ef10-compatibility.ps1", "prepare-release.ps1", "promote-release.ps1")
        module = str(ROOT / "eng").replace("'", "''")
        script = "$ErrorActionPreference = 'Stop'; $names = " + ",".join("'" + n + "'" for n in names) + "; " + """
$rows = @($names | ForEach-Object {
    $command = Get-Command (Join-Path 'MODULE' $_)
    [ordered]@{ name=$_; hasIntent=$command.Parameters.ContainsKey('ReleasePreparation') }
})
ConvertTo-Json -InputObject $rows
""".replace("MODULE", module)
        result = subprocess.run([PWSH, "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        rows = json.loads(result.stdout)
        self.assertIsInstance(rows, list)
        self.assertEqual(len(rows), len(names))
        self.assertEqual({row["name"] for row in rows}, set(names))
        for row in rows:
            self.assertTrue(row["hasIntent"], row)


if __name__ == "__main__":
    unittest.main()

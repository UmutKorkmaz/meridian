"""Exercise the actual consumer entry point's read-only compiler plan.

Planning proves the approved invocation contract, never execution of a compiler.
The dotnet trap makes accidental build/restore work an observable failure.
"""

import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class ConsumerCompilerContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="meridian-compiler-plan-")
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)
        self.canary = self.work / "dotnet-was-called"
        self.bin = self.work / "bin"
        self.bin.mkdir()
        if sys.platform != "win32":
            trap = self.bin / "dotnet"
            trap.write_text("#!" + sys.executable + "\nfrom pathlib import Path\n"
                            + f"Path({str(self.canary)!r}).write_text('unexpected compiler execution')\nraise SystemExit(97)\n")
            trap.chmod(trap.stat().st_mode | stat.S_IXUSR)
        self.policy = json.loads((ROOT / "eng/required-checks.json").read_text())
        self.inventory = json.loads((ROOT / "eng/packages.json").read_text())

    def plan(self, *arguments):
        executable = os.environ.get("PWSH") or shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell is required for the actual producer entry point")
        env = os.environ.copy()
        env["PATH"] = str(self.bin) + os.pathsep + env.get("PATH", "")
        result = subprocess.run(
            [executable, "-NoProfile", "-File", str(ROOT / "eng/verify-packages.ps1"),
             "-Feed", str(self.work / "deliberately-absent-feed"),
             "-CandidateVersion", "3.0.0-contracttest.1", "-PlanOnly", *arguments],
            cwd=ROOT, env=env, capture_output=True, text=True, timeout=30)
        self.assertFalse(self.canary.exists(), "Read-only planning must not execute dotnet")
        return result

    def test_all_current_servicing_families_use_the_approved_sdk_and_current_reference(self):
        servicing = self.policy["toolchains"]["currentServicing"]
        for row in servicing["lanes"]:
            with self.subTest(framework=row["framework"]):
                result = self.plan("-CurrentServicing", "-Framework", row["framework"], "-SdkVersion", row["sdk"])
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                plan = json.loads(result.stdout)
                self.assertFalse(plan["executionProven"])
                self.assertEqual("currentServicing", plan["compilerContract"])
                baseline = [lane for lane in plan["lanes"] if lane["baselineOnly"]]
                candidate = [lane for lane in plan["lanes"] if not lane["baselineOnly"]]
                self.assertEqual([("net10.0", servicing["buildSdk"])],
                                 [(lane["framework"], lane["sdkVersion"]) for lane in baseline])
                self.assertEqual([(row["framework"], row["sdk"])],
                                 [(lane["framework"], lane["sdkVersion"]) for lane in candidate])

    def test_default_keeps_every_historical_compiler_host_and_reference(self):
        result = self.plan()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        plan = json.loads(result.stdout)
        self.assertEqual("pinnedCompatibility", plan["compilerContract"])
        self.assertFalse(plan["executionProven"])
        candidates = [lane for lane in plan["lanes"] if not lane["baselineOnly"]]
        self.assertEqual(self.inventory["compilerHosts"],
                         [{"framework": lane["framework"], "sdkVersion": lane["sdkVersion"]} for lane in candidates])
        self.assertEqual(["10.0.300"], [lane["sdkVersion"] for lane in plan["lanes"] if lane["baselineOnly"]])

    def test_current_servicing_rejects_an_unapproved_sdk(self):
        result = self.plan("-CurrentServicing", "-Framework", "net10.0", "-SdkVersion", "10.0.999")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("approved", result.stdout + result.stderr)

    def test_current_servicing_rejects_a_sdk_from_another_framework_family(self):
        result = self.plan("-CurrentServicing", "-Framework", "net8.0",
                           "-SdkVersion", self.policy["toolchains"]["currentServicing"]["buildSdk"])
        self.assertNotEqual(0, result.returncode)
        self.assertIn("approved", result.stdout + result.stderr)

    def test_historical_default_rejects_a_current_sdk_without_the_explicit_contract(self):
        row = self.policy["toolchains"]["currentServicing"]["lanes"][0]
        result = self.plan("-Framework", row["framework"], "-SdkVersion", row["sdk"])
        self.assertNotEqual(0, result.returncode)
        self.assertIn("approved", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()

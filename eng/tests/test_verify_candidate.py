"""Behavioral tests for eng/verify-candidate.ps1 and Resolve-OutputDirectory.

The script is executed with PowerShell against the real tiny feed and collector-produced lane evidence of
test_candidate_gate (its policy sits at <fixture>/eng/required-checks.json, so policy references resolve against the
fixture repository). The Run mode of real lanes executes builds and suites, so it is exercised by the E4c self-test and
the cumulative campaign; here Run is exercised only for the warning-ratchet lane, with a `dotnet` shim, to prove that
the ratchet's exit reaches the evidence. These tests prove the exit-code contract: success only for complete valid
evidence, 3 only for an explicitly allowed pending state, 2 for invalid arguments and failure for everything else.
"""

import json
import os
import shutil
import stat
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import candidate_gate as gate  # noqa: E402
from test_candidate_gate import COMMIT, VERSION, Fixture  # noqa: E402

SCRIPT = ROOT / "eng/verify-candidate.ps1"


def pwsh():
    executable = os.environ.get("PWSH") or shutil.which("pwsh")
    if not executable:
        raise RuntimeError("Required PowerShell: set PWSH or install pwsh")
    return executable


def current_host():
    import platform
    os_name = {"linux": "linux", "darwin": "osx", "win32": "win"}[sys.platform]
    arch = {"x86_64": "x64", "amd64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower(), platform.machine().lower())
    return {"os": os_name, "arch": arch}


def environment(**extra):
    env = {k: v for k, v in os.environ.items() if k not in ("GITHUB_ACTIONS", "GITHUB_RUN_ID")}
    env.update(extra)
    return env


class VerifyCandidateTests(unittest.TestCase):
    def setUp(self):
        self.f = Fixture(self, produce=False)
        # The native lane must be bound to a host that is never the current one, so it is genuinely not runnable here.
        native = self.f.lane("native/linux-x64/net10.0")
        if current_host() == gate.RID_HOSTS["linux-x64"]:
            native.update(rid="osx-arm64", host=dict(gate.RID_HOSTS["osx-arm64"]))
        for lane in self.f.policy["lanes"]:
            self.f.produce(lane["id"])
        self.policy = self.f.root / "eng/required-checks.json"
        self.policy.write_text(json.dumps(self.f.policy))

    def run_script(self, *extra, mode="Aggregate", evidence=None, env=None):
        arguments = [pwsh(), "-NoProfile", "-File", str(SCRIPT), "-Feed", str(self.f.feed), "-EvidenceDirectory", str(evidence or self.f.evidence),
                     "-Mode", mode, "-Policy", str(self.policy), "-CandidateVersion", VERSION, *extra]
        return subprocess.run(arguments, capture_output=True, text=True, cwd=ROOT, env=env or environment())

    def summary(self):
        return json.loads((self.f.evidence / "summary.json").read_text())

    def test_complete_valid_evidence_passes_in_aggregate_mode(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.summary()["overall"], "PASS")
        self.assertTrue(self.summary()["releaseEligible"])
        self.assertTrue((self.f.evidence / "summary.md").exists())
        self.assertTrue((self.f.evidence / "run-context.json").exists())

    def test_a_named_source_commit_is_enforced(self):
        self.assertEqual(self.run_script("-SourceCommit", COMMIT).returncode, 0)
        self.assertEqual(self.run_script("-SourceCommit", "b" * 40).returncode, 1)

    def test_the_package_manifest_anchor_is_enforced(self):
        self.assertEqual(self.run_script("-PackageManifestSha256", self.f.manifest_sha).returncode, 0)
        self.assertEqual(self.run_script("-PackageManifestSha256", "0" * 64).returncode, 1)

    def test_ci_lanes_only_is_a_labelled_scoped_verdict(self):
        for lane in self.f.policy["lanes"]:
            lane["ci"] = {"workflow": "ci.yml", "job": "x", "matrix": {}}
        self.f.policy["lanes"][-1]["ci"] = None
        self.policy.write_text(json.dumps(self.f.policy))
        result = self.run_script("-CiLanesOnly")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((self.summary()["overall"], self.summary()["releaseEligible"]), ("SCOPED-PASS", False))
        self.assertIn("NOT a candidate verdict", result.stdout)

    def test_a_missing_lane_fails_aggregate_mode(self):
        self.f.path("properties/net10.0").unlink()
        self.assertEqual(self.run_script().returncode, 1)

    def test_a_tampered_package_fails(self):
        (self.f.feed / f"Pkg.B.{VERSION}.nupkg").write_bytes(b"tampered")
        result = self.run_script()
        self.assertEqual(result.returncode, 1)
        self.assertIn("Pkg.B", result.stdout)

    def test_a_tampered_retained_raw_file_fails(self):
        trx = self.f.lane_dir("runtime/linux/net8.0") / "raw/Core.Tests.trx"
        trx.write_text(trx.read_text().replace('outcome="Passed"', 'outcome="Failed"', 1))
        self.assertEqual(self.run_script().returncode, 1)

    def test_a_skipped_mandatory_case_fails(self):
        self.f.mutate("runtime/linux/net8.0", lambda r: r["skips"].append({"suite": "Core.Tests", "name": "X", "reason": "env"}))
        self.assertEqual(self.run_script().returncode, 1)

    def test_a_missing_service_fails(self):
        self.f.mutate("provider/redis/net10.0", lambda r: r["services"][0].update(available=False))
        self.assertEqual(self.run_script().returncode, 1)

    def test_mismatched_candidate_hashes_fail(self):
        self.f.mutate("consumers/linux/net10.0", lambda r: r["candidate"]["packages"].update({"Pkg.B": "0" * 64}))
        self.assertEqual(self.run_script().returncode, 1)

    def native_id(self):
        return next(l["id"] for l in self.f.policy["lanes"] if l["kind"] == "native-aot")

    def test_host_mode_never_turns_unavailable_infrastructure_into_success(self):
        self.f.path(self.native_id()).unlink()
        strict = self.run_script(mode="Evaluate")
        self.assertEqual(strict.returncode, 1, strict.stdout + strict.stderr)
        self.assertIn("UNAVAILABLE", strict.stdout)
        self.assertEqual(self.summary()["overall"], "FAIL")

    def test_allow_pending_has_its_own_exit_code_and_summary_state(self):
        self.f.path(self.native_id()).unlink()
        for lane in self.f.policy["lanes"]:
            if lane.get("host") and lane["host"] != current_host() and lane["kind"] != "native-aot":
                self.f.path(lane["id"]).unlink()
        pending = self.run_script("-AllowPending", mode="Evaluate")
        self.assertEqual(pending.returncode, 3, pending.stdout + pending.stderr)
        self.assertEqual(self.summary()["overall"], "PENDING")
        self.assertIn(self.native_id(), self.summary()["pending"])
        self.assertFalse(self.summary()["releaseEligible"])

    def test_allow_pending_does_not_excuse_a_failing_lane(self):
        self.f.path(self.native_id()).unlink()
        self.f.mutate("properties/net10.0", lambda r: r.update(status="FAIL"))
        self.assertEqual(self.run_script("-AllowPending", mode="Evaluate").returncode, 1)

    def test_aggregate_rejects_allow_pending(self):
        self.assertEqual(self.run_script("-AllowPending").returncode, 2)

    def test_invalid_policy_exits_two(self):
        document = json.loads(self.policy.read_text())
        document["lanes"].append(document["lanes"][0])
        self.policy.write_text(json.dumps(document))
        self.assertEqual(self.run_script().returncode, 2)

    def test_github_actions_context_is_rejected_outside_github_actions(self):
        self.assertEqual(self.run_script("-Context", "github-actions", "-RunId", "42").returncode, 2)
        inside = environment(GITHUB_ACTIONS="true", GITHUB_RUN_ID="42")
        self.assertEqual(self.run_script("-Context", "github-actions", "-RunId", "41", env=inside).returncode, 2)
        self.assertEqual(self.run_script("-Context", "github-actions", "-RunId", "42", env=inside).returncode, 0)

    def collect(self, *extra):
        raw = self.f.runtime_raw()
        evidence = self.f.root / "collected"
        base = ["-Lane", "runtime/linux/net8.0", "-Raw", str(raw), "-SourceCommit", COMMIT]
        return self.run_script(*base, *extra, mode="Collect", evidence=evidence), evidence / "lanes/runtime__linux__net8.0/lane-result.json"

    def test_collect_writes_evidence_and_propagates_the_producer_exit(self):
        ok, written = self.collect("-ProducerExit", "0")
        self.assertEqual(ok.returncode, 0, ok.stdout + ok.stderr)
        self.assertEqual(json.loads(written.read_text())["status"], "PASS")
        self.assertEqual(json.loads(written.read_text())["source"], {"commit": COMMIT, "dirty": False})
        failed, written = self.collect("-ProducerExit", "5")
        self.assertEqual(failed.returncode, 1)
        self.assertEqual(json.loads(written.read_text())["execution"]["producerExit"], 5)

    def test_collect_requires_an_explicit_producer_exit(self):
        result, written = self.collect()
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertFalse(written.exists())

    def test_collect_requires_exactly_one_lane(self):
        result = self.run_script("-SourceCommit", COMMIT, "-ProducerExit", "0", mode="Collect", evidence=self.f.root / "c")
        self.assertEqual(result.returncode, 2)

    def test_run_rejects_an_unknown_lane_before_running_anything(self):
        result = self.run_script("-Lane", "no/such/lane", "-AllowDirtySource", mode="Run", evidence=self.f.root / "run")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("unknown lane", result.stdout)

    def test_feed_without_a_single_manifest_needs_an_explicit_version(self):
        shutil.copyfile(self.f.feed / f"package-manifest.{VERSION}.json", self.f.feed / "package-manifest.2.1.4-other.1.json")
        arguments = [pwsh(), "-NoProfile", "-File", str(SCRIPT), "-Feed", str(self.f.feed), "-EvidenceDirectory", str(self.f.evidence),
                     "-Mode", "Aggregate", "-Policy", str(self.policy)]
        result = subprocess.run(arguments, capture_output=True, text=True, cwd=ROOT, env=environment())
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exactly one package-manifest", result.stdout + result.stderr)


@unittest.skipIf(sys.platform == "win32", "the dotnet shim is a POSIX script")
class WarningRatchetRunTests(unittest.TestCase):
    """Run mode of the warning-ratchet lane with a `dotnet` shim: the ratchet's own exit must reach the evidence."""

    def setUp(self):
        self.f = Fixture(self, produce=False)
        self.policy = self.f.root / "eng/required-checks.json"
        self.policy.write_text(json.dumps(self.f.policy))
        self.bin = self.f.root / "bin"
        self.bin.mkdir()

    def shim(self, extra_warning, one_sdk=False):
        repo = str(ROOT)
        warnings = [f"{repo}/src/Lib/A.cs(10,5): warning CA1305: msg [{repo}/src/Lib/Lib.csproj::TargetFramework=net8.0]"]
        if extra_warning:
            warnings.append(f"{repo}/src/Lib/A.cs(30,5): warning CA1305: msg [{repo}/src/Lib/Lib.csproj::TargetFramework=net8.0]")
        built = [f"  {Path(p).stem} -> {repo}/{Path(p).parent}/bin/Release/net8.0/{Path(p).stem}.dll" for p in self.f.SOLUTION_PROJECTS]
        sdk_listing = "10.0.300 [/shim/sdk]" if one_sdk else "8.0.421 [/shim/sdk]\n10.0.300 [/shim/sdk]"
        script = self.bin / "dotnet"
        script.write_text("#!" + sys.executable + "\n" + f'''import sys
args = sys.argv[1:]
if args == ["--version"]:
    print("10.0.300")
elif args == ["--list-sdks"]:
    print({sdk_listing!r})
elif args == ["--list-runtimes"]:
    print("Microsoft.NETCore.App 8.0.27 [/shim]\\nMicrosoft.NETCore.App 10.0.8 [/shim]")
elif args and args[0] == "build":
    print({chr(10).join(warnings + built)!r})
else:
    sys.exit(9)
''')
        script.chmod(script.stat().st_mode | stat.S_IXUSR)

    def run_lane(self, *extra, mode="Run"):
        evidence = self.f.root / "run-evidence"
        env = environment(PATH=str(self.bin) + os.pathsep + os.environ.get("PATH", ""))
        result = subprocess.run([pwsh(), "-NoProfile", "-File", str(SCRIPT), "-Feed", str(self.f.feed), "-EvidenceDirectory", str(evidence),
                                 "-Mode", mode, "-Policy", str(self.policy), "-CandidateVersion", VERSION, "-Lane", "warnings/net10.0",
                                 "-AllowDirtySource", *extra], capture_output=True, text=True, cwd=ROOT, env=env)
        path = evidence / "lanes/warnings__net10.0/lane-result.json"
        return result, json.loads(path.read_text()) if path.exists() else None

    def test_a_clean_build_whose_warnings_regressed_is_a_failed_producer(self):
        self.shim(extra_warning=True)
        result, lane = self.run_lane()
        self.assertEqual(lane["execution"]["producerExit"], 1, result.stdout + result.stderr)
        self.assertEqual(lane["warnings"]["status"], "FAIL")
        self.assertEqual(lane["status"], "FAIL")
        self.assertNotEqual(result.returncode, 0)

    def test_an_unchanged_warning_set_is_a_successful_producer(self):
        self.shim(extra_warning=False)
        result, lane = self.run_lane()
        self.assertEqual(lane["execution"]["producerExit"], 0, result.stdout + result.stderr)
        self.assertEqual(lane["warnings"]["status"], "PASS", lane["warnings"])
        self.assertEqual(lane["laneToolchain"]["sdk"], "10.0.300")

    def test_a_single_sdk_is_retained_as_an_array_in_the_actual_runner(self):
        self.shim(extra_warning=False, one_sdk=True)
        result, lane = self.run_lane()
        self.assertIsNotNone(lane, result.stdout + result.stderr)
        self.assertEqual(lane["laneToolchain"]["sdks"], ["10.0.300"])

    def test_collector_failure_cannot_reuse_a_previous_passing_lane(self):
        self.shim(extra_warning=False)
        self.f.produce("warnings/net10.0")
        evidence = self.f.root / "run-evidence"
        shutil.copytree(self.f.evidence, evidence)
        previous = (evidence / "lanes/warnings__net10.0/lane-result.json").read_bytes()
        script = self.bin / "python3"
        script.write_text("#!" + sys.executable + "\n" +
                          "import os, sys\n" +
                          "if any(arg.endswith('lane_collect.py') for arg in sys.argv[1:]): sys.exit(42)\n" +
                          f"os.execv({sys.executable!r}, [{sys.executable!r}, *sys.argv[1:]])\n")
        script.chmod(script.stat().st_mode | stat.S_IXUSR)
        # The collector exits before producing any new result. The old result must move to history.
        result, lane = self.run_lane("-SourceCommit", COMMIT)
        self.assertIsNone(lane)
        retained = [p for p in (evidence / "history").rglob("lane-result.json") if p.read_bytes() == previous]
        self.assertEqual(len(retained), 1)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads((evidence / "summary.json").read_text())["overall"], "FAIL")

    def test_rerun_retains_the_previous_raw_work_directory(self):
        self.shim(extra_warning=False)
        self.run_lane()
        evidence = self.f.root / "run-evidence"
        old = evidence / "work/warnings__net10.0"
        (old / "previous-attempt.bin").write_bytes(b"previous failed producer bytes")
        self.run_lane()
        self.assertFalse((old / "previous-attempt.bin").exists())
        retained = list((evidence / "history").rglob("previous-attempt.bin"))
        self.assertEqual(len(retained), 1)
        self.assertEqual(retained[0].read_bytes(), b"previous failed producer bytes")

    def test_collect_failure_retires_a_previous_canonical_pass(self):
        self.shim(extra_warning=False)
        self.f.produce("warnings/net10.0")
        evidence = self.f.root / "run-evidence"
        shutil.copytree(self.f.evidence, evidence)
        previous = (evidence / "lanes/warnings__net10.0/lane-result.json").read_bytes()
        script = self.bin / "python3"
        script.write_text("#!" + sys.executable + "\nimport os,sys\n" +
                          "if any(arg.endswith('lane_collect.py') for arg in sys.argv[1:]): sys.exit(42)\n" +
                          f"os.execv({sys.executable!r}, [{sys.executable!r}, *sys.argv[1:]])\n")
        script.chmod(script.stat().st_mode | stat.S_IXUSR)
        result, lane = self.run_lane("-SourceCommit", COMMIT, "-ProducerExit", "0", mode="Collect")
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIsNone(lane)
        retained = [p for p in (evidence / "history").rglob("lane-result.json") if p.read_bytes() == previous]
        self.assertEqual(len(retained), 1)

    def test_early_failure_retires_old_summaries_before_the_new_context(self):
        self.shim(extra_warning=False)
        evidence = self.f.root / "run-evidence"
        evidence.mkdir()
        for suffix in ("json", "md"):
            (evidence / f"summary.{suffix}").write_text("old PASS must not survive")
        # Malformed policy JSON aborts before a lane can produce fresh evidence.
        self.policy.write_text("{")
        result, _ = self.run_lane()
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        for suffix in ("json", "md"):
            self.assertFalse((evidence / f"summary.{suffix}").exists())
            retained = list((evidence / "history/summaries" / suffix).glob("*"))
            self.assertEqual(len(retained), 1)
            self.assertEqual(retained[0].read_text(), "old PASS must not survive")


class ResolveOutputDirectoryTests(unittest.TestCase):
    def resolve(self, path):
        script = (f"Import-Module '{ROOT / 'eng/package-inventory.psm1'}' -DisableNameChecking; "
                  f"Resolve-OutputDirectory '/repo/root' '{path}'")
        return subprocess.run([pwsh(), "-NoProfile", "-Command", script], capture_output=True, text=True).stdout.strip()

    @unittest.skipIf(sys.platform == "win32", "POSIX path semantics")
    def test_rooted_paths_are_not_appended_to_the_repository(self):
        self.assertEqual(self.resolve("/var/tmp/out"), "/var/tmp/out")

    @unittest.skipIf(sys.platform == "win32", "POSIX path semantics")
    def test_relative_paths_are_resolved_against_the_repository(self):
        self.assertEqual(self.resolve("artifacts/docs"), "/repo/root/artifacts/docs")
        self.assertEqual(self.resolve("../sibling"), "/repo/sibling")

    def test_every_output_directory_parameter_uses_the_helper(self):
        for script in ("verify-docs.ps1", "verify-candidate.ps1"):
            text = (ROOT / "eng" / script).read_text()
            self.assertIn("Resolve-OutputDirectory $repo", text, script)
            self.assertNotIn("Join-Path $repo $OutputDirectory", text, script)


if __name__ == "__main__":
    unittest.main()

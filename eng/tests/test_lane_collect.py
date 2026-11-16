"""Behavioral tests for the lane collector (eng/lane_collect.py) and its hand-off to the candidate gate.

The raw inputs are real TRX documents, receipts and logs in the shape the repository scripts write (the fixtures of
test_candidate_gate). Each test collects a lane with the real collector and, where the verdict matters, evaluates it
with eng/candidate_gate.py, so a collector that transcribes the wrong thing is caught by the verdict.
"""

import json
import socket
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import lane_collect as collect  # noqa: E402
from test_candidate_gate import COMMIT, REDIS, VERSION, Base, FakeDocker, Fixture, real_trx  # noqa: E402


class RetentionTests(Base):
    def test_raw_receipts_are_copied_and_hashed_and_the_evaluator_rechecks_them(self):
        f = Fixture(self, produce=False)
        f.produce("runtime/linux/net8.0")
        result = f.read("runtime/linux/net8.0")
        names = {a["path"] for a in result["artifacts"]}
        self.assertEqual(names, {"raw/Core.Tests.trx", "raw/samples-summary.json", "raw/lane-toolchain.json"})
        self.passes(f, "runtime/linux/net8.0")
        (f.lane_dir("runtime/linux/net8.0") / "raw/samples-summary.json").write_text("{}")
        self.fails(f, "runtime/linux/net8.0", "hash differs")

    def test_only_raw_files_of_the_lane_kind_are_retained(self):
        f = Fixture(self, produce=False)
        raw = f.runtime_raw()
        (raw / "coverage-gate.json").write_text("{}")
        (raw / "build.log").write_text("command=dotnet build\nexit=0\n")
        f.collect("runtime/linux/net8.0", raw)
        self.assertNotIn("raw/coverage-gate.json", {a["path"] for a in f.read("runtime/linux/net8.0")["artifacts"]})
        self.passes(f, "runtime/linux/net8.0")

    def test_same_named_receipts_from_different_directories_both_survive_and_count_separately(self):
        f = Fixture(self, produce=False)
        raw = f.runtime_raw()
        (raw / "one").mkdir()
        (raw / "one/Core.Tests.trx").write_text(real_trx("Core.Tests", "net8.0", passed=6))
        result = f.collect("runtime/linux/net8.0", raw)
        self.assertEqual(sorted(a["path"] for a in result["artifacts"] if a["path"].endswith(".trx")), ["raw/Core.Tests.2.trx", "raw/Core.Tests.trx"])
        self.assertEqual(sorted(r["total"] for r in result["counts"]), [6, 11])
        self.passes(f, "runtime/linux/net8.0")

    def test_a_raw_file_too_large_to_retain_fails_the_lane_instead_of_being_dropped(self):
        f = Fixture(self, produce=False)
        with mock.patch.object(collect, "MAX_COPY_BYTES", 64):
            result = f.collect("runtime/linux/net8.0", f.runtime_raw())
        self.assertEqual(result["status"], "FAIL")
        self.assertTrue(any("cannot be retained" in x for x in result["limitations"]), result["limitations"])
        self.fails(f, "runtime/linux/net8.0")

    def test_missing_trx_files_produce_a_failing_record_rather_than_a_missing_lane(self):
        f = Fixture(self, produce=False)
        raw = f.runtime_raw()
        (raw / "Core.Tests.trx").unlink()
        result = f.collect("runtime/linux/net8.0", raw)
        self.assertEqual(result["status"], "FAIL")
        self.assertTrue(any("no TRX" in x for x in result["limitations"]))
        self.assertEqual(f.state("runtime/linux/net8.0")[0]["state"], "FAIL")


class RecordTests(Base):
    def test_candidate_binding_provenance_and_producer_exit_are_recorded(self):
        f = Fixture(self, produce=False)
        result = f.collect("runtime/linux/net8.0", f.runtime_raw())
        self.assertEqual(result["candidate"]["packageManifestSha256"], f.manifest_sha)
        self.assertEqual(result["candidate"]["packages"], f.packages)
        self.assertEqual(result["source"], {"commit": COMMIT, "dirty": False})
        self.assertEqual(result["execution"]["producerExit"], 0)
        self.assertEqual(result["status"], "PASS", result["limitations"])

    def test_a_non_zero_producer_exit_is_recorded_as_failure(self):
        f = Fixture(self, produce=False)
        result = f.collect("runtime/linux/net8.0", f.runtime_raw(), producer_exit=3)
        self.assertEqual((result["status"], result["execution"]["producerExit"]), ("FAIL", 3))
        self.fails(f, "runtime/linux/net8.0", "exited 3")

    def test_a_dirty_source_is_recorded_and_fails(self):
        f = Fixture(self, produce=False)
        result = f.collect("runtime/linux/net8.0", f.runtime_raw(), dirty=True)
        self.assertEqual((result["status"], result["source"]["dirty"]), ("FAIL", True))

    def test_the_collector_status_reflects_the_derived_facts(self):
        f = Fixture(self, produce=False)
        self.assertEqual(f.collect("runtime/linux/net8.0", f.runtime_raw(failed=1))["status"], "FAIL")
        self.assertEqual(f.collect("warnings/net10.0", f.warning_raw(extra_warning=True))["status"], "FAIL")
        self.assertEqual(f.collect("coverage/net10.0", f.coverage_raw(hits={"src/Lib/Hot.cs": [(1, 0)]}))["status"], "FAIL")

    def test_an_inconsistent_feed_makes_the_lane_fail(self):
        f = Fixture(self, produce=False)
        raw = f.runtime_raw()
        (f.feed / f"Pkg.B.{VERSION}.nupkg").write_bytes(b"changed")
        self.assertEqual(f.collect("runtime/linux/net8.0", raw)["status"], "FAIL")


class ServiceProbeTests(Base):
    def test_auto_resolves_the_image_from_the_running_container_with_untruncated_ids(self):
        f = Fixture(self, produce=False)
        record = collect.probe_service(f.policy, "redis", "auto", f.environment, f.docker)
        self.assertEqual((record["available"], record["resolution"], record["image"]), (True, "docker-inspect", REDIS))
        self.assertEqual(record["repoDigests"], ["redis@" + REDIS.split("@")[1]])
        self.assertTrue(any(call[0] == "ps" and "--no-trunc" in call for call in f.docker.calls))

    def test_two_containers_on_the_port_or_no_docker_leave_the_image_unresolved(self):
        f = Fixture(self, produce=False)
        crowded = FakeDocker(f.port)
        original = crowded.__call__

        def two(arguments):
            reply = original(arguments)
            if arguments[0] == "ps":
                reply.stdout += reply.stdout.replace("c0ffee", "beef00")
            return reply
        record = collect.probe_service(f.policy, "redis", "auto", f.environment, two)
        self.assertEqual((record["resolution"], record["image"]), ("unresolved", None))

        def missing(arguments):
            raise FileNotFoundError("docker")
        record = collect.probe_service(f.policy, "redis", "auto", f.environment, missing)
        self.assertEqual(record["resolution"], "unresolved")
        self.assertIn("not usable", record["resolutionError"])

    def test_an_unset_environment_variable_means_the_service_is_unavailable(self):
        f = Fixture(self, produce=False)
        self.assertFalse(collect.probe_service(f.policy, "redis", "auto", {}, f.docker)["available"])

    def test_a_closed_port_means_the_service_is_unavailable(self):
        f = Fixture(self, produce=False)
        probe = socket.socket()
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
        probe.close()
        self.assertFalse(collect.probe_service(f.policy, "redis", "auto", {"MERIDIAN_TEST_REDIS": f"127.0.0.1:{port}"}, f.docker)["available"])


class CommandLineTests(Base):
    def cli(self, f, *extra):
        policy = f.root / "eng/required-checks.json"
        policy.write_text(json.dumps(f.policy))
        raw = f.runtime_raw()
        base = [sys.executable, "-B", str(ROOT / "eng/lane_collect.py"), "--policy", str(policy), "--lane-id", "runtime/linux/net8.0",
                "--evidence", str(f.evidence), "--feed", str(f.feed), "--source-commit", COMMIT,
                "--context", "local", "--host-os", "linux", "--host-arch", "x64", "--raw", str(raw)]
        return subprocess.run(base + list(extra), capture_output=True, text=True)

    def test_cli_writes_the_lane_judged_against_the_policy_repository_and_returns_its_status(self):
        f = Fixture(self, produce=False)
        ok = self.cli(f, "--producer-exit", "0")
        self.assertEqual(ok.returncode, 0, ok.stdout + ok.stderr)
        self.passes(f, "runtime/linux/net8.0")
        failed = self.cli(f, "--producer-exit", "2")
        self.assertEqual(failed.returncode, 1)
        self.assertEqual(f.read("runtime/linux/net8.0")["status"], "FAIL")

    def test_the_producer_exit_is_required(self):
        f = Fixture(self, produce=False)
        self.assertEqual(self.cli(f).returncode, 2)

    def test_source_dirty_is_recorded(self):
        f = Fixture(self, produce=False)
        self.assertEqual(self.cli(f, "--producer-exit", "0", "--source-dirty").returncode, 1)
        self.assertTrue(f.read("runtime/linux/net8.0")["source"]["dirty"])

    def test_unknown_lane_is_a_configuration_error(self):
        f = Fixture(self, produce=False)
        policy = f.root / "eng/required-checks.json"
        policy.write_text(json.dumps(f.policy))
        result = subprocess.run([sys.executable, "-B", str(ROOT / "eng/lane_collect.py"), "--policy", str(policy), "--lane-id", "nope",
                                 "--evidence", str(f.evidence), "--feed", str(f.feed), "--source-commit", COMMIT, "--producer-exit", "0",
                                 "--context", "local", "--host-os", "linux", "--host-arch", "x64"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()

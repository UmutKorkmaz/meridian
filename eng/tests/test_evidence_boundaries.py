"""Counterexamples from Codex's independent E4c review.

These are evidence-parser fixtures, not claims that their synthetic programs ran.
The control is collected by the real collector before one boundary is changed.
"""

import copy
import hashlib
import json
import struct
import types
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest import mock

from test_candidate_gate import Fixture, TRX_NS


class EvidenceBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.f = Fixture(self, produce=False)

    def verdict(self, lane, raw, **kwargs):
        self.f.collect(lane, raw, **kwargs)
        return self.f.state(lane)[0]

    def passing(self, lane, raw):
        result = self.verdict(lane, raw)
        self.assertEqual("PASS", result["state"], result)

    def rejected(self, lane, raw):
        result = self.verdict(lane, raw)
        self.assertEqual("FAIL", result["state"], result)

    def consumer_control(self):
        lane = "consumers/linux/net10.0"
        raw = self.f.consumer_raw(lane)
        path = raw / "consumer-results.json"
        rows = json.loads(path.read_text())
        for row in rows:
            row["runtime"] = "HOST runtime=10.0.8"
        path.write_text(json.dumps(rows))
        self.passing(lane, raw)
        return lane, raw, path, rows

    def test_consumer_from_another_framework_cannot_satisfy_the_lane(self):
        lane, raw, path, rows = self.consumer_control()
        rows[0]["framework"] = "net8.0"
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_consumer_must_record_the_executed_runtime_family(self):
        lane, raw, path, rows = self.consumer_control()
        rows[0]["runtime"] = "HOST runtime=8.0.27"
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_missing_consumer_runtime_is_not_execution_proof(self):
        lane, raw, path, rows = self.consumer_control()
        del rows[0]["runtime"]
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_duplicate_consumer_variant_is_rejected(self):
        lane, raw, path, rows = self.consumer_control()
        rows.append(copy.deepcopy(rows[0]))
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_undeclared_consumer_variant_is_rejected(self):
        lane, raw, path, rows = self.consumer_control()
        rows.append(dict(rows[0], variant="unreviewed-variant"))
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_consumer_binary_bytes_must_match_the_recorded_hash(self):
        lane, raw, _, rows = self.consumer_control()
        (raw / rows[0]["binaryArtifact"]).write_bytes(b"different consumer assembly")
        self.rejected(lane, raw)

    def test_missing_consumer_binary_is_not_a_hash_proof(self):
        lane, raw, _, rows = self.consumer_control()
        (raw / rows[0]["binaryArtifact"]).unlink()
        self.rejected(lane, raw)

    def test_consumer_package_closure_must_match_its_inventory(self):
        lane, raw, path, rows = self.consumer_control()
        rows[0]["packageIds"] = []
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_consumer_source_hashes_must_match_the_accepted_fixture(self):
        lane, raw, path, rows = self.consumer_control()
        rows[0]["sourceFiles"][0]["sha256"] = "0" * 64
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_consumer_deps_must_prove_the_actual_target_framework(self):
        lane, raw, _, rows = self.consumer_control()
        path = raw / rows[0]["depsArtifact"]
        deps = json.loads(path.read_text())
        deps["runtimeTarget"]["name"] = ".NETCoreApp,Version=v8.0"
        path.write_text(json.dumps(deps))
        self.rejected(lane, raw)

    def test_consumer_deps_must_bind_the_restored_candidate_archive(self):
        lane, raw, _, rows = self.consumer_control()
        path = raw / rows[0]["depsArtifact"]
        deps = json.loads(path.read_text())
        next(iter(deps["libraries"].values()))["sha512"] = "sha512-wrong-archive"
        path.write_text(json.dumps(deps))
        self.rejected(lane, raw)

    def test_malformed_consumer_runtime_target_retains_structured_failure(self):
        lane, raw, _, rows = self.consumer_control()
        path = raw / rows[0]["depsArtifact"]
        deps = json.loads(path.read_text())
        deps["runtimeTarget"] = ["unexpected"]
        path.write_text(json.dumps(deps))
        self.rejected(lane, raw)

    def test_malformed_consumer_project_retains_structured_failure(self):
        lane, raw, _, rows = self.consumer_control()
        path = raw / rows[0]["assetsArtifact"]
        assets = json.loads(path.read_text())
        assets["project"] = ["unexpected"]
        path.write_text(json.dumps(assets))
        self.rejected(lane, raw)

    def test_consumer_assets_must_bind_every_restored_inventory_package(self):
        lane, raw, _, rows = self.consumer_control()
        path = raw / rows[0]["assetsArtifact"]
        assets = json.loads(path.read_text())
        next(iter(assets["libraries"].values()))["sha512"] = "sha512-unrelated-restore"
        path.write_text(json.dumps(assets))
        self.rejected(lane, raw)

    def test_consumer_cannot_replace_an_inventory_package_with_a_source_project(self):
        lane, raw, _, rows = self.consumer_control()
        path = raw / rows[0]["depsArtifact"]
        deps = json.loads(path.read_text())
        next(iter(deps["libraries"].values()))["type"] = "project"
        path.write_text(json.dumps(deps))
        self.rejected(lane, raw)

    def test_candidate_common_behavior_must_equal_the_baseline(self):
        lane, raw, path, rows = self.consumer_control()
        rows[0]["commonOutput"] = ["COMMON behavior=changed"]
        path.write_text(json.dumps(rows))
        self.rejected(lane, raw)

    def test_baseline_archive_bytes_must_match_the_retained_hash(self):
        lane, raw, _, _ = self.consumer_control()
        (raw / "Meridian.Mapping.2.1.3.baseline-nupkg").write_bytes(b"changed baseline archive")
        self.rejected(lane, raw)

    def native_control(self):
        lane = "native/linux-x64/net10.0"
        raw = self.f.native_raw(lane)
        # Real format-header bytes for parser tests; these files are never executed.
        header = bytearray(64)
        header[:6] = b"\x7fELF\x02\x01"
        struct.pack_into("<H", header, 18, 62)
        receipts = sorted(raw.rglob("native-result.json"))
        for path in receipts:
            receipt = json.loads(path.read_text())
            binary = Path(receipt["file"])
            binary.parent.mkdir(parents=True, exist_ok=True)
            binary.write_bytes(header + path.parent.name.encode())
            (path.parent / receipt["retainedFile"]).write_bytes(binary.read_bytes())
            receipt["sha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
            path.write_text(json.dumps(receipt))
        self.passing(lane, raw)
        return lane, raw, receipts

    def test_markerless_failed_native_execution_is_not_ignored(self):
        lane, raw, _ = self.native_control()
        path = raw / "crashed-run/execute.log"
        path.parent.mkdir()
        path.write_text("command=/owned/crashed-program\nexit=1\ncrashed before semantic markers\n")
        self.rejected(lane, raw)

    def test_mapping_native_package_receipt_requires_an_object(self):
        lane, raw, _ = self.native_control()
        path = next(raw.rglob("package-hash.json"))
        for value in ([], None, "unexpected", 42):
            with self.subTest(value=value):
                path.write_text(json.dumps(value))
                self.rejected(lane, raw)

    def test_unknown_native_receipt_role_is_not_ignored(self):
        lane, raw, receipts = self.native_control()
        path = raw / "unknown-run/native-result.json"
        path.parent.mkdir()
        path.write_text(json.dumps(dict(json.loads(receipts[0].read_text()), mode="unreviewed")))
        self.rejected(lane, raw)

    def test_native_executable_bytes_must_match_the_receipt(self):
        lane, raw, receipts = self.native_control()
        receipt = json.loads(receipts[0].read_text())
        (receipts[0].parent / receipt["retainedFile"]).write_bytes(b"different bytes after execution")
        self.rejected(lane, raw)

    def test_missing_native_executable_cannot_be_replaced_by_a_hash_claim(self):
        lane, raw, receipts = self.native_control()
        receipt = json.loads(receipts[0].read_text())
        (receipts[0].parent / receipt["retainedFile"]).unlink()
        self.rejected(lane, raw)

    def test_native_format_is_derived_from_bytes(self):
        lane, raw, receipts = self.native_control()
        path = next(p for p in receipts if json.loads(p.read_text()).get("mode") == "combined")
        receipt = json.loads(path.read_text())
        binary = path.parent / receipt["retainedFile"]
        binary.write_bytes(b"MZ" + bytes(62))
        receipt["sha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
        path.write_text(json.dumps(receipt))
        self.rejected(lane, raw)

    def test_native_execution_command_must_name_its_executable(self):
        lane, raw, _ = self.native_control()
        path = next(p for p in raw.rglob("execute.log") if "--require-native" in p.read_text())
        lines = path.read_text().splitlines()
        lines[0] = "command=/unrelated/program --require-native --expect-os linux --expect-arch x64"
        path.write_text("\n".join(lines) + "\n")
        self.rejected(lane, raw)

    def trx_control(self):
        lane = "runtime/linux/net8.0"
        raw = self.f.runtime_raw(lane)
        self.passing(lane, raw)
        path = raw / "Core.Tests.trx"
        return lane, raw, path, ET.parse(path)

    def test_repeating_one_trx_execution_does_not_inflate_the_suite(self):
        lane, raw, path, tree = self.trx_control()
        results = tree.getroot().find(f"{{{TRX_NS}}}Results")
        first = copy.deepcopy(results[0])
        count = len(results)
        results.clear()
        for _ in range(count):
            results.append(copy.deepcopy(first))
        tree.write(path, encoding="utf-8", xml_declaration=True)
        self.rejected(lane, raw)

    def test_trx_result_requires_an_execution_identity(self):
        lane, raw, path, tree = self.trx_control()
        tree.getroot().find(f"{{{TRX_NS}}}Results")[0].attrib.pop("executionId")
        tree.write(path, encoding="utf-8", xml_declaration=True)
        self.rejected(lane, raw)

    def test_trx_result_must_link_to_its_definition_execution(self):
        lane, raw, path, tree = self.trx_control()
        definition = tree.getroot().find(f"{{{TRX_NS}}}TestDefinitions")[0]
        definition.find(f"{{{TRX_NS}}}Execution").set("id", "other-execution")
        tree.write(path, encoding="utf-8", xml_declaration=True)
        self.rejected(lane, raw)

    def test_duplicate_trx_definition_identity_is_rejected(self):
        lane, raw, path, tree = self.trx_control()
        definitions = tree.getroot().find(f"{{{TRX_NS}}}TestDefinitions")
        definitions.append(copy.deepcopy(definitions[0]))
        tree.write(path, encoding="utf-8", xml_declaration=True)
        self.rejected(lane, raw)

    def test_distinct_theory_executions_can_share_a_display_name(self):
        lane, raw, path, tree = self.trx_control()
        for result in tree.getroot().find(f"{{{TRX_NS}}}Results"):
            result.set("testName", "Theory(case)")
        tree.write(path, encoding="utf-8", xml_declaration=True)
        self.passing(lane, raw)

    def test_remote_provider_cannot_borrow_an_unrelated_local_image(self):
        lane = "provider/redis/net10.0"
        raw = self.f.provider_raw(lane)
        self.passing(lane, raw)
        self.f.environment["MERIDIAN_TEST_REDIS"] = f"remote.example.test:{self.f.port},abortConnect=true"
        with mock.patch("lane_collect.socket.create_connection"):
            self.rejected(lane, raw)

    def test_remote_docker_daemon_cannot_attest_a_local_provider(self):
        lane = "provider/redis/net10.0"
        raw = self.f.provider_raw(lane)
        self.passing(lane, raw)
        original = self.f.docker

        def remote_docker(arguments):
            if arguments[:2] == ["context", "show"]:
                return types.SimpleNamespace(returncode=0, stdout="remote\n", stderr="")
            if arguments[:2] == ["context", "inspect"]:
                return types.SimpleNamespace(returncode=0, stdout='"tcp://remote.example.test:2376"\n', stderr="")
            return original(arguments)

        self.f.docker = remote_docker
        self.rejected(lane, raw)

    def test_successful_rerun_preserves_the_failed_attempt_and_raw_bytes(self):
        lane = "runtime/linux/net8.0"
        raw = self.f.runtime_raw(lane)
        self.verdict(lane, raw, producer_exit=42)
        previous = self.f.path(lane).read_bytes()
        failed_trx = (self.f.lane_dir(lane) / "raw/Core.Tests.trx").read_bytes()
        self.passing(lane, raw)
        retained = [p for p in self.f.evidence.rglob("lane-result.json") if p.read_bytes() == previous]
        self.assertEqual(1, len(retained), "The earlier failed attempt must remain addressable.")
        self.assertEqual(failed_trx, (retained[0].parent / "raw/Core.Tests.trx").read_bytes())


if __name__ == "__main__":
    unittest.main()

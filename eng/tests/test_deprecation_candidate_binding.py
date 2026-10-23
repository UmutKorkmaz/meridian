"""Immutable lifecycle-leaf bindings, with fictional input bytes and actual CLI/wrapper execution."""
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from eng.tests import test_lifecycle_integration as integration


class CandidateBindingTests(unittest.TestCase):
    fixture = integration.LifecycleGateIntegrationTests.fixture
    def invoke(self, f, raw, candidate, output):
        return subprocess.run([sys.executable, "-B", str(integration.ROOT / "eng/deprecation_policy.py"), "validate",
            "--repo", str(f.root), "--ledger", str(f.root / "docs/deprecations.md"), "--candidate", str(candidate),
            "--consumer-results", str(raw / "consumer-results.json"), "--output", str(output)],
            capture_output=True, text=True, timeout=30)

    def test_cli_binds_immutable_manifest_and_one_complete_raw_semantic_lane(self):
        f, raw = self.fixture()
        candidate = f.feed / f"package-manifest.{integration.fixtures.VERSION}.json"
        output = f.root / "lifecycle.json"
        process = self.invoke(f, raw, candidate, output)
        self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
        report = json.loads(output.read_text())
        self.assertEqual(report["candidateKind"], "package-manifest")
        self.assertEqual(report["candidateSha256"], hashlib.sha256(candidate.read_bytes()).hexdigest())
        self.assertEqual(report["consumerResultsSha256"], hashlib.sha256((raw / "consumer-results.json").read_bytes()).hexdigest())
        self.assertEqual(report["sourceInvariants"]["status"], "PASS")
        self.assertEqual(report["consumerEvidence"]["status"], "matched")
        self.assertFalse(report["consumerEvidence"]["provenanceVerified"])
        self.assertEqual(len(report["entries"]), 4)

    def test_cli_rejects_enclosing_or_malformed_manifest_and_foreign_consumer_version(self):
        f, raw = self.fixture()
        manifest = f.feed / f"package-manifest.{integration.fixtures.VERSION}.json"
        original = json.loads(manifest.read_text())
        controls = [{}, dict(original, sourceDirty=None), dict(original, sourceDirty=0),
                    dict(original, candidateVersion="3.9.0"), dict(original, packages=[]),
                    dict(original, sourceCommit="not-a-commit"), dict(original, manifestSha256="not-a-hash")]
        duplicate = copy.deepcopy(original)
        duplicate["packages"].append(copy.deepcopy(duplicate["packages"][0]))
        controls.append(duplicate)
        for index, record in enumerate(controls):
            manifest.write_text(json.dumps(record))
            output = f.root / f"invalid-{index}.json"
            process = self.invoke(f, raw, manifest, output)
            with self.subTest(index=index):
                self.assertEqual(process.returncode, 2, process.stdout + process.stderr)
                self.assertFalse(output.exists())

    def test_actual_bound_powershell_wrapper_prepares_a_controlled_bundle(self):
        from eng.tests import test_release_candidate as release
        self.assertTrue(shutil.which("pwsh"))
        with tempfile.TemporaryDirectory(prefix="meridian-e7-bound-wrapper-") as directory:
            world = release.World(Path(directory))
            for name in ("verify-deprecations.ps1", "deprecation_policy.py"):
                shutil.copyfile(integration.ROOT / "eng" / name, world.repo / "eng" / name)
            code, report = world.prepare(deprecation_runner=None)
            self.assertEqual(code, 0, report)
            row = release.row(report, "e7-lifecycle")
            self.assertTrue(row["details"]["candidateBound"])
            bundle = world.out / "candidate"
            document = json.loads((bundle / "evidence/e7-deprecation-validation.json").read_text())
            self.assertEqual(document["consumerEvidence"]["status"], "matched")
            log = (bundle / "evidence/e7-deprecation-validation.log").read_text()
            self.assertIn("-Candidate ", log)
            self.assertIn("-ConsumerResults ", log)
            self.assertIn("exit=0", log)

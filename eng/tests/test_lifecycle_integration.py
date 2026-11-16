"""Pure E4/E5/E7 integration over explicitly fictional, never-executed byte fixtures."""
import base64
import hashlib
import json
from pathlib import Path
import shutil
import unittest

from eng import deprecation_policy as lifecycle
from eng.tests import test_candidate_gate as fixtures

ROOT = Path(__file__).resolve().parents[2]
CONSUMER = "Meridian.DeprecationConsumer"


def install_retained_source(repo):
    paths = ["docs/deprecations.md", "src/Meridian.Mapping.Generators/Diagnostics/MapperDiagnostics.cs"] + [path for path, _ in lifecycle.SOURCE_CONTRACTS]
    paths += consumer_contract([])["sourceFiles"]
    for relative in paths:
        target = repo / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / relative, target)


def consumer_contract(package_ids):
    contract = next(row for row in json.loads((ROOT / "eng/packages.json").read_text())["consumers"] if row["id"] == CONSUMER)
    return dict(contract, packageIds=sorted(package_ids), baselineComparison=False)


def semantic_rows(version):
    ledger = lifecycle.extract_ledger((ROOT / "docs/deprecations.md").read_text())
    names = {name for entry in ledger["entries"] for name in entry["migration"]["fixtures"]}
    names.add("retained-dynamic-registration")
    common = [f"COMMON fixture={name} result=fixture-only" for name in sorted(names)]
    return [{"consumer": CONSUMER, "variant": variant, "framework": "net10.0", "sdkVersion": "10.0.300",
             "compilerVersion": "5.0.0", "runtime": "HOST runtime=10.0.8", "packageVersion": version,
             "exit": 0, "commonOutput": list(common)} for variant in ("legacy", "migrated")]


def write_consumer_raw(raw, repo, feed, version, package_ids):
    """Shape-correct dependency/source receipts; these managed-byte fixtures never execute."""
    raw.mkdir(parents=True, exist_ok=True)
    contract = consumer_contract(package_ids)
    rows = semantic_rows(version)
    for row in rows:
        stem = f"{CONSUMER}-{row['variant']}-{row['framework']}"
        binary = ("never-executed-lifecycle-fixture:" + stem).encode()
        row.update(packageIds=sorted(package_ids), baselineComparison=False, binaryArtifact=stem + ".consumer-binary",
                   binarySha256=hashlib.sha256(binary).hexdigest(), depsArtifact=stem + ".consumer-deps.json",
                   assetsArtifact=stem + ".consumer-assets.json",
                   sourceFiles=[{"source": path, "file": Path(path).name,
                                 "sha256": hashlib.sha256((repo / path).read_bytes()).hexdigest()} for path in contract["sourceFiles"]])
        (raw / row["binaryArtifact"]).write_bytes(binary)
        libraries = {f"{name}/{version}": {"type": "package", "sha512": "sha512-" + base64.b64encode(
            hashlib.sha512((feed / f"{name}.{version}.nupkg").read_bytes()).digest()).decode()} for name in package_ids}
        (raw / row["depsArtifact"]).write_text(json.dumps({"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0"}, "libraries": libraries}))
        (raw / row["assetsArtifact"]).write_text(json.dumps({"project": {"frameworks": {"net10.0": {}}}, "libraries": libraries}))
    (raw / "consumer-selection.json").write_text(json.dumps({"scope": "all", "selectedIds": [CONSUMER], "omittedIds": []}))
    (raw / "consumer-results.json").write_text(json.dumps(rows))
    return rows


class LifecycleGateIntegrationTests(unittest.TestCase):
    def fixture(self):
        f = fixtures.Fixture(self, produce=False)
        install_retained_source(f.root)
        inventory = json.loads(f.inventory_path.read_text())
        inventory["consumers"].append(consumer_contract(["Meridian.Mapping"]))
        f.inventory_path.write_text(json.dumps(inventory))
        f.write_manifest(f.records)
        rows = [{"consumer": "A.Consumer", "variant": "legacy", "framework": "net10.0", "sdkVersion": fixtures.BUILD_SDK,
                 "compilerVersion": "5.0.0", "packageVersion": fixtures.VERSION, "exit": 0}] + semantic_rows(fixtures.VERSION)
        raw = f.consumer_raw(rows=rows, selection={"scope": "partial", "selectedIds": ["A.Consumer", CONSUMER], "omittedIds": ["Redis.Consumer"]})
        return f, raw

    def verdict(self, f, raw):
        lane_id = "consumers/linux/net10.0"
        f.collect(lane_id, raw)
        return f.evaluate(only=[lane_id])

    def test_honest_all_nine_old_new_contracts_pass_with_separate_raw_provenance(self):
        f, raw = self.fixture()
        summary = self.verdict(f, raw)
        self.assertEqual(summary["overall"], "SCOPED-PASS", summary["failures"])
        self.assertFalse(summary["releaseEligible"])

    def test_matching_rows_without_all_nine_fixtures_cannot_pass_the_real_gate(self):
        f, raw = self.fixture()
        path = raw / "consumer-results.json"
        rows = json.loads(path.read_text())
        for row in rows:
            if row["consumer"] == CONSUMER:
                row["commonOutput"] = ["COMMON fixture=alias-action result=fixture-only"]
        path.write_text(json.dumps(rows))
        self.assertEqual(self.verdict(f, raw)["overall"], "FAIL")

    def test_old_new_semantic_difference_cannot_pass_the_real_gate(self):
        f, raw = self.fixture()
        path = raw / "consumer-results.json"
        rows = json.loads(path.read_text())
        next(row for row in rows if row["consumer"] == CONSUMER and row["variant"] == "migrated")["commonOutput"][0] += " changed"
        path.write_text(json.dumps(rows))
        self.assertEqual(self.verdict(f, raw)["overall"], "FAIL")

    def test_changed_retained_default_cannot_pass_even_when_consumer_rows_match(self):
        f, raw = self.fixture()
        path = f.root / "src/Meridian.Mediator/Behaviors/CachingBehavior.cs"
        path.write_text(path.read_text().replace("UseRequestTypeNamespacedKeys { get; init; } = true;", "UseRequestTypeNamespacedKeys { get; init; } = false;"))
        self.assertEqual(self.verdict(f, raw)["overall"], "FAIL")

    def global_failure(self, *, scoped):
        f, _ = self.fixture()
        lane_id = "runtime/linux/net8.0"
        lane = f.lane(lane_id)
        if not scoped:
            lane["kind"] = "generic"
            f.policy["lanes"] = [lane]
        raw = f.runtime_raw(lane_id)
        f.collect(lane_id, raw)
        path = f.root / "src/Meridian.Mediator/Behaviors/CachingBehavior.cs"
        path.write_text(path.read_text().replace("UseRequestTypeNamespacedKeys { get; init; } = true;", "UseRequestTypeNamespacedKeys { get; init; } = false;"))
        summary = f.evaluate(only=[lane_id]) if scoped else f.evaluate()
        self.assertTrue(all(row["state"] == "PASS" for row in summary["lanes"]), summary["lanes"])
        self.assertTrue(any("deprecation" in reason for reason in summary["failures"]))
        self.assertEqual(summary["overall"], "FAIL")
        self.assertEqual(summary["exitCode"], 1)
        self.assertFalse(summary["releaseEligible"])

    def test_global_lifecycle_failure_rejects_an_otherwise_passing_scoped_runtime_lane(self):
        self.global_failure(scoped=True)

    def test_global_lifecycle_failure_rejects_an_otherwise_passing_generic_whole_policy(self):
        self.global_failure(scoped=False)

    def test_shared_lifecycle_engine_rejects_a_foreign_raw_consumer_package_version(self):
        f, raw = self.fixture()
        with self.assertRaises(ValueError):
            fixtures.gate.deprecation_report(f.root, f.manifest_sha, semantic_rows("3.9.0"),
                                            fixtures.sha((raw / "consumer-results.json").read_bytes()), fixtures.VERSION)


if __name__ == "__main__":
    unittest.main()

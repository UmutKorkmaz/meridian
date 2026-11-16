"""Independent E5 counterexamples. Every remote is an in-memory fixture."""

import copy
import json
import shutil
import unittest
import urllib.request
import urllib.error

from eng.tests import test_release_candidate as fixtures

rc = fixtures.rc


class BundleEvidenceBoundaryTests(fixtures.NetworkGuardMixin, fixtures.PreparedBundleMixin, unittest.TestCase):
    def setUp(self):
        self.guard_network()

    def test_prepare_requires_a_candidate_bound_complete_lifecycle_report(self):
        mutations = {
            "candidate": lambda d: d.update(candidateSha256="0" * 64),
            "kind": lambda d: d.update(candidateKind="enclosing-release-manifest"),
            "consumer-hash": lambda d: d.update(consumerResultsSha256="0" * 64),
            "entries": lambda d: d.update(entries=[]),
            "source": lambda d: d.update(sourceInvariants={"status": "FAIL", "problems": ["fixture"]}),
            "pending": lambda d: d.update(consumerEvidence={"status": "pending", "lanes": 0, "problems": [], "provenanceVerified": False}),
            "self-authorized-provenance": lambda d: d.update(consumerEvidence={"status": "matched", "lanes": 1, "problems": [], "provenanceVerified": True}),
            "clock": lambda d: d.update(clock="injected-test-clock"),
            "boolean-policy-version": lambda d: d.update(policyVersion=True),
            "numeric-provenance-flag": lambda d: d["consumerEvidence"].update(provenanceVerified=0),
        }
        for label, mutation in mutations.items():
            world = fixtures.World(self.temp())
            def runner(repo, output):
                row = fixtures.passing_deprecations(repo, output)
                document = json.loads(output.read_text())
                mutation(document)
                output.write_text(json.dumps(document))
                return row  # A supplied PASS row cannot replace independent report validation.
            code, report = world.prepare(deprecation_runner=runner)
            with self.subTest(label=label):
                self.assertNotEqual(code, 0)
                self.assertNotEqual(fixtures.row(report, "e7-lifecycle")["state"], "pass")

    def test_rehashed_lifecycle_forgery_is_rederived_during_bundle_verification(self):
        for field, value in (("candidateSha256", "0" * 64), ("entries", []),
                             ("consumerEvidence", {"status": "pending", "provenanceVerified": False})):
            _, bundle, manifest, _ = self.prepared()
            path = bundle / "evidence/e7-deprecation-validation.json"
            self.writable(path)
            document = json.loads(path.read_text())
            document[field] = value
            path.write_text(json.dumps(document))
            def refresh(candidate):
                record = next(item for item in candidate["files"] if item["path"] == "evidence/e7-deprecation-validation.json")
                record.update(sha256=fixtures.file_sha256(path), size=path.stat().st_size)
            digest = self.rewrite(manifest, refresh)
            with self.subTest(field=field):
                self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])

    def test_extra_self_claimed_lane_cannot_supply_the_lifecycle_leaf_or_its_verification(self):
        world = fixtures.World(self.temp())
        valid = world.inputs / "e4/lanes/fixture__consumers"
        poison = world.inputs / "e4/lanes/000-unvalidated-extra"
        shutil.copytree(valid, poison)
        result_path = poison / "lane-result.json"
        result = json.loads(result_path.read_text())
        result["laneId"] = "000/unvalidated-extra"
        artifact = next(row for row in result["artifacts"] if row["path"].endswith("consumer-results.json"))
        raw = poison / artifact["path"]
        rows = json.loads(raw.read_text())
        for row in rows:
            row["commonOutput"] = [line + " poison=unvalidated" for line in row["commonOutput"]]
        raw.write_text(json.dumps(rows))
        artifact["sha256"] = fixtures.file_sha256(raw)
        result_path.write_text(json.dumps(result))
        code, report = world.prepare()
        self.assertEqual(code, 0, report)
        bundle = world.out / "candidate"
        path = bundle / "evidence/e7-deprecation-validation.json"
        document = json.loads(path.read_text())
        valid_raw = valid / artifact["path"]
        self.assertEqual(document["consumerResultsSha256"], fixtures.file_sha256(valid_raw))
        manifest = bundle / "candidate.json"
        self.assertTrue(rc.verify_bundle(manifest, fixtures.file_sha256(manifest))["ok"])
        # Re-approve the bytes of a forged leaf derived from the ignored lane.
        source_candidate = next((bundle / "packages").glob("package-manifest.*.json"))
        forged = rc._load_candidate_gate().deprecation_report(world.repo, fixtures.file_sha256(source_candidate), rows,
                                                             fixtures.file_sha256(raw), world.version)
        self.writable(path)
        path.write_text(json.dumps(forged))
        def refresh(candidate):
            leaf = next(item for item in candidate["files"] if item["path"] == "evidence/e7-deprecation-validation.json")
            leaf.update(sha256=fixtures.file_sha256(path), size=path.stat().st_size)
        digest = self.rewrite(manifest, refresh)
        self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])

    def test_package_target_bytes_must_belong_to_its_exact_package_identity(self):
        _, _, manifest, _ = self.prepared()

        def swap(candidate):
            targets = [t for t in candidate["targets"] if t["kind"] == "nuget-package"]
            for field in ("path", "sha256", "size"):
                targets[0][field] = targets[1][field]

        digest = self.rewrite(manifest, swap)
        self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])

    def test_source_archive_inventory_file_list_and_tooling_are_mandatory(self):
        for relative in ("source/source.tar", "source/source-files.json", "source/packages.json", "source/tooling.json"):
            _, bundle, manifest, _ = self.prepared()
            self.writable(bundle / relative)
            (bundle / relative).unlink()

            def remove(candidate):
                candidate["files"] = [f for f in candidate["files"] if f["path"] != relative]
                for row in candidate["evidencePolicy"]["rows"]:
                    row["evidence"] = [p for p in row["evidence"] if p != relative]
                if relative == "source/source.tar":
                    candidate["source"].pop("archive")
                elif relative == "source/source-files.json":
                    candidate["source"].pop("fileList")
                elif relative == "source/packages.json":
                    candidate["inventory"].pop("snapshot")

            digest = self.rewrite(manifest, remove)
            with self.subTest(relative=relative):
                self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])

    def test_required_source_tag_release_and_asset_targets_cannot_be_omitted(self):
        for kind in ("source-commit", "git-tag", "github-release", "github-release-asset"):
            _, _, manifest, _ = self.prepared()
            digest = self.rewrite(manifest, lambda c: c.update(targets=[t for t in c["targets"] if t["kind"] != kind]))
            with self.subTest(kind=kind):
                self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])

    def test_governance_protection_must_identify_the_actual_release_branch(self):
        world = fixtures.World(self.temp())
        path = world.inputs / "protection.json"
        record = json.loads(path.read_text())
        record["command"][-1] = record["command"][-1].replace("/main/", "/unrelated-feature/")
        fixtures.write_json(path, record)
        row = rc.governance_row(path, world.inputs / "rulesets.json", fixtures.REPOSITORY, ["candidate gate", "fixture lane"])
        self.assertNotEqual(row["state"], "pass")

    def test_original_zip_names_with_nul_are_rejected_before_normalization(self):
        name = b"README.md__suffix"
        unsafe = b"README.md\0_suffix"
        self.assertEqual(len(name), len(unsafe))
        archive = fixtures.zip_bytes([(name.decode(), b"fixture")])
        self.assertEqual(archive.count(name), 2, "both local and central names must be mutated")
        result = rc.inspect_zip_bytes(archive.replace(name, unsafe))
        self.assertTrue(result["problems"], result)

    def test_https_redirect_never_forwards_authorization_to_a_different_origin(self):
        handler = rc._StripCrossHostAuthorization()
        request = urllib.request.Request("https://api.github.com/a", headers={"Authorization": "fixture-token"})
        with self.assertRaises(rc.TransportError):
            handler.redirect_request(request, None, 302, "Found", {}, "http://api.github.com/b")
        redirected = handler.redirect_request(request, None, 302, "Found", {}, "https://api.github.com:444/b")
        self.assertFalse(any(k.lower() == "authorization" for k in redirected.headers))

    def test_http_422_is_an_error_rather_than_evidence_of_absence(self):
        class InvalidRead:
            def open(self, request, timeout):
                raise urllib.error.HTTPError(request.full_url, 422, "Unprocessable", {}, None)
        with self.assertRaises(rc.TransportError):
            rc.HttpReadTransport(opener=InvalidRead()).tag(fixtures.REPOSITORY, "v3.0.0")

    def test_http_versions_preserve_only_typed_canonical_version_lists(self):
        class Response:
            status = 200
            def __init__(self, document): self.body = json.dumps(document).encode()
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def read(self): return self.body
        class Opener:
            def __init__(self, document): self.document = document
            def open(self, request, timeout): return Response(self.document)
        self.assertEqual(rc.HttpReadTransport(opener=Opener({"versions": ["3.0.0"]})).nuget_versions("Fixture.Core"),
                         {"state": "present", "versions": ["3.0.0"]})
        for document in ({}, {"versions": None}, {"versions": "3.0.0"}, {"versions": [3]}, {"versions": ["not-a-version"]}):
            with self.subTest(document=document), self.assertRaises(rc.TransportError):
                rc.HttpReadTransport(opener=Opener(document)).nuget_versions("Fixture.Core")

    def test_every_required_e4_lane_subtree_is_mandatory(self):
        _, bundle, manifest, _ = self.prepared()
        relative = "evidence/e4/lanes/fixture__local/"
        directory = bundle / relative
        for path in directory.rglob("*"):
            if path.is_file(): self.writable(path)
        shutil.rmtree(directory)
        digest = self.rewrite(manifest, lambda c: c.update(files=[f for f in c["files"] if not f["path"].startswith(relative)]))
        self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])

    def test_raw_e4_failure_is_rederived_even_with_a_retained_pass_summary(self):
        _, bundle, manifest, _ = self.prepared()
        lane = bundle / "evidence/e4/lanes/fixture__local"
        raw = next((lane / "raw").glob("*.trx"))
        self.writable(raw)
        raw.write_text(raw.read_text().replace('outcome="Passed"', 'outcome="Failed"')
                       .replace('passed="1"', 'passed="0"').replace('failed="0"', 'failed="1"'))
        result_path = lane / "lane-result.json"
        result = json.loads(result_path.read_text())
        for artifact in result["artifacts"]:
            if artifact["path"] == "raw/" + raw.name: artifact["sha256"] = fixtures.file_sha256(raw)
        self.writable(result_path)
        fixtures.write_json(result_path, result)
        def refresh_files(candidate):
            for record in candidate["files"]:
                if record["path"] in {str(raw.relative_to(bundle)), str(result_path.relative_to(bundle))}:
                    path = bundle / record["path"]
                    record.update(sha256=fixtures.file_sha256(path), size=path.stat().st_size)
        digest = self.rewrite(manifest, refresh_files)
        self.assertFalse(rc.verify_bundle(manifest, digest)["ok"])


class PromotionEvidenceBoundaryTests(fixtures.PromotionMixin, unittest.TestCase):
    def test_an_unknown_package_listing_keeps_the_plan_blocked(self):
        class UnknownListing(fixtures.FakeTransport):
            def nuget_versions(self, package_id):
                return {"state": "unknown"}
        code, plan = self.plan(UnknownListing())
        self.assertEqual(code, rc.EXIT_BLOCKED, plan)
        packages = [step for step in plan["steps"] if step["kind"] == "nuget-package"]
        self.assertTrue(all(step["state"] == "unknown" and step["action"] != "push-package" for step in packages), packages)

    def test_an_untrusted_dependency_stops_before_any_dependent_package_is_published(self):
        transport = fixtures.FakeTransport()
        code, result = self.execute(transport, signature_verifier=lambda data: {"trusted": False})
        self.assertEqual(code, rc.EXIT_BLOCKED, result)
        pushed = [call[1] for call in transport.calls if call[0] == "push_nuget"]
        self.assertEqual(pushed, [fixtures.nuget_key("Fixture.Core", "3.0.0")])

    def test_post_write_read_errors_become_bounded_uncertain_receipts(self):
        class ReadFailure(fixtures.FakeTransport):
            def tag(self, repository, tag):
                if tag in self.tags:
                    raise rc.TransportError("fixture read unavailable after accepted write")
                return super().tag(repository, tag)

        transport = ReadFailure()
        code, result = self.execute(transport)
        self.assertEqual(code, rc.EXIT_BLOCKED, result)
        events, problems = rc.ReceiptStream(self.receipts).read()
        self.assertEqual(problems, [])
        self.assertTrue(any(e.get("target") == "tag:v3.0.0" and e.get("state") == "uncertain" for e in events))
        self.assertEqual([c[0] for c in transport.writes()], ["create_tag"])

    def test_a_recorded_but_not_downloadable_package_keeps_the_plan_blocked(self):
        transport = fixtures.FakeTransport()
        self.assertEqual(self.execute(transport)[0], rc.EXIT_OK)
        original = transport.nuget_package
        transport.nuget_package = lambda package, version: None if package == "Fixture.Core" else original(package, version)
        code, plan = self.plan(transport)
        self.assertEqual((code, plan["overall"]), (rc.EXIT_BLOCKED, "blocked"), plan)

    def test_changed_release_metadata_stops_a_recorded_promotion(self):
        class MetadataTransport(fixtures.FakeTransport):
            def release(self, repository, tag):
                observed = super().release(repository, tag)
                if observed["state"] == "present":
                    observed.update({k: self.releases[tag].get(k, False if k == "draft" else None)
                                     for k in ("body", "name", "prerelease", "draft")})
                return observed

        transport = MetadataTransport()
        self.assertEqual(self.execute(transport)[0], rc.EXIT_OK)
        release = transport.releases["v3.0.0"]
        original = copy.deepcopy(release)
        for field, value in (("body", "unapproved notes"), ("name", "another release"), ("prerelease", True), ("draft", True)):
            release.clear()
            release.update(copy.deepcopy(original))
            release[field] = value
            with self.subTest(field=field):
                self.assertEqual(self.plan(transport)[0], rc.EXIT_FAIL)


if __name__ == "__main__":
    unittest.main()

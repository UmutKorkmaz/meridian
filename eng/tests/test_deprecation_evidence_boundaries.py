"""Counterexamples for E7 proof boundaries; all future eligibility data is fictional."""

from __future__ import annotations

import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from eng.tests import test_deprecation_policy as fixtures

policy = fixtures.policy


class DeprecationEvidenceBoundaryTests(unittest.TestCase):
    FILES = fixtures.SourceInvariantTests.FILES

    def rows(self):
        return fixtures.ConsumerEvidenceTests().rows(["COMMON fixture=example result=ok"],
                                                     ["COMMON fixture=example result=ok"], ("net8.0",))

    def gated_result(self, ledger, candidate="c" * 64, verifier=None, history_raw=None):
        return fixtures.evaluate(ledger, "2027-12-02T00:00:00Z",
                                 verifier or fixtures.TrustedFixtureVerifier(),
                                 fixtures.authorization_for(ledger, candidate=candidate),
                                 candidate=candidate, **({"history_raw": history_raw} if history_raw is not None else {}))

    def assert_ineligible(self, result):
        self.assertNotIn(fixtures.state(result)["state"], ("diagnostic_eligible", "removal_eligible"), result)

    def test_canonical_versions_reject_final_lf_and_unicode_digits(self):
        for value in ("3.0.0\n", "3.1١.0", "3.0.0-rc.١"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                policy.parse_version(value)

    def test_canonical_utc_rejects_final_lf_and_unicode_digits(self):
        for value in ("2027-01-01T00:00:00Z\n", "٢٠٢٧-01-01T00:00:00Z"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                policy.parse_utc(value)

    def test_duplicate_execution_cannot_erase_an_earlier_failure(self):
        rows = self.rows()
        failed = dict(rows[0], exit=1)
        self.assertEqual(policy.evaluate_consumer_evidence([failed, *rows])["status"], "failed")

    def test_consumer_exit_must_be_an_integer_zero(self):
        for value in (False, 0.0, "0"):
            rows = self.rows()
            rows[0]["exit"] = value
            with self.subTest(value=value):
                self.assertEqual(policy.evaluate_consumer_evidence(rows)["status"], "failed")

    def test_consumer_contract_output_is_a_list_of_complete_common_lines(self):
        for output in ("x", [1], ["not a contract"], ["COMMON a=1\nCOMMON b=2"]):
            rows = self.rows()
            for row in rows:
                row["commonOutput"] = output
            with self.subTest(output=output):
                self.assertEqual(policy.evaluate_consumer_evidence(rows)["status"], "failed")

    def test_bad_execution_identity_is_a_structured_failure(self):
        for key, value in (("framework", None), ("framework", []), ("sdkVersion", []),
                           ("variant", []), ("packageVersion", None)):
            rows = self.rows()
            for row in rows:
                row[key] = value
            with self.subTest(key=key, value=value):
                self.assertEqual(policy.evaluate_consumer_evidence(rows)["status"], "failed")

    def test_supplied_non_list_evidence_is_a_failure(self):
        for rows in ({}, {"rows": self.rows()}, "not rows", 12):
            with self.subTest(rows=rows):
                self.assertEqual(policy.evaluate_consumer_evidence(rows)["status"], "failed")

    def test_semantic_comparison_does_not_certify_execution_provenance(self):
        result = policy.evaluate_consumer_evidence(self.rows())
        self.assertEqual(result["status"], "matched")
        self.assertIs(result["provenanceVerified"], False)

    def test_supplied_e7_results_must_cover_the_declared_fixtures(self):
        result = policy.evaluate(fixtures.real_ledger(), fixtures.at("2026-10-24T00:00:00Z"),
                                 consumer_rows=self.rows())
        self.assertEqual(result["consumerEvidence"]["status"], "failed")
        self.assertEqual(result["overall"], "FAIL")

    def test_an_unreviewed_first_anchor_cannot_start_the_clock(self):
        ledger = fixtures.gated("remove", "4.0.0")
        facts = fixtures.entry(ledger, fixtures.ALIAS)["facts"]
        facts["firstStableReplacement"] = dict(facts["verifiedAvailableBy"], clockAnchorReviewed=False)
        facts["verifiedAvailableBy"] = None
        self.assert_ineligible(self.gated_result(ledger))

    def test_a_matching_authorization_still_requires_an_exact_candidate_hash(self):
        for candidate in (None, "", "c" * 64 + "\n", "not-a-digest"):
            with self.subTest(candidate=candidate):
                self.assert_ineligible(self.gated_result(fixtures.gated("remove", "4.0.0"), candidate=candidate))

    def test_empty_migration_or_review_identity_cannot_be_accepted(self):
        for section, key in (("migrationEvidence", "consumer"), ("migrationEvidence", "resultsSha256"),
                             ("review", "reference")):
            ledger = fixtures.gated("remove", "4.0.0")
            target = fixtures.entry(ledger, fixtures.ALIAS)
            (target["review"] if section == "review" else target["facts"][section])[key] = ""
            with self.subTest(section=section, key=key):
                self.assert_ineligible(self.gated_result(ledger))

    def test_exact_action_authorization_does_not_replace_evidence_acceptance(self):
        class ActionOnlyVerifier:
            def verify(self, record, binding):
                return record.get("binding") == binding

        self.assert_ineligible(self.gated_result(fixtures.gated("remove", "4.0.0"), verifier=ActionOnlyVerifier()))

    def test_hashed_unrelated_json_does_not_corroborate_release_history(self):
        raw = b'{"fixture":"unrelated to the declared releases"}'
        ledger = fixtures.gated("remove", "4.0.0")
        ledger["releaseHistory"]["packages"][0]["responseSha256"] = fixtures.sha(raw)
        self.assert_ineligible(self.gated_result(ledger, history_raw=(raw,)))

    def test_raw_history_must_contain_the_declared_publication(self):
        ledger = fixtures.gated("remove", "4.0.0")
        raw = fixtures.HISTORY_RAW
        ledger["releaseHistory"]["packages"][0]["stableReleases"].append(
            {"version": "3.3.0", "publishedAt": "2027-10-01T00:00:00Z", "retainsBothPaths": True})
        self.assert_ineligible(self.gated_result(ledger, history_raw=(raw,)))

    def test_notice_and_anchor_facts_must_match_retained_publications(self):
        for section, key, value in (("noticeRelease", "publishedAt", "2027-01-11T10:00:00Z"),
                                    ("verifiedAvailableBy", "version", "2.1.99")):
            ledger = fixtures.gated("remove", "4.0.0")
            fixtures.entry(ledger, fixtures.ALIAS)["facts"][section][key] = value
            with self.subTest(section=section):
                self.assert_ineligible(self.gated_result(ledger))

    def test_nested_surface_values_are_rejected_as_invalid_input(self):
        ledger = fixtures.gated("remove", "4.0.0")
        fixtures.entry(ledger, fixtures.ALIAS)["proposed"]["affectedSurface"] = [[]]
        with self.assertRaises(policy.LedgerError):
            self.gated_result(ledger)

    def test_an_impossible_anchor_date_returns_a_metadata_failure(self):
        ledger = fixtures.gated("remove", "4.0.0")
        fixtures.entry(ledger, fixtures.ALIAS)["facts"]["verifiedAvailableBy"]["publishedAt"] = "2027-02-30T00:00:00Z"
        result = self.gated_result(ledger)
        self.assertEqual(result["overall"], "FAIL")

    def test_multiline_obsolete_and_alias_attributes_fail_the_source_guard(self):
        for attribute in ('[Obsolete(\n        "legacy")]\n', '[System.ObsoleteAttribute(\n        "legacy")]\n',
                          '[OldWarning(\n        "legacy")]\n'):
            repo = fixtures.SourceInvariantTests.copy_repo(self)
            path = repo / self.FILES[0]
            text = path.read_text()
            signature = "public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)"
            if "OldWarning" in attribute:
                text = "using OldWarning = System.ObsoleteAttribute;\n" + text
            path.write_text(text.replace(signature, attribute + "    " + signature, 1))
            with self.subTest(attribute=attribute):
                self.assertEqual(policy.check_source(repo, fixtures.real_ledger())["status"], "FAIL")

    def test_entry_package_and_fixed_policy_numbers_have_strict_types(self):
        for field, value in (("package", []), ("package", ""), ("policyVersion", True),
                             ("calendarMonths", 6.0), ("qualifyingMinorLines", 2.0)):
            ledger = fixtures.real_ledger()
            if field == "package":
                ledger["entries"][0][field] = value
            elif field == "policyVersion":
                ledger[field] = value
            else:
                ledger["policy"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(policy.LedgerError):
                policy.evaluate(ledger, fixtures.at("2026-10-24T00:00:00Z"))

    def test_actual_cli_rejects_duplicate_consumer_json_fields(self):
        with tempfile.TemporaryDirectory() as directory:
            results = Path(directory) / "rows.json"
            results.write_text('[{"consumer":"ignored","exit":1,"exit":0}]')
            process = subprocess.run([sys.executable, "-B", str(fixtures.ROOT / "eng/deprecation_policy.py"),
                                      "validate", "--consumer-results", str(results)], capture_output=True, text=True)
        self.assertEqual(process.returncode, 2, process.stdout + process.stderr)

    def test_history_leaf_identity_listing_counts_and_embedded_pages_are_checked(self):
        for mutation in ("wrong-id", "unlisted", "duplicate", "count", "external-page"):
            ledger = fixtures.gated("remove", "4.0.0")
            package = ledger["releaseHistory"]["packages"][0]
            document = json.loads(fixtures.registration_raw(package))
            page = document["items"][0]
            if mutation == "wrong-id":
                page["items"][0]["catalogEntry"]["id"] = "Another.Package"
            elif mutation == "unlisted":
                page["items"][0]["catalogEntry"]["listed"] = False
            elif mutation == "duplicate":
                page["items"].append(copy.deepcopy(page["items"][0]))
                page["count"] += 1
            elif mutation == "count":
                page["count"] += 1
            else:
                del page["items"]
            raw = json.dumps(document).encode()
            package["responseSha256"] = fixtures.sha(raw)
            fixtures.entry(ledger, fixtures.ALIAS)["facts"]["verifiedAvailableBy"]["responseSha256"] = fixtures.sha(raw)
            with self.subTest(mutation=mutation):
                self.assert_ineligible(self.gated_result(ledger, history_raw=(raw,)))

    def test_duplicate_casefolded_package_histories_never_leave_an_eligible_entry(self):
        ledger = fixtures.gated("remove", "4.0.0")
        duplicate = copy.deepcopy(ledger["releaseHistory"]["packages"][0])
        duplicate["id"] = duplicate["id"].lower()
        ledger["releaseHistory"]["packages"].append(duplicate)
        self.assert_ineligible(self.gated_result(ledger))

    def test_action_approval_can_be_accepted_while_supporting_evidence_is_rejected(self):
        class RejectedEvidence(fixtures.TrustedFixtureVerifier):
            def verify_evidence(self, binding):
                return False
        self.assert_ineligible(self.gated_result(fixtures.gated("remove", "4.0.0"), verifier=RejectedEvidence()))

    def test_attribute_guard_ignores_mentions_in_comments_and_literals(self):
        for prefix in ('// [Obsolete("old")]\\n', '/// <summary>Obsolete is mentioned.</summary>\\n',
                       '[System.ComponentModel.Description("Obsolete [ ]")]\\n'):
            repo = fixtures.SourceInvariantTests.copy_repo(self)
            path = repo / self.FILES[0]
            signature = "public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)"
            path.write_text(path.read_text().replace(signature, prefix.replace("\\n", "\n") + "    " + signature, 1))
            with self.subTest(prefix=prefix):
                self.assertEqual(policy.check_source(repo, fixtures.real_ledger())["status"], "PASS")

    def test_a_new_diagnostic_requires_a_non_error_severity_and_reserved_id(self):
        for field, value in (("isError", True), ("isError", None), ("reservedId", None)):
            ledger = fixtures.gated("add-diagnostic", "3.1.0", now="2027-03-01T00:00:00Z")
            target = fixtures.entry(ledger, fixtures.ALIAS)
            (target["diagnostic"] if field == "reservedId" else target["proposed"])[field] = value
            result = fixtures.evaluate(ledger, "2027-03-01T00:00:00Z", fixtures.TrustedFixtureVerifier(),
                                       fixtures.authorization_for(ledger))
            with self.subTest(field=field, value=value):
                self.assert_ineligible(result)

    def test_a_new_non_error_diagnostic_with_all_prerequisites_is_eligible_for_review(self):
        ledger = fixtures.gated("add-diagnostic", "3.1.0", now="2027-03-01T00:00:00Z")
        self.assertIs(fixtures.entry(ledger, fixtures.ALIAS)["proposed"]["isError"], False)
        result = fixtures.evaluate(ledger, "2027-03-01T00:00:00Z", fixtures.TrustedFixtureVerifier(),
                                   fixtures.authorization_for(ledger))
        self.assertEqual(fixtures.state(result)["state"], "diagnostic_eligible", result)
        self.assertEqual(result["overall"], "PASS")

    def test_a_retained_signature_inside_a_comment_or_literal_is_not_a_declaration(self):
        signature = "public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)"
        for enclosure in ("comment", "raw-literal"):
            repo = fixtures.SourceInvariantTests.copy_repo(self)
            path = repo / self.FILES[0]
            text = path.read_text()
            start = text.index(signature)
            end = text.index("}", start) + 1
            declaration = text[start:end]
            replacement = ("/*\n" + declaration + "\n*/" if enclosure == "comment" else
                           'private const string RemovedDeclaration = """\n' + declaration + '\n""";')
            path.write_text(text[:start] + replacement + text[end:])
            with self.subTest(enclosure=enclosure):
                self.assertEqual(policy.check_source(repo, fixtures.real_ledger())["status"], "FAIL")

    def test_cross_file_global_obsolete_aliases_are_checked_within_the_project(self):
        repo = fixtures.SourceInvariantTests.copy_repo(self)
        path = repo / self.FILES[0]
        signature = "public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)"
        (repo / "src/Meridian.Mapping/Aliases.cs").write_text("global using Retired = global::System.ObsoleteAttribute;\n")
        path.write_text(path.read_text().replace(signature, '[Retired("legacy")]\n    ' + signature, 1))
        self.assertEqual(policy.check_source(repo, fixtures.real_ledger())["status"], "FAIL")

    def test_verbatim_backslashes_cannot_expose_a_signature_inside_another_literal(self):
        signature = "public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)"
        for prefix in ('@', '$@', '@$'):
            repo = fixtures.SourceInvariantTests.copy_repo(self)
            path = repo / self.FILES[0]
            declarations = ('private const string P = ' + prefix + r'"C:\";' + "\n" +
                            "    private const string Q = " + prefix + '"\n' + signature + '\n";\n    ')
            path.write_text(path.read_text().replace(signature, declarations + signature.replace("AddAutoMapper", "RemovedAlias"), 1))
            with self.subTest(prefix=prefix):
                self.assertEqual(policy.check_source(repo, fixtures.real_ledger())["status"], "FAIL")


if __name__ == "__main__":
    unittest.main()

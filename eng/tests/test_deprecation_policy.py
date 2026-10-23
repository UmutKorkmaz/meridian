"""Behavioral tests for the E7 deprecation lifecycle validator (eng/deprecation_policy.py, eng/verify-deprecations.ps1).

The real ledger in docs/deprecations.md is validated against the real repository source and the real UTC clock. All
eligibility rules are exercised with fixed fictional releases, announcements and a controlled clock that only the
Python test API can inject (the command line has no clock option). Fictional facts here are labelled fixtures: they
prove the policy implementation, not the eligibility of any real Meridian package.
"""

from __future__ import annotations

import copy
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))

import deprecation_policy as policy  # noqa: E402

UTC = datetime.timezone.utc
LEDGER_PATH = ROOT / "docs" / "deprecations.md"
ALIAS = "alias-addautomapper"


def at(text: str) -> datetime.datetime:
    return policy.parse_utc(text)


def real_ledger() -> dict:
    return policy.extract_ledger(LEDGER_PATH.read_text(encoding="utf-8"))


def entry(ledger: dict, entry_id: str) -> dict:
    return next(e for e in ledger["entries"] if e["id"] == entry_id)


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


class TrustedFixtureVerifier:
    """Stands in for a trusted controller/release authorization service. Test API only."""

    def __init__(self, accept: bool = True):
        self.accept = accept
        self.seen = []

    def verify(self, record: dict, binding: dict) -> bool:
        self.seen.append((record, binding))
        return self.accept and record.get("binding") == binding

    def verify_evidence(self, binding: dict) -> bool:
        # Controlled fixture acceptance stands in for an external artifact-review
        # service. A matching action record alone cannot accept these artifacts.
        return (self.accept and binding["migrationEvidence"] == {
            "consumer": "Meridian.DeprecationConsumer", "resultsSha256": "b" * 64,
            "acceptedAt": "2027-01-05T00:00:00Z"}
            and binding["review"] == {"reference": "fixture-review-1", "decidedAt": "2027-01-09T00:00:00Z"})


# Fictional release history for the fixture clock: notice in 3.0.0, then qualifying 3.1 and 3.2 lines.
HISTORY = {
    "packages": [{
        "id": "Meridian.Mapping",
        "authority": "https://fixture.invalid/registration/meridian.mapping/index.json",
        "retrievedAt": "2028-03-01T00:00:00Z",
        "responseSha256": None,
        "stableReleases": [
            {"version": "2.1.3", "publishedAt": "2026-06-23T16:22:57.407Z", "retainsBothPaths": True},
            {"version": "3.0.0", "publishedAt": "2027-01-10T10:00:00Z", "retainsBothPaths": True},
            {"version": "3.0.1", "publishedAt": "2027-02-01T10:00:00Z", "retainsBothPaths": True},
            {"version": "3.1.0", "publishedAt": "2027-04-01T10:00:00Z", "retainsBothPaths": True},
            {"version": "3.1.5", "publishedAt": "2027-05-01T10:00:00Z", "retainsBothPaths": True},
            {"version": "3.2.0", "publishedAt": "2027-08-01T10:00:00Z", "retainsBothPaths": True},
            {"version": "4.0.0", "publishedAt": "2027-09-01T10:00:00Z", "retainsBothPaths": True},
        ],
    }],
    "limit": "fixture",
}


def registration_raw(package: dict) -> bytes:
    """A complete fictional NuGet registration index, never real publication proof."""
    leaves = [{"catalogEntry": {"id": package["id"], "version": release["version"],
                               "published": release["publishedAt"], "listed": True}}
              for release in package["stableReleases"]]
    return json.dumps({"@id": package["authority"], "count": 1,
                       "items": [{"count": len(leaves), "items": leaves}]}).encode()


HISTORY_RAW = registration_raw(HISTORY["packages"][0])
HISTORY["packages"][0]["responseSha256"] = sha(HISTORY_RAW)


def history_as_of(now: str) -> dict:
    """A fictional registration snapshot taken at ``now``: it cannot contain releases published later."""
    history = copy.deepcopy(HISTORY)
    package = history["packages"][0]
    package["retrievedAt"] = now
    package["stableReleases"] = [r for r in package["stableReleases"] if at(r["publishedAt"]) <= at(now)]
    package["responseSha256"] = sha(registration_raw(package))
    return history


def gated(action: str, version: str, now: str = "2027-12-02T00:00:00Z", **overrides) -> dict:
    """Return a ledger whose alias entry proposes ``action`` with complete fictional prerequisites as of ``now``."""
    ledger = real_ledger()
    ledger["releaseHistory"] = history_as_of(now)
    target = entry(ledger, ALIAS)
    target["facts"]["verifiedAvailableBy"]["clockAnchorReviewed"] = True
    anchor = target["facts"]["verifiedAvailableBy"]
    history_package = ledger["releaseHistory"]["packages"][0]
    anchor.update(responseSha256=history_package["responseSha256"], authority=history_package["authority"], retrievedAt=now)
    target["facts"]["announcement"] = {"url": "https://fixture.invalid/announcement", "contentSha256": "a" * 64,
                                       "publishedAt": "2027-01-10T09:00:00Z", "retrievedAt": "2027-01-11T00:00:00Z"}
    target["facts"]["noticeRelease"] = {"package": "Meridian.Mapping", "version": "3.0.0", "publishedAt": "2027-01-10T10:00:00Z"}
    target["facts"]["migrationEvidence"] = {"consumer": "Meridian.DeprecationConsumer", "resultsSha256": "b" * 64,
                                            "acceptedAt": "2027-01-05T00:00:00Z"}
    target["proposed"] = {"action": action, "version": version, "isError": False if action in policy.DIAGNOSTIC_ACTIONS else None,
                          "affectedSurface": list(target["targets"])}
    target["review"] = {"reference": "fixture-review-1", "decidedAt": "2027-01-09T00:00:00Z"}
    target["supportStatus"] = policy.derived_support_status(action)
    target.update(overrides)
    return ledger


def authorization_for(ledger: dict, entry_id: str = ALIAS, candidate: str = "c" * 64) -> dict:
    target = entry(ledger, entry_id)
    return {"binding": policy.authorization_binding(ledger, target, candidate)}


def evaluate(ledger: dict, now: str, verifier=None, authorization=None, history_raw=None, candidate="c" * 64, repo=None):
    if history_raw is None:
        history_raw = tuple(registration_raw(p) for p in ledger["releaseHistory"]["packages"])
    return policy.evaluate(ledger, at(now), repo=repo, release_history_evidence=list(history_raw), authorization=authorization,
                           verifier=verifier, candidate_sha256=candidate)


def state(result: dict, entry_id: str = ALIAS) -> dict:
    return next(e for e in result["entries"] if e["id"] == entry_id)


class LedgerExtractionTests(unittest.TestCase):
    def test_the_real_ledger_has_exactly_the_four_entries(self):
        ledger = real_ledger()
        self.assertEqual(ledger["schema"], policy.LEDGER_SCHEMA)
        self.assertEqual([e["id"] for e in ledger["entries"]], list(policy.EXPECTED_ENTRY_IDS))

    def test_exactly_one_marked_json_block_is_required(self):
        text = LEDGER_PATH.read_text(encoding="utf-8")
        block = text[text.index(policy.BEGIN_MARKER):text.index(policy.END_MARKER) + len(policy.END_MARKER)]
        for broken in (text.replace(policy.BEGIN_MARKER, ""), text + "\n" + block, text.replace("```json", "```text", 1)):
            with self.subTest(broken=broken[:40]), self.assertRaises(policy.LedgerError):
                policy.extract_ledger(broken)

    def test_duplicate_json_keys_are_rejected(self):
        text = LEDGER_PATH.read_text(encoding="utf-8").replace('"policyVersion": 1,', '"policyVersion": 1,\n  "policyVersion": 1,', 1)
        with self.assertRaises(policy.LedgerError):
            policy.extract_ledger(text)

    def test_the_entry_set_is_exactly_the_four_candidates(self):
        for mutate in (lambda l: l["entries"].pop(),
                       lambda l: l["entries"].append(copy.deepcopy(l["entries"][0])),
                       lambda l: l["entries"][0].update(id="something-else"),
                       lambda l: l["entries"][0].update(unexpectedField=1),
                       lambda l: l.update(schema="other/1")):
            ledger = real_ledger()
            mutate(ledger)
            with self.subTest(mutate=mutate), self.assertRaises(policy.LedgerError):
                policy.evaluate(ledger, at("2026-10-23T00:00:00Z"))


class CalendarTests(unittest.TestCase):
    def test_six_calendar_months_preserve_the_instant_and_clamp_the_month_end(self):
        self.assertEqual(policy.add_calendar_months(at("2026-06-23T16:22:57.407Z"), 6), at("2026-12-23T16:22:57.407Z"))
        self.assertEqual(policy.add_calendar_months(at("2026-08-31T12:00:00Z"), 6), at("2027-02-28T12:00:00Z"))
        self.assertEqual(policy.add_calendar_months(at("2027-08-31T12:00:00Z"), 6), at("2028-02-29T12:00:00Z"))
        self.assertEqual(policy.add_calendar_months(at("2028-01-31T00:00:00Z"), 1), at("2028-02-29T00:00:00Z"))
        self.assertEqual(policy.add_calendar_months(at("2026-12-31T23:59:59Z"), 6), at("2027-06-30T23:59:59Z"))

    def test_timestamps_must_be_explicit_utc(self):
        for bad in ("2026-10-23T00:00:00", "2026-10-23", "2026-10-23T00:00:00+03:00", "yesterday"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                policy.parse_utc(bad)


class VersionTests(unittest.TestCase):
    def test_strict_semver(self):
        self.assertEqual(policy.parse_version("3.0.0"), (3, 0, 0, None))
        self.assertEqual(policy.parse_version("3.1.0-rc.1"), (3, 1, 0, "rc.1"))
        for bad in ("3.0", "03.0.0", "3.0.0.0", "3.0.0+build", "3.0.0-01", "v3.0.0", " 3.0.0"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                policy.parse_version(bad)


class MinorCountingTests(unittest.TestCase):
    def releases(self):
        return HISTORY["packages"][0]["stableReleases"]

    def test_two_higher_minor_lines_in_the_notice_major_count_once_each(self):
        notice = {"version": "3.0.0", "publishedAt": "2027-01-10T10:00:00Z"}
        self.assertEqual(policy.qualifying_minor_lines(notice, self.releases()), ["3.1", "3.2"])

    def test_patches_prereleases_majors_and_earlier_releases_do_not_count(self):
        notice = {"version": "3.0.0", "publishedAt": "2027-01-10T10:00:00Z"}
        releases = [r for r in self.releases() if r["version"] not in ("3.1.0", "3.1.5", "3.2.0")]
        releases.append({"version": "3.1.0-rc.1", "publishedAt": "2027-03-01T00:00:00Z", "retainsBothPaths": True})
        self.assertEqual(policy.qualifying_minor_lines(notice, releases), [])
        early = [{"version": "3.1.0", "publishedAt": "2027-01-09T00:00:00Z", "retainsBothPaths": True}]
        self.assertEqual(policy.qualifying_minor_lines(notice, early), [])

    def test_a_line_that_dropped_the_old_path_does_not_count(self):
        notice = {"version": "3.0.0", "publishedAt": "2027-01-10T10:00:00Z"}
        releases = [dict(r, retainsBothPaths=r["version"] != "3.2.0") for r in self.releases()]
        self.assertEqual(policy.qualifying_minor_lines(notice, releases), ["3.1"])

    def test_a_notice_on_the_2x_line_counts_2x_minors(self):
        notice = {"version": "2.2.0", "publishedAt": "2026-11-01T00:00:00Z"}
        releases = [{"version": "2.3.0", "publishedAt": "2027-01-01T00:00:00Z", "retainsBothPaths": True},
                    {"version": "2.4.0", "publishedAt": "2027-03-01T00:00:00Z", "retainsBothPaths": True},
                    {"version": "3.0.0", "publishedAt": "2027-04-01T00:00:00Z", "retainsBothPaths": True}]
        self.assertEqual(policy.qualifying_minor_lines(notice, releases), ["2.3", "2.4"])


class RealLedgerTests(unittest.TestCase):
    def test_the_real_ledger_passes_as_guidance_only_against_the_real_source(self):
        result = policy.evaluate(real_ledger(), datetime.datetime.now(UTC), repo=ROOT)
        self.assertEqual(result["overall"], "PASS", result)
        self.assertEqual([e["state"] for e in result["entries"]], ["retain_pass"] * 4)
        self.assertEqual(result["sourceInvariants"]["status"], "PASS", result["sourceInvariants"])
        self.assertEqual(result["consumerEvidence"]["status"], "pending")

    def test_no_real_entry_claims_a_first_stable_replacement_announcement_or_eligibility(self):
        for item in real_ledger()["entries"]:
            with self.subTest(entry=item["id"]):
                self.assertIsNone(item["facts"]["firstStableReplacement"])
                self.assertIsNone(item["facts"]["announcement"])
                self.assertIsNone(item["facts"]["noticeRelease"])
                self.assertEqual(item["proposed"]["action"], "retain")
                self.assertEqual(item["supportStatus"], "supported")
                anchor = item["facts"]["verifiedAvailableBy"]
                self.assertTrue(anchor is None or anchor["clockAnchorReviewed"] is False)

    def test_only_the_alias_entry_reserves_merdiandep001_and_nothing_emits_it(self):
        ids = {e["id"]: e["diagnostic"] for e in real_ledger()["entries"]}
        self.assertEqual(ids[ALIAS], {"reservedId": "MERIDIANDEP001", "status": "reserved-not-emitted", "existing": None})
        self.assertEqual(ids["generated-legacy-skipped-members"]["existing"], "MERIDIANGEN002")
        for other in ("generated-legacy-skipped-members", "cache-raw-keys", "idempotency-nonatomic-concurrency"):
            self.assertIsNone(ids[other]["reservedId"])

    def test_the_retained_supported_exclusions_are_complete(self):
        exclusions = set(real_ledger()["retainedSupported"])
        for required in policy.REQUIRED_EXCLUSIONS:
            self.assertIn(required, exclusions)


class EligibilityTests(unittest.TestCase):
    def test_retain_is_guidance_only_and_passes(self):
        result = evaluate(real_ledger(), "2026-10-24T00:00:00Z")
        self.assertEqual(state(result)["state"], "retain_pass")
        self.assertEqual(result["overall"], "PASS")

    def test_an_obsolete_warning_without_announcement_fails(self):
        ledger = gated("add-obsolete-warning", "3.1.0", now="2027-06-01T00:00:00Z")
        entry(ledger, ALIAS)["facts"]["announcement"] = None
        result = evaluate(ledger, "2027-06-01T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result)["state"], "diagnostic_ineligible")
        self.assertIn("public announcement", " ".join(state(result)["failedPrerequisites"]))
        self.assertEqual(result["overall"], "FAIL")

    def test_an_obsolete_warning_with_every_prerequisite_is_eligible(self):
        ledger = gated("add-obsolete-warning", "3.1.0", now="2027-03-01T00:00:00Z")
        result = evaluate(ledger, "2027-03-01T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result)["state"], "diagnostic_eligible", state(result))
        self.assertEqual(result["overall"], "PASS")

    def test_an_obsolete_warning_in_a_patch_or_as_an_error_fails(self):
        for overrides in ({"version": "3.0.2"}, {"isError": True}):
            ledger = gated("add-obsolete-warning", "3.1.0", now="2027-03-01T00:00:00Z")
            entry(ledger, ALIAS)["proposed"].update(overrides)
            result = evaluate(ledger, "2027-03-01T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))
            with self.subTest(overrides=overrides):
                self.assertEqual(state(result)["state"], "diagnostic_ineligible")

    def test_an_unreviewed_availability_anchor_cannot_start_any_clock(self):
        ledger = gated("add-obsolete-warning", "3.1.0", now="2027-03-01T00:00:00Z")
        entry(ledger, ALIAS)["facts"]["verifiedAvailableBy"]["clockAnchorReviewed"] = False
        result = evaluate(ledger, "2027-03-01T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result)["state"], "diagnostic_ineligible")

    def test_removal_needs_both_two_minor_lines_and_six_months(self):
        floor = policy.add_calendar_months(at("2027-01-10T09:00:00Z"), 6)  # the announcement is later than the replacement
        just_before = (floor - datetime.timedelta(milliseconds=1)).strftime("%Y-%m-%dT%H:%M:%S.%fZ")
        ledger = gated("remove", "4.0.0", now=just_before)
        result = evaluate(ledger, just_before, TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result)["state"], "removal_ineligible")
        self.assertIn("six calendar months", " ".join(state(result)["failedPrerequisites"]))
        exactly = floor.strftime("%Y-%m-%dT%H:%M:%S.%fZ")
        ledger = gated("remove", "4.0.0", now=exactly)
        result = evaluate(ledger, exactly, TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertNotIn("six calendar months", " ".join(state(result)["failedPrerequisites"]))
        # At the floor only 3.1 exists (3.2 is published later), so two minor lines are still missing.
        self.assertEqual(state(result)["state"], "removal_ineligible")
        self.assertIn("two qualifying", " ".join(state(result)["failedPrerequisites"]))

    def test_removal_is_eligible_only_for_review_when_every_gate_holds(self):
        ledger = gated("remove", "4.0.0")
        result = evaluate(ledger, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result)["state"], "removal_eligible", state(result))
        self.assertEqual(state(result)["derived"]["qualifyingMinorLines"], ["3.1", "3.2"])
        self.assertEqual(state(result)["derived"]["calendarFloor"], "2027-07-10T09:00:00.000000Z")
        self.assertEqual(result["overall"], "PASS")

    def test_the_window_starts_at_the_later_of_availability_and_announcement(self):
        ledger = gated("remove", "4.0.0")
        entry(ledger, ALIAS)["facts"]["announcement"]["publishedAt"] = "2027-08-15T00:00:00Z"
        entry(ledger, ALIAS)["facts"]["announcement"]["retrievedAt"] = "2027-08-16T00:00:00Z"
        result = evaluate(ledger, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result)["derived"]["windowStart"], "2027-08-15T00:00:00.000000Z")
        self.assertEqual(state(result)["state"], "removal_ineligible")

    def test_removal_in_the_notice_major_or_without_review_or_authorization_fails(self):
        cases = [("version", "3.3.0"), ("review", None), ("authorization", None), ("verifier", None)]
        for field, value in cases:
            ledger = gated("remove", "4.0.0")
            verifier, authorization = TrustedFixtureVerifier(), authorization_for(ledger)
            if field == "version":
                entry(ledger, ALIAS)["proposed"]["version"] = value
                authorization = authorization_for(ledger)
            elif field == "review":
                entry(ledger, ALIAS)["review"] = None
            elif field == "authorization":
                authorization = None
            else:
                verifier = None
            result = evaluate(ledger, "2027-12-02T00:00:00Z", verifier, authorization)
            with self.subTest(field=field):
                self.assertEqual(state(result)["state"], "removal_ineligible")
                self.assertEqual(result["overall"], "FAIL")

    def test_an_authorization_for_another_ledger_candidate_or_scope_is_rejected(self):
        ledger = gated("remove", "4.0.0")
        stale = authorization_for(ledger, candidate="d" * 64)
        result = evaluate(ledger, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), stale)
        self.assertEqual(state(result)["state"], "removal_ineligible")
        narrow = gated("remove", "4.0.0")
        entry(narrow, ALIAS)["proposed"]["affectedSurface"] = entry(narrow, ALIAS)["targets"][:2]
        authorized_narrow = authorization_for(narrow)
        entry(narrow, ALIAS)["proposed"]["affectedSurface"] = list(entry(narrow, ALIAS)["targets"])
        result = evaluate(narrow, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorized_narrow)
        self.assertEqual(state(result)["state"], "removal_ineligible")
        self.assertIn("not trusted for this exact", " ".join(state(result)["failedPrerequisites"]))
        beyond = gated("remove", "4.0.0")
        entry(beyond, ALIAS)["proposed"]["affectedSurface"].append("M:Meridian.Mapping.Extra")
        result = evaluate(beyond, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorization_for(beyond))
        self.assertEqual(state(result)["state"], "invalid_metadata")
        self.assertIn("wider than the entry's declared targets", " ".join(state(result)["failedPrerequisites"]))

    def test_an_unverified_release_history_cannot_count_minors(self):
        ledger = gated("remove", "4.0.0")
        result = evaluate(ledger, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger), history_raw=())
        self.assertEqual(state(result)["state"], "removal_ineligible")
        self.assertIn("release history", " ".join(state(result)["failedPrerequisites"]))

    def test_a_default_flip_follows_the_removal_gate(self):
        ledger = gated("flip-default", "4.0.0")
        self.assertEqual(state(evaluate(ledger, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger)))["state"],
                         "removal_eligible")
        early = gated("flip-default", "4.0.0", now="2027-06-01T00:00:00Z")
        self.assertEqual(state(evaluate(early, "2027-06-01T00:00:00Z", TrustedFixtureVerifier(), authorization_for(early)))["state"],
                         "removal_ineligible")


class MetadataTests(unittest.TestCase):
    def invalid(self, ledger, now="2027-12-02T00:00:00Z"):
        result = evaluate(ledger, now, TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result)["state"], "invalid_metadata", state(result))
        self.assertEqual(result["overall"], "FAIL")
        return state(result)

    def test_future_or_sentinel_dates_are_invalid(self):
        future = gated("remove", "4.0.0")
        entry(future, ALIAS)["facts"]["announcement"]["publishedAt"] = "2030-01-01T00:00:00Z"
        self.invalid(future)
        sentinel = gated("remove", "4.0.0")
        entry(sentinel, ALIAS)["facts"]["verifiedAvailableBy"]["publishedAt"] = "1900-01-01T00:00:00Z"
        self.invalid(sentinel)
        retrieved = gated("remove", "4.0.0")
        entry(retrieved, ALIAS)["facts"]["announcement"]["retrievedAt"] = "2031-01-01T00:00:00Z"
        self.invalid(retrieved)

    def test_an_announcement_before_the_replacement_existed_is_invalid(self):
        ledger = gated("remove", "4.0.0")
        entry(ledger, ALIAS)["facts"]["announcement"]["publishedAt"] = "2026-01-01T00:00:00Z"
        entry(ledger, ALIAS)["facts"]["announcement"]["retrievedAt"] = "2026-01-02T00:00:00Z"
        self.invalid(ledger)

    def test_a_self_authored_approval_is_never_authority(self):
        ledger = gated("remove", "4.0.0")
        entry(ledger, ALIAS)["review"]["approved"] = True
        with self.assertRaises(policy.LedgerError):
            evaluate(ledger, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))

    def test_a_support_status_that_contradicts_the_action_is_invalid(self):
        ledger = gated("remove", "4.0.0")
        entry(ledger, ALIAS)["supportStatus"] = "supported"
        self.invalid(ledger)

    def test_a_gated_action_on_a_retained_supported_api_is_invalid(self):
        ledger = gated("remove", "4.0.0")
        target = entry(ledger, "idempotency-nonatomic-concurrency")
        target["proposed"] = {"action": "remove", "version": "4.0.0", "isError": None,
                              "affectedSurface": ["T:Meridian.Mediator.Behaviors.IIdempotencyStore"]}
        target["supportStatus"] = policy.derived_support_status("remove")
        result = evaluate(ledger, "2027-12-02T00:00:00Z", TrustedFixtureVerifier(), authorization_for(ledger))
        self.assertEqual(state(result, "idempotency-nonatomic-concurrency")["state"], "invalid_metadata")


class SourceInvariantTests(unittest.TestCase):
    FILES = ("src/Meridian.Mapping/Extensions/ServiceCollectionExtensions.cs",
             "src/Meridian.Mapping.Generators/MeridianMapperGenerator.cs",
             "src/Meridian.Mediator/Behaviors/IdempotencyBehavior.cs",
             "src/Meridian.Mediator/Behaviors/CachingBehavior.cs",
             "src/Meridian.Mediator/Extensions/MeridianMediatorConfiguration.cs",
             "src/Meridian.Mapping.Generators/Diagnostics/MapperDiagnostics.cs")

    def copy_repo(self):
        root = Path(tempfile.mkdtemp(prefix="meridian-deprecation-"))
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        for relative in self.FILES:
            target = root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, target)
        return root

    def check(self, repo):
        return policy.evaluate(real_ledger(), at("2026-10-24T00:00:00Z"), repo=repo)

    def test_the_copied_real_source_passes(self):
        self.assertEqual(self.check(self.copy_repo())["sourceInvariants"]["status"], "PASS")

    def test_an_obsolete_alias_or_a_missing_overload_fails(self):
        repo = self.copy_repo()
        path = repo / self.FILES[0]
        text = path.read_text(encoding="utf-8")
        path.write_text(text.replace("    public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)",
                                     "    [Obsolete(\"x\", DiagnosticId = \"MERIDIANDEP001\")]\n    public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)", 1), encoding="utf-8")
        result = self.check(repo)
        self.assertEqual(result["overall"], "FAIL")
        self.assertTrue(any("Obsolete" in p or "MERIDIANDEP001" in p for p in result["sourceInvariants"]["problems"]), result["sourceInvariants"])
        path.write_text(text.replace("public static IServiceCollection AddAutoMapper(this IServiceCollection services, Action<IMapperConfigurationExpression> configure)",
                                     "public static IServiceCollection AddLegacyMapper(this IServiceCollection services, Action<IMapperConfigurationExpression> configure)", 1), encoding="utf-8")
        self.assertEqual(self.check(repo)["overall"], "FAIL")

    def test_a_changed_legacy_default_or_a_removed_base_store_fails(self):
        for relative, old, new in ((self.FILES[1], "= GeneratedMappingMode.Legacy;", "= GeneratedMappingMode.Structural;"),
                                   (self.FILES[2], "public interface IIdempotencyStore", "internal interface IIdempotencyStore"),
                                   (self.FILES[3], "public bool UseRequestTypeNamespacedKeys { get; init; } = true;", "public bool UseRequestTypeNamespacedKeys { get; init; } = false;"),
                                   (self.FILES[4], "public MeridianMediatorConfiguration AddClosedStreamBehavior(", "internal MeridianMediatorConfiguration AddClosedStreamBehavior(")):
            repo = self.copy_repo()
            path = repo / relative
            text = path.read_text(encoding="utf-8")
            self.assertIn(old, text)
            path.write_text(text.replace(old, new, 1), encoding="utf-8")
            with self.subTest(relative=relative, new=new):
                self.assertEqual(self.check(repo)["overall"], "FAIL")


class ConsumerEvidenceTests(unittest.TestCase):
    def rows(self, legacy_common, migrated_common, frameworks=("net8.0", "net10.0")):
        rows = []
        for framework in frameworks:
            rows.append({"consumer": "Meridian.DeprecationConsumer", "variant": "legacy", "framework": framework, "sdkVersion": "10.0.300",
                         "exit": 0, "commonOutput": list(legacy_common), "packageVersion": "3.0.0-x.1"})
            rows.append({"consumer": "Meridian.DeprecationConsumer", "variant": "migrated", "framework": framework, "sdkVersion": "10.0.300",
                         "exit": 0, "commonOutput": list(migrated_common), "packageVersion": "3.0.0-x.1"})
        return rows

    def test_identical_old_and_new_common_results_per_lane_match_without_proving_execution(self):
        common = ["COMMON alias assembly=42", "COMMON cache hit=1"]
        result = policy.evaluate_consumer_evidence(self.rows(common, list(reversed(common))))
        self.assertEqual(result["status"], "matched", result)
        self.assertIs(result["provenanceVerified"], False)
        self.assertEqual(result["lanes"], 2)

    def test_differing_or_missing_variants_fail(self):
        self.assertEqual(policy.evaluate_consumer_evidence(self.rows(["COMMON a=1"], ["COMMON a=2"]))["status"], "failed")
        rows = [r for r in self.rows(["COMMON a=1"], ["COMMON a=1"]) if not (r["variant"] == "migrated" and r["framework"] == "net8.0")]
        self.assertEqual(policy.evaluate_consumer_evidence(rows)["status"], "failed")
        failing = self.rows(["COMMON a=1"], ["COMMON a=1"])
        failing[0]["exit"] = 1
        self.assertEqual(policy.evaluate_consumer_evidence(failing)["status"], "failed")
        self.assertEqual(policy.evaluate_consumer_evidence([])["status"], "pending")


class CommandLineTests(unittest.TestCase):
    def run_cli(self, *args):
        return subprocess.run([sys.executable, "-B", str(ROOT / "eng/deprecation_policy.py"), *map(str, args)], capture_output=True, text=True)

    def test_validate_writes_the_report_with_the_system_clock(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory) / "deprecation-validation.json"
            result = self.run_cli("validate", "--ledger", LEDGER_PATH, "--output", out)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            report = json.loads(out.read_text())
        self.assertEqual(report["schema"], policy.OUTPUT_SCHEMA)
        self.assertEqual(report["clock"], "system-utc")
        self.assertEqual(report["ledgerSha256"], sha(LEDGER_PATH.read_bytes()))
        self.assertEqual(report["overall"], "PASS")

    def test_the_command_line_cannot_set_the_clock(self):
        self.assertEqual(self.run_cli("validate", "--ledger", LEDGER_PATH, "--as-of", "2030-01-01T00:00:00Z").returncode, 2)

    def test_a_gated_ledger_fails_on_the_command_line_without_a_trusted_verifier(self):
        with tempfile.TemporaryDirectory() as directory:
            ledger = gated("remove", "4.0.0")
            path = Path(directory) / "deprecations.md"
            path.write_text(policy.render_ledger_block(ledger), encoding="utf-8")
            auth = Path(directory) / "authorization.json"
            auth.write_text(json.dumps(authorization_for(ledger)))
            result = self.run_cli("validate", "--ledger", path, "--authorization", auth, "--output", Path(directory) / "out.json")
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            report = json.loads((Path(directory) / "out.json").read_text())
            self.assertEqual(report["overall"], "FAIL")
            self.assertNotEqual(state(report)["state"], "removal_eligible")

    def test_invalid_input_exits_two(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "deprecations.md"
            path.write_text("no ledger here", encoding="utf-8")
            self.assertEqual(self.run_cli("validate", "--ledger", path).returncode, 2)


@unittest.skipUnless(shutil.which("pwsh") or os.environ.get("PWSH"), "PowerShell is required for the wrapper test")
class PowerShellWrapperTests(unittest.TestCase):
    def run_ps(self, *args):
        pwsh = os.environ.get("PWSH") or shutil.which("pwsh")
        return subprocess.run([pwsh, "-NoProfile", "-File", str(ROOT / "eng/verify-deprecations.ps1"), *map(str, args)],
                              capture_output=True, text=True, cwd=ROOT)

    def test_the_wrapper_propagates_pass_fail_and_invalid_input(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory) / "report.json"
            ok = self.run_ps("-Ledger", LEDGER_PATH, "-Output", out)
            self.assertEqual(ok.returncode, 0, ok.stdout + ok.stderr)
            self.assertEqual(json.loads(out.read_text())["overall"], "PASS")
            ledger = gated("remove", "4.0.0")
            failing = Path(directory) / "failing.md"
            failing.write_text(policy.render_ledger_block(ledger), encoding="utf-8")
            self.assertEqual(self.run_ps("-Ledger", failing, "-Output", out).returncode, 1)
            broken = Path(directory) / "broken.md"
            broken.write_text("nothing", encoding="utf-8")
            self.assertEqual(self.run_ps("-Ledger", broken, "-Output", out).returncode, 2)


if __name__ == "__main__":
    unittest.main()

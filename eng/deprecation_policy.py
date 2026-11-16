#!/usr/bin/env python3
"""Validate the Meridian deprecation lifecycle ledger (docs/deprecations.md) — the E7 release validation rule.

The ledger is one machine-readable JSON block inside docs/deprecations.md (between the begin/end markers). It holds
exactly four entries. This module parses it strictly, checks every recorded fact, checks the retained contracts
against the repository source and decides, per entry, whether the proposed action is allowed:

  retain_pass            guidance only; the old API/behavior stays supported (always allowed when the facts are valid)
  diagnostic_eligible    a new diagnostic / non-error Obsolete warning may be reviewed for the proposed version
  diagnostic_ineligible  a diagnostic/warning was proposed but a prerequisite is missing
  removal_eligible       a removal/default flip may be *reviewed* for the proposed major (no automatic change follows)
  removal_ineligible     a removal/default flip was proposed but a prerequisite is missing
  invalid_metadata       a recorded fact is inconsistent (future/sentinel dates, contradictions, widened scope, ...)

Eligibility rules (controller rulings in the E7 lifecycle architecture):
  * windowStart = max(reviewed stable-replacement availability, public announcement); calendarFloor = windowStart plus
    six calendar months (same instant of day, month end clamped; never 180/182/183 days).
  * Removal/default flip additionally needs two qualifying higher stable minor lines in the notice's major, released
    after the notice release and retaining both paths (patches, prereleases and majors count zero), a separately
    reviewed major above the notice major, a review/adoption reference and a trusted exact-action authorization.
  * A new diagnostic/Obsolete warning needs the reviewed replacement anchor, an already public announcement, accepted
    migration evidence, a review, a minor or major (never patch) version, IsError=false and trusted authorization.
  * A self-authored field never authorizes anything; authorization is checked by a trusted verifier bound to the
    exact ledger, entry, action, version, surface and candidate. This repository has no trusted verifier, so on the
    command line every gated action fails closed. The clock is always the real UTC clock on the command line.

Exit codes: 0 PASS, 1 FAIL (an ineligible gated action, invalid metadata or a broken retained contract), 2 invalid input.
"""

from __future__ import annotations

import argparse
import calendar
import datetime
import hashlib
import json
import re
import sys
from pathlib import Path

LEDGER_SCHEMA = "meridian-deprecation-ledger/1"
OUTPUT_SCHEMA = "meridian-deprecation-validation/1"
BEGIN_MARKER = "<!-- meridian-deprecation-ledger:begin -->"
END_MARKER = "<!-- meridian-deprecation-ledger:end -->"
EXPECTED_ENTRY_IDS = ("alias-addautomapper", "generated-legacy-skipped-members", "cache-raw-keys", "idempotency-nonatomic-concurrency")
DIAGNOSTIC_ACTIONS = ("add-diagnostic", "add-obsolete-warning")
REMOVAL_ACTIONS = ("remove", "flip-default")
ACTIONS = ("retain",) + DIAGNOSTIC_ACTIONS + REMOVAL_ACTIONS
OK_STATES = ("retain_pass", "diagnostic_eligible", "removal_eligible")
SUPPORT_STATUS = {"retain": "supported", "add-diagnostic": "supported-guided", "add-obsolete-warning": "supported-deprecated",
                  "remove": "removal-proposed", "flip-default": "default-flip-proposed"}
REQUIRED_EXCLUSIONS = (
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.AddClosedBehavior(System.Type,System.Type,System.Int32)",
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.AddClosedStreamBehavior(System.Type,System.Type)",
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.RegisterServicesFromAssembly(System.Reflection.Assembly)",
    "T:Meridian.Mediator.Behaviors.IIdempotencyStore",
)
REPO_ROOT = Path(__file__).resolve().parents[1]

LEDGER_KEYS = {"schema", "policyVersion", "policy", "retainedSupported", "releaseHistory", "entries"}
POLICY_KEYS = {"windowStart", "calendarMonths", "qualifyingMinorLines", "minorCounting", "warningGate", "removalGate", "clock", "authority"}
HISTORY_KEYS = {"packages", "limit"}
HISTORY_PACKAGE_KEYS = {"id", "authority", "retrievedAt", "responseSha256", "stableReleases"}
RELEASE_KEYS = {"version", "publishedAt", "retainsBothPaths"}
ENTRY_KEYS = {"id", "title", "kind", "package", "targets", "replacements", "retainedInvariants", "migration", "diagnostic",
              "facts", "proposed", "review", "supportStatus"}
MIGRATION_KEYS = {"guide", "fixtures"}
DIAGNOSTIC_KEYS = {"reservedId", "status", "existing"}
FACT_KEYS = {"firstStableReplacement", "verifiedAvailableBy", "announcement", "noticeRelease", "migrationEvidence",
             "diagnosticIntroducedIn", "obsoleteShippedIn"}
ANCHOR_KEYS = {"package", "version", "publishedAt", "retrievedAt", "authority", "responseSha256", "artifactSha256",
               "clockAnchorReviewed", "note"}
ANNOUNCEMENT_KEYS = {"url", "contentSha256", "publishedAt", "retrievedAt"}
NOTICE_KEYS = {"package", "version", "publishedAt"}
MIGRATION_EVIDENCE_KEYS = {"consumer", "resultsSha256", "acceptedAt"}
PROPOSED_KEYS = {"action", "version", "isError", "affectedSurface"}
REVIEW_KEYS = {"reference", "decidedAt"}
KINDS = ("api", "behavior", "usage-pattern")
DIAGNOSTIC_STATUSES = ("none", "reserved-not-emitted", "emitted")

UTC = datetime.timezone.utc
_TIMESTAMP = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,6}))?(?:Z|\+00:00)")
_IDENT = r"(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
_SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(" + _IDENT + r"(?:\." + _IDENT + r")*))?")
_HEX64 = re.compile(r"^[0-9a-f]{64}$")


class LedgerError(Exception):
    """The ledger cannot be read as a valid four-entry ledger (exit code 2)."""


# --------------------------------------------------------------------------------------------------------------------
# Parsing primitives
# --------------------------------------------------------------------------------------------------------------------

def parse_utc(text) -> datetime.datetime:
    """An explicit UTC instant (``Z`` or ``+00:00``); anything else is rejected."""
    match = _TIMESTAMP.fullmatch(text) if isinstance(text, str) else None
    if not match:
        raise ValueError(f"not an explicit UTC timestamp: {text!r}")
    year, month, day, hour, minute, second = (int(match.group(i)) for i in range(1, 7))
    fraction = (match.group(7) or "").ljust(6, "0")
    return datetime.datetime(year, month, day, hour, minute, second, int(fraction or 0), tzinfo=UTC)


def format_utc(value: datetime.datetime) -> str:
    return value.astimezone(UTC).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def add_calendar_months(value: datetime.datetime, months: int) -> datetime.datetime:
    """Calendar months like .NET DateTime.AddMonths: keep the time of day, clamp to the last valid day of the month."""
    index = value.month - 1 + months
    year, month = value.year + index // 12, index % 12 + 1
    day = min(value.day, calendar.monthrange(year, month)[1])
    return value.replace(year=year, month=month, day=day)


def parse_version(text):
    """Strict SemVer 2.0.0 without build metadata: (major, minor, patch, prerelease-or-None)."""
    match = _SEMVER.fullmatch(text) if isinstance(text, str) else None
    if not match:
        raise ValueError(f"not a canonical SemVer version without build metadata: {text!r}")
    return int(match.group(1)), int(match.group(2)), int(match.group(3)), match.group(4)


def _reject_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise LedgerError(f"duplicate JSON key {key!r} in the ledger")
        result[key] = value
    return result


def extract_ledger(markdown: str) -> dict:
    """Return the single JSON ledger between the markers. Exactly one marker pair and one ```json fence are allowed."""
    if markdown.count(BEGIN_MARKER) != 1 or markdown.count(END_MARKER) != 1:
        raise LedgerError("docs/deprecations.md must contain exactly one ledger begin marker and one end marker")
    start, end = markdown.index(BEGIN_MARKER) + len(BEGIN_MARKER), markdown.index(END_MARKER)
    if start > end:
        raise LedgerError("the ledger end marker precedes the begin marker")
    block = markdown[start:end].strip()
    if not block.startswith("```json\n") or not block.endswith("\n```") or block.count("```") != 2:
        raise LedgerError("the ledger markers must enclose exactly one ```json fenced block")
    try:
        return json.loads(block[len("```json\n"):-len("\n```")], object_pairs_hook=_reject_duplicate_keys)
    except json.JSONDecodeError as error:
        raise LedgerError(f"the ledger block is not valid JSON: {error}") from error


def render_ledger_block(ledger: dict) -> str:
    """A minimal ledger document (used by tests and by maintainers to regenerate the block)."""
    return f"{BEGIN_MARKER}\n```json\n{json.dumps(ledger, indent=2)}\n```\n{END_MARKER}\n"


def canonical_sha256(value) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def derived_support_status(action: str) -> str:
    return SUPPORT_STATUS[action]


# --------------------------------------------------------------------------------------------------------------------
# Structure (invalid structure is exit 2; a self-authored approval field is a structural error)
# --------------------------------------------------------------------------------------------------------------------

def _keys(value, allowed: set, label: str, required: set | None = None) -> None:
    if not isinstance(value, dict):
        raise LedgerError(f"{label} must be an object")
    if not all(isinstance(key, str) for key in value):
        raise LedgerError(f"{label} object keys must be strings")
    unknown = sorted(set(value) - allowed)
    if unknown:
        hint = " (a self-authored approval is never authority; authorization comes from a trusted verifier)" if any(
            "approv" in key.lower() for key in unknown) else ""
        raise LedgerError(f"{label} has unknown field(s) {unknown}{hint}")
    missing = sorted((required if required is not None else allowed) - set(value))
    if missing:
        raise LedgerError(f"{label} lacks field(s) {missing}")


def _optional(value, allowed: set, label: str) -> None:
    if value is not None:
        _keys(value, allowed, label)


def validate_structure(ledger) -> None:
    _keys(ledger, LEDGER_KEYS, "ledger")
    if ledger["schema"] != LEDGER_SCHEMA:
        raise LedgerError(f"ledger schema must be {LEDGER_SCHEMA}")
    if type(ledger["policyVersion"]) is not int or ledger["policyVersion"] != 1:
        raise LedgerError("ledger policyVersion must be 1")
    _keys(ledger["policy"], POLICY_KEYS, "policy")
    if any(type(ledger["policy"][key]) is not int or ledger["policy"][key] != expected for key, expected in
           (("calendarMonths", 6), ("qualifyingMinorLines", 2))):
        raise LedgerError("the policy thresholds are fixed: six calendar months and two qualifying minor lines")
    if not isinstance(ledger["retainedSupported"], list) or not all(isinstance(x, str) for x in ledger["retainedSupported"]):
        raise LedgerError("retainedSupported must be a list of symbols")
    missing = [x for x in REQUIRED_EXCLUSIONS if x not in ledger["retainedSupported"]]
    if missing:
        raise LedgerError(f"retainedSupported lacks required retained APIs {missing}")
    history = ledger["releaseHistory"]
    _keys(history, HISTORY_KEYS, "releaseHistory")
    if not isinstance(history["packages"], list):
        raise LedgerError("releaseHistory.packages must be a list")
    for package in history["packages"]:
        _keys(package, HISTORY_PACKAGE_KEYS, "releaseHistory package")
        if not isinstance(package["stableReleases"], list):
            raise LedgerError("stableReleases must be a list")
        for release in package["stableReleases"]:
            _keys(release, RELEASE_KEYS, f"release of {package['id']}")
    entries = ledger["entries"]
    if not isinstance(entries, list):
        raise LedgerError("entries must be a list")
    ids = [e.get("id") if isinstance(e, dict) else None for e in entries]
    if ids != list(EXPECTED_ENTRY_IDS):
        raise LedgerError(f"the ledger must hold exactly the four entries {list(EXPECTED_ENTRY_IDS)} in order, found {ids}")
    for item in entries:
        label = f"entry {item['id']}"
        _keys(item, ENTRY_KEYS, label)
        if not isinstance(item["package"], str) or not item["package"].strip():
            raise LedgerError(f"{label}.package must be a non-empty package identity")
        if item["kind"] not in KINDS:
            raise LedgerError(f"{label} has unknown kind {item['kind']!r}")
        for key in ("targets", "replacements", "retainedInvariants"):
            if not isinstance(item[key], list) or not item[key] or not all(isinstance(x, str) and x for x in item[key]):
                raise LedgerError(f"{label}.{key} must be a non-empty list of strings")
        _keys(item["migration"], MIGRATION_KEYS, f"{label}.migration")
        fixtures = item["migration"]["fixtures"]
        if not isinstance(fixtures, list) or not fixtures or not all(isinstance(x, str) and x for x in fixtures):
            raise LedgerError(f"{label}.migration.fixtures must be a non-empty list of fixture names")
        _keys(item["diagnostic"], DIAGNOSTIC_KEYS, f"{label}.diagnostic")
        for field in ("reservedId", "existing"):
            value = item["diagnostic"][field]
            if value is not None and (not isinstance(value, str) or not value):
                raise LedgerError(f"{label}.diagnostic.{field} must be a non-empty string or null")
        if item["diagnostic"]["status"] not in DIAGNOSTIC_STATUSES:
            raise LedgerError(f"{label}.diagnostic.status must be one of {DIAGNOSTIC_STATUSES}")
        facts = item["facts"]
        _keys(facts, FACT_KEYS, f"{label}.facts")
        _optional(facts["firstStableReplacement"], ANCHOR_KEYS, f"{label}.facts.firstStableReplacement")
        _optional(facts["verifiedAvailableBy"], ANCHOR_KEYS, f"{label}.facts.verifiedAvailableBy")
        _optional(facts["announcement"], ANNOUNCEMENT_KEYS, f"{label}.facts.announcement")
        _optional(facts["noticeRelease"], NOTICE_KEYS, f"{label}.facts.noticeRelease")
        _optional(facts["migrationEvidence"], MIGRATION_EVIDENCE_KEYS, f"{label}.facts.migrationEvidence")
        _keys(item["proposed"], PROPOSED_KEYS, f"{label}.proposed")
        surface = item["proposed"]["affectedSurface"]
        if surface is not None and (not isinstance(surface, list) or not all(isinstance(x, str) and x for x in surface)):
            raise LedgerError(f"{label}.proposed.affectedSurface must be a list of symbol strings or null")
        if item["proposed"]["action"] not in ACTIONS:
            raise LedgerError(f"{label}.proposed.action must be one of {ACTIONS}")
        _optional(item["review"], REVIEW_KEYS, f"{label}.review")


# --------------------------------------------------------------------------------------------------------------------
# Release history
# --------------------------------------------------------------------------------------------------------------------

def qualifying_minor_lines(notice: dict, releases: list) -> list:
    """Distinct higher stable minor lines of the notice major released after the notice, retaining both paths."""
    major, minor, _, _ = parse_version(notice["version"])
    notice_at = parse_utc(notice["publishedAt"])
    lines = set()
    for release in releases:
        r_major, r_minor, _, prerelease = parse_version(release["version"])
        if prerelease is not None or r_major != major or r_minor <= minor:
            continue
        if parse_utc(release["publishedAt"]) <= notice_at or release.get("retainsBothPaths") is not True:
            continue
        lines.add((r_major, r_minor))
    return [f"{a}.{b}" for a, b in sorted(lines)]


def _registration_releases(raw: bytes, package: dict, retrieved: datetime.datetime) -> dict:
    """Read the complete inline registration index whose exact bytes the ledger anchors.

    Registration metadata establishes publication/listing facts, not API retention.
    External pages need separately reviewed hashes and are deliberately rejected here.
    See https://learn.microsoft.com/en-us/nuget/api/registration-base-url-resource.
    """
    document = json.loads(raw, object_pairs_hook=_reject_duplicate_keys)
    if not isinstance(document, dict) or not isinstance(document.get("items"), list):
        raise ValueError("the retained response is not a registration index")
    if document.get("@id", package["authority"]) != package["authority"]:
        raise ValueError("registration index identity differs from the recorded authority")
    if type(document.get("count")) is not int or document["count"] != len(document["items"]):
        raise ValueError("registration index page count is inconsistent")
    releases, seen = {}, set()
    for page in document["items"]:
        if not isinstance(page, dict) or not isinstance(page.get("items"), list):
            raise ValueError("registration pages must be embedded in the hash-anchored response; external pages are unverified")
        if type(page.get("count")) is not int or page["count"] != len(page["items"]):
            raise ValueError("registration page leaf count is inconsistent")
        for leaf in page["items"]:
            catalog = leaf.get("catalogEntry") if isinstance(leaf, dict) else None
            if not isinstance(catalog, dict) or not isinstance(catalog.get("id"), str):
                raise ValueError("a registration leaf lacks its catalog identity")
            if catalog["id"].casefold() != package["id"].casefold():
                raise ValueError("a catalog entry belongs to another package")
            version = catalog.get("version")
            parsed_version = parse_version(version)
            if version in seen:
                raise ValueError(f"duplicate registration version {version}")
            seen.add(version)
            listed = catalog.get("listed", True)  # NuGet's documented default.
            if type(listed) is not bool:
                raise ValueError("catalog listed must be a boolean")
            if not listed or parsed_version[3] is not None:
                continue
            published = parse_utc(catalog.get("published"))
            if published.year < 1901 or published > retrieved:
                raise ValueError(f"invalid retained publication instant for {version}")
            releases[version] = published
    return releases


def _history(ledger: dict, evidence: list, now: datetime.datetime):
    """Corroborate every recorded stable release against the anchored registration contents."""
    raw_by_digest = {}
    verified, problems = {}, []
    for raw in evidence:
        if not isinstance(raw, bytes):
            problems.append("release-history evidence must contain raw response bytes")
            continue
        raw_by_digest[hashlib.sha256(raw).hexdigest()] = raw
    seen_packages = set()
    for package in ledger["releaseHistory"]["packages"]:
        label = f"release history of {package['id']}"
        try:
            if not isinstance(package["id"], str) or not package["id"]:
                raise ValueError("package identity must be a non-empty string")
            identity = package["id"].casefold()
            if identity in seen_packages:
                verified.pop(package["id"], None)
                raise ValueError("duplicate package history identity")
            seen_packages.add(identity)
            retrieved = parse_utc(package["retrievedAt"])
            if retrieved > now:
                raise ValueError(f"was retrieved in the future ({package['retrievedAt']})")
            projection = {}
            for release in package["stableReleases"]:
                version = parse_version(release["version"])
                if version[3] is not None:
                    raise ValueError(f"lists prerelease {release['version']} as stable")
                published = parse_utc(release["publishedAt"])
                if published.year < 1901:
                    raise ValueError(f"uses the unlisted 1900 sentinel for {release['version']}")
                if published > retrieved:
                    raise ValueError(f"records {release['version']} published after the retrieval")
                if release["retainsBothPaths"] is not None and type(release["retainsBothPaths"]) is not bool:
                    raise ValueError("retainsBothPaths must be true, false or null")
                if release["version"] in projection:
                    raise ValueError(f"duplicate declared release {release['version']}")
                projection[release["version"]] = published
            digest = package["responseSha256"]
            if not isinstance(digest, str) or not _HEX64.fullmatch(digest):
                raise ValueError("has no canonical response hash")
            if digest not in raw_by_digest:
                continue
            actual = _registration_releases(raw_by_digest[digest], package, retrieved)
            if projection != actual:
                raise ValueError("declared stable versions/publication dates differ from the retained registration contents")
            verified[package["id"]] = package["stableReleases"]
        except (ValueError, LedgerError) as error:
            problems.append(f"{label}: {error}")
    # A malformed/duplicate snapshot must never leave an entry eligible even if
    # another package projection happened to verify before that error.
    return ({} if problems else verified), problems


def _corroborate_transition_facts(ledger, item, verified_history, failed):
    histories = {p["id"]: p for p in ledger["releaseHistory"]["packages"] if isinstance(p["id"], str)}
    for name in ("firstStableReplacement", "verifiedAvailableBy", "noticeRelease"):
        fact = item["facts"][name]
        if fact is None or (name != "noticeRelease" and fact["clockAnchorReviewed"] is not True):
            continue
        package = fact["package"]
        if not isinstance(package, str) or package != item["package"]:
            failed.append(f"{name} must identify the entry's exact package")
            continue
        releases = verified_history.get(package, [])
        matched = [r for r in releases if r["version"] == fact["version"] and
                   parse_utc(r["publishedAt"]) == parse_utc(fact["publishedAt"])]
        if len(matched) != 1:
            failed.append(f"{name} package/version/publication is not corroborated by retained registration evidence")
        if name != "noticeRelease" and (package not in histories or
                                         fact["responseSha256"] != histories[package]["responseSha256"]):
            failed.append(f"{name} is not bound to the verified registration response hash")


# --------------------------------------------------------------------------------------------------------------------
# Entry evaluation
# --------------------------------------------------------------------------------------------------------------------

def authorization_binding(ledger: dict, item: dict, candidate_sha256) -> dict:
    """What a trusted exact-action authorization must be bound to."""
    proposed = item["proposed"]
    return {"entryId": item["id"], "action": proposed["action"], "version": proposed["version"],
            "affectedSurface": sorted(proposed["affectedSurface"] or []), "ledgerSha256": canonical_sha256(ledger),
            "candidateSha256": candidate_sha256}


def _instant(value, label: str, now: datetime.datetime, problems: list):
    try:
        instant = parse_utc(value)
    except ValueError as error:
        problems.append(f"{label}: {error}")
        return None
    if instant.year < 1901:
        problems.append(f"{label} uses the unlisted 1900 sentinel")
    if instant > now:
        problems.append(f"{label} is in the future ({value})")
    return instant


def _metadata(item: dict, now: datetime.datetime) -> tuple:
    """(problems, anchor instant or None, announcement instant or None)."""
    problems: list = []
    facts = item["facts"]
    anchor_at = announcement_at = None
    first_known = []
    for key in ("firstStableReplacement", "verifiedAvailableBy"):
        anchor = facts[key]
        if anchor is None:
            continue
        published = _instant(anchor["publishedAt"], f"{key}.publishedAt", now, problems)
        if published is not None:
            first_known.append(published)
        retrieved = _instant(anchor["retrievedAt"], f"{key}.retrievedAt", now, problems)
        if published and retrieved and retrieved < published:
            problems.append(f"{key} was retrieved before it was published")
        try:
            if parse_version(anchor["version"])[3] is not None:
                problems.append(f"{key} {anchor['version']} is a prerelease, not stable evidence")
        except ValueError as error:
            problems.append(f"{key}: {error}")
        if not (_HEX64.fullmatch(str(anchor["responseSha256"])) and _HEX64.fullmatch(str(anchor["artifactSha256"]))):
            problems.append(f"{key} lacks archived response/artifact hashes")
        if type(anchor["clockAnchorReviewed"]) is not bool:
            problems.append(f"{key}.clockAnchorReviewed must be a boolean")
        if anchor["clockAnchorReviewed"] is True:
            anchor_at = published if anchor_at is None or (published and published < anchor_at) else anchor_at
    announcement = facts["announcement"]
    if announcement is not None:
        announcement_at = _instant(announcement["publishedAt"], "announcement.publishedAt", now, problems)
        retrieved = _instant(announcement["retrievedAt"], "announcement.retrievedAt", now, problems)
        if announcement_at and retrieved and retrieved < announcement_at:
            problems.append("the announcement was retrieved before it was published")
        if not _HEX64.fullmatch(str(announcement["contentSha256"])):
            problems.append("the announcement content hash is not recorded")
        if announcement_at and first_known and announcement_at < min(first_known):
            problems.append("the announcement predates the public replacement artifact it announces")
    if facts["noticeRelease"] is not None:
        _instant(facts["noticeRelease"]["publishedAt"], "noticeRelease.publishedAt", now, problems)
        try:
            if parse_version(facts["noticeRelease"]["version"])[3] is not None:
                problems.append("the notice release must be a stable release")
        except ValueError as error:
            problems.append(f"noticeRelease: {error}")
    if facts["migrationEvidence"] is not None:
        migration = facts["migrationEvidence"]
        _instant(migration["acceptedAt"], "migrationEvidence.acceptedAt", now, problems)
        if migration["consumer"] != "Meridian.DeprecationConsumer" or not _HEX64.fullmatch(str(migration["resultsSha256"])):
            problems.append("migrationEvidence must identify the old/new consumer and its exact accepted results SHA-256")
    if item["review"] is not None:
        _instant(item["review"]["decidedAt"], "review.decidedAt", now, problems)
        reference = item["review"]["reference"]
        if not isinstance(reference, str) or not reference.strip():
            problems.append("review must identify a non-empty accepted decision reference")
    action = item["proposed"]["action"]
    if item["supportStatus"] != derived_support_status(action):
        problems.append(f"supportStatus {item['supportStatus']!r} contradicts the proposed action {action!r} "
                        f"(expected {derived_support_status(action)!r})")
    return problems, anchor_at, announcement_at


def _latest_stable_before(releases: list, now: datetime.datetime):
    stable = [r for r in releases if parse_version(r["version"])[3] is None and parse_utc(r["publishedAt"]) <= now]
    return max((parse_version(r["version"])[:3] for r in stable), default=None)


def _authorized(ledger, item, authorization, verifier, candidate_sha256, failed: list) -> None:
    if not isinstance(candidate_sha256, str) or not _HEX64.fullmatch(candidate_sha256):
        failed.append("a gated action requires the exact candidate SHA-256")
        return
    if verifier is None:
        failed.append("no trusted authorization verifier is available in this repository; a self-authored record is not authority")
        return
    if authorization is None:
        failed.append("no exact-action authorization record was supplied")
        return
    binding = authorization_binding(ledger, item, candidate_sha256)
    verify_action = getattr(verifier, "verify", None)
    if not callable(verify_action) or verify_action(authorization, binding) is not True:
        failed.append("the authorization is not trusted for this exact ledger, entry, action, version, surface and candidate")
        return
    # Action approval and acceptance of its supporting artifacts are separate
    # responsibilities of the trusted controller. Neither is implemented by this CLI.
    accept_evidence = getattr(verifier, "verify_evidence", None)
    evidence_binding = {"authorizationBinding": binding, "migrationEvidence": item["facts"]["migrationEvidence"],
                        "review": item["review"], "availability": {k: item["facts"][k] for k in
                        ("firstStableReplacement", "verifiedAvailableBy", "announcement", "noticeRelease")},
                        "releaseHistory": ledger["releaseHistory"]}
    if not callable(accept_evidence) or accept_evidence(json.loads(json.dumps(evidence_binding))) is not True:
        failed.append("no trusted acceptance of the bound migration, review, announcement, replacement and retention evidence")


def evaluate_entry(ledger: dict, item: dict, now: datetime.datetime, verified_history: dict, history_problems: list,
                   authorization, verifier, candidate_sha256) -> dict:
    proposed = item["proposed"]
    action = proposed["action"]
    result = {"id": item["id"], "proposedAction": action, "state": "invalid_metadata", "failedPrerequisites": [],
              "derived": {"windowStart": None, "calendarFloor": None, "qualifyingMinorLines": []}}
    problems, anchor_at, announcement_at = _metadata(item, now)
    if action != "retain":
        surface = set(proposed["affectedSurface"] or [])
        retained = surface & set(ledger["retainedSupported"])
        if retained:
            problems.append(f"the proposed {action} touches retained supported API(s) {sorted(retained)}")
        if not surface:
            problems.append("a gated action must name its exact affected surface")
        elif not surface <= set(item["targets"]):
            problems.append("the affected surface is wider than the entry's declared targets")
    if problems:
        result["failedPrerequisites"] = problems
        return result
    if action == "retain":
        result["state"] = "retain_pass"
        return result

    failed: list = []
    facts = item["facts"]
    if anchor_at is None:
        failed.append("no reviewed stable-replacement availability anchor (first stable or reviewed verified-available-by)")
    if announcement_at is None:
        failed.append("no public announcement of the transition")
    if facts["migrationEvidence"] is None:
        failed.append("no accepted old/new migration evidence")
    if item["review"] is None:
        failed.append("no review/adoption decision reference")
    releases = verified_history.get(item["package"])
    if releases is None:
        failed.append(f"the release history of {item['package']} is not verified against its raw evidence")
        failed.extend(history_problems)
    _corroborate_transition_facts(ledger, item, verified_history, failed)
    try:
        version = parse_version(proposed["version"])
    except ValueError as error:
        version = None
        failed.append(f"proposed version: {error}")
    if version is not None and version[3] is not None:
        failed.append("a deprecation/removal must ship in a stable release")

    if action in DIAGNOSTIC_ACTIONS:
        if version is not None and releases is not None:
            latest = _latest_stable_before(releases, now)
            if latest is not None and (version[0], version[1]) <= (latest[0], latest[1]):
                failed.append(f"a patch or older line cannot introduce a deprecation (proposed {proposed['version']}, latest stable "
                              f"{'.'.join(map(str, latest))})")
        if proposed["isError"] is not False:
            failed.append("a new deprecation diagnostic or Obsolete warning must be IsError=false")
        if not item["diagnostic"]["reservedId"]:
            failed.append("a new deprecation diagnostic or Obsolete warning needs a reserved diagnostic id")
        _authorized(ledger, item, authorization, verifier, candidate_sha256, failed)
        result["state"] = "diagnostic_ineligible" if failed else "diagnostic_eligible"
        result["failedPrerequisites"] = failed
        return result

    notice = facts["noticeRelease"]
    if notice is None:
        failed.append("no stable release carrying the transition notice")
    if anchor_at is not None and announcement_at is not None:
        window_start = max(anchor_at, announcement_at)
        floor = add_calendar_months(window_start, 6)
        result["derived"]["windowStart"], result["derived"]["calendarFloor"] = format_utc(window_start), format_utc(floor)
        if now < floor:
            failed.append(f"six calendar months have not elapsed since {format_utc(window_start)} (floor {format_utc(floor)})")
    if notice is not None and releases is not None:
        visible = [r for r in releases if parse_utc(r["publishedAt"]) <= now]
        lines = qualifying_minor_lines(notice, visible)
        result["derived"]["qualifyingMinorLines"] = lines
        if len(lines) < 2:
            failed.append(f"two qualifying higher stable minor lines in the notice major are required, found {lines}")
        if version is not None:
            notice_major = parse_version(notice["version"])[0]
            if not (version[0] > notice_major and version[1] == 0 and version[2] == 0):
                failed.append(f"removal/default flip needs a separately reviewed major above {notice_major} (proposed {proposed['version']})")
    _authorized(ledger, item, authorization, verifier, candidate_sha256, failed)
    result["state"] = "removal_ineligible" if failed else "removal_eligible"
    result["failedPrerequisites"] = failed
    return result


# --------------------------------------------------------------------------------------------------------------------
# Retained contracts in the repository source
# --------------------------------------------------------------------------------------------------------------------

SOURCE_CONTRACTS = (
    ("src/Meridian.Mapping/Extensions/ServiceCollectionExtensions.cs", (
        "public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Assembly[] assemblies)",
        "public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)",
        "public static IServiceCollection AddAutoMapper(this IServiceCollection services, Action<IMapperConfigurationExpression> configure)",
        "public static IServiceCollection AddMeridianMapping(this IServiceCollection services, params Assembly[] assemblies)",
        "public static IServiceCollection AddMeridianMapping(this IServiceCollection services, params Type[] markerTypes)",
        "public static IServiceCollection AddMeridianMapping(this IServiceCollection services, Action<IMapperConfigurationExpression> configure)")),
    ("src/Meridian.Mapping.Generators/MeridianMapperGenerator.cs", (
        "public GeneratedMappingMode Mode { get; set; } = GeneratedMappingMode.Legacy;",)),
    ("src/Meridian.Mediator/Behaviors/IdempotencyBehavior.cs", (
        "public interface IIdempotencyStore",
        "public interface IAtomicIdempotencyStore : IIdempotencyStore")),
    ("src/Meridian.Mediator/Behaviors/CachingBehavior.cs", (
        "public bool UseRequestTypeNamespacedKeys { get; init; } = true;",)),
    ("src/Meridian.Mediator/Extensions/MeridianMediatorConfiguration.cs", (
        "public MeridianMediatorConfiguration RegisterServicesFromAssembly(Assembly assembly)",
        "public MeridianMediatorConfiguration AddClosedBehavior(Type closedBehaviorServiceType, Type behaviorType, int order = 0)",
        "public MeridianMediatorConfiguration AddClosedStreamBehavior(Type closedStreamBehaviorServiceType, Type behaviorType)")),
)


_CS_TOKENS = re.compile(
    r'//[^\n]*|/\*[\s\S]*?\*/|^[ \t]*\#[^\n]*|'
    r'\$*(?P<raw>"{3,})[\s\S]*?(?P=raw)|'
    r'(?:@\$*|\$+@)"(?:""|[^"])*"|'
    r'\$*"(?:\\.|[^"\\\n])*"|'
    r"'(?:\\.|[^'\\])*'|"
    r'[A-Za-z_][A-Za-z_0-9]*|[^\s]', re.MULTILINE)


def _csharp_literal(token: str) -> bool:
    return token.startswith(('"', "'", '@"', '@$"', '$@"')) or token.lstrip("$").startswith('"')


def _csharp_tokens(text: str) -> list:
    return ["<literal>" if _csharp_literal(match.group()) else match.group()
            for match in _CS_TOKENS.finditer(text)
            if not match.group().startswith(("//", "/*")) and not match.group().lstrip().startswith("#")]


def _obsolete_aliases(tokens: list, global_only=False) -> set:
    aliases = set()
    for index, token in enumerate(tokens):
        if token != "using" or index + 2 >= len(tokens) or tokens[index + 2] != "=":
            continue
        if global_only and (index == 0 or tokens[index - 1] != "global"):
            continue
        end = index + 3
        while end < len(tokens) and tokens[end] != ";":
            end += 1
        target = "".join(tokens[index + 3:end]).removeprefix("global::")
        if target in ("System.Obsolete", "System.ObsoleteAttribute"):
            aliases.add(tokens[index + 1])
    return aliases


def _csharp_code(text: str) -> str:
    """Mask comments, directives and literals while preserving declaration offsets."""
    masked, previous = [], 0
    for match in _CS_TOKENS.finditer(text):
        token = match.group()
        masked.append(text[previous:match.start()])
        if token.startswith(("//", "/*")) or _csharp_literal(token) or token.lstrip().startswith("#"):
            masked.append("".join("\n" if char == "\n" else " " for char in token))
        else:
            masked.append(token)
        previous = match.end()
    masked.append(text[previous:])
    return "".join(masked)


def _generated_attribute_source(text: str):
    """Extract the generator's named verbatim constant, rather than arbitrary literals."""
    tokens = [match.group() for match in _CS_TOKENS.finditer(text)
              if not match.group().startswith(("//", "/*")) and not match.group().lstrip().startswith("#")]
    declaration = ["private", "const", "string", "AttributeSource", "="]
    values = []
    for index in range(len(tokens) - len(declaration) - 1):
        if tokens[index:index + len(declaration)] == declaration:
            value, terminator = tokens[index + len(declaration):index + len(declaration) + 2]
            if value.startswith('@"') and value.endswith('"') and terminator == ";":
                values.append(value[2:-1].replace('""', '"'))
    return values[0] if len(values) == 1 else None


def _has_obsolete_attribute(prefix: str, global_aliases=()) -> bool:
    """Inspect attached attribute groups, skipping comments and string contents.

    This bounded source guard complements the actual warnings-as-errors package
    consumer; it is not a substitute for the C# compiler.
    """
    tokens = _csharp_tokens(prefix)
    obsolete = {"Obsolete", "ObsoleteAttribute"} | set(global_aliases)
    # Resolve simple source-level aliases to System.ObsoleteAttribute, including
    # global:: qualification. Alias chains are intentionally not guessed.
    obsolete.update(_obsolete_aliases(tokens))
    cursor = len(tokens) - 1
    while cursor >= 0 and tokens[cursor] == "]":
        end, depth = cursor, 1
        cursor -= 1
        while cursor >= 0 and depth:
            depth += (tokens[cursor] == "]") - (tokens[cursor] == "[")
            cursor -= 1
        if depth:
            return True  # Malformed attached metadata fails closed.
        group = tokens[cursor + 2:end]
        # At top-level commas each segment begins a new attribute name.
        name, nesting = [], 0
        for token in group + [","]:
            if token == "," and nesting == 0:
                identifiers = [t for t in name if re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", t)]
                if identifiers and identifiers[-1] in obsolete:
                    return True
                name = []
            elif token == "(":
                nesting += 1
            elif token == ")":
                nesting -= 1
            elif nesting == 0:
                name.append(token)
    return False


def check_source(repo: Path, ledger: dict) -> dict:
    """Retained supported contracts must still exist, unchanged in default and without Obsolete metadata."""
    problems: list = []
    sources = [p for p in Path(repo).glob("src/**/*.cs") if "/obj/" not in p.as_posix() and "/bin/" not in p.as_posix()]
    texts = {p: p.read_text(encoding="utf-8", errors="replace") for p in sources}
    global_aliases = {}
    for path, text in texts.items():
        # Global aliases apply within their project, never to unrelated packages.
        project = path.relative_to(repo).parts[1]
        global_aliases.setdefault(project, set()).update(_obsolete_aliases(_csharp_tokens(text), global_only=True))
    for relative, signatures in SOURCE_CONTRACTS:
        path = Path(repo) / relative
        if not path.is_file():
            problems.append(f"{relative} is missing")
            continue
        text = path.read_text(encoding="utf-8")
        if relative == "src/Meridian.Mapping.Generators/MeridianMapperGenerator.cs":
            # This contract is intentionally emitted by a generator constant.
            # Its generated code gets the same comment/literal masking as source.
            text = _generated_attribute_source(text)
            if text is None:
                problems.append(f"{relative}: the unique generated AttributeSource constant is missing")
                continue
        lines = text.splitlines()
        stripped = [line.strip() for line in _csharp_code(text).splitlines()]
        for signature in signatures:
            matches = [i for i, line in enumerate(stripped) if line.startswith(signature)]
            if len(matches) != 1:
                problems.append(f"{relative}: expected exactly one retained declaration '{signature}', found {len(matches)}")
                continue
            prefix = "\n".join(lines[:matches[0]])
            if _has_obsolete_attribute(prefix, global_aliases.get(Path(relative).parts[1], ())):
                problems.append(f"{relative}: '{signature}' carries Obsolete metadata, but the ledger keeps it supported")
    for item in ledger["entries"]:
        diagnostic = item["diagnostic"]
        reserved, status = diagnostic["reservedId"], diagnostic["status"]
        if reserved:
            used = sorted(str(p.relative_to(repo)) for p, text in texts.items() if reserved in text)
            if status != "emitted" and used:
                problems.append(f"{reserved} appears in {used}, but entry {item['id']} records it as {status}")
            if status == "emitted" and not used:
                problems.append(f"{reserved} is recorded as emitted but no source emits it")
        if diagnostic["existing"] and not any(diagnostic["existing"] in text for text in texts.values()):
            problems.append(f"entry {item['id']} relies on existing diagnostic {diagnostic['existing']}, which no source defines")
    return {"status": "FAIL" if problems else "PASS", "problems": problems}


# --------------------------------------------------------------------------------------------------------------------
# Old/new consumer evidence
# --------------------------------------------------------------------------------------------------------------------

def evaluate_consumer_evidence(rows, consumer: str = "Meridian.DeprecationConsumer", required_fixtures=()) -> dict:
    """Compare semantic results. E4 separately verifies candidate/source/binary/runtime execution provenance."""
    def report(status, lanes, problems):
        return {"status": status, "lanes": lanes, "problems": problems, "provenanceVerified": False}

    if rows is None:
        return report("pending", 0, ["no old/new consumer rows were supplied"])
    if not isinstance(rows, list):
        return report("failed", 0, ["consumer results must be a list of row objects"])
    relevant, problems = [], []
    for index, row in enumerate(rows):
        if not isinstance(row, dict) or not isinstance(row.get("consumer"), str):
            problems.append(f"consumer result row {index} is malformed")
        elif row["consumer"] == consumer:
            relevant.append(row)
    if not relevant:
        return report("failed" if problems else "pending", 0, problems or ["no matching old/new consumer rows were supplied"])
    lanes: dict = {}
    versions = set()
    for row in relevant:
        framework, sdk, variant = (row.get(k) for k in ("framework", "sdkVersion", "variant"))
        if framework not in ("net8.0", "net9.0", "net10.0") or not isinstance(sdk, str) or variant not in ("legacy", "migrated"):
            problems.append("a matching consumer row has an invalid framework, SDK or variant identity")
            continue
        try:
            if parse_version(sdk)[3] is not None:
                raise ValueError("a consumer SDK must be stable")
            parse_version(row.get("packageVersion"))
        except ValueError as error:
            problems.append(f"invalid consumer execution version: {error}")
            continue
        versions.add(row["packageVersion"])
        variants = lanes.setdefault((framework, sdk), {})
        if variant in variants:
            problems.append(f"duplicate consumer execution identity {framework}/SDK {sdk}/{variant}")
            continue
        variants[variant] = row
        if type(row.get("exit")) is not int or row["exit"] != 0:
            problems.append(f"{framework}/SDK {sdk}/{variant} must have an integer exit 0")
        common = row.get("commonOutput")
        if not isinstance(common, list) or not common or not all(
                isinstance(line, str) and line.startswith("COMMON ") and line.splitlines() == [line] for line in common):
            problems.append(f"{framework}/SDK {sdk}/{variant} must retain complete COMMON output lines")
            continue
        if len(common) != len(set(common)):
            problems.append(f"{framework}/SDK {sdk}/{variant} repeats a COMMON contract line")
        observed = [match.group(1) for line in common if (match := re.match(r"^COMMON fixture=([A-Za-z0-9_-]+)(?:\s|$)", line))]
        missing = set(required_fixtures) - set(observed)
        if missing or len(observed) != len(set(observed)):
            problems.append(f"{framework}/SDK {sdk}/{variant} has missing or repeated fixtures: {sorted(missing)}")
    if len(versions) != 1:
        problems.append("old/new consumer rows must identify one exact package version")
    for (framework, sdk), variants in sorted(lanes.items(), key=lambda kv: (str(kv[0][0]), str(kv[0][1]))):
        label = f"{framework}/SDK {sdk}"
        if set(variants) != {"legacy", "migrated"}:
            problems.append(f"{label} lacks a variant (found {sorted(variants)})")
            continue
        legacy, migrated = (variants[name].get("commonOutput") for name in ("legacy", "migrated"))
        if not isinstance(legacy, list) or not isinstance(migrated, list) or not all(
                isinstance(line, str) for line in legacy + migrated):
            continue  # The malformed output was recorded above, before any sorting.
        if not legacy or sorted(legacy) != sorted(migrated):
            problems.append(f"{label} old/new COMMON results differ or are empty")
    return report("failed" if problems else "matched", len(lanes), problems)


# --------------------------------------------------------------------------------------------------------------------
# Evaluation and command line
# --------------------------------------------------------------------------------------------------------------------

def evaluate(ledger: dict, now: datetime.datetime, repo=None, release_history_evidence=(), authorization=None, verifier=None,
             candidate_sha256=None, consumer_rows=None, ledger_sha256=None, consumer_results_sha256=None) -> dict:
    validate_structure(ledger)
    if now.tzinfo is None:
        raise ValueError("the evaluation clock must be timezone-aware UTC")
    verified, history_problems = _history(ledger, list(release_history_evidence or ()), now)
    entries = [evaluate_entry(ledger, item, now, verified, history_problems, authorization, verifier, candidate_sha256)
               for item in ledger["entries"]]
    source = check_source(Path(repo), ledger) if repo is not None else {"status": "not-checked", "problems": []}
    required_fixtures = {name for item in ledger["entries"] for name in item["migration"]["fixtures"]}
    required_fixtures.add("retained-dynamic-registration")
    consumers = evaluate_consumer_evidence(consumer_rows, required_fixtures=required_fixtures)
    failed = (any(e["state"] not in OK_STATES for e in entries) or source["status"] == "FAIL" or consumers["status"] == "failed"
              or bool(history_problems))
    return {"schema": OUTPUT_SCHEMA, "checkedAtUtc": format_utc(now), "ledgerSha256": ledger_sha256,
            "ledgerContentSha256": canonical_sha256(ledger), "policyVersion": ledger["policyVersion"],
            "candidateSha256": candidate_sha256, "candidateKind": "package-manifest" if candidate_sha256 else None,
            "consumerResultsSha256": consumer_results_sha256, "overall": "FAIL" if failed else "PASS", "entries": entries,
            "releaseHistory": {"verifiedPackages": sorted(verified), "problems": history_problems},
            "sourceInvariants": source, "consumerEvidence": consumers}


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    validate = commands.add_parser("validate", help="validate the ledger with the real UTC clock")
    validate.add_argument("--ledger", type=Path, default=REPO_ROOT / "docs" / "deprecations.md")
    validate.add_argument("--repo", type=Path, default=REPO_ROOT, help="repository whose source carries the retained contracts")
    validate.add_argument("--output", type=Path)
    validate.add_argument("--candidate", type=Path, help="immutable package-manifest.<version>.json whose exact bytes bind this lifecycle report")
    validate.add_argument("--authorization", type=Path, help="exact-action authorization record (needs a trusted verifier)")
    validate.add_argument("--release-history-evidence", type=Path, action="append", default=[],
                          help="raw registration response whose SHA-256 must equal a releaseHistory responseSha256")
    validate.add_argument("--consumer-results", type=Path, help="consumer-results.json of an executed package-consumer run")
    args = parser.parse_args(argv)
    try:
        raw = args.ledger.read_bytes()
        text = raw.decode("utf-8")
        ledger = extract_ledger(text)
        authorization = json.loads(args.authorization.read_text(encoding="utf-8"), object_pairs_hook=_reject_duplicate_keys) if args.authorization else None
        candidate = None
        if args.candidate:
            candidate_raw = args.candidate.read_bytes()
            record = json.loads(candidate_raw.decode("utf-8-sig"), object_pairs_hook=_reject_duplicate_keys)
            if (not isinstance(record, dict) or record.get("sourceDirty") is not False
                    or not re.fullmatch(r"[0-9a-f]{40}", str(record.get("sourceCommit", "")))
                    or not re.fullmatch(r"[0-9a-f]{64}", str(record.get("manifestSha256", "")))
                    or not isinstance(record.get("packages"), list) or not record["packages"]):
                raise LedgerError("candidate must be an immutable package manifest with explicit clean source and package records")
            parse_version(record.get("candidateVersion"))
            ids = [item.get("id") for item in record["packages"] if isinstance(item, dict)]
            if (len(ids) != len(record["packages"]) or not all(isinstance(name, str) and name for name in ids)
                    or len(ids) != len(set(ids)) or any(not re.fullmatch(r"[0-9a-f]{64}", str(item.get("sha256", ""))) for item in record["packages"])):
                raise LedgerError("candidate package manifest has malformed or duplicate package identities/hashes")
            if any(item["proposed"]["action"] != "retain" and item["proposed"]["version"] != record["candidateVersion"] for item in ledger["entries"]):
                raise LedgerError("a proposed deprecation action targets another package version")
            candidate = hashlib.sha256(candidate_raw).hexdigest()
        evidence = [p.read_bytes() for p in args.release_history_evidence]
        rows_raw = args.consumer_results.read_bytes() if args.consumer_results else None
        rows = json.loads(rows_raw.decode("utf-8"), object_pairs_hook=_reject_duplicate_keys) if rows_raw is not None else None
        if args.candidate and isinstance(rows, list) and any(isinstance(row, dict) and row.get("consumer") == "Meridian.DeprecationConsumer"
                and row.get("packageVersion") != record["candidateVersion"] for row in rows):
            raise LedgerError("consumer evidence identifies another package-manifest version")
        # The command line never accepts a clock and has no trusted verifier: gated actions fail closed here.
        report = evaluate(ledger, datetime.datetime.now(UTC), repo=args.repo, release_history_evidence=evidence,
                          authorization=authorization, verifier=None, candidate_sha256=candidate, consumer_rows=rows,
                          ledger_sha256=hashlib.sha256(raw).hexdigest(),
                          consumer_results_sha256=hashlib.sha256(rows_raw).hexdigest() if rows_raw is not None else None)
    except (OSError, ValueError, UnicodeDecodeError, LedgerError) as error:
        print(f"deprecation ledger input error: {error}", file=sys.stderr)
        return 2
    report["clock"] = "system-utc"
    report["ledger"] = str(args.ledger)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for item in report["entries"]:
        print(f"{item['id']}: {item['proposedAction']} -> {item['state']}" +
              (f" ({'; '.join(item['failedPrerequisites'])})" if item["failedPrerequisites"] else ""))
    print(f"source invariants: {report['sourceInvariants']['status']}; consumer evidence: {report['consumerEvidence']['status']}")
    print(f"DEPRECATION LEDGER {report['overall']}")
    return 0 if report["overall"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())

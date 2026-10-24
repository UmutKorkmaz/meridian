#!/usr/bin/env python3
"""Prepare, verify and plan the promotion of an exact Meridian release candidate (E5 release tools).

This module never builds, packs, renames, re-signs or rewrites a package, and it never changes remote state. It adopts
the feed that eng/pack-packages.ps1 already produced once, binds it to a clean source commit S and to the required
evidence, freezes everything into an immutable candidate directory described by ``candidate.json``, and plans a
promotion from observed remote state. Executing a promotion needs TRUSTED authorization tied to the candidate digest,
repository, version, targets and source commit. No trusted verifier exists in this repository, so the command line
cannot execute a promotion: ``execute`` fails closed with exit code 3 after its integrity checks.

Subcommands (the PowerShell wrappers eng/prepare-release.ps1 and eng/promote-release.ps1 call these):

  prepare        adopt a once-packed feed into a NEW candidate directory (never an occupied one)
  verify         verify a candidate directory against the exact SHA-256 of its candidate.json bytes
  plan           read-only promotion plan from observed remote state (GET requests only; --offline reads nothing)
  execute        fails closed: no trusted authorization verifier exists here (exit 3)
  compare-nuget  compare an approved package with a downloaded one under the repository-signing rule

Exit codes
  0  prepared / verified / plan produced with no stop condition / comparison equal
  1  failed validation: integrity, identity, collision, stop condition, corrupted receipts or a lost bundle
  2  invalid arguments (including a stable version without explicit release-preparation intent)
  3  BLOCKED: fail-closed missing evidence, governance, authorization, lock or unobservable/uncertain remote state

A candidate is ``prepared`` only when every required evidence row passes. A failed or blocked attempt is moved to a
quarantine directory next to the requested one and is never resumed: a new attempt gets a new candidate id. The
approval anchor is the SHA-256 of the exact candidate.json bytes; it is printed and recorded OUTSIDE the bundle and is
never stored inside candidate.json. Hashes establish integrity against an independently approved digest, not
authenticity.

The test seams (``source_provider``, ``gate_evaluator``, ``deprecation_runner``, an injected write transport,
authorization verifier, signature verifier and lifecycle revalidator) exist only in the Python API used by
eng/tests/test_release_candidate.py. The command line always uses git, eng/candidate_gate.py, the E7 validator
eng/verify-deprecations.ps1 and, for planning, a GET-only HTTPS transport.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import io
import json
import os
import re
import secrets
import shutil
import socket
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
import zlib
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath

EXIT_OK, EXIT_FAIL, EXIT_USAGE, EXIT_BLOCKED = 0, 1, 2, 3
CANDIDATE_SCHEMA = "meridian-release-candidate/1"
PREPARATION_SCHEMA = "meridian-release-preparation/1"
VERIFY_SCHEMA = "meridian-candidate-verification/1"
PLAN_SCHEMA = "meridian-promotion-plan/1"
EXECUTION_SCHEMA = "meridian-promotion-execution/1"
RECEIPT_SCHEMA = "meridian-promotion-receipt/1"
RELEASE_RECEIPT_SCHEMA = "meridian-release-receipt/1"
AVAILABILITY_SCHEMA = "meridian-remote-availability/1"
ANCHOR_SCHEMA = "meridian-approval-anchor/1"
DEPRECATION_SCHEMA = "meridian-deprecation-validation/1"
GATE_SUMMARY_SCHEMA = "meridian-candidate-summary/2"
EVIDENCE_POLICY_VERSION = 2
DEFAULT_REPOSITORY = "UmutKorkmaz/meridian"
SIGNATURE_ENTRY = ".signature.p7s"
RECEIPT_STATES = ("absent", "requested", "present-unverified", "verified", "failed", "uncertain")
E7_OK_STATES = ("retain_pass", "diagnostic_eligible", "removal_eligible")
FILE_ROLES = ("source-archive", "source-file-list", "inventory-snapshot", "tooling", "package-manifest", "package", "symbols",
              "evidence", "receipt", "metadata")
ACCEPTED_RELEASE_CLASS = "major"
RELEASE_CLASS_RATIONALE = (
    "E5 release architecture controller ruling: the reviewed scope changes established defaults (authorization continue -> "
    "reject, cache/idempotency namespaces, enum exception type, telemetry privacy, mixed pipeline order/deduplication, "
    "unresolved required constructor arguments). Opt-in compatibility flags do not make the defaults unchanged, so the "
    "candidate must start the next major line after the published 2.x baseline. 3.0.0 is a proposal until a fresh "
    "read-only availability check; a major number alone authorizes no removal.")
EXECUTION_NOTE = ("Execution (tag, GitHub release, NuGet push) requires trusted authorization bound to the candidate digest, "
                  "repository, version, targets and source commit. No trusted verifier exists in this repository, so execution "
                  "is unavailable and fails closed; a self-authored approval document or possession of the digest is not authority.")
SYMBOLS_LIMIT = ("NuGet exposes no retrievable symbol archive; symbol uploads are tracked separately and can at most be "
                 "present-unverified here. Use the supported symbol-server verification route; no remote byte proof is invented.")
GITHUB_API = "https://api.github.com"
NUGET_FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")
INT32_MAX = 2147483647
ZERO = "0" * 64
REPO_ROOT = Path(__file__).resolve().parents[1]
_VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")
_WINDOWS_RESERVED = re.compile(r"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$", re.IGNORECASE)


class UsageError(Exception):
    """Invalid arguments (exit code 2)."""


class VersionError(UsageError):
    """Not a canonical NuGet SemVer2 release identity."""


class ArchiveError(Exception):
    """An archive or nuspec is unreadable or ambiguous."""


class SourceError(Exception):
    """The source identity could not be read."""


class ReceiptError(Exception):
    """The receipt stream cannot be appended safely."""


class TransportError(Exception):
    """The provider answered with an explicit failure (the request did not take effect)."""


class TransportConflict(TransportError):
    """The provider reports the identity already exists (for example NuGet 409)."""


class TransportTimeout(Exception):
    """No reliable answer: a timeout or a lost response. The outcome is uncertain, never assumed absent."""


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def git_blob_id(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def canonical_json(document) -> bytes:
    return (json.dumps(document, indent=2, sort_keys=True, ensure_ascii=True) + "\n").encode("ascii")


# --------------------------------------------------------------------------------------------------------------------
# Versions and release intent
# --------------------------------------------------------------------------------------------------------------------

class Version:
    __slots__ = ("major", "minor", "patch", "labels", "text")

    def __init__(self, major: int, minor: int, patch: int, labels: tuple, text: str):
        self.major, self.minor, self.patch, self.labels, self.text = major, minor, patch, labels, text

    @property
    def is_stable(self) -> bool:
        return not self.labels

    def __repr__(self) -> str:
        return "Version(%r)" % self.text


def parse_version(text) -> Version:
    """Canonical three-part NuGet SemVer2: no alias, build metadata, fourth part, leading zeros or int32 overflow."""
    if not isinstance(text, str) or not text.isascii():
        raise VersionError("version must be an ASCII string")
    match = _VERSION.fullmatch(text)
    if not match:
        raise VersionError("version %r must be canonical three-part SemVer without aliases, build metadata, leading zeros "
                           "or a fourth component" % text)
    parts = [int(match.group(i)) for i in (1, 2, 3)]
    if any(part > INT32_MAX for part in parts):
        raise VersionError("version components exceed NuGet version bounds")
    labels = tuple(match.group(4).split(".")) if match.group(4) else ()
    for label in labels:
        if label.isdigit() and len(label) > 1 and label[0] == "0":
            raise VersionError("numeric prerelease identifiers must not have leading zeroes")
    return Version(parts[0], parts[1], parts[2], labels, text)


def _label_key(label: str):
    return (0, int(label), "") if label.isdigit() else (1, 0, label.lower())


def compare_versions(a: Version, b: Version) -> int:
    """NuGet SemVer2 precedence; prerelease labels compare case-insensitively, as NuGet identities do."""
    core_a, core_b = (a.major, a.minor, a.patch), (b.major, b.minor, b.patch)
    if core_a != core_b:
        return -1 if core_a < core_b else 1
    if not a.labels or not b.labels:
        return (not a.labels) - (not b.labels)
    for left, right in zip(a.labels, b.labels):
        key_a, key_b = _label_key(left), _label_key(right)
        if key_a != key_b:
            return -1 if key_a < key_b else 1
    return (len(a.labels) > len(b.labels)) - (len(a.labels) < len(b.labels))


def check_release_intent(version: Version, release_preparation: bool) -> None:
    """Same rule as Assert-CandidateVersion in eng/package-inventory.psm1. An intent guard, never approval."""
    if version.is_stable and not release_preparation:
        raise UsageError("Stable CandidateVersion requires explicit -ReleasePreparation; this does not authorize publication.")
    if not version.is_stable and release_preparation:
        raise UsageError("ReleasePreparation requires a stable identity; ordinary probes must remain prereleases.")


def classify_release(candidate: Version, baseline: Version) -> str:
    core_c, core_b = (candidate.major, candidate.minor, candidate.patch), (baseline.major, baseline.minor, baseline.patch)
    if core_c[0] > core_b[0]:
        return "major"
    if core_c[0] == core_b[0] and core_c[1] > core_b[1]:
        return "minor"
    if core_c[:2] == core_b[:2] and core_c[2] > core_b[2]:
        return "patch"
    return "not-greater"


def release_class_problems(candidate: Version, baseline: Version) -> list:
    release_class = classify_release(candidate, baseline)
    if release_class != ACCEPTED_RELEASE_CLASS:
        return ["release class %s relative to the published baseline %s does not follow the accepted rationale, which requires "
                "the next major line %d.x" % (release_class, baseline.text, baseline.major + 1)]
    if candidate.major != baseline.major + 1:
        return ["%s skips a major line; the accepted rationale targets the next available %d.x boundary" % (candidate.text, baseline.major + 1)]
    return []


def _same_version_text(text, version: Version) -> bool:
    try:
        return compare_versions(parse_version(str(text)), version) == 0
    except VersionError:
        return str(text).lower() == version.text.lower()


# --------------------------------------------------------------------------------------------------------------------
# Paths, archives and nuspec
# --------------------------------------------------------------------------------------------------------------------

def _name_problem(name, portable: bool):
    if not isinstance(name, str) or not name:
        return "empty name"
    if any(ord(c) < 32 or ord(c) == 127 for c in name):
        return "control character in name"
    if "\\" in name:
        return "backslash separator"
    if name.startswith("/"):
        return "absolute path"
    if name.endswith("/"):
        return "directory entry"
    parts = name.split("/")
    if any(part in ("", ".", "..") for part in parts):
        return "empty, '.' or '..' path segment (traversal)"
    if re.match(r"^[A-Za-z]:", parts[0]):
        return "drive-qualified path"
    if portable:
        for part in parts:
            if any(c in '<>:"|?*' for c in part):
                return "character that is not portable across file systems"
            if part.endswith(".") or part.endswith(" "):
                return "segment ends with a dot or space"
            if _WINDOWS_RESERVED.match(part):
                return "reserved device name"
    return None


def bundle_path_problem(path):
    """None when ``path`` is a safe, relative, contained, portable bundle path; otherwise the reason."""
    return _name_problem(path, portable=True)


def check_outside_bundle(path, bundle) -> None:
    resolved, root = os.path.realpath(str(path)), os.path.realpath(str(bundle))
    if resolved == root or resolved.startswith(root + os.sep):
        raise UsageError("%s must stay outside the frozen candidate bundle %s" % (path, bundle))


def _is_signature_name(path: str) -> bool:
    return path.rsplit("/", 1)[-1].casefold() == SIGNATURE_ENTRY


def inspect_zip_bytes(data: bytes, label: str = "archive", capture=None) -> dict:
    """Every entry's path/length/SHA-256 plus problems for ambiguous or unsafe archives (duplicate, case-colliding,
    traversal, absolute, symlink, encrypted, directory entries, prepended data, CRC failures)."""
    result = {"entries": [], "problems": [], "captured": {}}
    problems = result["problems"]
    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
    except (zipfile.BadZipFile, zipfile.LargeZipFile, OSError, ValueError, EOFError) as error:
        problems.append("%s: not a readable ZIP archive (%s)" % (label, error))
        return result
    with archive:
        infos = archive.infolist()
        if not infos:
            problems.append("%s: archive has no entries" % label)
        seen, folded, offsets = set(), {}, []
        for info in infos:
            name = info.filename
            if info.orig_filename != name or _name_problem(info.orig_filename, portable=False):
                problems.append("%s: original entry name %r was normalized or is unsafe" % (label, info.orig_filename))
                continue
            problem = _name_problem(name, portable=False)
            if problem:
                problems.append("%s: entry %r: %s" % (label, name, problem))
                continue
            if name in seen:
                problems.append("%s: duplicate entry %r" % (label, name))
                continue
            seen.add(name)
            key = name.casefold()
            if key in folded:
                problems.append("%s: entry %r collides with %r when case is ignored" % (label, name, folded[key]))
                continue
            folded[key] = name
            kind = stat.S_IFMT(info.external_attr >> 16)
            if kind == stat.S_IFLNK:
                problems.append("%s: entry %r is a symbolic link" % (label, name))
                continue
            if kind not in (0, stat.S_IFREG):
                problems.append("%s: entry %r is not a regular file" % (label, name))
                continue
            if info.flag_bits & 0x1:
                problems.append("%s: entry %r is encrypted" % (label, name))
                continue
            offsets.append(info.header_offset)
            try:
                with archive.open(info) as stream:
                    content = stream.read()
            except (zipfile.BadZipFile, OSError, RuntimeError, NotImplementedError, EOFError, zlib.error) as error:
                problems.append("%s: entry %r is unreadable (%s)" % (label, name, error))
                continue
            if len(content) != info.file_size:
                problems.append("%s: entry %r length differs from its header" % (label, name))
            result["entries"].append({"path": name, "length": len(content), "sha256": sha256_bytes(content)})
            if capture is not None and capture(name):
                result["captured"][name] = content
        if offsets and min(offsets) != 0:
            problems.append("%s: data precedes the first local header (prepended content)" % label)
    result["entries"].sort(key=lambda entry: entry["path"])
    return result


def _local(tag) -> str:
    return tag.rsplit("}", 1)[-1] if isinstance(tag, str) else ""


def parse_nuspec(data: bytes) -> dict:
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    upper = data.upper()
    if b"<!DOCTYPE" in upper or b"<!ENTITY" in upper:
        raise ArchiveError("nuspec declares a DTD or entity; rejected")
    try:
        root = ET.fromstring(data)
    except ET.ParseError as error:
        raise ArchiveError("nuspec is not well-formed XML: %s" % error) from error
    if _local(root.tag) != "package":
        raise ArchiveError("nuspec root is not <package>")
    metadata = next((child for child in root if _local(child.tag) == "metadata"), None)
    if metadata is None:
        raise ArchiveError("nuspec has no <metadata>")

    def child(name):
        return next((item for item in metadata if _local(item.tag) == name), None)

    def text(name):
        element = child(name)
        return (element.text or "").strip() if element is not None else None

    package_id, version = text("id"), text("version")
    if not package_id or not version:
        raise ArchiveError("nuspec lacks an id or version")

    def dependency(element):
        return {"id": element.get("id"), "version": element.get("version")}

    groups = []
    dependencies = child("dependencies")
    if dependencies is not None:
        direct = [dependency(item) for item in dependencies if _local(item.tag) == "dependency"]
        if direct:
            groups.append({"targetFramework": None, "dependencies": direct})
        for group in (item for item in dependencies if _local(item.tag) == "group"):
            groups.append({"targetFramework": group.get("targetFramework"),
                           "dependencies": [dependency(item) for item in group if _local(item.tag) == "dependency"]})
    package_types = []
    types = child("packageTypes")
    if types is not None:
        package_types = [item.get("name") for item in types if _local(item.tag) == "packageType"]
    repository = child("repository")
    return {"id": package_id, "version": version, "dependencies": groups, "packageTypes": package_types,
            "repository": None if repository is None else {"type": repository.get("type"), "url": repository.get("url"),
                                                           "commit": repository.get("commit")}}


def compare_nuget_archives(approved: bytes, downloaded: bytes) -> dict:
    """Repository-signing rule: every non-signature entry identical (path/length/SHA-256); the ONLY permitted added entry
    is the exact root ``.signature.p7s``. Upload and download whole-file hashes are kept separately."""
    result = {"payloadEqual": False, "signatureEntry": "absent", "problems": [], "uploadSha256": sha256_bytes(approved),
              "downloadSha256": sha256_bytes(downloaded),
              "rule": "all non-signature entries identical; only the exact root .signature.p7s may be added"}
    problems = result["problems"]
    left, right = inspect_zip_bytes(approved, "approved package"), inspect_zip_bytes(downloaded, "downloaded package")
    problems.extend(left["problems"] + right["problems"])
    if any(_is_signature_name(entry["path"]) for entry in left["entries"]):
        problems.append("the approved package already carries a signature entry; approved local packages are unsigned")
    approved_map = {entry["path"]: entry for entry in left["entries"]}
    payload = {entry["path"]: entry for entry in right["entries"]}
    if SIGNATURE_ENTRY in payload:
        result["signatureEntry"] = "present"
        payload.pop(SIGNATURE_ENTRY)
    for name in sorted(set(approved_map) | set(payload)):
        if name not in payload:
            problems.append("entry %s is missing from the downloaded package" % name)
        elif name not in approved_map:
            problems.append("entry %s was added; only the exact root %s may be added" % (name, SIGNATURE_ENTRY))
        elif approved_map[name] != payload[name]:
            problems.append("entry %s differs (length/SHA-256)" % name)
    result["payloadEqual"] = not problems
    return result


def compare_release_asset(approved: bytes, downloaded: bytes) -> dict:
    """GitHub release assets require whole-file equality."""
    upload, download = sha256_bytes(approved), sha256_bytes(downloaded)
    return {"equal": upload == download and approved == downloaded, "uploadSha256": upload, "downloadSha256": download}


def dependency_order(ids: list, dependencies: dict) -> tuple:
    """Dependency-first order (stable on ``ids``) plus problems for unknown dependencies, misordered input and cycles."""
    problems = []
    known = set(ids)
    if len(known) != len(ids):
        problems.append("duplicate package identities in the dependency graph")
    position = {pid: index for index, pid in enumerate(ids)}
    for pid in ids:
        for dep in sorted(dependencies.get(pid, ())):
            if dep not in known:
                problems.append("%s depends on %s, which is not part of the candidate" % (pid, dep))
            elif position[dep] > position[pid]:
                problems.append("order is not dependency-first: %s must precede %s" % (dep, pid))
    remaining, done, order = list(ids), set(), []
    while remaining:
        ready = [pid for pid in remaining if all(dep in done or dep not in known for dep in dependencies.get(pid, ()))]
        if not ready:
            problems.append("dependency cycle among: " + ", ".join(remaining))
            break
        order.append(ready[0])
        done.add(ready[0])
        remaining.remove(ready[0])
    return order, problems


# --------------------------------------------------------------------------------------------------------------------
# Evidence rows
# --------------------------------------------------------------------------------------------------------------------

ROW_TITLES = {
    "release-identity": "Canonical version, explicit stable intent and accepted release class",
    "source-identity": "Clean source tree at the packed source commit S, archived and verified per git blob",
    "package-adoption": "Once-packed feed adopted byte-for-byte: inventory, package set, archives, nuspec identity, dependency graph",
    "remote-availability": "Read-only remote availability snapshot (stale-able, not a reservation)",
    "e4-aggregate": "E4 cumulative candidate gate recomputed in aggregate mode for this exact candidate",
    "e7-lifecycle": "E7 deprecation lifecycle validator",
    "e6-maintenance": "E6 maintenance / clean-copy receipt bound to S",
    "final-review": "Final independent review receipt bound to S and the package manifest",
    "governance": "Effective repository governance: required status checks and approving reviews",
    "release-notes": "Release notes for this exact version",
}
ROW_ORDER = list(ROW_TITLES)


def _row(row_id: str) -> dict:
    return {"id": row_id, "title": ROW_TITLES[row_id], "required": True, "state": "pass", "reasons": [], "evidence": [], "details": {}}


def _finish(row: dict, failures: list, blockers: list) -> dict:
    row["reasons"] = list(failures) + list(blockers)
    row["state"] = "fail" if failures else ("blocked" if blockers else "pass")
    return row


def _read_json(path: Path):
    return json.loads(Path(path).read_bytes().decode("utf-8-sig"))


def load_availability(path):
    if path is None:
        return None
    path = Path(path)
    raw = path.read_bytes()
    document = json.loads(raw.decode("utf-8-sig"))
    if isinstance(document, list):
        entries, retrieved, source = document, None, "nuget-flat-container-status-list"
    elif isinstance(document, dict) and document.get("schema") == AVAILABILITY_SCHEMA and isinstance(document.get("packages"), list):
        entries, retrieved, source = document["packages"], document.get("retrievedAtUtc"), AVAILABILITY_SCHEMA
    else:
        raise ValueError("unrecognised remote availability snapshot")
    packages, times = {}, []
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("id"), str):
            raise ValueError("availability entry without an id")
        key = entry["id"].lower()
        if key in packages:
            raise ValueError("availability snapshot lists %s twice" % entry["id"])
        http = None if entry.get("http") is None else str(entry.get("http"))
        if entry.get("curlExit") not in (None, 0):
            record = {"state": "unknown", "versions": [], "stableOnly": False}
        elif entry.get("status") == "absent" or http == "404":
            record = {"state": "absent", "versions": [], "stableOnly": False}
        elif isinstance(entry.get("versions"), list):
            record = {"state": "present", "versions": list(entry["versions"]), "stableOnly": False}
        elif isinstance(entry.get("stableVersions"), list):
            record = {"state": "present", "versions": list(entry["stableVersions"]), "stableOnly": True}
        else:
            record = {"state": "unknown", "versions": [], "stableOnly": False}
        packages[key] = record
        stamp = entry.get("readAtUtc") or entry.get("retrievedAtUtc")
        if stamp:
            times.append(str(stamp))
    return {"path": str(path), "sha256": sha256_bytes(raw), "source": source,
            "retrievedAtUtc": retrieved or (min(times) if times else None), "packages": packages}


def availability_row(snapshot, ids: list, version: Version, baseline: Version) -> dict:
    row = _row("remote-availability")
    failures, blockers = [], []
    if snapshot is None:
        return _finish(row, [], ["no read-only remote availability snapshot was supplied; re-read the remote version state before freezing"])
    highest = None
    for pid in ids:
        entry = snapshot["packages"].get(pid.lower())
        if entry is None:
            blockers.append("the snapshot does not cover %s" % pid)
            continue
        if entry["state"] == "unknown":
            blockers.append("remote state of %s is unknown in the snapshot" % pid)
            continue
        for text in entry["versions"]:
            try:
                remote = parse_version(str(text))
            except VersionError:
                blockers.append("%s lists the non-canonical remote version %r, which cannot be compared safely" % (pid, text))
                continue
            if compare_versions(remote, version) == 0:
                failures.append("%s %s already exists remotely; a published identity is never reused" % (pid, text))
            if remote.is_stable and (highest is None or compare_versions(remote, highest) > 0):
                highest = remote
        if entry["stableOnly"] and not version.is_stable:
            blockers.append("the snapshot lists only stable versions of %s; it cannot prove the prerelease identity is free" % pid)
    if highest is not None and compare_versions(version, highest) <= 0:
        failures.append("%s does not exceed the highest published stable version %s" % (version.text, highest.text))
    if compare_versions(version, baseline) <= 0:
        failures.append("%s does not exceed the published baseline %s" % (version.text, baseline.text))
    row["details"] = {"snapshotSha256": snapshot["sha256"], "source": snapshot["source"], "retrievedAtUtc": snapshot["retrievedAtUtc"],
                      "highestPublishedStable": highest.text if highest else None, "staleAble": True,
                      "note": "a snapshot, not a reservation: re-read before presenting for authorization and again during promotion"}
    return _finish(row, failures, blockers)


def find_pwsh():
    return os.environ.get("PWSH") or shutil.which("pwsh")


def validate_deprecation_output(output: Path, exit_code: int, ledger: Path, row=None, *, candidate=None, consumer_results=None, repo=None) -> dict:
    """Validate a wrapper report; release callers must also supply all immutable/raw/source bindings.

    An unbound wrapper-contract probe is never candidate acceptance. Preparation
    and bundle verification independently require the full bound re-derivation.
    """
    row = row or _row("e7-lifecycle")
    failures = []
    if exit_code == 2:
        failures.append("the E7 validator reported invalid input (exit 2)")
    elif exit_code not in (0, 1):
        failures.append("the E7 validator exited with unexpected code %s" % exit_code)
    output = Path(output)
    if not output.is_file():
        failures.append("the E7 validator wrote no output document")
        return _finish(row, failures, [])
    try:
        document = _read_json(output)
    except (OSError, ValueError) as error:
        return _finish(row, failures + ["the E7 validator output is not readable JSON: %s" % error], [])
    if not isinstance(document, dict):
        return _finish(row, failures + ["the E7 validator output is not an object"], [])
    if document.get("schema") != DEPRECATION_SCHEMA:
        failures.append("the E7 validator output schema is %r, not %s" % (document.get("schema"), DEPRECATION_SCHEMA))
    overall = document.get("overall")
    if overall != "PASS":
        failures.append("the E7 lifecycle validation is %r, not PASS" % overall)
    if exit_code == 0 and overall != "PASS" or exit_code == 1 and overall == "PASS":
        failures.append("the E7 validator exit code %s disagrees with its verdict %r" % (exit_code, overall))
    ledger_sha = sha256_file(ledger) if Path(ledger).is_file() else None
    if document.get("ledgerSha256") != ledger_sha:
        failures.append("the E7 report is for ledger %r, not the source ledger %s" % (document.get("ledgerSha256"), ledger_sha))
    entries = document.get("entries")
    if not isinstance(entries, list) or not entries:
        failures.append("the E7 report lists no ledger entries")
        entries = []
    for entry in entries:
        if not isinstance(entry, dict) or entry.get("state") not in E7_OK_STATES:
            item = entry if isinstance(entry, dict) else {}
            failures.append("E7 entry %s is %s (%s)" % (item.get("id"), item.get("state"), "; ".join(item.get("failedPrerequisites") or [])))
    bound = candidate is not None and consumer_results is not None and repo is not None
    if any(value is not None for value in (candidate, consumer_results, repo)):
        if not bound:
            failures.append("E7 release validation requires the package manifest, validated consumer results and exact source")
        else:
            try:
                record = _read_json(Path(candidate))
                expected = _load_candidate_gate().deprecation_report(Path(repo), sha256_file(Path(candidate)),
                    _read_json(Path(consumer_results)), sha256_file(Path(consumer_results)), record["candidateVersion"])
                if (expected["overall"] != "PASS" or expected["sourceInvariants"]["status"] != "PASS"
                        or expected["consumerEvidence"]["status"] != "matched"):
                    failures.append("the independently re-derived E7 lifecycle/source/all-nine semantic contract did not pass")
                for field in ("schema", "ledgerSha256", "ledgerContentSha256", "policyVersion", "candidateSha256", "candidateKind",
                              "consumerResultsSha256", "overall", "entries", "releaseHistory", "sourceInvariants", "consumerEvidence"):
                    if canonical_json(document.get(field)) != canonical_json(expected[field]):
                        failures.append("the E7 report %s differs from the immutable candidate/source/raw evidence" % field)
                tool = _load_candidate_gate()._load_tool("meridian_e5_e7_clock", "deprecation_policy.py")
                if document.get("clock") != "system-utc" or tool.parse_utc(document.get("checkedAtUtc")) > datetime.now(timezone.utc):
                    failures.append("the E7 report does not identify a valid system UTC evaluation")
            except (OSError, ValueError, KeyError, TypeError) as error:
                failures.append("E7 bound lifecycle evidence cannot be re-derived: %s" % error)
    row["details"] = {"exitCode": exit_code, "ledgerSha256": document.get("ledgerSha256"), "checkedAtUtc": document.get("checkedAtUtc"),
                      "clock": document.get("clock"), "policyVersion": document.get("policyVersion"), "candidateBound": bound,
                      "entries": [[e.get("id"), e.get("proposedAction"), e.get("state")] for e in entries if isinstance(e, dict)]}
    return _finish(row, failures, [])


def run_deprecation_validator(repo: Path, output: Path, pwsh=None, *, candidate=None, consumer_results=None) -> dict:
    """Run the actual wrapper; release preparation supplies its immutable and validated raw inputs."""
    repo, output = Path(repo), Path(output)
    row = _row("e7-lifecycle")
    script, ledger = repo / "eng" / "verify-deprecations.ps1", repo / "docs" / "deprecations.md"
    if not script.is_file():
        return _finish(row, [], ["eng/verify-deprecations.ps1 does not exist; the required E7 lifecycle gate cannot run"])
    if not ledger.is_file():
        return _finish(row, [], ["docs/deprecations.md does not exist; the E7 lifecycle ledger is missing"])
    pwsh = find_pwsh() if pwsh is None else pwsh
    if not pwsh:
        return _finish(row, [], ["PowerShell (pwsh) is not available; the E7 lifecycle gate cannot run"])
    command = [pwsh, "-NoProfile", "-NonInteractive", "-File", str(script), "-Ledger", "docs/deprecations.md", "-Output", str(output)]
    if candidate is not None:
        command += ["-Candidate", str(candidate)]
    if consumer_results is not None:
        command += ["-ConsumerResults", str(consumer_results)]
    try:
        completed = subprocess.run(command, cwd=str(repo), capture_output=True, text=True, timeout=900)
    except subprocess.TimeoutExpired:
        return _finish(row, [], ["the E7 validator did not finish within 900 seconds"])
    except OSError as error:
        return _finish(row, [], ["the E7 validator could not start: %s" % error])
    log = output.with_name(output.stem + ".log")
    with open(log, "x", encoding="utf-8") as stream:
        stream.write("command=%s\nexit=%s\n--- stdout ---\n%s\n--- stderr ---\n%s\n" % (" ".join(command), completed.returncode, completed.stdout, completed.stderr))
    return validate_deprecation_output(output, completed.returncode, ledger, row, candidate=candidate,
                                       consumer_results=consumer_results, repo=repo if candidate is not None or consumer_results is not None else None)


def deprecation_consumer_results(evidence: Path, validated_lanes: list[str]) -> Path | None:
    """One complete semantic lane, after E4 has independently validated every required lane.

    OS lanes must not be concatenated: they repeat framework/SDK/variant identities.
    Raw execution provenance and complete cross-platform coverage remain E4's job.
    """
    gate = _load_candidate_gate()
    for lane_id in sorted(set(validated_lanes)):
        path = Path(evidence) / "lanes" / gate.lane_directory_name(lane_id) / "lane-result.json"
        if not path.is_file():
            continue
        result = _read_json(path)
        if (result.get("laneId") != lane_id or result.get("kind") != "package-consumers" or result.get("status") != "PASS"
                or "Meridian.DeprecationConsumer" not in (result.get("consumers") or {}).get("selectedIds", [])):
            continue
        raw = []
        for item in result.get("artifacts") or []:
            relative = item.get("path")
            if isinstance(relative, str) and gate.raw_name_matches(Path(relative).name, "consumer-results.json"):
                target = path.parent / relative
                if (not bundle_path_problem(relative) and target.is_file() and not target.is_symlink()
                        and sha256_file(target) == item.get("sha256")):
                    raw.append(target)
        if len(raw) == 1:
            return raw[0]
    return None


def required_check_names(policy) -> list:
    if not isinstance(policy, dict):
        return []
    names = [item.get("check") for item in policy.get("infrastructureChecks") or [] if isinstance(item, dict)]
    names += [lane.get("check") for lane in policy.get("lanes") or [] if isinstance(lane, dict) and lane.get("ci")]
    return [name for name in names if name]


def governance_row(protection_path, rulesets_path, repository: str, required_checks: list) -> dict:
    row = _row("governance")
    failures, blockers, advisories = [], [], []
    if protection_path is None or not Path(protection_path).is_file():
        return _finish(row, [], ["no effective governance evidence (read-only branch protection snapshot) was supplied"])
    try:
        envelope = _read_json(Path(protection_path))
    except (OSError, ValueError) as error:
        return _finish(row, ["governance evidence is not readable JSON: %s" % error], [])
    if not isinstance(envelope, dict):
        return _finish(row, ["governance evidence is not an object"], [])
    command = envelope.get("command")
    endpoint = "repos/%s/branches/main/protection" % repository
    if (not isinstance(command, list) or len(command) != 3 or command[:2] != ["gh", "api"] or
            not isinstance(command[2], str) or command[2].casefold() != endpoint.casefold()):
        failures.append("governance evidence must be the exact read-only main protection endpoint %s" % endpoint)
    if type(envelope.get("exit")) is not int or envelope["exit"] != 0:
        blockers.append("the branch protection read failed or the branch is unprotected (exit %r)" % envelope.get("exit"))
    data = envelope.get("data") if isinstance(envelope.get("data"), dict) else {}
    checks = data.get("required_status_checks")
    contexts = set()
    if isinstance(checks, dict):
        contexts.update(c for c in checks.get("contexts") or [] if isinstance(c, str))
        contexts.update(c.get("context") for c in checks.get("checks") or [] if isinstance(c, dict) and c.get("context"))
        if checks.get("strict") is not True:
            advisories.append("required status checks are not strict (up to date before merging)")
    if not contexts:
        blockers.append("required status checks are absent (%d policy checks expected)" % len(required_checks))
    else:
        missing = [name for name in required_checks if name not in contexts]
        if missing:
            blockers.append("required status checks miss %d of %d policy checks: %s" % (len(missing), len(required_checks), ", ".join(missing[:12])))
    reviews = data.get("required_pull_request_reviews")
    count = reviews.get("required_approving_review_count") if isinstance(reviews, dict) else None
    if not isinstance(count, int) or isinstance(count, bool) or count < 1:
        blockers.append("required approving reviews are absent")
    if rulesets_path is not None and Path(rulesets_path).is_file():
        try:
            rulesets = _read_json(Path(rulesets_path)).get("data")
        except (OSError, ValueError, AttributeError):
            rulesets = None
        if not isinstance(rulesets, list) or not any(isinstance(r, dict) and r.get("target") == "tag" and r.get("enforcement") == "active" for r in rulesets):
            advisories.append("no active tag ruleset protects release tags")
    else:
        advisories.append("no rulesets snapshot was supplied")
    row["details"] = {"observedAtUtc": envelope.get("atUtc"), "requiredChecks": len(required_checks), "advisories": advisories, "staleAble": True}
    return _finish(row, failures, blockers)


def final_review_scope(summary: dict, evidence: Path, policy_sha256: str) -> dict:
    """Bind a complete review to policy and exact lane receipts, without transient summary paths/timestamps."""
    if (not isinstance(summary, dict) or summary.get("schema") != GATE_SUMMARY_SCHEMA or
            summary.get("overall") != "PASS" or type(summary.get("exitCode")) is not int or summary["exitCode"] != 0 or
            summary.get("wholeCandidate") is not True or summary.get("releaseEligible") is not True or
            summary.get("mode") != "aggregate" or summary.get("scope") != "candidate"):
        raise ValueError("final review requires a passing whole-candidate E4 aggregate")
    policy = summary.get("policy") or {}
    lanes = summary.get("lanes")
    if (not isinstance(policy, dict) or not HEX64.fullmatch(str(policy_sha256)) or
            policy.get("sha256", policy_sha256) != policy_sha256 or
            not isinstance(lanes, list) or not lanes or type(policy.get("laneCount")) is not int or
            policy["laneCount"] != len(lanes)):
        raise ValueError("final review requires the complete E4 policy and lane set")
    root = Path(evidence).resolve()
    receipts = {}
    gate = _load_candidate_gate()
    for lane in lanes:
        identity = lane.get("id") if isinstance(lane, dict) else None
        if not isinstance(identity, str) or not identity or identity in receipts or lane.get("state") != "PASS":
            raise ValueError("final review requires unique passing E4 lanes")
        relative = "lanes/%s/lane-result.json" % gate.lane_directory_name(identity)
        target = root / relative
        if (bundle_path_problem(relative) or not target.is_file() or target.is_symlink() or
                not target.resolve().is_relative_to(root) or
                any(parent.is_symlink() for parent in target.parents if parent.is_relative_to(root))):
            raise ValueError("final review lane receipt is missing or unsafe: %s" % identity)
        receipts[identity] = sha256_file(target)
    return {"policySha256": policy_sha256, "laneReceiptSha256": receipts}


def release_receipt_row(row_id: str, path, kind: str, source_commit, version: str, manifest_sha256, require_manifest: bool,
                        *, e4_summary=None, e4_evidence=None, e4_policy_sha256=None) -> dict:
    row = _row(row_id)
    if path is None or not Path(path).is_file():
        return _finish(row, [], ["no %s receipt (%s) was supplied" % (kind, RELEASE_RECEIPT_SCHEMA)])
    try:
        document = _read_json(Path(path))
    except (OSError, ValueError) as error:
        return _finish(row, ["%s receipt is not readable JSON: %s" % (kind, error)], [])
    if not isinstance(document, dict):
        return _finish(row, ["%s receipt is not an object" % kind], [])
    failures, blockers = [], []
    expected = {"schema": RELEASE_RECEIPT_SCHEMA, "kind": kind, "verdict": "PASS", "sourceCommit": source_commit, "candidateVersion": version}
    if require_manifest:
        expected["packageManifestSha256"] = manifest_sha256
    for field, value in expected.items():
        if document.get(field) != value:
            failures.append("%s receipt %s is %r, not %r" % (kind, field, document.get(field), value))
    if kind == "final-review":
        if document.get("wholeCandidateReleaseEligible") is not True:
            blockers.append("final-review receipt does not approve the whole candidate; local-only approval cannot be reused")
        elif e4_summary is None or e4_evidence is None or e4_policy_sha256 is None:
            blockers.append("final-review requires a passing whole-candidate E4 aggregate")
        else:
            try:
                scope = final_review_scope(e4_summary, Path(e4_evidence), e4_policy_sha256)
            except (OSError, ValueError) as error:
                failures.append("final-review E4 binding cannot be derived: %s" % error)
            else:
                if document.get("reviewedE4") != scope:
                    failures.append("final-review reviewedE4 differs from the actual policy or complete lane receipt hashes")
                row["details"]["reviewedE4"] = scope
    row["details"].update(reviewedAtUtc=document.get("reviewedAtUtc"), reviewer=document.get("reviewer"))
    return _finish(row, failures, blockers)


def release_notes_row(path, version: Version) -> dict:
    row = _row("release-notes")
    if path is None or not Path(path).is_file():
        return _finish(row, [], ["no release notes for %s were supplied" % version.text])
    try:
        text = Path(path).read_bytes().decode("utf-8")
    except (OSError, UnicodeDecodeError) as error:
        return _finish(row, ["release notes are not readable UTF-8: %s" % error], [])
    if version.text not in text:
        return _finish(row, ["release notes do not name the exact version %s" % version.text], [])
    return _finish(row, [], [])


def _load_candidate_gate():
    path = Path(__file__).resolve().with_name("candidate_gate.py")
    spec = importlib.util.spec_from_file_location("meridian_candidate_gate", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def default_gate_evaluator(policy, feed, evidence, version, *, source_commit, repo, expected_manifest_sha256):
    """eng/candidate_gate.py evaluate --aggregate, recomputed (a recorded summary file is never trusted)."""
    gate = _load_candidate_gate()
    return gate.evaluate(policy, Path(feed), Path(evidence), version, host=None, allow_pending=False, source_commit=source_commit,
                         only=None, repo=Path(repo), expected_manifest_sha256=expected_manifest_sha256)


_MISSING_LANE = re.compile(r"^\S+ \[MISSING\]: ")
# candidate_gate adds this when no lane result exists at all; it is a consequence of missing evidence, not a failure.
_NO_LANE_COMMIT = "no lane evidence carries the required source commit"


def judge_gate_summary(summary, version: str, manifest_sha256: str, source_commit: str, packages: dict) -> dict:
    row = _row("e4-aggregate")
    failures, blockers = [], []
    if not isinstance(summary, dict):
        return _finish(row, ["the candidate gate returned no summary"], [])
    if summary.get("schema") != GATE_SUMMARY_SCHEMA:
        failures.append("gate summary schema %r is not %s" % (summary.get("schema"), GATE_SUMMARY_SCHEMA))
    candidate = summary.get("candidate") if isinstance(summary.get("candidate"), dict) else {}
    if candidate.get("version") != version:
        failures.append("gate summary is for %r, not %s" % (candidate.get("version"), version))
    if candidate.get("packageManifestSha256") != manifest_sha256:
        failures.append("gate summary package manifest %r is not the adopted %s" % (candidate.get("packageManifestSha256"), manifest_sha256))
    if candidate.get("packedSourceCommit") != source_commit:
        failures.append("gate summary packed source commit %r is not S %s" % (candidate.get("packedSourceCommit"), source_commit))
    reported = {}
    for item in candidate.get("packages") or []:
        if isinstance(item, dict):
            reported[item.get("id")] = (item.get("sha256"), item.get("symbolsSha256"))
    if reported != packages:
        failures.append("gate summary package identities/hashes differ from the adopted package set")
    overall = summary.get("overall")
    lanes = [lane for lane in summary.get("lanes") or [] if isinstance(lane, dict)]
    if overall == "PASS":
        if summary.get("releaseEligible") is not True:
            failures.append("gate summary is not release eligible")
        if summary.get("wholeCandidate") is not True:
            failures.append("gate summary is not a whole-candidate verdict")
        if summary.get("mode") != "aggregate":
            failures.append("gate summary was not evaluated in aggregate mode")
    elif overall == "FAIL":
        missing = [lane for lane in lanes if lane.get("state") == "MISSING"]
        failed = [lane for lane in lanes if lane.get("state") not in ("PASS", "MISSING")]
        others = [f for f in summary.get("failures") or [] if not _MISSING_LANE.match(str(f)) and f != _NO_LANE_COMMIT]
        if failed or others or not missing:
            failures.append("E4 aggregate gate FAIL: " + "; ".join(str(f) for f in (summary.get("failures") or [])[:8]))
        else:
            blockers.append("E4 aggregate gate has no evidence for %d required lane(s) (missing evidence is blocked, never prepared): %s"
                            % (len(missing), ", ".join(str(lane.get("id")) for lane in missing[:8])))
    else:
        failures.append("gate verdict %r is not a whole-candidate PASS" % overall)
    row["details"] = {"overall": overall, "releaseEligible": summary.get("releaseEligible"), "wholeCandidate": summary.get("wholeCandidate"),
                      "laneCount": len(lanes), "passingLanes": len([lane for lane in lanes if lane.get("state") == "PASS"])}
    return _finish(row, failures, blockers)


def evaluate_e4_gate(repo, policy_path, feed, evidence, version, source_commit, manifest_sha256, packages, evaluator=None):
    row = _row("e4-aggregate")
    if evidence is None or not Path(evidence).is_dir():
        return _finish(row, [], ["no E4 gate evidence directory was supplied; the final cumulative (E4d) evidence is required"]), None
    try:
        policy = _read_json(Path(policy_path))
    except (OSError, ValueError) as error:
        return _finish(row, ["required-check policy is not readable: %s" % error], []), None
    try:
        summary = (evaluator or default_gate_evaluator)(policy, Path(feed), Path(evidence), version, source_commit=source_commit,
                                                        repo=Path(repo), expected_manifest_sha256=manifest_sha256)
    except Exception as error:  # the gate reports policy/configuration errors as exceptions; any of them fails the row
        return _finish(row, ["the candidate gate could not evaluate: %s: %s" % (type(error).__name__, error)], []), None
    return judge_gate_summary(summary, version, manifest_sha256, source_commit, packages), summary


# --------------------------------------------------------------------------------------------------------------------
# Source identity (read-only git)
# --------------------------------------------------------------------------------------------------------------------

def _git_environment():
    return dict(os.environ, GIT_OPTIONAL_LOCKS="0", LC_ALL="C", GIT_TERMINAL_PROMPT="0")


class GitSourceProvider:
    """Read-only git access: rev-parse, status, ls-tree and archive. It never writes to the repository."""

    def __init__(self, repo):
        self.repo = Path(repo)

    def _git(self, *arguments, binary=False, stdout=None):
        command = ["git", "-C", str(self.repo)] + list(arguments)
        try:
            completed = subprocess.run(command, stdout=stdout if stdout is not None else subprocess.PIPE, stderr=subprocess.PIPE,
                                       env=_git_environment(), timeout=900)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise SourceError("git %s could not run: %s" % (arguments[0], error)) from error
        if completed.returncode != 0:
            raise SourceError("git %s failed: %s" % (" ".join(arguments[:2]), completed.stderr.decode("utf-8", "replace").strip()))
        if stdout is not None:
            return None
        return completed.stdout if binary else completed.stdout.decode("utf-8", "replace")

    def state(self) -> dict:
        head = self._git("rev-parse", "--verify", "HEAD").strip()
        status = [line for line in self._git("status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none").splitlines() if line]
        return {"head": head, "dirty": bool(status), "count": len(status), "status": status[:50]}

    def tree(self, commit: str) -> str:
        return self._git("rev-parse", "--verify", commit + "^{tree}").strip()

    def export(self, commit: str, destination: Path):
        """``git archive`` of S, then every member is checked against its git blob id (detects attribute rewrites)."""
        problems = []
        with open(destination, "xb") as stream:
            self._git("-c", "core.autocrlf=false", "-c", "core.eol=lf", "archive", "--format=tar", commit, stdout=stream)
        expected = {}
        for item in self._git("ls-tree", "-r", "-z", "--full-tree", commit, binary=True).split(b"\0"):
            if not item:
                continue
            meta, _, raw_path = item.partition(b"\t")
            mode, kind, obj = meta.decode("ascii").split(" ")
            path = raw_path.decode("utf-8", "surrogateescape")
            if kind != "blob":
                problems.append("%s: %s entries (submodules) are not supported in a release source archive" % (path, kind))
                continue
            expected[path] = (mode, obj)
        records, seen = [], set()
        with tarfile.open(destination) as archive:
            if archive.pax_headers.get("comment") != commit:
                problems.append("the source archive does not name commit %s" % commit)
            for member in archive:
                if member.isdir():
                    continue
                name = member.name
                problem = _name_problem(name, portable=False)
                if problem:
                    problems.append("source archive member %r: %s" % (name, problem))
                    continue
                if member.issym():
                    data, kind = member.linkname.encode("utf-8", "surrogateescape"), "symlink"
                elif member.isfile():
                    data, kind = archive.extractfile(member).read(), "file"
                else:
                    problems.append("source archive member %r has an unsupported type" % name)
                    continue
                known = expected.get(name)
                if known is None:
                    problems.append("source archive member %s is not in the commit tree" % name)
                    continue
                blob = git_blob_id(data)
                if blob != known[1]:
                    problems.append("source archive member %s differs from git blob %s" % (name, known[1]))
                seen.add(name)
                records.append({"path": name, "type": kind, "mode": known[0], "size": len(data), "sha256": sha256_bytes(data), "gitBlob": blob})
        for name in sorted(set(expected) - seen):
            problems.append("commit file %s is missing from the source archive" % name)
        records.sort(key=lambda record: record["path"])
        return records, problems


# --------------------------------------------------------------------------------------------------------------------
# Preparation
# --------------------------------------------------------------------------------------------------------------------

def _copy_exact(source: Path, destination: Path) -> tuple:
    destination.parent.mkdir(parents=True, exist_ok=True)
    digest, size = hashlib.sha256(), 0
    with open(source, "rb") as reader, open(destination, "xb") as writer:
        for block in iter(lambda: reader.read(1 << 20), b""):
            digest.update(block)
            size += len(block)
            writer.write(block)
    return digest.hexdigest(), size


def _copy_tree_exact(source: Path, destination: Path, problems: list, label: str) -> None:
    for directory, directories, files in os.walk(source, followlinks=False):
        for name in sorted(directories):
            if os.path.islink(os.path.join(directory, name)):
                problems.append("%s: %s is a symbolic link" % (label, os.path.relpath(os.path.join(directory, name), source)))
        for name in sorted(files):
            full = os.path.join(directory, name)
            relative = PurePosixPath(*Path(os.path.relpath(full, source)).parts).as_posix()
            mode = os.lstat(full).st_mode
            if stat.S_ISLNK(mode) or not stat.S_ISREG(mode):
                problems.append("%s: %s is not a regular file" % (label, relative))
                continue
            reason = bundle_path_problem(relative)
            if reason:
                problems.append("%s: %s has an unsafe name (%s)" % (label, relative, reason))
                continue
            _copy_exact(Path(full), destination / relative)


def _inspect_package(data: bytes, rel: str, package_id: str, version: Version, internal: dict, repository: str, source_commit,
                     symbols: bool) -> tuple:
    label = rel
    problems = []
    expected_nuspec = package_id + ".nuspec"
    inspected = inspect_zip_bytes(data, label, capture=lambda name: name == expected_nuspec)
    problems.extend(inspected["problems"])
    entries = inspected["entries"]
    if any(_is_signature_name(entry["path"]) for entry in entries):
        problems.append("%s carries a signature entry; local candidates are unsigned and .signature.p7s is reserved for repository signing" % label)
    roots = [entry["path"] for entry in entries if "/" not in entry["path"] and entry["path"].lower().endswith(".nuspec")]
    nuspec, internal_deps = None, set()
    if roots != [expected_nuspec]:
        problems.append("%s must hold exactly one root nuspec named %s (found %s)" % (label, expected_nuspec, roots))
    elif expected_nuspec in inspected["captured"]:
        try:
            nuspec = parse_nuspec(inspected["captured"][expected_nuspec])
        except ArchiveError as error:
            problems.append("%s: %s" % (label, error))
    if nuspec is not None:
        if nuspec["id"] != package_id or nuspec["version"] != version.text:
            problems.append("%s nuspec identity %s %s is not %s %s" % (label, nuspec["id"], nuspec["version"], package_id, version.text))
        for group in nuspec["dependencies"]:
            for dep in group["dependencies"]:
                canonical = internal.get(str(dep.get("id")).casefold())
                if canonical is None:
                    continue
                internal_deps.add(canonical)
                if dep.get("version") != version.text:
                    problems.append("%s: internal dependency %s must be exactly %s, not %r" % (label, canonical, version.text, dep.get("version")))
        source = nuspec.get("repository") or {}
        if source.get("commit") and source.get("commit") != source_commit:
            problems.append("%s nuspec repository commit %s is not S %s" % (label, source.get("commit"), source_commit))
        url = (source.get("url") or "").rstrip("/")
        if url.lower().endswith(".git"):
            url = url[:-4]
        if url and url.lower() != ("https://github.com/" + repository).lower():
            problems.append("%s nuspec repository %s is not https://github.com/%s" % (label, source.get("url"), repository))
        if symbols and "SymbolsPackage" not in nuspec["packageTypes"]:
            problems.append("%s is not a SymbolsPackage" % label)
    record = {"path": rel, "sha256": sha256_bytes(data), "size": len(data), "entries": entries,
              "nuspec": None if nuspec is None else {"path": expected_nuspec, "id": nuspec["id"], "version": nuspec["version"],
                                                     "dependencies": nuspec["dependencies"], "packageTypes": nuspec["packageTypes"],
                                                     "repository": nuspec["repository"]}}
    return record, sorted(internal_deps), problems


def _adopt_feed(feed: Path, version: Version, inventory: dict, inventory_sha: str, staging: Path, repository: str) -> tuple:
    failures = []
    manifest_name = "package-manifest.%s.json" % version.text
    entries = {}
    for item in sorted(os.scandir(feed), key=lambda entry: entry.name):
        if item.is_symlink():
            failures.append("feed entry %s is a symbolic link" % item.name)
        elif not item.is_file(follow_symlinks=False):
            failures.append("feed entry %s is not a regular file" % item.name)
        else:
            entries[item.name] = Path(item.path)
    if manifest_name not in entries:
        return failures + ["the feed has no %s; adopt only a feed produced once by eng/pack-packages.ps1" % manifest_name], None
    try:
        record = json.loads(entries[manifest_name].read_bytes().decode("utf-8-sig"))
    except (OSError, ValueError) as error:
        return failures + ["the package manifest is not readable JSON: %s" % error], None
    if not isinstance(record, dict) or not isinstance(record.get("packages"), list):
        return failures + ["the package manifest is not an object with a package list"], None
    if record.get("schemaVersion") != 1:
        failures.append("package manifest schemaVersion %r is not 1" % record.get("schemaVersion"))
    if record.get("candidateVersion") != version.text:
        failures.append("package manifest is for %r, not %s" % (record.get("candidateVersion"), version.text))
    if record.get("manifestSha256") != inventory_sha:
        failures.append("package manifest was packed from inventory %r, not the source inventory %s" % (record.get("manifestSha256"), inventory_sha))
    source_commit = record.get("sourceCommit")
    if not COMMIT.match(str(source_commit or "")):
        failures.append("package manifest records no valid source commit")
    if record.get("sourceDirty") is not False:
        failures.append("package manifest records sourceDirty=%r; only a pack of a clean commit is release evidence" % record.get("sourceDirty"))
    inventory_packages = inventory.get("packages") or []
    ids = [package.get("id") for package in inventory_packages]
    internal = {pid.casefold(): pid for pid in ids}
    by_id = {}
    for package in record["packages"]:
        pid = package.get("id") if isinstance(package, dict) else None
        if not isinstance(pid, str) or not pid:
            failures.append("package manifest has an entry without an id")
            continue
        if pid.casefold() in by_id:
            failures.append("package manifest lists %s more than once (duplicate identity)" % pid)
            continue
        if pid.casefold() not in internal or internal[pid.casefold()] != pid:
            failures.append("package %s is not in the package inventory" % pid)
            continue
        by_id[pid.casefold()] = package
    for pid in ids:
        if pid.casefold() not in by_id:
            failures.append("inventory package %s is missing from the feed (the exact package set is required)" % pid)
    expected = {manifest_name}
    for package in by_id.values():
        expected.add(str(package.get("package")))
        if package.get("symbols"):
            expected.add(str(package.get("symbols")))
    for name in sorted(set(entries) - expected):
        failures.append("feed holds %s, which the package manifest does not list" % name)
    for name in sorted(expected - set(entries)):
        failures.append("feed is missing %s" % name)
    packages_dir = staging / "packages"
    manifest_sha, _ = _copy_exact(entries[manifest_name], packages_dir / manifest_name)
    closure, dependencies = [], {}
    for inventory_package in inventory_packages:
        pid = inventory_package.get("id")
        package = by_id.get(pid.casefold())
        if package is None:
            continue
        nupkg_name, symbols_name = "%s.%s.nupkg" % (pid, version.text), "%s.%s.snupkg" % (pid, version.text)
        if package.get("package") != nupkg_name:
            failures.append("%s archive is %r, not %s" % (pid, package.get("package"), nupkg_name))
            continue
        if package.get("version") != version.text:
            failures.append("%s record version %r is not %s" % (pid, package.get("version"), version.text))
        if nupkg_name not in entries:
            continue
        digest, _ = _copy_exact(entries[nupkg_name], packages_dir / nupkg_name)
        if digest != package.get("sha256"):
            failures.append("%s archive hash %s differs from the recorded %s" % (pid, digest, package.get("sha256")))
        record_entry, internal_deps, problems = _inspect_package((packages_dir / nupkg_name).read_bytes(), "packages/" + nupkg_name, pid,
                                                                 version, internal, repository, source_commit, symbols=False)
        failures.extend(problems)
        declared = sorted("%s=%s" % (d.get("id"), d.get("version")) for d in package.get("dependencies") or [] if isinstance(d, dict))
        for group in (record_entry["nuspec"] or {}).get("dependencies") or []:
            actual = sorted("%s=%s" % (d.get("id"), d.get("version")) for d in group["dependencies"])
            if actual != declared:
                failures.append("%s nuspec dependencies for %s differ from the package-manifest record" % (pid, group["targetFramework"]))
        symbols_record = None
        if package.get("symbols"):
            if package.get("symbols") != symbols_name:
                failures.append("%s symbols archive is %r, not %s" % (pid, package.get("symbols"), symbols_name))
            elif symbols_name in entries:
                digest, _ = _copy_exact(entries[symbols_name], packages_dir / symbols_name)
                if digest != package.get("symbolsSha256"):
                    failures.append("%s symbols hash %s differs from the recorded %s" % (pid, digest, package.get("symbolsSha256")))
                symbols_record, _, problems = _inspect_package((packages_dir / symbols_name).read_bytes(), "packages/" + symbols_name, pid,
                                                               version, internal, repository, source_commit, symbols=True)
                failures.extend(problems)
        elif inventory_package.get("kind") == "runtime":
            failures.append("runtime package %s has no symbols archive" % pid)
        declared_internal = sorted(internal.get(str(d).casefold(), str(d)) for d in inventory_package.get("projectDependencies") or [])
        if sorted(internal_deps) != declared_internal:
            failures.append("%s internal dependencies %s differ from the inventory projectDependencies %s" % (pid, sorted(internal_deps), declared_internal))
        dependencies[pid] = set(internal_deps)
        closure.append({"id": pid, "kind": inventory_package.get("kind"), "version": version.text, "package": record_entry,
                        "symbols": symbols_record, "internalDependencies": sorted(internal_deps),
                        "externalDependencies": sorted(("%s=%s" % (d.get("id"), d.get("version")) for d in package.get("dependencies") or []
                                                        if isinstance(d, dict) and str(d.get("id")).casefold() not in internal))})
    order, problems = dependency_order([p["id"] for p in closure], dependencies)
    failures.extend(problems)
    return failures, {"manifestName": manifest_name, "manifestSha256": manifest_sha, "record": record, "packages": closure,
                      "dependencyOrder": order, "sourceCommit": source_commit}


def _role_for(path: str):
    fixed = {"source/source.tar": "source-archive", "source/source-files.json": "source-file-list",
             "source/packages.json": "inventory-snapshot", "source/tooling.json": "tooling"}
    if path in fixed:
        return fixed[path]
    if path.startswith("packages/") and path.count("/") == 1:
        name = path.split("/", 1)[1]
        if name.startswith("package-manifest.") and name.endswith(".json"):
            return "package-manifest"
        if name.endswith(".nupkg"):
            return "package"
        if name.endswith(".snupkg"):
            return "symbols"
        return None
    if path.startswith("evidence/receipts/"):
        return "receipt"
    if path.startswith("evidence/"):
        return "evidence"
    if path.startswith("metadata/"):
        return "metadata"
    return None


def _allowlist(staging: Path) -> tuple:
    files, problems = [], []
    for directory, directories, names in os.walk(staging, followlinks=False):
        for name in names:
            full = os.path.join(directory, name)
            relative = PurePosixPath(*Path(os.path.relpath(full, staging)).parts).as_posix()
            if relative in ("candidate.json", "preparation-report.json"):
                continue
            role = _role_for(relative)
            if role is None or bundle_path_problem(relative):
                problems.append("bundle file %s has no allowed role or an unsafe path" % relative)
                continue
            files.append({"path": relative, "sha256": sha256_file(Path(full)), "size": os.lstat(full).st_size, "role": role})
    files.sort(key=lambda item: item["path"])
    return files, problems


def _tooling(repo: Path) -> dict:
    names = ("eng/release_candidate.py", "eng/prepare-release.ps1", "eng/promote-release.ps1", "eng/candidate_gate.py",
             "eng/required-checks.json", "eng/packages.json", "eng/pack-packages.ps1", "eng/package-inventory.psm1",
             "eng/verify-deprecations.ps1", "eng/deprecation_policy.py")
    hashes = {name: (sha256_file(repo / name) if (repo / name).is_file() else None) for name in names}
    return {"files": hashes, "python": sys.version.split()[0], "platform": sys.platform}


def _targets(version: Version, source_commit: str, adoption: dict, files: list) -> list:
    tag = "v" + version.text
    by_path = {item["path"]: item for item in files}
    targets = [{"key": "source:" + source_commit, "kind": "source-commit", "commit": source_commit},
               {"key": "tag:" + tag, "kind": "git-tag", "tag": tag, "commit": source_commit},
               {"key": "release:" + tag, "kind": "github-release", "tag": tag, "name": "Meridian " + version.text,
                "prerelease": not version.is_stable, "notes": "metadata/release-notes.md"}]
    asset_paths = []
    for package in adoption["packages"]:
        asset_paths.append(package["package"]["path"])
        if package["symbols"]:
            asset_paths.append(package["symbols"]["path"])
    asset_paths.append("packages/" + adoption["manifestName"])
    for path in asset_paths:
        item = by_path[path]
        targets.append({"key": "asset:" + path.rsplit("/", 1)[-1], "kind": "github-release-asset", "tag": tag, "name": path.rsplit("/", 1)[-1],
                        "path": path, "sha256": item["sha256"], "size": item["size"]})
    targets.append({"key": "asset:candidate.json", "kind": "github-release-asset", "tag": tag, "name": "candidate.json", "path": "candidate.json",
                    "sha256": None, "digestSource": "approved-candidate-digest"})
    for package in adoption["packages"]:
        targets.append({"key": "nuget:%s/%s" % (package["id"], version.text), "kind": "nuget-package", "id": package["id"], "version": version.text,
                        "path": package["package"]["path"], "sha256": package["package"]["sha256"], "size": package["package"]["size"],
                        "dependsOn": ["nuget:%s/%s" % (dep, version.text) for dep in package["internalDependencies"]]})
    for package in adoption["packages"]:
        if package["symbols"]:
            targets.append({"key": "symbols:%s/%s" % (package["id"], version.text), "kind": "nuget-symbols", "id": package["id"],
                            "version": version.text, "path": package["symbols"]["path"], "sha256": package["symbols"]["sha256"],
                            "size": package["symbols"]["size"], "remoteProof": SYMBOLS_LIMIT})
    targets.append({"key": "consumer-verification", "kind": "consumer-verification", "status": "external",
                    "detail": "fresh isolated consumers restore the actual signed remote packages with the root-owned package engine; not performed by this tool"})
    targets.append({"key": "site", "kind": "site", "status": "not-prepared", "authorized": False})
    for index, target in enumerate(targets, 1):
        target["order"] = index
    return targets


def prepare_candidate(*, version_text, candidate_dir, feed, repo=None, gate_evidence=None, release_preparation=False, availability=None,
                      e6_receipt=None, final_review_receipt=None, governance_protection=None, governance_rulesets=None,
                      release_notes=None, repository=DEFAULT_REPOSITORY, source_provider=None, gate_evaluator=None,
                      deprecation_runner=None) -> tuple:
    """Adopt a once-packed feed into a NEW candidate directory. Returns (exit code, report)."""
    report = {"schema": PREPARATION_SCHEMA, "status": "invalid", "exitCode": EXIT_USAGE, "rows": [], "blockers": [], "failures": [],
              "scope": "local preparation only: no build, pack, rename, upload, tag or other remote action; promotion stays pending trusted authorization"}
    try:
        version = parse_version(version_text)
        check_release_intent(version, bool(release_preparation))
        if not REPOSITORY_PATTERN.match(str(repository or "")):
            raise UsageError("repository must be owner/name")
        repo = Path(repo).resolve() if repo else REPO_ROOT
        if feed is None or not Path(feed).is_dir():
            raise UsageError("--feed must be an existing directory holding the once-packed candidate")
        feed = Path(feed)
        if candidate_dir is None:
            raise UsageError("a new candidate directory is required")
        candidate_dir = Path(os.path.abspath(str(candidate_dir)))
        parent = candidate_dir.parent
        if not parent.is_dir():
            raise UsageError("the parent of the candidate directory must exist: %s" % parent)
    except UsageError as error:
        report["failures"].append(str(error))
        return EXIT_USAGE, report
    if os.path.lexists(candidate_dir):
        report.update(status="failed", exitCode=EXIT_FAIL)
        report["failures"].append("candidate directory %s already exists; a new attempt needs a new candidate directory and is never "
                                  "resumed or repacked into an occupied one" % candidate_dir)
        return EXIT_FAIL, report
    lock = parent / (".%s.prepare.lock" % candidate_dir.name)
    try:
        descriptor = os.open(str(lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError:
        report.update(status="blocked", exitCode=EXIT_BLOCKED)
        report["blockers"].append("another preparation holds %s; inspect it before removing" % lock)
        return EXIT_BLOCKED, report
    try:
        os.write(descriptor, json.dumps({"pid": os.getpid(), "atUtc": utc_now()}).encode())
        candidate_id = "%s-%s-%s" % (version.text, datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ"), secrets.token_hex(4))
        report["candidateId"] = candidate_id
        staging = parent / (".%s.staging-%s" % (candidate_dir.name, candidate_id))
        os.mkdir(str(staging), 0o700)
        try:
            return _prepare_locked(report, staging, candidate_id, version, candidate_dir, feed, repo, gate_evidence, bool(release_preparation),
                                   availability, e6_receipt, final_review_receipt, governance_protection, governance_rulesets, release_notes,
                                   repository, source_provider or GitSourceProvider(repo), gate_evaluator,
                                   deprecation_runner or run_deprecation_validator)
        except Exception as error:  # an interrupted attempt is quarantined with its error, never left as a resumable partial
            report["failures"] = report.get("failures", []) + ["preparation was interrupted: %s: %s" % (type(error).__name__, error)]
            if staging.is_dir():
                return _quarantine(report, staging, candidate_dir, candidate_id, EXIT_FAIL)
            report.update(status="failed", exitCode=EXIT_FAIL)
            return EXIT_FAIL, report
    finally:
        os.close(descriptor)
        os.unlink(str(lock))


def _optional_copy(path, destination: Path) -> Path:
    if path is None or not Path(path).is_file():
        return None
    _copy_exact(Path(path), destination)
    return destination


def _prepare_locked(report, staging, candidate_id, version, candidate_dir, feed, repo, gate_evidence, release_preparation, availability,
                    e6_receipt, final_review_receipt, governance_protection, governance_rulesets, release_notes, repository, source,
                    gate_evaluator, deprecation_runner) -> tuple:
    parent = candidate_dir.parent
    rows = {row_id: _row(row_id) for row_id in ROW_ORDER}
    policy_path = repo / "eng" / "required-checks.json"
    try:
        policy = _read_json(policy_path)
    except (OSError, ValueError):
        policy = None
    inventory_path = repo / ((policy or {}).get("packageInventory") or "eng/packages.json")
    inventory, inventory_sha, inventory_raw = None, None, None
    try:
        inventory_raw = inventory_path.read_bytes()
        inventory_sha = sha256_bytes(inventory_raw)
        inventory = json.loads(inventory_raw.decode("utf-8-sig"))
        if not isinstance(inventory, dict) or not isinstance(inventory.get("packages"), list) or not inventory["packages"]:
            raise ValueError("no package list")
    except (OSError, ValueError) as error:
        inventory = None
        _finish(rows["package-adoption"], ["package inventory %s is not readable: %s" % (inventory_path, error)], [])

    # Release identity: canonical version, explicit stable intent (already checked), accepted major class.
    baseline = None
    identity_failures = []
    if inventory is not None:
        baselines = []
        for package in inventory["packages"]:
            if package.get("baselineVersion"):
                try:
                    baselines.append(parse_version(str(package["baselineVersion"])))
                except VersionError:
                    identity_failures.append("inventory baselineVersion %r is not canonical" % package["baselineVersion"])
        for candidate_baseline in baselines:
            if baseline is None or compare_versions(candidate_baseline, baseline) > 0:
                baseline = candidate_baseline
    if baseline is None:
        identity_failures.append("the inventory declares no published API baseline to classify the release against")
    else:
        identity_failures.extend(release_class_problems(version, baseline))
    rows["release-identity"]["details"] = {
        "canonical": version.text, "kind": "stable" if version.is_stable else "prerelease", "releasePreparationIntent": release_preparation,
        "publishedBaseline": baseline.text if baseline else None, "releaseClass": classify_release(version, baseline) if baseline else None,
        "acceptedReleaseClass": ACCEPTED_RELEASE_CLASS, "rationale": RELEASE_CLASS_RATIONALE,
        "intentNote": "-ReleasePreparation is an intent guard for a stable identity, never publication approval"}
    _finish(rows["release-identity"], identity_failures, [])

    # Package adoption (byte-for-byte; never pack, rebuild or rename).
    adoption = None
    if inventory is not None:
        try:
            failures, adoption = _adopt_feed(feed, version, inventory, inventory_sha, staging, repository)
        except (OSError, ValueError) as error:
            failures = ["the feed could not be adopted: %s" % error]
        if adoption is not None:
            rows["package-adoption"]["details"] = {"packageManifestSha256": adoption["manifestSha256"], "packages": len(adoption["packages"]),
                                                   "dependencyOrder": adoption["dependencyOrder"]}
            rows["package-adoption"]["evidence"] = ["packages/" + adoption["manifestName"]]
        _finish(rows["package-adoption"], failures, [])
    source_commit = adoption["sourceCommit"] if adoption and COMMIT.match(str(adoption["sourceCommit"] or "")) else None

    # Source identity: HEAD is S, clean tree, archive verified per git blob.
    source_failures, tree, file_count, initial = [], None, 0, None
    try:
        state = initial = source.state()
        if source_commit is None:
            source_failures.append("the package manifest names no valid source commit S")
        elif state["head"] != source_commit:
            source_failures.append("repository HEAD %s is not the packed source commit S %s" % (state["head"], source_commit))
        if state["dirty"]:
            count = state.get("count", len(state.get("status") or []))
            source_failures.append("the working tree has uncommitted or untracked changes (%d entries); S must be pristine" % count)
        if not source_failures:
            tree = source.tree(source_commit)
            (staging / "source").mkdir(exist_ok=True)
            records, problems = source.export(source_commit, staging / "source" / "source.tar")
            source_failures.extend(problems)
            file_count = len(records)
            (staging / "source" / "source-files.json").write_bytes(canonical_json({"commit": source_commit, "tree": tree, "files": records}))
            if inventory_raw is not None:
                (staging / "source" / "packages.json").write_bytes(inventory_raw)
            (staging / "source" / "tooling.json").write_bytes(canonical_json(_tooling(repo)))
            rows["source-identity"]["evidence"] = ["source/source.tar", "source/source-files.json", "source/packages.json", "source/tooling.json"]
    except (SourceError, OSError, tarfile.TarError, ValueError) as error:
        source_failures.append("source identity could not be read: %s" % error)
    rows["source-identity"]["details"] = {"commit": source_commit, "tree": tree, "files": file_count}
    _finish(rows["source-identity"], source_failures, [])

    # Remote availability snapshot.
    ids = [package.get("id") for package in (inventory or {}).get("packages") or []]
    try:
        snapshot = load_availability(availability) if availability is not None and Path(availability).is_file() else None
        if snapshot is not None:
            _copy_exact(Path(availability), staging / "evidence" / "availability-snapshot.json")
        rows["remote-availability"] = availability_row(snapshot, ids, version, baseline or version)
        if snapshot is not None:
            rows["remote-availability"]["evidence"] = ["evidence/availability-snapshot.json"]
    except (OSError, ValueError) as error:
        rows["remote-availability"] = _finish(_row("remote-availability"), ["remote availability snapshot is not usable: %s" % error], [])

    # E4: recompute the aggregate gate over the staged copies of the packages and the evidence.
    manifest_sha = adoption["manifestSha256"] if adoption else None
    packages_map = {}
    if adoption:
        for record in adoption["record"].get("packages") or []:
            if isinstance(record, dict):
                packages_map[record.get("id")] = (record.get("sha256"), record.get("symbolsSha256"))
    staged_evidence = None
    summary = None
    if gate_evidence is not None and Path(gate_evidence).is_dir():
        staged_evidence = staging / "evidence" / "e4"
        staged_evidence.mkdir(parents=True)
        copy_problems = []
        _copy_tree_exact(Path(gate_evidence), staged_evidence, copy_problems, "E4 evidence")
        if copy_problems:
            rows["e4-aggregate"] = _finish(_row("e4-aggregate"), copy_problems, [])
            staged_evidence = False
    if staged_evidence is not False:
        row, summary = evaluate_e4_gate(repo, policy_path, staging / "packages", staged_evidence, version.text, source_commit, manifest_sha,
                                        packages_map, gate_evaluator)
        if summary is not None:
            summary_path = staging / "evidence" / "e4-candidate-summary.json"
            summary_path.parent.mkdir(parents=True, exist_ok=True)
            summary_path.write_bytes(canonical_json(summary))
            row["evidence"] = ["evidence/e4-candidate-summary.json"]
            row["details"]["summarySha256"] = sha256_file(summary_path)
        rows["e4-aggregate"] = row

    # E7 lifecycle validator (required gate; missing or failing never prepares).
    e7_output = staging / "evidence" / "e7-deprecation-validation.json"
    e7_output.parent.mkdir(parents=True, exist_ok=True)
    validated_lanes = ([entry["id"] for entry in summary["lanes"] if entry["state"] == "PASS"]
                       if summary is not None and rows["e4-aggregate"]["state"] == "pass" else [])
    semantic_input = deprecation_consumer_results(staging / "evidence/e4", validated_lanes)
    immutable_candidate = staging / "packages" / ("package-manifest.%s.json" % version.text)
    if semantic_input is None or not immutable_candidate.is_file():
        rows["e7-lifecycle"] = _finish(_row("e7-lifecycle"), [], ["E7 requires one complete E4-validated old/new consumer lane and the immutable package manifest"])
    else:
        if deprecation_runner is run_deprecation_validator:
            rows["e7-lifecycle"] = deprecation_runner(repo, e7_output, candidate=immutable_candidate, consumer_results=semantic_input)
        else:
            rows["e7-lifecycle"] = deprecation_runner(repo, e7_output)  # Controlled test seam; its PASS row is never sufficient.
        if e7_output.is_file():
            actual_exit = 0 if rows["e7-lifecycle"]["state"] == "pass" else 1
            rows["e7-lifecycle"] = validate_deprecation_output(e7_output, actual_exit, repo / "docs/deprecations.md",
                candidate=immutable_candidate, consumer_results=semantic_input, repo=repo)
    if e7_output.is_file():
        rows["e7-lifecycle"]["evidence"] = ["evidence/e7-deprecation-validation.json"]

    # Receipts, governance and release notes are copied into the bundle and judged from the copies.
    receipts = staging / "evidence" / "receipts"
    staged = _optional_copy(e6_receipt, receipts / "e6-maintenance.json")
    rows["e6-maintenance"] = release_receipt_row("e6-maintenance", staged, "e6-maintenance", source_commit, version.text, manifest_sha, False)
    staged = _optional_copy(final_review_receipt, receipts / "final-review.json")
    rows["final-review"] = release_receipt_row("final-review", staged, "final-review", source_commit, version.text, manifest_sha, True,
        e4_summary=summary if rows["e4-aggregate"]["state"] == "pass" else None, e4_evidence=staged_evidence,
        e4_policy_sha256=sha256_file(policy_path) if policy is not None else None)
    protection = _optional_copy(governance_protection, receipts / "governance-protection.json")
    rulesets = _optional_copy(governance_rulesets, receipts / "governance-rulesets.json")
    if policy is None:
        rows["governance"] = _finish(_row("governance"), ["required-check policy %s is not readable" % policy_path], [])
    else:
        rows["governance"] = governance_row(protection, rulesets, repository, required_check_names(policy))
    notes = _optional_copy(release_notes, staging / "metadata" / "release-notes.md")
    rows["release-notes"] = release_notes_row(notes, version)
    for name in ("CHANGELOG.md", "LICENSE", "NOTICE"):
        if (repo / name).is_file():
            _copy_exact(repo / name, staging / "metadata" / name)
    for row_id, name in (("e6-maintenance", "receipts/e6-maintenance.json"), ("final-review", "receipts/final-review.json"),
                         ("governance", "receipts/governance-protection.json")):
        if (staging / "evidence" / name).is_file():
            rows[row_id]["evidence"] = ["evidence/" + name]
    if notes is not None:
        rows["release-notes"]["evidence"] = ["metadata/release-notes.md"]

    # Freeze only if the source identity still matches after everything else.
    try:
        later = source.state()
        changed = initial is None or later["head"] != initial["head"] or bool(later["dirty"]) != bool(initial["dirty"])
        if changed and (later["head"] != source_commit or later["dirty"]):
            rows["source-identity"]["reasons"].append("the source changed during preparation (HEAD %s, dirty %s)" % (later["head"], later["dirty"]))
            rows["source-identity"]["state"] = "fail"
    except SourceError as error:
        rows["source-identity"]["reasons"].append("the source identity could not be re-read: %s" % error)
        rows["source-identity"]["state"] = "fail"

    ordered = [rows[row_id] for row_id in ROW_ORDER]
    report["rows"] = ordered
    report["failures"] = ["%s: %s" % (r["id"], reason) for r in ordered if r["state"] == "fail" for reason in r["reasons"]]
    report["blockers"] = ["%s: %s" % (r["id"], reason) for r in ordered if r["state"] == "blocked" for reason in r["reasons"]]
    if report["failures"] or report["blockers"]:
        return _quarantine(report, staging, candidate_dir, candidate_id, EXIT_FAIL if report["failures"] else EXIT_BLOCKED)

    files, problems = _allowlist(staging)
    candidate = {
        "schema": CANDIDATE_SCHEMA, "candidateId": candidate_id, "createdAtUtc": utc_now(), "status": "prepared", "repository": repository,
        "version": dict(rows["release-identity"]["details"], tag="v" + version.text),
        "source": {"commit": source_commit, "tree": tree, "dirty": False, "archive": "source/source.tar", "fileList": "source/source-files.json",
                   "fileCount": file_count, "exclusions": [
                       "untracked, ignored and generated working-tree files are excluded by construction: source/source.tar is git archive of S",
                       "this bundle, its evidence and promotion receipts live outside S; S never references its own artifacts"]},
        "inventory": {"path": str(inventory_path.relative_to(repo).as_posix()), "sha256": inventory_sha, "snapshot": "source/packages.json"},
        "packageManifest": {"path": "packages/" + adoption["manifestName"], "sha256": manifest_sha},
        "packages": adoption["packages"], "dependencyOrder": adoption["dependencyOrder"], "files": files,
        "evidencePolicy": {"version": EVIDENCE_POLICY_VERSION, "rows": [
            {"id": r["id"], "title": r["title"], "required": True, "state": r["state"], "evidence": r["evidence"], "details": r["details"]}
            for r in ordered]},
        "targets": _targets(version, source_commit, adoption, files),
        "tooling": _tooling(repo),
        "limitations": [
            "local preparation only; no remote state was read or changed by preparation except through the supplied snapshots",
            EXECUTION_NOTE,
            "the SBOM is the package manifest's private analyzer build provenance (custom format), not CycloneDX or SPDX",
            "remote availability and governance are snapshots; re-read them before authorization and during promotion",
            "NuGet signature trust, symbol processing and fresh signed-remote consumer verification are separate external steps"]}
    data = canonical_json(candidate)
    (staging / "candidate.json").write_bytes(data)
    digest = sha256_bytes(data)
    verification = verify_bundle(staging / "candidate.json", digest)
    if problems or not verification["ok"]:
        report["failures"] = ["self-verification: %s" % p for p in problems + verification["problems"]]
        return _quarantine(report, staging, candidate_dir, candidate_id, EXIT_FAIL)
    for item in files + [{"path": "candidate.json"}]:
        os.chmod(str(staging / item["path"]), stat.S_IRUSR | stat.S_IRGRP | stat.S_IROTH)
    anchor = parent / ("%s.approval-anchor.json" % candidate_dir.name)
    if os.path.lexists(candidate_dir) or os.path.lexists(anchor):
        report["failures"] = ["candidate directory %s or its approval anchor appeared during preparation" % candidate_dir]
        return _quarantine(report, staging, candidate_dir, candidate_id, EXIT_FAIL)
    os.rename(str(staging), str(candidate_dir))
    with open(anchor, "xb") as stream:
        stream.write(canonical_json({"schema": ANCHOR_SCHEMA, "candidateId": candidate_id, "candidateJson": str(candidate_dir / "candidate.json"),
                                     "candidateJsonSha256": digest, "repository": repository, "version": version.text, "sourceCommit": source_commit,
                                     "note": "SHA-256 of the exact candidate.json bytes. Present it with the target list for TRUSTED authorization. "
                                             "This file is a record outside the bundle, not authorization."}))
    report.update(status="prepared", exitCode=EXIT_OK, candidateDirectory=str(candidate_dir), candidateJsonSha256=digest, approvalAnchor=str(anchor),
                  note="local preparation complete; promotion pending trusted authorization (not available in this repository)")
    return EXIT_OK, report


def _quarantine(report, staging: Path, candidate_dir: Path, candidate_id: str, code: int) -> tuple:
    quarantine = candidate_dir.parent / ("%s.quarantine-%s" % (candidate_dir.name, candidate_id))
    report.update(status="failed" if code == EXIT_FAIL else "blocked", exitCode=code, quarantineDirectory=str(quarantine),
                  note="never resumed or repacked: a new attempt needs a new candidate id; quarantined bytes are kept for diagnosis")
    name = "preparation-report.json"
    if (staging / name).exists():
        name = "preparation-report.%s.json" % secrets.token_hex(4)
    with open(staging / name, "xb") as stream:
        stream.write(canonical_json(report))
    os.rename(str(staging), str(quarantine))
    return code, report


# --------------------------------------------------------------------------------------------------------------------
# Verification
# --------------------------------------------------------------------------------------------------------------------

def verify_bundle(manifest_path, expected_sha256) -> dict:
    """Exact-bytes digest, allowlist (presence, hash, size, no unlisted/symlinked/special files), safe unique paths,
    archive entries and nuspec identity, internal dependency graph and evidence rows."""
    if not isinstance(expected_sha256, str) or not HEX64.match(expected_sha256):
        raise UsageError("the approved candidate digest must be 64 lowercase hexadecimal characters")
    path = Path(manifest_path)
    report = {"schema": VERIFY_SCHEMA, "ok": False, "manifest": str(path), "expectedSha256": expected_sha256, "candidateSha256": None,
              "problems": [], "candidate": None}
    problems = report["problems"]
    if not os.path.lexists(str(path)):
        problems.append("candidate manifest %s is missing (lost bundle: restore byte-identical bytes from a verified retained copy; "
                        "rebuilding cannot recover the approved identity)" % path)
        return report
    mode = os.lstat(str(path)).st_mode
    if stat.S_ISLNK(mode) or not stat.S_ISREG(mode):
        problems.append("candidate manifest is not a regular file (symbolic link or special file)")
        return report
    raw = path.read_bytes()
    report["candidateSha256"] = sha256_bytes(raw)
    if report["candidateSha256"] != expected_sha256:
        problems.append("candidate.json digest %s is not the approved digest %s" % (report["candidateSha256"], expected_sha256))
        return report
    try:
        candidate = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as error:
        problems.append("candidate.json is not valid UTF-8 JSON: %s" % error)
        return report
    if not isinstance(candidate, dict) or candidate.get("schema") != CANDIDATE_SCHEMA:
        problems.append("candidate.json schema is not %s" % CANDIDATE_SCHEMA)
        return report
    report["candidate"] = candidate
    if candidate.get("status") != "prepared":
        problems.append("candidate status %r is not prepared" % candidate.get("status"))
    try:
        version_record = candidate.get("version") if isinstance(candidate.get("version"), dict) else {}
        version = parse_version(version_record.get("canonical"))
    except VersionError as error:
        problems.append("candidate version: %s" % error)
        return report
    bundle = path.resolve().parent
    files = candidate.get("files") if isinstance(candidate.get("files"), list) else []
    if not files:
        problems.append("candidate lists no files")
    listed, folded = {}, {}
    for item in files:
        rel = item.get("path") if isinstance(item, dict) else None
        reason = bundle_path_problem(rel)
        if reason:
            problems.append("allowlist path %r is unsafe: %s" % (rel, reason))
            continue
        if rel == "candidate.json":
            problems.append("candidate.json must not list itself")
            continue
        if rel in listed:
            problems.append("allowlist lists %s twice (duplicate path)" % rel)
            continue
        if rel.casefold() in folded:
            problems.append("allowlist paths %s and %s collide when case is ignored" % (rel, folded[rel.casefold()]))
            continue
        folded[rel.casefold()] = rel
        if item.get("role") not in FILE_ROLES or item.get("role") != _role_for(rel):
            problems.append("allowlist role %r of %s is not allowed" % (item.get("role"), rel))
        if not HEX64.match(str(item.get("sha256"))) or not isinstance(item.get("size"), int):
            problems.append("allowlist entry %s lacks a SHA-256 or size" % rel)
        listed[rel] = item
    root = os.path.realpath(str(bundle))
    for directory, directories, names in os.walk(str(bundle), followlinks=False):
        for name in directories:
            if os.path.islink(os.path.join(directory, name)):
                problems.append("bundle holds a symbolic link: %s" % os.path.relpath(os.path.join(directory, name), root))
        for name in names:
            full = os.path.join(directory, name)
            rel = PurePosixPath(*Path(os.path.relpath(full, str(bundle))).parts).as_posix()
            mode = os.lstat(full).st_mode
            if stat.S_ISLNK(mode):
                problems.append("bundle holds a symbolic link: %s" % rel)
            elif not stat.S_ISREG(mode):
                problems.append("bundle holds a special file: %s" % rel)
            elif rel != "candidate.json" and rel not in listed:
                problems.append("bundle holds an unlisted file: %s" % rel)
    contents = {}
    for rel, item in listed.items():
        full = bundle / rel
        if not os.path.lexists(str(full)):
            problems.append("listed file is missing: %s" % rel)
            continue
        if os.path.islink(str(full)) or not os.path.realpath(str(full)).startswith(root + os.sep):
            problems.append("listed file %s is a link or escapes the bundle" % rel)
            continue
        if not stat.S_ISREG(os.lstat(str(full)).st_mode):
            problems.append("listed path %s is not a regular file" % rel)
            continue
        data = full.read_bytes()
        if len(data) != item.get("size"):
            problems.append("%s size %d differs from the approved %s" % (rel, len(data), item.get("size")))
        if sha256_bytes(data) != item.get("sha256"):
            problems.append("%s sha256 %s differs from the approved %s" % (rel, sha256_bytes(data), item.get("sha256")))
        if item.get("role") in ("package", "symbols", "package-manifest"):
            contents[rel] = data
    try:
        _verify_packages(candidate, version, listed, contents, problems)
        _verify_bundle_closure(candidate, bundle, listed, problems)
        policy = candidate.get("evidencePolicy") if isinstance(candidate.get("evidencePolicy"), dict) else {}
        rows = policy.get("rows") if isinstance(policy.get("rows"), list) else []
        seen_rows = [r.get("id") for r in rows if isinstance(r, dict)]
        if sorted(str(r) for r in seen_rows) != sorted(ROW_ORDER) or len(rows) != len(ROW_ORDER):
            problems.append("evidence policy rows %s are not the required set" % seen_rows)
        for item in rows:
            if not isinstance(item, dict) or item.get("required") is not True or item.get("state") != "pass":
                problems.append("required evidence row %s is not pass" % (item.get("id") if isinstance(item, dict) else item))
                continue
            for rel in item.get("evidence") or []:
                if rel not in listed:
                    problems.append("evidence row %s names unlisted %s" % (item.get("id"), rel))
        _verify_targets(candidate, version, listed, problems)
    except (AttributeError, KeyError, TypeError, ValueError, OSError, tarfile.TarError) as error:
        # The digest matched, but the approved document itself is malformed: fail closed instead of raising.
        problems.append("candidate.json structure is malformed: %s: %s" % (type(error).__name__, error))
    report["ok"] = not problems
    return report


def _verify_packages(candidate: dict, version: Version, listed: dict, contents: dict, problems: list) -> None:
    packages = candidate.get("packages") if isinstance(candidate.get("packages"), list) else []
    if not packages:
        problems.append("candidate lists no packages")
    internal, seen = {}, set()
    for package in packages:
        pid = package.get("id") if isinstance(package, dict) else None
        if not isinstance(pid, str) or pid.casefold() in seen:
            problems.append("duplicate or missing package identity %r" % pid)
            continue
        seen.add(pid.casefold())
        internal[pid.casefold()] = pid
    source_commit = (candidate.get("source") or {}).get("commit")
    repository = str(candidate.get("repository") or "")
    dependencies = {}
    for package in packages:
        if not isinstance(package, dict):
            continue
        pid = package.get("id")
        if package.get("version") != version.text:
            problems.append("package %s version %r is not %s" % (pid, package.get("version"), version.text))
        for label, symbols in (("package", False), ("symbols", True)):
            approved = package.get(label)
            if approved is None:
                if label == "package" or package.get("kind") == "runtime":
                    problems.append("package %s has no %s record" % (pid, label))
                continue
            rel = approved.get("path")
            expected_name = "packages/%s.%s.%s" % (pid, version.text, "snupkg" if symbols else "nupkg")
            if rel != expected_name or rel not in listed or listed[rel].get("role") != ("symbols" if symbols else "package"):
                problems.append("package %s %s path %r is not the listed %s" % (pid, label, rel, expected_name))
                continue
            if approved.get("sha256") != listed[rel].get("sha256") or approved.get("size") != listed[rel].get("size"):
                problems.append("package %s %s record differs from the allowlist" % (pid, label))
            if rel not in contents:
                continue
            record, internal_deps, inspect_problems = _inspect_package(contents[rel], rel, pid, version, internal, repository, source_commit, symbols)
            problems.extend(inspect_problems)
            if record["entries"] != approved.get("entries"):
                problems.append("%s archive entries differ from the approved record" % rel)
            if record["nuspec"] != approved.get("nuspec"):
                problems.append("%s nuspec identity/dependencies differ from the approved record" % rel)
            if not symbols:
                if sorted(internal_deps) != sorted(package.get("internalDependencies") or []):
                    problems.append("%s internal dependency record differs from its nuspec" % pid)
                dependencies[pid] = set(package.get("internalDependencies") or [])
    ids = [p.get("id") for p in packages if isinstance(p, dict)]
    order, order_problems = dependency_order(ids, dependencies)
    problems.extend(order_problems)
    if candidate.get("dependencyOrder") != order:
        problems.append("dependencyOrder %s is not the dependency-first order %s" % (candidate.get("dependencyOrder"), order))
    manifest = candidate.get("packageManifest") or {}
    rel = manifest.get("path")
    if rel not in listed or listed[rel].get("role") != "package-manifest" or listed[rel].get("sha256") != manifest.get("sha256"):
        problems.append("package manifest %r is not the listed package-manifest file" % rel)
    elif rel in contents:
        try:
            record = json.loads(contents[rel].decode("utf-8-sig"))
            recorded = {p.get("id"): (p.get("sha256"), p.get("symbolsSha256")) for p in record.get("packages") or []}
            approved = {p.get("id"): ((p.get("package") or {}).get("sha256"), (p.get("symbols") or {}).get("sha256") if p.get("symbols") else None)
                        for p in packages if isinstance(p, dict)}
            if record.get("candidateVersion") != version.text or recorded != approved or record.get("sourceCommit") != source_commit:
                problems.append("package manifest record differs from the approved packages, version or source commit")
        except (ValueError, AttributeError) as error:
            problems.append("package manifest is not readable: %s" % error)


def _verify_bundle_closure(candidate: dict, bundle: Path, listed: dict, problems: list) -> None:
    """Require the source/evidence leaves independently of their self-reported pass rows."""
    source, inventory = candidate.get("source") or {}, candidate.get("inventory") or {}
    fixed = {"source/source.tar", "source/source-files.json", "source/packages.json", "source/tooling.json",
             "evidence/availability-snapshot.json", "evidence/e4-candidate-summary.json",
             "evidence/e7-deprecation-validation.json", "evidence/receipts/e6-maintenance.json",
             "evidence/receipts/final-review.json", "evidence/receipts/governance-protection.json",
             "metadata/release-notes.md", "metadata/LICENSE"}
    if fixed - set(listed):
        problems.append("mandatory source/evidence bundle leaves are missing: %s" % sorted(fixed - set(listed)))
        return
    if (source.get("archive") != "source/source.tar" or source.get("fileList") != "source/source-files.json" or
            inventory.get("snapshot") != "source/packages.json" or source.get("dirty") is not False or
            not COMMIT.fullmatch(str(source.get("commit"))) or not COMMIT.fullmatch(str(source.get("tree")))):
        problems.append("source/archive/file-list/inventory identities are incomplete or inconsistent")
        return
    if inventory.get("sha256") != listed["source/packages.json"]["sha256"]:
        problems.append("the source inventory hash differs from its snapshot")
    tooling = _read_json(bundle / "source/tooling.json")
    if tooling != candidate.get("tooling"):
        problems.append("the tooling snapshot differs from the approved tooling record")
    file_list = _read_json(bundle / "source/source-files.json")
    if not isinstance(file_list, dict) or file_list.get("commit") != source["commit"] or file_list.get("tree") != source["tree"]:
        problems.append("the source file-list identity differs from S")
        return
    expected = file_list.get("files")
    if not isinstance(expected, list) or not expected or type(source.get("fileCount")) is not int or source["fileCount"] != len(expected):
        problems.append("source file-list/count is missing or malformed")
        return
    actual, seen, source_hashes = [], set(), {}
    with tarfile.open(bundle / "source/source.tar") as archive:
        if archive.pax_headers.get("comment") != source["commit"]:
            problems.append("source archive commit differs from S")
        for member in archive:
            if member.isdir():
                continue
            if _name_problem(member.name, portable=False) or member.name in seen or not (member.isfile() or member.issym()):
                problems.append("source archive has an unsafe, duplicate or unsupported member")
                continue
            seen.add(member.name)
            data = member.linkname.encode("utf-8", "surrogateescape") if member.issym() else archive.extractfile(member).read()
            source_hashes[member.name] = sha256_bytes(data)
            mode = "120000" if member.issym() else ("100755" if member.mode & 0o111 else "100644")
            actual.append({"path": member.name, "type": "symlink" if member.issym() else "file", "mode": mode,
                           "size": len(data), "sha256": sha256_bytes(data), "gitBlob": git_blob_id(data)})
            if member.name == inventory.get("path") and sha256_bytes(data) != inventory.get("sha256"):
                problems.append("source archive inventory differs from the package inventory snapshot")
    if sorted(actual, key=lambda item: item["path"]) != expected:
        problems.append("source archive contents differ from the complete source file-list")
    if inventory.get("path") not in seen:
        problems.append("the package inventory is missing from source S")
    tooling_files = tooling.get("files") if isinstance(tooling, dict) else None
    if not isinstance(tooling_files, dict) or not tooling_files or any(
            digest != source_hashes.get(path) for path, digest in (tooling_files or {}).items()):
        problems.append("tooling hashes differ from their source S archive contents")
    inventory_document = _read_json(bundle / "source/packages.json")
    records = inventory_document.get("packages") if isinstance(inventory_document, dict) else None
    if (not isinstance(records, list) or not all(isinstance(p, dict) and isinstance(p.get("id"), str) for p in records) or
            sorted(p["id"] for p in records) != sorted(p["id"] for p in candidate["packages"])):
        problems.append("the source inventory package closure differs from the approved packages")
    if not any(path.startswith("evidence/e4/lanes/") and path.endswith("/lane-result.json") for path in listed):
        problems.append("the complete retained E4 lane evidence is missing")
    required = {"source-identity": {"source/source.tar", "source/source-files.json", "source/packages.json", "source/tooling.json"},
                "package-adoption": {candidate["packageManifest"]["path"]},
                "remote-availability": {"evidence/availability-snapshot.json"},
                "e4-aggregate": {"evidence/e4-candidate-summary.json"}, "e7-lifecycle": {"evidence/e7-deprecation-validation.json"},
                "e6-maintenance": {"evidence/receipts/e6-maintenance.json"},
                "final-review": {"evidence/receipts/final-review.json"}, "governance": {"evidence/receipts/governance-protection.json"},
                "release-notes": {"metadata/release-notes.md"}}
    rows = {row["id"]: row for row in candidate["evidencePolicy"]["rows"]}
    if type(candidate["evidencePolicy"].get("version")) is not int or candidate["evidencePolicy"]["version"] != EVIDENCE_POLICY_VERSION:
        problems.append("evidence policy version is not supported")
    for row_id, paths in required.items():
        if not paths.issubset(rows.get(row_id, {}).get("evidence") or []):
            problems.append("evidence row %s lacks its mandatory artifact references" % row_id)
    if not problems:
        _verify_retained_e4(candidate, bundle, problems)


def _verify_retained_e4(candidate: dict, bundle: Path, problems: list) -> None:
    """Re-derive every required E4 lane and E7 lifecycle leaf from archived source/raw data.

    No code from the archive is imported, compiled or executed, and links are never
    created. The temporary regular source files supply only policy/baseline/source
    bytes to the same trusted E4/E7 parsers used during preparation.
    """
    gate = _load_candidate_gate()
    with tempfile.TemporaryDirectory(prefix="meridian-e5-e4-source-") as directory:
        repo = Path(directory)
        with tarfile.open(bundle / "source/source.tar") as archive:
            for member in archive:
                if member.isfile():
                    if _name_problem(member.name, portable=False):
                        problems.append("unsafe source path during E4 verification")
                        return
                    target = repo / member.name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(archive.extractfile(member).read())
        policy = _read_json(repo / "eng/required-checks.json")
        reference = policy.get("packageInventory") if isinstance(policy, dict) else None
        if bundle_path_problem(reference) or reference != candidate["inventory"]["path"]:
            problems.append("the archived E4 policy references another package inventory")
            return
        try:
            summary = gate.evaluate(policy, bundle / "packages", bundle / "evidence/e4", candidate["version"]["canonical"],
                host=None, allow_pending=False, only=None, source_commit=candidate["source"]["commit"], repo=repo,
                expected_manifest_sha256=candidate["packageManifest"]["sha256"])
        except gate.PolicyError as error:
            problems.append("the archived E4 policy is invalid: %s" % error)
            return
        packages = {p["id"]: (p["package"]["sha256"], p["symbols"]["sha256"] if p["symbols"] else None)
                    for p in candidate["packages"]}
        judged = judge_gate_summary(summary, candidate["version"]["canonical"], candidate["packageManifest"]["sha256"],
                                    candidate["source"]["commit"], packages)
        if judged["state"] != "pass":
            problems.extend("retained E4 evidence: %s" % reason for reason in judged["reasons"])
            return
        recorded = _read_json(bundle / "evidence/e4-candidate-summary.json")
        # Recomputed summaries have a new timestamp/path context; their verdict,
        # candidate identity and complete lane states must still agree.
        if (recorded.get("overall") != summary["overall"] or recorded.get("releaseEligible") is not True or
                recorded.get("candidate") != summary.get("candidate") or
                [(r.get("id"), r.get("state")) for r in recorded.get("lanes", [])] !=
                [(r.get("id"), r.get("state")) for r in summary["lanes"]]):
            problems.append("the recorded E4 summary differs from the fully re-derived candidate/lanes")
        review = release_receipt_row("final-review", bundle / "evidence/receipts/final-review.json", "final-review",
            candidate["source"]["commit"], candidate["version"]["canonical"], candidate["packageManifest"]["sha256"], True,
            e4_summary=summary, e4_evidence=bundle / "evidence/e4", e4_policy_sha256=sha256_file(repo / "eng/required-checks.json"))
        if review["state"] != "pass":
            problems.extend("retained final-review evidence: %s" % reason for reason in review["reasons"])
        lifecycle = validate_deprecation_output(bundle / "evidence/e7-deprecation-validation.json", 0, repo / "docs/deprecations.md",
            candidate=bundle / candidate["packageManifest"]["path"],
            consumer_results=deprecation_consumer_results(bundle / "evidence/e4", [entry["id"] for entry in summary["lanes"]
                                                                                   if entry["state"] == "PASS"]), repo=repo)
        if lifecycle["state"] != "pass" or lifecycle["details"].get("candidateBound") is not True:
            problems.extend("retained E7 evidence: %s" % reason for reason in lifecycle["reasons"])


def _verify_targets(candidate: dict, version: Version, listed: dict, problems: list) -> None:
    packages = candidate["packages"]
    if [package["id"] for package in packages] != candidate["dependencyOrder"]:
        problems.append("the package closure itself is not dependency-first")
    adoption = {"packages": packages, "manifestName": PurePosixPath(candidate["packageManifest"]["path"]).name}
    expected = _targets(version, candidate["source"]["commit"], adoption, list(listed.values()))
    if candidate.get("targets") != expected:
        problems.append("promotion targets differ from the exact ordered source/package/asset/dependency projection")
    targets = candidate.get("targets") if isinstance(candidate.get("targets"), list) else []
    if not targets:
        problems.append("candidate lists no promotion targets")
    source_commit = (candidate.get("source") or {}).get("commit")
    keys, names = set(), set()
    for target in targets:
        if not isinstance(target, dict) or target.get("key") in keys:
            problems.append("duplicate or malformed target %r" % target)
            continue
        keys.add(target.get("key"))
        kind = target.get("kind")
        if kind == "git-tag" and (target.get("tag") != "v" + version.text or target.get("commit") != source_commit):
            problems.append("tag target must be v%s at S" % version.text)
        if kind == "github-release" and (target.get("tag") != "v" + version.text or target.get("notes") not in listed
                                         or listed[target["notes"]].get("role") != "metadata"):
            problems.append("release target must be v%s with listed metadata release notes" % version.text)
        if kind == "github-release-asset":
            if target.get("name") in names:
                problems.append("release asset name %s is not unique" % target.get("name"))
            names.add(target.get("name"))
            if target.get("path") == "candidate.json":
                if target.get("digestSource") != "approved-candidate-digest":
                    problems.append("candidate.json asset must be bound to the approved digest")
            elif target.get("path") not in listed or listed[target["path"]].get("sha256") != target.get("sha256"):
                problems.append("release asset %s is not a listed bundle file" % target.get("name"))
        if kind in ("nuget-package", "nuget-symbols") and (target.get("path") not in listed or listed[target["path"]].get("sha256") != target.get("sha256")):
            problems.append("target %s is not a listed bundle file" % target.get("key"))
    nuget = [t.get("id") for t in targets if isinstance(t, dict) and t.get("kind") == "nuget-package"]
    if nuget != candidate.get("dependencyOrder"):
        problems.append("NuGet targets %s are not in dependency-first order" % nuget)


# --------------------------------------------------------------------------------------------------------------------
# Receipts, transports and authorization boundaries
# --------------------------------------------------------------------------------------------------------------------

def _receipt_line(record: dict) -> bytes:
    return (json.dumps(record, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n").encode("ascii")


class ReceiptStream:
    """Durable append-only promotion receipts: hash-chained JSON lines OUTSIDE the frozen bundle. Never rewritten."""

    def __init__(self, path):
        self.path = Path(path)

    def _scan(self):
        if not self.path.exists():
            return [], [], ZERO
        raw = self.path.read_bytes()
        events, problems, previous = [], [], ZERO
        if raw and not raw.endswith(b"\n"):
            problems.append("the receipt stream ends with a partial line (interrupted write); it is never truncated automatically")
        for number, line in enumerate(raw.splitlines(keepends=True), 1):
            if not line.endswith(b"\n"):
                break
            try:
                event = json.loads(line.decode("ascii"))
            except (UnicodeDecodeError, ValueError):
                problems.append("receipt line %d is not valid JSON" % number)
                break
            if not isinstance(event, dict):
                problems.append("receipt line %d is not an object" % number)
                break
            body = {k: v for k, v in event.items() if k != "eventSha256"}
            if event.get("schema") != RECEIPT_SCHEMA or event.get("sequence") != number:
                problems.append("receipt line %d has a wrong schema or sequence" % number)
            if event.get("previousSha256") != previous:
                problems.append("receipt chain is broken at line %d" % number)
            if event.get("eventSha256") != sha256_bytes(_receipt_line(body)):
                problems.append("receipt line %d content does not match its digest" % number)
            previous = sha256_bytes(line)
            events.append(event)
        return events, problems, previous

    def read(self):
        events, problems, _ = self._scan()
        return events, problems

    def append(self, event: dict) -> dict:
        events, problems, previous = self._scan()
        if problems:
            raise ReceiptError("refusing to append to a corrupted receipt stream: " + "; ".join(problems))
        record = dict(event)
        record.update({"schema": RECEIPT_SCHEMA, "sequence": len(events) + 1, "previousSha256": previous, "atUtc": utc_now()})
        record.pop("eventSha256", None)
        record["eventSha256"] = sha256_bytes(_receipt_line(record))
        descriptor = os.open(str(self.path), os.O_WRONLY | os.O_APPEND | os.O_CREAT, 0o600)
        try:
            os.write(descriptor, _receipt_line(record))
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
        return record


def final_states(events: list) -> dict:
    states = {}
    for event in events:
        if event.get("event") == "target" and event.get("target"):
            states[event["target"]] = event.get("state")
    return states


def _first_intents(events: list) -> dict:
    intents = {}
    for event in events:
        if event.get("event") == "target" and event.get("state") == "requested" and event.get("target") not in intents:
            intents[event["target"]] = event.get("sequence")
    return intents


class ReadTransport:
    """Narrow read interface over remote truth (GitHub + NuGet)."""

    name = "abstract-read"

    def source_commit(self, repository, commit):
        raise NotImplementedError

    def tag(self, repository, tag):
        raise NotImplementedError

    def release(self, repository, tag):
        raise NotImplementedError

    def release_asset(self, repository, asset):
        raise NotImplementedError

    def nuget_versions(self, package_id):
        raise NotImplementedError

    def nuget_package(self, package_id, version):
        raise NotImplementedError


class WriteTransport(ReadTransport):
    """Write interface used only by ``execute_promotion``. This repository ships NO implementation of it."""

    name = "abstract-write"

    def create_tag(self, repository, tag, commit):
        raise NotImplementedError

    def create_release(self, repository, tag, name, body, prerelease):
        raise NotImplementedError

    def upload_release_asset(self, repository, release_id, name, data):
        raise NotImplementedError

    def push_nuget(self, package_id, version, data):
        raise NotImplementedError

    def push_symbols(self, package_id, version, data):
        raise NotImplementedError


class TrustedAuthorizationVerifier:
    """Boundary for TRUSTED human authorization bound to digest, repository, version, targets and source commit.
    This repository ships NO implementation; a JSON document claiming approval is never an instance of it."""

    name = "abstract"

    def verify(self, request: dict) -> dict:
        raise NotImplementedError


class _StripCrossHostAuthorization(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        destination = urllib.parse.urlsplit(newurl)
        origin = urllib.parse.urlsplit(req.full_url)
        if destination.scheme.lower() != "https":
            raise TransportError("a read-only release request cannot redirect to a non-HTTPS origin")
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        def identity(parsed):
            return parsed.scheme.lower(), parsed.hostname, parsed.port or 443
        if new is not None and identity(destination) != identity(origin):
            for collection in (new.headers, new.unredirected_hdrs):
                for name in list(collection):
                    if name.lower() == "authorization":
                        del collection[name]
        return new


class HttpReadTransport(ReadTransport):
    """GET-only HTTPS reads of GitHub and the NuGet flat container. It has no write method. A token (read from
    GITHUB_TOKEN by the command line) is sent only to api.github.com and never printed."""

    name = "github-nuget-readonly-https"

    def __init__(self, opener=None, token=None, timeout=60, github_api=GITHUB_API, nuget=NUGET_FLAT_CONTAINER):
        self._opener = opener or urllib.request.build_opener(_StripCrossHostAuthorization())
        self._token, self._timeout, self._github, self._nuget = token, timeout, github_api.rstrip("/"), nuget.rstrip("/")

    def _get(self, url: str, accept: str = "application/json") -> tuple:
        origin = urllib.parse.urlsplit(url)
        if origin.scheme.lower() != "https" or not origin.hostname or origin.username or origin.password:
            raise TransportError("release reads require an HTTPS URL without user information")
        headers = {"Accept": accept, "User-Agent": "meridian-release-candidate/1"}
        if self._token and origin.hostname == "api.github.com" and (origin.port or 443) == 443:
            headers["Authorization"] = "Bearer " + self._token
        request = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with self._opener.open(request, timeout=self._timeout) as response:
                return getattr(response, "status", 200), response.read()
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return error.code, b""
            raise TransportError("GET %s failed with HTTP %s" % (url, error.code)) from error
        except (socket.timeout, TimeoutError) as error:
            raise TransportTimeout("GET %s timed out" % url) from error
        except urllib.error.URLError as error:
            if isinstance(error.reason, (socket.timeout, TimeoutError)):
                raise TransportTimeout("GET %s timed out" % url) from error
            raise TransportError("GET %s failed: %s" % (url, error.reason)) from error

    def _json(self, url: str):
        status, body = self._get(url)
        return status, (json.loads(body.decode("utf-8")) if status == 200 else None)

    def _repo(self, repository: str) -> str:
        if not REPOSITORY_PATTERN.match(repository):
            raise TransportError("invalid repository %r" % repository)
        return "%s/repos/%s" % (self._github, repository)

    def source_commit(self, repository, commit):
        status, _ = self._get("%s/commits/%s" % (self._repo(repository), urllib.parse.quote(commit, safe="")))
        return {"state": "present" if status == 200 else "absent"}

    def tag(self, repository, tag):
        status, document = self._json("%s/git/ref/tags/%s" % (self._repo(repository), urllib.parse.quote(tag, safe="")))
        if status != 200:
            return {"state": "absent"}
        target = document.get("object") or {}
        if target.get("type") == "tag":
            status, annotated = self._json("%s/git/tags/%s" % (self._repo(repository), urllib.parse.quote(str(target.get("sha")), safe="")))
            if status != 200:
                raise TransportError("annotated tag object of %s is unreadable" % tag)
            target = annotated.get("object") or {}
        return {"state": "present", "commit": target.get("sha")}

    def release(self, repository, tag):
        status, document = self._json("%s/releases/tags/%s" % (self._repo(repository), urllib.parse.quote(tag, safe="")))
        if status != 200:
            return {"state": "absent"}
        return {"state": "present", "id": document.get("id"), "tag": document.get("tag_name"),
                "name": document.get("name"), "body": document.get("body"), "prerelease": document.get("prerelease"),
                "draft": document.get("draft"),
                "assets": [{"name": a.get("name"), "size": a.get("size"), "id": a.get("id"), "url": a.get("browser_download_url"), "release": tag}
                           for a in document.get("assets") or []]}

    def release_asset(self, repository, asset):
        url = str(asset.get("url") or "")
        if not url.lower().startswith(("https://github.com/%s/releases/download/" % repository).lower()):
            raise TransportError("release asset URL %r is not a download of %s" % (url, repository))
        status, body = self._get(url, accept="application/octet-stream")
        if status != 200:
            raise TransportError("release asset %s is not downloadable" % asset.get("name"))
        return body

    def nuget_versions(self, package_id):
        status, document = self._json("%s/%s/index.json" % (self._nuget, urllib.parse.quote(package_id.lower(), safe="")))
        if status != 200:
            return {"state": "absent"}
        versions = document.get("versions") if isinstance(document, dict) else None
        if not isinstance(versions, list):
            raise TransportError("NuGet returned a malformed versions list")
        try:
            for version in versions:
                parse_version(version)
        except VersionError as error:
            raise TransportError("NuGet returned a non-canonical package version") from error
        return {"state": "present", "versions": versions}

    def nuget_package(self, package_id, version):
        lowered, lowered_version = package_id.lower(), version.lower()
        status, body = self._get("%s/%s/%s/%s.%s.nupkg" % (self._nuget, urllib.parse.quote(lowered, safe=""), urllib.parse.quote(lowered_version, safe=""),
                                                           urllib.parse.quote(lowered, safe=""), urllib.parse.quote(lowered_version, safe="")),
                                 accept="application/octet-stream")
        return body if status == 200 else None


# --------------------------------------------------------------------------------------------------------------------
# Promotion planning and (test-API-only) execution
# --------------------------------------------------------------------------------------------------------------------

def _snapshot(manifest_path, approved: str, workdir=None) -> tuple:
    """Verify the bundle, copy it into a private snapshot and verify the copy; promotion reads only the snapshot."""
    verification = verify_bundle(manifest_path, approved)
    if not verification["ok"]:
        return None, verification
    root = Path(tempfile.mkdtemp(prefix="meridian-promotion-", dir=workdir))
    bundle = Path(manifest_path).resolve().parent
    try:
        _copy_exact(bundle / "candidate.json", root / "candidate.json")
        for item in verification["candidate"]["files"]:
            _copy_exact(bundle / item["path"], root / item["path"])
        second = verify_bundle(root / "candidate.json", approved)
    except OSError as error:
        shutil.rmtree(str(root), ignore_errors=True)
        verification["ok"] = False
        verification["problems"].append("private snapshot failed: %s" % error)
        return None, verification
    if not second["ok"]:
        shutil.rmtree(str(root), ignore_errors=True)
        return None, second
    return root, second


def _expected_bytes(target: dict, snapshot: Path) -> bytes:
    return (snapshot / target["path"]).read_bytes()


def _release_metadata_problems(target: dict, observed: dict, snapshot: Path) -> list:
    expected = {"tag": target["tag"], "name": target["name"],
                "body": (snapshot / target["notes"]).read_text(encoding="utf-8"),
                "prerelease": target["prerelease"], "draft": False}
    problems = []
    for key, value in expected.items():
        actual = observed.get(key)
        if actual != value or (type(value) is bool and type(actual) is not bool):
            problems.append("release %s differs from the approved metadata" % key)
    if type(observed.get("id")) is not int or observed["id"] < 1:
        problems.append("release identity is missing or invalid")
    return problems


def _observe(candidate: dict, snapshot: Path, transport, signature_verifier) -> dict:
    repository, source_commit = candidate["repository"], candidate["source"]["commit"]
    version = parse_version(candidate["version"]["canonical"])
    observations, release = {}, None
    for target in candidate["targets"]:
        key, kind = target["key"], target["kind"]
        try:
            if transport is None and kind in ("source-commit", "git-tag", "github-release", "github-release-asset", "nuget-package"):
                observations[key] = {"state": "unknown", "error": "offline: remote truth was not read"}
            elif kind == "source-commit":
                observations[key] = dict(transport.source_commit(repository, source_commit))
            elif kind == "git-tag":
                observations[key] = dict(transport.tag(repository, target["tag"]))
            elif kind == "github-release":
                release = dict(transport.release(repository, target["tag"]))
                expected = {t["name"] for t in candidate["targets"] if t["kind"] == "github-release-asset"}
                release["unexpectedAssets"] = sorted(a.get("name") for a in release.get("assets") or [] if a.get("name") not in expected)
                if release.get("state") == "present":
                    release["metadataProblems"] = _release_metadata_problems(target, release, snapshot)
                observations[key] = release
            elif kind == "github-release-asset":
                if release is None or release.get("state") not in ("present", "absent"):
                    observations[key] = {"state": "unknown", "error": "the release state is unknown"}
                elif release["state"] == "absent":
                    observations[key] = {"state": "absent"}
                else:
                    asset = next((a for a in release.get("assets") or [] if a.get("name") == target["name"]), None)
                    if asset is None:
                        observations[key] = {"state": "absent"}
                    else:
                        downloaded = transport.release_asset(repository, asset)
                        comparison = compare_release_asset(_expected_bytes(target, snapshot), downloaded)
                        observations[key] = {"state": "present", "equal": comparison["equal"], "uploadSha256": comparison["uploadSha256"],
                                             "downloadSha256": comparison["downloadSha256"]}
            elif kind == "nuget-package":
                observations[key] = _observe_nuget(target, snapshot, transport, signature_verifier, version)
            elif kind == "nuget-symbols":
                observations[key] = {"state": "not-observable", "detail": SYMBOLS_LIMIT}
            elif kind == "consumer-verification":
                observations[key] = {"state": "external"}
            elif kind == "site":
                observations[key] = {"state": "not-prepared"}
            else:
                observations[key] = {"state": "unknown", "error": "unknown target kind %s" % kind}
        except TransportTimeout as error:
            observations[key] = {"state": "unknown", "error": "timeout: %s" % error}
        except (TransportError, OSError, ValueError, KeyError, TypeError) as error:
            observations[key] = {"state": "unknown", "error": "%s: %s" % (type(error).__name__, error)}
    return observations


def _observe_nuget(target: dict, snapshot: Path, transport, signature_verifier, version: Version) -> dict:
    listing = transport.nuget_versions(target["id"])
    if listing.get("state") == "absent":
        return {"state": "absent"}
    if listing.get("state") != "present" or not isinstance(listing.get("versions"), list):
        raise TransportError("NuGet package listing is unknown or malformed")
    try:
        for listed_version in listing["versions"]:
            parse_version(listed_version)
    except VersionError as error:
        raise TransportError("NuGet package listing contains a non-canonical version") from error
    if not any(_same_version_text(v, version) for v in listing["versions"]):
        return {"state": "absent"}
    downloaded = transport.nuget_package(target["id"], version.text)
    if downloaded is None:
        return {"state": "present-index-only"}
    comparison = compare_nuget_archives(_expected_bytes(target, snapshot), downloaded)
    signature = None
    if comparison["payloadEqual"] and comparison["signatureEntry"] == "present" and signature_verifier is not None:
        signature = signature_verifier(downloaded)
    return {"state": "present", "payloadEqual": comparison["payloadEqual"], "signatureEntry": comparison["signatureEntry"],
            "signatureTrusted": bool(isinstance(signature, dict) and signature.get("trusted") is True),
            "signatureTool": (signature or {}).get("tool") if isinstance(signature, dict) else None,
            "uploadSha256": comparison["uploadSha256"], "downloadSha256": comparison["downloadSha256"], "problems": comparison["problems"][:10]}


def _decide(target: dict, observed: dict, last, resume: bool) -> dict:
    key, kind = target["key"], target["kind"]
    state = observed.get("state")
    step = {"order": target["order"], "key": key, "kind": kind, "observed": observed, "lastReceiptState": last, "state": None, "action": None,
            "requiresAuthorization": False, "reason": ""}

    def set_(new_state, action, reason="", write=False):
        step.update(state=new_state, action=action, reason=reason, requiresAuthorization=write)
        return step

    def absent_step(create_action, label):
        if last in (None, "absent", "failed"):
            return set_("absent", create_action, "%s is absent" % label, True)
        if last in ("requested", "uncertain"):
            return set_("uncertain", "requery-then-retry-same-bytes", "an earlier request for %s has no confirmed outcome; requery, then retry "
                                                                       "the same approved bytes under the original intent" % label, True)
        return set_("uncertain", "requery", "%s was recorded as %s but is not visible: indexing delay or provider issue; never republish "
                                            "different bytes" % (label, last))

    if state == "unknown":
        return set_("unknown", "requery", "%s: remote state could not be observed (%s)" % (key, observed.get("error")))
    if kind == "source-commit":
        if state == "present":
            return set_("verified", "none", "S is on the remote")
        return set_("absent", "push-source", "S is not on the remote; push exactly S within the concrete authorization (this tool never pushes source)", True)
    if kind == "git-tag":
        if state == "absent":
            if last in ("present-unverified", "verified"):
                return set_("failed", "stop", "tag %s was recorded as created but is now absent; never recreate silently" % target["tag"])
            return absent_step("create-tag", "tag " + target["tag"])
        if observed.get("commit") != target["commit"]:
            return set_("failed", "stop", "tag %s is present elsewhere (%s), not at S; never move or delete it" % (target["tag"], observed.get("commit")))
        if not resume:
            return set_("failed", "stop", "tag %s already exists at S without a recorded approved promotion of this candidate; explicit "
                                          "disposition required" % target["tag"])
        return set_("verified", "none", "tag at S")
    if kind == "github-release":
        if state == "absent":
            if last in ("present-unverified", "verified"):
                return set_("failed", "stop", "release %s was recorded as created but is now absent" % target["tag"])
            return absent_step("create-release", "release " + target["tag"])
        if not resume:
            return set_("failed", "stop", "release %s already exists without a recorded approved promotion; explicit disposition required" % target["tag"])
        if observed.get("unexpectedAssets"):
            return set_("failed", "stop", "release %s holds unexpected assets %s; never overwrite or clobber" % (target["tag"], observed["unexpectedAssets"]))
        if observed.get("metadataProblems"):
            return set_("failed", "stop", "; ".join(observed["metadataProblems"]))
        return set_("verified", "none", "release exists for the recorded promotion")
    if kind == "github-release-asset":
        if state == "present":
            if observed.get("equal"):
                return set_("verified", "none", "asset bytes equal the approved bytes")
            return set_("failed", "stop", "asset %s is present with different bytes; never overwrite or clobber" % target["name"])
        return absent_step("upload-asset", "asset " + target["name"])
    if kind == "nuget-package":
        label = "%s %s" % (target["id"], target["version"])
        if state == "absent":
            return absent_step("push-package", label)
        if not resume:
            return set_("failed", "stop", "%s already exists on NuGet; a new attempt never reuses an identity (matching metadata does not make "
                                          "it reusable)" % label)
        if state == "present-index-only":
            return set_("uncertain", "requery", "%s is listed but not yet downloadable (indexing delay)" % label)
        if not observed.get("payloadEqual"):
            return set_("failed", "stop", "%s on NuGet differs from the approved bytes: %s" % (label, "; ".join(observed.get("problems") or [])))
        if observed.get("signatureEntry") == "present" and observed.get("signatureTrusted"):
            return set_("verified", "none", "payload equal and repository signature trusted")
        return set_("present-unverified", "verify-signature", "payload equal; repository signature trust is not verified by this tool "
                                                              "(dotnet nuget verify --all with the pinned SDK)")
    if kind == "nuget-symbols":
        if last in ("present-unverified", "verified"):
            return set_("present-unverified", "none", SYMBOLS_LIMIT)
        if last in ("requested", "uncertain"):
            return set_("uncertain", "retry-same-bytes", "an earlier symbols upload has no confirmed outcome; retry the same bytes (a 409 means present)", True)
        return set_("absent", "push-symbols", "symbols upload pending; " + SYMBOLS_LIMIT, True)
    if kind == "consumer-verification":
        return set_("external", "external", target.get("detail", ""))
    if kind == "site":
        return set_("not-prepared", "not-authorized", "no site artifact was prepared or authorized")
    return set_("unknown", "requery", "unknown target kind %s" % kind)


def _receipt_context(events: list, problems: list, digest: str) -> tuple:
    stops = []
    if problems:
        stops.append("the receipt stream is corrupted (%s); never erase or rewrite it; investigate before any further step" % "; ".join(problems))
    others = sorted({str(e.get("candidateSha256")) for e in events if e.get("candidateSha256") != digest})
    if others:
        stops.append("the receipt stream records another candidate (%s); never mix candidates" % ", ".join(others))
    resume = any(e.get("event") == "authorized" and e.get("candidateSha256") == digest for e in events)
    return resume, final_states(events), stops


def _plan_steps(candidate: dict, observations: dict, last: dict, resume: bool) -> list:
    return [_decide(target, observations.get(target["key"], {"state": "unknown", "error": "not observed"}), last.get(target["key"]), resume)
            for target in candidate["targets"]]


def _publication(steps: list) -> dict:
    by_kind = {}
    for step in steps:
        by_kind.setdefault(step["kind"], {})[step["key"]] = step["state"]
    return {"github": {k: v for kind in ("source-commit", "git-tag", "github-release", "github-release-asset") for k, v in by_kind.get(kind, {}).items()},
            "nuget": by_kind.get("nuget-package", {}), "symbols": by_kind.get("nuget-symbols", {}),
            "consumerVerification": "external", "site": "not-prepared",
            "note": "GitHub and NuGet status are recorded separately; a tag or GitHub release alone is not package publication"}


def plan_promotion(manifest_path, approved_sha256, *, transport, receipts_path=None, release_preparation=False, signature_verifier=None,
                   workdir=None) -> tuple:
    """Read-only plan computed from observed remote state. Never writes remote state or receipts."""
    plan = {"schema": PLAN_SCHEMA, "mode": "read-only-plan", "overall": "stopped", "exitCode": EXIT_FAIL, "manifest": str(manifest_path),
            "approvedSha256": approved_sha256, "transport": getattr(transport, "name", "offline") if transport is not None else "offline",
            "steps": [], "stops": [], "blockers": [], "executionAvailable": False, "executionNote": EXECUTION_NOTE,
            "limitations": ["a plan is not publication; observed state can change before an authorized execution re-reads it", SYMBOLS_LIMIT]}
    try:
        snapshot, verification = _snapshot(manifest_path, approved_sha256, workdir)
    except UsageError as error:
        plan.update(overall="invalid", exitCode=EXIT_USAGE)
        plan["stops"].append(str(error))
        return EXIT_USAGE, plan
    plan["bundleVerification"] = {"ok": verification["ok"], "problems": verification["problems"], "candidateSha256": verification["candidateSha256"]}
    if snapshot is None:
        plan["stops"].append("bundle verification failed; stop and restore byte-identical approved bytes (never rebuild)")
        return EXIT_FAIL, plan
    try:
        candidate = verification["candidate"]
        version = parse_version(candidate["version"]["canonical"])
        check_release_intent(version, bool(release_preparation))
        events, receipt_problems = [], []
        if receipts_path is not None:
            check_outside_bundle(receipts_path, Path(manifest_path).resolve().parent)
            events, receipt_problems = ReceiptStream(receipts_path).read()
        resume, last, stops = _receipt_context(events, receipt_problems, approved_sha256)
        plan.update(candidateId=candidate["candidateId"], version=version.text, repository=candidate["repository"],
                    sourceCommit=candidate["source"]["commit"], observedAtUtc=utc_now(),
                    resume={"isResume": resume, "receipts": str(receipts_path) if receipts_path else None, "events": len(events)})
        observations = _observe(candidate, snapshot, transport, signature_verifier)
        steps = _plan_steps(candidate, observations, last, resume)
        plan["steps"] = steps
        plan["stops"] = stops + [s["reason"] for s in steps if s["action"] == "stop"]
        plan["blockers"] = [s["reason"] for s in steps if s["state"] in ("unknown", "uncertain")]
        plan["publication"] = _publication(steps)
        if plan["stops"]:
            plan.update(overall="stopped", exitCode=EXIT_FAIL)
        elif plan["blockers"]:
            plan.update(overall="blocked", exitCode=EXIT_BLOCKED)
        else:
            plan.update(overall="plan-ready", exitCode=EXIT_OK)
        return plan["exitCode"], plan
    except UsageError as error:
        plan.update(overall="invalid", exitCode=EXIT_USAGE)
        plan["stops"].append(str(error))
        return EXIT_USAGE, plan
    finally:
        shutil.rmtree(str(snapshot), ignore_errors=True)


AUTHORIZATION_FIELDS = ("candidateSha256", "repository", "version", "sourceCommit", "targets")


def _grant_problems(grant, request: dict) -> list:
    if not isinstance(grant, dict):
        return ["the authorization verifier returned no grant"]
    problems = ["authorization %s %r is not bound to %r" % (field, grant.get(field), request[field])
                for field in AUTHORIZATION_FIELDS if grant.get(field) != request[field]]
    if not grant.get("grantId"):
        problems.append("the authorization grant has no identifier")
    return problems


class _Executor:
    def __init__(self, stream, candidate, digest, snapshot, transport, signature_verifier, sleep, attempts, delay, intents, release_id):
        self.stream, self.candidate, self.digest, self.snapshot, self.transport = stream, candidate, digest, snapshot, transport
        self.signature_verifier, self.sleep, self.attempts, self.delay, self.intents = signature_verifier, sleep, attempts, delay, intents
        self.repository, self.version = candidate["repository"], candidate["version"]["canonical"]
        self.attempt = secrets.token_hex(8)
        self.release_id = release_id

    def append(self, document: dict) -> dict:
        record = {"candidateSha256": self.digest, "candidateId": self.candidate["candidateId"], "attemptId": self.attempt}
        record.update(document)
        return self.stream.append(record)

    def event(self, key: str, state: str, **fields) -> dict:
        document = {"event": "target", "target": key, "state": state}
        document.update({k: v for k, v in fields.items() if v is not None})
        return self.append(document)

    def payload(self, target: dict) -> bytes:
        data = _expected_bytes(target, self.snapshot)
        expected = self.digest if target.get("digestSource") == "approved-candidate-digest" else target["sha256"]
        if sha256_bytes(data) != expected:
            raise ReceiptError("snapshot bytes of %s changed before upload" % target["key"])
        return data

    def requery(self, check) -> tuple:
        for attempt in range(self.attempts):
            try:
                outcome = check()
            except (TransportError, OSError, TimeoutError):
                # An accepted write may already exist. A failed confirmation read
                # is uncertainty, never absence or permission to advance.
                outcome = None
            if outcome is not None:
                return outcome
            if attempt + 1 < self.attempts:
                self.sleep(self.delay)
        return None

    def perform(self, step: dict, request: dict, call, confirm, retry_of=None) -> str:
        key = step["key"]
        requested = self.event(key, "requested", request=request, retryOfSequence=retry_of)
        sequence = requested["sequence"]
        self.intents.setdefault(key, sequence)
        try:
            result = call()
        except TransportTimeout as error:
            self.event(key, "uncertain", intentSequence=sequence, error="lost response or timeout: %s" % error)
        except TransportConflict as error:
            self.event(key, "present-unverified", intentSequence=sequence, result={"conflict": str(error)})
        except TransportError as error:
            self.event(key, "failed", intentSequence=sequence, error=str(error))
            return "failed"
        else:
            self.event(key, "present-unverified", intentSequence=sequence, result=result if isinstance(result, dict) else None)
        return self.confirm(step, sequence, confirm)

    def confirm(self, step: dict, sequence, check) -> str:
        outcome = self.requery(check)
        if outcome is None:
            self.event(step["key"], "uncertain", intentSequence=sequence, error="not visible after %d bounded requeries; never republish different bytes" % self.attempts)
            return "uncertain"
        state, fields = outcome
        self.event(step["key"], state, intentSequence=sequence, **fields)
        return state

    # Remote-truth checks used after a write or for a resume. Each returns None (not visible yet) or (state, fields).
    def check_tag(self, target):
        def check():
            observed = self.transport.tag(self.repository, target["tag"])
            if observed.get("state") != "present":
                return None
            if observed.get("commit") != target["commit"]:
                return "failed", {"error": "tag points to %s, not S" % observed.get("commit")}
            return "verified", {"result": {"commit": observed.get("commit")}}
        return check

    def check_release(self, target):
        def check():
            observed = self.transport.release(self.repository, target["tag"])
            if observed.get("state") != "present":
                return None
            problems = _release_metadata_problems(target, observed, self.snapshot)
            if problems:
                return "failed", {"error": "; ".join(problems)}
            self.release_id = observed.get("id")
            return "verified", {"result": {"releaseId": observed.get("id")}}
        return check

    def check_asset(self, target, data):
        def check():
            observed = self.transport.release(self.repository, target["tag"])
            asset = next((a for a in observed.get("assets") or [] if a.get("name") == target["name"]), None) if observed.get("state") == "present" else None
            if asset is None:
                return None
            comparison = compare_release_asset(data, self.transport.release_asset(self.repository, asset))
            state = "verified" if comparison["equal"] else "failed"
            return state, {"uploadSha256": comparison["uploadSha256"], "downloadSha256": comparison["downloadSha256"]}
        return check

    def check_package(self, target, data):
        version = parse_version(self.version)

        def check():
            listing = self.transport.nuget_versions(target["id"])
            if listing.get("state") != "present" or not any(_same_version_text(v, version) for v in listing.get("versions") or []):
                return None
            downloaded = self.transport.nuget_package(target["id"], self.version)
            if downloaded is None:
                return None
            comparison = compare_nuget_archives(data, downloaded)
            fields = {"uploadSha256": comparison["uploadSha256"], "downloadSha256": comparison["downloadSha256"],
                      "verification": {"payloadEqual": comparison["payloadEqual"], "signatureEntry": comparison["signatureEntry"],
                                       "problems": comparison["problems"][:10]}}
            if not comparison["payloadEqual"]:
                return "failed", fields
            signature = self.signature_verifier(downloaded) if self.signature_verifier and comparison["signatureEntry"] == "present" else None
            trusted = isinstance(signature, dict) and signature.get("trusted") is True
            fields["verification"]["signatureTrusted"] = trusted
            fields["verification"]["signatureTool"] = signature.get("tool") if isinstance(signature, dict) else None
            return ("verified" if trusted else "present-unverified"), fields
        return check

    def run(self, steps: list) -> tuple:
        states = {}
        targets = {t["key"]: t for t in self.candidate["targets"]}
        for step in steps:
            key, action, target = step["key"], step["action"], targets[step["key"]]
            if target["kind"] == "nuget-package" and any(states.get(dep) != "verified" for dep in target["dependsOn"]):
                return EXIT_BLOCKED, "promoted-unverified", states, "%s has a dependency that is not verified" % key
            if action in ("none", "external", "not-authorized"):
                states[key] = step["state"]
                continue
            if action == "push-source":
                return EXIT_BLOCKED, "blocked", states, "S is not on the remote; push exactly S within the concrete authorization, then resume"
            retry_of = self.intents.get(key) if action in ("requery-then-retry-same-bytes", "retry-same-bytes") else None
            outcome = self.step(step, target, action, retry_of)
            states[key] = outcome
            if outcome == "failed":
                return EXIT_FAIL, "stopped", states, "%s failed verification; stop without deleting or replacing anything" % key
            if outcome == "uncertain":
                return EXIT_BLOCKED, "uncertain", states, "%s is uncertain; dependents are not advanced; resume later from remote truth" % key
            if target["kind"] == "nuget-package" and outcome != "verified":
                return EXIT_BLOCKED, "promoted-unverified", states, "%s is not verified; no dependent package or symbols are advanced" % key
        required = [k for k, t in targets.items() if t["kind"] in ("source-commit", "git-tag", "github-release", "github-release-asset", "nuget-package")]
        if all(states.get(k) == "verified" for k in required):
            return EXIT_OK, "promoted-pending-consumer-verification", states, "fresh signed-remote consumer verification remains an external step"
        return EXIT_BLOCKED, "promoted-unverified", states, "some targets are present but not verified (for example repository signature trust)"

    def step(self, step, target, action, retry_of) -> str:
        if action == "verify-signature":
            return self.confirm(step, self.intents.get(step["key"]), self.check_package(target, self.payload(target)))
        if action == "requery":
            check = {"git-tag": self.check_tag, "github-release": self.check_release}.get(target["kind"])
            if target["kind"] == "nuget-package":
                check = self.check_package(target, self.payload(target))
            elif check is not None:
                check = check(target)
            if check is None:
                return "uncertain"
            return self.confirm(step, self.intents.get(step["key"]), check)
        if action == "requery-then-retry-same-bytes":
            quick = {"git-tag": lambda: self.check_tag(target), "github-release": lambda: self.check_release(target),
                     "github-release-asset": lambda: self.check_asset(target, self.payload(target)),
                     "nuget-package": lambda: self.check_package(target, self.payload(target))}[target["kind"]]()
            outcome = self.requery(quick)
            if outcome is not None:
                state, fields = outcome
                self.event(step["key"], state, intentSequence=retry_of, **fields)
                return state
        kind = target["kind"]
        if kind == "git-tag":
            return self.perform(step, {"action": "create-tag", "tag": target["tag"], "commit": target["commit"]},
                                lambda: self.transport.create_tag(self.repository, target["tag"], target["commit"]), self.check_tag(target), retry_of)
        if kind == "github-release":
            body = (self.snapshot / target["notes"]).read_bytes().decode("utf-8")

            def create():
                created = self.transport.create_release(self.repository, target["tag"], target["name"], body, target["prerelease"])
                if isinstance(created, dict) and created.get("id") is not None:
                    self.release_id = created["id"]
                return created
            return self.perform(step, {"action": "create-release", "tag": target["tag"], "prerelease": target["prerelease"],
                                       "notesSha256": sha256_bytes(body.encode("utf-8"))}, create, self.check_release(target), retry_of)
        if kind == "github-release-asset":
            data = self.payload(target)
            if self.release_id is None:
                return "uncertain"
            return self.perform(step, {"action": "upload-asset", "name": target["name"], "sha256": sha256_bytes(data), "size": len(data)},
                                lambda: self.transport.upload_release_asset(self.repository, self.release_id, target["name"], data),
                                self.check_asset(target, data), retry_of)
        if kind == "nuget-package":
            data = self.payload(target)
            return self.perform(step, {"action": "push-package", "id": target["id"], "version": target["version"], "uploadSha256": sha256_bytes(data)},
                                lambda: self.transport.push_nuget(target["id"], target["version"], data), self.check_package(target, data), retry_of)
        if kind == "nuget-symbols":
            data = self.payload(target)
            return self.perform(step, {"action": "push-symbols", "id": target["id"], "version": target["version"], "uploadSha256": sha256_bytes(data)},
                                lambda: self.transport.push_symbols(target["id"], target["version"], data),
                                lambda: ("present-unverified", {"verification": {"limitation": SYMBOLS_LIMIT}}), retry_of)
        return "uncertain"


def execute_promotion(manifest_path, approved_sha256, *, transport, authorization_verifier, receipts_path, release_preparation=False,
                      signature_verifier=None, lifecycle_revalidator=None, sleep=time.sleep, requery_attempts=3, requery_delay=10.0,
                      workdir=None) -> tuple:
    """Execute an authorized promotion. Python API only: the command line has no write transport and no trusted
    authorization verifier, so it never reaches this function."""
    result = {"schema": EXECUTION_SCHEMA, "status": "stopped", "exitCode": EXIT_FAIL, "targets": {}, "problems": [],
              "note": "promotion never builds, packs or rewrites a package; it uses only the approved bundle bytes"}

    def finish(code, status, message=None, states=None):
        result.update(exitCode=code, status=status)
        if message:
            result["problems"].append(message)
        if states is not None:
            result["targets"] = states
        return code, result

    if not isinstance(approved_sha256, str) or not HEX64.match(approved_sha256):
        return finish(EXIT_USAGE, "invalid", "the approved candidate digest must be 64 lowercase hexadecimal characters")
    if receipts_path is None:
        return finish(EXIT_USAGE, "invalid", "an append-only receipt stream path outside the bundle is required")
    snapshot, verification = _snapshot(manifest_path, approved_sha256, workdir)
    if snapshot is None:
        return finish(EXIT_FAIL, "stopped", "bundle verification failed (%s); restore byte-identical bytes, never rebuild" % "; ".join(verification["problems"]))
    try:
        candidate = verification["candidate"]
        version = parse_version(candidate["version"]["canonical"])
        try:
            check_release_intent(version, bool(release_preparation))
            check_outside_bundle(receipts_path, Path(manifest_path).resolve().parent)
            check_outside_bundle(receipts_path, snapshot)
        except UsageError as error:
            return finish(EXIT_USAGE, "invalid", str(error))
        if not isinstance(transport, WriteTransport):
            return finish(EXIT_BLOCKED, "blocked", "no write transport is available")
        if not isinstance(authorization_verifier, TrustedAuthorizationVerifier):
            return finish(EXIT_BLOCKED, "blocked", EXECUTION_NOTE)
        request = {"candidateSha256": approved_sha256, "repository": candidate["repository"], "version": version.text,
                   "sourceCommit": candidate["source"]["commit"], "targets": [t["key"] for t in candidate["targets"]]}
        grant = authorization_verifier.verify(dict(request))
        problems = _grant_problems(grant, request)
        if problems:
            return finish(EXIT_BLOCKED, "blocked", "authorization rejected: " + "; ".join(problems))
        if lifecycle_revalidator is None:
            return finish(EXIT_BLOCKED, "blocked", "E7 lifecycle revalidation against the current time before the first side effect is unavailable")
        lifecycle = lifecycle_revalidator(snapshot)
        if not isinstance(lifecycle, dict) or lifecycle.get("state") != "pass":
            return finish(EXIT_BLOCKED, "blocked", "E7 lifecycle revalidation did not pass: %r" % (lifecycle,))
        lock = Path(str(receipts_path) + ".lock")
        try:
            descriptor = os.open(str(lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError:
            return finish(EXIT_BLOCKED, "blocked", "another promotion holds %s (or a crashed one left it); inspect before removing it" % lock)
        try:
            os.write(descriptor, json.dumps({"pid": os.getpid(), "atUtc": utc_now(), "candidateSha256": approved_sha256}).encode())
            stream = ReceiptStream(receipts_path)
            events, receipt_problems = stream.read()
            resume, last, stops = _receipt_context(events, receipt_problems, approved_sha256)
            if stops:
                return finish(EXIT_FAIL, "stopped", "; ".join(stops))
            observations = _observe(candidate, snapshot, transport, signature_verifier)
            steps = _plan_steps(candidate, observations, last, resume)
            stops = [s["reason"] for s in steps if s["action"] == "stop"]
            unknown = [s["reason"] for s in steps if s["state"] == "unknown"]
            intents = _first_intents(events)
            release = next((o for k, o in observations.items() if k.startswith("release:")), {})
            executor = _Executor(stream, candidate, approved_sha256, snapshot, transport, signature_verifier, sleep, max(1, int(requery_attempts)),
                                 requery_delay, intents, release.get("id") if release.get("state") == "present" else None)
            if stops:
                executor.append({"event": "stopped", "reasons": stops})
                return finish(EXIT_FAIL, "stopped", "; ".join(stops), {s["key"]: s["state"] for s in steps})
            if unknown:
                return finish(EXIT_BLOCKED, "blocked", "; ".join(unknown), {s["key"]: s["state"] for s in steps})
            if any(s["action"] == "push-source" for s in steps):
                # Refuse before recording an authorized promotion: nothing may be written for a source the remote lacks.
                return finish(EXIT_BLOCKED, "blocked", "S is not on the remote; push exactly S within the concrete authorization, then run again",
                              {s["key"]: s["state"] for s in steps})
            redacted = {k: grant.get(k) for k in AUTHORIZATION_FIELDS + ("grantId", "verifier")}
            executor.append({"event": "resumed" if resume else "authorized", "grant": redacted})
            code, status, states, message = executor.run(steps)
            for step in steps:
                states.setdefault(step["key"], step["state"])
            return finish(code, status, message, states)
        except ReceiptError as error:
            return finish(EXIT_FAIL, "stopped", str(error))
        finally:
            os.close(descriptor)
            os.unlink(str(lock))
    finally:
        shutil.rmtree(str(snapshot), ignore_errors=True)


# --------------------------------------------------------------------------------------------------------------------
# Command line
# --------------------------------------------------------------------------------------------------------------------

def _print(document) -> None:
    print(json.dumps(document, indent=2, sort_keys=True, default=str))


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare = commands.add_parser("prepare", help="adopt a once-packed feed into a new candidate directory")
    prepare.add_argument("--candidate-version", required=True)
    prepare.add_argument("--candidate-dir", required=True, type=Path)
    prepare.add_argument("--feed", required=True, type=Path)
    prepare.add_argument("--gate-evidence", type=Path, help="E4 cumulative evidence directory (lanes/<lane>/lane-result.json ...)")
    prepare.add_argument("--release-preparation", action="store_true", help="explicit stable intent; never publication approval")
    prepare.add_argument("--availability-snapshot", type=Path)
    prepare.add_argument("--e6-receipt", type=Path)
    prepare.add_argument("--final-review-receipt", type=Path)
    prepare.add_argument("--governance-protection", type=Path)
    prepare.add_argument("--governance-rulesets", type=Path)
    prepare.add_argument("--release-notes", type=Path)
    prepare.add_argument("--repository", default=DEFAULT_REPOSITORY)
    prepare.add_argument("--repo", type=Path, help="source repository (default: the repository containing this script)")
    verify = commands.add_parser("verify", help="verify a candidate directory against the exact candidate.json digest")
    verify.add_argument("--manifest", required=True, type=Path)
    verify.add_argument("--expected-sha256", required=True)
    plan = commands.add_parser("plan", help="read-only promotion plan from observed remote state")
    plan.add_argument("--manifest", required=True, type=Path)
    plan.add_argument("--approved-sha256", required=True)
    plan.add_argument("--receipts", type=Path)
    plan.add_argument("--output", type=Path)
    plan.add_argument("--offline", action="store_true", help="read nothing remote; the plan is then BLOCKED")
    plan.add_argument("--release-preparation", action="store_true")
    execute = commands.add_parser("execute", help="fails closed: no trusted authorization verifier exists in this repository")
    execute.add_argument("--manifest", required=True, type=Path)
    execute.add_argument("--approved-sha256", required=True)
    execute.add_argument("--receipts", type=Path)
    execute.add_argument("--authorization", type=Path, help="never trusted: a self-authored approval document is not authority")
    execute.add_argument("--release-preparation", action="store_true")
    compare = commands.add_parser("compare-nuget", help="compare an approved package with a downloaded one (repository-signing rule)")
    compare.add_argument("--approved", required=True, type=Path)
    compare.add_argument("--downloaded", required=True, type=Path)
    args = parser.parse_args(argv)

    if args.command == "prepare":
        code, report = prepare_candidate(version_text=args.candidate_version, candidate_dir=args.candidate_dir, feed=args.feed, repo=args.repo,
                                         gate_evidence=args.gate_evidence, release_preparation=args.release_preparation,
                                         availability=args.availability_snapshot, e6_receipt=args.e6_receipt,
                                         final_review_receipt=args.final_review_receipt, governance_protection=args.governance_protection,
                                         governance_rulesets=args.governance_rulesets, release_notes=args.release_notes, repository=args.repository)
        _print(report)
        print("RELEASE PREPARATION %s (exit %d)%s" % (report["status"].upper(), code,
                                                       "; approval anchor sha256 %s" % report["candidateJsonSha256"] if code == EXIT_OK else ""))
        return code
    if args.command == "verify":
        try:
            report = verify_bundle(args.manifest, args.expected_sha256)
        except UsageError as error:
            print("invalid arguments: %s" % error, file=sys.stderr)
            return EXIT_USAGE
        report.pop("candidate", None)
        _print(report)
        return EXIT_OK if report["ok"] else EXIT_FAIL
    if args.command == "plan":
        try:
            if args.output is not None:
                check_outside_bundle(args.output, args.manifest.resolve().parent)
        except UsageError as error:
            print("invalid arguments: %s" % error, file=sys.stderr)
            return EXIT_USAGE
        transport = None if args.offline else HttpReadTransport(token=os.environ.get("GITHUB_TOKEN") or None)
        code, document = plan_promotion(args.manifest, args.approved_sha256, transport=transport, receipts_path=args.receipts,
                                        release_preparation=args.release_preparation)
        if args.output is not None:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_bytes(canonical_json(document))
        _print(document)
        print("PROMOTION PLAN %s (exit %d); execution available: no" % (document["overall"].upper(), code))
        return code
    if args.command == "execute":
        try:
            report = verify_bundle(args.manifest, args.approved_sha256)
        except UsageError as error:
            print("invalid arguments: %s" % error, file=sys.stderr)
            return EXIT_USAGE
        if not report["ok"]:
            report.pop("candidate", None)
            _print(report)
            return EXIT_FAIL
        try:
            check_release_intent(parse_version(report["candidate"]["version"]["canonical"]), args.release_preparation)
        except UsageError as error:
            print("invalid arguments: %s" % error, file=sys.stderr)
            return EXIT_USAGE
        if args.authorization is not None:
            print("The supplied authorization document is not trusted: a self-authored approval (approved=true) is never authority.")
        print("BLOCKED: " + EXECUTION_NOTE)
        return EXIT_BLOCKED
    approved, downloaded = args.approved.read_bytes(), args.downloaded.read_bytes()
    comparison = compare_nuget_archives(approved, downloaded)
    _print(comparison)
    return EXIT_OK if comparison["payloadEqual"] else EXIT_FAIL


if __name__ == "__main__":
    sys.exit(main())

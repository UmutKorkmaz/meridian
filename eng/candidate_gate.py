#!/usr/bin/env python3
"""Evaluate cumulative candidate evidence against the required-check policy (eng/required-checks.json).

The evaluator never runs a check and never trusts a lane's own verdict or its recorded summary. For every required lane
it re-derives the facts from the raw files the lane retained next to its lane-result.json (TRX documents, sample and
consumer receipts, the executed native output, Cobertura documents, the MSBuild log, toolchain receipts), checks that
the recorded summary agrees with those retained facts, and judges the retained facts. Every lane must also be bound to
exactly the packed candidate: the complete set of feed packages with their actual archive hashes. A lane can only pass
with valid passing evidence for the same candidate, and the candidate verdict only when every required lane passes.

Verdict model
  PASS         every required lane has valid passing evidence for this candidate (``releaseEligible``).
  FAIL         any lane failed validation, evidence is missing, the feed is inconsistent with its manifest or with the
               package inventory, or lanes disagree on the candidate/source identity. This is also the verdict for
               infrastructure that is not available on this host unless ``--allow-pending`` was requested.
  PENDING      only with ``--allow-pending`` in host mode: the sole non-passing lanes are bound to an OS/architecture
               that is not this host (for example linux-x64 Native AOT on an Apple Silicon machine) and have no evidence.
               It has its own exit code, is never success and never release eligible.
  SCOPED-PASS  only when the evaluation was restricted to named lanes (``--only``/``--ci-lanes``). It proves those lanes
               and nothing else: ``wholeCandidate`` and ``releaseEligible`` are false.

Lane states: PASS, FAIL, MISSING (no evidence, runnable here or aggregate mode) and UNAVAILABLE (no evidence and the
lane is bound to a different host). ``--aggregate`` has no host: nothing is ever UNAVAILABLE, every lane must have
evidence, which is how the CI aggregation job and the final cumulative campaign evaluate a candidate.

Exit codes: 0 PASS or SCOPED-PASS, 1 FAIL, 2 invalid policy/arguments, 3 PENDING.

The module also parses Visual Studio TRX files (``trx`` command) and derives lane facts for eng/lane_collect.py, so the
collector and the evaluator use one derivation.
"""

from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import importlib.util
import json
import ipaddress
import re
import struct
import sys
import zipfile
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path, PurePosixPath, PureWindowsPath

EXIT_PASS, EXIT_FAIL, EXIT_CONFIG, EXIT_PENDING = 0, 1, 2, 3
POLICY_SCHEMA = "meridian-required-checks/1"
LANE_SCHEMA = "meridian-lane-result/1"
SUMMARY_SCHEMA = "meridian-candidate-summary/2"
KINDS = {"runtime-tests", "package-consumers", "provider", "native-aot", "property-counts", "coverage",
         "warning-ratchet", "current-servicing", "generic"}
OSES = {"linux", "osx", "win"}
ARCHES = {"x64", "arm64"}
CONTEXTS = {"local", "github-actions"}
HOST_BOUND_KINDS = {"runtime-tests", "package-consumers", "native-aot"}
RID_HOSTS = {"linux-x64": {"os": "linux", "arch": "x64"}, "osx-arm64": {"os": "osx", "arch": "arm64"},
             "linux-arm64": {"os": "linux", "arch": "arm64"}, "win-x64": {"os": "win", "arch": "x64"},
             "win-arm64": {"os": "win", "arch": "arm64"}}
RID_FORMATS = {"linux-x64": "elf-x64", "osx-arm64": "macho-arm64"}
DIGEST_IMAGE = re.compile(r"^[^@\s]+@sha256:[0-9a-f]{64}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
FRAMEWORK = re.compile(r"^net\d+\.\d+$")
VERSION3 = re.compile(r"^\d+\.\d+\.\d+$")
REPO_ROOT = Path(__file__).resolve().parents[1]

# Raw files a lane of each kind retains (copied by the collector, re-hashed and re-parsed by the evaluator). A retained
# copy may carry a ".<n>" counter before its suffix when two raw outputs share a name.
RAW_PATTERNS = {
    "runtime-tests": ("*.trx", "samples-summary.json", "lane-toolchain.json"),
    "provider": ("*.trx", "samples-summary.json", "http-ownership.json", "lane-toolchain.json"),
    "package-consumers": ("consumer-selection.json", "consumer-results.json", "baseline-hashes.json", "lane-toolchain.json",
                          "*.consumer-binary", "*.consumer-deps.json", "*.consumer-assets.json", "*.baseline-nupkg"),
    "native-aot": ("native-result.json", "execute.log", "package-hash.json", "combined.native-executable", "mapping.native-executable"),
    "property-counts": ("property-output.log", "*.trx", "lane-toolchain.json"),
    "coverage": ("coverage.cobertura.xml", "coverage-gate.json", "*.trx", "lane-toolchain.json"),
    "warning-ratchet": ("build.log", "warnings-check.json", "lane-toolchain.json"),
    "current-servicing": ("*.trx", "lane-toolchain.json"),
    "generic": ("*.trx", "lane-toolchain.json"),
}
# Kinds whose suites are built and executed by the repository's pinned build SDK (toolchains.pinnedCompatibility.buildSdk).
PINNED_BUILD_KINDS = {"runtime-tests", "provider", "property-counts", "coverage", "warning-ratchet", "generic"}
PROPERTY_PASSED = re.compile(r"Ok, passed (\d+) tests?\.")
NATIVE_PROOF = "generated-only: value=42 cycle=true collectionIdentity=true depth=true limit=true cancellation=true"
NATIVE_SUMMARY = re.compile(r"^SUMMARY schema=meridian-native-aot-cases/1 mode=native count=(\d+) expected=(\d+) ids=(\S*)$")
NATIVE_MARKER = re.compile(r"^NATIVE os=(\S+) arch=(\S+) dynamicCodeSupported=(\S+) dynamicCodeCompiled=(\S+) runtime=(.*)$")


class PolicyError(Exception):
    """The policy file (or a file it names) is not a valid required-check policy (exit code 2)."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def sha512_file(path: Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return "sha512-" + base64.b64encode(digest.digest()).decode("ascii")


def nuget_content_sha512(path: Path) -> str:
    """Derive NuGet's signature-independent content hash, not signature trust.

    Baseline SHA-256 still binds the complete retained archive. Signed ZIP32
    layouts up to 64 MiB are supported; ambiguous/unsupported layouts fail.
    NuGet's algorithm removes the signature entry and repairs ZIP offsets:
    https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/Signing/Archive/SignedPackageArchiveUtility.cs
    """
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        signed = [entry for entry in entries if entry.orig_filename == '.signature.p7s']
        if not signed:
            return sha512_file(path)
        if len(signed) != 1 or path.stat().st_size > 64 * 1024 * 1024:
            raise ValueError('unsupported signed baseline size or duplicate signature')
        comment = archive.comment
    data = path.read_bytes()
    end = len(data) - 22 - len(comment)
    marker, disk, cd_disk, disk_count, count, cd_size, cd_start, comment_size = struct.unpack_from('<4s4H2IH', data, end)
    if (marker != b'PK\x05\x06' or disk or cd_disk or disk_count != count or count != len(entries)
            or count == 0xffff or cd_start + cd_size != end or comment_size != len(comment)):
        raise ValueError('unsupported signed baseline ZIP directory')
    records, cursor, names, offsets = [], cd_start, set(), set()
    for _ in range(count):
        fields = struct.unpack_from('<4s6H3I5H2I', data, cursor)
        size = 46 + sum(fields[10:13])
        name = data[cursor + 46:cursor + 46 + fields[10]]
        offset = fields[16]
        if (fields[0] != b'PK\x01\x02' or cursor + size > end or fields[13] or fields[3] & 1
                or 0xffffffff in (fields[8], fields[9], offset) or not name or b'\0' in name
                or name in names or offset in offsets or offset >= cd_start):
            raise ValueError('ambiguous or unsupported signed baseline entry')
        if name == b'.signature.p7s' and fields[3] & (1 << 11):
            raise ValueError('signature name must use the NuGet signing encoding')
        names.add(name)
        offsets.add(offset)
        records.append((name, offset, data[cursor:cursor + size], fields))
        cursor += size
    if cursor != end or not records or min(offsets) != 0:
        raise ValueError('unsupported signed baseline directory bounds')
    ordered = sorted(records, key=lambda row: row[1])
    payload, adjusted = bytearray(), {}
    for index, (name, offset, _, fields) in enumerate(ordered):
        boundary = ordered[index + 1][1] if index + 1 < len(ordered) else cd_start
        local = struct.unpack_from('<4s5H3I2H', data, offset)
        body = offset + 30 + local[9] + local[10]
        body_end = body + fields[8]
        if (local[0] != b'PK\x03\x04' or local[2] != fields[3] or local[3] != fields[4]
                or data[offset + 30:offset + 30 + local[9]] != name or body_end > boundary):
            raise ValueError('baseline local header disagrees with its directory')
        descriptor = data[body_end:boundary]
        if fields[3] & 8:
            if descriptor.startswith(b'PK\x07\x08'):
                descriptor = descriptor[4:]
            if len(descriptor) != 12 or struct.unpack('<3I', descriptor) != fields[7:10]:
                raise ValueError('unsupported signed baseline data descriptor')
        elif descriptor or local[6:9] != fields[7:10]:
            raise ValueError('unsupported padding or baseline local sizes')
        if name != b'.signature.p7s':
            adjusted[offset] = len(payload)
            payload.extend(data[offset:boundary])
    directory = bytearray()
    for name, offset, header, _ in records:
        if name != b'.signature.p7s':
            repaired = bytearray(header)
            struct.pack_into('<I', repaired, 42, adjusted[offset])
            directory.extend(repaired)
    trailer = bytearray(data[end:])
    struct.pack_into('<HHII', trailer, 8, count - 1, count - 1, len(directory), len(payload))
    digest = hashlib.sha512(payload + directory + trailer).digest()
    return 'sha512-' + base64.b64encode(digest).decode('ascii')


def lane_directory_name(lane_id: str) -> str:
    return lane_id.replace("/", "__")


def _is_int(value) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _family(version_or_framework: str) -> str:
    text = version_or_framework[3:] if version_or_framework.startswith("net") else version_or_framework
    return text.split(".")[0]


def raw_name_matches(name: str, pattern: str) -> bool:
    """``pattern`` is a file name or ``*.ext``; retained copies may carry a ``.<n>`` collision counter."""
    if pattern.startswith("*."):
        return name.endswith(pattern[1:])
    stem, dot, suffix = pattern.rpartition(".")
    return re.fullmatch(re.escape(stem) + r"(\.\d+)?" + re.escape(dot + suffix), name) is not None


def _select(files: list[Path], pattern: str) -> list[Path]:
    return sorted(p for p in files if raw_name_matches(p.name, pattern))


# --------------------------------------------------------------------------------------------------------------------
# Policy
# --------------------------------------------------------------------------------------------------------------------

def _toolchain_rows(policy: dict, name: str) -> dict:
    rows = ((policy.get("toolchains") or {}).get(name) or {}).get("lanes") or []
    return {row.get("framework"): row for row in rows}


def validate_policy(policy: dict) -> None:
    """Raise PolicyError unless the policy is internally consistent. Lanes are the unit of enforcement."""
    if policy.get("schema") != POLICY_SCHEMA:
        raise PolicyError(f"policy schema must be {POLICY_SCHEMA}")
    providers = policy.get("providers", {})
    for name, provider in providers.items():
        if not DIGEST_IMAGE.match(provider.get("image", "")):
            raise PolicyError(f"provider {name} image must be pinned by sha256 digest")
        if not provider.get("environment"):
            raise PolicyError(f"provider {name} needs an environment variable name")
    profiles = policy.get("countProfiles", {})
    for profile_name, per_framework in profiles.items():
        for framework, requirements in per_framework.items():
            if not FRAMEWORK.match(framework):
                raise PolicyError(f"count profile {profile_name} has invalid framework {framework}")
            for requirement in requirements:
                if not requirement.get("suite") or not _is_int(requirement.get("minimum")) or requirement["minimum"] < 1:
                    raise PolicyError(f"count profile {profile_name}/{framework} has an invalid requirement (minimum must be at least 1)")
    samples = policy.get("samples", {})
    for framework, count in samples.items():
        if not FRAMEWORK.match(framework) or not _is_int(count) or count < 1:
            raise PolicyError(f"sample count for {framework} must be a positive integer")
        names = (policy.get("sampleNames") or {}).get(framework)
        if names is not None and (len(names) != count or len(set(names)) != len(names)):
            raise PolicyError(f"sampleNames for {framework} must list exactly {count} distinct samples")
    pinned, servicing = _toolchain_rows(policy, "pinnedCompatibility"), _toolchain_rows(policy, "currentServicing")
    build_sdk = ((policy.get("toolchains") or {}).get("currentServicing") or {}).get("buildSdk")
    lanes = policy.get("lanes")
    if not isinstance(lanes, list) or not lanes:
        raise PolicyError("policy needs at least one lane")
    ids, checks, directories = set(), set(), set()
    for lane in lanes:
        lane_id, check, kind = lane.get("id"), lane.get("check"), lane.get("kind")
        if not lane_id or not check:
            raise PolicyError("every lane needs an id and a check name")
        if lane_id in ids:
            raise PolicyError(f"duplicate lane id: {lane_id}")
        if lane_directory_name(lane_id) in directories:
            raise PolicyError(f"lane id {lane_id} collides with another lane's evidence directory")
        directories.add(lane_directory_name(lane_id))
        if check in checks:
            raise PolicyError(f"duplicate check name: {check}")
        ids.add(lane_id)
        checks.add(check)
        if kind not in KINDS:
            raise PolicyError(f"lane {lane_id} has unknown kind {kind}")
        framework = lane.get("framework")
        if framework is not None and not FRAMEWORK.match(framework):
            raise PolicyError(f"lane {lane_id} has invalid framework {framework}")
        host = lane.get("host")
        if host is not None:
            if host.get("os") not in OSES:
                raise PolicyError(f"lane {lane_id} host os must be one of {sorted(OSES)}")
            if host.get("arch") is not None and host["arch"] not in ARCHES:
                raise PolicyError(f"lane {lane_id} host arch must be one of {sorted(ARCHES)}")
        if kind in HOST_BOUND_KINDS and not lane.get("toolchainRootEnvironment") and not (host and host.get("arch")):
            raise PolicyError(f"{kind} lane {lane_id} must be bound to an operating system and an architecture")
        for service in lane.get("services", []):
            if service not in providers:
                raise PolicyError(f"lane {lane_id} requires unknown service {service}")
        profile = lane.get("countsProfile")
        if profile is not None:
            if profile not in profiles:
                raise PolicyError(f"lane {lane_id} references missing count profile {profile}")
            if framework not in profiles[profile]:
                raise PolicyError(f"count profile {profile} has no entry for {framework} (lane {lane_id})")
        if kind == "runtime-tests":
            if not profile or framework not in policy.get("samples", {}):
                raise PolicyError(f"runtime lane {lane_id} needs a count profile and a sample count for its framework")
        if kind == "package-consumers":
            if lane.get("consumerScope") not in ("all", "partial") or not lane.get("sdk"):
                raise PolicyError(f"consumer lane {lane_id} needs consumerScope (all|partial) and sdk")
            if lane.get("toolchainRootEnvironment"):
                row = servicing.get(framework)
                if not row or row.get("sdk") != lane["sdk"]:
                    raise PolicyError(f"current-servicing consumer lane {lane_id} SDK {lane['sdk']} is not the currentServicing SDK for {framework}")
            elif pinned and (framework not in pinned or pinned[framework].get("sdk") != lane["sdk"]):
                raise PolicyError(f"consumer lane {lane_id} SDK {lane['sdk']} is not the pinned compatibility SDK for {framework}")
        if kind == "native-aot":
            if not (lane.get("rid") and framework):
                raise PolicyError(f"native lane {lane_id} needs rid and framework")
            if RID_HOSTS.get(lane["rid"]) != {"os": host.get("os"), "arch": host.get("arch")}:
                raise PolicyError(f"native lane {lane_id} host {host} does not match its runtime identifier {lane['rid']}")
        if kind == "property-counts" and not policy.get("propertyCases"):
            raise PolicyError("property lanes need policy.propertyCases")
        if kind == "native-aot" and not policy.get("nativeCases"):
            raise PolicyError("native lanes need a non-empty policy.nativeCases")
        if kind == "generic" and not profile:
            raise PolicyError(f"generic lane {lane_id} needs a count profile; a lane without requirements proves nothing")
        if lane.get("httpSmoke") and kind != "provider":
            raise PolicyError(f"lane {lane_id}: httpSmoke applies to provider lanes only")
        if kind == "provider" and not lane.get("services") and not profile:
            raise PolicyError(f"provider lane {lane_id} needs services or a count profile")
        if kind == "current-servicing":
            if not (lane.get("sdk") and lane.get("runtime") and profile and lane.get("toolchainRootEnvironment")):
                raise PolicyError(f"current-servicing lane {lane_id} needs sdk, runtime, a count profile and toolchainRootEnvironment")
            row = servicing.get(framework)
            if not row or row.get("runtime") != lane["runtime"]:
                raise PolicyError(f"current-servicing lane {lane_id} runtime {lane['runtime']} is not the currentServicing runtime for {framework}")
            if build_sdk and lane["sdk"] != build_sdk:
                raise PolicyError(f"current-servicing lane {lane_id} must build with the current SDK {build_sdk}")
        if kind == "coverage" and not (policy.get("coverage") or {}).get("baseline"):
            raise PolicyError("coverage lanes need policy.coverage.baseline")
        if kind == "warning-ratchet" and not ((policy.get("warnings") or {}).get("baseline") and (policy.get("warnings") or {}).get("solution")):
            raise PolicyError("warning-ratchet lanes need policy.warnings.baseline and policy.warnings.solution")
    for framework, row in servicing.items():
        if not (VERSION3.match(row.get("sdk", "")) and VERSION3.match(row.get("runtime", ""))):
            raise PolicyError(f"currentServicing row {framework} needs exact sdk and runtime patch versions")
        if _family(row["sdk"]) != _family(framework) or _family(row["runtime"]) != _family(framework):
            raise PolicyError(f"currentServicing row {framework} names another runtime family")
        if pinned.get(framework, {}).get("sdk") == row["sdk"]:
            raise PolicyError(f"currentServicing row {framework} repeats the pinned compatibility SDK; they prove different things")


def ci_lane_ids(policy: dict) -> list[str]:
    """Lanes the CI workflows define. Lanes with ``ci: null`` (for example current servicing) need local evidence."""
    return [l["id"] for l in policy["lanes"] if l.get("ci")]


def host_matches(lane_host: dict | None, host: dict | None) -> bool:
    if lane_host is None:
        return True
    if host is None:
        return False
    if lane_host.get("os") != host.get("os"):
        return False
    return lane_host.get("arch") in (None, host.get("arch"))


def load_inventory(policy: dict, repo: Path) -> dict | None:
    """The package inventory the feed must match exactly (``policy.packageInventory``, relative to the repository)."""
    reference = policy.get("packageInventory")
    if not reference:
        return None
    path = Path(repo) / reference
    try:
        inventory = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise PolicyError(f"package inventory {path} is not readable: {error}") from error
    ids = [p.get("id") for p in inventory.get("packages") or []]
    if not ids or not all(ids):
        raise PolicyError(f"package inventory {path} lists no package ids")
    return {"ids": ids, "consumers": {c.get("id"): list(c.get("variants") or []) for c in inventory.get("consumers") or []},
            "packages": {p["id"]: p for p in inventory["packages"]},
            "consumerContracts": {c["id"]: c for c in inventory.get("consumers") or []},
            "sha256": sha256_file(path)}


# --------------------------------------------------------------------------------------------------------------------
# Feed
# --------------------------------------------------------------------------------------------------------------------

def _plain_file_name(name) -> bool:
    return isinstance(name, str) and name not in ("", ".", "..") and "/" not in name and "\\" not in name and ":" not in name


def _inspect_archive(feed: Path, name: str, expected_name: str, label: str, problems: list[str]) -> Path | None:
    if not _plain_file_name(name):
        problems.append(f"{label} {name!r} must be a plain file name inside the feed")
        return None
    if name != expected_name:
        problems.append(f"{label} {name} is not the expected {expected_name}")
    path = feed / name
    if path.is_symlink():
        problems.append(f"{label} {name} is a symbolic link; the feed must hold the archive bytes itself")
        return None
    if not path.is_file():
        problems.append(f"{label} {name} is missing from the feed")
        return None
    if path.resolve().parent != feed.resolve():
        problems.append(f"{label} {name} resolves outside the feed")
        return None
    return path


def inspect_feed(feed: Path, version: str, inventory: dict | None = None) -> dict:
    """Hash the packed candidate. The pack-time manifest is only a claim until the files (and the inventory) agree."""
    feed = Path(feed)
    info = {"manifestSha256": None, "packages": [], "problems": [], "sourceCommit": None}
    problems = info["problems"]
    manifest_path = feed / f"package-manifest.{version}.json"
    if not manifest_path.is_file() or manifest_path.is_symlink():
        problems.append(f"feed has no regular package-manifest.{version}.json")
        return info
    info["manifestSha256"] = sha256_file(manifest_path)
    try:
        record = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        problems.append(f"package manifest is not readable JSON: {error}")
        return info
    if not isinstance(record, dict) or not isinstance(record.get("packages"), list) \
            or not all(isinstance(p, dict) for p in record.get("packages")):
        problems.append("package manifest is not an object with a list of package records")
        return info
    if record.get("candidateVersion") != version:
        problems.append(f"package manifest is for {record.get('candidateVersion')}, not {version}")
    info["sourceCommit"] = record.get("sourceCommit")
    if record.get("sourceDirty") is not False:
        problems.append("the candidate was packed from a working tree with uncommitted changes or has no explicit clean-source proof")
    if inventory is not None and inventory.get("sha256") and record.get("manifestSha256") != inventory["sha256"]:
        problems.append(f"package manifest was packed from inventory {record.get('manifestSha256')}, not the repository inventory {inventory['sha256']}")
    packages = record["packages"]
    if not packages:
        problems.append("package manifest lists no packages")
    seen, listed = set(), {manifest_path.name}
    for package in packages:
        package_id = package.get("id")
        if not isinstance(package_id, str) or not package_id:
            problems.append("package manifest has an entry without an id")
            continue
        if package_id.lower() in seen:
            problems.append(f"package manifest lists {package_id} more than once (duplicate id)")
            continue
        seen.add(package_id.lower())
        entry = {"id": package_id, "sha256": None, "symbolsSha256": None}
        archive = _inspect_archive(feed, package.get("package"), f"{package_id}.{version}.nupkg", f"package {package_id}: archive", problems)
        if package.get("package"):
            listed.add(str(package.get("package")))
        if archive is not None:
            entry["sha256"] = sha256_file(archive)
            entry["sha512"] = sha512_file(archive)
            if entry["sha256"] != package.get("sha256"):
                problems.append(f"package {package_id}: archive hash {entry['sha256']} differs from the recorded {package.get('sha256')}")
        if package.get("symbols"):
            listed.add(str(package["symbols"]))
            symbols = _inspect_archive(feed, package["symbols"], f"{package_id}.{version}.snupkg", f"package {package_id}: symbols", problems)
            if symbols is not None:
                entry["symbolsSha256"] = sha256_file(symbols)
                if entry["symbolsSha256"] != package.get("symbolsSha256"):
                    problems.append(f"package {package_id}: symbols hash differs from the recorded value")
        info["packages"].append(entry)
    for path in sorted(feed.iterdir()):
        if path.is_symlink():
            if path.name not in listed:
                problems.append(f"feed entry {path.name} is a symbolic link")
        elif path.suffix in (".nupkg", ".snupkg") and path.name not in listed:
            problems.append(f"feed holds {path.name}, which the package manifest does not list")
    if inventory is not None:
        expected = {i.lower(): i for i in inventory["ids"]}
        for key, package_id in sorted(expected.items()):
            if key not in seen:
                problems.append(f"inventory package {package_id} is not in the candidate feed (the feed must hold the exact inventory)")
        for package in packages:
            if isinstance(package.get("id"), str) and package["id"].lower() not in expected:
                problems.append(f"feed package {package['id']} is not in the package inventory")
    return info


# --------------------------------------------------------------------------------------------------------------------
# TRX parsing
# --------------------------------------------------------------------------------------------------------------------

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
_WINDOWS_PATH = re.compile(r"^(?:[A-Za-z]:[\\/]|\\\\)|\\")


def _path_style(code_base: str) -> str:
    return "windows" if _WINDOWS_PATH.search(code_base) else "posix"


def _pure(code_base: str):
    return PureWindowsPath(code_base) if _path_style(code_base) == "windows" else PurePosixPath(code_base)


def parse_trx(path: Path) -> dict:
    """Summarise one TRX file and report everything that makes it unusable as evidence under ``problems``.

    The test assembly and target framework come from the test binary path (Windows or POSIX). One TRX must hold
    exactly one assembly and one framework. Only top-level results count; the ResultSummary counters must agree with
    them, otherwise the run was incomplete or the document was edited. Outcomes other than Passed and NotExecuted count
    as failures.
    """
    root = ET.parse(path).getroot()
    problems: list[str] = []
    definitions = {}
    for unit in root.iterfind("./t:TestDefinitions/t:UnitTest", TRX_NS):
        identity = unit.get("id")
        method = unit.find("t:TestMethod", TRX_NS)
        executions = unit.findall("t:Execution", TRX_NS)
        if not identity or identity in definitions:
            problems.append(f"missing or duplicate test definition identity {identity!r}")
            continue
        if len(executions) != 1 or not executions[0].get("id"):
            problems.append(f"test definition {identity} needs exactly one execution identity")
        definitions[identity] = {
            "codeBase": method.get("codeBase") if method is not None else None,
            "executionId": executions[0].get("id") if len(executions) == 1 else None,
        }
    outcomes = defaultdict(int)
    skipped_names, failed_names = [], []
    results = root.findall("./t:Results/t:UnitTestResult", TRX_NS)
    used_bases = set()
    execution_ids = set()
    result_pairs = set()
    definition_results = defaultdict(set)
    for result in results:
        execution_id = result.get("executionId")
        if not execution_id or execution_id in execution_ids:
            problems.append(f"missing or duplicate result execution identity {execution_id!r}")
        execution_ids.add(execution_id)
        outcome = result.get("outcome", "Unknown")
        outcomes[outcome] += 1
        if outcome == "NotExecuted":
            skipped_names.append(result.get("testName"))
        elif outcome != "Passed":
            failed_names.append(result.get("testName"))
        test_id = result.get("testId")
        result_pairs.add((test_id, execution_id))
        if test_id not in definitions:
            problems.append(f"result {result.get('testName')} has no test definition")
        else:
            definition = definitions[test_id]
            definition_results[test_id].add(execution_id)
            if definition["codeBase"]:
                used_bases.add(definition["codeBase"])
    entries = root.findall("./t:TestEntries/t:TestEntry", TRX_NS)
    entry_pairs = [(entry.get("testId"), entry.get("executionId")) for entry in entries]
    if len(entry_pairs) != len(results) or len(set(entry_pairs)) != len(entry_pairs) or set(entry_pairs) != result_pairs:
        problems.append("TestEntries must link every unique result test/execution pair exactly once")
    for test_id, definition in definitions.items():
        if not definition_results[test_id]:
            problems.append(f"test definition {test_id} is not referenced by any result")
        elif definition["executionId"] not in definition_results[test_id]:
            problems.append(f"test definition {test_id} execution identity is not among its results")
    code_bases = sorted(used_bases or {d["codeBase"] for d in definitions.values() if d["codeBase"]})
    styles = sorted({_path_style(b) for b in code_bases})
    assemblies = sorted({_pure(b).stem for b in code_bases})
    frameworks = sorted({_pure(b).parent.name for b in code_bases if FRAMEWORK.match(_pure(b).parent.name)})
    if not assemblies:
        problems.append("records no test assembly")
    elif len(assemblies) > 1:
        problems.append(f"records {len(assemblies)} test assemblies ({', '.join(assemblies)}); one TRX must hold one assembly")
    if not frameworks:
        problems.append("records no target framework in its test binary path")
    elif len(frameworks) > 1:
        problems.append(f"records several target frameworks ({', '.join(frameworks)})")
    total = len(results)
    passed = outcomes.get("Passed", 0)
    skipped = outcomes.get("NotExecuted", 0)
    summary = root.find("./t:ResultSummary", TRX_NS)
    counters = summary.find("t:Counters", TRX_NS) if summary is not None else None
    counter_values = {}
    if counters is None:
        problems.append("has no ResultSummary counters (incomplete run)")
    else:
        for key, value in counters.attrib.items():
            try:
                counter_values[key] = int(value)
            except ValueError:
                problems.append(f"counter {key}={value!r} is not an integer")
        expectations = {"total": total, "executed": total - skipped, "passed": passed}
        if "failed" in counter_values:
            expectations["failed"] = outcomes.get("Failed", 0)
        for key, actual in expectations.items():
            if counter_values.get(key) != actual:
                problems.append(f"counters {key}={counter_values.get(key)} differ from the {actual} recorded results (incomplete or edited run)")
        run_outcome = summary.get("outcome")
        if run_outcome != "Completed" and not failed_names:
            problems.append(f"run outcome is {run_outcome}")
        if any(info.get("outcome") == "Error" for info in summary.findall(".//t:RunInfo", TRX_NS)):
            problems.append("the run reports an Error RunInfo (aborted or incomplete test host)")
    return {"file": str(path), "assembly": assemblies[0] if len(assemblies) == 1 else None, "assemblies": assemblies,
            "framework": frameworks[0] if len(frameworks) == 1 else None, "frameworks": frameworks,
            "pathStyle": styles[0] if len(styles) == 1 else ("mixed" if styles else None),
            "total": total, "passed": passed, "failed": total - passed - skipped, "skipped": skipped,
            "skippedNames": skipped_names, "failedNames": failed_names, "outcomes": dict(outcomes),
            "countersTotal": counter_values.get("total"), "counters": counter_values, "problems": problems}


# --------------------------------------------------------------------------------------------------------------------
# Fact derivation (shared with the collector)
# --------------------------------------------------------------------------------------------------------------------

def _load_json(path: Path, problems: list[str]):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        problems.append(f"{path.name} is not readable JSON: {error}")
        return None


def _exactly_one(files: list[Path], pattern: str, problems: list[str], what: str | None = None) -> Path | None:
    matches = _select(files, pattern)
    if len(matches) != 1:
        problems.append(f"expected exactly one {what or pattern} in the retained raw evidence, found {len(matches)}")
        return None
    return matches[0]


def _derive_counts(lane: dict, files: list[Path], problems: list[str]) -> tuple[list[dict], list[dict]]:
    rows, skips = [], []
    trx_files = _select(files, "*.trx")
    if not trx_files:
        problems.append("no TRX files were retained; test counts cannot be derived")
    host_os = (lane.get("host") or {}).get("os")
    for path in trx_files:
        try:
            summary = parse_trx(path)
        except (ET.ParseError, OSError) as error:
            problems.append(f"{path.name}: unreadable TRX ({error})")
            continue
        problems.extend(f"{path.name}: {p}" for p in summary["problems"])
        if summary["assembly"] is None or summary["framework"] is None:
            continue
        if lane.get("framework") and summary["framework"] != lane["framework"]:
            problems.append(f"{path.name}: TRX targets {summary['framework']}, the lane requires {lane['framework']}")
        if host_os and summary["pathStyle"] in ("windows", "posix", "mixed"):
            expected = "windows" if host_os == "win" else "posix"
            if summary["pathStyle"] != expected:
                problems.append(f"{path.name}: test binaries ran from a {summary['pathStyle']} path; the lane requires a {host_os} host")
        rows.append({"suite": summary["assembly"], "framework": summary["framework"], "total": summary["total"],
                     "passed": summary["passed"], "failed": summary["failed"], "skipped": summary["skipped"]})
        skips.extend({"suite": summary["assembly"], "name": name, "reason": "NotExecuted"} for name in summary["skippedNames"])
    rows.sort(key=lambda r: (r["suite"], r["framework"], r["total"], r["passed"], r["failed"], r["skipped"]))
    skips.sort(key=lambda s: (s["suite"], str(s["name"])))
    return rows, skips


def _derive_samples(policy: dict, lane: dict, files: list[Path], problems: list[str]) -> dict | None:
    path = _exactly_one(files, "samples-summary.json", problems)
    data = _load_json(path, problems) if path else None
    if not isinstance(data, dict):
        return None
    samples = [s for s in data.get("samples") or [] if isinstance(s, dict)]
    names = [s.get("name") for s in samples]
    if len(set(names)) != len(names):
        problems.append("samples-summary.json lists a sample more than once")
    facts = {"framework": data.get("framework"), "expected": policy["samples"].get(data.get("framework")), "total": len(samples),
             "passed": sum(1 for s in samples if s.get("exit") == 0), "names": sorted(str(n) for n in names),
             "includeHttp": bool(data.get("includeHttp"))}
    if data.get("includeHttp"):
        http = data.get("http") if isinstance(data.get("http"), dict) else {}
        facts["http"] = {"exit": http.get("exit"), "timeout": http.get("timeout"), "ownershipValidated": http.get("ownershipValidated"),
                         "exitObserved": http.get("exitObserved"), "drainsObserved": http.get("drainsObserved")}
    return facts


def _consumer_closure(contract: dict, inventory: dict, problems: list[str]) -> list[str]:
    packages = inventory.get("packages", {})
    seen, pending = set(), list(contract.get("packageIds") or [])
    while pending:
        package = pending.pop()
        if not isinstance(package, str) or package not in packages:
            problems.append("consumer references a package outside the inventory")
            continue
        if package not in seen:
            seen.add(package)
            pending.extend(packages[package].get("projectDependencies") or [])
    return sorted(seen)


def _consumer_artifact(files: list[Path], name, problems: list[str], used: set[Path]) -> Path | None:
    if not _plain_file_name(name):
        problems.append(f"consumer artifact {name!r} must be a plain file name")
        return None
    path = _exactly_one(files, name, problems)
    if path is not None:
        used.add(path)
    return path


def _consumer_libraries(document: dict, expected: list[str], version: str, hashes: dict, inventory: dict,
                        problems: list[str], label: str, assets: bool = False) -> None:
    libraries = document.get("libraries")
    if not isinstance(libraries, dict):
        problems.append(f"{label} records no library identities")
        return
    known = {name.lower(): name for name in inventory.get("ids", [])}
    observed = {}
    for identity, data in libraries.items():
        if not isinstance(identity, str) or not isinstance(data, dict):
            problems.append(f"{label} has a malformed library")
            continue
        package, separator, actual_version = identity.partition("/")
        canonical = known.get(package.lower())
        if assets and data.get("type") == "project":
            problems.append(f"{label} includes a source ProjectReference")
        if canonical is None:
            continue
        if canonical in observed:
            problems.append(f"{label} repeats an inventory package identity")
        observed[canonical] = data
        actual_hash = data.get("sha512")
        if isinstance(actual_hash, str) and not actual_hash.startswith("sha512-"):
            actual_hash = "sha512-" + actual_hash
        if not separator or actual_version != version or data.get("type") != "package" or actual_hash != hashes.get(canonical):
            problems.append(f"{label} restored {identity} from a different archive or source project")
    required = expected if assets else [name for name in expected if inventory["packages"][name].get("kind", "runtime") == "runtime"]
    if not set(required).issubset(observed) or not set(observed).issubset(expected):
        problems.append(f"{label} inventory package closure differs from {expected}")


def _consumer_source_files(row: dict, contract: dict, repo: Path, problems: list[str]) -> None:
    sources = row.get("sourceFiles")
    expected = contract.get("sourceFiles") or []
    if not expected or not isinstance(sources, list) or not all(isinstance(item, dict) for item in sources):
        problems.append(f"consumer {row.get('consumer')} has no complete source fixture receipt")
        return
    names = [item.get("source") for item in sources]
    if not all(isinstance(name, str) for name in names) or sorted(names) != sorted(expected):
        problems.append(f"consumer {row.get('consumer')} source fixture differs from its inventory")
        return
    root = repo.resolve()
    source_folder = Path(contract.get("project", "")).parent
    for item in sources:
        path = (root / item["source"]).resolve()
        if root not in path.parents or not path.is_file():
            problems.append("consumer source fixture escapes or is missing from the accepted repository")
            continue
        try:
            relative = Path(item["source"]).relative_to(source_folder).as_posix()
        except ValueError:
            problems.append("consumer source fixture is outside its declared project")
            continue
        if item.get("file") != relative or item.get("sha256") != sha256_file(path):
            problems.append(f"consumer source fixture {item['source']} hash or relative path differs")


def _derive_consumers(policy: dict, lane: dict, files: list[Path], version: str, inventory: dict | None,
                      repo: Path, feed: dict, problems: list[str]) -> dict | None:
    selection_path = _exactly_one(files, "consumer-selection.json", problems)
    results_path = _exactly_one(files, "consumer-results.json", problems)
    selection = _load_json(selection_path, problems) if selection_path else None
    rows = _load_json(results_path, problems) if results_path else None
    if not isinstance(selection, dict) or not isinstance(rows, list):
        return None
    if not all(isinstance(row, dict) for row in rows):
        problems.append("consumer-results.json must contain only object rows")
        return None
    if any(not isinstance(row.get(key), str) or not row[key] for row in rows
           for key in ("consumer", "variant", "framework", "sdkVersion", "compilerVersion")):
        problems.append("consumer execution identities, SDKs and compiler versions must be non-empty strings")
        return None
    for key in ("selectedIds", "omittedIds"):
        value = selection.get(key)
        if not isinstance(value, list) or not all(isinstance(item, str) for item in value) or len(value) != len(set(value)):
            problems.append(f"consumer selection {key} must contain distinct string ids")
            return None
    candidate = [r for r in rows if r.get("variant") != "baseline"]
    sdks = sorted({str(r.get("sdkVersion")) for r in candidate})
    compilers = sorted({r.get("compilerVersion") for r in candidate if r.get("compilerVersion")})
    if any(not _is_int(r.get("exit")) or r["exit"] != 0 for r in rows):
        problems.append("a consumer row recorded a non-zero exit")
    wrong = [r for r in candidate if r.get("packageVersion") != version]
    if wrong:
        problems.append(f"{len(wrong)} consumer row(s) used a package version other than {version}")
    selected, omitted = list(selection.get("selectedIds") or []), list(selection.get("omittedIds") or [])
    if set(selected) & set(omitted):
        problems.append(f"consumers both selected and omitted: {sorted(set(selected) & set(omitted))}")
    produced = {(r.get("consumer"), r.get("variant")) for r in candidate}
    identities = [(r.get("consumer"), r.get("variant"), r.get("framework")) for r in rows]
    if len(identities) != len(set(identities)):
        problems.append("duplicate consumer/variant/framework execution identity")
    unselected = sorted({c for c, _ in produced if c not in selected})
    if unselected:
        problems.append(f"consumer rows exist for consumers that were not selected: {unselected}")
    known = (inventory or {}).get("consumers") or {}
    contracts = (inventory or {}).get("consumerContracts") or {}
    used_artifacts: set[Path] = set()
    baseline_hashes, baseline_versions = {}, {}
    expected_baselines = set()
    for consumer in selected:
        contract_row = contracts.get(consumer) or {}
        if contract_row.get("baselineComparison"):
            expected_baselines.update(_consumer_closure(contract_row, inventory or {}, problems))
    if expected_baselines:
        baseline_path = _exactly_one(files, "baseline-hashes.json", problems)
        baseline_records = _load_json(baseline_path, problems) if baseline_path else None
        if not isinstance(baseline_records, list) or not all(isinstance(item, dict) for item in baseline_records):
            problems.append("baseline archives have no valid receipt")
        else:
            recorded = [item.get("id") for item in baseline_records]
            if not all(isinstance(name, str) for name in recorded) or sorted(recorded) != sorted(expected_baselines):
                problems.append("baseline archive inventory differs from the selected comparison consumers")
            for item in baseline_records:
                name = item.get("id")
                if not isinstance(name, str) or name not in expected_baselines:
                    continue
                baseline_version = inventory["packages"][name].get("baselineVersion")
                archive = _consumer_artifact(files, item.get("artifact"), problems, used_artifacts)
                if not baseline_version or item.get("version") != baseline_version:
                    problems.append(f"baseline {name} version is not the inventoried stable baseline")
                if archive is not None:
                    if item.get("sha256") != sha256_file(archive):
                        problems.append(f"baseline {name} archive bytes differ from the receipt")
                    try:
                        baseline_hashes[name] = nuget_content_sha512(archive)
                    except (OSError, ValueError, struct.error, zipfile.BadZipFile) as error:
                        problems.append(f'baseline {name} content hash cannot be derived: {error}')
                    baseline_versions[name] = baseline_version
    contract = "currentServicing" if lane.get("toolchainRootEnvironment") else "pinnedCompatibility"
    runtime_rows = {row["framework"]: row for row in policy.get("toolchains", {}).get(contract, {}).get("lanes", [])}
    candidate_hashes = {entry["id"]: entry.get("sha512") for entry in feed.get("packages", [])}
    for row in rows:
        baseline = row.get("variant") == "baseline"
        contract_row = contracts.get(row["consumer"])
        if contract_row is None or row["consumer"] not in selected:
            problems.append(f"consumer {row['consumer']} has no selected inventory contract")
            continue
        framework = "net10.0" if baseline else lane["framework"]
        if row.get("framework") != framework:
            problems.append(f"consumer {row.get('consumer')} targets {row.get('framework')}, expected {framework}")
        runtime = re.match(r"^HOST runtime=(\d+\.\d+\.\d+)(?:\s|$)", str(row.get("runtime", "")))
        expected_runtime = (runtime_rows.get(framework) or {}).get("runtime")
        if not runtime or _family(runtime.group(1)) != _family(framework) or (expected_runtime and runtime.group(1) != expected_runtime):
            problems.append(f"consumer {row.get('consumer')} runtime {row.get('runtime')!r} does not prove {expected_runtime or framework}")
        if not baseline and row.get("consumer") in known and row.get("variant") not in known[row["consumer"]]:
            problems.append(f"consumer {row['consumer']} has undeclared variant {row.get('variant')!r}")
        comparison = bool(contract_row.get("baselineComparison"))
        if row.get("baselineComparison") is not comparison or (baseline and not comparison):
            problems.append(f"consumer {row['consumer']} comparison contract differs from the inventory")
        closure = _consumer_closure(contract_row, inventory, problems)
        package_ids = row.get("packageIds")
        if not closure or not isinstance(package_ids, list) or not all(isinstance(name, str) for name in package_ids) or sorted(package_ids) != closure:
            problems.append(f"consumer {row['consumer']} package closure differs from the inventory")
        common = row.get("commonOutput")
        if not isinstance(common, list) or not common or not all(isinstance(line, str) and line.startswith("COMMON ") for line in common):
            problems.append(f"consumer {row['consumer']} has no asserted common behavior output")
        _consumer_source_files(row, contract_row, repo, problems)
        stem = f"{row['consumer']}-{row['variant']}-{row['framework']}"
        for field, suffix in (("binaryArtifact", ".consumer-binary"), ("depsArtifact", ".consumer-deps.json"), ("assetsArtifact", ".consumer-assets.json")):
            if row.get(field) != stem + suffix:
                problems.append(f"consumer {row['consumer']} {field} does not name its exact execution artifact")
        binary = _consumer_artifact(files, row.get("binaryArtifact"), problems, used_artifacts)
        if binary is not None and sha256_file(binary) != row.get("binarySha256"):
            problems.append(f"consumer {row['consumer']} executed binary hash differs from retained bytes")
        dependency_files = {}
        for field in ("depsArtifact", "assetsArtifact"):
            path = _consumer_artifact(files, row.get(field), problems, used_artifacts)
            document = _load_json(path, problems) if path else None
            if not isinstance(document, dict):
                problems.append(f"consumer {row['consumer']} {field} is not a retained dependency object")
            else:
                dependency_files[field] = document
        expected_version = version
        hashes = candidate_hashes
        if baseline:
            versions = {baseline_versions.get(name) for name in closure}
            expected_version = next(iter(versions)) if len(versions) == 1 else None
            hashes = baseline_hashes
            expected_sdk = policy.get("toolchains", {}).get(contract, {}).get("buildSdk")
            if row.get("sdkVersion") != expected_sdk:
                problems.append("the baseline comparison did not use the approved SDK 10 reference host")
            if not expected_version or row.get("packageVersion") != expected_version:
                problems.append("the baseline comparison used a different package version")
        deps = dependency_files.get("depsArtifact")
        if deps is not None:
            runtime_target = deps.get("runtimeTarget")
            target = runtime_target.get("name") if isinstance(runtime_target, dict) else None
            expected_target = ".NETCoreApp,Version=v" + framework[3:]
            if not isinstance(target, str) or target.split("/", 1)[0] != expected_target:
                problems.append(f"consumer {row['consumer']} dependency file targets another framework")
            _consumer_libraries(deps, closure, expected_version, hashes, inventory, problems, "consumer deps")
        assets = dependency_files.get("assetsArtifact")
        if assets is not None:
            project = assets.get("project")
            frameworks = project.get("frameworks") if isinstance(project, dict) else None
            if not isinstance(frameworks, dict) or set(frameworks) != {framework}:
                problems.append(f"consumer {row['consumer']} restore assets target another framework")
            _consumer_libraries(assets, closure, expected_version, hashes, inventory, problems, "consumer assets", assets=True)
    if known and sorted(selected + omitted) != sorted(known):
        problems.append(f"selected plus omitted consumers {sorted(selected + omitted)} are not the inventory consumers {sorted(known)}")
    for consumer in selected:
        variants = known.get(consumer) or [None]
        for variant in variants:
            if variant is None and not any(c == consumer for c, _ in produced):
                problems.append(f"selected consumer {consumer} produced no candidate row")
            elif variant is not None and (consumer, variant) not in produced:
                problems.append(f"selected consumer {consumer} ({variant}) produced no candidate row")
        if (contracts.get(consumer) or {}).get("baselineComparison"):
            reference = [row for row in rows if row["consumer"] == consumer and row["variant"] == "baseline"]
            if len(reference) != 1:
                problems.append(f"consumer {consumer} needs exactly one baseline behavior row")
            elif isinstance(reference[0].get("commonOutput"), list) and all(isinstance(line, str) for line in reference[0]["commonOutput"]):
                for row in candidate:
                    if row["consumer"] == consumer and isinstance(row.get("commonOutput"), list) and all(isinstance(line, str) for line in row["commonOutput"]):
                        if sorted(row["commonOutput"]) != sorted(reference[0]["commonOutput"]):
                            problems.append(f"consumer {consumer} changed common behavior from its baseline")
    retained_payloads = [path for path in files if any(path.name.endswith(suffix) for suffix in
                         (".consumer-binary", ".consumer-deps.json", ".consumer-assets.json", ".baseline-nupkg"))]
    if any(path not in used_artifacts for path in retained_payloads):
        problems.append("a retained consumer execution or baseline artifact has no declared row")
    deprecations = None
    if "Meridian.DeprecationConsumer" in selected:
        try:
            report = deprecation_report(repo, feed.get("manifestSha256"), rows, sha256_file(results_path), version)
            if report["overall"] != "PASS" or report["consumerEvidence"]["status"] != "matched":
                problems.append("the deprecation lifecycle/source/old-new semantic contract failed: " +
                                "; ".join(report["consumerEvidence"]["problems"] + report["sourceInvariants"]["problems"]))
            # Re-derived facts are stable: the current evaluation clock is never compared as execution evidence.
            deprecations = {key: report[key] for key in ("candidateSha256", "consumerResultsSha256", "ledgerSha256",
                                                        "ledgerContentSha256", "sourceInvariants", "consumerEvidence")}
        except (OSError, ValueError) as error:
            problems.append(f"deprecation lifecycle evidence is invalid: {error}")
    return {"scope": selection.get("scope"), "selectedIds": sorted(selected), "omittedIds": sorted(omitted), "rows": len(candidate),
            "baselineRows": len(rows) - len(candidate), "sdk": sdks[0] if len(sdks) == 1 else sdks,
            "compilerVersion": compilers[0] if len(compilers) == 1 else None, "deprecations": deprecations}


def _log_header(text: str) -> tuple[str | None, int | None]:
    command = exit_code = None
    for line in text.splitlines()[:3]:
        if line.startswith("command=") and command is None:
            command = line[len("command="):].strip()
        elif line.startswith("exit=") and exit_code is None:
            try:
                exit_code = int(line[len("exit="):].strip())
            except ValueError:
                exit_code = None
    return command, exit_code


def native_binary_format(path: Path) -> str:
    """Read the native header, without executing the retained artifact."""
    with path.open("rb") as stream:
        header = stream.read(64)
    if len(header) >= 20 and header[:4] == b"\x7fELF" and header[4] == 2 and header[5] in (1, 2):
        machine = int.from_bytes(header[18:20], "little" if header[5] == 1 else "big")
        return {62: "elf-x64", 183: "elf-arm64"}.get(machine, "elf-unknown")
    if len(header) >= 8 and header[:4] == b"\xcf\xfa\xed\xfe":
        cpu = int.from_bytes(header[4:8], "little")
        return {0x100000C: "macho-arm64", 0x1000007: "macho-x64"}.get(cpu, "macho-unknown")
    return "unknown"


def _native_binary(receipt: dict, name: str, files: list[Path], lane: dict, problems: list[str]) -> tuple[str | None, str | None]:
    retained = "combined.native-executable" if name == "Meridian.NativeAotConsumer" else "mapping.native-executable"
    binary = _exactly_one(files, retained, problems, f"retained {name} executable")
    executed_name = name + (".exe" if lane["rid"].startswith("win-") else "")
    if _pure(str(receipt.get("file", ""))).name != executed_name:
        problems.append(f"native receipt file does not name the {name} executable")
    if receipt.get("retainedFile") != retained:
        problems.append(f"native receipt does not bind the dedicated {retained} artifact")
    if binary is None:
        return None, None
    digest, binary_format = sha256_file(binary), native_binary_format(binary)
    if digest != receipt.get("sha256"):
        problems.append(f"{name} executable bytes do not match the receipt hash")
    expected_format = RID_FORMATS.get(lane["rid"])
    if expected_format and binary_format != expected_format:
        problems.append(f"{name} actual executable format is {binary_format}, expected {expected_format}")
    if receipt.get("format") is not None and receipt["format"] != binary_format:
        problems.append(f"{name} claimed format differs from its executable bytes")
    return digest, binary_format


def _derive_native(policy: dict, lane: dict, files: list[Path], version: str, feed: dict, problems: list[str]) -> dict | None:
    receipts = [(p, _load_json(p, problems)) for p in _select(files, "native-result.json")]
    combined = [(p, d) for p, d in receipts if isinstance(d, dict) and d.get("mode") == "combined"]
    mapping = [(p, d) for p, d in receipts if isinstance(d, dict) and "mode" not in d]
    if len(combined) + len(mapping) != len(receipts):
        problems.append("a native receipt has an unknown or malformed execution role")
    logs = [(p, p.read_text(encoding="utf-8", errors="replace")) for p in _select(files, "execute.log")]
    target = RID_HOSTS.get(lane["rid"], {})
    combined_command = (f"{combined[0][1].get('file')} --require-native --expect-os {target.get('os')} --expect-arch {target.get('arch')}"
                        if len(combined) == 1 else None)
    mapping_command = str(mapping[0][1].get("file")) if len(mapping) == 1 else None
    combined_logs, mapping_logs = [], []
    for path, text in logs:
        command, _ = _log_header(text)
        if command is not None and command == combined_command:
            combined_logs.append((path, text))
        elif command is not None and command == mapping_command:
            mapping_logs.append((path, text))
        else:
            problems.append(f"{path.name} is not bound to either declared native executable and role")
    if len(combined) != 1:
        problems.append(f"expected exactly one combined native receipt, found {len(combined)}")
    if len(mapping) != 1:
        problems.append(f"expected exactly one mapping-only native receipt, found {len(mapping)}")
    if len(combined_logs) != 1:
        problems.append(f"expected exactly one executed combined native output, found {len(combined_logs)}")
    if len(mapping_logs) != 1:
        problems.append(f"expected exactly one executed mapping-only native output, found {len(mapping_logs)}")
    if len(combined) != 1:
        return None
    receipt = combined[0][1]
    digest, binary_format = _native_binary(receipt, "Meridian.NativeAotConsumer", files, lane, problems)
    native = {"rid": receipt.get("rid"), "framework": receipt.get("framework"), "executed": False, "exit": receipt.get("exit"),
              "caseIds": [], "format": binary_format, "sha256": digest,
              "candidateVersion": receipt.get("candidateVersion"), "packageRecordSha256": receipt.get("packageRecordSha256"),
              "nativeRuntime": None, "mappingOnly": None}
    if receipt.get("candidateVersion") is None:
        problems.append("the combined native receipt records no candidate version")
    elif receipt["candidateVersion"] != version:
        problems.append(f"the combined native receipt is for candidate {receipt['candidateVersion']}, not {version}")
    if feed.get("manifestSha256") and receipt.get("packageRecordSha256") != feed["manifestSha256"]:
        problems.append("the combined native receipt is bound to a different package record than the candidate feed")
    expected_format = RID_FORMATS.get(lane["rid"])
    if expected_format and receipt.get("format") != expected_format:
        problems.append(f"the native executable is {receipt.get('format')}, expected {expected_format} for {lane['rid']}")
    if (receipt.get("hostOs"), receipt.get("hostArch")) != (target.get("os"), target.get("arch")):
        problems.append(f"the native receipt ran on {receipt.get('hostOs')}/{receipt.get('hostArch')}, not {lane['rid']}")
    if receipt.get("sdk") != lane.get("sdk") or not isinstance(receipt.get("compiler"), str) or not receipt["compiler"]:
        problems.append("the combined native receipt does not prove the lane SDK and compiler")
    if receipt.get("unreviewedWarnings") != 0:
        problems.append(f"the native publish has {receipt.get('unreviewedWarnings')} unreviewed warning(s)")
    if len(combined_logs) == 1:
        text = combined_logs[0][1]
        command, exit_code = _log_header(text)
        lines = text.splitlines()
        cases = [m.group(1) for m in (re.match(r"^CASE (\S+) PASS$", l) for l in lines) if m]
        failed = [l for l in lines if re.match(r"^CASE \S+ FAIL", l)]
        native["executed"] = command is not None and exit_code is not None
        if exit_code != 0:
            problems.append(f"the executed combined native output records exit {exit_code}")
        if exit_code != receipt.get("exit"):
            problems.append(f"the native receipt exit {receipt.get('exit')} differs from the executed output exit {exit_code}")
        if failed:
            problems.append(f"native case failures: {'; '.join(failed)}")
        if len(set(cases)) != len(cases):
            problems.append("a native case id is reported more than once")
        native["caseIds"] = cases
        if sorted(receipt.get("caseIds") or []) != sorted(cases):
            problems.append("the native receipt case ids differ from the executed output")
        summary = [m for m in (NATIVE_SUMMARY.match(l) for l in lines) if m]
        if len(summary) != 1 or int(summary[0].group(1)) != len(cases) or summary[0].group(3) != ",".join(cases):
            problems.append("the native SUMMARY marker is missing, repeated or disagrees with the executed CASE lines")
        marker = [m for m in (NATIVE_MARKER.match(l) for l in lines) if m]
        if len(marker) != 1 or marker[0].group(3) != "false" or marker[0].group(4) != "false":
            problems.append("the NATIVE marker is missing or does not prove a non-JIT runtime")
        else:
            native["nativeRuntime"] = marker[0].group(5)
            if (marker[0].group(1), marker[0].group(2)) != (target.get("os"), target.get("arch")):
                problems.append(f"the executed output ran on {marker[0].group(1)}/{marker[0].group(2)}, not {lane['rid']}")
        host_line = [l for l in lines if l.startswith("HOST runtime=")]
        if len(host_line) != 1 or not host_line[0].startswith(f"HOST runtime={_family(lane['framework'])}."):
            problems.append(f"the executed output does not prove the {lane['framework']} runtime family")
    if len(mapping) == 1:
        data = mapping[0][1]
        mapping_digest, _ = _native_binary(data, "Meridian.GeneratedOnlyConsumer", files, lane, problems)
        if data.get("sdk") != lane.get("sdk"):
            problems.append("the mapping-only native receipt used a different SDK than the lane")
        row = {"executed": False, "exit": None, "rid": data.get("rid"), "framework": data.get("framework"), "sha256": mapping_digest,
               "packageSha256": None}
        if len(mapping_logs) == 1:
            command, exit_code = _log_header(mapping_logs[0][1])
            row["exit"] = exit_code
            row["executed"] = command is not None and exit_code == 0 and NATIVE_PROOF in mapping_logs[0][1]
        if not row["executed"]:
            problems.append("the mapping-only native consumer has no successful executed output with the semantic proof")
        package = _exactly_one(files, "package-hash.json", problems, "mapping-only package-hash.json")
        package_data = _load_json(package, problems) if package else None
        if not isinstance(package_data, dict):
            problems.append("the mapping-only package receipt must be a JSON object binding the candidate archive")
        else:
            row["packageSha256"] = package_data.get("sha256")
            packed = {p["id"]: p["sha256"] for p in feed.get("packages", [])}
            name = PureWindowsPath(str(package_data.get("file", ""))).name if "\\" in str(package_data.get("file", "")) else PurePosixPath(str(package_data.get("file", ""))).name
            if name != f"Meridian.Mapping.{version}.nupkg" or not HEX64.fullmatch(str(package_data.get("sha256", ""))) \
                    or package_data.get("sha256") != packed.get("Meridian.Mapping"):
                problems.append("the mapping-only native row restored a Meridian.Mapping archive other than the candidate's")
        native["mappingOnly"] = row
    return native


def _derive_property(files: list[Path], problems: list[str]) -> list[int] | None:
    path = _exactly_one(files, "property-output.log", problems)
    if path is None:
        return None
    text = path.read_text(encoding="utf-8", errors="replace")
    if "Falsifiable" in text:
        problems.append("the property log reports a falsified property")
    return sorted(int(m) for m in PROPERTY_PASSED.findall(text))


def _load_tool(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).resolve().parent / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def deprecation_report(repo: Path, candidate_sha256: str, rows=None, consumer_results_sha256=None, version=None) -> dict:
    """Trusted controller engine, archived/source bytes as data, immutable package-manifest binding."""
    tool = _load_tool("meridian_e4_deprecations", "deprecation_policy.py")
    raw = (Path(repo) / "docs/deprecations.md").read_bytes()
    ledger = tool.extract_ledger(raw.decode("utf-8"))
    if version and any(item["proposed"]["action"] != "retain" and item["proposed"]["version"] != version for item in ledger["entries"]):
        raise ValueError("a proposed deprecation action targets another package version")
    if version and isinstance(rows, list) and any(isinstance(row, dict) and row.get("consumer") == "Meridian.DeprecationConsumer"
            and row.get("packageVersion") != version for row in rows):
        raise ValueError("deprecation consumer evidence identifies another package-manifest version")
    report = tool.evaluate(ledger, datetime.datetime.now(datetime.timezone.utc), repo=repo, candidate_sha256=candidate_sha256,
                           consumer_rows=rows, ledger_sha256=hashlib.sha256(raw).hexdigest(),
                           consumer_results_sha256=consumer_results_sha256)
    report["clock"] = "system-utc"
    return report


def _derive_coverage(policy: dict, files: list[Path], repo: Path, problems: list[str]) -> dict | None:
    documents = _select(files, "coverage.cobertura.xml")
    if not documents:
        problems.append("no Cobertura documents were retained; coverage cannot be recomputed")
        return None
    receipt_path = _exactly_one(files, "coverage-gate.json", problems)
    receipt = _load_json(receipt_path, problems) if receipt_path else None
    baseline_path = Path(repo) / policy["coverage"]["baseline"]
    try:
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise PolicyError(f"coverage baseline {baseline_path} is not readable: {error}") from error
    baseline_sha = sha256_file(baseline_path)
    cov = _load_tool("meridian_coverage_gate", "coverage_gate.py")
    prefix = (receipt or {}).get("repo") or str(repo)
    try:
        result = cov.check(baseline, cov.measure(documents, prefix))
    except (cov.BaselineError, ET.ParseError, ValueError) as error:
        problems.append(f"coverage cannot be recomputed from the retained documents: {error}")
        return None
    if isinstance(receipt, dict) and receipt.get("baselineSha256") != baseline_sha:
        problems.append(f"the coverage receipt was computed against baseline {receipt.get('baselineSha256')}, not the candidate's baseline {baseline_sha}")
    if isinstance(receipt, dict) and receipt.get("status") != result["status"]:
        problems.append(f"the coverage receipt status {receipt.get('status')} differs from the status {result['status']} recomputed from the retained documents")
    return {"status": result["status"], "ratchetRegressions": result["ratchetRegressions"], "hotPathFailures": result["hotPathFailures"],
            "missingFiles": result["missingFiles"], "baselineSha256": baseline_sha}


def _derive_warnings(policy: dict, files: list[Path], repo: Path, problems: list[str]) -> dict | None:
    log = _exactly_one(files, "build.log", problems)
    if log is None:
        return None
    receipt_path = _exactly_one(files, "warnings-check.json", problems)
    receipt = _load_json(receipt_path, problems) if receipt_path else None
    text = log.read_text(encoding="utf-8", errors="replace")
    command, exit_code = _log_header(text)
    solution_path = Path(repo) / policy["warnings"]["solution"]
    if command is None:
        problems.append("the build log does not record the command that produced it")
    else:
        arguments = command.split()
        if "build" not in arguments or solution_path.name not in arguments or "--no-incremental" not in arguments:
            problems.append(f"the build log is not a non-incremental (--no-incremental) build of {solution_path.name}: {command}")
    if exit_code != 0:
        problems.append(f"the build recorded exit {exit_code}")
    baseline_path = Path(repo) / policy["warnings"]["baseline"]
    tool = _load_tool("meridian_warning_inventory", "warning-inventory.py")
    try:
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
        projects = tool.solution_projects(solution_path)
    except (OSError, ValueError) as error:
        raise PolicyError(f"warning baseline or solution is not readable: {error}") from error
    prefix = (receipt or {}).get("repo") or str(repo)
    unique, unparsed = tool.parse([log], prefix)
    failures, report = tool.check(unique, unparsed, baseline)
    built = tool.built_project_names([log])
    missing = sorted(Path(p).stem for p in projects if Path(p).stem not in built)
    failures = failures + [f"solution project not built in this log: {name}" for name in missing]
    status = "FAIL" if failures else "PASS"
    if isinstance(receipt, dict):
        if not isinstance(receipt.get("failures"), list):
            problems.append("the warning receipt records no failures list")
        elif ("FAIL" if receipt["failures"] else "PASS") != status:
            problems.append(f"the warning receipt says {'FAIL' if receipt['failures'] else 'PASS'}, the retained build log recomputes to {status}")
    return {"status": status, "failures": failures, "missingProjects": missing,
            "total": report["current"]["total"], "baselineSha256": sha256_file(baseline_path)}


def _derive_toolchain(files: list[Path], problems: list[str]) -> dict | None:
    path = _exactly_one(files, "lane-toolchain.json", problems)
    data = _load_json(path, problems) if path else None
    if not isinstance(data, dict):
        return None
    for field in ("sdks", "runtimes"):
        if not isinstance(data.get(field), list) or not all(isinstance(line, str) for line in data[field]):
            problems.append(f"toolchain {field} must be a list of output lines")
            return None
    runtimes = sorted({m.group(1) for line in data.get("runtimes") or [] if (m := re.match(r"^Microsoft\.NETCore\.App (\S+)", str(line)))})
    sdks = sorted({str(line).split()[0] for line in data.get("sdks") or [] if str(line).strip()})
    return {"sdk": data.get("sdkVersion"), "sdks": sdks, "runtimes": runtimes, "dotnetRoot": data.get("dotnetRoot")}


def derive_lane_facts(policy: dict, lane: dict, files: list[Path], version: str, feed: dict, repo: Path,
                      inventory: dict | None = None) -> tuple[dict, list[str]]:
    """Facts of one lane from its raw files. Used by the collector to record them and by the evaluator to re-derive them."""
    problems: list[str] = []
    facts: dict = {}
    kind = lane["kind"]
    trx_present = bool(_select(files, "*.trx"))
    if kind in ("runtime-tests", "provider", "current-servicing") or lane.get("countsProfile") or trx_present:
        facts["counts"], facts["skips"] = _derive_counts(lane, files, problems)
    if kind in PINNED_BUILD_KINDS or kind == "current-servicing" or lane.get("toolchainRootEnvironment"):
        facts["laneToolchain"] = _derive_toolchain(files, problems)
    if kind == "runtime-tests" or lane.get("httpSmoke"):
        facts["samples"] = _derive_samples(policy, lane, files, problems)
    if lane.get("httpSmoke") and len(_select(files, "http-ownership.json")) != 1:
        problems.append("the HTTP smoke retained no single http-ownership.json receipt")
    if kind == "package-consumers":
        facts["consumers"] = _derive_consumers(policy, lane, files, version, inventory, repo, feed, problems)
    elif kind == "native-aot":
        facts["native"] = _derive_native(policy, lane, files, version, feed, problems)
    elif kind == "property-counts":
        facts["propertyCases"] = _derive_property(files, problems)
    elif kind == "coverage":
        facts["coverage"] = _derive_coverage(policy, files, repo, problems)
    elif kind == "warning-ratchet":
        facts["warnings"] = _derive_warnings(policy, files, repo, problems)
    return facts, problems


# --------------------------------------------------------------------------------------------------------------------
# Lane validation
# --------------------------------------------------------------------------------------------------------------------

def _check_counts(rows: list[dict], reasons: list[str]) -> None:
    for row in rows:
        label = f"{row.get('suite')}/{row.get('framework')}"
        if not all(_is_int(row.get(k)) for k in ("total", "passed", "failed", "skipped")):
            reasons.append(f"counts for {label} are not integers")
            continue
        if row["failed"]:
            reasons.append(f"{label} has {row['failed']} failed test(s)")
        if row["skipped"]:
            reasons.append(f"{label} has {row['skipped']} skipped test(s); mandatory cases cannot be skipped")
        if row["total"] <= 0:
            reasons.append(f"{label} executed no tests")
        if row["passed"] != row["total"]:
            reasons.append(f"{label} passed {row['passed']} of {row['total']}")


def _check_profile(policy: dict, lane: dict, rows: list[dict], reasons: list[str]) -> None:
    profile = lane.get("countsProfile")
    if not profile:
        return
    for requirement in policy["countProfiles"][profile][lane["framework"]]:
        totals = [r["total"] for r in rows if r.get("suite") == requirement["suite"] and r.get("framework") == lane["framework"] and _is_int(r.get("total"))]
        if not totals:
            reasons.append(f"required suite {requirement['suite']} ({lane['framework']}) has no counts")
        elif max(totals) < requirement["minimum"]:
            reasons.append(f"{requirement['suite']} ({lane['framework']}) ran {max(totals)} tests, below the recorded minimum {requirement['minimum']}")


def _pinned_digest(image: str) -> tuple[str, str]:
    """('redis', 'sha256:...') for 'redis:7.4-alpine@sha256:...'."""
    reference, _, digest = image.partition("@")
    repository = reference.rsplit(":", 1)[0] if ":" in reference.rsplit("/", 1)[-1] else reference
    return repository, digest


def local_docker_socket(endpoint) -> bool:
    return isinstance(endpoint, str) and (endpoint.startswith("unix:///") or endpoint.lower().startswith("npipe:////./pipe/"))


def _service_endpoint_bound(service: dict) -> bool:
    try:
        address = ipaddress.ip_address(service.get("endpointAddress", ""))
        host = service.get("endpointHost")
        if not isinstance(host, str) or (host.lower() != "localhost" and ipaddress.ip_address(host) != address):
            return False
        wildcard = "0.0.0.0" if address.version == 4 else "::"
        return (address.is_loopback and local_docker_socket(service.get("daemonEndpoint"))
                and service.get("publishedAddress") in (str(address), wildcard)
                and isinstance(service.get("endpointPort"), str) and service["endpointPort"].isdigit()
                and service.get("publishedPort") == service["endpointPort"])
    except (TypeError, ValueError):
        return False


def _check_services(policy: dict, lane: dict, result: dict, reasons: list[str]) -> None:
    reported = {s.get("name"): s for s in result.get("services", []) if isinstance(s, dict)}
    for name in lane.get("services", []):
        service = reported.get(name)
        pinned = policy["providers"][name]["image"]
        repository, digest = _pinned_digest(pinned)
        if service is None:
            reasons.append(f"required service {name} is not recorded")
        elif service.get("available") is not True:
            reasons.append(f"required service {name} was not available")
        elif service.get("resolution") != "docker-inspect":
            reasons.append(f"service {name} image was claimed ({service.get('resolution')}), not resolved from the running container")
        elif not _service_endpoint_bound(service):
            reasons.append(f"service {name} image is not bound to the connected loopback endpoint and local Docker daemon")
        elif service.get("image") != pinned or not any(str(d).split("@")[-1] == digest and str(d).split("@")[0].split("/")[-1] == repository.split("/")[-1]
                                                       for d in service.get("repoDigests") or []):
            reasons.append(f"service {name} image {service.get('image')} (digests {service.get('repoDigests')}) is not the pinned image {pinned}")


def retained_raw_files(lane: dict, lane_directory: Path, result: dict, reasons: list[str]) -> list[Path]:
    """Hash-verified raw files listed under ``artifacts``. Unlisted, escaping, linked or changed files fail the lane."""
    base = lane_directory.resolve()
    listed, verified = set(), []
    artifacts = result.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        reasons.append("the lane retains no raw evidence; nothing can be re-derived from it")
        return []
    for artifact in artifacts:
        relative = str((artifact or {}).get("path", "")) if isinstance(artifact, dict) else ""
        candidate = lane_directory / relative
        target = candidate.resolve()
        if candidate.is_symlink():
            reasons.append(f"artifact {relative} is a symbolic link")
        elif base not in target.parents:
            reasons.append(f"artifact path {relative} escapes the lane directory")
        elif not target.is_file():
            reasons.append(f"artifact {relative} is missing")
        elif sha256_file(target) != artifact.get("sha256"):
            reasons.append(f"artifact {relative} hash differs from the recorded value")
        else:
            listed.add(target)
            verified.append(target)
    raw_directory = lane_directory / "raw"
    if raw_directory.is_dir():
        for path in sorted(raw_directory.rglob("*")):
            if (path.is_file() or path.is_symlink()) and path.resolve() not in listed:
                reasons.append(f"retained raw file {path.relative_to(lane_directory)} is not listed under artifacts")
    patterns = RAW_PATTERNS[lane["kind"]]
    for path in verified:
        if path.parent == raw_directory.resolve() and not any(raw_name_matches(path.name, p) for p in patterns):
            reasons.append(f"retained raw file {path.name} is not raw evidence of a {lane['kind']} lane")
    return [p for p in verified if p.parent == raw_directory.resolve()]


def _differs(claimed, derived) -> bool:
    return json.dumps(claimed, sort_keys=True, default=str) != json.dumps(derived, sort_keys=True, default=str)


def _check_toolchain(policy: dict, lane: dict, toolchain: dict | None, reasons: list[str]) -> None:
    """Exact build SDK and, where a runtime is named, the only runtime patch of that family that could execute."""
    toolchain = toolchain or {}
    kind = lane["kind"]
    pinned = ((policy.get("toolchains") or {}).get("pinnedCompatibility") or {})
    if kind == "current-servicing":
        require_sdk, require_runtime = lane["sdk"], lane["runtime"]
    elif kind == "package-consumers":
        require_sdk, require_runtime = lane["sdk"], (_toolchain_rows(policy, "currentServicing").get(lane["framework"]) or {}).get("runtime")
    else:
        require_sdk = pinned.get("buildSdk")
        row = _toolchain_rows(policy, "pinnedCompatibility").get(lane.get("framework"))
        require_runtime = row.get("runtime") if (row and kind == "runtime-tests") else None
    if not toolchain:
        reasons.append("the lane retains no toolchain receipt (lane-toolchain.json)")
        return
    if require_sdk:
        selected_ok = toolchain.get("sdk") == require_sdk if kind != "package-consumers" else True
        if require_sdk not in (toolchain.get("sdks") or []) or not selected_ok:
            reasons.append(f"the lane toolchain did not provide SDK {require_sdk} (selected {toolchain.get('sdk')}, installed {toolchain.get('sdks')})")
    if require_runtime:
        family = [r for r in toolchain.get("runtimes") or [] if _family(r) == _family(require_runtime)]
        if family != [require_runtime]:
            reasons.append(f"the lane toolchain offers {_family(require_runtime)}.x runtimes {family}; exactly {require_runtime} must be the only one")


def fact_reasons(policy: dict, lane: dict, facts: dict) -> list[str]:
    """Lane-level judgement of derived facts (shared by the collector, which records it, and the evaluator)."""
    reasons: list[str] = []
    kind = lane["kind"]
    rows = facts.get("counts") or []
    _check_counts(rows, reasons)
    _check_profile(policy, lane, rows, reasons)
    if facts.get("skips"):
        reasons.append(f"{len(facts['skips'])} skipped case(s) in the retained TRX; mandatory cases cannot be skipped")
    if "laneToolchain" in facts:
        _check_toolchain(policy, lane, facts.get("laneToolchain"), reasons)
    if kind == "runtime-tests" or lane.get("httpSmoke"):
        samples = facts.get("samples") or {}
        framework = lane["framework"]
        expected = policy["samples"].get(framework)
        names = (policy.get("sampleNames") or {}).get(framework)
        if samples.get("framework") != framework:
            reasons.append(f"sample evidence is for {samples.get('framework')}, not {framework}")
        if samples.get("total") != expected:
            reasons.append(f"sample count {samples.get('total')} differs from the required {expected}")
        if names is not None and samples.get("names") != sorted(names):
            reasons.append(f"executed samples {samples.get('names')} are not the required samples {sorted(names)}")
        if samples.get("passed") != samples.get("total"):
            reasons.append(f"samples passed {samples.get('passed')} of {samples.get('total')}")
        if kind == "runtime-tests" and samples.get("includeHttp"):
            reasons.append("the runtime lane sample receipt includes the HTTP smoke; runtime and HTTP proof are separate lanes")
        if lane.get("httpSmoke"):
            http = samples.get("http") or {}
            if not samples.get("includeHttp") or http.get("exit") != 0 or http.get("timeout") is not False \
                    or http.get("ownershipValidated") is not True or http.get("exitObserved") is not True or http.get("drainsObserved") is not True:
                reasons.append(f"the HTTP smoke did not complete successfully with validated ownership: {http}")
    if kind == "package-consumers":
        consumers = facts.get("consumers") or {}
        if consumers.get("scope") != lane["consumerScope"]:
            reasons.append(f"consumer scope {consumers.get('scope')} differs from the lane scope {lane['consumerScope']}")
        if lane["consumerScope"] == "partial" and sorted(consumers.get("omittedIds", [])) != sorted(lane.get("omittedConsumerIds", [])):
            reasons.append(f"omitted consumers {consumers.get('omittedIds')} differ from the allowed {lane.get('omittedConsumerIds', [])}")
        if lane["consumerScope"] == "all" and consumers.get("omittedIds"):
            reasons.append("an all-consumer lane omitted consumers")
        if not _is_int(consumers.get("rows")) or consumers["rows"] <= 0:
            reasons.append("consumer lane executed no consumer rows")
        if consumers.get("sdk") != lane["sdk"]:
            reasons.append(f"consumer SDK {consumers.get('sdk')} is not the pinned {lane['sdk']}")
        if not consumers.get("compilerVersion"):
            reasons.append("consumer compiler version is not recorded")
    elif kind == "native-aot":
        native = facts.get("native") or {}
        if native.get("executed") is not True:
            reasons.append("the native binary was not executed")
        if native.get("exit") != 0:
            reasons.append(f"native binary exit code {native.get('exit')}")
        if native.get("rid") != lane["rid"]:
            reasons.append(f"native row is for {native.get('rid')}, not {lane['rid']}")
        if native.get("framework") != lane["framework"]:
            reasons.append(f"native row is for {native.get('framework')}, not {lane['framework']}")
        required = sorted(policy.get("nativeCases", []))
        executed = sorted(native.get("caseIds", []))
        if executed != required:
            missing = sorted(set(required) - set(executed))
            extra = sorted(set(executed) - set(required))
            reasons.append(f"executed native case ids differ from the required cases (missing {missing}, unexpected {extra})")
        if not HEX64.match(str(native.get("sha256", ""))):
            reasons.append("native executable hash is not recorded")
        mapping_only = native.get("mappingOnly") or {}
        if mapping_only.get("executed") is not True or mapping_only.get("exit") != 0:
            reasons.append("the mapping-only native consumer was not executed successfully")
        elif mapping_only.get("rid") != lane["rid"] or mapping_only.get("framework") != lane["framework"] \
                or not HEX64.match(str(mapping_only.get("sha256", ""))):
            reasons.append("the mapping-only native row does not match the lane rid/framework or lacks a hash")
        if not HEX64.fullmatch(str(mapping_only.get("packageSha256", ""))):
            reasons.append("the mapping-only native row lacks a candidate package archive binding")
    elif kind == "property-counts":
        cases = facts.get("propertyCases") or []
        if sorted(cases) != sorted(policy["propertyCases"]):
            reasons.append(f"property case counts {sorted(cases)} differ from the required {sorted(policy['propertyCases'])}")
    elif kind == "coverage":
        coverage = facts.get("coverage") or {}
        if coverage.get("status") != "PASS":
            reasons.append(f"recomputed coverage gate status {coverage.get('status')}")
        for key in ("ratchetRegressions", "hotPathFailures", "missingFiles"):
            if coverage.get(key):
                reasons.append(f"recomputed coverage reports {key}: {coverage[key]}")
    elif kind == "warning-ratchet":
        warnings = facts.get("warnings") or {}
        if warnings.get("status") != "PASS":
            reasons.append(f"recomputed warning ratchet status {warnings.get('status')}: {warnings.get('failures')}")
    return reasons


def validate_lane(policy: dict, lane: dict, result: dict, lane_directory: Path, feed: dict, version: str,
                  repo: Path = REPO_ROOT, inventory: dict | None = None) -> tuple[list[str], dict]:
    reasons: list[str] = []
    if result.get("schema") != LANE_SCHEMA:
        reasons.append(f"lane result schema {result.get('schema')} is not {LANE_SCHEMA}")
    if result.get("laneId") != lane["id"]:
        reasons.append(f"lane result claims id {result.get('laneId')} but sits in the directory of {lane['id']}")
    if result.get("kind") != lane["kind"]:
        reasons.append(f"lane result kind {result.get('kind')} differs from {lane['kind']}")
    if result.get("status") != "PASS":
        reasons.append(f"lane reported status {result.get('status')}")
    collector_problems = [x for x in result.get("limitations") or [] if str(x).startswith("collector:")]
    if collector_problems:
        reasons.append(f"the collector recorded problems: {'; '.join(map(str, collector_problems))}")
    execution = result.get("execution") or {}
    if execution.get("context") not in CONTEXTS:
        reasons.append("execution context must be local or github-actions")
    elif execution["context"] == "github-actions" and not execution.get("runId"):
        reasons.append("a github-actions lane must record its run id")
    if execution.get("producerExit") != 0:
        reasons.append(f"the lane's producing command exit is {execution.get('producerExit')}, not 0")
    reported = result.get("host") or {}
    if reported.get("os") not in OSES or reported.get("arch") not in ARCHES:
        reasons.append(f"evidence host {reported} is not a known operating system and architecture")
    lane_host = lane.get("host")
    if lane_host:
        if reported.get("os") != lane_host["os"]:
            reasons.append(f"evidence ran on {reported.get('os')}, lane requires {lane_host['os']}")
        elif lane_host.get("arch") and reported.get("arch") != lane_host["arch"]:
            reasons.append(f"evidence ran on {reported.get('arch')}, lane requires {lane_host['arch']}")
    candidate = result.get("candidate") or {}
    if candidate.get("version") != version:
        reasons.append(f"lane is bound to candidate {candidate.get('version')}, not {version}")
    if feed["manifestSha256"] is not None and candidate.get("packageManifestSha256") != feed["manifestSha256"]:
        reasons.append("lane is bound to a different package manifest than the candidate feed")
    packed = {p["id"]: p["sha256"] for p in feed["packages"]}
    bound = candidate.get("packages") if isinstance(candidate.get("packages"), dict) else {}
    if not bound:
        reasons.append("lane records no candidate package binding")
    for package_id, digest in sorted(bound.items()):
        if package_id not in packed:
            reasons.append(f"lane used package {package_id}, which is not in the candidate feed")
        elif packed[package_id] != digest:
            reasons.append(f"lane used a different {package_id} archive than the candidate feed")
    for package_id in sorted(set(packed) - set(bound)):
        reasons.append(f"lane binding lacks candidate package {package_id}")
    source = result.get("source") or {}
    if not COMMIT.match(str(source.get("commit", ""))):
        reasons.append("source commit is not recorded")
    if source.get("dirty") is not False:
        reasons.append(f"the lane source state is {'dirty' if source.get('dirty') else 'not recorded'}; evidence must come from committed source")
    raw_files = retained_raw_files(lane, lane_directory, result, reasons)
    facts, problems = derive_lane_facts(policy, lane, raw_files, version, feed, repo, inventory)
    reasons.extend(f"retained evidence: {p}" for p in problems)
    for key, derived in facts.items():
        if _differs(result.get(key), derived):
            reasons.append(f"recorded {key} differ from the retained raw evidence (recorded {json.dumps(result.get(key), default=str)[:300]}; "
                           f"retained {json.dumps(derived, default=str)[:300]})")
    if result.get("skips") and not facts.get("skips"):
        reasons.append(f"{len(result['skips'])} skipped case(s) recorded; mandatory cases cannot be skipped")
    reasons.extend(fact_reasons(policy, lane, facts))
    _check_services(policy, lane, result, reasons)
    return reasons, facts


# --------------------------------------------------------------------------------------------------------------------
# Evaluation
# --------------------------------------------------------------------------------------------------------------------

def _load_result(path: Path):
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        return None, f"lane result is not readable JSON: {error}"
    if not isinstance(data, dict):
        return None, "lane result is not a JSON object"
    for field in ("services", "limitations", "artifacts", "counts", "skips"):
        if not isinstance(data.get(field), list):
            return None, f"lane result {field} must be a JSON array"
    if not all(isinstance(item, str) for item in data["limitations"]):
        return None, "lane result limitations must contain only strings"
    names = [item.get("name") for item in data["services"] if isinstance(item, dict)]
    if (len(names) != len(data["services"]) or not all(isinstance(name, str) and name for name in names)
            or len(names) != len(set(names))):
        return None, "lane result services must contain objects with distinct non-empty string names"
    for field in ("execution", "source", "candidate", "host", "toolchain"):
        if not isinstance(data.get(field), dict):
            return None, f"lane result {field} must be a JSON object"
    return data, None


def evaluate(policy: dict, feed_directory: Path, evidence_directory: Path, version: str, host: dict | None = None,
             allow_pending: bool = False, source_commit: str | None = None, only: list[str] | None = None,
             repo: Path | None = None, expected_manifest_sha256: str | None = None) -> dict:
    validate_policy(policy)
    repo = Path(repo) if repo is not None else REPO_ROOT
    inventory = load_inventory(policy, repo)
    all_ids = {l["id"] for l in policy["lanes"]}
    scoped = only is not None
    if scoped:
        if not only:
            raise PolicyError("a scoped evaluation needs at least one lane")
        unknown = sorted(set(only) - all_ids)
        if unknown:
            raise PolicyError(f"--only names unknown lane(s): {unknown}")
        policy = dict(policy, lanes=[l for l in policy["lanes"] if l["id"] in set(only)])
    feed = inspect_feed(Path(feed_directory), version, inventory)
    if expected_manifest_sha256 and feed["manifestSha256"] != expected_manifest_sha256:
        feed["problems"].append(f"the feed's package manifest {feed['manifestSha256']} is not the packed candidate's {expected_manifest_sha256}")
    if not COMMIT.match(str(feed.get("sourceCommit") or "")) and feed["manifestSha256"] is not None:
        feed["problems"].append("the package manifest records no source commit; the candidate cannot be bound to the evidence source")
    failures: list[str] = list(feed["problems"])
    if "Meridian.DeprecationConsumer" in (inventory or {}).get("consumerContracts", {}):
        try:
            lifecycle = deprecation_report(repo, feed.get("manifestSha256"), version=version)
            if lifecycle["overall"] != "PASS" or lifecycle["sourceInvariants"]["status"] != "PASS":
                failures.append("the candidate's deprecation ledger or retained source contract failed")
        except (OSError, ValueError) as error:
            failures.append(f"the candidate's deprecation lifecycle cannot be validated: {error}")
    lanes_root = Path(evidence_directory) / "lanes"
    known = {lane_directory_name(l["id"]) for l in policy["lanes"]}
    unexpected = []
    if lanes_root.is_dir():
        for directory in sorted(lanes_root.iterdir()):
            if directory.is_dir() and directory.name not in known:
                data = None
                if (directory / "lane-result.json").is_file():
                    data, _ = _load_result(directory / "lane-result.json")
                unexpected.append((data or {}).get("laneId") or directory.name)
    states, commits = [], defaultdict(list)
    totals = defaultdict(lambda: {"total": 0, "passed": 0, "failed": 0, "skipped": 0})
    skips, limitations = [], []
    for lane in policy["lanes"]:
        directory = lanes_root / lane_directory_name(lane["id"])
        path = directory / "lane-result.json"
        entry = {"id": lane["id"], "kind": lane["kind"], "check": lane["check"], "state": "MISSING", "proof": "none", "reasons": [],
                 "host": lane.get("host"), "framework": lane.get("framework")}
        if not path.is_file():
            if host is not None and not host_matches(lane.get("host"), host):
                entry["state"] = "UNAVAILABLE"
                entry["reasons"].append(f"bound to {lane['host']}, current host is {host}; no evidence was supplied")
            else:
                entry["reasons"].append("no lane evidence was supplied")
            states.append(entry)
            continue
        result, error = _load_result(path)
        if result is None:
            entry["state"] = "FAIL"
            entry["reasons"].append(error)
            states.append(entry)
            continue
        reasons, facts = validate_lane(policy, lane, result, directory, feed, version, repo, inventory)
        context = (result.get("execution") or {}).get("context")
        entry["proof"] = {"local": "executed-local", "github-actions": "executed-ci"}.get(context, "none")
        entry["reasons"] = reasons
        entry["evidenceHost"] = result.get("host")
        entry["derivedCounts"] = facts.get("counts") or []
        commit = (result.get("source") or {}).get("commit")
        if commit:
            commits[commit].append(lane["id"])
            if source_commit and commit != source_commit:
                reasons.append(f"lane source commit {commit} is not the required {source_commit}")
            if feed.get("sourceCommit") and commit != feed["sourceCommit"]:
                reasons.append(f"lane source commit {commit} is not the commit {feed['sourceCommit']} the candidate was packed from")
        entry["state"] = "FAIL" if reasons else "PASS"
        for row in facts.get("counts") or []:
            for key in ("total", "passed", "failed", "skipped"):
                totals[row["framework"]][key] += row[key]
        for item in (facts.get("skips") or []):
            skips.append({"lane": lane["id"], **item})
        for item in result.get("limitations", []):
            if item not in limitations:
                limitations.append(item)
        states.append(entry)
    if len(commits) > 1:
        failures.append("lanes disagree on the source commit: " + "; ".join(f"{c[:12]} ({len(l)} lanes)" for c, l in sorted(commits.items())))
    if source_commit and not commits:
        failures.append("no lane evidence carries the required source commit")
    unavailable = [s["id"] for s in states if s["state"] == "UNAVAILABLE"]
    for entry in states:
        if entry["state"] in ("FAIL", "MISSING"):
            failures.append(f"{entry['id']} [{entry['state']}]: " + "; ".join(entry["reasons"]))
    hard_failure = bool(failures)
    if hard_failure:
        overall = "FAIL"
    elif unavailable:
        if allow_pending and host is not None:
            overall = "PENDING"
        else:
            overall = "FAIL"
            failures.extend(f"{lane_id} [UNAVAILABLE]: infrastructure for this lane is not available on this host; strict mode does not accept it"
                            for lane_id in unavailable)
    else:
        overall = "PASS"
    if scoped and overall == "PASS":
        overall = "SCOPED-PASS"
    exit_code = {"PASS": EXIT_PASS, "SCOPED-PASS": EXIT_PASS, "FAIL": EXIT_FAIL, "PENDING": EXIT_PENDING}[overall]
    return {
        "schema": SUMMARY_SCHEMA,
        "overall": overall,
        "exitCode": exit_code,
        "wholeCandidate": not scoped,
        "releaseEligible": overall == "PASS",
        "mode": "aggregate" if host is None else "host",
        "scope": "candidate" if not scoped else "lanes",
        "allowPending": allow_pending,
        "host": host,
        "candidate": {"version": version, "packageManifestSha256": feed["manifestSha256"], "packedSourceCommit": feed.get("sourceCommit"),
                      "packages": [{"id": p["id"], "sha256": p["sha256"], "symbolsSha256": p["symbolsSha256"]} for p in feed["packages"]],
                      "inventory": (inventory or {}).get("ids"),
                      "sourceCommits": sorted(commits)},
        "policy": {"policyVersion": policy.get("policyVersion"), "laneCount": len(policy["lanes"])},
        "lanes": states,
        "failures": failures,
        "pending": unavailable if overall == "PENDING" else [],
        "unavailable": unavailable,
        "unexpectedEvidence": unexpected,
        "totalsByFramework": {k: dict(v) for k, v in sorted(totals.items())},
        "totalsNote": "sums over lane executions re-derived from retained TRX files; the same suite executed on several operating systems is counted once per lane",
        "skips": skips,
        "limitations": limitations,
    }


def render_markdown(summary: dict) -> str:
    lines = [f"# Candidate gate: {summary['overall']}", ""]
    if summary.get("scope") == "lanes":
        lines += ["> Scoped to the named lanes only. This is NOT a candidate verdict and NOT release eligible: every required lane "
                  "must pass in an aggregate evaluation.", ""]
    elif summary["overall"] == "PENDING":
        lines += ["> PENDING: lanes bound to another host have no evidence. This is not success and not release eligible.", ""]
    lines += [
             f"* Candidate: `{summary['candidate']['version']}` (package manifest `{summary['candidate']['packageManifestSha256']}`)",
             f"* Mode: {summary['mode']}{' (pending allowed)' if summary['allowPending'] else ''}; exit code {summary['exitCode']}",
             f"* Whole candidate: {'yes' if summary.get('wholeCandidate') else 'no'}; release eligible: {'yes' if summary.get('releaseEligible') else 'no'}",
             f"* Source commit(s): {', '.join(summary['candidate'].get('sourceCommits', [])) or 'none recorded'}", ""]
    if summary["failures"]:
        lines += ["## Failures", ""] + [f"* {f}" for f in summary["failures"]] + [""]
    lines += ["## Lanes", "", "| Check | State | Proof | Notes |", "| --- | --- | --- | --- |"]
    for lane in summary["lanes"]:
        note = "; ".join(lane["reasons"]).replace("|", "\\|") if lane["reasons"] else ""
        lines.append(f"| {lane['check']} | {lane['state']} | {lane['proof']} | {note} |")
    lines += ["", "## Test totals by framework", "", "| Framework | Total | Passed | Failed | Skipped |", "| --- | --- | --- | --- | --- |"]
    for framework, row in summary["totalsByFramework"].items():
        lines.append(f"| {framework} | {row['total']} | {row['passed']} | {row['failed']} | {row['skipped']} |")
    lines += ["", f"_{summary['totalsNote']}_", "", "## Skips", ""]
    lines += [f"* {s}" for s in summary["skips"]] or ["None recorded."]
    lines += ["", "## Limitations", ""] + ([f"* {x}" for x in summary["limitations"]] or ["None recorded."])
    lines += ["", "## Candidate packages", "", "| Package | SHA-256 |", "| --- | --- |"]
    lines += [f"| {p['id']} | `{p['sha256']}` |" for p in summary["candidate"]["packages"]]
    if summary["unexpectedEvidence"]:
        lines += ["", "## Unexpected evidence (does not satisfy any required lane)", ""] + [f"* {u}" for u in summary["unexpectedEvidence"]]
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------------------------------------------------
# Command line
# --------------------------------------------------------------------------------------------------------------------

def policy_repository(policy_path: Path) -> Path:
    """Policy references (inventory, baselines, solution) are relative to the repository whose eng/ holds the policy."""
    return Path(policy_path).resolve().parent.parent


def _discover_version(feed: Path) -> str:
    manifests = sorted(feed.glob("package-manifest.*.json"))
    if len(manifests) != 1:
        raise PolicyError(f"feed must hold exactly one package-manifest.<version>.json (found {len(manifests)}); pass --candidate-version")
    return manifests[0].name[len("package-manifest."):-len(".json")]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    evaluate_parser = commands.add_parser("evaluate", help="evaluate lane evidence for one candidate")
    evaluate_parser.add_argument("--policy", required=True, type=Path)
    evaluate_parser.add_argument("--feed", required=True, type=Path)
    evaluate_parser.add_argument("--evidence", required=True, type=Path)
    evaluate_parser.add_argument("--repo", type=Path,
                                 help="repository whose package inventory, coverage/warning baselines and solution the evidence is judged "
                                 "against (default: the repository containing the policy, i.e. the parent of its eng/ directory)")
    evaluate_parser.add_argument("--candidate-version")
    evaluate_parser.add_argument("--output", type=Path, help="summary JSON path; a .md summary is written next to it")
    evaluate_parser.add_argument("--aggregate", action="store_true", help="no host: every lane must have evidence")
    evaluate_parser.add_argument("--host-os", choices=sorted(OSES))
    evaluate_parser.add_argument("--host-arch")
    evaluate_parser.add_argument("--allow-pending", action="store_true")
    evaluate_parser.add_argument("--source-commit")
    evaluate_parser.add_argument("--package-manifest-sha256", help="SHA-256 of the package manifest the pack job produced (independent anchor)")
    evaluate_parser.add_argument("--ci-lanes", action="store_true", help="scope to the lanes that the CI workflows define (scoped verdict)")
    evaluate_parser.add_argument("--only", action="append", help="judge only this lane id (repeatable); the result is a scoped verdict, never a candidate verdict")
    trx_parser = commands.add_parser("trx", help="summarise TRX files as JSON")
    trx_parser.add_argument("files", nargs="+", type=Path)
    args = parser.parse_args(argv)

    if args.command == "trx":
        print(json.dumps([parse_trx(p) for p in args.files], indent=2))
        return EXIT_PASS

    if args.aggregate == bool(args.host_os):
        parser.error("exactly one of --aggregate or --host-os/--host-arch is required")
    if args.host_os and not args.host_arch:
        parser.error("--host-os needs --host-arch")
    if args.aggregate and args.host_arch:
        parser.error("--aggregate cannot be combined with a host")
    if args.aggregate and args.allow_pending:
        parser.error("--allow-pending applies to host mode only; an aggregate evaluation never accepts missing lanes")
    try:
        policy = json.loads(args.policy.read_text(encoding="utf-8"))
        validate_policy(policy)
        version = args.candidate_version or _discover_version(args.feed)
    except (OSError, ValueError, PolicyError) as error:
        print(f"candidate gate configuration error: {error}", file=sys.stderr)
        return EXIT_CONFIG
    host = None if args.aggregate else {"os": args.host_os, "arch": args.host_arch}
    repo = args.repo or policy_repository(args.policy)
    selection = (args.only or []) + (ci_lane_ids(policy) if args.ci_lanes else [])
    try:
        summary = evaluate(policy, args.feed, args.evidence, version, host=host, allow_pending=args.allow_pending,
                           source_commit=args.source_commit, only=selection or None, repo=repo,
                           expected_manifest_sha256=args.package_manifest_sha256)
    except PolicyError as error:
        print(f"candidate gate configuration error: {error}", file=sys.stderr)
        return EXIT_CONFIG
    summary["policy"]["path"] = str(args.policy)
    summary["policy"]["sha256"] = sha256_file(args.policy)
    if args.ci_lanes:
        summary["scopeNote"] = "CI-defined lanes only; local-only lanes (current servicing) are not part of this verdict"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        args.output.with_suffix(".md").write_text(render_markdown(summary), encoding="utf-8")
    print(render_markdown(summary))
    print(f"CANDIDATE GATE {summary['overall']} (exit {summary['exitCode']}; whole candidate: {summary['wholeCandidate']}; "
          f"release eligible: {summary['releaseEligible']})")
    return summary["exitCode"]


if __name__ == "__main__":
    sys.exit(main())

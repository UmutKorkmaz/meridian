#!/usr/bin/env python3
"""Turn the raw outputs of one required lane into evidence/lanes/<lane>/lane-result.json.

The collector only transcribes: it selects the raw files a lane of this kind produces (TRX files, sample summaries,
consumer receipts, the executed native output and receipts, property logs, Cobertura documents, the MSBuild log,
toolchain receipts), copies them next to the result with their SHA-256 under ``artifacts`` and records the facts
derived from them with eng/candidate_gate.py's own derivation, together with the candidate binding (the complete set
of feed packages and their actual hashes, the package manifest hash), the source commit, the host and the services the
lane really reached. It never runs a check and never decides pass/fail for the candidate: the evaluator re-hashes and
re-parses the retained copies and judges those, so a recorded summary that disagrees with its raw files fails.

A lane whose producing step failed (``--producer-exit`` non-zero) or whose raw outputs cannot be parsed is still
written, with status FAIL and the reason under ``limitations``, so a failure leaves evidence and can never be mistaken
for a missing lane. A raw file that is too large to retain fails the lane instead of being dropped silently.
"""

from __future__ import annotations

import argparse
import datetime
import json
import ipaddress
import os
import re
import shutil
import socket
import subprocess
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import candidate_gate as gate  # noqa: E402

MAX_COPY_BYTES = 256 * 1024 * 1024


def _now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _files(raw: list[Path], pattern: str) -> list[Path]:
    found: list[Path] = []
    for root in raw:
        root = Path(root)
        if root.is_file():
            if gate.raw_name_matches(root.name, pattern):
                found.append(root)
        elif root.is_dir():
            found.extend(p for p in root.rglob("*") if p.is_file() and gate.raw_name_matches(p.name, pattern))
    return sorted(set(found))


def raw_inputs(lane: dict, raw: list[Path]) -> list[Path]:
    """The raw files a lane of this kind retains, in a stable order."""
    selected: list[Path] = []
    for pattern in gate.RAW_PATTERNS[lane["kind"]]:
        selected.extend(p for p in _files(raw, pattern) if p not in selected)
    return selected


def _run_docker(arguments: list[str], environment: dict | None = None):
    return subprocess.run(["docker", *arguments], capture_output=True, text=True, timeout=30, env=environment)


def _local_docker_socket(run, environment: dict) -> str:
    context = environment.get("DOCKER_CONTEXT")
    endpoint = environment.get("DOCKER_HOST") if not context else None
    if not endpoint:
        if not context:
            current = run(["context", "show"])
            if current.returncode != 0 or not current.stdout.strip():
                raise ValueError("cannot identify the effective Docker context")
            context = current.stdout.strip()
        inspected = run(["context", "inspect", context, "--format", "{{json .Endpoints.docker.Host}}"])
        if inspected.returncode != 0:
            raise ValueError("cannot inspect the effective Docker daemon endpoint")
        endpoint = json.loads(inspected.stdout)
    if not gate.local_docker_socket(endpoint):
        raise ValueError("the Docker daemon must use a supported local Unix socket or local Windows pipe")
    return endpoint


def resolve_container_image(port: str, docker=None, address: str = "127.0.0.1", environment: dict | None = None) -> dict:
    """Identify the container publishing host ``port`` and read its image reference and repository digests.

    ``docker ps`` truncates and drops digests in its default output, so the container id is listed untruncated and the
    image is read with ``docker inspect``; ``RepoDigests`` of the image id is what proves the pinned digest.
    """
    run = docker or (lambda arguments: _run_docker(arguments, environment))
    try:
        daemon = _local_docker_socket(run, environment or {})
        listing = run(["ps", "--no-trunc", "--format", "{{.ID}}\t{{.Ports}}"])
        if listing.returncode != 0:
            return {"error": f"docker ps exited {listing.returncode}"}
        ids = [line.split("\t", 1)[0] for line in listing.stdout.splitlines()
               if "\t" in line and f":{port}->" in line.split("\t", 1)[1]]
        if len(ids) != 1:
            return {"error": f"{len(ids)} containers publish host port {port}"}
        inspected = run(["inspect", "--format", "{{.Config.Image}}\t{{.Image}}", ids[0]])
        config_image, _, image_id = inspected.stdout.strip().partition("\t")
        if inspected.returncode != 0 or not image_id:
            return {"error": f"docker inspect of {ids[0]} failed"}
        ports = run(["inspect", "--format", "{{json .NetworkSettings.Ports}}", ids[0]])
        if ports.returncode != 0:
            return {"error": "cannot inspect the container's published addresses"}
        bindings = json.loads(ports.stdout)
        family = ipaddress.ip_address(address).version
        wildcard = "0.0.0.0" if family == 4 else "::"
        matches = [binding for values in bindings.values() for binding in (values or [])
                   if binding.get("HostPort") == port and binding.get("HostIp") in (address, wildcard)]
        if len(matches) != 1:
            return {"error": "the connected loopback address and port do not match exactly one published container binding"}
        digests = run(["image", "inspect", "--format", "{{json .RepoDigests}}", image_id])
        repo_digests = json.loads(digests.stdout) if digests.returncode == 0 and digests.stdout.strip() else []
        return {"containerId": ids[0], "configImage": config_image, "imageId": image_id, "repoDigests": repo_digests or [],
                "daemonEndpoint": daemon, "publishedAddress": matches[0]["HostIp"], "publishedPort": port}
    except (OSError, subprocess.SubprocessError, ValueError) as error:
        return {"error": f"docker is not usable: {error}"}


def probe_service(policy: dict, name: str, claimed_image: str | None, environment: dict, docker=None) -> dict:
    """Record whether the endpoint named by the provider variable accepts a connection and which image serves it.

    ``auto`` resolves the image from the running container (``resolution: docker-inspect``). Any other value is only a
    claim (``resolution: claimed``), which the evaluator does not accept for a required service.
    """
    variable = policy["providers"][name]["environment"]
    value = environment.get(variable, "")
    host, port = None, None
    if name == "redis":
        endpoint = value.split(",", 1)[0]
        if endpoint.count(":") == 1:
            host, port = endpoint.split(":")
    else:
        parts = dict(p.split("=", 1) for p in value.split(";") if "=" in p)
        host, port = parts.get("Host"), parts.get("Port", "5432")
    available = False
    address = None
    if host and port and port.isdigit():
        try:
            if host.lower() != "localhost" and not ipaddress.ip_address(host).is_loopback:
                raise ValueError("required provider proof supports loopback endpoints only")
            with socket.create_connection((host, int(port)), timeout=5) as connection:
                address = connection.getpeername()[0]
                if not ipaddress.ip_address(address).is_loopback:
                    raise ValueError("the connected provider address is not loopback")
                available = True
        except (OSError, ValueError):
            available = False
    record = {"name": name, "image": claimed_image, "available": available, "environmentVariable": variable,
              "endpoint": f"{host}:{port}" if host else None, "resolution": "claimed", "repoDigests": None,
              "endpointHost": host, "endpointAddress": address, "endpointPort": port}
    if claimed_image == "auto":
        record.update(image=None, resolution="unresolved")
        if available:
            resolved = resolve_container_image(port, docker, address, environment)
            if "error" in resolved:
                record["resolutionError"] = resolved["error"]
            else:
                record.update(image=resolved["configImage"], resolution="docker-inspect", repoDigests=resolved["repoDigests"],
                              containerId=resolved["containerId"], imageId=resolved["imageId"], daemonEndpoint=resolved["daemonEndpoint"],
                              publishedAddress=resolved["publishedAddress"], publishedPort=resolved["publishedPort"])
    return record


def _toolchain() -> dict:
    result: dict = {}
    try:
        sdk = subprocess.run(["dotnet", "--version"], capture_output=True, text=True, timeout=60)
        if sdk.returncode == 0:
            result["sdk"] = sdk.stdout.strip()
        runtimes = subprocess.run(["dotnet", "--list-runtimes"], capture_output=True, text=True, timeout=60)
        if runtimes.returncode == 0:
            result["runtimes"] = sorted({m.group(1) for line in runtimes.stdout.splitlines()
                                         if (m := re.match(r"Microsoft\.NETCore\.App (\S+) ", line))})
    except (OSError, subprocess.SubprocessError):
        pass
    return result


def build_lane_result(policy: dict, lane: dict, raw: list[Path], feed: Path, version: str, source_commit: str,
                      context: str, run_id: str | None, host: dict, producer_exit: int, claimed_services: dict,
                      environment: dict, toolchain: dict | None = None, repo: Path | None = None, source_dirty: bool = False,
                      docker=None) -> dict:
    problems: list[str] = []
    repo = Path(repo) if repo is not None else gate.REPO_ROOT
    inventory = None
    try:
        inventory = gate.load_inventory(policy, repo)
    except gate.PolicyError as error:
        problems.append(str(error))
    feed_info = gate.inspect_feed(Path(feed), version, inventory)
    problems.extend(feed_info["problems"])
    result = {
        "schema": gate.LANE_SCHEMA, "laneId": lane["id"], "kind": lane["kind"], "status": "PASS",
        "execution": {"context": context, "runId": run_id, "producerExit": producer_exit, "startedUtc": _now(), "finishedUtc": _now()},
        "host": host, "source": {"commit": source_commit, "dirty": bool(source_dirty)},
        "candidate": {"version": version, "packageManifestSha256": feed_info["manifestSha256"],
                      "packages": {p["id"]: p["sha256"] for p in feed_info["packages"] if p["sha256"]}},
        "toolchain": toolchain if toolchain is not None else _toolchain(),
        "counts": [], "skips": [], "services": [], "artifacts": [], "limitations": [],
    }
    if producer_exit != 0:
        problems.append(f"the lane's producing command exited {producer_exit}")
    if source_dirty:
        problems.append("the source working tree had uncommitted changes; evidence must come from committed source")
    files = raw_inputs(lane, raw)
    for source in files:
        if source.stat().st_size > MAX_COPY_BYTES:
            problems.append(f"{source.name} exceeds {MAX_COPY_BYTES} bytes and cannot be retained as evidence")
    try:
        facts, derived_problems = gate.derive_lane_facts(policy, lane, files, version, feed_info, repo, inventory)
    except gate.PolicyError as error:
        facts, derived_problems = {}, [str(error)]
    result.update(facts)
    problems.extend(derived_problems)
    problems.extend(gate.fact_reasons(policy, lane, facts))
    for name in lane.get("services", []):
        result["services"].append(probe_service(policy, name, claimed_services.get(name), environment, docker))
    result["limitations"].extend(f"collector: {p}" for p in problems)
    if problems:
        result["status"] = "FAIL"
    return result


def write_lane(evidence: Path, lane: dict, result: dict, raw: list[Path]) -> Path:
    directory = Path(evidence) / "lanes" / gate.lane_directory_name(lane["id"])
    raw_directory = directory / "raw"
    if directory.exists():
        if directory.is_symlink():
            raise gate.PolicyError("the prior lane directory must not be a symbolic link")
        history = Path(evidence) / "history" / gate.lane_directory_name(lane["id"])
        history.mkdir(parents=True, exist_ok=True)
        # Retain the previous result together with its exact raw files. A new attempt gets an empty canonical
        # directory; earlier failures never leak into its counts and never disappear when it succeeds.
        directory.rename(history / (datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex))
    raw_directory.mkdir(parents=True)
    used: set[str] = set()
    result["artifacts"] = []
    for source in raw_inputs(lane, raw):
        if source.stat().st_size > MAX_COPY_BYTES:
            continue
        name, counter = source.name, 1
        stem, dot, suffix = source.name.rpartition(".")
        while name in used:
            counter += 1
            name = f"{stem}.{counter}.{suffix}" if dot else f"{source.name}.{counter}"
        used.add(name)
        shutil.copyfile(source, raw_directory / name)
        result["artifacts"].append({"path": f"raw/{name}", "sha256": gate.sha256_file(raw_directory / name)})
    target = directory / "lane-result.json"
    target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return target


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--policy", required=True, type=Path)
    parser.add_argument("--lane-id", required=True)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--feed", required=True, type=Path)
    parser.add_argument("--repo", type=Path, help="default: the repository containing the policy (the parent of its eng/ directory)")
    parser.add_argument("--candidate-version")
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--context", choices=sorted(gate.CONTEXTS), required=True)
    parser.add_argument("--run-id")
    parser.add_argument("--host-os", required=True, choices=sorted(gate.OSES))
    parser.add_argument("--host-arch", required=True)
    parser.add_argument("--raw", action="append", default=[], type=Path, help="raw output file or directory (repeatable)")
    parser.add_argument("--producer-exit", type=int, required=True,
                        help="exit code of the command that produced the raw outputs (required: there is no fail-open default)")
    parser.add_argument("--source-dirty", action="store_true", help="the source working tree had uncommitted changes")
    parser.add_argument("--service", action="append", default=[], help="name=image (use name=auto to read the image from docker)")
    args = parser.parse_args(argv)
    try:
        policy = json.loads(args.policy.read_text(encoding="utf-8"))
        gate.validate_policy(policy)
        lane = next((l for l in policy["lanes"] if l["id"] == args.lane_id), None)
        if lane is None:
            raise gate.PolicyError(f"unknown lane id {args.lane_id}")
        version = args.candidate_version or gate._discover_version(args.feed)
    except (OSError, ValueError, gate.PolicyError) as error:
        print(f"lane collector configuration error: {error}", file=sys.stderr)
        return gate.EXIT_CONFIG
    claimed = dict(item.split("=", 1) for item in args.service)
    result = build_lane_result(policy, lane, args.raw, args.feed, version, args.source_commit, args.context, args.run_id,
                               {"os": args.host_os, "arch": args.host_arch}, args.producer_exit, claimed, dict(os.environ),
                               repo=args.repo or gate.policy_repository(args.policy), source_dirty=args.source_dirty)
    target = write_lane(args.evidence, lane, result, args.raw)
    print(f"LANE {lane['id']} collected status={result['status']} -> {target}")
    for item in result["limitations"]:
        print(f"  limitation: {item}")
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())

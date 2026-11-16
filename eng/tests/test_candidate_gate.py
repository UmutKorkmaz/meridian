"""Behavioral tests for the candidate gate evaluator (eng/candidate_gate.py).

Every passing lane in these tests is produced by the real collector (eng/lane_collect.py) from real raw files in the
shapes the repository scripts write: TRX documents with linked results and ResultSummary counters, sample/consumer
receipts, the executed native output and receipts, Cobertura documents, an MSBuild log and toolchain receipts. The feed
is a real tiny feed whose package manifest hashes the archives, names the inventory hash and the source commit. The
negative tests change exactly one input of that passing evidence (a raw file, the recorded summary, the feed or the
policy), so an evaluator that trusts a recorded summary, a partial binding or a self-reported verdict cannot pass.
"""

import copy
import base64
import hashlib
import io
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import types
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))

import candidate_gate as gate  # noqa: E402
import coverage_gate as cov  # noqa: E402
import lane_collect as collect  # noqa: E402

VERSION = "2.1.4-gatetest.1"
COMMIT = "a" * 40
REDIS = "redis:7.4-alpine@sha256:" + "1" * 64
PGRES = "postgres:16.4-alpine@sha256:" + "2" * 64
CASES = ["case-one", "case-two", "case-three"]
BUILD_SDK, SERVICING_SDK = "10.0.300", "10.0.402"
TRX_NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
POSIX_BIN = "/home/runner/work/meridian/tests/{assembly}/bin/Release/{framework}/{assembly}.dll"
WINDOWS_BIN = "D:\\a\\meridian\\tests\\{assembly}\\bin\\Release\\{framework}\\{assembly}.dll"
SAMPLES = {"net8.0": ["Alpha", "Beta"], "net10.0": ["Alpha", "Beta", "Gamma"]}


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def real_trx(assembly, framework, passed=0, failed=0, skipped=0, code_base=None, counters_total=None, extra_assembly=None):
    """A TRX document in the shape the VSTest TRX logger writes: linked results/definitions plus ResultSummary counters."""
    code_base = code_base or POSIX_BIN.format(assembly=assembly, framework=framework)
    results, definitions, entries, index = [], [], [], 0
    for outcome, count in (("Passed", passed), ("Failed", failed), ("NotExecuted", skipped)):
        for _ in range(count):
            index += 1
            test_id, execution_id = f"00000000-0000-0000-0000-{index:012d}", f"10000000-0000-0000-0000-{index:012d}"
            base = code_base if not (extra_assembly and index == 1) else code_base.replace(assembly, extra_assembly)
            results.append(f'<UnitTestResult executionId="{execution_id}" testId="{test_id}" testName="{assembly}.Case{index}" '
                           f'computerName="runner" duration="00:00:00.001" outcome="{outcome}" />')
            entries.append(f'<TestEntry testId="{test_id}" executionId="{execution_id}" />')
            definitions.append(f'<UnitTest name="{assembly}.Case{index}" storage="{base}" id="{test_id}"><Execution id="{execution_id}" />'
                               f'<TestMethod codeBase="{base}" adapterTypeName="executor://xunit/VsTestRunner2/netcoreapp" '
                               f'className="{assembly}.Cases" name="Case{index}" /></UnitTest>')
    total = passed + failed + skipped if counters_total is None else counters_total
    summary = "Failed" if failed else "Completed"
    return (f'<?xml version="1.0" encoding="utf-8"?><TestRun id="r" name="run" xmlns="{TRX_NS}"><Results>{"".join(results)}</Results>'
            f'<TestDefinitions>{"".join(definitions)}</TestDefinitions><TestEntries>{"".join(entries)}</TestEntries><ResultSummary outcome="{summary}">'
            f'<Counters total="{total}" executed="{passed + failed}" passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" '
            f'inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="{skipped}" disconnected="0" warning="0" completed="0" '
            f'inProgress="0" pending="0" /></ResultSummary></TestRun>')


def native_output(cases, framework="net10.0", os_name="linux", arch="x64", omit=None):
    family = framework[3:].split(".")[0]
    ids = [c for c in cases if c != omit]
    lines = [f"CASE {c} PASS" for c in ids]
    lines.append(f"SUMMARY schema=meridian-native-aot-cases/1 mode=native count={len(ids)} expected={len(cases)} ids={','.join(ids)}")
    lines.append(f"HOST runtime={family}.0.1")
    lines.append(f"NATIVE os={os_name} arch={arch} dynamicCodeSupported=false dynamicCodeCompiled=false runtime=.NET {family}.0.1")
    return "\n".join(lines) + "\n"


def mini_policy():
    """A policy that exercises every lane kind with the smallest possible numbers."""
    linux, win = {"os": "linux", "arch": "x64"}, {"os": "win", "arch": "x64"}
    return {
        "schema": "meridian-required-checks/1",
        "policyVersion": 1,
        "packageInventory": "eng/packages.json",
        "providers": {
            "redis": {"image": REDIS, "environment": "MERIDIAN_TEST_REDIS"},
            "postgres": {"image": PGRES, "environment": "MERIDIAN_TEST_POSTGRES"},
        },
        "toolchains": {
            "pinnedCompatibility": {"buildSdk": BUILD_SDK, "lanes": [{"framework": "net8.0", "sdk": "8.0.421", "runtime": "8.0.27"},
                                                                    {"framework": "net10.0", "sdk": BUILD_SDK, "runtime": "10.0.8"}]},
            "currentServicing": {"buildSdk": SERVICING_SDK, "lanes": [{"framework": "net10.0", "sdk": SERVICING_SDK, "runtime": "10.0.13"}]},
        },
        "samples": {framework: len(names) for framework, names in SAMPLES.items()},
        "sampleNames": copy.deepcopy(SAMPLES),
        "propertyCases": [10000, 500],
        "nativeCases": list(CASES),
        "coverage": {"baseline": "eng/coverage-baseline.json"},
        "warnings": {"baseline": "eng/warning-baseline.json", "solution": "Meridian.sln"},
        "countProfiles": {
            "runtime": {
                "net8.0": [{"suite": "Core.Tests", "minimum": 10}],
                "net10.0": [{"suite": "Core.Tests", "minimum": 10}, {"suite": "Http.Tests", "minimum": 4}],
            },
            "redis": {"net10.0": [{"suite": "Redis.Tests", "minimum": 5}]},
            "http": {"net10.0": [{"suite": "Http.Tests", "minimum": 4}]},
            "property": {"net10.0": [{"suite": "Core.Tests", "minimum": 2}]},
            "coverage": {"net10.0": [{"suite": "Lib.Tests", "minimum": 12}]},
            "servicing": {"net10.0": [{"suite": "Core.Tests", "minimum": 10}]},
        },
        "lanes": [
            {"id": "runtime/linux/net8.0", "kind": "runtime-tests", "check": "runtime (linux, net8.0)", "framework": "net8.0",
             "host": dict(linux), "countsProfile": "runtime"},
            {"id": "runtime/win/net10.0", "kind": "runtime-tests", "check": "runtime (win, net10.0)", "framework": "net10.0",
             "host": dict(win), "countsProfile": "runtime"},
            {"id": "consumers/linux/net10.0", "kind": "package-consumers", "check": "consumers (linux, net10.0)",
             "framework": "net10.0", "sdk": BUILD_SDK, "host": dict(linux), "consumerScope": "partial",
             "omittedConsumerIds": ["Redis.Consumer"]},
            {"id": "provider/redis/net10.0", "kind": "provider", "check": "redis (net10.0)", "framework": "net10.0",
             "services": ["redis"], "countsProfile": "redis"},
            {"id": "http/net10.0", "kind": "provider", "check": "http (net10.0)", "framework": "net10.0", "services": ["redis"],
             "countsProfile": "http", "httpSmoke": True},
            {"id": "native/linux-x64/net10.0", "kind": "native-aot", "check": "native linux-x64 (net10.0)",
             "framework": "net10.0", "sdk": BUILD_SDK, "rid": "linux-x64", "host": dict(linux)},
            {"id": "properties/net10.0", "kind": "property-counts", "check": "properties (net10.0)", "framework": "net10.0",
             "countsProfile": "property"},
            {"id": "coverage/net10.0", "kind": "coverage", "check": "coverage (net10.0)", "framework": "net10.0", "countsProfile": "coverage"},
            {"id": "warnings/net10.0", "kind": "warning-ratchet", "check": "warnings (net10.0)", "framework": "net10.0"},
            {"id": "servicing/net10.0", "kind": "current-servicing", "check": "servicing (net10.0)", "framework": "net10.0",
             "sdk": SERVICING_SDK, "runtime": "10.0.13", "countsProfile": "servicing", "toolchainRootEnvironment": "MERIDIAN_SERVICING_DOTNET_ROOT"},
        ],
    }


class FakeDocker:
    """`docker` as the collector calls it: a container publishing the redis port from the pinned image."""

    def __init__(self, port, image=REDIS, digests=None):
        self.port, self.image = port, image
        self.digests = digests if digests is not None else ["redis@" + REDIS.split("@")[1]]
        self.calls = []

    def __call__(self, arguments):
        self.calls.append(list(arguments))
        reply = lambda out, code=0: types.SimpleNamespace(returncode=code, stdout=out, stderr="")  # noqa: E731
        if arguments[:2] == ["context", "show"]:
            return reply("fixture-local\n")
        if arguments[:2] == ["context", "inspect"]:
            return reply('"unix:///owned-fixture/docker.sock"\n')
        if arguments[0] == "ps":
            # The default listing truncates ids (and drops digests); only --no-trunc yields an id `inspect` accepts.
            container = "c0ffee" * 10 if "--no-trunc" in arguments else "c0ffee"
            return reply(f"{container}\t0.0.0.0:{self.port}->6379/tcp, :::{self.port}->6379/tcp\n")
        if arguments[0] == "inspect":
            if arguments[-1] != "c0ffee" * 10:
                return reply("", 1)
            if "{{json .NetworkSettings.Ports}}" in arguments:
                return reply(json.dumps({"6379/tcp": [{"HostIp": "0.0.0.0", "HostPort": str(self.port)}]}) + "\n")
            return reply(f"{self.image}\tsha256:{'7' * 64}\n")
        if arguments[:2] == ["image", "inspect"]:
            return reply(json.dumps(self.digests) + "\n")
        return reply("", 1)


class Fixture:
    """A feed, an inventory, baselines and honest raw outputs for every lane, collected with the real collector."""

    SOLUTION_PROJECTS = ["src/Lib/Lib.csproj", "tests/Lib.Tests/Lib.Tests.csproj"]
    INVENTORY = ["Meridian.Mapping", "Pkg.B"]

    def __init__(self, test, produce=True):
        self.test = test
        self.root = Path(tempfile.mkdtemp(prefix="meridian-gate-test-"))
        test.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.feed, self.evidence = self.root / "feed", self.root / "evidence"
        for directory in (self.feed, self.evidence, self.root / "eng"):
            directory.mkdir()
        self.policy = mini_policy()
        inventory = {"schemaVersion": 1, "packages": [{"id": i, "projectDependencies": [], "baselineVersion": "2.1.3" if i == "Meridian.Mapping" else None} for i in self.INVENTORY],
                     "consumers": [{"id": "A.Consumer", "variants": ["legacy"], "packageIds": ["Meridian.Mapping"], "baselineComparison": True},
                                   {"id": "Redis.Consumer", "variants": ["legacy"], "packageIds": ["Pkg.B"], "baselineComparison": False}]}
        for consumer in inventory["consumers"]:
            folder = f"tests/consumers/{consumer['id']}"
            consumer["project"] = f"{folder}/{consumer['id']}.csproj"
            consumer["sourceFiles"] = [consumer["project"], f"{folder}/Program.cs"]
            for source in consumer["sourceFiles"]:
                path = self.root / source
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("<Project />" if source.endswith(".csproj") else 'Console.WriteLine("COMMON fixture=unchanged");')
        self.inventory_path = self.root / "eng/packages.json"
        self.inventory_path.write_text(json.dumps(inventory))
        self.packages, self.records = {}, []
        for name in self.INVENTORY:
            data = f"nupkg-{name}".encode()
            (self.feed / f"{name}.{VERSION}.nupkg").write_bytes(data)
            self.packages[name] = sha(data)
            self.records.append({"id": name, "package": f"{name}.{VERSION}.nupkg", "sha256": sha(data), "symbols": None, "symbolsSha256": None})
        self.write_manifest(self.records)
        self.port = self._serve()
        self.docker = FakeDocker(self.port)
        self.environment = {"MERIDIAN_TEST_REDIS": f"127.0.0.1:{self.port},abortConnect=true"}
        self.baselines()
        if produce:
            for lane in self.policy["lanes"]:
                self.produce(lane["id"])

    # -- infrastructure -------------------------------------------------------------------------------------------
    def write_manifest(self, records, **overrides):
        manifest = {"schemaVersion": 1, "candidateVersion": VERSION, "manifestSha256": sha(self.inventory_path.read_bytes()),
                    "sourceCommit": COMMIT, "sourceDirty": False, "packages": records}
        manifest.update(overrides)
        text = json.dumps(manifest, indent=2).encode()
        (self.feed / f"package-manifest.{VERSION}.json").write_bytes(text)
        self.manifest_sha = sha(text)

    def _serve(self):
        server = socket.socket()
        server.bind(("127.0.0.1", 0))
        server.listen(32)
        stop = threading.Event()

        def accept():
            server.settimeout(0.1)
            while not stop.is_set():
                try:
                    server.accept()[0].close()
                except OSError:
                    pass

        thread = threading.Thread(target=accept, daemon=True)
        thread.start()
        self.test.addCleanup(server.close)
        self.test.addCleanup(thread.join, 2)
        self.test.addCleanup(stop.set)
        return server.getsockname()[1]

    def baselines(self):
        self.coverage_files = {"src/Lib/Hot.cs": [(n, 1) for n in range(1, 11)], "src/Lib/Cold.cs": [(1, 1), (2, 0)]}
        baseline = {"schema": "meridian-coverage-baseline/1", "tolerance": 0.0, "targets": {"line": 90.0, "branch": 80.0},
                    "aggregate": {"line": 90.0}, "files": {"src/Lib/Hot.cs": {"line": 100.0, "branch": None},
                                                            "src/Lib/Cold.cs": {"line": 50.0, "branch": None}},
                    "hotPaths": ["src/Lib/Hot.cs"]}
        (self.root / "eng/coverage-baseline.json").write_text(json.dumps(baseline))
        warnings_baseline = {"schemaVersion": 1, "scope": "test", "unit": "unique", "rule": "ratchet", "total": 1, "byCode": {"CA1305": 1},
                             "projects": {"src/Lib/Lib.csproj": {"total": 1, "codes": {"CA1305": 1}}}}
        (self.root / "eng/warning-baseline.json").write_text(json.dumps(warnings_baseline))
        projects = "".join(f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "{Path(p).stem}", "{p.replace("/", chr(92))}", '
                           f'"{{{i:08d}-0000-0000-0000-000000000000}}"\nEndProject\n' for i, p in enumerate(self.SOLUTION_PROJECTS))
        (self.root / "Meridian.sln").write_text("Microsoft Visual Studio Solution File, Format Version 12.00\n"
                                                'Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{11111111-0000-0000-0000-000000000000}"\nEndProject\n'
                                                + projects)

    def lane(self, lane_id):
        return next(l for l in self.policy["lanes"] if l["id"] == lane_id)

    def raw_dir(self, lane_id):
        directory = self.root / "raw" / lane_id.replace("/", "__")
        if directory.exists():
            shutil.rmtree(directory)
        directory.mkdir(parents=True)
        return directory

    def lane_dir(self, lane_id):
        return self.evidence / "lanes" / lane_id.replace("/", "__")

    def path(self, lane_id):
        return self.lane_dir(lane_id) / "lane-result.json"

    def read(self, lane_id):
        return json.loads(self.path(lane_id).read_text())

    def mutate(self, lane_id, fn):
        result = self.read(lane_id)
        fn(result)
        self.path(lane_id).write_text(json.dumps(result, indent=2))

    def collect(self, lane_id, raw, producer_exit=0, dirty=False, services="auto"):
        lane = self.lane(lane_id)
        host = {"os": (lane.get("host") or {}).get("os", "linux"), "arch": (lane.get("host") or {}).get("arch", "x64")}
        claimed = {name: (services if services == "auto" else self.policy["providers"][name]["image"]) for name in lane.get("services", [])}
        result = collect.build_lane_result(self.policy, lane, [raw], self.feed, VERSION, COMMIT, "local", None, host, producer_exit,
                                           claimed, dict(self.environment), toolchain={"sdk": "informational"}, repo=self.root,
                                           source_dirty=dirty, docker=self.docker)
        collect.write_lane(self.evidence, lane, result, [raw])
        return result

    def evaluate(self, **kwargs):
        options = {"host": None, "allow_pending": False, "source_commit": None, "only": None, "repo": self.root}
        options.update(kwargs)
        return gate.evaluate(self.policy, self.feed, self.evidence, VERSION, **options)

    def state(self, lane_id):
        summary = self.evaluate(only=[lane_id])
        return next(l for l in summary["lanes"] if l["id"] == lane_id), summary

    # -- honest raw outputs per lane kind -------------------------------------------------------------------------
    def toolchain(self, raw, sdk=BUILD_SDK, sdks=("8.0.421", BUILD_SDK), runtimes=("8.0.27", "10.0.8")):
        (raw / "lane-toolchain.json").write_text(json.dumps({
            "dotnetPath": "/usr/share/dotnet/dotnet", "dotnetRoot": None, "sdkVersion": sdk,
            "sdks": [f"{s} [/usr/share/dotnet/sdk]" for s in sdks],
            "runtimes": [f"Microsoft.NETCore.App {r} [/usr/share/dotnet/shared/Microsoft.NETCore.App]" for r in runtimes]
                        + ["Microsoft.AspNetCore.App 10.0.8 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]"]}))

    def trx_for_profile(self, raw, lane, failed=0, skipped=0, scale=1, framework=None, code_base=None, extra_assembly=None,
                        counters_total=None, drop=None):
        framework = framework or lane["framework"]
        for requirement in self.policy["countProfiles"][lane["countsProfile"]][lane["framework"]]:
            if requirement["suite"] == drop:
                continue
            suite, total = requirement["suite"], requirement["minimum"] * scale + 1
            base = code_base.format(assembly=suite, framework=framework) if code_base else None
            (raw / f"{suite}.trx").write_text(real_trx(suite, framework, passed=total - failed - skipped, failed=failed, skipped=skipped,
                                                       code_base=base, extra_assembly=extra_assembly, counters_total=counters_total))

    def samples(self, raw, framework, names=None, failing=(), http=None):
        names = SAMPLES[framework] if names is None else names
        summary = {"framework": framework, "includeHttp": http is not None,
                   "samples": [{"name": n, "exit": 1 if n in failing else 0} for n in names]}
        if http is not None:
            summary["http"] = http
        (raw / "samples-summary.json").write_text(json.dumps(summary))

    def runtime_raw(self, lane_id="runtime/linux/net8.0", names=None, failing=(), toolchain=None, **trx):
        lane = self.lane(lane_id)
        raw = self.raw_dir(lane_id)
        if lane["host"]["os"] == "win" and "code_base" not in trx:
            trx["code_base"] = WINDOWS_BIN
        self.trx_for_profile(raw, lane, **trx)
        self.samples(raw, lane["framework"], names, failing)
        self.toolchain(raw, **(toolchain or {}))
        return raw

    def provider_raw(self, lane_id="provider/redis/net10.0", **trx):
        raw = self.raw_dir(lane_id)
        self.trx_for_profile(raw, self.lane(lane_id), **trx)
        self.toolchain(raw)
        return raw

    def http_raw(self, http=None, ownership=True):
        raw = self.provider_raw("http/net10.0")
        smoke = {"exit": 0, "timeout": False, "exitObserved": True, "drainsObserved": True, "ownershipValidated": True}
        smoke.update(http or {})
        self.samples(raw, "net10.0", http=smoke)
        if ownership:
            (raw / "http-ownership.json").write_text(json.dumps({"pid": 4242, "token": "t" * 32, "kind": "unix-session"}))
        return raw

    def consumer_raw(self, lane_id="consumers/linux/net10.0", rows=None, selection=None):
        lane = self.lane(lane_id)
        raw = self.raw_dir(lane_id)
        (raw / "consumer-selection.json").write_text(json.dumps(selection or {"scope": "partial", "selectedIds": ["A.Consumer"], "omittedIds": ["Redis.Consumer"]}))
        rows = rows if rows is not None else [{"framework": lane["framework"], "sdkVersion": lane["sdk"], "compilerVersion": "5.0.0",
                                               "consumer": "A.Consumer", "variant": "legacy", "packageVersion": VERSION, "exit": 0}]
        contract = "currentServicing" if lane.get("toolchainRootEnvironment") else "pinnedCompatibility"
        runtime = gate._toolchain_rows(self.policy, contract)[lane["framework"]]["runtime"]
        baseline_runtime = gate._toolchain_rows(self.policy, contract).get("net10.0", {"runtime": "10.0.8"})["runtime"]
        for row in rows:
            row.setdefault("runtime", f"HOST runtime={runtime}")
        rows = rows + [{"framework": "net10.0", "sdkVersion": BUILD_SDK, "compilerVersion": "5.0.0", "consumer": "A.Consumer",
                        "variant": "baseline", "packageVersion": "2.1.3", "runtime": f"HOST runtime={baseline_runtime}", "exit": 0}]
        inventory = json.loads(self.inventory_path.read_text())
        packages = {p["id"]: p for p in inventory["packages"]}
        contracts = {c["id"]: c for c in inventory["consumers"]}
        baseline_buffer = io.BytesIO()
        with zipfile.ZipFile(baseline_buffer, 'w') as baseline_archive:
            baseline_archive.writestr('parser-fixture.txt', b'published-baseline-byte-fixture')
        baseline_data = baseline_buffer.getvalue()
        baseline_file = "Meridian.Mapping.2.1.3.baseline-nupkg"
        (raw / baseline_file).write_bytes(baseline_data)
        (raw / "baseline-hashes.json").write_text(json.dumps([{"id": "Meridian.Mapping", "version": "2.1.3", "file": "meridian.mapping.2.1.3.nupkg", "artifact": baseline_file, "sha256": sha(baseline_data)}]))
        for row in rows:
            contract_row = contracts.get(row.get("consumer"), contracts["A.Consumer"])
            closure, pending = set(), list(contract_row.get("packageIds", []))
            while pending:
                package = pending.pop()
                if package not in closure:
                    closure.add(package)
                    pending.extend(packages[package].get("projectDependencies", []))
            row.setdefault("packageIds", sorted(closure))
            row.setdefault("baselineComparison", bool(contract_row.get("baselineComparison")))
            row.setdefault("commonOutput", ["COMMON fixture=unchanged"])
            stem = f"{row.get('consumer', 'unknown')}-{row.get('variant', 'unknown')}-{row.get('framework', 'unknown')}"
            binary_name, deps_name = stem + ".consumer-binary", stem + ".consumer-deps.json"
            binary = ("managed-byte-fixture:" + stem).encode()
            (raw / binary_name).write_bytes(binary)
            row.setdefault("binaryArtifact", binary_name)
            row.setdefault("binarySha256", sha(binary))
            row.setdefault("depsArtifact", deps_name)
            assets_name = stem + ".consumer-assets.json"
            row.setdefault("assetsArtifact", assets_name)
            row.setdefault("sourceFiles", [{"source": source, "file": Path(source).name, "sha256": sha((self.root / source).read_bytes())} for source in contract_row.get("sourceFiles", [])])
            libraries = {}
            for package in sorted(closure):
                archive = baseline_data if row.get("variant") == "baseline" else (self.feed / f"{package}.{VERSION}.nupkg").read_bytes()
                libraries[f"{package}/{row.get('packageVersion')}"] = {"type": "package", "sha512": "sha512-" + base64.b64encode(hashlib.sha512(archive).digest()).decode()}
            framework_name = ".NETCoreApp,Version=v" + str(row.get("framework", ""))[3:]
            (raw / deps_name).write_text(json.dumps({"runtimeTarget": {"name": framework_name}, "libraries": libraries}))
            (raw / assets_name).write_text(json.dumps({"project": {"frameworks": {row.get("framework"): {}}}, "libraries": libraries}))
        (raw / "consumer-results.json").write_text(json.dumps(rows))
        return raw

    def native_raw(self, lane_id="native/linux-x64/net10.0", combined_overrides=None, omit_case=None, mapping_package=None,
                   second_combined=False, mapping_log=True):
        lane = self.lane(lane_id)
        target = gate.RID_HOSTS[lane["rid"]]
        raw = self.raw_dir(lane_id)
        work = raw / "native/run"
        work.mkdir(parents=True)
        binary = f"{work}/publish/Meridian.NativeAotConsumer"
        # Format-parser fixtures only: these byte artifacts are never executed by this unit test.
        header = bytearray(64)
        if lane["rid"] == "osx-arm64":
            header[:4] = b"\xcf\xfa\xed\xfe"
            header[4:8] = (0x100000C).to_bytes(4, "little")
        else:
            header[:6] = b"\x7fELF\x02\x01"
            header[18:20] = (62).to_bytes(2, "little")
        Path(binary).parent.mkdir()
        Path(binary).write_bytes(header + b"combined-format-fixture")
        shutil.copyfile(binary, work / "combined.native-executable")
        (work / "execute.log").write_text(f"command={binary} --require-native --expect-os {target['os']} --expect-arch {target['arch']}\nexit=0\n"
                                          + native_output(CASES, lane["framework"], target["os"], target["arch"], omit=omit_case))
        combined = {"mode": "combined", "consumer": "Meridian.NativeAotConsumer", "candidateVersion": VERSION, "framework": lane["framework"],
                    "sdk": BUILD_SDK, "compiler": "5.0.0", "rid": lane["rid"], "file": binary, "retainedFile": "combined.native-executable", "sha256": sha(Path(binary).read_bytes()),
                    "format": gate.RID_FORMATS.get(lane["rid"], "pe-x64"), "exit": 0, "nativeRuntime": ".NET 10.0.1", "caseIds": list(CASES),
                    "caseCount": len(CASES), "hostOs": target["os"], "hostArch": target["arch"], "manifestSha256": "e" * 64, "packageRecordSha256": self.manifest_sha, "warningCount": 0,
                    "unreviewedWarnings": 0}
        combined.update(combined_overrides or {})
        (work / "native-result.json").write_text(json.dumps(combined))
        if second_combined:
            other = work.parent / "rerun"
            other.mkdir()
            (other / "native-result.json").write_text(json.dumps(dict(combined, sha256="9" * 64)))
        mapping = raw / "native-mapping/run"
        mapping.mkdir(parents=True)
        mapped = f"{mapping}/publish/Meridian.GeneratedOnlyConsumer"
        Path(mapped).parent.mkdir()
        Path(mapped).write_bytes(header + b"mapping-format-fixture")
        shutil.copyfile(mapped, mapping / "mapping.native-executable")
        if mapping_log:
            (mapping / "execute.log").write_text(f"command={mapped} \nexit=0\ngenerated-only: value=42 cycle=true collectionIdentity=true "
                                                 "depth=true limit=true cancellation=true\n")
        package = mapping_package or {"file": f"/feed/Meridian.Mapping.{VERSION}.nupkg", "sha256": self.packages["Meridian.Mapping"]}
        (mapping / "package-hash.json").write_text(json.dumps(package))
        (mapping / "native-result.json").write_text(json.dumps({"file": mapped, "retainedFile": "mapping.native-executable", "sha256": sha(Path(mapped).read_bytes()), "sdk": BUILD_SDK,
                                                                "framework": lane["framework"], "rid": lane["rid"]}))
        return raw

    def property_raw(self, log=" Ok, passed 10000 tests.\n Ok, passed 500 tests.\n", failed=0):
        raw = self.raw_dir("properties/net10.0")
        (raw / "property-output.log").write_text("Configured budgets\n" + log)
        self.trx_for_profile(raw, self.lane("properties/net10.0"), failed=failed)
        self.toolchain(raw)
        return raw

    def coverage_raw(self, hits=None, receipt=None):
        raw = self.raw_dir("coverage/net10.0")
        classes = []
        for path, lines in (hits or self.coverage_files).items():
            line_xml = "".join(f'<line number="{n}" hits="{h}" branch="False" />' for n, h in lines)
            classes.append(f'<class name="C" filename="{path}" line-rate="1" branch-rate="1"><methods><method name="M" signature="()" '
                           f'line-rate="1" branch-rate="1"><lines>{line_xml}</lines></method></methods><lines>{line_xml}</lines></class>')
        document = raw / "cov/Lib.Tests/run/coverage.cobertura.xml"
        document.parent.mkdir(parents=True)
        document.write_text(f'<?xml version="1.0" encoding="utf-8"?><coverage line-rate="1" branch-rate="1"><sources><source>{self.root}/</source>'
                            f'</sources><packages><package name="P"><classes>{"".join(classes)}</classes></package></packages></coverage>')
        baseline_path = self.root / "eng/coverage-baseline.json"
        result = cov.check(json.loads(baseline_path.read_text()), cov.measure([document], str(self.root)))
        result.update(repo=str(self.root), baselineSha256=sha(baseline_path.read_bytes()),
                      documents=[{"path": str(document), "sha256": sha(document.read_bytes())}])
        result.update(receipt or {})
        (raw / "coverage-gate.json").write_text(json.dumps(result))
        self.trx_for_profile(raw, self.lane("coverage/net10.0"))
        self.toolchain(raw)
        return raw

    def warning_raw(self, extra_warning=False, incremental=False, skip_project=None, receipt=None, exit_code=0):
        raw = self.raw_dir("warnings/net10.0")
        command = "command=dotnet build Meridian.sln -c Release " + ("" if incremental else "--no-incremental ") + "-nologo -v:minimal -clp:NoSummary"
        lines = [command, f"exit={exit_code}",
                 f"{self.root}/src/Lib/A.cs(10,5): warning CA1305: msg [{self.root}/src/Lib/Lib.csproj::TargetFramework=net8.0]"]
        if extra_warning:
            lines.append(f"{self.root}/src/Lib/A.cs(30,5): warning CA1305: msg [{self.root}/src/Lib/Lib.csproj::TargetFramework=net8.0]")
        for project in self.SOLUTION_PROJECTS:
            name = Path(project).stem
            if name != skip_project:
                lines.append(f"  {name} -> {self.root}/{Path(project).parent}/bin/Release/net8.0/{name}.dll")
        (raw / "build.log").write_text("\n".join(lines) + "\n")
        check = {"failures": [], "missingProjects": [], "current": {"total": 1}, "repo": str(self.root)}
        check.update(receipt or {})
        (raw / "warnings-check.json").write_text(json.dumps(check))
        self.toolchain(raw)
        return raw

    def servicing_raw(self, sdk=SERVICING_SDK, runtimes=("10.0.13",), **trx):
        raw = self.raw_dir("servicing/net10.0")
        self.trx_for_profile(raw, self.lane("servicing/net10.0"), **trx)
        self.toolchain(raw, sdk=sdk, sdks=(SERVICING_SDK,), runtimes=runtimes)
        return raw

    def honest_raw(self, lane_id):
        kind = self.lane(lane_id)["kind"]
        if lane_id == "http/net10.0":
            return self.http_raw()
        return {"runtime-tests": lambda: self.runtime_raw(lane_id), "package-consumers": lambda: self.consumer_raw(lane_id),
                "provider": lambda: self.provider_raw(lane_id), "native-aot": lambda: self.native_raw(lane_id),
                "property-counts": self.property_raw, "coverage": self.coverage_raw, "warning-ratchet": self.warning_raw,
                "current-servicing": self.servicing_raw}[kind]()

    def produce(self, lane_id):
        return self.collect(lane_id, self.honest_raw(lane_id))


def lane_state(summary, lane_id):
    return next(l for l in summary["lanes"] if l["id"] == lane_id)


class Base(unittest.TestCase):
    def passes(self, f, lane_id):
        state, summary = f.state(lane_id)
        self.assertEqual(state["state"], "PASS", state["reasons"])
        self.assertEqual(summary["overall"], "SCOPED-PASS")
        return state

    def fails(self, f, lane_id, *fragments):
        state, summary = f.state(lane_id)
        self.assertEqual(state["state"], "FAIL", f"counterexample accepted: {summary['overall']}")
        self.assertEqual(summary["exitCode"], gate.EXIT_FAIL)
        text = " ".join(state["reasons"] + summary["failures"])
        for fragment in fragments:
            self.assertIn(fragment, text)
        return state


class EvaluatorVerdictTests(Base):
    def test_complete_valid_evidence_passes_with_exit_zero(self):
        f = Fixture(self)
        summary = f.evaluate()
        self.assertEqual(summary["overall"], "PASS", summary["failures"])
        self.assertEqual(summary["exitCode"], gate.EXIT_PASS)
        self.assertTrue(summary["releaseEligible"])
        self.assertTrue(summary["wholeCandidate"])
        self.assertTrue(all(l["state"] == "PASS" for l in summary["lanes"]))
        for lane in f.policy["lanes"]:
            self.assertEqual(f.read(lane["id"])["status"], "PASS", f.read(lane["id"])["limitations"])

    def test_missing_lane_evidence_fails_closed(self):
        f = Fixture(self)
        f.path("properties/net10.0").unlink()
        summary = f.evaluate()
        self.assertEqual(lane_state(summary, "properties/net10.0")["state"], "MISSING")
        self.assertEqual(summary["overall"], "FAIL")
        self.assertEqual(summary["exitCode"], gate.EXIT_FAIL)
        self.assertFalse(summary["releaseEligible"])

    def test_failed_status_fails(self):
        f = Fixture(self)
        f.mutate("properties/net10.0", lambda r: r.update(status="FAIL"))
        self.assertEqual(f.evaluate()["exitCode"], gate.EXIT_FAIL)

    def test_a_lane_cannot_pass_by_self_report_when_counts_show_a_failure(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(failed=1))
        self.fails(f, "runtime/linux/net8.0", "failed test")
        self.assertEqual(f.read("runtime/linux/net8.0")["status"], "FAIL")

    def test_mandatory_skip_in_the_retained_trx_fails(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(skipped=1))
        self.fails(f, "runtime/linux/net8.0", "skipped")

    def test_listed_skip_entry_fails_even_when_counts_look_clean(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r.update(skips=[{"suite": "Core.Tests", "name": "Some.Test", "reason": "env"}]))
        self.fails(f, "runtime/linux/net8.0", "skipped")

    def test_total_below_recorded_minimum_fails(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(scale=0))
        self.fails(f, "runtime/linux/net8.0", "minimum")

    def test_required_suite_absent_from_the_retained_trx_fails(self):
        f = Fixture(self)
        f.collect("runtime/win/net10.0", f.runtime_raw("runtime/win/net10.0", drop="Http.Tests"))
        self.fails(f, "runtime/win/net10.0", "Http.Tests")

    def test_recorded_counts_that_differ_from_the_retained_trx_fail(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r["counts"][0].update(passed=r["counts"][0]["total"] - 2))
        self.fails(f, "runtime/linux/net8.0", "retained")

    def test_sample_count_must_match_policy(self):
        f = Fixture(self)
        f.collect("runtime/win/net10.0", f.runtime_raw("runtime/win/net10.0", names=["Alpha", "Beta"]))
        self.fails(f, "runtime/win/net10.0", "sample")

    def test_sample_failure_fails(self):
        f = Fixture(self)
        f.collect("runtime/win/net10.0", f.runtime_raw("runtime/win/net10.0", failing=("Beta",)))
        self.fails(f, "runtime/win/net10.0", "samples passed")

    def test_the_required_sample_names_must_have_run(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(names=["Alpha", "Other"]))
        self.fails(f, "runtime/linux/net8.0", "required samples")

    def test_one_sample_repeated_cannot_stand_in_for_the_others(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(names=["Alpha", "Alpha"]))
        self.fails(f, "runtime/linux/net8.0", "more than once")


class CandidateBindingTests(Base):
    def test_lane_bound_to_other_package_manifest_fails(self):
        f = Fixture(self)
        f.mutate("consumers/linux/net10.0", lambda r: r["candidate"].update(packageManifestSha256="0" * 64))
        self.fails(f, "consumers/linux/net10.0", "manifest")

    def test_lane_restored_package_hash_mismatch_fails(self):
        f = Fixture(self)
        f.mutate("consumers/linux/net10.0", lambda r: r["candidate"]["packages"].update({"Meridian.Mapping": "0" * 64}))
        self.fails(f, "consumers/linux/net10.0", "Meridian.Mapping")

    def test_lane_for_other_candidate_version_fails(self):
        f = Fixture(self)
        f.mutate("properties/net10.0", lambda r: r["candidate"].update(version="2.1.4-other.1"))
        self.fails(f, "properties/net10.0")

    def test_empty_candidate_package_binding_fails(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r["candidate"].update(packages={}))
        self.fails(f, "runtime/linux/net8.0", "no candidate package binding")

    def test_partial_candidate_package_binding_fails(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r["candidate"]["packages"].pop("Pkg.B"))
        self.fails(f, "runtime/linux/net8.0", "lacks candidate package Pkg.B")

    def test_tampered_feed_package_fails_globally(self):
        f = Fixture(self)
        (f.feed / f"Pkg.B.{VERSION}.nupkg").write_bytes(b"tampered")
        summary = f.evaluate()
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("Pkg.B" in x for x in summary["failures"]), summary["failures"])

    def test_a_feed_that_disagrees_with_its_own_manifest_fails_even_when_lanes_bound_to_the_changed_file(self):
        f = Fixture(self)
        changed = b"rebuilt after the manifest was written"
        (f.feed / f"Pkg.B.{VERSION}.nupkg").write_bytes(changed)
        for lane in f.policy["lanes"]:
            f.mutate(lane["id"], lambda r: r["candidate"]["packages"].update({"Pkg.B": sha(changed)}))
        summary = f.evaluate()
        self.assertTrue(all(l["state"] == "PASS" for l in summary["lanes"]), [l["reasons"] for l in summary["lanes"]])
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("differs from the recorded" in x for x in summary["failures"]))

    def test_missing_feed_package_fails_globally(self):
        f = Fixture(self)
        (f.feed / f"Meridian.Mapping.{VERSION}.nupkg").unlink()
        self.assertEqual(f.evaluate()["exitCode"], gate.EXIT_FAIL)

    def test_missing_feed_manifest_fails_globally(self):
        f = Fixture(self)
        (f.feed / f"package-manifest.{VERSION}.json").unlink()
        self.assertEqual(f.evaluate()["exitCode"], gate.EXIT_FAIL)

    def test_lanes_from_different_source_commits_fail(self):
        f = Fixture(self)
        f.mutate("properties/net10.0", lambda r: r["source"].update(commit="b" * 40))
        summary = f.evaluate()
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("source commit" in x for x in summary["failures"] + sum((l["reasons"] for l in summary["lanes"]), [])))

    def test_required_source_commit_is_enforced(self):
        f = Fixture(self)
        self.assertEqual(f.evaluate(source_commit="c" * 40)["overall"], "FAIL")
        self.assertEqual(f.evaluate(source_commit=COMMIT)["overall"], "PASS")

    def test_lanes_must_come_from_the_commit_the_candidate_was_packed_from(self):
        f = Fixture(self)
        f.write_manifest(f.records, sourceCommit="d" * 40)
        for lane in f.policy["lanes"]:
            f.mutate(lane["id"], lambda r: r["candidate"].update(packageManifestSha256=f.manifest_sha))
        summary = f.evaluate()
        self.assertEqual(summary["overall"], "FAIL")
        self.assertIn("packed from", " ".join(lane_state(summary, "properties/net10.0")["reasons"]))

    def test_a_manifest_without_a_source_commit_fails(self):
        f = Fixture(self)
        f.write_manifest(f.records, sourceCommit=None)
        summary = f.evaluate()
        self.assertTrue(any("records no source commit" in x for x in summary["failures"]), summary["failures"])

    def test_a_candidate_packed_from_a_dirty_tree_fails(self):
        f = Fixture(self)
        f.write_manifest(f.records, sourceDirty=True)
        self.assertTrue(any("uncommitted" in x for x in f.evaluate()["failures"]))

    def test_a_manifest_packed_from_another_inventory_fails(self):
        f = Fixture(self)
        f.write_manifest(f.records, manifestSha256="0" * 64)
        self.assertTrue(any("inventory" in x for x in f.evaluate()["failures"]))

    def test_the_independent_manifest_anchor_is_enforced(self):
        f = Fixture(self)
        self.assertEqual(f.evaluate(expected_manifest_sha256=f.manifest_sha)["overall"], "PASS")
        summary = f.evaluate(expected_manifest_sha256="0" * 64)
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("packed candidate" in x for x in summary["failures"]))


class FeedInventoryTests(Base):
    def run_scoped(self, f):
        f.produce("runtime/linux/net8.0")
        return f.evaluate(only=["runtime/linux/net8.0"])

    def test_a_feed_missing_an_inventory_package_fails(self):
        f = Fixture(self, produce=False)
        f.write_manifest([r for r in f.records if r["id"] != "Pkg.B"])
        (f.feed / f"Pkg.B.{VERSION}.nupkg").unlink()
        summary = self.run_scoped(f)
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("Pkg.B" in x and "inventory" in x for x in summary["failures"]), summary["failures"])

    def test_a_feed_package_outside_the_inventory_fails(self):
        f = Fixture(self, produce=False)
        (f.feed / f"Evil.Package.{VERSION}.nupkg").write_bytes(b"evil")
        f.write_manifest(f.records + [{"id": "Evil.Package", "package": f"Evil.Package.{VERSION}.nupkg", "sha256": sha(b"evil"),
                                       "symbols": None, "symbolsSha256": None}])
        self.assertTrue(any("Evil.Package" in x and "inventory" in x for x in self.run_scoped(f)["failures"]))

    def test_a_feed_archive_outside_the_feed_fails(self):
        f = Fixture(self, produce=False)
        shutil.move(f.feed / f"Pkg.B.{VERSION}.nupkg", f.root / f"Pkg.B.{VERSION}.nupkg")
        f.write_manifest([dict(r, package=f"../Pkg.B.{VERSION}.nupkg") if r["id"] == "Pkg.B" else r for r in f.records])
        summary = self.run_scoped(f)
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("Pkg.B" in x and "plain file name" in x for x in summary["failures"]), summary["failures"])

    def test_an_absolute_archive_path_fails(self):
        f = Fixture(self, produce=False)
        f.write_manifest([dict(r, package=str(f.feed / r["package"])) if r["id"] == "Pkg.B" else r for r in f.records])
        self.assertTrue(any("plain file name" in x for x in self.run_scoped(f)["failures"]))

    def test_an_archive_named_for_another_version_fails(self):
        f = Fixture(self, produce=False)
        shutil.move(f.feed / f"Pkg.B.{VERSION}.nupkg", f.feed / "Pkg.B.2.0.0.nupkg")
        f.write_manifest([dict(r, package="Pkg.B.2.0.0.nupkg") if r["id"] == "Pkg.B" else r for r in f.records])
        self.assertTrue(any("expected" in x and "Pkg.B" in x for x in self.run_scoped(f)["failures"]))

    def test_a_symlinked_feed_archive_fails(self):
        f = Fixture(self, produce=False)
        archive = f.feed / f"Pkg.B.{VERSION}.nupkg"
        outside = f.root / "elsewhere.nupkg"
        shutil.move(archive, outside)
        os.symlink(outside, archive)
        summary = self.run_scoped(f)
        self.assertEqual(summary["overall"], "FAIL")
        self.assertTrue(any("symbolic link" in x for x in summary["failures"]), summary["failures"])

    def test_an_archive_in_the_feed_that_the_manifest_does_not_list_fails(self):
        f = Fixture(self, produce=False)
        (f.feed / f"Pkg.Extra.{VERSION}.nupkg").write_bytes(b"extra")
        self.assertTrue(any("Pkg.Extra" in x for x in self.run_scoped(f)["failures"]))

    def test_duplicate_manifest_package_ids_fail(self):
        f = Fixture(self, produce=False)
        f.write_manifest(f.records + [dict(f.records[1])])
        self.assertTrue(any("duplicate" in x for x in self.run_scoped(f)["failures"]))

    def test_a_manifest_of_the_wrong_shape_is_a_recorded_failure_not_a_crash(self):
        for broken in ({"candidateVersion": VERSION, "packages": {"id": "x"}}, [1, 2], {"candidateVersion": VERSION, "packages": ["x"]}):
            with self.subTest(broken=broken):
                f = Fixture(self, produce=False)
                (f.feed / f"package-manifest.{VERSION}.json").write_text(json.dumps(broken))
                info = gate.inspect_feed(f.feed, VERSION)
                self.assertTrue(any("not an object" in x for x in info["problems"]), info["problems"])


class TrxTests(Base):
    def test_windows_trx_paths_yield_the_real_assembly_and_framework(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "w.trx"
            path.write_text(real_trx("Core.Tests", "net8.0", passed=3, code_base=WINDOWS_BIN.format(assembly="Core.Tests", framework="net8.0")))
            summary = gate.parse_trx(path)
        self.assertEqual((summary["assembly"], summary["framework"], summary["pathStyle"]), ("Core.Tests", "net8.0", "windows"))
        self.assertEqual(summary["problems"], [])

    def test_trx_summary_counts_outcomes_framework_and_skips(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "x.trx"
            path.write_text(real_trx("My.Tests", "net9.0", passed=3, failed=1, skipped=2))
            summary = gate.parse_trx(path)
        self.assertEqual((summary["assembly"], summary["framework"]), ("My.Tests", "net9.0"))
        self.assertEqual((summary["total"], summary["passed"], summary["failed"], summary["skipped"]), (6, 3, 1, 2))
        self.assertEqual(len(summary["skippedNames"]), 2)
        self.assertEqual(summary["countersTotal"], 6)
        self.assertEqual(summary["problems"], [])

    def test_outcomes_other_than_passed_and_not_executed_are_failures(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "x.trx"
            text = real_trx("My.Tests", "net9.0", passed=3).replace('outcome="Passed"', 'outcome="Timeout"', 1)
            path.write_text(text.replace('passed="3"', 'passed="2"'))
            summary = gate.parse_trx(path)
        self.assertEqual((summary["passed"], summary["failed"]), (2, 1))

    def test_a_trx_with_two_test_assemblies_fails(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(extra_assembly="Other.Tests"))
        self.fails(f, "runtime/linux/net8.0", "one TRX must hold one assembly")

    def test_an_incomplete_trx_whose_counters_exceed_its_results_fails(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(counters_total=50))
        self.fails(f, "runtime/linux/net8.0", "counters total=50")

    def test_a_trx_without_a_target_framework_does_not_inherit_the_lane_framework(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(code_base="/home/runner/{assembly}/{assembly}.dll"))
        self.fails(f, "runtime/linux/net8.0", "no target framework")

    def test_a_trx_for_another_framework_fails(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(framework="net9.0"))
        self.fails(f, "runtime/linux/net8.0", "targets net9.0")

    def test_windows_test_binaries_cannot_satisfy_a_linux_lane(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(code_base=WINDOWS_BIN))
        self.fails(f, "runtime/linux/net8.0", "windows path")

    def test_a_copied_trx_cannot_double_a_suite_count_to_reach_its_minimum(self):
        f = Fixture(self)
        raw = f.runtime_raw(scale=0)
        (raw / "copy").mkdir()
        shutil.copyfile(raw / "Core.Tests.trx", raw / "copy/Core.Tests.trx")
        f.collect("runtime/linux/net8.0", raw)
        self.fails(f, "runtime/linux/net8.0", "minimum")


class TrustBoundaryCounterexampleTests(Base):
    """The confirmed entry counterexample and its siblings; each has a passing control in the same fixture."""

    def test_altered_summary_cannot_hide_a_failing_retained_trx(self):
        f = Fixture(self)
        self.passes(f, "runtime/linux/net8.0")
        f.collect("runtime/linux/net8.0", f.runtime_raw(failed=1))

        def lie(result):
            for row in result["counts"]:
                row.update(passed=row["total"], failed=0)
            result.update(status="PASS", limitations=[])
            result["candidate"]["packages"] = {}
        f.mutate("runtime/linux/net8.0", lie)
        self.fails(f, "runtime/linux/net8.0", "retained", "no candidate package binding")
        self.assertEqual(f.evaluate()["overall"], "FAIL")

    def test_empty_artifact_list_fails(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r.update(artifacts=[]))
        self.fails(f, "runtime/linux/net8.0", "retains no raw evidence")

    def test_an_unlisted_retained_raw_file_fails(self):
        f = Fixture(self)
        (f.lane_dir("runtime/linux/net8.0") / "raw/hidden.trx").write_text(real_trx("Core.Tests", "net8.0", passed=1, failed=1))
        self.fails(f, "runtime/linux/net8.0", "hidden.trx")

    def test_a_retained_file_of_another_lane_kind_fails(self):
        f = Fixture(self)
        directory = f.lane_dir("runtime/linux/net8.0")
        (directory / "raw/coverage-gate.json").write_text("{}")
        f.mutate("runtime/linux/net8.0", lambda r: r["artifacts"].append({"path": "raw/coverage-gate.json", "sha256": sha(b"{}")}))
        self.fails(f, "runtime/linux/net8.0", "not raw evidence of a runtime-tests lane")

    def test_status_flipped_to_pass_cannot_hide_collector_problems(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(), producer_exit=3)
        f.mutate("runtime/linux/net8.0", lambda r: r.update(status="PASS"))
        self.fails(f, "runtime/linux/net8.0", "collector recorded problems")

    def test_the_producer_exit_is_a_structured_field_that_must_be_zero(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r["execution"].pop("producerExit"))
        self.fails(f, "runtime/linux/net8.0", "producing command exit is None")

    def test_evidence_from_a_dirty_source_tree_fails(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(), dirty=True)
        f.mutate("runtime/linux/net8.0", lambda r: r.update(status="PASS", limitations=[]))
        self.fails(f, "runtime/linux/net8.0", "dirty")
        f.mutate("runtime/linux/net8.0", lambda r: r["source"].pop("dirty"))
        self.fails(f, "runtime/linux/net8.0", "not recorded")


class ServiceTests(Base):
    def test_a_resolved_pinned_service_passes(self):
        f = Fixture(self)
        self.passes(f, "provider/redis/net10.0")
        service = f.read("provider/redis/net10.0")["services"][0]
        self.assertEqual((service["resolution"], service["image"]), ("docker-inspect", REDIS))
        self.assertTrue(any("--no-trunc" in call for call in f.docker.calls))

    def test_missing_service_entry_fails(self):
        f = Fixture(self)
        f.mutate("provider/redis/net10.0", lambda r: r.update(services=[]))
        self.fails(f, "provider/redis/net10.0", "not recorded")

    def test_unavailable_service_fails(self):
        f = Fixture(self)
        f.mutate("provider/redis/net10.0", lambda r: r["services"][0].update(available=False))
        self.fails(f, "provider/redis/net10.0", "not available")

    def test_a_claimed_image_is_not_accepted_for_a_required_service(self):
        f = Fixture(self)
        f.collect("provider/redis/net10.0", f.provider_raw(), services="claimed")
        self.assertEqual(f.read("provider/redis/net10.0")["services"][0]["resolution"], "claimed")
        self.fails(f, "provider/redis/net10.0", "claimed")

    def test_an_image_without_the_pinned_digest_fails(self):
        f = Fixture(self)
        f.docker.digests = ["redis@sha256:" + "9" * 64]
        f.produce("provider/redis/net10.0")
        self.fails(f, "provider/redis/net10.0", "not the pinned image")

    def test_a_container_created_from_an_unpinned_reference_fails(self):
        f = Fixture(self)
        f.docker.image = "redis:7.4-alpine"
        f.produce("provider/redis/net10.0")
        self.fails(f, "provider/redis/net10.0", "not the pinned image")

    def test_service_skips_in_a_provider_lane_fail(self):
        f = Fixture(self)
        f.collect("provider/redis/net10.0", f.provider_raw(skipped=1))
        self.fails(f, "provider/redis/net10.0", "skipped")


class PerKindTests(Base):
    def test_consumer_scope_must_match_the_lane_definition(self):
        f = Fixture(self)
        f.collect("consumers/linux/net10.0", f.consumer_raw(selection={"scope": "all", "selectedIds": ["A.Consumer", "Redis.Consumer"], "omittedIds": []}))
        self.fails(f, "consumers/linux/net10.0", "scope")

    def test_consumer_omissions_must_match_exactly(self):
        f = Fixture(self)
        f.mutate("consumers/linux/net10.0", lambda r: r["consumers"].update(omittedIds=["Redis.Consumer", "Other.Consumer"]))
        self.fails(f, "consumers/linux/net10.0")

    def test_consumer_lane_with_no_rows_fails(self):
        f = Fixture(self)
        f.collect("consumers/linux/net10.0", f.consumer_raw(rows=[]))
        self.fails(f, "consumers/linux/net10.0", "no consumer rows")

    def test_consumer_sdk_must_be_the_pinned_one(self):
        f = Fixture(self)
        rows = [{"framework": "net10.0", "sdkVersion": "10.0.100", "compilerVersion": "5.0.0", "consumer": "A.Consumer",
                 "variant": "legacy", "packageVersion": VERSION, "exit": 0}]
        f.collect("consumers/linux/net10.0", f.consumer_raw(rows=rows))
        self.fails(f, "consumers/linux/net10.0", "10.0.100")

    def test_a_selected_consumer_without_a_row_fails(self):
        f = Fixture(self)
        rows = [{"framework": "net10.0", "sdkVersion": BUILD_SDK, "compilerVersion": "5.0.0", "consumer": "Other.Consumer",
                 "variant": "legacy", "packageVersion": VERSION, "exit": 0}]
        f.collect("consumers/linux/net10.0", f.consumer_raw(rows=rows))
        self.fails(f, "consumers/linux/net10.0", "A.Consumer (legacy) produced no candidate row")

    def test_a_selection_that_is_not_the_inventory_fails(self):
        f = Fixture(self)
        f.collect("consumers/linux/net10.0", f.consumer_raw(selection={"scope": "partial", "selectedIds": ["A.Consumer"], "omittedIds": []}))
        self.fails(f, "consumers/linux/net10.0", "inventory consumers")

    def test_a_consumer_row_for_another_package_version_fails(self):
        f = Fixture(self)
        rows = [{"framework": "net10.0", "sdkVersion": BUILD_SDK, "compilerVersion": "5.0.0", "consumer": "A.Consumer",
                 "variant": "legacy", "packageVersion": "2.1.4-other.1", "exit": 0}]
        f.collect("consumers/linux/net10.0", f.consumer_raw(rows=rows))
        self.fails(f, "consumers/linux/net10.0", "package version")

    def test_native_lane_requires_an_executed_binary(self):
        f = Fixture(self)
        f.mutate("native/linux-x64/net10.0", lambda r: r["native"].update(executed=False))
        self.fails(f, "native/linux-x64/net10.0")

    def test_native_cases_come_from_the_executed_output_not_the_receipt(self):
        f = Fixture(self)
        f.collect("native/linux-x64/net10.0", f.native_raw(omit_case=CASES[1]))
        self.fails(f, "native/linux-x64/net10.0", CASES[1])

    def test_a_second_combined_native_receipt_fails(self):
        f = Fixture(self)
        f.collect("native/linux-x64/net10.0", f.native_raw(second_combined=True))
        self.fails(f, "native/linux-x64/net10.0", "exactly one combined native receipt")

    def test_a_native_receipt_without_a_candidate_version_fails(self):
        f = Fixture(self)
        raw = f.native_raw()
        receipt = raw / "native/run/native-result.json"
        data = json.loads(receipt.read_text())
        data.pop("candidateVersion")
        receipt.write_text(json.dumps(data))
        f.collect("native/linux-x64/net10.0", raw)
        self.fails(f, "native/linux-x64/net10.0", "no candidate version")

    def test_a_native_receipt_bound_to_another_package_record_fails(self):
        f = Fixture(self)
        f.collect("native/linux-x64/net10.0", f.native_raw(combined_overrides={"packageRecordSha256": "0" * 64}))
        self.fails(f, "native/linux-x64/net10.0", "different package record")

    def test_a_native_executable_of_the_wrong_format_or_host_fails(self):
        f = Fixture(self)
        f.collect("native/linux-x64/net10.0", f.native_raw(combined_overrides={"format": "macho-arm64", "hostOs": "osx", "hostArch": "arm64"}))
        self.fails(f, "native/linux-x64/net10.0", "macho-arm64", "osx/arm64")

    def test_a_mapping_only_receipt_without_executed_output_fails(self):
        f = Fixture(self)
        f.collect("native/linux-x64/net10.0", f.native_raw(mapping_log=False))
        self.fails(f, "native/linux-x64/net10.0", "mapping-only")

    def test_a_mapping_only_row_bound_to_another_package_fails(self):
        f = Fixture(self)
        f.collect("native/linux-x64/net10.0", f.native_raw(mapping_package={"file": f"/feed/Meridian.Mapping.{VERSION}.nupkg", "sha256": "0" * 64}))
        self.fails(f, "native/linux-x64/net10.0", "mapping-only native row restored")

    def test_property_counts_must_equal_policy(self):
        f = Fixture(self)
        f.collect("properties/net10.0", f.property_raw(log=" Ok, passed 100 tests.\n Ok, passed 50 tests.\n"))
        self.fails(f, "properties/net10.0", "property case counts")

    def test_a_falsified_property_fails_even_when_the_counts_match(self):
        f = Fixture(self)
        f.collect("properties/net10.0", f.property_raw(log=" Ok, passed 10000 tests.\n Ok, passed 500 tests.\n Falsifiable, after 3 tests\n"))
        self.fails(f, "properties/net10.0", "falsified")

    def test_a_failed_property_test_in_the_trx_fails(self):
        f = Fixture(self)
        f.collect("properties/net10.0", f.property_raw(failed=1))
        self.fails(f, "properties/net10.0", "failed test")

    def test_the_http_smoke_must_succeed_with_validated_ownership(self):
        f = Fixture(self)
        self.passes(f, "http/net10.0")
        for broken in ({"exit": 124, "timeout": True}, {"ownershipValidated": False}, {"drainsObserved": False}):
            with self.subTest(broken=broken):
                f.collect("http/net10.0", f.http_raw(http=broken))
                self.fails(f, "http/net10.0", "HTTP smoke")

    def test_the_http_smoke_needs_its_ownership_receipt(self):
        f = Fixture(self)
        f.collect("http/net10.0", f.http_raw(ownership=False))
        self.fails(f, "http/net10.0", "http-ownership.json")


class CoverageAndWarningTests(Base):
    def test_a_coverage_receipt_cannot_override_retained_measurements(self):
        f = Fixture(self)
        low = dict(f.coverage_files, **{"src/Lib/Hot.cs": [(n, 1 if n < 5 else 0) for n in range(1, 11)]})
        f.collect("coverage/net10.0", f.coverage_raw(hits=low, receipt={"status": "PASS", "ratchetRegressions": [], "hotPathFailures": []}))
        self.fails(f, "coverage/net10.0", "Hot.cs", "receipt status PASS differs")

    def test_a_coverage_receipt_for_another_baseline_fails(self):
        f = Fixture(self)
        f.collect("coverage/net10.0", f.coverage_raw(receipt={"baselineSha256": "0" * 64}))
        self.fails(f, "coverage/net10.0", "baseline")

    def test_a_coverage_lane_without_cobertura_fails(self):
        f = Fixture(self)
        raw = f.coverage_raw()
        shutil.rmtree(raw / "cov")
        f.collect("coverage/net10.0", raw)
        self.fails(f, "coverage/net10.0", "no Cobertura documents")

    def test_a_weakened_baseline_in_the_evidence_cannot_change_the_verdict(self):
        f = Fixture(self)
        low = dict(f.coverage_files, **{"src/Lib/Hot.cs": [(n, 1 if n < 5 else 0) for n in range(1, 11)]})
        weak = f.root / "weak-baseline.json"
        weak.write_text(json.dumps({"schema": "meridian-coverage-baseline/1", "files": {}, "hotPaths": []}))
        f.collect("coverage/net10.0", f.coverage_raw(hits=low, receipt={"status": "PASS", "baselineSha256": sha(weak.read_bytes())}))
        self.fails(f, "coverage/net10.0", "Hot.cs")

    def test_a_warning_receipt_cannot_override_the_retained_build_log(self):
        f = Fixture(self)
        f.collect("warnings/net10.0", f.warning_raw(extra_warning=True))
        self.fails(f, "warnings/net10.0", "INCREASE CA1305", "receipt says PASS")

    def test_an_empty_warning_receipt_is_not_a_pass(self):
        f = Fixture(self)
        f.collect("warnings/net10.0", f.warning_raw(receipt={"failures": None}))
        self.fails(f, "warnings/net10.0", "no failures list")

    def test_an_incremental_build_log_cannot_prove_the_warning_ratchet(self):
        f = Fixture(self)
        f.collect("warnings/net10.0", f.warning_raw(incremental=True))
        self.fails(f, "warnings/net10.0", "--no-incremental")

    def test_a_failed_build_cannot_prove_the_warning_ratchet(self):
        f = Fixture(self)
        f.collect("warnings/net10.0", f.warning_raw(exit_code=1))
        self.fails(f, "warnings/net10.0", "recorded exit 1")

    def test_every_solution_project_must_be_built_not_only_projects_with_warnings(self):
        f = Fixture(self)
        f.collect("warnings/net10.0", f.warning_raw(skip_project="Lib.Tests"))
        self.fails(f, "warnings/net10.0", "solution project not built in this log: Lib.Tests")


class ToolchainTests(Base):
    def test_a_runtime_lane_must_build_with_the_pinned_sdk(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(toolchain={"sdk": "10.0.402", "sdks": ("8.0.421", "10.0.402")}))
        self.fails(f, "runtime/linux/net8.0", "did not provide SDK 10.0.300")

    def test_a_runtime_lane_must_offer_exactly_its_pinned_runtime_patch(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(toolchain={"runtimes": ("8.0.27", "8.0.32", "10.0.8")}))
        self.fails(f, "runtime/linux/net8.0", "exactly 8.0.27")

    def test_a_lane_without_a_toolchain_receipt_fails(self):
        f = Fixture(self)
        raw = f.provider_raw()
        (raw / "lane-toolchain.json").unlink()
        f.collect("provider/redis/net10.0", raw)
        self.fails(f, "provider/redis/net10.0", "lane-toolchain.json")

    def test_the_servicing_control_passes(self):
        f = Fixture(self)
        self.passes(f, "servicing/net10.0")

    def test_servicing_needs_exactly_the_current_runtime_patch(self):
        f = Fixture(self)
        f.collect("servicing/net10.0", f.servicing_raw(runtimes=("10.0.12", "10.0.13")))
        self.fails(f, "servicing/net10.0", "exactly 10.0.13")
        f.collect("servicing/net10.0", f.servicing_raw(runtimes=("10.0.12",)))
        self.fails(f, "servicing/net10.0", "exactly 10.0.13")

    def test_servicing_must_build_with_the_current_sdk(self):
        f = Fixture(self)
        f.collect("servicing/net10.0", f.servicing_raw(sdk="10.0.300"))
        self.fails(f, "servicing/net10.0", "SDK 10.0.402")


class HostBoundLaneTests(Base):
    """Lanes bound to an OS/architecture that is not the current host cannot be executed here."""

    def drop_native(self, f):
        f.path("native/linux-x64/net10.0").unlink()

    def test_strict_mode_never_treats_an_unavailable_lane_as_success(self):
        f = Fixture(self)
        self.drop_native(f)
        summary = f.evaluate(host={"os": "osx", "arch": "arm64"})
        self.assertEqual(lane_state(summary, "native/linux-x64/net10.0")["state"], "UNAVAILABLE")
        self.assertEqual(summary["overall"], "FAIL")
        self.assertEqual(summary["exitCode"], gate.EXIT_FAIL)

    def test_allow_pending_records_pending_with_a_distinct_exit_code(self):
        f = Fixture(self)
        self.drop_native(f)
        summary = f.evaluate(host={"os": "osx", "arch": "arm64"}, allow_pending=True)
        self.assertEqual(summary["overall"], "PENDING")
        self.assertEqual(summary["exitCode"], gate.EXIT_PENDING)
        self.assertNotIn(summary["exitCode"], (gate.EXIT_PASS, gate.EXIT_FAIL))
        self.assertEqual(summary["pending"], ["native/linux-x64/net10.0"])
        self.assertFalse(summary["releaseEligible"])
        self.assertIn("not release eligible", gate.render_markdown(summary))

    def test_allow_pending_never_hides_a_failure(self):
        f = Fixture(self)
        self.drop_native(f)
        f.mutate("properties/net10.0", lambda r: r.update(status="FAIL"))
        summary = f.evaluate(host={"os": "osx", "arch": "arm64"}, allow_pending=True)
        self.assertEqual(summary["overall"], "FAIL")
        self.assertEqual(summary["exitCode"], gate.EXIT_FAIL)

    def test_allow_pending_does_not_excuse_a_missing_lane_on_a_matching_host(self):
        f = Fixture(self)
        self.drop_native(f)
        summary = f.evaluate(host={"os": "linux", "arch": "x64"}, allow_pending=True)
        self.assertEqual(lane_state(summary, "native/linux-x64/net10.0")["state"], "MISSING")
        self.assertEqual(summary["exitCode"], gate.EXIT_FAIL)

    def test_allow_pending_does_not_excuse_a_missing_lane_without_a_host_binding(self):
        f = Fixture(self)
        f.path("servicing/net10.0").unlink()
        summary = f.evaluate(host={"os": "osx", "arch": "arm64"}, allow_pending=True)
        self.assertEqual(lane_state(summary, "servicing/net10.0")["state"], "MISSING")
        self.assertEqual(summary["exitCode"], gate.EXIT_FAIL)

    def test_architecture_mismatch_is_unavailable_not_missing(self):
        f = Fixture(self)
        self.drop_native(f)
        summary = f.evaluate(host={"os": "linux", "arch": "arm64"})
        self.assertEqual(lane_state(summary, "native/linux-x64/net10.0")["state"], "UNAVAILABLE")

    def test_aggregate_mode_has_no_host_and_requires_every_lane(self):
        f = Fixture(self)
        self.drop_native(f)
        summary = f.evaluate(host=None, allow_pending=True)
        self.assertEqual(lane_state(summary, "native/linux-x64/net10.0")["state"], "MISSING")
        self.assertEqual(summary["exitCode"], gate.EXIT_FAIL)

    def test_present_evidence_for_a_foreign_host_lane_is_still_validated(self):
        f = Fixture(self)
        f.mutate("native/linux-x64/net10.0", lambda r: r["native"].update(executed=False))
        summary = f.evaluate(host={"os": "osx", "arch": "arm64"}, allow_pending=True)
        self.assertEqual(lane_state(summary, "native/linux-x64/net10.0")["state"], "FAIL")

    def test_evidence_claiming_the_wrong_host_fails(self):
        f = Fixture(self)
        f.mutate("native/linux-x64/net10.0", lambda r: r["host"].update(os="osx", arch="arm64"))
        self.fails(f, "native/linux-x64/net10.0", "lane requires linux")

    def test_runtime_evidence_from_another_architecture_fails(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r["host"].update(arch="arm64"))
        self.fails(f, "runtime/linux/net8.0", "lane requires x64")

    def test_an_unknown_host_fails_even_for_a_lane_without_a_host_binding(self):
        f = Fixture(self)
        f.mutate("properties/net10.0", lambda r: r.update(host={"os": "plan9", "arch": "mips"}))
        self.fails(f, "properties/net10.0", "not a known operating system")


class EvidenceIntegrityTests(Base):
    def test_malformed_lane_result_fails(self):
        f = Fixture(self)
        f.path("properties/net10.0").write_text("{ not json")
        self.fails(f, "properties/net10.0", "not readable JSON")

    def test_lane_id_must_match_its_directory(self):
        f = Fixture(self)
        result = f.read("properties/net10.0")
        result["laneId"] = "runtime/linux/net8.0"
        f.path("properties/net10.0").write_text(json.dumps(result))
        summary = f.evaluate()
        self.assertEqual(lane_state(summary, "properties/net10.0")["state"], "FAIL")
        self.assertEqual(lane_state(summary, "runtime/linux/net8.0")["state"], "PASS")

    def test_wrong_schema_fails(self):
        f = Fixture(self)
        f.mutate("properties/net10.0", lambda r: r.update(schema="other/1"))
        self.fails(f, "properties/net10.0", "schema")

    def test_listed_artifact_must_exist_with_the_recorded_hash(self):
        f = Fixture(self)
        directory = f.path("properties/net10.0").parent
        (directory / "notes.log").write_text("log")
        f.mutate("properties/net10.0", lambda r: r["artifacts"].append({"path": "notes.log", "sha256": sha(b"log")}))
        self.passes(f, "properties/net10.0")
        (directory / "notes.log").write_text("changed")
        self.fails(f, "properties/net10.0", "hash differs")
        (directory / "notes.log").unlink()
        self.fails(f, "properties/net10.0", "missing")

    def test_a_changed_retained_raw_file_fails(self):
        f = Fixture(self)
        (f.lane_dir("properties/net10.0") / "raw/property-output.log").write_text(" Ok, passed 10000 tests.\n Ok, passed 500 tests.\n edited\n")
        self.fails(f, "properties/net10.0", "hash differs")

    def test_artifact_paths_cannot_escape_the_lane_directory(self):
        f = Fixture(self)
        (f.root / "outside.txt").write_text("x")
        f.mutate("properties/net10.0", lambda r: r["artifacts"].append({"path": "../../../outside.txt", "sha256": sha(b"x")}))
        self.fails(f, "properties/net10.0", "escapes")

    def test_a_symlinked_artifact_fails(self):
        f = Fixture(self)
        target = f.lane_dir("properties/net10.0") / "raw/property-output.log"
        moved = f.root / "moved.log"
        shutil.move(target, moved)
        os.symlink(moved, target)
        self.fails(f, "properties/net10.0", "symbolic link")

    def test_evidence_for_unknown_lanes_is_reported_but_does_not_satisfy_anything(self):
        f = Fixture(self)
        shutil.copytree(f.lane_dir("properties/net10.0"), f.evidence / "lanes" / "unknown__lane")
        extra = json.loads((f.evidence / "lanes/unknown__lane/lane-result.json").read_text())
        extra["laneId"] = "unknown/lane"
        (f.evidence / "lanes/unknown__lane/lane-result.json").write_text(json.dumps(extra))
        summary = f.evaluate()
        self.assertEqual(summary["overall"], "PASS")
        self.assertEqual(summary["unexpectedEvidence"], ["unknown/lane"])


class SummaryContentTests(Base):
    def test_summary_carries_counts_skips_hashes_and_limitations(self):
        f = Fixture(self)
        f.mutate("native/linux-x64/net10.0", lambda r: r["limitations"].append("linux-x64 executed under test fixture"))
        summary = f.evaluate()
        self.assertEqual(summary["schema"], "meridian-candidate-summary/2")
        self.assertEqual(summary["candidate"]["version"], VERSION)
        self.assertEqual(summary["candidate"]["packageManifestSha256"], f.manifest_sha)
        self.assertEqual(summary["candidate"]["packedSourceCommit"], COMMIT)
        self.assertEqual(summary["candidate"]["inventory"], f.INVENTORY)
        self.assertEqual({p["id"]: p["sha256"] for p in summary["candidate"]["packages"]}, f.packages)
        self.assertEqual(summary["totalsByFramework"]["net8.0"], {"total": 11, "passed": 11, "failed": 0, "skipped": 0})
        self.assertGreater(summary["totalsByFramework"]["net10.0"]["passed"], 0)
        self.assertEqual(summary["skips"], [])
        self.assertIn("linux-x64 executed under test fixture", summary["limitations"])
        self.assertEqual(lane_state(summary, "properties/net10.0")["check"], "properties (net10.0)")
        self.assertEqual(lane_state(summary, "runtime/linux/net8.0")["derivedCounts"][0]["suite"], "Core.Tests")

    def test_totals_come_from_the_retained_trx_not_the_recorded_summary(self):
        f = Fixture(self)
        f.mutate("runtime/linux/net8.0", lambda r: r["counts"][0].update(total=999, passed=999))
        self.assertEqual(f.evaluate()["totalsByFramework"]["net8.0"]["total"], 11)

    def test_proof_level_is_reported_per_lane(self):
        f = Fixture(self)
        f.mutate("properties/net10.0", lambda r: r["execution"].update(context="github-actions", runId="42"))
        summary = f.evaluate()
        self.assertEqual(lane_state(summary, "properties/net10.0")["proof"], "executed-ci")
        self.assertEqual(lane_state(summary, "runtime/win/net10.0")["proof"], "executed-local")
        f.path("properties/net10.0").unlink()
        self.assertEqual(lane_state(f.evaluate(), "properties/net10.0")["proof"], "none")

    def test_ci_context_requires_a_run_id(self):
        f = Fixture(self)
        f.mutate("properties/net10.0", lambda r: r["execution"].update(context="github-actions", runId=None))
        self.fails(f, "properties/net10.0", "run id")

    def test_markdown_lists_every_lane_and_the_verdict(self):
        f = Fixture(self)
        f.path("properties/net10.0").unlink()
        text = gate.render_markdown(f.evaluate())
        self.assertIn("FAIL", text)
        for lane in f.policy["lanes"]:
            self.assertIn(lane["check"], text)
        self.assertIn("MISSING", text)
        self.assertIn("release eligible: no", text)


class ScopedEvaluationTests(Base):
    """--only judges named lanes (a CI job proving its own lane) and is never a candidate verdict."""

    def test_a_scoped_pass_is_labelled_and_ignores_other_lanes(self):
        f = Fixture(self)
        f.path("properties/net10.0").unlink()
        f.path("native/linux-x64/net10.0").unlink()
        summary = f.evaluate(only=["runtime/linux/net8.0"])
        self.assertEqual(summary["overall"], "SCOPED-PASS")
        self.assertEqual(summary["exitCode"], gate.EXIT_PASS)
        self.assertEqual(summary["scope"], "lanes")
        self.assertIs(summary["wholeCandidate"], False)
        self.assertIs(summary["releaseEligible"], False)
        self.assertEqual([l["id"] for l in summary["lanes"]], ["runtime/linux/net8.0"])
        self.assertIn("NOT a candidate verdict", gate.render_markdown(summary))

    def test_a_scoped_failure_still_fails(self):
        f = Fixture(self)
        f.collect("runtime/linux/net8.0", f.runtime_raw(failed=1))
        self.assertEqual(f.evaluate(only=["runtime/linux/net8.0"])["exitCode"], gate.EXIT_FAIL)

    def test_a_scoped_missing_lane_fails(self):
        f = Fixture(self)
        f.path("properties/net10.0").unlink()
        self.assertEqual(f.evaluate(only=["properties/net10.0"])["exitCode"], gate.EXIT_FAIL)

    def test_an_unknown_or_empty_scope_is_a_configuration_error(self):
        f = Fixture(self, produce=False)
        for only in (["no/such/lane"], []):
            with self.subTest(only=only), self.assertRaises(gate.PolicyError):
                f.evaluate(only=only)

    def test_ci_lanes_selects_only_lanes_defined_in_ci(self):
        f = Fixture(self)
        for lane in f.policy["lanes"]:
            lane["ci"] = {"workflow": "ci.yml", "job": "x", "matrix": {}}
        f.policy["lanes"][-1]["ci"] = None
        f.path(f.policy["lanes"][-1]["id"]).unlink()
        summary = f.evaluate(only=gate.ci_lane_ids(f.policy))
        self.assertNotIn(f.policy["lanes"][-1]["id"], [l["id"] for l in summary["lanes"]])
        self.assertEqual(summary["overall"], "SCOPED-PASS")
        self.assertEqual(f.evaluate()["exitCode"], gate.EXIT_FAIL)

    def test_the_full_evaluation_is_labelled_as_the_candidate(self):
        summary = Fixture(self).evaluate()
        self.assertEqual(summary["scope"], "candidate")
        self.assertEqual(summary["overall"], "PASS")


class PolicyValidationTests(unittest.TestCase):
    def invalid(self, mutate):
        policy = mini_policy()
        mutate(policy)
        with self.assertRaises(gate.PolicyError):
            gate.validate_policy(policy)

    def lane(self, policy, lane_id):
        return next(l for l in policy["lanes"] if l["id"] == lane_id)

    def test_the_mini_policy_is_valid(self):
        gate.validate_policy(mini_policy())

    def test_duplicate_lane_ids_are_a_configuration_error(self):
        self.invalid(lambda p: p["lanes"].append(copy.deepcopy(p["lanes"][0])))

    def test_lane_ids_that_share_an_evidence_directory_are_a_configuration_error(self):
        def collide(p):
            lane = copy.deepcopy(p["lanes"][0])
            lane.update(id="runtime__linux/net8.0", check="other check")
            p["lanes"].append(lane)
        self.invalid(collide)

    def test_unknown_kind_is_a_configuration_error(self):
        self.invalid(lambda p: p["lanes"][0].update(kind="mystery"))

    def test_missing_count_profile_is_a_configuration_error(self):
        self.invalid(lambda p: p["lanes"][0].update(countsProfile="nope"))

    def test_duplicate_check_names_are_a_configuration_error(self):
        self.invalid(lambda p: p["lanes"][1].update(check=p["lanes"][0]["check"]))

    def test_unknown_service_is_a_configuration_error(self):
        self.invalid(lambda p: self.lane(p, "provider/redis/net10.0").update(services=["mongo"]))

    def test_unpinned_provider_image_is_a_configuration_error(self):
        self.invalid(lambda p: p["providers"]["redis"].update(image="redis:7.4-alpine"))

    def test_runtime_and_consumer_lanes_must_bind_an_architecture(self):
        self.invalid(lambda p: self.lane(p, "runtime/linux/net8.0")["host"].pop("arch"))
        self.invalid(lambda p: self.lane(p, "consumers/linux/net10.0")["host"].pop("arch"))
        self.invalid(lambda p: self.lane(p, "runtime/linux/net8.0")["host"].update(arch="mips"))

    def test_a_native_lane_host_must_match_its_runtime_identifier(self):
        self.invalid(lambda p: self.lane(p, "native/linux-x64/net10.0").update(host={"os": "osx", "arch": "arm64"}))

    def test_a_consumer_lane_sdk_must_be_the_pinned_compatibility_sdk(self):
        self.invalid(lambda p: self.lane(p, "consumers/linux/net10.0").update(sdk="10.0.402"))

    def test_servicing_lanes_must_match_the_current_servicing_rows(self):
        self.invalid(lambda p: self.lane(p, "servicing/net10.0").update(runtime="10.0.12"))
        self.invalid(lambda p: self.lane(p, "servicing/net10.0").update(sdk="10.0.300"))
        self.invalid(lambda p: self.lane(p, "servicing/net10.0").pop("toolchainRootEnvironment"))

    def test_servicing_rows_must_be_exact_family_patches_distinct_from_pinned(self):
        self.invalid(lambda p: p["toolchains"]["currentServicing"]["lanes"][0].update(runtime="9.0.21"))
        self.invalid(lambda p: p["toolchains"]["currentServicing"]["lanes"][0].update(sdk="10.0"))
        self.invalid(lambda p: p["toolchains"]["currentServicing"]["lanes"][0].update(sdk=BUILD_SDK))

    def test_vacuous_requirements_are_configuration_errors(self):
        self.invalid(lambda p: p.update(nativeCases=[]))
        self.invalid(lambda p: p["samples"].update({"net8.0": 0}))
        self.invalid(lambda p: p["sampleNames"].update({"net8.0": ["Alpha", "Alpha"]}))
        self.invalid(lambda p: p["countProfiles"]["redis"]["net10.0"][0].update(minimum=0))
        self.invalid(lambda p: p["lanes"].append({"id": "generic/x", "kind": "generic", "check": "generic"}))

    def test_coverage_and_warning_lanes_need_their_baselines(self):
        self.invalid(lambda p: p.pop("coverage"))
        self.invalid(lambda p: p["warnings"].pop("solution"))

    def test_http_smoke_is_only_for_provider_lanes(self):
        self.invalid(lambda p: self.lane(p, "properties/net10.0").update(httpSmoke=True))


class CommandLineTests(unittest.TestCase):
    def run_cli(self, f, *extra):
        out = f.root / "summary.json"
        policy = f.root / "eng/required-checks.json"
        policy.write_text(json.dumps(f.policy))
        process = subprocess.run(
            [sys.executable, "-B", str(ROOT / "eng/candidate_gate.py"), "evaluate", "--policy", str(policy), "--feed", str(f.feed),
             "--evidence", str(f.evidence), "--candidate-version", VERSION, "--output", str(out), *extra],
            capture_output=True, text=True)
        return process, out

    def test_exit_codes_and_written_summary_agree_with_the_policy_repository_as_default(self):
        f = Fixture(self)
        process, out = self.run_cli(f, "--aggregate")
        self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
        self.assertEqual(json.loads(out.read_text())["exitCode"], 0)
        self.assertTrue(out.with_suffix(".md").exists())
        self.assertIn("release eligible: True", process.stdout)
        f.path("properties/net10.0").unlink()
        process, out = self.run_cli(f, "--aggregate")
        self.assertEqual(process.returncode, 1)
        self.assertEqual(json.loads(out.read_text())["overall"], "FAIL")

    def test_pending_exit_code_through_the_command_line(self):
        f = Fixture(self)
        f.path("native/linux-x64/net10.0").unlink()
        process, _ = self.run_cli(f, "--host-os", "osx", "--host-arch", "arm64", "--allow-pending")
        self.assertEqual(process.returncode, 3, process.stdout + process.stderr)
        process, _ = self.run_cli(f, "--host-os", "osx", "--host-arch", "arm64")
        self.assertEqual(process.returncode, 1)

    def test_aggregate_rejects_allow_pending(self):
        f = Fixture(self, produce=False)
        process, _ = self.run_cli(f, "--aggregate", "--allow-pending")
        self.assertEqual(process.returncode, 2)

    def test_the_manifest_anchor_through_the_command_line(self):
        f = Fixture(self)
        self.assertEqual(self.run_cli(f, "--aggregate", "--package-manifest-sha256", f.manifest_sha)[0].returncode, 0)
        self.assertEqual(self.run_cli(f, "--aggregate", "--package-manifest-sha256", "0" * 64)[0].returncode, 1)

    def test_invalid_policy_exits_two(self):
        f = Fixture(self, produce=False)
        f.policy["lanes"].append(copy.deepcopy(f.policy["lanes"][0]))
        process, _ = self.run_cli(f, "--aggregate")
        self.assertEqual(process.returncode, 2, process.stdout + process.stderr)

    def test_host_options_are_mutually_exclusive_with_aggregate(self):
        f = Fixture(self, produce=False)
        process, _ = self.run_cli(f, "--aggregate", "--host-os", "linux", "--host-arch", "x64")
        self.assertEqual(process.returncode, 2)

    def test_a_host_or_aggregate_choice_is_required(self):
        f = Fixture(self, produce=False)
        process, _ = self.run_cli(f)
        self.assertEqual(process.returncode, 2)


if __name__ == "__main__":
    unittest.main()

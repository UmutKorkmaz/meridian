"""Behavioral checks for the packaged Native AOT runner (eng/run-aot-smoke.ps1 -Combined).

These execute the real PowerShell module/runner with inventory- and output-shaped values. Native publication and
execution themselves are exercised by the runner on a matching real host, not by this file.
"""

import atexit
import json
import os
import platform
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
# Scratch fixtures live in a private temporary directory, never in the repository (artifacts/ may not exist on a clean checkout).
SCRATCH = Path(tempfile.mkdtemp(prefix="meridian-native-aot-tests-"))
atexit.register(shutil.rmtree, SCRATCH, ignore_errors=True)
MODULE = ROOT / "eng/native-aot.psm1"
RUNNER = ROOT / "eng/run-aot-smoke.ps1"
CONSUMER = ROOT / "tests/consumers/Meridian.NativeAotConsumer"
SAMPLE = ROOT / "samples/Meridian.NativeAot"
SHARED_CASES = [
    "typed-object-query",
    "void-forms",
    "standard-pipeline",
    "standard-composition",
    "scoped-construction",
    "notification-composition",
    "stream-lifetime",
    "generated-mapping",
    "strict-failure",
]
NATIVE_CASES = [
    "native-runtime",
    "native-nonstrict-unknown",
    "native-scanning-unsupported",
]


def native_output(ids=None, os_name="osx", arch="arm64", family="10", summary=None, extra=""):
    ids = SHARED_CASES + NATIVE_CASES if ids is None else ids
    lines = [f"HOST runtime={family}.0.8 framework=.NET {family}.0.8"]
    for case in ids:
        lines.append(f"CASE {case} PASS")
    lines.append(
        f"NATIVE os={os_name} arch={arch} dynamicCodeSupported=false dynamicCodeCompiled=false runtime=.NET {family}.0.8"
    )
    lines.append(
        summary
        if summary is not None
        else f"SUMMARY schema=meridian-native-aot-cases/1 mode=native count={len(ids)} expected={len(SHARED_CASES + NATIVE_CASES)} ids={','.join(ids)}"
    )
    lines.append(f"COMMON native-aot-cases={len(ids)}")
    return "\n".join(lines) + "\n" + extra


class NativeAotRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pwsh = os.environ.get("PWSH") or shutil.which("pwsh")
        if not cls.pwsh or not (shutil.which(cls.pwsh) or Path(cls.pwsh).is_file()):
            raise RuntimeError("Required PowerShell: set PWSH or install pwsh; these tests cannot be skipped")

    def ps(self, script, *, cwd=ROOT):
        prelude = f"$ErrorActionPreference='Stop'; Import-Module '{MODULE}' -Force -DisableNameChecking; "
        return subprocess.run(
            [self.pwsh, "-NoProfile", "-NonInteractive", "-Command", prelude + script],
            cwd=cwd,
            capture_output=True,
            text=True,
        )

    def json(self, script):
        result = self.ps(script)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    # --- case-set proof -------------------------------------------------------------------------------------

    def check_output(self, text, rid="osx-arm64", framework="net10.0", exit_code=0):
        path = SCRATCH / "test-output.txt"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        return self.ps(
            f"$o = Get-Content -Raw '{path}'; "
            f"ConvertTo-Json -InputObject (Test-NativeAotCaseOutput $o (Get-NativeAotExpectedCases) {exit_code} '{rid}' '{framework}') -Compress"
        )

    def test_expected_cases_match_the_consumer_program(self):
        data = self.json("ConvertTo-Json -InputObject (Get-NativeAotExpectedCases) -Compress")
        self.assertEqual(data, SHARED_CASES + NATIVE_CASES)
        source = (CONSUMER / "Program.cs").read_text()
        shared = re.search(r"var sharedCases = new\[\]\s*\{(.*?)\};", source, re.S).group(1)
        native = re.search(r"var nativeCases = new\[\] \{(.*?)\};", source, re.S).group(1)
        self.assertEqual(re.findall(r'"([^"]+)"', shared), SHARED_CASES)
        self.assertEqual(re.findall(r'"([^"]+)"', native), NATIVE_CASES)

    def test_complete_native_output_is_accepted(self):
        result = self.check_output(native_output())
        self.assertEqual(result.returncode, 0, result.stderr)
        record = json.loads(result.stdout)
        self.assertEqual(record["caseIds"], SHARED_CASES + NATIVE_CASES)
        self.assertEqual(record["os"], "osx")
        self.assertEqual(record["arch"], "arm64")

    def test_missing_duplicate_unexpected_and_failed_cases_are_rejected(self):
        everything = SHARED_CASES + NATIVE_CASES
        scenarios = {
            "missing": (native_output(everything[:-1]), "missing"),
            "duplicate": (native_output(everything + [everything[0]]), "duplicate"),
            "unexpected": (native_output(everything + ["extra-case"]), "unexpected"),
            "failed": (native_output(everything, extra="CASE strict-failure FAIL InvalidOperationException: boom\n"), "FAIL"),
        }
        for name, (text, diagnostic) in scenarios.items():
            with self.subTest(name):
                result = self.check_output(text)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(diagnostic, result.stderr)

    def test_nonzero_exit_and_inconsistent_summary_are_rejected(self):
        self.assertNotEqual(self.check_output(native_output(), exit_code=1).returncode, 0)
        bad_summary = native_output(summary="SUMMARY schema=meridian-native-aot-cases/1 mode=native count=3 expected=12 ids=a,b,c")
        self.assertIn("SUMMARY", self.check_output(bad_summary).stderr)
        self.assertNotEqual(self.check_output(native_output(summary="SUMMARY schema=other/1 mode=native count=12 expected=12 ids=x")).returncode, 0)

    def test_runtime_identity_and_native_markers_are_enforced(self):
        self.assertIn("runtime family", self.check_output(native_output(family="9")).stderr)
        self.assertIn("NATIVE", self.check_output(native_output(os_name="linux")).stderr)
        self.assertIn("NATIVE", self.check_output(native_output(arch="x64")).stderr)
        jit = native_output().replace("dynamicCodeSupported=false", "dynamicCodeSupported=true")
        self.assertIn("NATIVE", self.check_output(jit).stderr)
        managed = "\n".join(l for l in native_output().splitlines() if not l.startswith("NATIVE "))
        self.assertIn("NATIVE", self.check_output(managed).stderr)

    # --- matching real host ---------------------------------------------------------------------------------

    def host(self, rid, os_name, arch, translated="$false"):
        return self.json(
            f"ConvertTo-Json -InputObject (Test-NativeAotHost '{rid}' -Os '{os_name}' -Arch '{arch}' -Translated {translated}) -Compress"
        )

    def test_only_a_matching_native_host_is_accepted(self):
        self.assertTrue(self.host("osx-arm64", "osx", "arm64")["ok"])
        self.assertTrue(self.host("linux-x64", "linux", "x64")["ok"])
        for rid, os_name, arch in [
            ("linux-x64", "linux", "arm64"),  # an arm64 Docker daemon is not a linux-x64 runner
            ("linux-x64", "osx", "arm64"),
            ("osx-arm64", "osx", "x64"),
            ("osx-arm64", "linux", "arm64"),
        ]:
            with self.subTest(rid=rid, os=os_name, arch=arch):
                result = self.host(rid, os_name, arch)
                self.assertFalse(result["ok"])
                self.assertIn("matching", result["reason"])

    def test_emulated_process_is_not_a_native_host(self):
        result = self.host("osx-arm64", "osx", "arm64", "$true")
        self.assertFalse(result["ok"])
        self.assertIn("translated", result["reason"])

    # --- Linux emulation probes (fix round 1) ---------------------------------------------------------------

    CLEAN_CPUINFO = "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R) Platinum 8272CL CPU @ 2.60GHz\nflags\t\t: fpu vme sse2 avx2\n"
    ROSETTA_CPUINFO = "processor\t: 0\nvendor_id\t: VirtualApple\nmodel name\t: VirtualApple @ 2.50GHz\nflags\t\t: fpu sse2\n"
    HOST_ARM_CPUINFO = "processor\t: 0\nBogoMIPS\t: 48.00\nFeatures\t: fp asimd evtstrm\nCPU implementer\t: 0x00\n"
    CLEAN_MAPS = "00400000-00500000 r-xp 00000000 08:01 1 /home/runner/work/Meridian/native/consumer\n7ffd0000-7ffd1000 rw-p 00000000 00:00 0 [stack]\n"

    def fake_proc(self, name, *, cpuinfo=None, maps=None, binfmt=None):
        root = SCRATCH / "fake-proc" / name
        if root.exists():
            shutil.rmtree(root)
        (root / "self").mkdir(parents=True)
        (root / "sys/fs/binfmt_misc").mkdir(parents=True)
        (root / "cpuinfo").write_text(self.CLEAN_CPUINFO if cpuinfo is None else cpuinfo)
        (root / "self/maps").write_text(self.CLEAN_MAPS if maps is None else maps)
        (root / "sys/fs/binfmt_misc/status").write_text("enabled\n")
        for entry, body in (binfmt or {}).items():
            (root / "sys/fs/binfmt_misc" / entry).write_text(body)
        return root

    def linux_host(self, proc):
        return self.json(
            f"ConvertTo-Json -InputObject (Test-NativeAotHost 'linux-x64' -Os 'linux' -Arch 'x64' -ProcRoot '{proc}') -Compress"
        )

    def test_native_linux_x64_proc_tree_is_accepted(self):
        result = self.linux_host(self.fake_proc("native"))
        self.assertTrue(result["ok"], result)
        self.assertFalse(result["translated"])

    def test_linux_emulation_is_detected_so_it_cannot_record_a_native_pass(self):
        qemu_binfmt = "enabled\ninterpreter /usr/bin/qemu-x86_64-static\nflags: OCF\noffset 0\nmagic 7f454c46\n"
        scenarios = {
            # docker --platform linux/amd64 on Apple silicon through qemu-user (guest sees an x86 uname)
            "qemu-maps": dict(maps=self.CLEAN_MAPS + "7f0000000000-7f0000100000 r-xp 0 08:01 9 /usr/bin/qemu-x86_64-static\n"),
            # Rosetta for Linux virtual machines report a VirtualApple CPU
            "rosetta-cpuinfo": dict(cpuinfo=self.ROSETTA_CPUINFO),
            "rosetta-maps": dict(maps=self.CLEAN_MAPS + "7f0000000000-7f0000100000 r-xp 0 00:2a 9 /run/rosetta/rosetta\n"),
            # an aarch64 /proc/cpuinfo seen from an x64-looking process
            "foreign-cpuinfo": dict(cpuinfo=self.HOST_ARM_CPUINFO),
            "binfmt-qemu": dict(binfmt={"qemu-x86_64": qemu_binfmt}),
            "binfmt-rosetta": dict(binfmt={"rosetta": "enabled\ninterpreter /run/rosetta/rosetta\nflags: F\n"}),
        }
        for name, parts in scenarios.items():
            with self.subTest(name):
                result = self.linux_host(self.fake_proc(name, **parts))
                self.assertFalse(result["ok"], result)
                self.assertTrue(result["translated"])
                self.assertIn("emulat", result["reason"])
                self.assertTrue(result["signals"])

    def test_disabled_or_foreign_arch_binfmt_registrations_are_not_emulation(self):
        disabled = "disabled\ninterpreter /usr/bin/qemu-x86_64-static\nflags: OCF\n"
        other_arch = "enabled\ninterpreter /usr/bin/qemu-aarch64-static\nflags: OCF\n"
        result = self.linux_host(self.fake_proc("benign", binfmt={"qemu-x86_64": disabled, "qemu-aarch64": other_arch}))
        self.assertTrue(result["ok"], result)

    def test_native_binary_format_is_read_from_headers(self):
        macho_arm64 = bytes.fromhex("cffaedfe0c000001") + bytes(24)
        macho_x64 = bytes.fromhex("cffaedfe07000001") + bytes(24)
        # 16 ident bytes, e_type=ET_EXEC, then e_machine (x86-64 = 0x3E, aarch64 = 0xB7).
        elf_x64 = bytes.fromhex("7f454c460201010000000000000000000200" + "3e00") + bytes(16)
        elf_arm64 = bytes.fromhex("7f454c460201010000000000000000000200" + "b700") + bytes(16)
        for name, data, expected in [
            ("a", macho_arm64, "macho-arm64"),
            ("b", macho_x64, "macho-x64"),
            ("c", elf_x64, "elf-x64"),
            ("d", elf_arm64, "elf-arm64"),
            ("e", b"not a binary at all, just text.........", "unknown"),
        ]:
            path = SCRATCH / f"format-{name}.bin"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            with self.subTest(expected):
                result = self.ps(f"Get-NativeBinaryFormat '{path}'")
                self.assertEqual(result.stdout.strip(), expected, result.stderr)

    # --- warning disposition --------------------------------------------------------------------------------

    def warnings(self, text, rid, framework):
        path = SCRATCH / "test-warnings.txt"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        return self.json(
            f"$w = @(Find-NativeAotWarnings (Get-Content -Raw '{path}') '{rid}' '{framework}'); ConvertTo-Json -InputObject $w -Compress"
        )

    def test_warning_scan_records_every_warning_and_allows_only_the_named_apple_linker_notice(self):
        ld = "  ld: warning: -ld_classic is deprecated and will be removed in a future release\n"
        trim = "Meridian.Mediator/Mediator.cs(271): Trim analysis warning IL2067: boom\n"
        summary = "    0 Warning(s)\n    0 Error(s)\n"
        data = self.warnings(ld + trim + summary, "osx-arm64", "net9.0")
        self.assertEqual([w["disposition"] for w in data], ["toolchain-known", "unreviewed"])
        self.assertEqual(self.warnings(summary, "osx-arm64", "net9.0"), [])
        self.assertEqual(self.warnings(ld, "osx-arm64", "net10.0")[0]["disposition"], "unreviewed")
        self.assertEqual(self.warnings(ld, "linux-x64", "net8.0")[0]["disposition"], "unreviewed")

    # --- fixture, manifest and runner contract --------------------------------------------------------------

    def test_sample_and_packaged_consumer_share_identical_behavior_files(self):
        for name in ("Program.cs", "Registry.cs", "Mapping.cs"):
            with self.subTest(name):
                self.assertEqual((SAMPLE / name).read_bytes(), (CONSUMER / name).read_bytes())
        self.assertTrue((SAMPLE / "README.md").is_file())
        self.assertTrue((SAMPLE / "Meridian.NativeAot.csproj").is_file())
        result = self.ps(f"Assert-NativeAotParity '{ROOT}'")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_manifest_lists_the_exact_package_only_fixture(self):
        manifest = json.loads((ROOT / "eng/packages.json").read_text())
        entries = [c for c in manifest["consumers"] if c["id"] == "Meridian.NativeAotConsumer"]
        self.assertEqual(len(entries), 1)
        entry = entries[0]
        base = "tests/consumers/Meridian.NativeAotConsumer/"
        self.assertEqual(
            entry["sourceFiles"],
            [base + n for n in ("Meridian.NativeAotConsumer.csproj", "Program.cs", "Registry.cs", "Mapping.cs")],
        )
        self.assertEqual(entry["packageIds"], ["Meridian.Mapping", "Meridian.Mediator", "Meridian.Mediator.Generators"])
        self.assertEqual(entry["variants"], ["legacy"])
        self.assertFalse(entry["baselineComparison"])
        project = (CONSUMER / "Meridian.NativeAotConsumer.csproj").read_text()
        self.assertNotIn("ProjectReference", project)
        for package in entry["packageIds"]:
            self.assertRegex(project, rf'Include="{re.escape(package)}" Version="\[\$\(MeridianPackageVersion\)\]"')
        self.assertIn("NativeAotSmoke", project)
        # No optional provider/exporter dependency joins the native baseline.
        self.assertNotRegex(project, r"(?i)FluentValidation|EntityFramework|Redis|AspNetCore|OpenTelemetry")

    def run_runner(self, *args):
        return subprocess.run(
            [self.pwsh, "-NoProfile", "-NonInteractive", "-File", str(RUNNER), *args],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )

    COMMON = ["-Feed", "artifacts/D4/does-not-exist", "-CandidateVersion", "0.0.1-d4.test", "-Framework", "net10.0", "-SdkVersion", "10.0.300"]

    def test_modes_are_mutually_exclusive_and_never_defaulted(self):
        neither = self.run_runner(*self.COMMON, "-Rid", "osx-arm64")
        self.assertNotEqual(neither.returncode, 0)
        self.assertIn("parameter set", (neither.stderr + neither.stdout).lower())
        both = self.run_runner("-MappingOnly", "-Combined", *self.COMMON, "-Rid", "osx-arm64")
        self.assertNotEqual(both.returncode, 0)

    def test_combined_rejects_rids_without_a_native_proof_path(self):
        result = self.run_runner("-Combined", *self.COMMON, "-Rid", "win-x64")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("linux-x64 and osx-arm64", result.stderr + result.stdout)

    def test_combined_refuses_a_non_matching_host_before_touching_the_feed(self):
        machine = platform.machine().lower()
        system = platform.system()
        matching = "osx-arm64" if system == "Darwin" and machine == "arm64" else "linux-x64" if system == "Linux" and machine in ("x86_64", "amd64") else None
        mismatching = "linux-x64" if matching == "osx-arm64" else "osx-arm64"
        result = self.run_runner("-Combined", *self.COMMON, "-Rid", mismatching)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("matching native", result.stderr + result.stdout)
        self.assertNotIn("Candidate", result.stderr)

    def test_runner_exposes_rid_alias_and_both_parameter_sets(self):
        tokens = errors = None
        script = (
            "$t=$null;$e=$null;"
            f"$ast=[Management.Automation.Language.Parser]::ParseFile('{RUNNER}',[ref]$t,[ref]$e);"
            "if($e.Count){throw 'parse'};"
            "$p=$ast.ParamBlock.Parameters|ForEach-Object{[pscustomobject]@{name=$_.Name.VariablePath.UserPath;"
            "attrs=@($_.Attributes|ForEach-Object{$_.Extent.Text})}};"
            "ConvertTo-Json -InputObject $p -Compress -Depth 6"
        )
        result = subprocess.run([self.pwsh, "-NoProfile", "-Command", script], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        params = {p["name"]: " ".join(p["attrs"]) for p in json.loads(result.stdout)}
        self.assertIn("ParameterSetName='MappingOnly'", params["MappingOnly"])
        self.assertIn("ParameterSetName='Combined'", params["Combined"])
        self.assertIn("Alias('Rid')", params["RuntimeIdentifier"])
        self.assertIn("Mandatory", params["CandidateVersion"])
        self.assertIn("Mandatory", params["Framework"])
        self.assertIn("Mandatory", params["SdkVersion"])
        self.assertIn("Manifest", params)


class NativeAotWorkflowTests(unittest.TestCase):
    """The CI lanes must publish and execute on matching runners from one immutable candidate feed."""

    @classmethod
    def setUpClass(cls):
        import yaml  # required test infrastructure (PyYAML); an import failure is a failure, not a skip

        cls.workflow = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        cls.manifest = json.loads((ROOT / "eng/packages.json").read_text())

    def test_native_lanes_use_one_packed_feed_and_matching_runners(self):
        jobs = self.workflow["jobs"]
        pack, lane = jobs["candidate-pack"], jobs["native-aot"]
        self.assertEqual(lane["needs"], "candidate-pack")
        matrix = lane["strategy"]["matrix"]
        self.assertEqual(matrix["rid"], ["linux-x64", "osx-arm64"])
        self.assertEqual(
            matrix["host"],
            [{"framework": h["framework"], "sdk": h["sdkVersion"]} for h in self.manifest["compilerHosts"]],
        )
        runners = {i["rid"]: i["runner"] for i in matrix["include"]}
        self.assertTrue(runners["linux-x64"].startswith("ubuntu"))
        self.assertTrue(runners["osx-arm64"].startswith("macos"))
        pack_text = json.dumps(pack)
        self.assertEqual(pack_text.count("pack-packages.ps1"), 1)
        self.assertIn("candidate-feed", pack_text)
        run = "\n".join(str(step.get("run", "")) for step in lane["steps"])
        # The version is derived once by the pack job and published as a job output (see the re-run test below).
        self.assertIn("-CandidateVersion '${{ needs.candidate-pack.outputs.candidate }}'", run)
        self.assertIn("-Lane 'native-aot/${{ matrix.rid }}/${{ matrix.host.framework }}'", run)
        self.assertIn("verify-candidate.ps1 -Mode Run", run)
        self.assertNotIn("pack-packages.ps1", run)  # lanes inspect, never repack
        downloads = [s for s in lane["steps"] if str(s.get("uses", "")).startswith("actions/download-artifact")]
        self.assertEqual([d["with"]["name"] for d in downloads], ["${{ needs.candidate-pack.outputs.feedArtifact }}"])
        # The lane runner publishes and executes both native consumers for the lane's RID.
        runner = (ROOT / "eng/verify-candidate.ps1").read_text()
        branch = runner[runner.index("'native-aot' {"):runner.index("'property-counts' {")]
        self.assertIn("'run-aot-smoke.ps1'), '-Combined')", branch)
        self.assertIn("'run-aot-smoke.ps1'), '-MappingOnly')", branch)
        self.assertIn("'-Rid', $LaneEntry.rid", branch)
        self.assertIn("'-SdkVersion', $LaneEntry.sdk", branch)
        uploads = [s for s in lane["steps"] if str(s.get("uses", "")).startswith("actions/upload-artifact")]
        self.assertTrue(uploads and all(str(u.get("if", "")).startswith("always()") for u in uploads))

    def test_candidate_version_is_derived_once_and_survives_failed_lane_reruns(self):
        # "Re-run failed jobs" reruns only failed lanes with a larger run_attempt while the uploaded feed keeps the
        # first attempt's package-manifest.<version>.json. A lane that rebuilds the version from run_attempt would
        # then inspect a manifest that does not exist and fail falsely.
        jobs = self.workflow["jobs"]
        pack = jobs["candidate-pack"]
        self.assertIn("candidate", pack.get("outputs", {}), "pack job must publish the candidate version as an output")
        self.assertIn("steps.", pack["outputs"]["candidate"])
        consumers = [name for name, job in jobs.items() if "candidate-pack" in ([job["needs"]] if isinstance(job.get("needs"), str) else job.get("needs", []))]
        self.assertIn("native-aot", consumers)
        for name in consumers:
            job = jobs[name]
            self.assertNotIn("run_attempt", json.dumps(job.get("env", {})), f"{name} env must not embed run_attempt")
            for step in job["steps"]:
                if "upload-artifact" not in str(step.get("uses", "")):
                    self.assertNotIn("run_attempt", json.dumps(step), f"{name} steps must not recompute the version from run_attempt")
        self.assertNotIn("run_attempt", json.dumps(pack.get("env", {})))
        # The pack step consumes the same single derivation.
        self.assertIn("steps.version.outputs.candidate", json.dumps(pack["steps"]))

    def test_native_reruns_retain_attempt_snapshots_and_current_views(self):
        jobs = self.workflow["jobs"]
        feed = [s["with"] for s in jobs["candidate-pack"]["steps"] if "upload-artifact" in str(s.get("uses", ""))]
        self.assertEqual(len(feed), 1)
        self.assertIn("steps.version.outputs.candidate", feed[0]["name"])
        self.assertIsNot(feed[0].get("overwrite"), True)
        uploads = [s["with"] for s in jobs["native-aot"]["steps"] if "upload-artifact" in str(s.get("uses", ""))]
        current = [u for u in uploads if u.get("overwrite") is True]
        snapshots = {u["name"]: u for u in uploads if u.get("overwrite") is not True}
        self.assertEqual(len(current), 2)
        for view in current:
            snapshot = snapshots["history-" + view["name"] + "-attempt-${{ github.run_attempt }}"]
            self.assertEqual(snapshot["path"], view["path"])

    def test_existing_required_jobs_are_preserved(self):
        for name in ("build", "projection-postgresql", "caching-redis", "package-compatibility", "redis-package-compatibility", "ef-transactions", "http-integration"):
            self.assertIn(name, self.workflow["jobs"])
        redis = json.dumps(self.workflow["jobs"]["redis-package-compatibility"])
        self.assertIn("MERIDIAN_TEST_REDIS_REQUIRED", redis)
        self.assertNotIn("ExcludeConsumerIds", redis)


if __name__ == "__main__":
    unittest.main()

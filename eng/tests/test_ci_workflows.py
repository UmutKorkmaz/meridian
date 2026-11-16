"""Structural tests for the CI/fuzz/release workflows against eng/required-checks.json.

actionlint is not available offline, so these tests assert the properties that matter for the gate: YAML validity, that
every required lane has exactly one job producing its exact check name and running its lane id, exact SDK pins that
mirror eng/packages.json, no preview SDK in a required job, provider images identical to the policy, read-only token
permissions, consistently pinned action references, artifact overwrite for re-runs, no new scheduled monitors, and that
the aggregation job depends on every lane job. They parse files; the workflows themselves have not executed on GitHub.
"""

import itertools
import json
import re
import sys
import unittest
import tempfile
from unittest import mock
from pathlib import Path

import yaml  # required test infrastructure (PyYAML); an import failure is a failure, not a skip

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))
import candidate_gate as gate  # noqa: E402

WORKFLOWS = ROOT / ".github/workflows"
POLICY = json.loads((ROOT / "eng/required-checks.json").read_text())
INVENTORY = json.loads((ROOT / "eng/packages.json").read_text())
TEMPLATE = re.compile(r"\$\{\{\s*matrix\.([A-Za-z0-9_.]+)\s*\}\}")
ALLOWED_SCHEDULES = {"fuzz.yml": ["0 2 * * *"], "benchmarks.yml": ["0 3 * * 1"], "codeql.yml": ["0 4 * * 1"], "scorecard.yml": ["0 5 * * 1"]}


def load(name):
    return yaml.safe_load((WORKFLOWS / name).read_text())


def triggers(workflow):
    # PyYAML (YAML 1.1) parses the bare key `on` as boolean True.
    return workflow.get("on", workflow.get(True))


def steps_text(job):
    return "\n".join(str(step.get("run", "")) for step in job.get("steps", []))


def flatten(prefix, value, out):
    if isinstance(value, dict):
        for key, inner in value.items():
            flatten(f"{prefix}.{key}" if prefix else key, inner, out)
    else:
        out[prefix] = value
    return out


def expand(template, values):
    return TEMPLATE.sub(lambda m: str(values[m.group(1)]), template)


def combos(job):
    """Every concrete matrix combination of a job, with `include` entries merged into the combinations they extend."""
    matrix = (job.get("strategy") or {}).get("matrix") or {}
    axes = {k: v for k, v in matrix.items() if k != "include"}
    rows = [dict(zip(axes, values)) for values in itertools.product(*axes.values())] if axes else [{}]
    flat_rows = []
    for row in rows:
        flat = flatten("", row, {})
        for include in matrix.get("include", []):
            if all(flat.get(k) == v for k, v in include.items() if k in axes and not isinstance(v, dict)) and \
                    any(k in axes for k in include):
                flat.update(flatten("", include, {}))
        flat_rows.append(flat)
    return flat_rows


def lane_values(lane):
    return flatten("", lane["ci"]["matrix"], {})


class PolicyConsistencyTests(unittest.TestCase):
    def test_the_policy_is_valid(self):
        gate.validate_policy(POLICY)

    def test_pinned_sdks_mirror_the_package_inventory_compiler_hosts(self):
        hosts = {h["framework"]: h["sdkVersion"] for h in INVENTORY["compilerHosts"]}
        pinned = {l["framework"]: l["sdk"] for l in POLICY["toolchains"]["pinnedCompatibility"]["lanes"]}
        self.assertEqual(pinned, hosts)
        for lane in POLICY["lanes"]:
            if lane["kind"] in ("package-consumers", "native-aot") and not lane.get("toolchainRootEnvironment"):
                self.assertEqual(lane["sdk"], hosts[lane["framework"]], lane["id"])
        self.assertEqual(POLICY["toolchains"]["pinnedCompatibility"]["buildSdk"], hosts["net10.0"])

    def test_current_servicing_is_the_refreshed_official_patch_set_and_distinct_from_pinned(self):
        servicing = POLICY["toolchains"]["currentServicing"]
        current = {l["framework"]: l for l in servicing["lanes"]}
        pinned = {l["framework"]: l for l in POLICY["toolchains"]["pinnedCompatibility"]["lanes"]}
        # Official release metadata read 2026-11-16 (latest release 2026-11-10); 8/9 are final compatibility patches.
        self.assertEqual({f: (r["sdk"], r["runtime"]) for f, r in current.items()},
                         {"net8.0": ("8.0.428", "8.0.34"), "net9.0": ("9.0.321", "9.0.23"), "net10.0": ("10.0.404", "10.0.15")})
        self.assertEqual((servicing["asOf"], servicing["releaseDate"], servicing["buildSdk"]), ("2026-11-16", "2026-11-10", "10.0.404"))
        self.assertEqual(sorted(m["family"] for m in servicing["metadata"]), ["10.0", "8.0", "9.0"])
        for row in servicing["metadata"]:
            self.assertRegex(row["sha256"], r"^[0-9a-f]{64}$")
            self.assertTrue(row["url"].startswith("https://builds.dotnet.microsoft.com/dotnet/release-metadata/"))
        for framework in ("net8.0", "net9.0", "net10.0"):
            self.assertNotEqual(current[framework]["sdk"], pinned[framework]["sdk"], framework)
            self.assertNotEqual(current[framework]["runtime"], pinned[framework]["runtime"], framework)
        runtime_lanes = {l["framework"]: l for l in POLICY["lanes"] if l["kind"] == "current-servicing"}
        consumer_lanes = {l["framework"]: l for l in POLICY["lanes"] if l["kind"] == "package-consumers" and l.get("toolchainRootEnvironment")}
        self.assertEqual(sorted(runtime_lanes), ["net10.0", "net8.0", "net9.0"])
        self.assertEqual(sorted(consumer_lanes), ["net10.0", "net8.0", "net9.0"])
        for framework in runtime_lanes:
            self.assertEqual((runtime_lanes[framework]["sdk"], runtime_lanes[framework]["runtime"]), (servicing["buildSdk"], current[framework]["runtime"]))
            self.assertEqual(consumer_lanes[framework]["sdk"], current[framework]["sdk"])
            self.assertEqual(consumer_lanes[framework]["consumerScope"], "all")
            for lane in (runtime_lanes[framework], consumer_lanes[framework]):
                self.assertIsNone(lane["ci"], "current servicing is defined locally against an isolated root, not in CI")
                self.assertEqual(lane["toolchainRootEnvironment"], servicing["toolchainRootEnvironment"])
        self.assertNotRegex(json.dumps(POLICY), r'"toolchainRoot"\s*:', "no machine-specific toolchain path is committed")

    def test_provider_images_are_the_pinned_redis_7_4_and_postgres_16_4_digests(self):
        self.assertTrue(POLICY["providers"]["redis"]["image"].startswith("redis:7.4"))
        self.assertTrue(POLICY["providers"]["postgres"]["image"].startswith("postgres:16.4"))

    def test_selected_package_consumers_have_their_required_backends(self):
        # These consumers fail rather than skip when their real backend is absent.
        backend_consumers = {
            "Meridian.RedisIdempotencyConsumer": "redis",
            "Meridian.EfTransactionsConsumer": "postgres",
        }
        all_consumers = {consumer["id"] for consumer in INVENTORY["consumers"]}
        self.assertTrue(set(backend_consumers).issubset(all_consumers))
        for lane in POLICY["lanes"]:
            if lane["kind"] != "package-consumers":
                continue
            with self.subTest(lane=lane["id"]):
                selected = all_consumers - set(lane.get("omittedConsumerIds", []))
                required = {service for consumer, service in backend_consumers.items() if consumer in selected}
                self.assertTrue(required.issubset(set(lane.get("services", []))),
                                f"{lane['id']} selects consumers requiring {sorted(required)}")

    def test_sample_counts_match_the_sample_runner(self):
        text = (ROOT / "eng/run-samples.ps1").read_text()
        base = re.search(r"\$samples = @\(([^)]*)\)", text).group(1)
        names = re.findall(r"'([^']+)'", base)
        self.assertEqual(POLICY["samples"], {"net8.0": len(names), "net9.0": len(names), "net10.0": len(names) + 1})
        self.assertEqual((len(names), POLICY["samples"]["net10.0"]), (6, 7))
        self.assertEqual(POLICY["sampleNames"], {"net8.0": names, "net9.0": names, "net10.0": ["QuickStart"] + names})

    def test_every_legacy_suite_is_required_by_some_profile(self):
        required = {r["suite"] for profile in POLICY["countProfiles"].values() for rows in profile.values() for r in rows}
        projects = {p.parent.name for p in (ROOT / "tests").glob("*/*.csproj") if p.parent.name.endswith(".Tests")}
        self.assertEqual(sorted(projects - required), [], "a test project is not covered by any required lane")
        for suite in required - {"Meridian.Ef10Compatibility"}:
            self.assertTrue((ROOT / f"tests/{suite}/{suite}.csproj").is_file(), suite)

    def test_native_cases_match_the_native_runner_module(self):
        text = (ROOT / "eng/native-aot.psm1").read_text()
        block = text[text.index("function Get-NativeAotExpectedCases"):]
        block = block[:block.index("}")]
        self.assertEqual(re.findall(r"'([a-z-]+)'", block), POLICY["nativeCases"])

    def test_property_budgets_match_the_property_tests(self):
        text = (ROOT / "tests/Meridian.Mapping.Tests/PropertyBasedTests.cs").read_text()
        scalar = len(re.findall(r"\[MeridianProperty\(", text))
        collection = len(re.findall(r"\[CollectionMeridianProperty\(", text))
        self.assertEqual(sorted(POLICY["propertyCases"]), sorted([10000] * scalar + [500] * collection))
        lane = next(l for l in POLICY["lanes"] if l["kind"] == "property-counts")
        self.assertEqual(POLICY["countProfiles"][lane["countsProfile"]]["net10.0"],
                         [{"suite": "Meridian.Mapping.Tests", "minimum": scalar + collection, "filter": "FullyQualifiedName~PropertyBasedTests"}])

    def test_coverage_baseline_and_hot_paths_exist_when_the_lane_is_required(self):
        baseline = json.loads((ROOT / POLICY["coverage"]["baseline"]).read_text())
        self.assertEqual(baseline["schema"], "meridian-coverage-baseline/1")
        for path in baseline["hotPaths"]:
            self.assertTrue((ROOT / path).is_file(), path)
            self.assertIn(path, baseline["files"])
        self.assertNotIn("waivers", baseline, "hot paths meet their targets with tests; the gate accepts no waivers")
        self.assertGreaterEqual(baseline["targets"]["line"], 90.0)
        self.assertGreaterEqual(baseline["targets"]["branch"], 80.0)
        self.assertLessEqual(baseline["tolerance"], 0.5)

    def test_every_built_suite_is_measured_by_the_coverage_lane(self):
        lane = next(l for l in POLICY["lanes"] if l["kind"] == "coverage")
        runtime = {r["suite"] for r in POLICY["countProfiles"]["runtime"]["net10.0"]}
        self.assertEqual({r["suite"] for r in POLICY["countProfiles"][lane["countsProfile"]]["net10.0"]}, runtime)
        for suite in runtime:
            self.assertIn("coverlet.collector", (ROOT / f"tests/{suite}/{suite}.csproj").read_text(), suite)

    def test_policy_paths_and_lane_hosts(self):
        for key in (POLICY["packageInventory"], POLICY["coverage"]["baseline"], POLICY["warnings"]["baseline"], POLICY["warnings"]["solution"]):
            self.assertTrue((ROOT / key).is_file(), key)
        arch = {"linux": "x64", "win": "x64", "osx": "arm64"}
        for lane in POLICY["lanes"]:
            if lane["kind"] in ("runtime-tests", "package-consumers") and not lane.get("toolchainRootEnvironment"):
                self.assertEqual(lane["host"]["arch"], arch[lane["host"]["os"]], lane["id"])
        http = next(l for l in POLICY["lanes"] if l["id"] == "http-integration/net10.0")
        self.assertIs(http["httpSmoke"], True)


class DocumentationTests(unittest.TestCase):
    DOC = (ROOT / "docs/governance/required-checks.md").read_text()

    def test_the_required_checks_page_names_every_check_and_the_proof_levels(self):
        for lane in POLICY["lanes"]:
            self.assertIn(f"`{lane['check']}`", self.DOC, lane["id"])
        for entry in POLICY["infrastructureChecks"]:
            self.assertIn(f"`{entry['check']}`", self.DOC)
        for level in ("local-executed", "CI-defined-not-yet-run", "UNAVAILABLE"):
            self.assertIn(level, self.DOC)

    def test_the_page_states_the_pending_and_scoped_semantics_and_the_governance_findings(self):
        for phrase in ("PENDING", "SCOPED-PASS", "releaseEligible", "Required status checks on `main` | **not enabled**", "UNAVAILABLE"):
            self.assertIn(phrase, self.DOC)

    def test_the_page_names_the_current_servicing_patches_of_the_policy_and_no_stale_ones(self):
        servicing = POLICY["toolchains"]["currentServicing"]
        for row in servicing["lanes"]:
            self.assertIn(row["sdk"], self.DOC)
            self.assertIn(row["runtime"], self.DOC)
        self.assertIn(servicing["releaseDate"], self.DOC)
        for stale in ("8.0.425", "8.0.31", "10.0.401", "10.0.12", "2026-10-04", "artifacts/controller-servicing-toolchains"):
            self.assertNotIn(stale, self.DOC)

    def test_the_page_does_not_document_coverage_waivers_as_accepted(self):
        self.assertNotIn("waived (line)", self.DOC)
        self.assertNotIn("### Per-file waivers", self.DOC)

    def test_every_hot_path_is_in_the_page_table(self):
        baseline = json.loads((ROOT / POLICY["coverage"]["baseline"]).read_text())
        for path in baseline["hotPaths"]:
            self.assertIn(path.replace("src/", ""), self.DOC)


class CiWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ci = load("ci.yml")
        cls.jobs = cls.ci["jobs"]

    def test_every_workflow_parses_and_every_needs_reference_exists(self):
        for path in sorted(WORKFLOWS.glob("*.yml")):
            document = yaml.safe_load(path.read_text())
            self.assertIn("jobs", document, path.name)
            for name, job in document["jobs"].items():
                needs = job.get("needs", [])
                for need in [needs] if isinstance(needs, str) else needs:
                    self.assertIn(need, document["jobs"], f"{path.name}:{name} needs {need}")

    def test_every_ci_defined_lane_has_a_job_with_its_exact_check_name_and_runs_its_lane(self):
        for lane in POLICY["lanes"]:
            if not lane["ci"]:
                continue
            ci = lane["ci"]
            self.assertEqual(ci["workflow"], "ci.yml")
            job = self.jobs[ci["job"]]
            candidates = [row for row in combos(job) if all(row.get(k) == v for k, v in lane_values(lane).items())]
            self.assertEqual(len(candidates), 1, f"{lane['id']}: matrix selects {len(candidates)} combinations")
            row = candidates[0]
            self.assertEqual(expand(job["name"], row), lane["check"], lane["id"])
            self.assertIn(f"-Lane '{lane['id']}'", expand(steps_text(job), row), lane["id"])

    def test_every_matrix_combination_of_a_lane_job_is_a_declared_lane(self):
        declared = {}
        for lane in POLICY["lanes"]:
            if lane["ci"]:
                declared.setdefault(lane["ci"]["job"], 0)
                declared[lane["ci"]["job"]] += 1
        for name, count in declared.items():
            self.assertEqual(len(combos(self.jobs[name])), count, f"{name}: matrix size differs from the declared lanes")

    def test_check_names_are_unique_across_the_workflow_and_match_the_infrastructure_entries(self):
        names = []
        for job_name, job in self.jobs.items():
            names.extend(expand(job["name"], row) for row in combos(job))
        self.assertEqual(len(names), len(set(names)))
        for entry in POLICY["infrastructureChecks"] + POLICY["optionalChecks"]:
            self.assertEqual(self.jobs[entry["job"]]["name"], entry["check"])

    def test_lane_jobs_use_the_verify_candidate_runner_and_the_one_candidate_feed(self):
        for lane in POLICY["lanes"]:
            if not lane["ci"]:
                continue
            job = self.jobs[lane["ci"]["job"]]
            self.assertIn("candidate-pack", job["needs"] if isinstance(job["needs"], list) else [job["needs"]], lane["id"])
            text = steps_text(job)
            self.assertIn("verify-candidate.ps1 -Mode Run", text)
            self.assertIn("artifacts/candidate-feed", text)
            downloads = [s for s in job["steps"] if str(s.get("uses", "")).startswith("actions/download-artifact")]
            self.assertTrue(any(d["with"].get("name") == "${{ needs.candidate-pack.outputs.feedArtifact }}" for d in downloads), lane["id"])

    def test_candidate_pack_packs_once_and_exports_the_version_and_manifest_anchor(self):
        job = self.jobs["candidate-pack"]
        self.assertIn("candidate", job["outputs"])
        self.assertEqual(job["outputs"]["manifestSha256"], "${{ steps.anchor.outputs.manifestSha256 }}")
        self.assertIn("-PackageManifestSha256 '${{ needs.candidate-pack.outputs.manifestSha256 }}'", steps_text(self.jobs["candidate-gate"]))
        self.assertEqual(steps_text(job).count("pack-packages.ps1"), 1)
        for name, other in self.jobs.items():
            if name != "candidate-pack":
                self.assertNotIn("pack-packages.ps1", steps_text(other), f"{name} repacks instead of consuming the candidate feed")

    def test_the_gate_job_aggregates_every_lane_job_and_does_not_hide_failures(self):
        gate_job = self.jobs["candidate-gate"]
        lane_jobs = {l["ci"]["job"] for l in POLICY["lanes"] if l["ci"]} | {"candidate-pack"}
        self.assertEqual(lane_jobs - set(gate_job["needs"]), set())
        self.assertIn("!cancelled()", gate_job["if"])
        text = steps_text(gate_job)
        self.assertIn("-Mode Aggregate", text)
        self.assertIn("-SourceCommit", text)
        merged = [s for s in gate_job["steps"] if s.get("with", {}).get("pattern") == "lane-*"]
        self.assertEqual(len(merged), 1)
        self.assertTrue(merged[0]["with"]["merge-multiple"])

    def test_required_jobs_pin_exact_sdks_that_mirror_the_inventory(self):
        pinned = {h["sdkVersion"] for h in INVENTORY["compilerHosts"]}
        for name, job in self.jobs.items():
            if name == "preview":
                continue
            for step in job.get("steps", []):
                if not str(step.get("uses", "")).startswith("actions/setup-dotnet"):
                    continue
                versions = [v.strip() for v in str(step["with"]["dotnet-version"]).splitlines() if v.strip()]
                self.assertTrue(versions, f"{name} installs no SDK")
                for version in versions:
                    self.assertTrue(version in pinned or version == "${{ matrix.host.sdk }}", f"{name}: {version} is not an exact pinned SDK")
                self.assertNotIn("global-json-file", step["with"])

    def test_runtime_lanes_install_every_runtime_family(self):
        text = "\n".join(str(s["with"]["dotnet-version"]) for s in self.jobs["build"]["steps"] if "setup-dotnet" in str(s.get("uses", "")))
        for sdk in ("8.0.421", "9.0.318", "10.0.300"):
            self.assertIn(sdk, text)
        self.assertIn('[[ "$sdk" == "10.0.300" ]]', steps_text(self.jobs["build"]))

    def test_required_jobs_install_into_a_private_directory_so_only_pinned_sdks_and_runtimes_exist(self):
        for name, job in self.jobs.items():
            for step in job.get("steps", []):
                if str(step.get("uses", "")).startswith("actions/setup-dotnet"):
                    installed = (step.get("env") or {}).get("DOTNET_INSTALL_DIR")
                    if name == "preview":
                        self.assertIsNone(installed)
                    else:
                        self.assertEqual(installed, "${{ runner.temp }}/dotnet", name)

    def test_runtime_and_consumer_lane_hosts_match_their_runner_architecture(self):
        runner_arch = {"ubuntu-latest": ("linux", "x64"), "windows-latest": ("win", "x64"), "macos-latest": ("osx", "arm64")}
        for lane in POLICY["lanes"]:
            if lane["ci"] and lane["kind"] in ("runtime-tests", "package-consumers"):
                job = self.jobs[lane["ci"]["job"]]
                row = next(r for r in combos(job) if all(r.get(k) == v for k, v in lane_values(lane).items()))
                runner = expand(job["runs-on"], row)
                self.assertEqual(runner_arch[runner], (lane["host"]["os"], lane["host"]["arch"]), lane["id"])

    def test_the_eng_tooling_job_is_a_pinned_required_infrastructure_check(self):
        job = self.jobs["eng-tooling-tests"]
        self.assertIn({"check": "eng tooling tests", "workflow": "ci.yml", "job": "eng-tooling-tests"}, POLICY["infrastructureChecks"])
        self.assertGreaterEqual(job["timeout-minutes"], 45)
        self.assertTrue(any("setup-dotnet" in str(s.get("uses", "")) for s in job["steps"]))
        self.assertIn("unittest discover -s eng/tests", steps_text(job))

    def test_the_http_lane_retains_its_smoke_receipts(self):
        uploads = [s for s in self.jobs["http-integration"]["steps"] if str(s.get("uses", "")).startswith("actions/upload-artifact")]
        paths = "\n".join(str(s["with"]["path"]) for s in uploads)
        for fragment in ("samples-summary.json", "http-ownership.json", "artifacts/C6/socket/**"):
            self.assertIn(fragment, paths)

    def test_preview_is_optional_and_isolated(self):
        preview = self.jobs["preview"]
        self.assertIs(preview["continue-on-error"], True)
        self.assertNotIn("needs", preview)
        for name, job in self.jobs.items():
            needs = job.get("needs", [])
            self.assertNotIn("preview", [needs] if isinstance(needs, str) else needs, name)
            if name != "preview":
                self.assertNotIn("11.0", steps_text(job) + json.dumps(job.get("steps", [])))
        lane_jobs = {l["ci"]["job"] for l in POLICY["lanes"] if l["ci"]}
        self.assertNotIn("preview", lane_jobs)

    def test_provider_images_equal_the_policy_pins(self):
        for lane in POLICY["lanes"]:
            if not lane["ci"] or not lane.get("services"):
                continue
            services = self.jobs[lane["ci"]["job"]].get("services", {})
            for name in lane["services"]:
                self.assertIn(name, services, f"{lane['id']} needs the {name} service")
                self.assertEqual(services[name]["image"], POLICY["providers"][name]["image"], lane["id"])
        for name, job in self.jobs.items():
            for service, definition in job.get("services", {}).items():
                self.assertEqual(definition["image"], POLICY["providers"][service]["image"], f"{name}/{service}")

    def test_provider_lanes_export_their_connection_variables(self):
        for lane in POLICY["lanes"]:
            if lane["ci"] and lane.get("services"):
                job = self.jobs[lane["ci"]["job"]]
                # Service ports exist only after the runner starts, so job-level env cannot use them.
                self.assertFalse(any("job.services." in str(value) for value in job.get("env", {}).values()), lane["id"])
                runners = [step for step in job["steps"] if "verify-candidate.ps1 -Mode Run" in step.get("run", "")]
                self.assertEqual(len(runners), 1, lane["id"])
                env = runners[0].get("env", {})
                for name in lane["services"]:
                    self.assertIn(POLICY["providers"][name]["environment"], env, lane["id"])
                if "redis" in lane["services"]:
                    self.assertIn("127.0.0.1:${{ job.services.redis.ports['6379'] }}", env["MERIDIAN_TEST_REDIS"])
                if "postgres" in lane["services"]:
                    service = self.jobs[lane["ci"]["job"]]["services"]["postgres"]
                    # The pinned postgres image defaults the user to postgres when POSTGRES_USER is omitted.
                    user = service["env"].get("POSTGRES_USER", "postgres")
                    database = service["env"]["POSTGRES_DB"]
                    self.assertTrue(isinstance(user, str) and user)
                    self.assertTrue(isinstance(database, str) and database)
                    self.assertIn(f'pg_isready -U {user} -d {database}', service["options"])
                    self.assertIn("Port=${{ job.services.postgres.ports['5432'] }}", env["MERIDIAN_TEST_POSTGRES"])
                    self.assertIn("Database=" + service["env"]["POSTGRES_DB"], env["MERIDIAN_TEST_POSTGRES"])
                    self.assertIn("Username=" + user, env["MERIDIAN_TEST_POSTGRES"])
                    self.assertIn("Password=" + service["env"]["POSTGRES_PASSWORD"], env["MERIDIAN_TEST_POSTGRES"])

    def test_native_lanes_run_on_matching_real_runners(self):
        job = self.jobs["native-aot"]
        include = {entry["rid"]: entry["runner"] for entry in job["strategy"]["matrix"]["include"]}
        self.assertEqual(include, {"linux-x64": "ubuntu-24.04", "osx-arm64": "macos-14"})
        for lane in POLICY["lanes"]:
            if lane["kind"] == "native-aot":
                self.assertEqual(lane["host"]["os"], "linux" if lane["rid"] == "linux-x64" else "osx")

    def test_runtime_and_sdk_host_consumer_lanes_are_separate_jobs_per_operating_system(self):
        runtime = self.jobs["build"]["strategy"]["matrix"]["os"]
        consumers = self.jobs["package-compatibility"]["strategy"]["matrix"]["os"]
        self.assertEqual(sorted(runtime), ["macos-latest", "ubuntu-latest", "windows-latest"])
        self.assertEqual(sorted(consumers), sorted(runtime))
        self.assertNotIn("run-samples", steps_text(self.jobs["package-compatibility"]))

    def test_no_event_payload_field_reaches_a_run_block(self):
        for path in sorted(WORKFLOWS.glob("*.yml")):
            for name, job in yaml.safe_load(path.read_text())["jobs"].items():
                self.assertNotIn("github.event.", steps_text(job), f"{path.name}:{name}")


class CrossWorkflowTests(unittest.TestCase):
    def test_checkout_exception_cannot_allow_a_delivery_workflow_downgrade(self):
        with tempfile.TemporaryDirectory(prefix="meridian-checkout-contract-") as directory:
            root = Path(directory)
            for name, ref in (("ci.yml", "v4"), ("pages.yml", "v4"), ("benchmarks.yml", "v6")):
                (root / name).write_text(yaml.safe_dump({"jobs": {"fixture": {"steps": [
                    {"uses": "actions/checkout@" + ref}, {"uses": "actions/setup-dotnet@v5"}]}}}))
            with mock.patch(__name__ + ".WORKFLOWS", root), self.assertRaises(AssertionError):
                self.test_actions_are_referenced_consistently_by_version_tag_or_sha()

    def test_delivery_workflows_request_only_read_permissions(self):
        # codeql.yml / scorecard.yml / pages.yml legitimately request security-events/id-token/pages writes and are not E4c scope.
        for path in sorted(WORKFLOWS / n for n in ("ci.yml", "fuzz.yml", "release.yml", "benchmarks.yml")):
            document = yaml.safe_load(path.read_text())
            top = document.get("permissions")
            self.assertEqual(top, {"contents": "read"}, f"{path.name} workflow-level permissions")
            for name, job in document["jobs"].items():
                escalated = {k: v for k, v in (job.get("permissions") or {}).items() if v != "read"}
                self.assertEqual(escalated, {}, f"unexpected write permission in {path.name}:{name}")

    def test_ci_and_fuzz_have_no_write_permissions_anywhere(self):
        for name in ("ci.yml", "fuzz.yml"):
            text = (WORKFLOWS / name).read_text()
            self.assertNotRegex(text, r"(?m)^\s+(contents|id-token|attestations|packages|actions|pull-requests|checks|statuses):\s*write")

    def test_actions_are_referenced_consistently_by_version_tag_or_sha(self):
        refs = {}
        for path in sorted(WORKFLOWS.glob("*.yml")):
            for match in re.finditer(r"(?m)^\s*(?:-\s+)?uses:\s*([^@\s]+)@(\S+)", path.read_text()):
                action, ref = match.groups()
                self.assertRegex(ref, r"^(v\d+(\.\d+){0,2}|[0-9a-f]{40})$", f"{path.name}: {action}@{ref}")
                if action == "actions/checkout":
                    self.assertEqual(ref, "v4" if path.name == "pages.yml" else "v6", path.name)
                refs.setdefault(action, set()).add(ref)
        for action, versions in refs.items():
            if action == "actions/checkout":
                self.assertEqual(versions, {"v4", "v6"})  # Existing Pages workflow remains v4; all delivery workflows use v6.
            else:
                self.assertEqual(len(versions), 1, f"{action} is referenced with several versions: {versions}")
        self.assertEqual({r for v in [refs["actions/setup-dotnet"]] for r in v}, {"v5"})

    def test_artifact_uploads_have_immutable_attempt_or_candidate_identity(self):
        for name in ("ci.yml", "fuzz.yml"):
            for job_name, job in yaml.safe_load((WORKFLOWS / name).read_text())["jobs"].items():
                for step in job.get("steps", []):
                    if str(step.get("uses", "")).startswith("actions/upload-artifact"):
                        options = step["with"]
                        if options.get("overwrite") is True:
                            continue  # Immutable snapshots are checked independently in test_workflow_evidence_history.
                        self.assertTrue("github.run_attempt" in options["name"] or "steps.version.outputs.candidate" in options["name"], f"{name}:{job_name}")

    def test_there_are_no_new_scheduled_monitors(self):
        for path in sorted(WORKFLOWS.glob("*.yml")):
            schedule = (triggers(yaml.safe_load(path.read_text())) or {}).get("schedule", [])
            crons = [entry["cron"] for entry in schedule]
            self.assertEqual(crons, ALLOWED_SCHEDULES.get(path.name, []), path.name)

    def test_the_fuzz_schedule_pins_the_sdk_and_requires_the_configured_case_counts(self):
        fuzz = load("fuzz.yml")["jobs"]["property-tests-high-count"]
        text = steps_text(fuzz)
        counts = next(s["run"] for s in fuzz["steps"] if s.get("name") == "Require the configured case counts")
        from collections import Counter
        for count, occurrences in Counter(POLICY["propertyCases"]).items():
            self.assertIn(f'test "$(grep -c \'Ok, passed {count} tests\' "$log")" = {occurrences}', counts)
        setup = next(s for s in fuzz["steps"] if "setup-dotnet" in str(s.get("uses", "")))
        self.assertEqual(setup["with"]["dotnet-version"], "10.0.300")
        self.assertEqual(fuzz["env"]["DOTNET_ROLL_FORWARD_TO_PRERELEASE"], "0")

    def test_release_is_a_manual_read_only_plan_for_the_supplied_bundle_digest(self):
        release = load("release.yml")
        self.assertEqual(set(triggers(release)), {"workflow_dispatch"})
        self.assertEqual(set(release["jobs"]), {"promotion-plan"})
        job = release["jobs"]["promotion-plan"]
        text = steps_text(job)
        self.assertIn("./eng/promote-release.ps1", text)
        self.assertIn("ApprovedManifestSha256 = $env:APPROVED_SHA256", text)
        self.assertNotRegex(text, r"(?i)(?:-Execute\b|dotnet\s+(?:build|pack|publish|test|nuget\s+push)|gh\s+(?:release|api[^\n]*--method))")
        step = next(s for s in job["steps"] if "./eng/promote-release.ps1" in s.get("run", ""))
        self.assertEqual(step["env"]["APPROVED_SHA256"], "${{ inputs.approved_manifest_sha256 }}")
        self.assertEqual(job["permissions"], {"contents": "read", "actions": "read"})

    def test_eng_infrastructure_executes_the_actual_lifecycle_wrapper(self):
        job = load("ci.yml")["jobs"]["eng-tooling-tests"]
        steps = [s for s in job["steps"] if "./eng/verify-deprecations.ps1" in s.get("run", "")]
        self.assertEqual(len(steps), 1)
        self.assertEqual(steps[0]["shell"], "pwsh")
        self.assertNotIn("continue-on-error", steps[0])
        self.assertIn("-Output", steps[0]["run"])

    def test_expressions_are_balanced(self):
        for path in sorted(WORKFLOWS.glob("*.yml")):
            text = path.read_text()
            self.assertEqual(text.count("${{"), len(re.findall(r"\$\{\{.*?\}\}", text)), path.name)


if __name__ == "__main__":
    unittest.main()

"""Actual PowerShell/.NET subprocess checks; disposable projects are infrastructure fixtures."""

import copy
import hashlib
import shutil
import json
import os
from pathlib import Path
import subprocess
import tempfile
import sys
import uuid
from datetime import datetime, timezone
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PACK_SCRIPT = Path(
    os.environ.get("E2_PACK_SCRIPT", str(ROOT / "eng/pack-packages.ps1"))
)
VERSION = "0.0.1-e2.fixture"


class PackageInventoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pwsh = os.environ.get("PWSH") or shutil.which("pwsh")
        if not cls.pwsh or not (shutil.which(cls.pwsh) or Path(cls.pwsh).is_file()):
            raise RuntimeError(
                "Required package-test infrastructure is missing: install pwsh on PATH or set PWSH to its executable; these required tests cannot be skipped"
            )
        cls.artifacts = Path(
            os.environ.get(
                "E2_TEST_ARTIFACT_ROOT", str(ROOT / "artifacts/package-inventory-tests")
            )
        )
        cls.artifacts.mkdir(parents=True, exist_ok=True)
        # Inventory paths and fixture LICENSE includes use this fixed repository layout.
        cls.fixture_root = ROOT / "artifacts/package-inventory-tests"
        cls.fixture_root.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        cls.execution = (
            cls.artifacts / "test-runs" / f"{stamp}-{os.getpid()}-{uuid.uuid4().hex}"
        )
        cls.execution.mkdir(parents=True)
        (cls.execution / "execution.json").write_text(
            json.dumps(
                dict(
                    execution=str(cls.execution),
                    testSourceSha256=hashlib.sha256(
                        Path(__file__).read_bytes()
                    ).hexdigest(),
                    pwsh=cls.pwsh,
                    pid=os.getpid(),
                ),
                indent=2,
            )
        )
        print(f"Package test evidence: {cls.execution}", file=sys.stderr)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(
            prefix="inventory-", dir=self.fixture_root
        )
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.manifest = json.loads((ROOT / "eng/packages.json").read_text())
        self.logs = self.execution / self.id() / uuid.uuid4().hex
        self.logs.mkdir(parents=True)
        self.invocations = 0

    def run_command(self, command, label, timeout=180):
        self.invocations += 1
        evidence = self.logs / f"{self.invocations:02d}-{label}"
        evidence.mkdir()
        inputs = []
        for source in sorted(self.work.rglob("*")):
            relative = source.relative_to(self.work)
            if any(
                part in {"bin", "obj", "feed", "consumers", "packages", "private-feed"}
                for part in relative.parts
            ):
                continue
            if source.is_symlink():
                inputs.append(
                    dict(path=str(relative), kind="symlink", target=os.readlink(source))
                )
            elif source.is_file() and source.suffix.lower() in {
                ".cs",
                ".csproj",
                ".json",
                ".config",
                ".props",
                ".targets",
                ".md",
            }:
                content = source.read_bytes()
                inputs.append(
                    dict(
                        path=str(relative),
                        sha256=hashlib.sha256(content).hexdigest(),
                        content=content.decode(),
                    )
                )
        (evidence / "fixture-source.json").write_text(json.dumps(inputs, indent=2))
        result = subprocess.run(
            command, cwd=ROOT, text=True, capture_output=True, timeout=timeout
        )
        (evidence / "process.json").write_text(
            json.dumps(
                dict(
                    command=command,
                    exit=result.returncode,
                    stdout=result.stdout,
                    stderr=result.stderr,
                ),
                indent=2,
            )
        )
        (evidence / "command.log").write_text(
            f"command={command!r}\nexit={result.returncode}\n{result.stdout}{result.stderr}"
        )
        self.last_evidence = evidence
        return result

    def invoke(self, manifest=None, inspect=False, validate=True):
        path = self.work / "manifest.json"
        path.write_text(json.dumps(manifest or self.manifest))
        command = [
            self.pwsh,
            "-NoProfile",
            "-File",
            str(PACK_SCRIPT),
            "-Manifest",
            str(path),
            "-CandidateVersion",
            VERSION,
            "-OutputDirectory",
            str(self.work / "feed"),
        ]
        if validate:
            command.append("-ValidateOnly")
        if inspect:
            command.append("-InspectOnly")
        label = "inspect" if inspect else "validate" if validate else "pack"
        result = self.run_command(command, label)
        for artifact in (self.work / "feed").glob("*"):
            if artifact.is_file():
                shutil.copy2(artifact, self.last_evidence / artifact.name)
        return result

    def verify(self):
        command = [
            self.pwsh,
            "-NoProfile",
            "-File",
            str(ROOT / "eng/verify-packages.ps1"),
            "-Manifest",
            str(self.work / "manifest.json"),
            "-Feed",
            str(self.work / "feed"),
            "-CandidateVersion",
            VERSION,
            "-Framework",
            "net10.0",
            "-SdkVersion",
            "10.0.300",
            "-OutputDirectory",
            str(self.work / "consumers"),
        ]
        result = self.run_command(command, "verify")
        for report in (self.work / "consumers").glob("**/consumer-results.json"):
            shutil.copytree(
                report.parent,
                self.last_evidence / report.parent.name,
                ignore=shutil.ignore_patterns("packages"),
            )
        for report in (self.work / "consumers").glob("**/sbom-inventory.json"):
            if not (self.last_evidence / report.parent.name).exists():
                shutil.copytree(
                    report.parent,
                    self.last_evidence / report.parent.name,
                    ignore=shutil.ignore_patterns("packages"),
                )
        return result

    def release_selection(self, version):
        path = self.work / "manifest.json"
        path.write_text(json.dumps(self.manifest))
        escaped = str(path).replace("'", "''")
        script = f"$ErrorActionPreference='Stop'; Import-Module ./eng/package-inventory.psm1 -Force -DisableNameChecking; $inventory=Read-PackageInventory (Get-Location).Path '{escaped}'; $selected=@(Select-ReleasePackages $inventory (Get-Location).Path (Get-Command dotnet).Source '{version}'); ConvertTo-Json -InputObject @($selected.id)"
        return self.run_command(
            [self.pwsh, "-NoProfile", "-Command", script], "release-selection"
        )

    def rejects(self, phrase):
        result = self.invoke()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(phrase, result.stdout + result.stderr)
        self.assertFalse(list((self.work / "feed").glob("*.nupkg")))

    def test_current_core_inventory_validates_without_packing(self):
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(list((self.work / "feed").glob("*.nupkg")))

    def test_duplicate_nuget_ids_are_case_insensitive(self):
        extra = copy.deepcopy(self.manifest["packages"][0])
        extra["id"] = "meridian.mapping"
        self.manifest["packages"].append(extra)
        self.rejects("Duplicate package")

    def test_missing_project_fails_before_any_pack(self):
        self.manifest["packages"][1]["project"] = "src/missing.csproj"
        self.rejects("Missing project")

    def test_wrong_target_framework_fails(self):
        self.manifest["packages"][0]["targetFrameworks"] = ["net7.0"]
        self.rejects("TargetFramework mismatch")

    def test_unlisted_analyzer_producer_fails(self):
        self.manifest["packages"][0]["analyzerPaths"] = []
        self.rejects("Unlisted analyzer reference")

    def test_wrong_analyzer_producer_fails(self):
        self.manifest["packages"][0]["analyzerPaths"][0]["project"] = self.manifest[
            "packages"
        ][1]["project"]
        self.rejects("Unlisted analyzer reference")

    def test_executable_version_source_is_rejected(self):
        self.manifest["packages"][0]["versionSource"] = "$(touch /tmp/e2-invalid)"
        self.rejects("Invalid package manifest")

    def test_executable_external_dependency_version_is_rejected(self):
        self.manifest["packages"][0]["directPackageDependencies"][0][
            "version"
        ] = "$(touch /tmp/e2-invalid)"
        self.rejects("Invalid package manifest")

    def test_project_path_escape_is_rejected(self):
        self.manifest["packages"][0][
            "project"
        ] = "../Meridian/src/Meridian.Mapping/Meridian.Mapping.csproj"
        self.rejects("Invalid package manifest")

    def test_symlink_project_escape_is_rejected(self):
        escaped = self.work / "escaped.csproj"
        escaped.symlink_to("/tmp/nonexistent-e2-project.csproj")
        self.manifest["packages"][0]["project"] = str(escaped.relative_to(ROOT))
        self.rejects("Repository path escapes")

    def test_unlisted_external_dependency_fails(self):
        self.manifest["packages"][1]["directPackageDependencies"].pop()
        self.rejects("Direct dependency mismatch")

    def fixture(self, name, reference=None, analyzer=False):
        project_dir = self.work / name
        project_dir.mkdir()
        (self.work / "Directory.Build.props").write_text("<Project />")
        (self.work / "Directory.Build.targets").write_text("<Project />")
        (project_dir / "Class.cs").write_text(
            f"public class {name} {{ public int Value => 42; }}"
        )
        references = (
            ""
            if reference is None
            else f'<ItemGroup><ProjectReference Include="../{reference}/{reference}.csproj" /></ItemGroup>'
        )
        layout = (
            ""
            if not analyzer
            else "<IncludeBuildOutput>false</IncludeBuildOutput><SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking><IncludeSymbols>false</IncludeSymbols>"
        )
        framework = "netstandard2.0" if analyzer else "net10.0"
        assets = (
            ""
            if not analyzer
            else f'<ItemGroup><None Include="$(OutputPath){name}.dll" Pack="true" PackagePath="analyzers/dotnet/cs" /></ItemGroup>'
        )
        (project_dir / f"{name}.csproj").write_text(
            f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>{framework}</TargetFramework><IsPackable>true</IsPackable><PackageId>Fixture.{name}</PackageId><Version>0.0.1</Version><PackageLicenseExpression>MIT</PackageLicenseExpression><PackageReadmeFile>README.md</PackageReadmeFile><IncludeSymbols>true</IncludeSymbols><SymbolPackageFormat>snupkg</SymbolPackageFormat>{layout}</PropertyGroup>{references}{assets}<ItemGroup><None Include="../../../../LICENSE" Pack="true" PackagePath="/"/><None Include="README.md" Pack="true" PackagePath="/" /></ItemGroup></Project>'
        )
        (project_dir / "README.md").write_text(
            "Disposable E2 infrastructure fixture; not an optional adapter."
        )
        entry = dict(
            id=f"Fixture.{name}",
            project=str((project_dir / f"{name}.csproj").relative_to(ROOT)),
            kind="analyzer" if analyzer else "runtime",
            versionSource="Version",
            baselineVersion=None,
            targetFrameworks=[framework],
            analyzerPaths=[],
            directPackageDependencies=[],
            projectDependencies=[],
        )
        if analyzer:
            entry["analyzerPaths"] = [
                dict(
                    project=entry["project"],
                    packagePath=f"analyzers/dotnet/cs/{name}.dll",
                )
            ]
        return entry

    def fixture_manifest(self):
        base = self.fixture("Base")
        dependent = self.fixture("Dependent", "Base")
        return dict(
            schemaVersion=1,
            packages=[base, dependent],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )

    def test_unlisted_runtime_project_reference_fails(self):
        self.manifest = self.fixture_manifest()
        self.rejects("Project dependency mismatch")

    def test_project_dependency_order_is_validated(self):
        self.manifest = self.fixture_manifest()
        self.manifest["packages"][1]["projectDependencies"] = ["Fixture.Base"]
        self.manifest["packages"].reverse()
        self.rejects("must precede")

    def test_null_baseline_closure_cannot_claim_baseline_comparison(self):
        self.manifest = self.fixture_manifest()
        self.manifest["packages"][1]["projectDependencies"] = ["Fixture.Base"]
        self.manifest["consumers"] = [
            dict(
                id="FixtureConsumer",
                project=self.manifest["packages"][1]["project"],
                sourceFiles=[self.manifest["packages"][1]["project"]],
                packageIds=["Fixture.Dependent"],
                variants=["legacy"],
                baselineComparison=True,
            )
        ]
        self.rejects("Baseline closure")

    def test_core_cannot_declare_optional_backend_dependency(self):
        entry = self.fixture("Core")
        project = ROOT / entry["project"]
        project.write_text(
            project.read_text()
            .replace("Fixture.Core", "Meridian.Mediator")
            .replace(
                "</PropertyGroup>",
                "<PackageValidationBaselineVersion>2.1.3</PackageValidationBaselineVersion><EnablePackageValidation>true</EnablePackageValidation></PropertyGroup>",
            )
            .replace(
                "</Project>",
                '<ItemGroup><PackageReference Include="StackExchange.Redis" Version="2.8.0" /></ItemGroup></Project>',
            )
        )
        entry.update(
            id="Meridian.Mediator",
            baselineVersion="2.1.3",
            directPackageDependencies=[dict(id="StackExchange.Redis", version="2.8.0")],
        )
        self.manifest = dict(
            schemaVersion=1,
            packages=[entry],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        self.rejects("Core dependency boundary")

    def test_runtime_project_dependencies_pack_candidate_version_and_inspect_without_rebuild(
        self,
    ):
        self.manifest = self.fixture_manifest()
        self.manifest["packages"][1]["projectDependencies"] = ["Fixture.Base"]
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        report = json.loads(
            (self.work / "feed" / f"package-manifest.{VERSION}.json").read_text()
        )
        self.assertEqual(
            report["packages"][1]["dependencies"],
            [dict(id="Fixture.Base", version=VERSION)],
        )
        (self.work / "Dependent/Class.cs").write_text("this source cannot compile")
        result = self.invoke(inspect=True, validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_analyzer_only_self_producer_packs_without_runtime_lib(self):
        entry = self.fixture("Analyzer", analyzer=True)
        self.manifest = dict(
            schemaVersion=1,
            packages=[entry],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        report = json.loads(
            (self.work / "feed" / f"package-manifest.{VERSION}.json").read_text()
        )
        self.assertEqual(report["packages"][0]["kind"], "analyzer")
        self.assertFalse(
            any(x.startswith("lib/") for x in report["packages"][0]["assets"])
        )
        self.assertEqual(len(report["packages"][0]["analyzers"]), 1)

    def test_subset_consumer_runs_candidate_closure_without_inventing_baseline(self):
        self.manifest = self.fixture_manifest()
        self.manifest["packages"][1]["projectDependencies"] = ["Fixture.Base"]
        # A third unrelated package must not be demanded from this consumer's restore.
        self.manifest["packages"].append(self.fixture("Unrelated"))
        consumer_dir = self.work / "SubsetConsumer"
        consumer_dir.mkdir()
        project = consumer_dir / "SubsetConsumer.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>$(ConsumerFramework)</TargetFramework><LangVersion>12</LangVersion><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include="Fixture.Dependent" Version="[$(MeridianPackageVersion)]" /></ItemGroup></Project>'
        )
        (consumer_dir / "Program.cs").write_text(
            'Console.WriteLine($"HOST runtime={Environment.Version}"); if(new Dependent().Value != 42 || new Base().Value != 42) throw new Exception("fixture dependency"); Console.WriteLine("COMMON fixture dependencies=42 candidate=true");'
        )
        self.manifest["consumers"] = [
            dict(
                id="SubsetConsumer",
                project=str(project.relative_to(ROOT)),
                sourceFiles=[
                    str(project.relative_to(ROOT)),
                    str((consumer_dir / "Program.cs").relative_to(ROOT)),
                ],
                packageIds=["Fixture.Dependent"],
                variants=["legacy"],
                baselineComparison=False,
            )
        ]
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        reports = list((self.work / "consumers").glob("**/consumer-results.json"))
        rows = json.loads(reports[0].read_text())
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["packageIds"], ["Fixture.Base", "Fixture.Dependent"])
        sbom = json.loads((reports[0].parent / "sbom-inventory.json").read_text())
        self.assertEqual(
            {component["id"] for component in sbom["components"]},
            {"Fixture.Base", "Fixture.Dependent"},
        )
        for component in sbom["components"]:
            self.assertEqual(component["license"], "MIT")
            self.assertEqual(component["version"], VERSION)
            self.assertEqual(
                component["sha256"],
                hashlib.sha256(Path(component["path"]).read_bytes()).hexdigest(),
            )
        self.assertEqual(
            rows[0]["binarySha256"],
            hashlib.sha256(
                (
                    Path(rows[0]["directory"])
                    / "bin/Release/net10.0/SubsetConsumer.dll"
                ).read_bytes()
            ).hexdigest(),
        )
        shutil.copytree(
            reports[0].parent,
            self.logs / self.id().split(".")[-1] / "consumer-evidence",
            dirs_exist_ok=True,
            ignore=shutil.ignore_patterns("packages"),
        )

    def test_inventory_dependency_cannot_fall_back_to_published_literal_version(self):
        self.manifest = self.fixture_manifest()
        project = ROOT / self.manifest["packages"][1]["project"]
        project.write_text(
            project.read_text().replace(
                '<ProjectReference Include="../Base/Base.csproj" />',
                '<PackageReference Include="Fixture.Base" Version="0.0.1" />',
            )
        )
        self.manifest["packages"][1]["directPackageDependencies"] = [
            dict(id="Fixture.Base", version="0.0.1")
        ]
        self.rejects("Inventory dependency must use projectDependencies")

    def test_unlisted_packed_analyzer_asset_fails_actual_archive_inspection(self):
        entry = self.fixture("Analyzer", analyzer=True)
        project = ROOT / entry["project"]
        source = project.read_text().replace(
            "</Project>",
            '<ItemGroup><None Include="Class.cs" Pack="true" PackagePath="analyzers/dotnet/cs/Extra.dll" /></ItemGroup></Project>',
        )
        project.write_text(source)
        self.manifest = dict(
            schemaVersion=1,
            packages=[entry],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        result = self.invoke(validate=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Analyzer archive layout mismatch", result.stdout + result.stderr)

    def test_release_selection_uses_inventory_versions_and_rejects_unmatched_tag(self):
        script = "Import-Module ./eng/package-inventory.psm1 -Force -DisableNameChecking; $inventory=Read-PackageInventory (Get-Location).Path 'eng/packages.json'; $selected=@(Select-ReleasePackages $inventory (Get-Location).Path (Get-Command dotnet).Source '2.1.3'); ConvertTo-Json -InputObject @($selected.id); Select-ReleasePackages $inventory (Get-Location).Path (Get-Command dotnet).Source '99.0.0'"
        result = self.run_command(
            [self.pwsh, "-NoProfile", "-Command", script], "core-release-selection"
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Meridian.Mapping", result.stdout)
        self.assertIn("Meridian.Mediator", result.stdout)
        self.assertIn("No inventory package version matches tag", result.stderr)

    def test_analyzer_self_producer_must_target_netstandard2(self):
        entry = self.fixture("Analyzer", analyzer=True)
        entry["targetFrameworks"] = ["net10.0"]
        project = ROOT / entry["project"]
        project.write_text(project.read_text().replace("netstandard2.0", "net10.0"))
        self.manifest = dict(
            schemaVersion=1,
            packages=[entry],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        self.rejects("Analyzer target framework mismatch")

    def test_release_selection_rejects_dependency_with_different_source_version(self):
        self.manifest = self.fixture_manifest()
        self.manifest["packages"][1]["projectDependencies"] = ["Fixture.Base"]
        project = ROOT / self.manifest["packages"][1]["project"]
        project.write_text(
            project.read_text().replace(
                "<Version>0.0.1</Version>", "<Version>0.0.2</Version>"
            )
        )
        result = self.release_selection("0.0.2")
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("Release dependency version mismatch", result.stderr)

    def test_release_selection_same_version_dependency_closure_keeps_manifest_order(
        self,
    ):
        self.manifest = self.fixture_manifest()
        self.manifest["packages"][1]["projectDependencies"] = ["Fixture.Base"]
        result = self.release_selection("0.0.1")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(
            json.loads(result.stdout), ["Fixture.Base", "Fixture.Dependent"]
        )

    def test_release_selection_preserves_independent_matching_package(self):
        base = self.fixture("Base")
        unrelated = self.fixture("Unrelated")
        project = ROOT / unrelated["project"]
        project.write_text(
            project.read_text().replace(
                "<Version>0.0.1</Version>", "<Version>0.0.2</Version>"
            )
        )
        self.manifest = dict(
            schemaVersion=1,
            packages=[base, unrelated],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        result = self.release_selection("0.0.1")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout), ["Fixture.Base"])

    def test_private_analyzer_tooling_provenance_remains_bound_to_pack_after_restore(
        self,
    ):
        tool = self.fixture("Tool")
        tool_project = ROOT / tool["project"]
        tool_project.write_text(
            tool_project.read_text().replace("net10.0", "netstandard2.0")
        )
        tool_feed = self.work / "private-feed"
        for version in ["0.0.1", "0.0.2"]:
            result = self.run_command(
                [
                    "dotnet",
                    "pack",
                    str(tool_project),
                    "-c",
                    "Release",
                    "-o",
                    str(tool_feed),
                    f"-p:Version={version}",
                    f"-p:PackageVersion={version}",
                ],
                f"private-tool-{version}",
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        entry = self.fixture("Analyzer", analyzer=True)
        project = ROOT / entry["project"]
        project.write_text(
            project.read_text().replace(
                "</Project>",
                '<ItemGroup><PackageReference Include="Fixture.Tool" Version="0.0.1" PrivateAssets="all" /></ItemGroup></Project>',
            )
        )
        (self.work / "NuGet.Config").write_text(
            f'<configuration><packageSources><clear/><add key="fixture-tool" value="{tool_feed}"/><add key="dependencies" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
        )
        self.manifest = dict(
            schemaVersion=1,
            packages=[entry],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        producer = project.parent / "bin/Release/netstandard2.0/Analyzer.dll"
        packed_hash = hashlib.sha256(producer.read_bytes()).hexdigest()
        project.write_text(
            project.read_text().replace('Version="0.0.1"', 'Version="0.0.2"')
        )
        result = self.run_command(
            ["dotnet", "restore", str(project), "--force", "--no-cache"],
            "post-pack-restore",
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(hashlib.sha256(producer.read_bytes()).hexdigest(), packed_hash)
        assets = json.loads((project.parent / "obj/project.assets.json").read_text())
        self.assertIn("Fixture.Tool/0.0.2", assets["libraries"])
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        reports = list((self.work / "consumers").glob("**/sbom-inventory.json"))
        sbom = json.loads(reports[0].read_text())
        roots = sbom["buildTooling"][0]["privateRoots"]
        self.assertEqual(
            [root["version"] for root in roots if root["id"] == "Fixture.Tool"],
            ["0.0.1"],
        )
        self.assertIn(
            "Fixture.Tool/0.0.1",
            {
                component["id"] + "/" + component["version"]
                for component in sbom["buildTooling"][0]["components"]
            },
        )

    def test_inspect_rejects_missing_analyzer_build_provenance(self):
        entry = self.fixture("Analyzer", analyzer=True)
        self.manifest = dict(
            schemaVersion=1,
            packages=[entry],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        manifest = self.work / "feed" / f"package-manifest.{VERSION}.json"
        record = json.loads(manifest.read_text())
        record.pop("buildTooling", None)
        manifest.write_text(json.dumps(record))
        result = self.invoke(inspect=True, validate=False)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(
            "Missing analyzer build provenance", result.stdout + result.stderr
        )

    def test_inspect_rejects_analyzer_producer_identity_different_from_inventory(self):
        entry = self.fixture("Analyzer", analyzer=True)
        self.manifest = dict(
            schemaVersion=1,
            packages=[entry],
            compilerHosts=self.manifest["compilerHosts"],
            consumers=[],
        )
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        manifest = self.work / "feed" / f"package-manifest.{VERSION}.json"
        record = json.loads(manifest.read_text())
        record["packages"][0]["analyzers"][0]["project"] = "different/Producer.csproj"
        manifest.write_text(json.dumps(record))
        result = self.invoke(inspect=True, validate=False)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(
            "Analyzer build provenance identity mismatch", result.stdout + result.stderr
        )

    def write_nuspec_identity(self, source, destination, version=None, package_id=None):
        with zipfile.ZipFile(source) as original, zipfile.ZipFile(
            destination, "w"
        ) as changed:
            for entry in original.infolist():
                content = original.read(entry.filename)
                if entry.filename.endswith(".nuspec"):
                    text = content.decode()
                    if version is not None:
                        text = text.replace(
                            "<version>0.0.1</version>", f"<version>{version}</version>"
                        )
                    if package_id is not None:
                        text = text.replace(
                            "<id>Fixture.Tool</id>", f"<id>{package_id}</id>"
                        )
                    content = text.encode()
                changed.writestr(entry, content)

    def restored_component_fixture(self, version="0.0.1"):
        tool = self.fixture("Tool")
        project = ROOT / tool["project"]
        original_feed = self.work / "private-feed"
        result = self.run_command(
            [
                "dotnet",
                "pack",
                str(project),
                "-c",
                "Release",
                "-o",
                str(original_feed),
                "-p:Version=0.0.1",
                "-p:PackageVersion=0.0.1",
            ],
            "pack-restored-tool",
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        original = original_feed / "Fixture.Tool.0.0.1.nupkg"
        feed = self.work / "restored-tool-feed"
        feed.mkdir()
        self.write_nuspec_identity(original, feed / original.name, version=version)
        probe = self.work / "Probe"
        probe.mkdir()
        project = probe / "Probe.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Fixture.Tool" Version="[0.0.1]" /></ItemGroup></Project>'
        )
        config = probe / "NuGet.Config"
        config.write_text(
            f'<configuration><packageSources><clear/><add key="fixture" value="{feed}"/></packageSources></configuration>'
        )
        packages = self.work / "packages"
        result = self.run_command(
            [
                "dotnet",
                "restore",
                str(project),
                "--configfile",
                str(config),
                f"-p:RestorePackagesPath={packages}",
            ],
            "restore-normalized-tool",
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        assets = probe / "obj/project.assets.json"
        self.assertEqual(
            list(json.loads(assets.read_text())["libraries"]), ["Fixture.Tool/0.0.1"]
        )
        shutil.copy2(assets, self.last_evidence / "project.assets.json")
        shutil.copy2(feed / original.name, self.last_evidence / original.name)
        return assets, packages, original

    def read_restored_components(self, assets, packages):
        quoted_assets = str(assets).replace("'", "''")
        quoted_packages = str(packages).replace("'", "''")
        script = f"$ErrorActionPreference='Stop'; Import-Module ./eng/package-inventory.psm1 -Force -DisableNameChecking; $assets=Get-Content -Raw '{quoted_assets}' | ConvertFrom-Json; $components=@(Read-RestoredComponents $assets '{quoted_packages}'); $assembly=[NuGet.Versioning.NuGetVersion].Assembly; $sdk=(& dotnet --version) -join ''; $receipt=[ordered]@{{sdk=$sdk;runtime=[Environment]::Version.ToString();assemblyPath=$assembly.Location;assemblyVersion=$assembly.GetName().Version.ToString();assemblySha256=(Get-FileHash $assembly.Location -Algorithm SHA256).Hash.ToLowerInvariant()}}; [Console]::Error.WriteLine(('NuGet identity receipt: '+($receipt | ConvertTo-Json -Compress))); ConvertTo-Json -InputObject $components -Depth 10"
        result = self.run_command(
            [self.pwsh, "-NoProfile", "-Command", script], "read-restored-components"
        )
        shutil.copy2(assets, self.last_evidence / "project.assets.json")
        for package in packages.glob("**/*.nupkg"):
            shutil.copy2(package, self.last_evidence / package.name)
        return result

    def test_actual_restore_accepts_nuspec_fourth_zero_version_identity(self):
        assets, packages, _ = self.restored_component_fixture("0.0.1.0")
        result = self.read_restored_components(assets, packages)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        components = json.loads(result.stdout)
        self.assertEqual(
            [(item["id"], item["version"]) for item in components],
            [("Fixture.Tool", "0.0.1")],
        )
        package = packages / "fixture.tool/0.0.1/fixture.tool.0.0.1.nupkg"
        self.assertEqual(
            components[0]["sha256"], hashlib.sha256(package.read_bytes()).hexdigest()
        )

    def test_actual_restore_accepts_nuspec_leading_zero_version_identity(self):
        assets, packages, _ = self.restored_component_fixture("0.00.01")
        result = self.read_restored_components(assets, packages)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_actual_restore_accepts_nuspec_build_metadata_version_identity(self):
        assets, packages, _ = self.restored_component_fixture("0.0.1+fixture.1")
        result = self.read_restored_components(assets, packages)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_restored_component_rejects_genuinely_different_version(self):
        assets, packages, original = self.restored_component_fixture()
        package = packages / "fixture.tool/0.0.1/fixture.tool.0.0.1.nupkg"
        for version in ("0.0.2", "0.0.1.1", "0.0.1-beta.1"):
            with self.subTest(nuspec_version=version):
                self.write_nuspec_identity(original, package, version=version)
                result = self.read_restored_components(assets, packages)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(
                    "Restored nuspec identity mismatch", result.stdout + result.stderr
                )

    def test_restored_component_rejects_genuinely_different_package_id(self):
        assets, packages, original = self.restored_component_fixture()
        package = packages / "fixture.tool/0.0.1/fixture.tool.0.0.1.nupkg"
        self.write_nuspec_identity(original, package, package_id="Fixture.Other")
        result = self.read_restored_components(assets, packages)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(
            "Restored nuspec identity mismatch", result.stdout + result.stderr
        )

    def test_evidence_root_is_independent_of_repository_fixture_layout(self):
        for location in ("external", "nested"):
            with self.subTest(evidence_root=location):
                parent = None if location == "external" else self.work
                with tempfile.TemporaryDirectory(
                    prefix="meridian-evidence-", dir=parent
                ) as evidence:
                    evidence_root = Path(evidence) / "nested/evidence/depth"

                    class EvidenceProbe(PackageInventoryTests):
                        pass

                    with mock.patch.dict(
                        os.environ, E2_TEST_ARTIFACT_ROOT=str(evidence_root)
                    ):
                        EvidenceProbe.setUpClass()
                    probe = EvidenceProbe(
                        "test_current_core_inventory_validates_without_packing"
                    )
                    probe.setUp()
                    try:
                        self.assertTrue(
                            probe.work.is_relative_to(
                                ROOT / "artifacts/package-inventory-tests"
                            )
                        )
                        entry = probe.fixture("EvidenceProbe")
                        probe.manifest = dict(
                            schemaVersion=1,
                            packages=[entry],
                            compilerHosts=self.manifest["compilerHosts"],
                            consumers=[],
                        )
                        result = probe.invoke(validate=False)
                        self.assertEqual(
                            result.returncode, 0, result.stdout + result.stderr
                        )
                        self.assertTrue(probe.execution.is_relative_to(evidence_root))
                        shutil.copytree(
                            probe.execution, self.logs / f"{location}-evidence"
                        )
                    finally:
                        probe.doCleanups()


class ProjectEvaluationBannerTests(unittest.TestCase):
    def test_first_use_banner_is_suppressed_and_environment_is_restored(self):
        pwsh = shutil.which('pwsh')
        self.assertIsNotNone(pwsh, 'PowerShell is required for project evaluation checks.')
        for prior in (None, '', 'previous-value'):
            with self.subTest(prior=prior), tempfile.TemporaryDirectory() as directory:
                work = Path(directory)
                shim = work / 'first-use-dotnet.ps1'
                shim.write_text(
                    "if ($env:DOTNET_NOLOGO -ne '1') { Write-Output 'Welcome to .NET 8.0!' }\n"
                    "Write-Output '{\"Properties\":{\"AssemblyName\":\"Probe\",\"TargetFramework\":\"net8.0\"}}'\n"
                    '$global:LASTEXITCODE = 0\n')
                driver = work / 'driver.ps1'
                module = str(ROOT / 'eng/package-inventory.psm1').replace("'", "''")
                executable = str(shim).replace("'", "''")
                initial = ("Remove-Item Env:DOTNET_NOLOGO -ErrorAction Ignore" if prior is None
                           else f"$env:DOTNET_NOLOGO = '{prior}'")
                driver.write_text(
                    "$ErrorActionPreference = 'Stop'\n" + initial + '\n'
                    "$before = @{present=(Test-Path Env:DOTNET_NOLOGO); value=$env:DOTNET_NOLOGO}\n"
                    f"Import-Module '{module}' -Force -DisableNameChecking\n"
                    f"$result = Invoke-ProjectEvaluation '{executable}' 'unused.csproj' @('AssemblyName','TargetFramework')\n"
                    "@{name=$result.Properties.AssemblyName; framework=$result.Properties.TargetFramework; "
                    "before=$before; present=(Test-Path Env:DOTNET_NOLOGO); value=$env:DOTNET_NOLOGO} | ConvertTo-Json -Compress\n")
                result = subprocess.run([pwsh, '-NoProfile', '-File', str(driver)],
                                        capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                report = json.loads(result.stdout)
                self.assertEqual(('Probe', 'net8.0'), (report['name'], report['framework']))
                if prior != '':
                    self.assertEqual(prior is not None, report['before']['present'])
                    self.assertEqual(prior, report['before']['value'])
                # Older hosts cannot represent a present-but-empty variable.
                # Restore the actual caller state on either representation.
                self.assertEqual(report['before']['present'], report['present'])
                self.assertEqual(report['before']['value'], report['value'])

    def test_evaluation_failure_restores_the_callers_environment(self):
        pwsh = shutil.which('pwsh')
        self.assertIsNotNone(pwsh, 'PowerShell is required for project evaluation checks.')
        for prior in (None, 'previous-value'):
            with self.subTest(prior=prior), tempfile.TemporaryDirectory() as directory:
                work = Path(directory)
                shim = work / 'failed-dotnet.ps1'
                shim.write_text('$global:LASTEXITCODE = 23\n')
                module = str(ROOT / 'eng/package-inventory.psm1').replace("'", "''")
                executable = str(shim).replace("'", "''")
                initial = ("Remove-Item Env:DOTNET_NOLOGO -ErrorAction Ignore" if prior is None
                           else "$env:DOTNET_NOLOGO = 'previous-value'")
                driver = work / 'driver.ps1'
                driver.write_text(
                    "$ErrorActionPreference = 'Stop'\n" + initial + '\n'
                    f"Import-Module '{module}' -Force -DisableNameChecking\n"
                    "$failure = $null\ntry {\n"
                    f"Invoke-ProjectEvaluation '{executable}' 'unused.csproj' @('AssemblyName') | Out-Null\n"
                    "} catch { $failure = $_.Exception.Message }\n"
                    "@{failure=$failure; present=(Test-Path Env:DOTNET_NOLOGO); value=$env:DOTNET_NOLOGO} | ConvertTo-Json -Compress\n")
                result = subprocess.run([pwsh, '-NoProfile', '-File', str(driver)],
                                        capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                report = json.loads(result.stdout)
                self.assertIn('exit=23', report['failure'])
                self.assertEqual(prior is not None, report['present'])
                self.assertEqual(prior, report['value'])


if __name__ == "__main__":
    unittest.main()

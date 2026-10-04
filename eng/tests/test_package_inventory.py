"""Actual PowerShell/.NET subprocess checks; disposable projects are infrastructure fixtures."""
import copy
import hashlib
import shutil
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
PWSH = os.environ.get('PWSH', '/opt/homebrew/bin/pwsh')
PACK_SCRIPT = Path(os.environ.get('E2_PACK_SCRIPT', str(ROOT / 'eng/pack-packages.ps1')))
VERSION = '0.0.1-e2.fixture'

class PackageInventoryTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'artifacts/E2').mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='inventory-', dir=ROOT / 'artifacts/E2')
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.manifest = json.loads((ROOT / 'eng/packages.json').read_text())
        self.logs = ROOT / 'artifacts/E2/test-logs'
        self.logs.mkdir(exist_ok=True)
        self.invocations = 0

    def invoke(self, manifest=None, inspect=False, validate=True):
        path = self.work / 'manifest.json'
        path.write_text(json.dumps(manifest or self.manifest))
        evidence = self.logs / self.id().split('.')[-1]
        evidence.mkdir(exist_ok=True)
        inputs = []
        for source in self.work.rglob('*'):
            if source.is_file() and not any(part in {'bin','obj','feed','consumers','packages'} for part in source.relative_to(self.work).parts):
                inputs.append(dict(path=str(source.relative_to(self.work)),sha256=hashlib.sha256(source.read_bytes()).hexdigest(),content=source.read_text()))
        self.invocations += 1
        (evidence / f'fixture-source.{self.invocations}.json').write_text(json.dumps(inputs,indent=2))
        cmd = [PWSH, '-NoProfile', '-File', str(PACK_SCRIPT),
               '-Manifest', str(path), '-CandidateVersion', VERSION,
               '-OutputDirectory', str(self.work / 'feed')]
        if validate:
            cmd.append('-ValidateOnly')
        if inspect:
            cmd.append('-InspectOnly')
        result = subprocess.run(cmd, cwd=ROOT, text=True, capture_output=True, timeout=180)
        with (self.logs / (self.id().split('.')[-1] + '.log')).open('a') as log:
            log.write(f'command={cmd!r}\nexit={result.returncode}\n{result.stdout}{result.stderr}')
        for artifact in (self.work / 'feed').glob('*'):
            if artifact.is_file(): shutil.copy2(artifact, evidence / artifact.name)
        return result

    def rejects(self, phrase):
        result = self.invoke()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(phrase, result.stdout + result.stderr)
        self.assertFalse(list((self.work / 'feed').glob('*.nupkg')))

    def test_current_core_inventory_validates_without_packing(self):
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(list((self.work / 'feed').glob('*.nupkg')))

    def test_duplicate_nuget_ids_are_case_insensitive(self):
        extra = copy.deepcopy(self.manifest['packages'][0])
        extra['id'] = 'meridian.mapping'
        self.manifest['packages'].append(extra)
        self.rejects('Duplicate package')

    def test_missing_project_fails_before_any_pack(self):
        self.manifest['packages'][1]['project'] = 'src/missing.csproj'
        self.rejects('Missing project')

    def test_wrong_target_framework_fails(self):
        self.manifest['packages'][0]['targetFrameworks'] = ['net7.0']
        self.rejects('TargetFramework mismatch')

    def test_unlisted_analyzer_producer_fails(self):
        self.manifest['packages'][0]['analyzerPaths'] = []
        self.rejects('Unlisted analyzer reference')

    def test_wrong_analyzer_producer_fails(self):
        self.manifest['packages'][0]['analyzerPaths'][0]['project'] = self.manifest['packages'][1]['project']
        self.rejects('Unlisted analyzer reference')

    def test_executable_version_source_is_rejected(self):
        self.manifest['packages'][0]['versionSource'] = "$(touch /tmp/e2-invalid)"
        self.rejects('Invalid package manifest')

    def test_executable_external_dependency_version_is_rejected(self):
        self.manifest['packages'][0]['directPackageDependencies'][0]['version'] = "$(touch /tmp/e2-invalid)"
        self.rejects('Invalid package manifest')

    def test_project_path_escape_is_rejected(self):
        self.manifest['packages'][0]['project'] = '../Meridian/src/Meridian.Mapping/Meridian.Mapping.csproj'
        self.rejects('Invalid package manifest')

    def test_symlink_project_escape_is_rejected(self):
        escaped = self.work / 'escaped.csproj'
        escaped.symlink_to('/tmp/nonexistent-e2-project.csproj')
        self.manifest['packages'][0]['project'] = str(escaped.relative_to(ROOT))
        self.rejects('Repository path escapes')

    def test_unlisted_external_dependency_fails(self):
        self.manifest['packages'][1]['directPackageDependencies'].pop()
        self.rejects('Direct dependency mismatch')

    def fixture(self, name, reference=None, analyzer=False):
        project_dir = self.work / name
        project_dir.mkdir()
        (self.work / 'Directory.Build.props').write_text('<Project />')
        (self.work / 'Directory.Build.targets').write_text('<Project />')
        (project_dir / 'Class.cs').write_text(f'public class {name} {{ public int Value => 42; }}')
        references = '' if reference is None else f'<ItemGroup><ProjectReference Include="../{reference}/{reference}.csproj" /></ItemGroup>'
        layout = '' if not analyzer else '<IncludeBuildOutput>false</IncludeBuildOutput><SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking><IncludeSymbols>false</IncludeSymbols>'
        framework = 'netstandard2.0' if analyzer else 'net10.0'
        assets = '' if not analyzer else f'<ItemGroup><None Include="$(OutputPath){name}.dll" Pack="true" PackagePath="analyzers/dotnet/cs" /></ItemGroup>'
        (project_dir / f'{name}.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>{framework}</TargetFramework><IsPackable>true</IsPackable><PackageId>Fixture.{name}</PackageId><Version>0.0.1</Version><PackageLicenseExpression>MIT</PackageLicenseExpression><PackageReadmeFile>README.md</PackageReadmeFile><IncludeSymbols>true</IncludeSymbols><SymbolPackageFormat>snupkg</SymbolPackageFormat>{layout}</PropertyGroup>{references}{assets}<ItemGroup><None Include="../../../../LICENSE" Pack="true" PackagePath="/"/><None Include="README.md" Pack="true" PackagePath="/" /></ItemGroup></Project>')
        (project_dir / 'README.md').write_text('Disposable E2 infrastructure fixture; not an optional adapter.')
        entry = dict(id=f'Fixture.{name}', project=str((project_dir / f'{name}.csproj').relative_to(ROOT)), kind='analyzer' if analyzer else 'runtime', versionSource='Version', baselineVersion=None, targetFrameworks=[framework], analyzerPaths=[], directPackageDependencies=[], projectDependencies=[])
        if analyzer:
            entry['analyzerPaths'] = [dict(project=entry['project'], packagePath=f'analyzers/dotnet/cs/{name}.dll')]
        return entry

    def fixture_manifest(self):
        base = self.fixture('Base')
        dependent = self.fixture('Dependent', 'Base')
        return dict(schemaVersion=1, packages=[base, dependent], compilerHosts=self.manifest['compilerHosts'], consumers=[])

    def test_unlisted_runtime_project_reference_fails(self):
        self.manifest = self.fixture_manifest()
        self.rejects('Project dependency mismatch')

    def test_project_dependency_order_is_validated(self):
        self.manifest = self.fixture_manifest()
        self.manifest['packages'][1]['projectDependencies'] = ['Fixture.Base']
        self.manifest['packages'].reverse()
        self.rejects('must precede')

    def test_null_baseline_closure_cannot_claim_baseline_comparison(self):
        self.manifest = self.fixture_manifest()
        self.manifest['packages'][1]['projectDependencies'] = ['Fixture.Base']
        self.manifest['consumers'] = [dict(id='FixtureConsumer', project=self.manifest['packages'][1]['project'], sourceFiles=[self.manifest['packages'][1]['project']], packageIds=['Fixture.Dependent'], variants=['legacy'], baselineComparison=True)]
        self.rejects('Baseline closure')

    def test_core_cannot_declare_optional_backend_dependency(self):
        entry = self.fixture('Core')
        project=ROOT / entry['project']
        project.write_text(project.read_text().replace('Fixture.Core','Meridian.Mediator').replace('</PropertyGroup>','<PackageValidationBaselineVersion>2.1.3</PackageValidationBaselineVersion><EnablePackageValidation>true</EnablePackageValidation></PropertyGroup>').replace('</Project>','<ItemGroup><PackageReference Include="StackExchange.Redis" Version="2.8.0" /></ItemGroup></Project>'))
        entry.update(id='Meridian.Mediator',baselineVersion='2.1.3',directPackageDependencies=[dict(id='StackExchange.Redis',version='2.8.0')])
        self.manifest=dict(schemaVersion=1,packages=[entry],compilerHosts=self.manifest['compilerHosts'],consumers=[])
        self.rejects('Core dependency boundary')

    def test_runtime_project_dependencies_pack_candidate_version_and_inspect_without_rebuild(self):
        self.manifest = self.fixture_manifest()
        self.manifest['packages'][1]['projectDependencies'] = ['Fixture.Base']
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        report = json.loads((self.work / 'feed' / f'package-manifest.{VERSION}.json').read_text())
        self.assertEqual(report['packages'][1]['dependencies'], [dict(id='Fixture.Base', version=VERSION)])
        (self.work / 'Dependent/Class.cs').write_text('this source cannot compile')
        result = self.invoke(inspect=True, validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_analyzer_only_self_producer_packs_without_runtime_lib(self):
        entry = self.fixture('Analyzer', analyzer=True)
        self.manifest = dict(schemaVersion=1, packages=[entry], compilerHosts=self.manifest['compilerHosts'], consumers=[])
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        report = json.loads((self.work / 'feed' / f'package-manifest.{VERSION}.json').read_text())
        self.assertEqual(report['packages'][0]['kind'], 'analyzer')
        self.assertFalse(any(x.startswith('lib/') for x in report['packages'][0]['assets']))
        self.assertEqual(len(report['packages'][0]['analyzers']), 1)

    def test_subset_consumer_runs_candidate_closure_without_inventing_baseline(self):
        self.manifest = self.fixture_manifest()
        self.manifest['packages'][1]['projectDependencies'] = ['Fixture.Base']
        # A third unrelated package must not be demanded from this consumer's restore.
        self.manifest['packages'].append(self.fixture('Unrelated'))
        consumer_dir = self.work / 'SubsetConsumer'
        consumer_dir.mkdir()
        project = consumer_dir / 'SubsetConsumer.csproj'
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>$(ConsumerFramework)</TargetFramework><LangVersion>12</LangVersion><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include="Fixture.Dependent" Version="[$(MeridianPackageVersion)]" /></ItemGroup></Project>')
        (consumer_dir / 'Program.cs').write_text('Console.WriteLine($"HOST runtime={Environment.Version}"); if(new Dependent().Value != 42 || new Base().Value != 42) throw new Exception("fixture dependency"); Console.WriteLine("COMMON fixture dependencies=42 candidate=true");')
        self.manifest['consumers'] = [dict(id='SubsetConsumer', project=str(project.relative_to(ROOT)), sourceFiles=[str(project.relative_to(ROOT)),str((consumer_dir / 'Program.cs').relative_to(ROOT))], packageIds=['Fixture.Dependent'], variants=['legacy'], baselineComparison=False)]
        result = self.invoke(validate=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        cmd = [PWSH, '-NoProfile', '-File', str(ROOT / 'eng/verify-packages.ps1'), '-Manifest',str(self.work / 'manifest.json'), '-Feed',str(self.work / 'feed'), '-CandidateVersion', VERSION, '-Framework','net10.0','-SdkVersion','10.0.300','-OutputDirectory',str(self.work / 'consumers')]
        result = subprocess.run(cmd,cwd=ROOT,text=True,capture_output=True,timeout=180)
        (self.logs / (self.id().split('.')[-1] + '-verify.log')).write_text(f'command={cmd!r}\nexit={result.returncode}\n{result.stdout}{result.stderr}')
        self.assertEqual(result.returncode,0,result.stdout + result.stderr)
        reports=list((self.work / 'consumers').glob('**/consumer-results.json'))
        rows=json.loads(reports[0].read_text())
        self.assertEqual(len(rows),1)
        self.assertEqual(rows[0]['packageIds'],['Fixture.Base','Fixture.Dependent'])
        sbom=json.loads((reports[0].parent / 'sbom-inventory.json').read_text())
        self.assertEqual({component['id'] for component in sbom['components']}, {'Fixture.Base','Fixture.Dependent'})
        for component in sbom['components']:
            self.assertEqual(component['license'], 'MIT')
            self.assertEqual(component['version'], VERSION)
            self.assertEqual(component['sha256'],hashlib.sha256(Path(component['path']).read_bytes()).hexdigest())
        self.assertEqual(rows[0]['binarySha256'],hashlib.sha256((Path(rows[0]['directory']) / 'bin/Release/net10.0/SubsetConsumer.dll').read_bytes()).hexdigest())
        shutil.copytree(reports[0].parent,self.logs / self.id().split('.')[-1] / 'consumer-evidence',dirs_exist_ok=True,ignore=shutil.ignore_patterns('packages'))

    def test_inventory_dependency_cannot_fall_back_to_published_literal_version(self):
        self.manifest = self.fixture_manifest()
        project = ROOT / self.manifest['packages'][1]['project']
        project.write_text(project.read_text().replace('<ProjectReference Include="../Base/Base.csproj" />','<PackageReference Include="Fixture.Base" Version="0.0.1" />'))
        self.manifest['packages'][1]['directPackageDependencies'] = [dict(id='Fixture.Base', version='0.0.1')]
        self.rejects('Inventory dependency must use projectDependencies')

    def test_unlisted_packed_analyzer_asset_fails_actual_archive_inspection(self):
        entry=self.fixture('Analyzer',analyzer=True)
        project=ROOT / entry['project']
        source=project.read_text().replace('</Project>','<ItemGroup><None Include="Class.cs" Pack="true" PackagePath="analyzers/dotnet/cs/Extra.dll" /></ItemGroup></Project>')
        project.write_text(source)
        self.manifest=dict(schemaVersion=1,packages=[entry],compilerHosts=self.manifest['compilerHosts'],consumers=[])
        result=self.invoke(validate=False)
        self.assertNotEqual(result.returncode,0)
        self.assertIn('Analyzer archive layout mismatch', result.stdout + result.stderr)

    def test_release_selection_uses_inventory_versions_and_rejects_unmatched_tag(self):
        script = "Import-Module ./eng/package-inventory.psm1 -Force -DisableNameChecking; $inventory=Read-PackageInventory (Get-Location).Path 'eng/packages.json'; $selected=@(Select-ReleasePackages $inventory (Get-Location).Path (Get-Command dotnet).Source '2.1.3'); ConvertTo-Json -InputObject @($selected.id); Select-ReleasePackages $inventory (Get-Location).Path (Get-Command dotnet).Source '99.0.0'"
        result=subprocess.run([PWSH,'-NoProfile','-Command',script],cwd=ROOT,text=True,capture_output=True,timeout=180)
        (self.logs / 'release-selection.log').write_text(f'command={script}\nexit={result.returncode}\n{result.stdout}{result.stderr}')
        self.assertNotEqual(result.returncode,0)
        self.assertIn('Meridian.Mapping',result.stdout)
        self.assertIn('Meridian.Mediator',result.stdout)
        self.assertIn('No inventory package version matches tag',result.stderr)

    def test_analyzer_self_producer_must_target_netstandard2(self):
        entry=self.fixture('Analyzer',analyzer=True)
        entry['targetFrameworks']=['net10.0']
        project=ROOT / entry['project']
        project.write_text(project.read_text().replace('netstandard2.0','net10.0'))
        self.manifest=dict(schemaVersion=1,packages=[entry],compilerHosts=self.manifest['compilerHosts'],consumers=[])
        self.rejects('Analyzer target framework mismatch')

if __name__ == '__main__':
    unittest.main()

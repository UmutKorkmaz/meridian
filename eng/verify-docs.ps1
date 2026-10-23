[CmdletBinding()]
param(
    # Only Release is accepted until the reused package verifier supports other configurations.
    [ValidateSet('Release')][string] $Configuration = 'Release',
    [Parameter(Mandatory)][string] $Feed,
    # Optional disambiguation when the feed holds more than one package manifest.
    [ValidatePattern('\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z')][string] $CandidateVersion,
    # Intent for a stable local candidate; never authorization to publish.
    [switch] $ReleasePreparation,
    [string] $Manifest = 'eng/packages.json',
    [string] $OutputDirectory = 'artifacts/docs',
    # Reuse an existing full-scope eng/verify-packages.ps1 run for this feed instead of running it again.
    [string] $PackageEvidence,
    [string] $WarningBaseline = 'eng/docs-warning-baseline.json',
    [ValidatePattern('^/([A-Za-z0-9_.-]+/)*$')][string] $DeploymentPrefix = '/meridian/'
)
# Builds and checks the documentation for one exact packed candidate. It generates local output only and never
# publishes, pushes or uploads anything. Steps: inspect the feed, restore the pinned private DocFX tool, generate API
# metadata from the packed DLL+XML pairs for every target framework and require identical surfaces, build the site
# twice, combine it with the legacy docs-site entry page, check links/anchors/resources, compare documentation
# snippets with the compiled DocumentationConsumer regions, compare compiler warnings with the committed baseline and
# require the DocumentationConsumer to pass on every manifest compiler host. Reused -PackageEvidence must match the feed
# nupkg hashes and the current consumer source hashes (eng/check-docs.py evidence).
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
Import-Module (Join-Path $PSScriptRoot 'package-inventory.psm1') -Force -DisableNameChecking
$repo = Split-Path -Parent $PSScriptRoot
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repo $Manifest }
$inventory = Read-PackageInventory $repo $manifestPath
$Feed = [IO.Path]::GetFullPath($Feed)
if (-not (Test-Path -LiteralPath $Feed -PathType Container)) { throw "Feed directory not found: $Feed" }
$records = @(Get-ChildItem -LiteralPath $Feed -Filter 'package-manifest.*.json')
if ($CandidateVersion) {
    $records = @($records | Where-Object Name -EQ "package-manifest.$CandidateVersion.json")
    if ($records.Count -ne 1) { throw "Feed has no package manifest for $CandidateVersion" }
} elseif ($records.Count -ne 1) {
    throw "Feed must contain exactly one package-manifest.<version>.json (found $($records.Count)); pass -CandidateVersion"
}
$packageRecord = Get-Content -Raw -LiteralPath $records[0].FullName | ConvertFrom-Json
if ($records[0].Name -ne "package-manifest.$($packageRecord.candidateVersion).json") { throw 'Package manifest name does not match its candidateVersion' }
$CandidateVersion = $packageRecord.candidateVersion
Assert-CandidateVersion $CandidateVersion ([bool] $ReleasePreparation)
$releaseArguments = @(); if ($ReleasePreparation) { $releaseArguments += '-ReleasePreparation' }
$python = (Get-Command python3 -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$checker = Join-Path $PSScriptRoot 'check-docs.py'
$run = Join-Path (Resolve-OutputDirectory $repo $OutputDirectory) "$CandidateVersion/$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))"
$null = New-Item -ItemType Directory -Force $run
$steps = [Collections.Generic.List[object]]::new()
function Write-Step([string] $Name, [string] $Detail) {
    $steps.Add([ordered]@{ step=$Name; result='PASS'; detail=$Detail; utc=[DateTime]::UtcNow.ToString('o') })
    ConvertTo-Json -InputObject @($steps) -Depth 6 | Set-Content (Join-Path $run 'steps.json')
    Write-Host "PASS $Name $Detail"
}
function Invoke-Logged([string] $File, [string[]] $Arguments, [string] $Log, [string] $WorkingDirectory = $repo) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($File)
    $process.StartInfo.WorkingDirectory = $WorkingDirectory
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    try {
        $null = $process.Start()
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $out.GetAwaiter().GetResult() + $err.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Log, "command=$File $($Arguments -join ' ')`ncwd=$WorkingDirectory`nexit=$($process.ExitCode)`n$text")
        if ($process.ExitCode -ne 0) { Write-Host $text; throw "Command failed (exit=$($process.ExitCode)); see $Log" }
        return $text
    } finally { $process.Dispose() }
}
function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# 1. The feed must still match its pack-time hash manifest and the current package inventory.
$null = Invoke-Logged 'pwsh' (@('-NoProfile', '-File', (Join-Path $PSScriptRoot 'pack-packages.ps1'), '-OutputDirectory', $Feed, '-CandidateVersion', $CandidateVersion, '-Manifest', $manifestPath, '-InspectOnly') + $releaseArguments) (Join-Path $run 'feed-inspection.log')
Write-Step 'feed-inspection' "candidate=$CandidateVersion packages=$(@($packageRecord.packages).Count)"

# 2. Restore the pinned private DocFX tool into a run-local package folder from an explicit NuGet configuration.
$tooling = Join-Path $run 'tooling'
$null = New-Item -ItemType Directory -Force (Join-Path $tooling 'home'), (Join-Path $tooling 'packages'), (Join-Path $tooling 'http-cache')
$toolConfig = Join-Path $tooling 'NuGet.Config'
'<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>' | Set-Content $toolConfig
$toolManifest = Join-Path $repo '.config/dotnet-tools.json'
$savedEnvironment = @{}
foreach ($name in 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO') { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:DOTNET_CLI_HOME = Join-Path $tooling 'home'
    $env:NUGET_PACKAGES = Join-Path $tooling 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $tooling 'http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $null = Invoke-Logged $dotnet @('tool', 'restore', '--tool-manifest', $toolManifest, '--configfile', $toolConfig) (Join-Path $tooling 'restore.log')
    $docfxVersion = (Invoke-Logged $dotnet @('tool', 'run', 'docfx', '--', '--version') (Join-Path $tooling 'docfx-version.log')).Trim()
    $pinned = (Get-Content -Raw $toolManifest | ConvertFrom-Json).tools.docfx.version
    if (-not $docfxVersion.StartsWith("$pinned+", [StringComparison]::Ordinal) -and $docfxVersion -ne $pinned) { throw "DocFX $docfxVersion does not match the pinned $pinned" }
    $toolPackage = Join-Path $env:NUGET_PACKAGES "docfx/$pinned"
    [xml] $nuspec = Get-Content -Raw (Join-Path $toolPackage 'docfx.nuspec')
    $sdkVersion = (Invoke-Logged $dotnet @('--version') (Join-Path $tooling 'sdk.log')).Trim()
    [ordered]@{
        scope = 'private-documentation-build'
        note = 'Documentation tooling is not part of any distributed Meridian package or consumer dependency graph.'
        toolManifest = '.config/dotnet-tools.json'; toolManifestSha256 = Get-Sha256 $toolManifest
        nugetConfigSha256 = Get-Sha256 $toolConfig; packageSource = 'https://api.nuget.org/v3/index.json'
        sdkVersion = $sdkVersion; docfxVersion = $docfxVersion
        docfxPackage = [ordered]@{ id = 'docfx'; version = $pinned; nupkgSha512 = (Get-Content -Raw (Join-Path $toolPackage "docfx.$pinned.nupkg.sha512")).Trim(); license = $nuspec.package.metadata.license.InnerText; licenseType = $nuspec.package.metadata.license.type }
        toolRuntimeConfig = @(Get-ChildItem -Recurse (Join-Path $toolPackage 'tools') -Filter 'docfx.runtimeconfig.json' | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($toolPackage, $_.FullName); sha256 = Get-Sha256 $_.FullName } })
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $tooling 'tooling.json')
    Write-Step 'docfx-tool' "docfx=$docfxVersion sdk=$sdkVersion"

    # 3. API inputs: the packed lib/<tfm> DLL and its adjacent XML documentation for every runtime package.
    $runtimePackages = @($inventory.packages | Where-Object kind -EQ 'runtime')
    $frameworks = @($runtimePackages | ForEach-Object { $_.targetFrameworks } | Select-Object -Unique)
    $inputs = @()
    foreach ($framework in $frameworks) {
        $target = Join-Path $run "api-input/$framework"
        $null = New-Item -ItemType Directory -Force $target
        foreach ($package in $runtimePackages) {
            $recorded = @($packageRecord.packages | Where-Object id -EQ $package.id)
            if ($recorded.Count -ne 1) { throw "Package record missing $($package.id)" }
            $assembly = $recorded[0].assemblyName
            $nupkg = Join-Path $Feed "$($package.id).$CandidateVersion.nupkg"
            $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
            try {
                foreach ($extension in 'dll', 'xml') {
                    $entry = $zip.GetEntry("lib/$framework/$assembly.$extension")
                    if (-not $entry) { throw "Packed $($package.id) has no lib/$framework/$assembly.$extension (XML reference input is required)" }
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target "$assembly.$extension"), $true)
                }
            } finally { $zip.Dispose() }
            $inputs += [ordered]@{ id = $package.id; framework = $framework; nupkgSha256 = Get-Sha256 $nupkg; dllSha256 = Get-Sha256 (Join-Path $target "$assembly.dll"); xmlSha256 = Get-Sha256 (Join-Path $target "$assembly.xml") }
        }
    }
    ConvertTo-Json -InputObject $inputs -Depth 5 | Set-Content (Join-Path $run 'api-inputs.json')
    Write-Step 'api-inputs' "dll+xml pairs=$($inputs.Count) frameworks=$($frameworks -join ',')"

    # 4. Dependency reference assemblies resolved exactly as a consumer of the candidate would restore them.
    $escapedFeed = [Security.SecurityElement]::Escape($Feed)
    $patterns = ($inventory.packages | ForEach-Object { "<package pattern=`"$($_.id)`" />" }) -join ''
    $referenceSets = @{}
    $referenceRecord = @()
    foreach ($framework in $frameworks) {
        $work = Join-Path $run "references/$framework"
        $null = New-Item -ItemType Directory -Force $work
        '<Project />' | Set-Content (Join-Path $work 'Directory.Build.props')
        '<Project />' | Set-Content (Join-Path $work 'Directory.Build.targets')
        "<configuration><packageSources><clear/><add key=`"candidate`" value=`"$escapedFeed`"/><add key=`"dependencies`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"candidate`">$patterns</packageSource><packageSource key=`"dependencies`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" | Set-Content (Join-Path $work 'NuGet.Config')
        $references = ($runtimePackages | ForEach-Object { "<PackageReference Include=`"$($_.id)`" Version=`"[$CandidateVersion]`" />" }) -join ''
        "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>$framework</TargetFramework></PropertyGroup><ItemGroup>$references</ItemGroup></Project>" | Set-Content (Join-Path $work 'References.csproj')
        $packages = Join-Path $run 'references/packages' # shared by all frameworks; hashes are still checked per restore
        $null = Invoke-Logged $dotnet @('restore', (Join-Path $work 'References.csproj'), '--configfile', (Join-Path $work 'NuGet.Config'), "-p:RestorePackagesPath=$packages", '--no-cache') (Join-Path $work 'restore.log') $work
        $assets = Get-Content -Raw (Join-Path $work 'obj/project.assets.json') | ConvertFrom-Json
        foreach ($package in $runtimePackages) {
            $id = $package.id.ToLowerInvariant()
            if ((Get-Sha256 (Join-Path $packages "$id/$CandidateVersion/$id.$CandidateVersion.nupkg")) -ne (Get-Sha256 (Join-Path $Feed "$($package.id).$CandidateVersion.nupkg"))) { throw "Restored $($package.id) differs from the feed" }
        }
        $target = @($assets.targets.PSObject.Properties)[0].Value
        $paths = @()
        foreach ($library in $target.PSObject.Properties) {
            $name, $version = $library.Name.Split('/')
            if ($library.Value.type -ne 'package' -or $name -in $inventory.packages.id -or -not $library.Value.PSObject.Properties['compile']) { continue }
            $folder = $assets.libraries.($library.Name).path
            foreach ($file in $library.Value.compile.PSObject.Properties.Name) {
                if ($file.EndsWith('/_._', [StringComparison]::Ordinal)) { continue }
                $path = Join-Path $packages "$folder/$file"
                $paths += $path
                $referenceRecord += [ordered]@{ framework = $framework; package = $name; version = $version; file = $file; sha256 = Get-Sha256 $path }
            }
        }
        $referenceSets[$framework] = $paths
    }
    ConvertTo-Json -InputObject $referenceRecord -Depth 5 | Set-Content (Join-Path $run 'api-references.json')
    Write-Step 'api-references' "dependency reference assemblies=$($referenceRecord.Count)"

    # 5. Metadata per target framework; the API surfaces must be identical before net8.0 is used as the reference.
    $metadataDirs = @()
    foreach ($framework in $frameworks) {
        # DocFX file mappings are globbed relative to a source folder, so group references by their folder.
        $referenceMappings = @($referenceSets[$framework] | Group-Object { Split-Path $_ } | ForEach-Object { [ordered]@{ src = $_.Name; files = @($_.Group | ForEach-Object { Split-Path $_ -Leaf }) } })
        $config = [ordered]@{ metadata = @([ordered]@{ src = @([ordered]@{ src = "api-input/$framework"; files = @('*.dll') }); dest = "api-$framework"; references = $referenceMappings; disableGitFeatures = $true }) }
        $configPath = Join-Path $run "metadata-$framework.json"
        $config | ConvertTo-Json -Depth 8 | Set-Content $configPath
        $log = Join-Path $run "metadata-$framework.log.json"
        $null = Invoke-Logged $dotnet @('tool', 'run', 'docfx', '--', 'metadata', $configPath, '--log', $log, '--logLevel', 'info') (Join-Path $run "metadata-$framework.log") $run
        $null = Invoke-Logged $python @('-B', $checker, 'docfx-log', '--require-message', 'Creating output', '--log', $log, '--report', (Join-Path $run "metadata-$framework.report.json")) (Join-Path $run "metadata-$framework.check.log")
        $metadataDirs += Join-Path $run "api-$framework"
    }
    $null = Invoke-Logged $python (@('-B', $checker, 'surface', '--report', (Join-Path $run 'api-surface.json')) + $metadataDirs) (Join-Path $run 'api-surface.log')
    $canonical = if ('net8.0' -in $frameworks) { 'net8.0' } else { $frameworks[0] }
    Write-Step 'api-metadata' "frameworks=$($frameworks -join ',') identical=true canonical=$canonical docfxWarnings=0"

    # 6. Build the site twice from the same inputs into fresh directories and compare the output.
    $source = Get-Content -Raw (Join-Path $repo 'docs/docfx.json') | ConvertFrom-Json -AsHashtable
    $authored = @($source.build.content[0].files)
    # DocFX resolves TOC folder links physically, so authored pages and API metadata share one content folder.
    $content = Join-Path $run 'content'
    $null = New-Item -ItemType Directory -Force (Join-Path $content 'api')
    foreach ($file in $authored) { Copy-Item -LiteralPath (Join-Path $repo "docs/$file") -Destination (Join-Path $content $file) }
    Copy-Item -Path (Join-Path $run "api-$canonical/*") -Destination (Join-Path $content 'api')
    $sites = @()
    foreach ($label in 'a', 'b') {
        $build = [ordered]@{
            content = @([ordered]@{ files = $authored; src = 'content' }, [ordered]@{ files = @('api/*.yml'); src = 'content' })
            template = $source.build.template
            globalMetadata = $source.build.globalMetadata
            disableGitFeatures = $true
            output = "site-$label"
        }
        $build.globalMetadata['_appTitle'] = "Meridian $CandidateVersion (unpublished candidate)"
        $configPath = Join-Path $run "build-$label.json"
        @{ build = $build } | ConvertTo-Json -Depth 8 | Set-Content $configPath
        $log = Join-Path $run "build-$label.log.json"
        $null = Invoke-Logged $dotnet @('tool', 'run', 'docfx', '--', 'build', $configPath, '--log', $log, '--logLevel', 'info') (Join-Path $run "build-$label.log") $run
        $null = Invoke-Logged $python @('-B', $checker, 'docfx-log', '--require-message', 'XRef map exported', '--log', $log, '--report', (Join-Path $run "build-$label.report.json")) (Join-Path $run "build-$label.check.log")
        $sites += Join-Path $run "site-$label"
    }
    $hashes = foreach ($site in $sites) {
        $map = @{}
        Get-ChildItem -LiteralPath $site -Recurse -File | ForEach-Object { $map[[IO.Path]::GetRelativePath($site, $_.FullName).Replace('\', '/')] = Get-Sha256 $_.FullName }
        $map
    }
    $differences = @(@($hashes[0].Keys) + @($hashes[1].Keys) | Select-Object -Unique | Where-Object { $hashes[0][$_] -ne $hashes[1][$_] } | Sort-Object)
    [ordered]@{ files = $hashes[0].Count; identical = ($differences.Count -eq 0); differences = $differences } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'reproducibility.json')
    if ($differences.Count) { throw "Two builds from identical inputs differ: $($differences -join ', ')" }
    $pages = @(Get-ChildItem -LiteralPath $sites[0] -Recurse -Filter '*.html').Count
    $apiPages = @(Get-ChildItem -LiteralPath (Join-Path $sites[0] 'api') -Filter '*.html').Count
    if ($apiPages -eq 0) { throw 'Generated API reference is empty' }
    Write-Step 'site-build' "pages=$pages apiPages=$apiPages files=$($hashes[0].Count) reproducible=true docfxWarnings=0"
} finally {
    # Restore the caller's environment exactly; an unset variable must stay unset (an empty DOTNET_CLI_HOME would make
    # later restores resolve a relative ".nuget" folder).
    foreach ($name in $savedEnvironment.Keys) {
        if ($null -eq $savedEnvironment[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath "Env:$name" -Value $savedEnvironment[$name] }
    }
}

# 7. Combined local preview root: the legacy entry page at the root, the candidate reference below it.
$preview = Join-Path $run 'preview'
$null = New-Item -ItemType Directory -Force $preview
Copy-Item -Path (Join-Path $repo 'docs-site/*') -Destination $preview -Recurse
$entry = Join-Path $preview 'index.html'
$entryHtml = Get-Content -Raw $entry
if ($entryHtml -notmatch '<ul class="links">') { throw 'Legacy entry page has no docs link list to extend' }
$entryHtml = $entryHtml.Replace('<ul class="links">', "<ul class=`"links`">`n        <li><a href=`"reference/$CandidateVersion/index.html`">Candidate reference $CandidateVersion (local preview only)</a></li>")
[IO.File]::WriteAllText($entry, $entryHtml)
$reference = Join-Path $preview "reference/$CandidateVersion"
$null = New-Item -ItemType Directory -Force $reference
Copy-Item -Path (Join-Path $sites[0] '*') -Destination $reference -Recurse
# Backlink from the candidate reference landing page to the entry page (preview composition only).
$landing = Join-Path $reference 'index.html'
$landingHtml = Get-Content -Raw $landing
if ($landingHtml -notmatch '</article>') { throw 'Reference landing page has no article element' }
$landingHtml = ([regex]'</article>').Replace($landingHtml, '<p><a href="../../index.html">Back to the project entry page</a></p></article>', 1)
[IO.File]::WriteAllText($landing, $landingHtml)
$null = Invoke-Logged $python @('-B', $checker, 'links', '--root', $preview, '--generated-api-dir', 'api', '--report', (Join-Path $run 'links.json')) (Join-Path $run 'links.log')
$null = Invoke-Logged $python @('-B', $checker, 'links', '--root', $preview, '--generated-api-dir', 'api', '--prefix', $DeploymentPrefix, '--report', (Join-Path $run 'links-prefix.json')) (Join-Path $run 'links-prefix.log')
$linkReport = Get-Content -Raw (Join-Path $run 'links.json') | ConvertFrom-Json
Write-Step 'links' "pages=$($linkReport.pages) internal=$($linkReport.internalLinks) broken=0 authoredDuplicateIds=0 docfxOverloadAnchorRepeats=$(@($linkReport.generatedApiDuplicateIds).Count) external=$(@($linkReport.external).Count) prefix=$DeploymentPrefix"

# 8. Documentation snippets and repository Markdown links.
$consumerSource = Join-Path $repo 'tests/consumers/Meridian.DocumentationConsumer/Program.cs'
$readmes = @((Join-Path $repo 'README.md')) + @($inventory.packages | ForEach-Object { Join-Path (Split-Path (Resolve-RepositoryPath $repo $_.project)) 'README.md' } | Where-Object { Test-Path -LiteralPath $_ })
$documents = @($authored | Where-Object { $_.EndsWith('.md') } | ForEach-Object { Join-Path $repo "docs/$_" }) + $readmes
$null = Invoke-Logged $python (@('-B', $checker, 'snippets', '--source', $consumerSource, '--report', (Join-Path $run 'snippets.json')) + $documents) (Join-Path $run 'snippets.log')
$snippetReport = Get-Content -Raw (Join-Path $run 'snippets.json') | ConvertFrom-Json
$markdown = $readmes + @((Join-Path $repo 'docs/README.md')) + @(Get-ChildItem (Join-Path $repo 'samples') -Recurse -Filter 'README.md' | Where-Object { $_.FullName -notmatch '[/\\](bin|obj)[/\\]' } | ForEach-Object FullName)
$null = Invoke-Logged $python (@('-B', $checker, 'mdlinks', '--report', (Join-Path $run 'markdown-links.json')) + $markdown) (Join-Path $run 'markdown-links.log')
Write-Step 'snippets' "compiled=$($snippetReport.compiled) illustrative=$($snippetReport.illustrative) failures=0 documents=$(@($documents).Count)"

# 9. Compiler warning ratchet for the packable runtime projects (clean Release build, every target framework).
$warningLogs = @()
foreach ($package in $runtimePackages) {
    $log = Join-Path $run "warnings-$($package.id).log"
    $null = Invoke-Logged $dotnet @('build', (Resolve-RepositoryPath $repo $package.project), '-c', $Configuration, '--no-incremental', '-clp:NoSummary') $log
    $warningLogs += $log
}
$baselinePath = if ([IO.Path]::IsPathRooted($WarningBaseline)) { $WarningBaseline } else { Join-Path $repo $WarningBaseline }
$null = Invoke-Logged $python (@('-B', $checker, 'warnings', '--repo', $repo, '--baseline', $baselinePath, '--report', (Join-Path $run 'warnings.json')) + $warningLogs) (Join-Path $run 'warnings.log')
$warningReport = Get-Content -Raw (Join-Path $run 'warnings.json') | ConvertFrom-Json
Write-Step 'warnings' "unique=$($warningReport.unique) new=0 explained=$(@($warningReport.explained).Count) resolved=$(@($warningReport.resolved).Count)"

# 10. The DocumentationConsumer (and every other inventoried consumer) must pass on every compiler host.
if (-not $PackageEvidence) {
    $null = Invoke-Logged 'pwsh' (@('-NoProfile', '-File', (Join-Path $PSScriptRoot 'verify-packages.ps1'), '-Feed', $Feed, '-CandidateVersion', $CandidateVersion, '-Manifest', $manifestPath, '-OutputDirectory', (Join-Path $run 'consumers')) + $releaseArguments) (Join-Path $run 'verify-packages.log')
    $PackageEvidence = @(Get-ChildItem (Join-Path $run "consumers/$CandidateVersion") -Directory | Sort-Object Name)[-1].FullName
}
$PackageEvidence = [IO.Path]::GetFullPath($PackageEvidence)
$selection = Get-Content -Raw (Join-Path $PackageEvidence 'consumer-selection.json') | ConvertFrom-Json
if ($selection.scope -ne 'all' -or @($selection.omittedIds).Count) { throw 'Package evidence is a partial consumer run; documentation acceptance needs the full inventory' }
Assert-SameSet @($selection.selectedIds) @($inventory.consumers.id) 'Package evidence does not cover the current consumer inventory'
$sbom = Get-Content -Raw (Join-Path $PackageEvidence 'sbom-inventory.json') | ConvertFrom-Json
if ($sbom.candidateVersion -ne $CandidateVersion -or $sbom.manifestSha256 -ne (Get-Sha256 $manifestPath)) { throw 'Package evidence belongs to another candidate or manifest' }
# Reused evidence must be bound to these exact feed packages and to the current consumer source, not only to a version.
$null = Invoke-Logged $python @('-B', $checker, 'evidence', '--evidence', $PackageEvidence, '--feed', $Feed, '--repo', $repo, '--consumer', 'Meridian.DocumentationConsumer', '--report', (Join-Path $run 'package-evidence-binding.json')) (Join-Path $run 'package-evidence-binding.log')
$results = @(Get-Content -Raw (Join-Path $PackageEvidence 'consumer-results.json') | ConvertFrom-Json)
foreach ($lane in $inventory.compilerHosts) {
    $rows = @($results | Where-Object { $_.consumer -eq 'Meridian.DocumentationConsumer' -and $_.framework -eq $lane.framework -and $_.sdkVersion -eq $lane.sdkVersion -and $_.exit -eq 0 -and $_.packageVersion -eq $CandidateVersion })
    if ($rows.Count -ne 1 -or 'COMMON documentation snippets=executed' -notin @($rows[0].commonOutput)) { throw "DocumentationConsumer did not pass with all assertions on $($lane.framework)/$($lane.sdkVersion)" }
}
$consumerRows = @($results).Count
Write-Step 'documentation-consumer' "lanes=$(@($inventory.compilerHosts).Count) allConsumerRows=$consumerRows evidence=$PackageEvidence"

[ordered]@{
    result = 'PASS'; candidateVersion = $CandidateVersion; configuration = $Configuration; feed = $Feed
    packageManifestSha256 = Get-Sha256 $records[0].FullName; inventorySha256 = Get-Sha256 $manifestPath
    docfxConfigSha256 = Get-Sha256 (Join-Path $repo 'docs/docfx.json'); consumerSourceSha256 = Get-Sha256 $consumerSource
    preview = $preview; reference = $reference; packageEvidence = $PackageEvidence; steps = @($steps)
    published = $false
} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'run.json')
Write-Host "PASS documentation verified for $CandidateVersion; local preview root: $preview (not published)"

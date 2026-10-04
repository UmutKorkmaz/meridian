[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/packages',
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$')][string] $CandidateVersion,
    [string] $Manifest = 'eng/packages.json',
    [switch] $InspectOnly,
    [switch] $ValidateOnly
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'package-inventory.psm1') -Force -DisableNameChecking
$repo = Split-Path -Parent $PSScriptRoot
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repo $Manifest }
$inventory = Read-PackageInventory $repo $manifestPath
$feed = [IO.Path]::GetFullPath($OutputDirectory)
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$evaluations = @{}
# Validate every entry before creating any package. InspectOnly never evaluates or builds projects.
if (-not $InspectOnly) { $evaluations = Test-EvaluatedInventory $inventory $repo $dotnet $Configuration }
if ($ValidateOnly) { Write-Host "PASS validated inventory: $($inventory.packages.Count) packages"; return }
$null = New-Item -ItemType Directory -Force $feed
$hashFile = Join-Path $feed "package-manifest.$CandidateVersion.json"
$recorded = if ($InspectOnly) { Get-Content -Raw $hashFile | ConvertFrom-Json } else { $null }
if (-not $InspectOnly -and @(Get-ChildItem $feed -Filter "*.$CandidateVersion.nupkg").Count) { throw 'Candidate version already exists in output feed; choose a fresh version' }
$evidence = @()
$buildTooling = @{}
$versionOverrides = @($inventory.packages.versionSource | Select-Object -Unique | ForEach-Object { "-p:${_}=$CandidateVersion" })
foreach ($package in $inventory.packages) {
    $project = Resolve-RepositoryPath $repo $package.project
    if (-not $InspectOnly) {
        Push-Location $repo
        try {
            & $dotnet pack $project -c $Configuration -o $feed "-p:PackageVersion=$CandidateVersion" @versionOverrides
            if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed (exit=$LASTEXITCODE): $project" }
        } finally { Pop-Location }
    }
    $nupkg = Join-Path $feed "$($package.id).$CandidateVersion.nupkg"
    $symbols = Join-Path $feed "$($package.id).$CandidateVersion.snupkg"
    $assembly = if ($InspectOnly) { @($recorded.packages | Where-Object id -EQ $package.id)[0].assemblyName } else { $evaluations[$package.id].properties.AssemblyName }
    $declaredDeps = @(Get-DeclaredDependencies $package $CandidateVersion)
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        [xml] $nuspec = Read-ArchiveText $zip "$($package.id).nuspec"
        $metadata = $nuspec.package.metadata
        if ($metadata.id -ne $package.id -or $metadata.version -ne $CandidateVersion) { throw 'Nuspec identity mismatch' }
        if ($metadata.license.type -ne 'expression' -or $metadata.license.InnerText -ne $package.licenseExpression -or $metadata.readme -ne 'README.md') { throw 'Missing README/license metadata' }
        foreach ($asset in @('README.md','LICENSE')) { if (-not $zip.GetEntry($asset)) { throw "Missing $asset" } }
        if ($package.kind -eq 'runtime') {
            foreach ($tfm in $package.targetFrameworks) { if (-not $zip.GetEntry("lib/$tfm/$assembly.dll")) { throw "Missing runtime $tfm" } }
            Assert-SameSet @($metadata.dependencies.group | ForEach-Object targetFramework) @($package.targetFrameworks) 'Dependency group mismatch'
        } elseif (@($zip.Entries | Where-Object FullName -Like 'lib/*').Count) { throw 'Analyzer-only package contains runtime lib assets' }
        $groups = @($metadata.dependencies.group | Where-Object { $_ })
        if (-not $groups.Count -and $declaredDeps.Count) { throw 'Missing nuspec dependency groups' }
        foreach ($group in $groups) {
            $actual = @($group.dependency | Where-Object { $_ } | ForEach-Object { "$($_.id)=$($_.version)" })
            Assert-SameSet $actual @($declaredDeps | ForEach-Object { "$($_.id)=$($_.version)" }) "Nuspec dependency mismatch for $($group.targetFramework)"
        }
        $assets = @($zip.Entries | Where-Object FullName -Like 'analyzers/*' | ForEach-Object FullName)
        Assert-SameSet $assets @($package.analyzerPaths | ForEach-Object packagePath) 'Analyzer archive layout mismatch'
        $analyzerHashes = @()
        foreach ($analyzer in $package.analyzerPaths) {
            $hash = Get-ArchiveHash $zip.GetEntry($analyzer.packagePath)
            $producerHash = if ($InspectOnly) { @(@($recorded.packages | Where-Object id -EQ $package.id)[0].analyzers | Where-Object packagePath -EQ $analyzer.packagePath)[0].producerSha256 } else { (Get-FileHash $evaluations[$package.id].analyzers[$analyzer.packagePath] -Algorithm SHA256).Hash.ToLowerInvariant() }
            if ($hash -ne $producerHash) { throw 'Packed analyzer differs from evaluated producer TargetPath' }
            $toolingHash = $null
            if (-not $InspectOnly) {
                if (-not $buildTooling.ContainsKey($analyzer.project)) {
                    $outputs = @{}
                    foreach ($entry in $inventory.packages) {
                        foreach ($asset in @($entry.analyzerPaths | Where-Object project -EQ $analyzer.project)) { $outputs[$asset.packagePath] = $evaluations[$entry.id].analyzers[$asset.packagePath] }
                    }
                    $buildTooling[$analyzer.project] = New-AnalyzerBuildEvidence $repo $analyzer.project $dotnet $Configuration $CandidateVersion $outputs $versionOverrides
                }
                $toolingHash = $buildTooling[$analyzer.project].evidenceSha256
            }
            $analyzerHashes += [ordered]@{ project=$analyzer.project; packagePath=$analyzer.packagePath; sha256=$hash; producerSha256=$producerHash; toolingEvidenceSha256=$toolingHash }
        }
        $entries = @($zip.Entries | ForEach-Object FullName)
        $binaryHashes = @($zip.Entries | Where-Object { $_.FullName -match '\.(dll|pdb)$' } | ForEach-Object { [ordered]@{ path=$_.FullName;sha256=(Get-ArchiveHash $_) } })
        $licenseHash = Get-ArchiveHash $zip.GetEntry('LICENSE')
    } finally { $zip.Dispose() }
    $symbolsHash = $null
    if ($package.kind -eq 'runtime') {
        $zip = [IO.Compression.ZipFile]::OpenRead($symbols)
        try { foreach ($tfm in $package.targetFrameworks) { if (-not $zip.GetEntry("lib/$tfm/$assembly.pdb")) { throw "Missing symbols $tfm" } } } finally { $zip.Dispose() }
        $symbolsHash = (Get-FileHash $symbols -Algorithm SHA256).Hash.ToLowerInvariant()
    } elseif (Test-Path $symbols) { $symbolsHash = (Get-FileHash $symbols -Algorithm SHA256).Hash.ToLowerInvariant() }
    $evidence += [ordered]@{ id=$package.id; kind=$package.kind; version=$CandidateVersion; assemblyName=$assembly; package=[IO.Path]::GetFileName($nupkg); sha256=(Get-FileHash $nupkg -Algorithm SHA256).Hash.ToLowerInvariant(); symbols=if ($symbolsHash) {[IO.Path]::GetFileName($symbols)} else {$null}; symbolsSha256=$symbolsHash; license=[ordered]@{ expression=$package.licenseExpression; file='LICENSE'; sha256=$licenseHash }; dependencies=$declaredDeps; assets=$entries; binaries=$binaryHashes; analyzers=$analyzerHashes; baselineVersion=$package.baselineVersion }
}
$result = [ordered]@{ schemaVersion=1; candidateVersion=$CandidateVersion; manifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); packages=$evidence; buildTooling=@($buildTooling.Values | Sort-Object producer) }
if ($InspectOnly) {
    $null = Test-RecordedBuildTooling $inventory $recorded $CandidateVersion
    if ($recorded.manifestSha256 -ne $result.manifestSha256) { throw 'Inventory hash changed since packing' }
    Assert-SameSet @($recorded.packages.id) @($evidence.id) 'Recorded inventory identities mismatch'
    foreach ($entry in $evidence) {
        $old = @($recorded.packages | Where-Object id -EQ $entry.id)
        if ($old.Count -ne 1 -or $old[0].sha256 -ne $entry.sha256 -or $old[0].symbolsSha256 -ne $entry.symbolsSha256) { throw "Package hash mismatch: $($entry.id)" }
    }
} else { $null = Test-RecordedBuildTooling $inventory $result $CandidateVersion; $result | ConvertTo-Json -Depth 14 | Set-Content $hashFile }
Write-Host "PASS package inventory, assets, dependencies and hashes: $($inventory.packages.Count) packages ($CandidateVersion)"

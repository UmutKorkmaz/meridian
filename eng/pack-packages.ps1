[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/packages',
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$')][string] $CandidateVersion,
    [string] $Manifest = 'eng/packages.json',
    [switch] $InspectOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repo $Manifest }
$json = Get-Content -Raw $manifestPath
if (-not (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'packages.schema.json'))) { throw 'Invalid package manifest' }
$inventory = $json | ConvertFrom-Json
$feed = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Force $feed
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
function Invoke-Dotnet([string[]] $Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
function Evaluate([string] $Project, [string[]] $Properties, [string[]] $Items = @(), [string[]] $Extra = @()) {
    $arguments = @('msbuild', $Project, '-nologo', "-p:Configuration=$Configuration", "-getProperty:$($Properties -join ',')") + $Extra
    if ($Items.Count) { $arguments += "-getItem:$($Items -join ',')" }
    $output = & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Could not evaluate $Project" }
    return ($output -join "`n" | ConvertFrom-Json)
}
function Read-ZipText($Archive, [string] $Path) {
    $entry = $Archive.GetEntry($Path)
    if (-not $entry) { throw "Missing archive asset $Path" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
$ids = @()
$evidence = @()
foreach ($package in $inventory.packages) {
    if ($ids -contains $package.id) { throw "Duplicate package $($package.id)" }
    $ids += $package.id
    $analyzerTargets = @{}
    $project = Join-Path $repo $package.project
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing project $project" }
    if (-not $InspectOnly) {
        $evaluated = Evaluate $project @('PackageId', 'IsPackable', 'TargetFrameworks', 'TargetFramework', 'PackageValidationBaselineVersion', $package.versionSource) @('PackageReference', 'ProjectReference')
        $p = $evaluated.Properties
        if ($p.PackageId -ne $package.id -or $p.IsPackable -ne 'true') { throw "Package identity mismatch: $project" }
        if (-not $p.($package.versionSource)) { throw "Missing version property: $($package.versionSource)" }
        $tfms = if ($p.TargetFrameworks) { @($p.TargetFrameworks -split ';') } else { @($p.TargetFramework) }
        if (Compare-Object $tfms @($package.targetFrameworks)) { throw "TargetFramework mismatch: $project" }
        if ($p.PackageValidationBaselineVersion -ne $package.baselineVersion) { throw "API baseline mismatch: $project" }
        $actualDeps = @($evaluated.Items.PackageReference | Where-Object { $_.PrivateAssets -ne 'all' } | ForEach-Object { "$($_.Identity)=$($_.Version)" })
        $expectedDeps = @($package.directPackageDependencies | ForEach-Object { "$($_.id)=$($_.version)" })
        if (Compare-Object $actualDeps $expectedDeps) { throw "Direct dependency mismatch: $project" }
        foreach ($analyzer in $package.analyzerPaths) {
            $producer = [IO.Path]::GetFullPath((Join-Path $repo $analyzer.project))
            $references = @($evaluated.Items.ProjectReference | Where-Object { [IO.Path]::GetFullPath($_.FullPath) -eq $producer -and $_.ReferenceOutputAssembly -eq 'false' -and $_.OutputItemType -eq 'Analyzer' })
            if ($references.Count -ne 1) { throw "Analyzer producer is not an evaluated analyzer reference: $producer" }
            $target = Evaluate $producer @('TargetPath', 'TargetFramework') @() @("-p:Configuration=$Configuration")
            if ($target.Properties.TargetFramework -ne 'netstandard2.0' -or [IO.Path]::GetFileName($target.Properties.TargetPath) -ne [IO.Path]::GetFileName($analyzer.packagePath)) { throw 'Analyzer target mismatch' }
            $analyzerTargets[$analyzer.packagePath] = $target.Properties.TargetPath
        }
        Push-Location $repo
        try { Invoke-Dotnet @('pack', $project, '-c', $Configuration, '-o', $feed, "-p:Version=$CandidateVersion", "-p:PackageVersion=$CandidateVersion") } finally { Pop-Location }
    }
    $nupkg = Join-Path $feed "$($package.id).$CandidateVersion.nupkg"
    $symbols = Join-Path $feed "$($package.id).$CandidateVersion.snupkg"
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        [xml] $nuspec = Read-ZipText $zip "$($package.id).nuspec"
        $metadata = $nuspec.package.metadata
        if ($metadata.id -ne $package.id -or $metadata.version -ne $CandidateVersion) { throw 'Nuspec identity mismatch' }
        if ($metadata.license.type -ne 'expression' -or $metadata.license.InnerText -ne 'MIT' -or $metadata.readme -ne 'README.md') { throw 'Missing README/license metadata' }
        foreach ($asset in @('README.md', 'LICENSE')) { if (-not $zip.GetEntry($asset)) { throw "Missing $asset" } }
        foreach ($tfm in $package.targetFrameworks) { if (-not $zip.GetEntry("lib/$tfm/$($package.id).dll")) { throw "Missing runtime $tfm" } }
        $groups = @($metadata.dependencies.group)
        if (Compare-Object @($groups | ForEach-Object targetFramework) @($package.targetFrameworks)) { throw 'Dependency group mismatch' }
        foreach ($group in $groups) {
            $actual = @($group.dependency | ForEach-Object { "$($_.id)=$($_.version)" })
            $expected = @($package.directPackageDependencies | ForEach-Object { "$($_.id)=$($_.version)" })
            if (Compare-Object $actual $expected) { throw "Nuspec dependency mismatch for $($group.targetFramework)" }
        }
        $assets = @($zip.Entries | Where-Object FullName -Like 'analyzers/*' | ForEach-Object FullName)
        $expectedAssets = @($package.analyzerPaths | ForEach-Object packagePath)
        if (($assets.Count -ne $expectedAssets.Count) -or ($assets.Count -gt 0 -and (Compare-Object $assets $expectedAssets))) { throw 'Analyzer archive layout mismatch' }
        $analyzerHashes = @()
        foreach ($analyzer in $package.analyzerPaths) {
            $stream = $zip.GetEntry($analyzer.packagePath).Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
            if (-not $InspectOnly -and $hash -ne (Get-FileHash $analyzerTargets[$analyzer.packagePath] -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Packed analyzer differs from evaluated producer TargetPath' }
            $analyzerHashes += @{ project=$analyzer.project; packagePath=$analyzer.packagePath; sha256=$hash }
        }
        $entries = @($zip.Entries | ForEach-Object FullName)
    } finally { $zip.Dispose() }
    $zip = [IO.Compression.ZipFile]::OpenRead($symbols)
    try {
        foreach ($tfm in $package.targetFrameworks) { if (-not $zip.GetEntry("lib/$tfm/$($package.id).pdb")) { throw "Missing symbols $tfm" } }
    } finally { $zip.Dispose() }
    $evidence += [ordered]@{ id=$package.id; version=$CandidateVersion; package=[IO.Path]::GetFileName($nupkg); sha256=(Get-FileHash $nupkg -Algorithm SHA256).Hash.ToLowerInvariant(); symbols=[IO.Path]::GetFileName($symbols); symbolsSha256=(Get-FileHash $symbols -Algorithm SHA256).Hash.ToLowerInvariant(); assets=$entries; analyzers=$analyzerHashes }
}
$hashFile = Join-Path $feed "package-manifest.$CandidateVersion.json"
$result = [ordered]@{ schemaVersion=1; manifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); packages=$evidence }
if ($InspectOnly) {
    $recorded = Get-Content -Raw $hashFile | ConvertFrom-Json
    if ($recorded.manifestSha256 -ne $result.manifestSha256) { throw 'Inventory hash changed since packing' }
    foreach ($entry in $evidence) {
        $old = @($recorded.packages | Where-Object id -EQ $entry.id)
        if ($old.Count -ne 1 -or $old[0].sha256 -ne $entry.sha256 -or $old[0].symbolsSha256 -ne $entry.symbolsSha256) { throw "Package hash mismatch: $($entry.id)" }
    }
} else { $result | ConvertTo-Json -Depth 12 | Set-Content $hashFile }
Write-Host "PASS package inventory, assets, dependencies and hashes: $($ids.Count) packages ($CandidateVersion)"

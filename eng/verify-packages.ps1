[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Feed,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$')][string] $CandidateVersion,
    [string] $Framework,
    [string] $SdkVersion,
    [string] $Manifest = 'eng/packages.json',
    [string] $BaselineFeed,
    [string] $OutputDirectory = 'artifacts/package-consumers'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'package-inventory.psm1') -Force -DisableNameChecking
$repo = Split-Path -Parent $PSScriptRoot
$Feed = [IO.Path]::GetFullPath($Feed)
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repo $Manifest }
& (Join-Path $PSScriptRoot 'pack-packages.ps1') -OutputDirectory $Feed -CandidateVersion $CandidateVersion -Manifest $manifestPath -InspectOnly
$inventory = Read-PackageInventory $repo $manifestPath
if ([bool]$Framework -ne [bool]$SdkVersion) { throw 'Framework and SdkVersion must be specified together' }
$candidateLanes = if ($Framework) { @([pscustomobject]@{ framework=$Framework; sdkVersion=$SdkVersion }) } else { @($inventory.compilerHosts) }
# Published 2.1.3 old-host failures remain A4 historical evidence. The baseline
# operational comparison runs on SDK 10; all candidate compiler hosts must pass.
$baselineConsumers = @($inventory.consumers | Where-Object baselineComparison)
$baselinePackages = @(Get-PackageClosure $inventory @($baselineConsumers.packageIds | Select-Object -Unique))
$referenceLane = @($inventory.compilerHosts | Where-Object framework -EQ 'net10.0')
if ($baselineConsumers.Count -and $referenceLane.Count -ne 1) { throw 'Exactly one SDK 10 reference lane is required' }
$lanes = @()
if ($baselineConsumers.Count) { $lanes += [pscustomobject]@{ framework=$referenceLane[0].framework; sdkVersion=$referenceLane[0].sdkVersion; baselineOnly=$true } }
$lanes += @($candidateLanes | ForEach-Object { [pscustomobject]@{framework=$_.framework;sdkVersion=$_.sdkVersion;baselineOnly=$false} })
$common = @{}
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$run = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$CandidateVersion/$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))"
$null = New-Item -ItemType Directory -Force $run
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.targets')
if ($baselinePackages.Count -and -not $BaselineFeed) {
    $BaselineFeed = Join-Path $run 'baseline-feed'
    $null = New-Item -ItemType Directory -Force $BaselineFeed
    foreach ($package in $baselinePackages) {
        $id = $package.id.ToLowerInvariant()
        $version = $package.baselineVersion
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile (Join-Path $BaselineFeed "$id.$version.nupkg")
    }
}
if ($BaselineFeed) { $BaselineFeed = [IO.Path]::GetFullPath($BaselineFeed) }
$baselineHashes = @($baselinePackages | ForEach-Object {
    $path = Join-Path $BaselineFeed "$($_.id.ToLowerInvariant()).$($_.baselineVersion).nupkg"
    [ordered]@{ id=$_.id; version=$_.baselineVersion; file=[IO.Path]::GetFileName($path); sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
ConvertTo-Json -InputObject $baselineHashes -Depth 5 | Set-Content (Join-Path $run 'baseline-hashes.json')
function Execute([string[]] $Arguments, [string] $Log) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $process.StartInfo.WorkingDirectory = (Get-Location).ProviderPath
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.Environment['DOTNET_ROLL_FORWARD'] = 'LatestPatch'
    $process.StartInfo.Environment['DOTNET_ROLL_FORWARD_TO_PRERELEASE'] = '0'
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $process.StartInfo.ArgumentList.Add($arg) }
    try {
        $null = $process.Start()
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $out.GetAwaiter().GetResult() + $err.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Log, "command=dotnet $($Arguments -join ' ')`nexit=$($process.ExitCode)`n$output")
        Write-Host $output
        if ($process.ExitCode -ne 0) { throw "Consumer command failed ($($process.ExitCode)); $Log" }
        return $output
    } finally { $process.Dispose() }
}
$results = @()
$components = @{}
$graphs = @()
foreach ($lane in $lanes) {
    if (@($inventory.compilerHosts | Where-Object { $_.framework -eq $lane.framework -and $_.sdkVersion -eq $lane.sdkVersion }).Count -ne 1) { throw 'Invalid compiler lane: must match a pinned manifest host' }
    $dir = Join-Path $run "$($lane.framework)-$($lane.sdkVersion)-$($lane.baselineOnly)"
    $null = New-Item -ItemType Directory -Force $dir
    @{ sdk=@{ version=$lane.sdkVersion; rollForward='disable'; allowPrerelease=$false } } | ConvertTo-Json | Set-Content (Join-Path $dir 'global.json')
    Push-Location $dir
    try {
        $sdk = (Execute @('--version') (Join-Path $dir 'sdk.log')).Trim()
        if ($sdk -ne $lane.sdkVersion) { throw "Wrong actual SDK: $sdk" }
        $null = Execute @('--info') (Join-Path $dir 'host.log')
        $installed = Execute @('--list-sdks') (Join-Path $dir 'installed-sdks.log')
        $sdkLine = @($installed -split "`r?`n" | Where-Object { $_.StartsWith("$sdk ") })
        if ($sdkLine.Count -ne 1 -or $sdkLine[0] -notmatch '\[(.+)\]$') { throw 'Cannot locate selected SDK compiler' }
        $compiler = Join-Path $Matches[1] "$sdk/Roslyn/bincore/csc.dll"
        $compilerVersion = (Execute @('exec',$compiler,'-version') (Join-Path $dir 'compiler.log')).Trim()
        $consumers = if ($lane.baselineOnly) { $baselineConsumers } else { @($inventory.consumers) }
        foreach ($consumer in $consumers) {
            $closure = @(Get-PackageClosure $inventory $consumer.packageIds)
            $variants = if ($lane.baselineOnly) { @('baseline') } else { @($consumer.variants) }
            foreach ($variant in $variants) {
                $versions = @(if ($variant -eq 'baseline') { $closure.baselineVersion | Select-Object -Unique } else { $CandidateVersion })
                if ($versions.Count -ne 1) { throw "Consumer closure needs one shared package version: $($consumer.id)" }
                $version = $versions[0]
                $sourceFeed = if ($variant -eq 'baseline') { $BaselineFeed } else { $Feed }
                $work = Join-Path $dir "$variant-$($consumer.id)"
                $null = New-Item -ItemType Directory -Force $work
                @{ sdk=@{ version=$lane.sdkVersion; rollForward='disable'; allowPrerelease=$false } } | ConvertTo-Json | Set-Content (Join-Path $work 'global.json')
                $sourceDir = Split-Path (Resolve-RepositoryPath $repo $consumer.project)
                $fixture = @()
                foreach ($input in $consumer.sourceFiles) {
                    $source = Resolve-RepositoryPath $repo $input
                    $relative = [IO.Path]::GetRelativePath($sourceDir,$source)
                    $destination = Join-Path $work $relative
                    $null = New-Item -ItemType Directory -Force (Split-Path $destination)
                    Copy-Item -LiteralPath $source -Destination $destination
                    $fixture += [ordered]@{ source=$input; file=$relative; sha256=(Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
                }
                ConvertTo-Json -InputObject $fixture -Depth 5 | Set-Content (Join-Path $work 'fixture-hashes.json')
                $escapedFeed = [Security.SecurityElement]::Escape($sourceFeed)
                $patterns = ($inventory.packages | ForEach-Object { "<package pattern=`"$($_.id)`" />" }) -join ''
                "<configuration><packageSources><clear/><add key=`"candidate`" value=`"$escapedFeed`"/><add key=`"dependencies`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"candidate`">$patterns</packageSource><packageSource key=`"dependencies`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" | Set-Content (Join-Path $work 'NuGet.Config')
                $packages = Join-Path $work 'packages'
                $project = Join-Path $work ([IO.Path]::GetFileName($consumer.project))
                $properties = @("-p:ConsumerFramework=$($lane.framework)","-p:MeridianPackageVersion=$version","-p:BaselineConsumer=$($variant -eq 'baseline')","-p:MigratedConsumer=$($variant -eq 'migrated')","-p:RestorePackagesPath=$packages")
                $evaluated = Invoke-ProjectEvaluation $dotnet $project @('LangVersion','TreatWarningsAsErrors','AssemblyName') @('PackageReference','ProjectReference') $properties
                if (@($evaluated.Items.ProjectReference).Count) { throw "NuGet consumer cannot load source ProjectReference: $($consumer.id)" }
                if ($evaluated.Properties.LangVersion -notin @('12','12.0') -or $evaluated.Properties.TreatWarningsAsErrors -ne 'true') { throw 'Consumer must enforce CSharp12 and warnings as errors' }
                $roots = @($evaluated.Items.PackageReference | Where-Object { $_.Identity -in $inventory.packages.id })
                Assert-SameSet @($roots.Identity) @($consumer.packageIds) "Consumer root dependency mismatch: $($consumer.id)"
                foreach ($root in $roots) { if ($root.Version -ne "[$version]") { throw "Consumer package version must be exact: $($root.Identity)" } }
                $null = Execute (@('restore',$project,'--configfile',(Join-Path $work 'NuGet.Config'),'--no-cache') + $properties) (Join-Path $work 'restore.log')
                $assetsPath = Join-Path $work 'obj/project.assets.json'
                $assets = Get-Content -Raw $assetsPath | ConvertFrom-Json
                $actualIds = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name.Split('/')[0] -in $inventory.packages.id } | ForEach-Object { $_.Name.Split('/')[0] })
                Assert-SameSet $actualIds @($closure.id) "Restored inventory closure mismatch: $($consumer.id)"
                foreach ($package in $closure) {
                    $id = $package.id.ToLowerInvariant()
                    $restored = Join-Path $packages "$id/$version/$id.$version.nupkg"
                    $expectedPath = Join-Path $sourceFeed "$($package.id).$version.nupkg"
                    if (-not (Test-Path $expectedPath)) { $expectedPath = Join-Path $sourceFeed "$id.$version.nupkg" }
                    if ((Get-FileHash $restored -Algorithm SHA256).Hash -ne (Get-FileHash $expectedPath -Algorithm SHA256).Hash) { throw "Restored package hash differs: $id" }
                }
                foreach ($component in (Read-RestoredComponents $assets $packages)) { $components["$($component.id)/$($component.version)"] = $component }
                $graph = @($assets.targets.PSObject.Properties | ForEach-Object { $target=$_; @($target.Value.PSObject.Properties | ForEach-Object { [ordered]@{ identity=$_.Name; dependencies=$_.Value.dependencies } }) })
                $graphs += [ordered]@{consumer=$consumer.id;variant=$variant;framework=$lane.framework;assetsSha256=(Get-FileHash $assetsPath -Algorithm SHA256).Hash.ToLowerInvariant();dependencies=$graph}
                $null = Execute (@('build',$project,'-c','Release','--no-restore','-warnaserror') + $properties) (Join-Path $work 'build.log')
                $binary = Join-Path $work "bin/Release/$($lane.framework)/$($evaluated.Properties.AssemblyName).dll"
                $output = Execute @($binary) (Join-Path $work 'run.log')
                $runtimeLine = @($output -split "`r?`n" | Where-Object { $_.StartsWith('HOST runtime=') })
                $family = $lane.framework.Substring(3).Split('.')[0]
                if ($runtimeLine.Count -ne 1 -or $runtimeLine[0] -notmatch "^HOST runtime=$family\.") { throw "Wrong actual consumer runtime: $runtimeLine" }
                $lines = @($output -split "`r?`n" | Where-Object { $_.StartsWith('COMMON ') })
                if (-not $lines.Count) { throw "Consumer has no asserted COMMON output: $($consumer.id)" }
                if ($consumer.baselineComparison) {
                    if ($variant -eq 'baseline') { $common[$consumer.id] = $lines } else { Assert-SameSet @($common[$consumer.id]) $lines "Old/new common output mismatch: $($consumer.id)" }
                }
                $results += [ordered]@{ framework=$lane.framework; sdkVersion=$sdk; compilerVersion=$compilerVersion; runtime=$runtimeLine[0]; consumer=$consumer.id; variant=$variant; baselineComparison=$consumer.baselineComparison; packageVersion=$version; packageIds=@($closure.id); exit=0; commonOutput=$lines; binarySha256=(Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant(); directory=$work }
                ConvertTo-Json -InputObject $results -Depth 10 | Set-Content (Join-Path $run 'consumer-results.json')
                Write-Host "PASS $variant $($consumer.id) SDK=$sdk framework=$($lane.framework) CSharp=12 warningsAsErrors=true hashes=matched"
            }
        }
    } finally { Pop-Location }
}
# Pack-time producer evidence was validated by InspectOnly above; never restamp live obj inputs.
$packageRecord = Get-Content -Raw (Join-Path $Feed "package-manifest.$CandidateVersion.json") | ConvertFrom-Json
$buildTooling = @(Test-RecordedBuildTooling $inventory $packageRecord $CandidateVersion)
# This is a Meridian schemaVersion1 SBOM inventory, not a claimed CycloneDX/SPDX document.
$sbom = [ordered]@{schemaVersion=1;format='meridian-restored-package-inventory';manifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();candidateVersion=$CandidateVersion;components=@($components.Values | Sort-Object id,version);consumerGraphs=$graphs;distributedPackages=$packageRecord.packages;buildTooling=$buildTooling;buildToolingScope='Private build tools are recorded separately from consumer distribution graphs'}
$sbom | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $run 'sbom-inventory.json')
Write-Host "PASS packaged compiler consumers: $($results.Count); evidence=$run"

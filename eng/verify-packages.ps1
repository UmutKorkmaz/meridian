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
$repo = Split-Path -Parent $PSScriptRoot
$Feed = [IO.Path]::GetFullPath($Feed)
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repo $Manifest }
# Verify against the hashes produced at pack time, without rebuilding the candidate.
& (Join-Path $PSScriptRoot 'pack-packages.ps1') -OutputDirectory $Feed -CandidateVersion $CandidateVersion -Manifest $manifestPath -InspectOnly
$inventory = Get-Content -Raw $manifestPath | ConvertFrom-Json
if ([bool]$Framework -ne [bool]$SdkVersion) { throw 'Framework and SdkVersion must be specified together' }
$candidateLanes = if ($Framework) { @([pscustomobject]@{ framework=$Framework; sdkVersion=$SdkVersion }) } else { @($inventory.compilerHosts) }
# 2.1.3 bundles Roslyn 5.3 and cannot load on the older hosts. Its unsuppressed
# historical failures are retained in the A4 compatibility evidence. Operational
# output comparison uses SDK 10; every requested candidate lane still must pass.
$referenceLane = @($inventory.compilerHosts | Where-Object framework -EQ 'net10.0')
if ($referenceLane.Count -ne 1) { throw 'Exactly one SDK 10 reference lane is required' }
$lanes = @([pscustomobject]@{ framework=$referenceLane[0].framework; sdkVersion=$referenceLane[0].sdkVersion; baselineOnly=$true }) + $candidateLanes
$common = @{}
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$run = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$CandidateVersion/$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))"
$null = New-Item -ItemType Directory -Force $run
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.targets')
if (-not $BaselineFeed) {
    $BaselineFeed = Join-Path $run 'baseline-feed'
    $null = New-Item -ItemType Directory -Force $BaselineFeed
    foreach ($package in $inventory.packages | Where-Object baselineVersion) {
        $id = $package.id.ToLowerInvariant()
        $version = $package.baselineVersion
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile (Join-Path $BaselineFeed "$id.$version.nupkg")
    }
}
$BaselineFeed = [IO.Path]::GetFullPath($BaselineFeed)
$baselineVersions = @($inventory.packages | Where-Object baselineVersion | Select-Object -ExpandProperty baselineVersion -Unique)
if ($baselineVersions.Count -ne 1) { throw 'Core consumer fixtures require one shared stable baseline version' }
$baselineHashes = @($inventory.packages | Where-Object baselineVersion | ForEach-Object {
    $path = Join-Path $BaselineFeed "$($_.id.ToLowerInvariant()).$($_.baselineVersion).nupkg"
    [ordered]@{ id=$_.id; version=$_.baselineVersion; file=[IO.Path]::GetFileName($path); sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$baselineHashes | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $run 'baseline-hashes.json')
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
foreach ($lane in $lanes) {
    if ($lane.framework -notin @($inventory.compilerHosts.framework) -or $lane.sdkVersion -notmatch '^\d+\.\d+\.\d+$' -or $lane.sdkVersion.Split('.')[0] -ne $lane.framework.Substring(3).Split('.')[0]) { throw 'Invalid compiler lane' }
    $dir = Join-Path $run "$($lane.framework)-$($lane.sdkVersion)-$($lane.baselineOnly -eq $true)"
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
        $compilerVersion = (Execute @('exec', $compiler, '-version') (Join-Path $dir 'compiler.log')).Trim()
        $variants = if ($lane.baselineOnly) { @('baseline') } else { @('legacy', 'migrated') }
        foreach ($variant in $variants) {
            $version = if ($variant -eq 'baseline') { $baselineVersions[0] } else { $CandidateVersion }
            $sourceFeed = if ($variant -eq 'baseline') { $BaselineFeed } else { $Feed }
            foreach ($name in @('Legacy', 'Generated')) {
                if ($variant -eq 'migrated' -and $name -eq 'Generated') { continue }
                $consumer = "Meridian.${name}Consumer"
                $work = Join-Path $dir "$variant-$consumer"
                $null = New-Item -ItemType Directory -Force $work
                @{ sdk=@{ version=$lane.sdkVersion; rollForward='disable'; allowPrerelease=$false } } | ConvertTo-Json | Set-Content (Join-Path $work 'global.json')
                # Copy input fixtures, including their hashes, before any restore/build.
                Copy-Item (Join-Path $repo "tests/consumers/$consumer/Program.cs") $work
                Copy-Item (Join-Path $repo "tests/consumers/$consumer/$consumer.csproj") $work
                $fixture = @(Get-ChildItem $work -File | ForEach-Object { @{ file=$_.Name; sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
                $fixture | ConvertTo-Json | Set-Content (Join-Path $work 'fixture-hashes.json')
                # No inherited repository sources; exact Meridian IDs route only to the local feed.
                $escapedFeed = [Security.SecurityElement]::Escape($sourceFeed)
                $patterns = ($inventory.packages | ForEach-Object { "<package pattern=`"$($_.id)`" />" }) -join ''
                "<configuration><packageSources><clear/><add key=`"candidate`" value=`"$escapedFeed`"/><add key=`"dependencies`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"candidate`">$patterns</packageSource><packageSource key=`"dependencies`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" | Set-Content (Join-Path $work 'NuGet.Config')
                $packages = Join-Path $work 'packages'
                $project = Join-Path $work "$consumer.csproj"
                $properties = @("-p:ConsumerFramework=$($lane.framework)", "-p:MeridianPackageVersion=$version", "-p:BaselineConsumer=$($variant -eq 'baseline')", "-p:MigratedConsumer=$($variant -eq 'migrated')", "-p:RestorePackagesPath=$packages")
                $null = Execute (@('restore', $project, '--configfile', (Join-Path $work 'NuGet.Config'), '--no-cache') + $properties) (Join-Path $work 'restore.log')
                foreach ($package in $inventory.packages) {
                    $id = $package.id.ToLowerInvariant()
                    $restored = Join-Path $packages "$id/$version/$id.$version.nupkg"
                    $expected = Get-FileHash (Join-Path $sourceFeed "$($package.id).$version.nupkg") -Algorithm SHA256 -ErrorAction SilentlyContinue
                    if (-not $expected) { $expected = Get-FileHash (Join-Path $sourceFeed "$id.$version.nupkg") -Algorithm SHA256 }
                    if ((Get-FileHash $restored -Algorithm SHA256).Hash -ne $expected.Hash) { throw "Restored package hash differs: $id" }
                }
                $null = Execute (@('build', $project, '-c', 'Release', '--no-restore', '-warnaserror') + $properties) (Join-Path $work 'build.log')
                $output = Execute @((Join-Path $work "bin/Release/$($lane.framework)/$consumer.dll")) (Join-Path $work 'run.log')
                $runtimeLine = @($output -split "`r?`n" | Where-Object { $_.StartsWith('HOST runtime=') })
                $family = $lane.framework.Substring(3).Split('.')[0]
                if ($runtimeLine.Count -ne 1 -or $runtimeLine[0] -notmatch "^HOST runtime=$family\.") { throw "Wrong actual consumer runtime: $runtimeLine" }
                $lines = @($output -split "`r?`n" | Where-Object { $_.StartsWith('COMMON ') })
                if ($variant -eq 'baseline') { $common[$name] = $lines } elseif (Compare-Object @($common[$name]) $lines) { throw "Old/new common output mismatch: $consumer" }
                $results += @{ framework=$lane.framework; sdkVersion=$sdk; compilerVersion=$compilerVersion; runtime=$runtimeLine[0]; consumer=$consumer; variant=$variant; packageVersion=$version; exit=0; commonOutput=$lines; directory=$work }
                Write-Host "PASS $variant $consumer SDK=$sdk framework=$($lane.framework) CSharp=12 warningsAsErrors=true hashes=matched"
            }
        }
    } finally { Pop-Location }
}
$results | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'consumer-results.json')
Write-Host "PASS packaged compiler consumers: $($results.Count); evidence=$run"

[CmdletBinding()]
param(
    # Only Release is accepted; the packed candidate and every reused verifier are Release builds.
    [ValidateSet('Release')][string] $Configuration = 'Release',
    # Directory holding the packed candidate (packages plus package-manifest.<version>.json).
    [Parameter(Mandatory)][string] $Feed,
    # Where lane evidence (lanes/<lane>/lane-result.json), the run context and the summary are written or read.
    [Parameter(Mandatory)][string] $EvidenceDirectory,
    # Run       execute every lane that can run on this host, collect its evidence, then evaluate (default).
    # Collect   turn the raw outputs of one lane that something else already produced (CI job, E4d campaign) into evidence.
    # Evaluate  judge existing evidence for this host: lanes bound to another OS/architecture without evidence are UNAVAILABLE.
    # Aggregate judge existing evidence with no host: every required lane must have evidence (CI aggregation, final campaign).
    [ValidateSet('Run', 'Collect', 'Evaluate', 'Aggregate')][string] $Mode = 'Run',
    [ValidatePattern('\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z')][string] $CandidateVersion,
    # Intent for a stable local candidate; never authorization to publish.
    [switch] $ReleasePreparation,
    [string] $Policy = 'eng/required-checks.json',
    # Lane ids. Run: restrict the run (default: every lane this host can execute). Collect: exactly one.
    [string[]] $Lane = @(),
    # Collect: raw output files or directories of the lane, the exit code of the command that produced them (required;
    # there is no fail-open default) and the services the lane used (name=auto resolves the image from the container).
    [string[]] $Raw = @(),
    [Nullable[int]] $ProducerExit,
    [string[]] $Service = @(),
    # github-actions is accepted only inside GitHub Actions (GITHUB_ACTIONS=true) with -RunId equal to GITHUB_RUN_ID.
    [ValidateSet('local', 'github-actions')][string] $Context = 'local',
    [string] $RunId,
    [string] $SourceCommit,
    # Evaluate/Aggregate: SHA-256 of the package manifest the pack step produced; a feed with another manifest fails.
    [ValidatePattern('^[0-9a-f]{64}$')][string] $PackageManifestSha256,
    # PENDING (exit 3) instead of FAIL (exit 1) when the ONLY non-passing lanes are bound to another OS/architecture and
    # have no evidence. A distinct state, never success and never release eligible. Rejected with -Mode Aggregate.
    [switch] $AllowPending,
    # Run/Collect refuse an uncommitted working tree because evidence must name the exact source it came from. With this
    # switch they run anyway (tooling self-tests), but the evidence records source.dirty=true and every such lane FAILs.
    [switch] $AllowDirtySource,
    # Aggregate: judge only the lanes the CI workflows define (a scoped verdict). The candidate verdict still needs every lane.
    [switch] $CiLanesOnly
)
# Exit codes: 0 PASS (whole candidate) or SCOPED-PASS (named lanes only; summary.json wholeCandidate=false and
# releaseEligible=false), 1 FAIL (failed, missing or unavailable lane evidence, inconsistent candidate), 2 invalid policy
# or arguments, 3 PENDING (-AllowPending only). Missing infrastructure is never converted into success: a lane without
# evidence is MISSING or UNAVAILABLE, a required service that is not reachable fails its lane, and a skipped mandatory
# case fails its lane. The evaluator re-derives every lane from its retained raw files. See docs/governance/required-checks.md.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'package-inventory.psm1') -Force -DisableNameChecking
$repo = Split-Path -Parent $PSScriptRoot
$Feed = Resolve-OutputDirectory $repo $Feed
$EvidenceDirectory = Resolve-OutputDirectory $repo $EvidenceDirectory
$policyPath = Resolve-OutputDirectory $repo $Policy
# Paths inside the policy (inventory, baselines, solution) are relative to the repository whose eng/ holds the policy.
$policyRepo = Split-Path -Parent (Split-Path -Parent $policyPath)

if ($Context -eq 'github-actions' -and ($env:GITHUB_ACTIONS -ne 'true' -or -not $RunId -or $RunId -ne $env:GITHUB_RUN_ID)) {
    Write-Host 'FAIL: -Context github-actions is only valid inside GitHub Actions with -RunId equal to GITHUB_RUN_ID.'
    exit 2
}
if ($Mode -eq 'Aggregate' -and $AllowPending) { Write-Host 'FAIL: -AllowPending applies to host evaluation only; Aggregate never accepts missing lanes.'; exit 2 }
if ($Mode -eq 'Collect' -and $null -eq $ProducerExit) { Write-Host 'FAIL: Collect needs -ProducerExit (the exit code of the command that produced the raw outputs).'; exit 2 }

function Get-Python {
    foreach ($name in 'python3', 'python') {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        & $command.Source -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { return $command.Source }
    }
    throw 'Python 3.9 or newer is required (python3 or python on PATH).'
}
$python = Get-Python

function Get-HostInfo {
    $os = if ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } elseif ($IsWindows) { 'win' } else { throw 'Unsupported host OS.' }
    $arch = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) { 'X64' { 'x64' } 'Arm64' { 'arm64' } default { $_.ToLowerInvariant() } }
    return [pscustomobject]@{ os = $os; arch = $arch }
}
$hostInfo = Get-HostInfo

function Invoke-Native([string] $File, [string[]] $Arguments, [string] $Log, [hashtable] $Environment = @{}) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $repo
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = [string] $Environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $out.GetAwaiter().GetResult() + $err.GetAwaiter().GetResult()
        $null = New-Item -ItemType Directory -Force (Split-Path $Log)
        [IO.File]::WriteAllText($Log, "command=$File $($Arguments -join ' ')`nexit=$($process.ExitCode)`n$text")
        Write-Host $text
        return $process.ExitCode
    } finally { $process.Dispose() }
}

function Get-SourceState {
    $commit = (& git -C $repo rev-parse HEAD).Trim()
    $dirty = @(& git -C $repo status --porcelain).Count -gt 0
    return [pscustomobject]@{ commit = $commit; dirty = $dirty }
}

function Resolve-CandidateVersion {
    if ($CandidateVersion) { return $CandidateVersion }
    $manifests = @(Get-ChildItem -LiteralPath $Feed -Filter 'package-manifest.*.json' -ErrorAction SilentlyContinue)
    if ($manifests.Count -ne 1) { throw "Feed must hold exactly one package-manifest.<version>.json (found $($manifests.Count)); pass -CandidateVersion." }
    return $manifests[0].Name.Substring('package-manifest.'.Length).Replace('.json', '')
}

function Invoke-Collect([string] $LaneId, [string[]] $RawPaths, [int] $Exit, [string] $Version, [string] $Commit, [bool] $Dirty, [string[]] $Services) {
    $arguments = @('-B', (Join-Path $PSScriptRoot 'lane_collect.py'), '--policy', $policyPath, '--lane-id', $LaneId, '--evidence', $EvidenceDirectory,
        '--feed', $Feed, '--candidate-version', $Version, '--source-commit', $Commit, '--context', $Context,
        '--host-os', $hostInfo.os, '--host-arch', $hostInfo.arch, '--producer-exit', "$Exit")
    if ($RunId) { $arguments += @('--run-id', $RunId) }
    if ($Dirty) { $arguments += '--source-dirty' }
    foreach ($path in $RawPaths) { $arguments += @('--raw', $path) }
    foreach ($item in $Services) { $arguments += @('--service', $item) }
    & $python @arguments | Out-Host
    return $LASTEXITCODE
}

function Invoke-Evaluate([string] $Version, [string] $Commit, [string[]] $Only = @()) {
    $arguments = @('-B', (Join-Path $PSScriptRoot 'candidate_gate.py'), 'evaluate', '--policy', $policyPath, '--feed', $Feed, '--evidence', $EvidenceDirectory,
        '--candidate-version', $Version, '--output', (Join-Path $EvidenceDirectory 'summary.json'))
    if ($Mode -eq 'Aggregate') { $arguments += '--aggregate' } else { $arguments += @('--host-os', $hostInfo.os, '--host-arch', $hostInfo.arch) }
    if ($AllowPending -and $Mode -ne 'Aggregate') { $arguments += '--allow-pending' }
    if ($Commit) { $arguments += @('--source-commit', $Commit) }
    if ($PackageManifestSha256) { $arguments += @('--package-manifest-sha256', $PackageManifestSha256) }
    foreach ($id in $Only) { $arguments += @('--only', $id) }
    if ($CiLanesOnly) { $arguments += '--ci-lanes' }
    & $python @arguments | Out-Host
    return $LASTEXITCODE
}

# ---- lane runners (Run mode) -------------------------------------------------------------------------------------
function Get-TestSuites($PolicyDocument, $LaneEntry) {
    return @($PolicyDocument.countProfiles.($LaneEntry.countsProfile).($LaneEntry.framework))
}

# A lane that names toolchainRootEnvironment runs every command with that isolated dotnet root and nothing else.
function Get-LaneEnvironment($LaneEntry) {
    if (-not $LaneEntry.toolchainRootEnvironment) { return @{} }
    $root = [Environment]::GetEnvironmentVariable($LaneEntry.toolchainRootEnvironment)
    if (-not $root) { throw "Lane $($LaneEntry.id) needs the isolated toolchain root named by `$env:$($LaneEntry.toolchainRootEnvironment)." }
    $root = Resolve-OutputDirectory $repo $root
    if (-not (Test-Path -LiteralPath (Join-Path $root 'dotnet'))) { throw "Isolated toolchain root $root has no dotnet host." }
    return @{ DOTNET_ROOT = $root; DOTNET_ROOT_ARM64 = $root; DOTNET_HOST_PATH = (Join-Path $root 'dotnet'); DOTNET_MULTILEVEL_LOOKUP = '0'
        VSTEST_DOTNET_ROOT_PATH = $root; PATH = "$root$([IO.Path]::PathSeparator)$env:PATH"; DOTNET_ROLL_FORWARD = 'LatestPatch'; DOTNET_ROLL_FORWARD_TO_PRERELEASE = '0' }
}

function Get-Dotnet([hashtable] $Environment) {
    if ($Environment.ContainsKey('DOTNET_HOST_PATH')) { return $Environment['DOTNET_HOST_PATH'] }
    return (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}

# Retained receipt of the SDK the repository selects and of every installed SDK and runtime of the dotnet the lane used.
function Write-LaneToolchain([string] $RawDirectory, [hashtable] $Environment) {
    $dotnet = Get-Dotnet $Environment
    $receipt = [ordered]@{ dotnetPath = $dotnet; dotnetRoot = $Environment['DOTNET_ROOT'] }
    foreach ($pair in @(@('sdkVersion', '--version'), @('sdks', '--list-sdks'), @('runtimes', '--list-runtimes'))) {
        $log = Join-Path $RawDirectory "toolchain-$($pair[1].TrimStart('-')).log"
        $null = Invoke-Native $dotnet @($pair[1]) $log $Environment
        $lines = @(Get-Content -LiteralPath $log | Select-Object -Skip 2 | Where-Object { $_.Trim() })
        if ($pair[0] -eq 'sdkVersion') { $receipt[$pair[0]] = $lines | Select-Object -First 1 }
        else { $receipt[$pair[0]] = $lines }
    }
    $receipt | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $RawDirectory 'lane-toolchain.json')
}

function Invoke-DotnetTests($PolicyDocument, $LaneEntry, [string] $RawDirectory, [hashtable] $Environment = @{}, [string[]] $Extra = @()) {
    $worst = 0
    $dotnet = Get-Dotnet $Environment
    foreach ($suite in Get-TestSuites $PolicyDocument $LaneEntry) {
        $arguments = @('test', "tests/$($suite.suite)/$($suite.suite).csproj", '-c', 'Release', '-f', $LaneEntry.framework, '--no-build',
            '--logger', "trx;LogFileName=$($suite.suite)-$($LaneEntry.framework).trx", '--results-directory', (Join-Path $RawDirectory 'trx')) + $Extra
        if ($suite.PSObject.Properties.Name -contains 'filter' -and $suite.filter) { $arguments += @('--filter', $suite.filter) }
        $exit = Invoke-Native $dotnet $arguments (Join-Path $RawDirectory "$($suite.suite).log") $Environment
        if ($exit -ne 0) { $worst = $exit }
    }
    return $worst
}

function Save-PreviousAttempt([string] $Path, [string] $HistoryName) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Previous attempt path must not be a symbolic link: $Path"
    }
    $history = Join-Path $EvidenceDirectory "history/$HistoryName"
    $null = New-Item -ItemType Directory -Force $history
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '-' + [Guid]::NewGuid().ToString('N')
    Move-Item -LiteralPath $Path -Destination (Join-Path $history $stamp) -ErrorAction Stop
}

function Merge-ProducerExit([int[]] $Codes) {
    foreach ($code in $Codes) { if ($code -ne 0) { return $code } }
    return 0
}

function Invoke-Lane($PolicyDocument, $LaneEntry, [string] $RawDirectory, [string] $Version) {
    # Keep prior bytes separate from the fresh canonical attempt and its counts.
    Save-PreviousAttempt $RawDirectory "work/$($LaneEntry.id.Replace('/', '__'))"
    $null = New-Item -ItemType Directory -Force $RawDirectory
    $environment = Get-LaneEnvironment $LaneEntry
    if ($LaneEntry.kind -ne 'native-aot') { Write-LaneToolchain $RawDirectory $environment }
    $dotnet = Get-Dotnet $environment
    switch ($LaneEntry.kind) {
        'runtime-tests' {
            $exit = Invoke-DotnetTests $PolicyDocument $LaneEntry $RawDirectory
            $sampleExit = Invoke-Native 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-samples.ps1'), '-Framework', $LaneEntry.framework, '-NoBuild', '-LogDirectory', (Join-Path $RawDirectory 'samples')) (Join-Path $RawDirectory 'run-samples.log')
            return Merge-ProducerExit @($exit, $sampleExit)
        }
        'provider' {
            switch ($LaneEntry.countsProfile) {
                'ef10' { return Invoke-Native 'pwsh' (@('-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-ef10-compatibility.ps1'), '-Feed', $Feed, '-CandidateVersion', $Version, '-OutputDirectory', (Join-Path $RawDirectory 'ef10')) + $releaseArguments) (Join-Path $RawDirectory 'ef10.log') }
                'http' {
                    $exit = Invoke-DotnetTests $PolicyDocument $LaneEntry $RawDirectory
                    $smoke = Invoke-Native 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-samples.ps1'), '-Framework', 'net10.0', '-IncludeHttp', '-NoBuild', '-LogDirectory', (Join-Path $RawDirectory 'samples')) (Join-Path $RawDirectory 'run-samples.log')
                    return Merge-ProducerExit @($exit, $smoke)
                }
                default { return Invoke-DotnetTests $PolicyDocument $LaneEntry $RawDirectory }
            }
        }
        'current-servicing' {
            # Build and execute with the isolated current-servicing root: its SDK selects buildSdk and it holds exactly one
            # runtime patch of the lane's family, which the evaluator checks from lane-toolchain.json.
            $build = Invoke-Native $dotnet @('build', 'Meridian.sln', '-c', 'Release', '-nologo', '-v:minimal', '-clp:NoSummary') (Join-Path $RawDirectory 'servicing-build.log') $environment
            if ($build -ne 0) { return $build }
            return Invoke-DotnetTests $PolicyDocument $LaneEntry $RawDirectory $environment
        }
        'package-consumers' {
            $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'verify-packages.ps1'), '-Feed', $Feed, '-CandidateVersion', $Version, '-Framework', $LaneEntry.framework, '-SdkVersion', $LaneEntry.sdk, '-OutputDirectory', (Join-Path $RawDirectory 'consumers'))
            $arguments += $releaseArguments
            if ($LaneEntry.toolchainRootEnvironment) { $arguments += @('-CurrentServicing', '-RequiredChecksPolicy', $policyPath) }
            if ($LaneEntry.consumerScope -eq 'partial') { $arguments += @('-ExcludeConsumerIds', (@($LaneEntry.omittedConsumerIds) -join ',')) }
            return Invoke-Native 'pwsh' $arguments (Join-Path $RawDirectory 'verify-packages.log') $environment
        }
        'native-aot' {
            $common = @('-Feed', $Feed, '-CandidateVersion', $Version, '-Framework', $LaneEntry.framework, '-SdkVersion', $LaneEntry.sdk, '-Rid', $LaneEntry.rid) + $releaseArguments
            $combined = Invoke-Native 'pwsh' (@('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-aot-smoke.ps1'), '-Combined') + $common + @('-OutputDirectory', (Join-Path $RawDirectory 'native'))) (Join-Path $RawDirectory 'combined.log')
            $mapping = Invoke-Native 'pwsh' (@('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-aot-smoke.ps1'), '-MappingOnly') + $common + @('-OutputDirectory', (Join-Path $RawDirectory 'native-mapping'))) (Join-Path $RawDirectory 'mapping-only.log')
            return Merge-ProducerExit @($combined, $mapping)
        }
        'property-counts' {
            $environment = @{ MERIDIAN_FSCHECK_MAX_TEST = '10000'; MERIDIAN_FSCHECK_COLLECTION_MAX_TEST = '500'; MERIDIAN_FSCHECK_REPLAY = '12345,11400714819323198485' }
            return Invoke-Native 'dotnet' @('test', 'tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj', '-c', 'Release', '-f', 'net10.0', '--no-build', '--filter', 'FullyQualifiedName~PropertyBasedTests', '--logger', 'console;verbosity=detailed', '--logger', 'trx;LogFileName=Meridian.Mapping.Tests-properties.trx', '--results-directory', (Join-Path $RawDirectory 'trx')) (Join-Path $RawDirectory 'property-output.log') $environment
        }
        'coverage' {
            # Every suite of the lane's profile runs unfiltered (real providers) with coverlet; TRX and Cobertura are both retained.
            $worst = 0
            foreach ($suite in Get-TestSuites $PolicyDocument $LaneEntry) {
                $exit = Invoke-Native 'dotnet' @('test', "tests/$($suite.suite)/$($suite.suite).csproj", '-c', 'Release', '-f', $LaneEntry.framework, '--no-build', '--collect:XPlat Code Coverage', '--logger', "trx;LogFileName=$($suite.suite)-coverage.trx", '--results-directory', (Join-Path $RawDirectory "cov/$($suite.suite)")) (Join-Path $RawDirectory "$($suite.suite).log")
                if ($exit -ne 0) { $worst = $exit }
            }
            $documents = @(Get-ChildItem (Join-Path $RawDirectory 'cov') -Recurse -Filter 'coverage.cobertura.xml' | Where-Object { $_.FullName -notmatch '[\\/]In[\\/]' } | ForEach-Object FullName)
            $gate = Invoke-Native $python (@('-B', (Join-Path $PSScriptRoot 'coverage_gate.py'), 'check', '--baseline', (Join-Path $policyRepo $PolicyDocument.coverage.baseline), '--repo', $repo, '--output', (Join-Path $RawDirectory 'coverage-gate.json')) + $documents) (Join-Path $RawDirectory 'coverage-gate.log')
            return Merge-ProducerExit @($worst, $gate)
        }
        'warning-ratchet' {
            $build = Invoke-Native 'dotnet' @('build', $PolicyDocument.warnings.solution, '-c', 'Release', '--no-incremental', '-nologo', '-v:minimal', '-clp:NoSummary') (Join-Path $RawDirectory 'build.log')
            $ratchet = Invoke-Native $python @('-B', (Join-Path $PSScriptRoot 'warning-inventory.py'), 'check', '--repo', $repo, '--baseline', (Join-Path $policyRepo $PolicyDocument.warnings.baseline), '--require-built-projects', '--solution', (Join-Path $policyRepo $PolicyDocument.warnings.solution), '--output', (Join-Path $RawDirectory 'warnings-check.json'), (Join-Path $RawDirectory 'build.log')) (Join-Path $RawDirectory 'warning-ratchet.log')
            # Both exits matter: a clean build whose warnings regressed is a failed lane, not a passing producer.
            return Merge-ProducerExit @($build, $ratchet)
        }
        default { throw "Lane kind $($LaneEntry.kind) has no local runner." }
    }
}

# ---- main ---------------------------------------------------------------------------------------------------------
$null = New-Item -ItemType Directory -Force $EvidenceDirectory
Save-PreviousAttempt (Join-Path $EvidenceDirectory 'summary.json') 'summaries/json'
Save-PreviousAttempt (Join-Path $EvidenceDirectory 'summary.md') 'summaries/md'
$version = Resolve-CandidateVersion
Assert-CandidateVersion $version ([bool] $ReleasePreparation)
$releaseArguments = @(); if ($ReleasePreparation) { $releaseArguments += '-ReleasePreparation' }
$null = New-Item -ItemType Directory -Force $EvidenceDirectory
$source = Get-SourceState
# Evaluate/Aggregate enforce a source commit only when the caller names one; Run/Collect default to this checkout's HEAD.
$explicitCommit = $SourceCommit
if (-not $SourceCommit) { $SourceCommit = $source.commit }
# Evidence records whether the tree was dirty. Collect of raw outputs produced elsewhere for a named commit is not
# affected by this checkout's state.
$sourceDirty = $source.dirty -and -not ($Mode -eq 'Collect' -and $explicitCommit)
$policyDocument = Get-Content -Raw -LiteralPath $policyPath | ConvertFrom-Json
[ordered]@{
    mode = $Mode; candidateVersion = $version; host = $hostInfo; context = $Context; runId = $RunId
    sourceCommit = $SourceCommit; workingTreeDirty = $source.dirty; policy = $Policy
    policySha256 = (Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    startedUtc = [DateTime]::UtcNow.ToString('o'); allowPending = [bool] $AllowPending
} | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'run-context.json')

if (($Mode -in 'Run', 'Collect') -and $sourceDirty -and -not $AllowDirtySource) {
    Write-Host 'FAIL: the working tree has uncommitted changes; evidence must name the exact source it came from (use -AllowDirtySource only for tooling self-tests; such evidence fails the gate).'
    exit 1
}

if ($Mode -eq 'Collect') {
    if ($Lane.Count -ne 1) { Write-Host 'FAIL: Collect needs exactly one -Lane.'; exit 2 }
    if (-not (@($policyDocument.lanes | Where-Object id -EQ $Lane[0]).Count)) { Write-Host "FAIL: unknown lane $($Lane[0])"; exit 2 }
    Save-PreviousAttempt (Join-Path $EvidenceDirectory "lanes/$($Lane[0].Replace('/', '__'))") "lanes/$($Lane[0].Replace('/', '__'))"
    $code = Invoke-Collect $Lane[0] $Raw ([int] $ProducerExit) $version $SourceCommit $sourceDirty $Service
    exit $code
}

$runFailed = $false
if ($Mode -eq 'Run') {
    foreach ($id in $Lane) { if (-not (@($policyDocument.lanes | Where-Object id -EQ $id).Count)) { Write-Host "FAIL: unknown lane $id"; exit 2 } }
    # Lanes that rebuild with another SDK (current servicing) run last so every other lane tests the pinned build.
    $selected = @($policyDocument.lanes | Where-Object { $Lane.Count -eq 0 -or $_.id -in $Lane } | Sort-Object -Stable { if ($_.toolchainRootEnvironment) { 1 } else { 0 } })
    $needsBuild = @($selected | Where-Object { ($_.kind -in 'runtime-tests', 'provider', 'property-counts', 'coverage') -and $_.countsProfile -ne 'ef10' }).Count -gt 0
    if ($needsBuild) {
        Save-PreviousAttempt (Join-Path $EvidenceDirectory 'work/build.log') 'solution-build'
        $buildExit = Invoke-Native 'dotnet' @('build', 'Meridian.sln', '-c', 'Release', '-nologo', '-v:minimal', '-clp:NoSummary') (Join-Path $EvidenceDirectory 'work/build.log')
        if ($buildExit -ne 0) { Write-Host 'FAIL: solution build failed; no lane can run.'; exit 1 }
    }
    foreach ($entry in $selected) {
        $runsHere = (-not $entry.host) -or ($entry.host.os -eq $hostInfo.os -and (-not $entry.host.arch -or $entry.host.arch -eq $hostInfo.arch))
        if (-not $runsHere) { Write-Host "SKIP lane $($entry.id): bound to $($entry.host.os)/$($entry.host.arch), this host is $($hostInfo.os)/$($hostInfo.arch); it stays UNAVAILABLE."; continue }
        $rawDirectory = Join-Path $EvidenceDirectory "work/$($entry.id.Replace('/', '__'))"
        # Retire an old PASS before starting: a collector crash must leave this attempt missing/failed, never reuse it.
        Save-PreviousAttempt (Join-Path $EvidenceDirectory "lanes/$($entry.id.Replace('/', '__'))") "lanes/$($entry.id.Replace('/', '__'))"
        try { $exit = Invoke-Lane $policyDocument $entry $rawDirectory $version } catch { Write-Host "Lane $($entry.id) runner error: $_"; $exit = 1 }
        $services = @($entry.services | ForEach-Object { if ($_) { "$_=auto" } })
        $collectExit = Invoke-Collect $entry.id @($rawDirectory) $exit $version $SourceCommit $sourceDirty $services
        if ($exit -ne 0 -or $collectExit -ne 0) { $runFailed = $true }
    }
}

# A Run restricted with -Lane is judged only for those lanes (scoped verdict); a full Run or any Evaluate/Aggregate judges the candidate.
$verdict = Invoke-Evaluate $version $(if ($Mode -eq 'Run') { $SourceCommit } else { $explicitCommit }) $(if ($Mode -eq 'Run') { $Lane } else { @() })
if ($runFailed) { $verdict = 1 }
Write-Host "Evidence: $EvidenceDirectory (summary.json, summary.md)"
exit $verdict

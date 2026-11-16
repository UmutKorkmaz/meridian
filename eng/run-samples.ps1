[CmdletBinding()]
param(
    [ValidateSet('net8.0', 'net9.0', 'net10.0')]
    [string] $Framework = 'net10.0',

    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 300,

    [string] $LogDirectory,

    [switch] $NoBuild,

    [switch] $IncludeHttp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $LogDirectory) {
    $LogDirectory = Join-Path $repoRoot "artifacts/sample-results/$Framework"
}
$LogDirectory = [IO.Path]::GetFullPath($LogDirectory)
$null = New-Item -ItemType Directory -Force -Path $LogDirectory
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$samples = @('Showcase', 'CleanArchitecture', 'EventDrivenCqrs', 'ModularMonolith', 'VerticalSlice', 'Hexagonal')
if ($Framework -eq 'net10.0') {
    $samples = @('QuickStart') + $samples
} else {
    Write-Host "SKIP QuickStart: targets net10.0 only ($Framework runs six multi-target samples)."
}

# Machine-readable receipt for eng/lane_collect.py: every expected sample is listed up front with exit=null and is
# overwritten as it runs, so a sample that never ran (earlier failure, timeout) still counts against the total. With
# -IncludeHttp the HTTP smoke has its own row (null until it ran) with its exit, timeout and ownership observations.
$summaryPath = Join-Path $LogDirectory 'samples-summary.json'
$sampleResults = [ordered]@{}
foreach ($name in $samples) { $sampleResults[$name] = $null }
$httpResult = $null
function Write-SamplesSummary {
    $entries = @($sampleResults.Keys | ForEach-Object { [ordered]@{ name = $_; exit = $sampleResults[$_] } })
    $summary = [ordered]@{ framework = $Framework; includeHttp = [bool] $IncludeHttp; samples = $entries }
    if ($IncludeHttp) { $summary.http = $httpResult }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath
}
Write-SamplesSummary
$passed = 0
foreach ($sample in $samples) {
    $project = Join-Path $repoRoot "samples/Meridian.$sample/Meridian.$sample.csproj"
    $logPath = Join-Path $LogDirectory "$sample.log"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('run', '--project', $project, '-c', 'Release', '-f', $Framework)) {
        $start.ArgumentList.Add($argument)
    }
    if ($NoBuild) { $start.ArgumentList.Add('--no-build') }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Write-Host "RUN $sample ($Framework), timeout=${TimeoutSeconds}s"
        $null = $process.Start()
        # Drain both pipes concurrently so verbose builds cannot deadlock.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            $process.Kill($true)
            $null = $process.WaitForExit(10000)
        }
        $streams = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]] @($stdout, $stderr))
        $drained = $streams.Wait(10000)
        $output = if ($stdout.IsCompletedSuccessfully) { $stdout.Result } else { '[stdout drain timed out]' }
        $errors = if ($stderr.IsCompletedSuccessfully) { $stderr.Result } else { '[stderr drain timed out]' }
        $exitCode = if ($timedOut) { 124 } elseif (-not $drained) { 1 } else { $process.ExitCode }
        $log = "sample=$sample framework=$Framework exit=$exitCode timeout=$timedOut`n--- stdout ---`n$output`n--- stderr ---`n$errors"
        [IO.File]::WriteAllText($logPath, $log)
        Write-Host $log
        $sampleResults[$sample] = $exitCode
        Write-SamplesSummary
        if ($exitCode -ne 0) {
            Write-Host "FAIL $sample; passed=$passed/$($samples.Count); log=$logPath"
            exit $exitCode
        }
        $passed++
        Write-Host "PASS $sample; log=$logPath"
    } catch {
        $sampleResults[$sample] = 1
        Write-SamplesSummary
        [IO.File]::WriteAllText($logPath, "sample=$sample framework=$Framework runner error: $_")
        Write-Error "Sample runner failed for $sample; log=$logPath`: $_" -ErrorAction Continue
        exit 1
    } finally {
        # Also clean up a started process when the runner itself encounters an error.
        try { if ($process.Id -gt 0 -and -not $process.HasExited) { $process.Kill($true) } }
        catch { Write-Warning "Could not clean up sample process: $_" }
        $process.Dispose()
    }
}
if ($IncludeHttp) {
    if ($Framework -ne 'net10.0') { throw 'HTTP sample targets net10.0 only.' }
    if (-not $env:MERIDIAN_TEST_REDIS) { throw 'HTTP smoke requires an explicitly isolated MERIDIAN_TEST_REDIS.' }
    $arguments = @('test', (Join-Path $repoRoot 'tests/Meridian.AspNetCore.Tests/Meridian.AspNetCore.Tests.csproj'), '-c', 'Release', '--filter', 'FullyQualifiedName~SocketTests', '--logger', 'trx;LogFileName=http-socket.trx', '--results-directory', (Join-Path $LogDirectory 'http'))
    if ($NoBuild) { $arguments += '--no-build' }
    $logPath = Join-Path $LogDirectory 'http.log'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $ownershipToken = [Guid]::NewGuid().ToString('N')
    $ownedDirectory = Join-Path $LogDirectory ".http-owned-$ownershipToken"
    if ($IsWindows) { $null = [IO.Directory]::CreateDirectory($ownedDirectory) }
    else {
        $mode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        $null = [IO.Directory]::CreateDirectory($ownedDirectory, $mode)
    }
    $ownershipPath = Join-Path $ownedDirectory 'ownership.json'
    $argumentsPath = Join-Path $ownedDirectory 'arguments.json'
    [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject $arguments -Compress))
    $helperAssembly = Join-Path $LogDirectory "http-owned-helper-$ownershipToken.dll"
    Add-Type -Path (Join-Path $PSScriptRoot 'OwnedHttpProcess.cs') -OutputAssembly $helperAssembly
    Add-Type -Path $helperAssembly
    $helperSha256 = (Get-FileHash -LiteralPath $helperAssembly -Algorithm SHA256).Hash
    $start.FileName = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-owned-http.ps1'),
            '-HelperAssembly', $helperAssembly, '-Executable', $dotnet,
            '-RunArgumentsPath', $argumentsPath, '-WorkingDirectory', $repoRoot,
            '-OwnershipPath', $ownershipPath, '-OwnershipToken', $ownershipToken)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $timedOut = $false
    $exitObserved = $false
    $drainsObserved = $false
    $stdout = $null
    $stderr = $null
    $exited = $null
    $ownershipValidated = $false
    $failures = [Collections.Generic.List[string]]::new()
    try {
        Write-Host "RUN HTTP ($Framework), timeout=${TimeoutSeconds}s"
        $null = $process.Start()
        $supervisorPid = $process.Id
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExitAsync()
        $completion = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]] @($exited, $stdout, $stderr))
        # One run budget covers the actual supervisor exit and both original pipe reads.
        $timedOut = -not $completion.Wait($TimeoutSeconds * 1000)
    } catch { $failures.Add("HTTP runner error: $_") }
    finally {
        try {
            if ([IO.File]::Exists($ownershipPath)) {
                $ownership = Get-Content -LiteralPath $ownershipPath -Raw | ConvertFrom-Json
                if ($ownership.pid -ne $process.Id -or $ownership.token -cne $ownershipToken -or
                    $ownership.kind -ne $(if ($IsWindows) { 'windows-job' } else { 'unix-session' })) {
                    throw 'Owned HTTP supervisor receipt does not match this run.'
                }
                $ownershipValidated = $true
                [IO.File]::Copy($ownershipPath, (Join-Path $LogDirectory 'http-ownership.json'), $true)
            }
        } catch { $failures.Add("HTTP ownership receipt error: $_") }
        try {
            if ($process.Id -gt 0) {
                if (-not $IsWindows) {
                    # Kill the private group even after its leader exited; no PID-name search.
                    $null = [Meridian.Tools.OwnedHttpProcess]::TerminateUnixGroup($process.Id, $ownershipValidated)
                }
                # Windows closes the supervisor's exclusive kill-on-close job handle on exit.
                # Before Unix ownership setup there cannot be an inner child yet.
                if (-not $process.HasExited) { $process.Kill($true) }
                if ($null -ne $exited) { $exited.GetAwaiter().GetResult() }
                else { $process.WaitForExit() }
                $exitObserved = $true
            }
        } catch { $failures.Add("HTTP process cleanup error: $_") }
        try {
            if ($null -ne $stdout -and $null -ne $stderr) {
                $streams = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]] @($stdout, $stderr))
                $streams.GetAwaiter().GetResult()
                $drainsObserved = $true
            }
        } catch { $failures.Add("HTTP drain error: $_") }
        $childExitCode = if ($exitObserved) { $process.ExitCode } else { 1 }
        try { $process.Dispose() } catch { $failures.Add("HTTP handle cleanup error: $_") }
        try { Remove-Item -LiteralPath $ownedDirectory -Recurse -Force }
        catch { $failures.Add("HTTP private evidence cleanup error: $_") }
        $output = if ($null -ne $stdout -and $stdout.IsCompletedSuccessfully) { $stdout.Result } else { '[stdout unavailable]' }
        $errors = if ($null -ne $stderr -and $stderr.IsCompletedSuccessfully) { $stderr.Result } else { '[stderr unavailable]' }
        # Cleanup diagnostics accompany, rather than replace, the original timeout diagnosis.
        $exitCode = if ($timedOut) { 124 } elseif ($failures.Count -gt 0) { 1 } else { $childExitCode }
        $log = "sample=HTTP framework=$Framework exit=$exitCode timeout=$timedOut exitObserved=$exitObserved drainsObserved=$drainsObserved ownershipValidated=$ownershipValidated supervisorPid=$supervisorPid helperSha256=$helperSha256 helperAssembly=$helperAssembly`n--- stdout ---`n$output`n--- stderr ---`n$errors`n--- runner ---`n$($failures -join "`n")"
        try { [IO.File]::WriteAllText($logPath, $log) }
        catch { Write-Warning "HTTP log write also failed: $_" }
        Write-Host $log
        $httpResult = [ordered]@{ exit = $exitCode; timeout = $timedOut; exitObserved = $exitObserved; drainsObserved = $drainsObserved; ownershipValidated = $ownershipValidated }
        try { Write-SamplesSummary } catch { Write-Warning "HTTP summary write failed: $_" }
    }
    if ($exitCode -ne 0) { exit $exitCode }
}
Write-Host "PASS samples: $passed/$($samples.Count) ($Framework)"
exit 0

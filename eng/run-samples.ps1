[CmdletBinding()]
param(
    [ValidateSet('net8.0', 'net9.0', 'net10.0')]
    [string] $Framework = 'net10.0',

    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 300,

    [string] $LogDirectory,

    [switch] $NoBuild
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
        if ($exitCode -ne 0) {
            Write-Host "FAIL $sample; passed=$passed/$($samples.Count); log=$logPath"
            exit $exitCode
        }
        $passed++
        Write-Host "PASS $sample; log=$logPath"
    } catch {
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
Write-Host "PASS samples: $passed/$($samples.Count) ($Framework)"
exit 0

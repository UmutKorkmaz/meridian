[CmdletBinding()]
param(
    # BenchmarkDotNet filter glob(s) over Namespace.Class.Method names, for example '*E3MappingFlat*'.
    [Parameter(Mandatory)][string[]] $Filter,

    # A directory that does not exist yet or is empty. It must not overlap the source trees. Raw BenchmarkDotNet artifacts,
    # logs, the run manifest and the summary are written here.
    [Parameter(Mandatory)][string] $OutputDirectory,

    # Fixed: every scenario class uses the one job declared by its own configuration (hot or cold). Smoke: one short
    # invocation per scenario; it proves the run executes and is never performance evidence.
    [ValidateSet('Fixed', 'Smoke')][string] $Profile = 'Fixed',

    # The named measurement window this run belongs to (recorded in the manifest).
    [string] $WindowName = '',

    # Source root to build and run. Defaults to this repository. Another root must have the same layout (for example a
    # frozen P0/P1 snapshot) and is copied, never modified.
    [string] $SourceRoot = '',

    # Extra arguments passed to BenchmarkDotNet after the filter, for harnesses whose job is given on the command line.
    [string[]] $BenchmarkArguments = @(),

    # Only for frozen harnesses that predate the E3 preflight; must say why. The scenario classes' own GlobalSetup
    # validation still runs before timing. E3 scenario runs never use this.
    [string] $SkipPreflightReason = '',

    # Abort the timed run (and fail it) when the 1-minute load average stays above this value for three consecutive
    # samples. 0 records the load without enforcing it.
    [double] $MaxLoadAverage = 0,

    [ValidateRange(5, 600)][int] $SampleSeconds = 30,

    [switch] $KeepWorkspace
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceRoot = if ($SourceRoot) { [IO.Path]::GetFullPath($SourceRoot) } else { $repoRoot }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$python = (Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
$productDirectories = @('src/Meridian.Mapping', 'src/Meridian.Mapping.Generators', 'src/Meridian.Mediator', 'src/Meridian.Mediator.Generators')
$harnessDirectory = 'benchmarks/Meridian.Benchmarks'
$rootFiles = @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.config', 'global.json', '.editorconfig')
$excludedDirectories = @('bin', 'obj', 'TestResults', 'BenchmarkDotNet.Artifacts')
# Competitors whose pinned version is deliberately an old, permissively licensed release (not the current one).
$historicalCompetitors = @('AutoMapper', 'MediatR')
$competitorIds = @('Mapster', 'Riok.Mapperly', 'AutoMapper', 'MediatR')

function Fail([string] $message) { throw [InvalidOperationException]::new($message) }

function Test-IsUnder([string] $child, [string] $parent) {
    $c = [IO.Path]::GetFullPath($child).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $p = [IO.Path]::GetFullPath($parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return $c.StartsWith($p, $comparison)
}

# ---- argument validation (before anything is created) ----
foreach ($pattern in $Filter) {
    if ($pattern -notmatch "^[A-Za-z0-9_.*?][A-Za-z0-9_.*?()':, -]*$" -or $pattern.Contains('--')) {
        Fail "Filter pattern '$pattern' is invalid: it must start with a letter, digit, '_', '.', '*' or '?', and may not contain '--'."
    }
}
if ($SkipPreflightReason -and $Filter -match 'E3') { Fail 'The E3 preflight cannot be skipped.' }

$out = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($tree in @('src', 'tests', 'eng', 'docs', 'samples', 'benchmarks', 'docs-site', '.github')) {
    $protected = Join-Path $repoRoot $tree
    if ((Test-IsUnder $out $protected) -or (Test-IsUnder $protected $out)) {
        Fail "OutputDirectory '$out' overlaps the source tree '$protected'."
    }
}
if ((Test-IsUnder $repoRoot $out) -or ($out -eq $repoRoot)) { Fail "OutputDirectory '$out' overlaps the repository root." }
if ((Test-Path -LiteralPath $out) -and @(Get-ChildItem -LiteralPath $out -Force).Count -gt 0) {
    Fail "OutputDirectory '$out' is not empty; choose a fresh directory (an existing run is never overwritten or resumed)."
}
if (-not $python) { Fail 'python3 is required to summarize results.' }
foreach ($required in $productDirectories[0..2] + @($harnessDirectory)) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $required))) { Fail "SourceRoot '$sourceRoot' lacks '$required'." }
}

$null = New-Item -ItemType Directory -Force -Path $out
# Build and run from the symlink-free spelling of the path: the compiler and the project graph otherwise see the same
# files under two spellings (for example /var and /private/var on macOS), which broke source generators in testing.
$out = (& $python -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' $out).Trim()
$workspace = Join-Path $out 'workspace'
$manifestPath = Join-Path $out 'manifest.json'
$runId = [Guid]::NewGuid().ToString('N')
$startedUtc = [DateTime]::UtcNow
$manifest = [ordered]@{
    schemaVersion = 1
    runId = $runId
    status = 'running'
    profile = $Profile
    filter = ($Filter -join ' ')
    filterPatterns = @($Filter)
    startedUtc = $startedUtc.ToString('o')
    window = [ordered]@{ name = $WindowName }
    sourceRoot = $sourceRoot
    benchmarkArguments = @($BenchmarkArguments)
    failures = @()
    exclusions = @()
}

function Save-Manifest { [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 12) + "`n") }

# Runs an executable with an argument array, appends combined output to a log file and returns the exit code.
function Invoke-Logged([string] $exe, [string[]] $arguments, [string] $log, [string] $workingDirectory = $workspace) {
    Push-Location $workingDirectory
    try {
        & $exe @arguments *>> $log
        return $LASTEXITCODE
    } finally { Pop-Location }
}

function Get-Text([string] $exe, [string[]] $arguments) {
    try { return ((& $exe @arguments 2>&1) | Out-String).Trim() } catch { return "unavailable: $($_.Exception.Message)" }
}

function Get-HostConditions {
    $uptime = Get-Text 'uptime' @()
    $load = @()
    if ($uptime -match 'load averages?:\s*([0-9.,]+)[ ,]+([0-9.,]+)[ ,]+([0-9.,]+)') {
        $load = @($Matches[1], $Matches[2], $Matches[3] | ForEach-Object { [double]::Parse($_.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) })
    }
    $power = 'unknown'
    $battery = ''
    if ($IsMacOS) {
        $battery = Get-Text 'pmset' @('-g', 'batt')
        $power = if ($battery -match "Now drawing from 'AC Power'") { 'AC' } elseif ($battery -match "Now drawing from 'Battery Power'") { 'battery' } else { 'unknown' }
    } elseif ($IsLinux) {
        $supply = Get-ChildItem '/sys/class/power_supply' -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'A*' -or $_.Name -like 'ADP*' } | Select-Object -First 1
        if ($supply -and (Test-Path (Join-Path $supply.FullName 'online'))) { $power = if ((Get-Content (Join-Path $supply.FullName 'online')).Trim() -eq '1') { 'AC' } else { 'battery' } }
    }
    $thermal = if ($IsMacOS) { Get-Text 'pmset' @('-g', 'therm') } else { 'unavailable' }
    $top = if ($IsWindows) { 'unavailable' } else { Get-Text 'ps' @('-Ao', 'pid,pcpu,pmem,comm', '-r') }
    $topLines = @($top -split "`n" | Select-Object -First 13)
    return [ordered]@{
        capturedUtc = [DateTime]::UtcNow.ToString('o')
        uptime = $uptime
        loadAverage = $load
        powerSource = $power
        battery = $battery
        thermal = $thermal
        topProcessesByCpu = $topLines
    }
}

function Get-DirectoryHash([string] $root, [string[]] $directories, [string] $manifestFile) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($directory in $directories) {
        $base = Join-Path $root $directory
        if (-not (Test-Path -LiteralPath $base)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $base -Recurse -File -Force | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            $segments = $relative.Split('/')
            if ($segments | Where-Object { $excludedDirectories -contains $_ }) { continue }
            $lines.Add("$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  ./$relative")
        }
    }
    $sorted = @($lines | Sort-Object { $_.Substring(66) })
    if ($manifestFile) { [IO.File]::WriteAllLines($manifestFile, $sorted) }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($sorted -join "`n"))
    return ([BitConverter]::ToString([Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').ToLowerInvariant())
}

function Copy-SourceTree([string] $from, [string] $to) {
    $null = New-Item -ItemType Directory -Force -Path $to
    foreach ($file in $rootFiles) {
        $path = Join-Path $from $file
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $to $file) }
    }
    foreach ($directory in $productDirectories + @($harnessDirectory)) {
        $base = Join-Path $from $directory
        if (-not (Test-Path -LiteralPath $base)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $base -Recurse -File -Force)) {
            $relative = [IO.Path]::GetRelativePath($from, $file.FullName)
            if (($relative.Replace('\', '/').Split('/')) | Where-Object { $excludedDirectories -contains $_ }) { continue }
            $target = Join-Path $to $relative
            $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target)
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    # A stub solution file anchors BenchmarkDotNet's project discovery at this isolated tree, so project files of other
    # copies elsewhere in the repository (frozen task snapshots) can never be found.
    [IO.File]::WriteAllText((Join-Path $to 'Snapshot.sln'), "Microsoft Visual Studio Solution File, Format Version 12.00`n# Isolated benchmark workspace root marker (not a buildable solution)`n")
}

function Get-CompetitorRecords([string] $assetsPath) {
    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    $records = @()
    foreach ($id in $competitorIds) {
        $library = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -like "$id/*" }) | Select-Object -First 1
        if (-not $library) { continue }
        $version = $library.Name.Substring($id.Length + 1)
        $license = 'unknown'
        foreach ($folder in $folders) {
            $nuspec = Join-Path $folder "$($id.ToLowerInvariant())/$version/$($id.ToLowerInvariant()).nuspec"
            if (Test-Path -LiteralPath $nuspec) {
                [xml] $xml = Get-Content -Raw -LiteralPath $nuspec
                $node = $xml.package.metadata.license
                if ($node) { $license = if ($node.InnerText) { $node.InnerText } else { [string] $node } }
                break
            }
        }
        $records += [ordered]@{ id = $id; version = $version; license = $license; historical = ($historicalCompetitors -contains $id) }
    }
    return $records
}

$exitCode = 1
try {
    Save-Manifest
    # ---- isolated workspace (identical layout, no bin/obj) ----
    Copy-SourceTree $sourceRoot $workspace
    $productHash = Get-DirectoryHash $workspace $productDirectories (Join-Path $out 'product-source-manifest.txt')
    $harnessHash = Get-DirectoryHash $workspace @($harnessDirectory) (Join-Path $out 'harness-source-manifest.txt')
    $gitHead = Get-Text 'git' @('-C', $repoRoot, 'rev-parse', 'HEAD')
    $gitStatus = Get-Text 'git' @('-C', $repoRoot, 'status', '--porcelain=v1')
    $gitStatusHash = ([BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($gitStatus))).Replace('-', '').ToLowerInvariant())
    $manifest.candidate = [ordered]@{
        lane = 'project-reference'
        note = 'Built from source with project references; this is not evidence about a released package. No package-lane run was made.'
        sourceRoot = $sourceRoot
        gitHead = $gitHead
        gitBranch = (Get-Text 'git' @('-C', $repoRoot, 'rev-parse', '--abbrev-ref', 'HEAD'))
        gitStatusSha256 = $gitStatusHash
        productSourceSha256 = $productHash
        harnessSha256 = $harnessHash
        productDirectories = @($productDirectories | Where-Object { Test-Path -LiteralPath (Join-Path $workspace $_) })
        assemblies = @()
    }
    Save-Manifest

    # ---- build (Release) ----
    $project = Join-Path $workspace "$harnessDirectory/Meridian.Benchmarks.csproj"
    $buildLog = Join-Path $out 'build.log'
    # No shared compiler server: the benchmark build must not depend on state left by an earlier build, and
    # BenchmarkDotNet's own generated builds already run with UseSharedCompilation=false.
    $code = Invoke-Logged $dotnet @('build', $project, '-c', 'Release', '-v:minimal', '-nologo', '-p:UseSharedCompilation=false') $buildLog
    if ($code -ne 0) { Fail "dotnet build failed (exit $code); see $buildLog" }
    $dllDirectory = Join-Path $workspace "$harnessDirectory/bin/Release/net10.0"
    $dll = Join-Path $dllDirectory 'Meridian.Benchmarks.dll'
    if (-not (Test-Path -LiteralPath $dll)) { Fail "Build did not produce $dll" }
    $buildText = Get-Content -Raw -LiteralPath $buildLog
    $manifest.build = [ordered]@{
        log = 'build.log'
        warningLines = ([regex]::Matches($buildText, ' warning ')).Count
        auditWarnings = @([regex]::Matches($buildText, 'warning NU190\d[^\r\n]*') | ForEach-Object { $_.Value } | Select-Object -Unique)
    }
    foreach ($name in @('Meridian.Mapping.dll', 'Meridian.Mediator.dll', 'Meridian.Benchmarks.dll')) {
        $path = Join-Path $dllDirectory $name
        if (Test-Path -LiteralPath $path) {
            $manifest.candidate.assemblies += [ordered]@{
                name = $name
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
            }
        }
    }
    $assetsPath = Join-Path $workspace "$harnessDirectory/obj/project.assets.json"
    $manifest.competitors = @(Get-CompetitorRecords $assetsPath)
    $runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $dllDirectory 'Meridian.Benchmarks.runtimeconfig.json') | ConvertFrom-Json
    $manifest.runtimeConfiguration = $runtimeConfig.runtimeOptions.configProperties
    Save-Manifest

    # ---- the filter must select scenarios ----
    $listLog = Join-Path $out 'list.txt'
    $listArguments = @($dll, '--list', 'flat', '--filter') + $Filter
    $null = Invoke-Logged $dotnet $listArguments $listLog $dllDirectory
    $listed = @(Get-Content -LiteralPath $listLog | Where-Object { $_ -match '^Meridian\.Benchmarks\.\w+\.' })
    if ($listed.Count -eq 0) { Fail "Filter '$($Filter -join ' ')' matches zero scenarios (see list.txt); nothing was timed." }
    $classes = @($listed | ForEach-Object { ($_ -split '\.')[2] } | Select-Object -Unique)
    $manifest.scenarios = [ordered]@{ count = $listed.Count; classes = $classes }
    Save-Manifest

    # ---- semantic preflight (separate process; fails the run before any timing) ----
    $preflightLog = Join-Path $out 'preflight.log'
    if ($SkipPreflightReason) {
        $manifest.preflight = [ordered]@{ passed = $false; skipped = $true; reason = $SkipPreflightReason }
        $manifest.exclusions += "preflight skipped: $SkipPreflightReason"
    } else {
        $e3Classes = @($classes | Where-Object { $_ -like 'E3*' })
        if ($e3Classes.Count -eq 0) { Fail 'The selection contains no E3 scenario class, so the semantic preflight cannot run; pass -SkipPreflightReason for a frozen harness.' }
        $preflightExit = Invoke-Logged $dotnet (@($dll, '--e3-preflight') + $e3Classes) $preflightLog $dllDirectory
        $manifest.preflight = [ordered]@{ passed = ($preflightExit -eq 0); skipped = $false; classes = $e3Classes; log = 'preflight.log' }
        Save-Manifest
        if ($preflightExit -ne 0) { Fail "Semantic preflight failed (exit $preflightExit); see $preflightLog. No timing was started." }
    }

    # ---- named measurement window: conditions, timed run, conditions ----
    $before = Get-HostConditions
    $manifest.host = [ordered]@{
        name = (Get-Text 'hostname' @())
        os = if ($IsMacOS) { (Get-Text 'sw_vers' @('-productVersion')) + ' (' + (Get-Text 'uname' @('-sr')) + ')' } else { [Runtime.InteropServices.RuntimeInformation]::OSDescription }
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        cpu = if ($IsMacOS) { Get-Text 'sysctl' @('-n', 'machdep.cpu.brand_string') } else { [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() }
        logicalCores = [Environment]::ProcessorCount
        memoryBytes = if ($IsMacOS) { [long](Get-Text 'sysctl' @('-n', 'hw.memsize')) } else { 0 }
        sdk = (Get-Text $dotnet @('--version'))
        powerSource = $before.powerSource
        loadAverageBefore = $before.loadAverage
    }
    $manifest.window.conditionsBefore = $before
    $manifest.window.maxLoadAverageEnforced = $MaxLoadAverage
    Save-Manifest

    $bdnDirectory = Join-Path $out 'bdn'
    $runLog = Join-Path $out 'run.log'
    $runErrors = Join-Path $out 'run.stderr.log'
    $bdnArguments = @($dll, '--filter') + $Filter + @('--artifacts', $bdnDirectory) + $BenchmarkArguments
    $executable = $dotnet
    $executableArguments = $bdnArguments
    if ($IsMacOS -and (Get-Command caffeinate -CommandType Application -ErrorAction SilentlyContinue)) {
        $executable = (Get-Command caffeinate -CommandType Application).Source
        $executableArguments = @('-i', $dotnet) + $bdnArguments
    }
    $manifest.command = @($executable) + $executableArguments
    $manifest.environment = [ordered]@{ MERIDIAN_BENCH_PROFILE = $Profile }
    Save-Manifest

    $previousProfile = $env:MERIDIAN_BENCH_PROFILE
    $env:MERIDIAN_BENCH_PROFILE = $Profile
    $samples = New-Object System.Collections.Generic.List[object]
    $aborted = $false
    try {
        $timedStart = [DateTime]::UtcNow
        $process = Start-Process -FilePath $executable -ArgumentList $executableArguments -WorkingDirectory $dllDirectory `
            -RedirectStandardOutput $runLog -RedirectStandardError $runErrors -PassThru -NoNewWindow
        $high = 0
        while (-not $process.WaitForExit($SampleSeconds * 1000)) {
            $condition = Get-HostConditions
            $samples.Add([ordered]@{ utc = $condition.capturedUtc; load1 = if ($condition.loadAverage.Count) { $condition.loadAverage[0] } else { $null }; power = $condition.powerSource })
            if ($MaxLoadAverage -gt 0 -and $condition.loadAverage.Count -and $condition.loadAverage[0] -gt $MaxLoadAverage) { $high++ } else { $high = 0 }
            if ($high -ge 3) {
                $aborted = $true
                $process.Kill($true)
                $null = $process.WaitForExit(30000)
                break
            }
        }
        $bdnExit = if ($aborted) { 1 } else { $process.ExitCode }
        $timedEnd = [DateTime]::UtcNow
    } finally {
        $env:MERIDIAN_BENCH_PROFILE = $previousProfile
    }
    $after = Get-HostConditions
    $manifest.window.conditionsAfter = $after
    $manifest.window.timedStartUtc = $timedStart.ToString('o')
    $manifest.window.timedEndUtc = $timedEnd.ToString('o')
    $manifest.window.timedSeconds = [math]::Round(($timedEnd - $timedStart).TotalSeconds, 1)
    $manifest.window.samples = $samples.ToArray()
    $loads = @($before.loadAverage[0], $after.loadAverage[0]) + @($samples | ForEach-Object { $_.load1 }) | Where-Object { $null -ne $_ }
    $manifest.window.maxLoad1Observed = if ($loads) { ($loads | Measure-Object -Maximum).Maximum } else { $null }
    $manifest.window.powerSources = @(@($before.powerSource, $after.powerSource) + @($samples | ForEach-Object { $_.power }) | Select-Object -Unique)
    $manifest.window.aborted = $aborted
    $manifest.bdn = [ordered]@{ exitCode = $bdnExit; log = 'run.log'; stderr = 'run.stderr.log'; artifacts = 'bdn' }
    Save-Manifest
    if ($aborted) { Fail "Timed run aborted: 1-minute load stayed above $MaxLoadAverage; evidence is not valid." }
    if ($bdnExit -ne 0) { Fail "BenchmarkDotNet exited with $bdnExit; see $runLog" }

    # ---- BenchmarkDotNet's own runtime facts and the summary ----
    $reports = @(Get-ChildItem -LiteralPath (Join-Path $bdnDirectory 'results') -Filter '*-report-full*.json' -ErrorAction SilentlyContinue)
    if ($reports.Count -eq 0) { Fail 'BenchmarkDotNet produced no JSON reports.' }
    $hostInfo = (Get-Content -Raw -LiteralPath $reports[0].FullName | ConvertFrom-Json).HostEnvironmentInfo
    $manifest.host.runtimeVersion = $hostInfo.RuntimeVersion
    $manifest.host.benchmarkDotNetVersion = $hostInfo.BenchmarkDotNetVersion
    $manifest.host.jit = $hostInfo.HasRyuJit
    $manifest.reports = @($reports | ForEach-Object { $_.Name })
    $manifest.status = 'passed'
    $manifest.endedUtc = [DateTime]::UtcNow.ToString('o')
    Save-Manifest
    $summaryExit = Invoke-Logged $python @('-B', (Join-Path $repoRoot 'eng/benchmark-summary.py'), 'summarize', '--results', (Join-Path $bdnDirectory 'results'),
        '--output', (Join-Path $out 'summary.md'), '--json', (Join-Path $out 'summary.json'), '--manifest', $manifestPath) (Join-Path $out 'summary.log') $repoRoot
    if ($summaryExit -ne 0) { Fail "Summary generation failed (exit $summaryExit)." }
    $exitCode = 0
    Write-Host "PASS benchmarks: $($manifest.scenarios.count) scenarios, manifest=$manifestPath"
} catch {
    $manifest.status = 'failed'
    $manifest.endedUtc = [DateTime]::UtcNow.ToString('o')
    $manifest.failures = @($manifest.failures) + @($_.Exception.Message)
    if ($_.InvocationInfo -and $_.Exception -isnot [InvalidOperationException]) { $manifest.failureLocation = $_.InvocationInfo.PositionMessage }
    try { Save-Manifest } catch { }
    [Console]::Error.WriteLine("FAIL benchmarks: $($_.Exception.Message)")
    $exitCode = 1
} finally {
    if (-not $KeepWorkspace -and (Test-Path -LiteralPath $workspace)) { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exitCode

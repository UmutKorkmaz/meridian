$ErrorActionPreference = 'Stop'

# Case IDs of tests/consumers/Meridian.NativeAotConsumer/Program.cs. The first block runs in every mode;
# the second block runs only when the executable is started with --require-native. A Python test keeps
# this list identical to the arrays in Program.cs.
function Get-NativeAotExpectedCases {
    return @(
        'typed-object-query', 'void-forms', 'standard-pipeline', 'standard-composition', 'scoped-construction',
        'notification-composition', 'stream-lifetime', 'generated-mapping', 'strict-failure',
        'native-runtime', 'native-nonstrict-unknown', 'native-scanning-unsupported')
}

function ConvertFrom-NativeRid([string] $Rid) {
    switch ($Rid) {
        'osx-arm64' { return [pscustomobject]@{ os = 'osx'; arch = 'arm64' } }
        'linux-x64' { return [pscustomobject]@{ os = 'linux'; arch = 'x64' } }
        default { throw "Combined mode supports only linux-x64 and osx-arm64; got $Rid." }
    }
}

function Get-HostOs {
    if ($IsMacOS) { return 'osx' }
    if ($IsLinux) { return 'linux' }
    if ($IsWindows) { return 'win' }
    return 'other'
}

# Emulation markers of an x64 process on a Linux kernel. qemu-user (docker --platform linux/amd64 on Apple silicon or
# aarch64 hosts), Rosetta for Linux and similar translators keep the guest uname/OSArchitecture at x64, so the
# architecture comparison alone cannot see them. Every signal below is read from procfs; ProcRoot exists so the
# probes can be exercised against recorded process trees.
function Get-LinuxEmulationSignals([string] $ProcRoot = '/proc') {
    $signals = @()
    $emulator = '(?i)qemu-(x86_64|i386|i486)|qemu-user|\brosetta\b|box64|fex-emu|fexinterpreter'
    # 1. binfmt_misc registrations that are enabled and handle x86 ELF binaries. A disabled entry or a handler for
    #    another guest architecture does not translate this process.
    $binfmt = Join-Path $ProcRoot 'sys/fs/binfmt_misc'
    if (Test-Path -LiteralPath $binfmt -PathType Container) {
        foreach ($entry in Get-ChildItem -LiteralPath $binfmt -File -ErrorAction SilentlyContinue) {
            if ($entry.Name -in @('register', 'status')) { continue }
            $body = Get-Content -Raw -LiteralPath $entry.FullName -ErrorAction SilentlyContinue
            if (-not $body -or $body -notmatch '(?m)^enabled\s*$') { continue }
            $interpreter = [regex]::Match($body, '(?m)^interpreter\s+(\S+)').Groups[1].Value
            if ("$($entry.Name) $interpreter" -match $emulator) {
                $signals += "binfmt_misc entry '$($entry.Name)' is enabled with interpreter '$interpreter'"
            }
        }
    }
    # 2. Translator images mapped into the process.
    $maps = Join-Path $ProcRoot 'self/maps'
    if (Test-Path -LiteralPath $maps -PathType Leaf) {
        $hit = Get-Content -LiteralPath $maps -ErrorAction SilentlyContinue | Where-Object { $_ -match $emulator } | Select-Object -First 1
        if ($hit) { $signals += "emulator image mapped into the process: $($hit.Trim())" }
    }
    # 3. The CPU the process sees: a virtual Apple/QEMU CPU, or a non-x86 cpuinfo layout under an x64 process.
    $cpu = Join-Path $ProcRoot 'cpuinfo'
    if (Test-Path -LiteralPath $cpu -PathType Leaf) {
        $text = Get-Content -Raw -LiteralPath $cpu -ErrorAction SilentlyContinue
        if ($text) {
            if ($text -match '(?im)^(vendor_id|model name)\s*:\s*.*(VirtualApple|QEMU)') { $signals += 'cpuinfo reports a virtual (VirtualApple/QEMU) CPU' }
            elseif ($text -notmatch '(?im)^flags\s*:' -and $text -match '(?im)^(CPU implementer|Features)\s*:') { $signals += 'cpuinfo has a non-x86 layout (CPU implementer/Features) although the process reports x64' }
        }
    }
    return @($signals)
}

function Test-ProcessTranslated([string] $Os, [string] $ProcRoot = '/proc') {
    if (-not $Os) { $Os = Get-HostOs }
    if ($Os -eq 'osx') {
        # Rosetta 2 reports 1 for translated processes; the key does not exist elsewhere.
        $value = (& sysctl -in sysctl.proc_translated 2>$null)
        return ($LASTEXITCODE -eq 0 -and "$value".Trim() -eq '1')
    }
    if ($Os -eq 'linux') { return (@(Get-LinuxEmulationSignals $ProcRoot).Count -gt 0) }
    return $false
}

# A native proof is only meaningful on the matching real OS and process architecture. Mismatching OS/architecture,
# macOS Rosetta, and Linux qemu-user/Rosetta/box64/FEX translation (procfs probes above) are refused. These probes are
# best effort: the guarantee that a row came from a real x64 machine is the provisioned runner (ubuntu-24.04 in CI).
function Test-NativeAotHost([string] $Rid, [string] $Os, [string] $Arch, [Nullable[bool]] $Translated, [string] $ProcRoot = '/proc') {
    $target = ConvertFrom-NativeRid $Rid
    if (-not $Os) { $Os = Get-HostOs }
    if (-not $Arch) {
        $machine = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $process = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
        if ($machine -ne $process) { $Arch = "$process-on-$machine" } else { $Arch = $machine }
    }
    $signals = @()
    if ($null -eq $Translated) {
        if ($Os -eq 'linux') { $signals = @(Get-LinuxEmulationSignals $ProcRoot) }
        $Translated = if ($Os -eq 'linux') { $signals.Count -gt 0 } else { Test-ProcessTranslated $Os $ProcRoot }
    }
    $ok = $true
    $reason = "host $Os/$Arch matches $Rid"
    if ($Os -ne $target.os -or $Arch -ne $target.arch) {
        $ok = $false
        $reason = "$Rid needs a matching native runner (os=$($target.os) arch=$($target.arch)); this host is os=$Os arch=$Arch. Cross-publication and emulation do not count as native proof."
    } elseif ($Translated) {
        $ok = $false
        $detail = if ($signals.Count) { ' Signals: ' + ($signals -join '; ') + '.' } else { '' }
        $reason = "$Rid needs a matching native runner; this process is translated (emulated) on a different architecture.$detail"
    }
    return [pscustomobject]@{ ok = $ok; reason = $reason; os = $Os; arch = $Arch; translated = [bool]$Translated; signals = @($signals) }
}

# Read the executable header; never trust the file name or an external tool for the format.
function Get-NativeBinaryFormat([string] $Path) {
    $bytes = [byte[]]::new(24)
    $stream = [IO.File]::OpenRead($Path)
    try { $read = $stream.Read($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    if ($read -lt 20) { return 'unknown' }
    if ($bytes[0] -eq 0xCF -and $bytes[1] -eq 0xFA -and $bytes[2] -eq 0xED -and $bytes[3] -eq 0xFE) {
        $cpu = [BitConverter]::ToUInt32($bytes, 4)
        if ($cpu -eq 0x0100000C) { return 'macho-arm64' }
        if ($cpu -eq 0x01000007) { return 'macho-x64' }
        return 'unknown'
    }
    if ($bytes[0] -eq 0x7F -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4C -and $bytes[3] -eq 0x46) {
        $machine = [BitConverter]::ToUInt16($bytes, 18)
        if ($machine -eq 0x3E) { return 'elf-x64' }
        if ($machine -eq 0xB7) { return 'elf-arm64' }
    }
    return 'unknown'
}

# Every warning line is recorded. Only the exact Apple linker notice emitted by the .NET 8/9 ILCompiler targets on
# macOS is classified as a known toolchain item; everything else is unreviewed and fails the run.
function Find-NativeAotWarnings([string] $Text, [string] $Rid, [string] $Framework) {
    $found = @()
    $number = 0
    foreach ($line in ($Text -split "`r?`n")) {
        $number++
        if ($line -notmatch '(?i)\bwarning\b') { continue }
        if ($line -match '^\s*\d+\s+Warning\(s\)\s*$') { continue }
        $known = $line -match '^\s*ld: warning: -ld_classic is deprecated and will be removed in a future release\s*$' -and
            $Rid -like 'osx-*' -and $Framework -in @('net8.0', 'net9.0')
        $found += [pscustomobject]@{
            line = $number
            text = $line.Trim()
            disposition = $(if ($known) { 'toolchain-known' } else { 'unreviewed' })
            owner = $(if ($known) { 'Apple linker flag emitted by the .NET 8/9 ILCompiler targets; not a Meridian code path (E6/final toolchain triage)' } else { $null })
        }
    }
    return $found
}

# The native executable must complete exactly the expected case set: no missing, duplicate, failed or unexpected
# case, a consistent summary, the pinned runtime family and a native (non-JIT) runtime on the expected OS/arch.
function Test-NativeAotCaseOutput([string] $Output, [string[]] $ExpectedIds, [int] $ExitCode, [string] $Rid, [string] $Framework) {
    $target = ConvertFrom-NativeRid $Rid
    if ($ExitCode -ne 0) { throw "Native executable exited with $ExitCode." }
    $lines = @($Output -split "`r?`n")
    $failed = @($lines | Where-Object { $_ -match '^CASE \S+ FAIL' })
    if ($failed.Count) { throw "Native case FAIL: $($failed -join '; ')" }
    $passed = @($lines | ForEach-Object { if ($_ -match '^CASE (\S+) PASS$') { $Matches[1] } })
    $duplicates = @($passed | Group-Object | Where-Object Count -GT 1 | ForEach-Object Name)
    if ($duplicates.Count) { throw "Native case duplicate: $($duplicates -join ', ')" }
    $missing = @($ExpectedIds | Where-Object { $_ -notin $passed })
    if ($missing.Count) { throw "Native case missing: $($missing -join ', ')" }
    $unexpected = @($passed | Where-Object { $_ -notin $ExpectedIds })
    if ($unexpected.Count) { throw "Native case unexpected: $($unexpected -join ', ')" }
    $summary = @($lines | Where-Object { $_.StartsWith('SUMMARY ') })
    if ($summary.Count -ne 1 -or $summary[0] -notmatch '^SUMMARY schema=meridian-native-aot-cases/1 mode=native count=(\d+) expected=(\d+) ids=(\S*)$') {
        throw 'SUMMARY marker missing, repeated or not schema meridian-native-aot-cases/1 mode=native.'
    }
    if ([int]$Matches[1] -ne $passed.Count -or [int]$Matches[2] -ne $ExpectedIds.Count -or $Matches[3] -ne ($passed -join ',')) {
        throw 'SUMMARY marker disagrees with the executed CASE lines.'
    }
    $family = $Framework.Substring(3).Split('.')[0]
    $host_ = @($lines | Where-Object { $_.StartsWith('HOST runtime=') })
    if ($host_.Count -ne 1 -or $host_[0] -notmatch "^HOST runtime=$family\.") { throw "Wrong runtime family: expected $family, got '$($host_ -join ';')'." }
    $native = @($lines | Where-Object { $_.StartsWith('NATIVE ') })
    if ($native.Count -ne 1 -or $native[0] -notmatch '^NATIVE os=(\S+) arch=(\S+) dynamicCodeSupported=false dynamicCodeCompiled=false runtime=(.*)$') {
        throw 'NATIVE marker missing or not a non-JIT runtime (dynamicCodeSupported/dynamicCodeCompiled must be false).'
    }
    if ($Matches[1] -ne $target.os -or $Matches[2] -ne $target.arch) { throw "NATIVE marker os/arch $($Matches[1])/$($Matches[2]) differs from $Rid." }
    return [ordered]@{ caseIds = @($passed); os = $Matches[1]; arch = $Matches[2]; runtime = $Matches[3]; hostLine = $host_[0] }
}

# The readable sample and the packaged fixture must keep identical behavior files.
function Assert-NativeAotParity([string] $Repo) {
    foreach ($name in @('Program.cs', 'Registry.cs', 'Mapping.cs')) {
        $sample = Join-Path $Repo "samples/Meridian.NativeAot/$name"
        $consumer = Join-Path $Repo "tests/consumers/Meridian.NativeAotConsumer/$name"
        foreach ($path in @($sample, $consumer)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing native AOT file: $path" } }
        if ((Get-FileHash $sample -Algorithm SHA256).Hash -ne (Get-FileHash $consumer -Algorithm SHA256).Hash) {
            throw "Sample and packaged consumer differ: $name"
        }
    }
}

function Get-CommandText([string] $File, [string[]] $Arguments) {
    $command = Get-Command $File -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { return $null }
    try { return ((& $command.Source @Arguments 2>&1) -join "`n").Trim() } catch { return $null }
}

# Best-effort toolchain/OS receipt. Recording presence is not a test result.
function Get-NativeToolchainReceipt {
    $osRelease = $null
    if (Test-Path '/etc/os-release') { $osRelease = (Get-Content -Raw '/etc/os-release').Trim() }
    return [ordered]@{
        uname = Get-CommandText 'uname' @('-a')
        swVers = Get-CommandText 'sw_vers' @()
        osRelease = $osRelease
        clang = Get-CommandText 'clang' @('--version')
        xcodeSelect = Get-CommandText 'xcode-select' @('-p')
        sdkVersion = Get-CommandText 'xcrun' @('--show-sdk-version')
        ld = Get-CommandText 'ld' @('-v')
        gcc = Get-CommandText 'gcc' @('--version')
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        translated = Test-ProcessTranslated
        emulationSignals = $(if ((Get-HostOs) -eq 'linux') { @(Get-LinuxEmulationSignals) } else { @() })
    }
}

Export-ModuleMember -Function Get-NativeAotExpectedCases, Get-LinuxEmulationSignals, Test-NativeAotHost, Get-NativeBinaryFormat, Find-NativeAotWarnings, Test-NativeAotCaseOutput, Assert-NativeAotParity, Get-NativeToolchainReceipt

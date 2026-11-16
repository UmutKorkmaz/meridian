[CmdletBinding()]
param(
    # Exactly one explicit mode is required; neither is ever selected implicitly.
    [Parameter(Mandatory, ParameterSetName='MappingOnly')][switch] $MappingOnly,
    [Parameter(Mandatory, ParameterSetName='Combined')][switch] $Combined,
    [Parameter(Mandatory)][string] $Feed,
    [Parameter(Mandatory)][ValidatePattern('\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z')][string] $CandidateVersion,
    # Intent for a stable local candidate; never authorization to publish.
    [switch] $ReleasePreparation,
    [Parameter(Mandatory)][ValidateSet('net8.0','net9.0','net10.0')][string] $Framework,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $SdkVersion,
    [Parameter(Mandatory)][Alias('Rid')][ValidateSet('osx-arm64','linux-x64','linux-arm64','win-x64','win-arm64')][string] $RuntimeIdentifier,
    [Parameter(ParameterSetName='Combined')][string] $Manifest = 'eng/packages.json',
    [Parameter(ParameterSetName='Combined')][string] $Consumer = 'Meridian.NativeAotConsumer',
    [string] $OutputDirectory = 'artifacts/aot-smoke',
    [string] $RequiredChecksPolicy = 'eng/required-checks.json'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'package-inventory.psm1') -Force -DisableNameChecking
Assert-CandidateVersion $CandidateVersion ([bool] $ReleasePreparation)
if ($SdkVersion.Split('.')[0] -ne $Framework.Substring(3).Split('.')[0]) { throw 'Framework and SDK major version must match.' }
$repo = Split-Path -Parent $PSScriptRoot
$Feed = [IO.Path]::GetFullPath($Feed)
$policyPath = Resolve-OutputDirectory $repo $RequiredChecksPolicy
$policy = Get-Content -Raw -LiteralPath $policyPath | ConvertFrom-Json
$approved = @($policy.toolchains.currentServicing.lanes | Where-Object framework -EQ $Framework)
if ($approved.Count -ne 1 -or $approved[0].sdk -ne $SdkVersion) {
    throw "Native AOT requires the latest-patch SDK for $Framework from $RequiredChecksPolicy."
}
function Assert-NativeCompilerPackages($Assets) {
    $approvedRuntime = $approved[0].runtime
    $generic = @($Assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.DotNet.ILCompiler/*' })
    Assert-SameSet $generic @("Microsoft.DotNet.ILCompiler/$approvedRuntime") 'Restored ILCompiler reference differs from the approved native runtime patch.'
    $native = @($Assets.project.frameworks.$Framework.downloadDependencies | Where-Object { $_.name -like 'runtime.*.Microsoft.DotNet.ILCompiler' })
    if ($native.Count -ne 1 -or $native[0].name -ne "runtime.$RuntimeIdentifier.Microsoft.DotNet.ILCompiler" -or
        $native[0].version -notin @("[$approvedRuntime, $approvedRuntime]", "[$approvedRuntime]")) {
        throw 'Restored ILCompiler download differs from the approved native runtime patch or host RID.'
    }
}
function Execute([string] $File, [string[]] $Arguments, [string] $Log) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($File)
    $process.StartInfo.WorkingDirectory = $work
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.Environment['DOTNET_ROLL_FORWARD'] = 'LatestPatch'
    foreach ($argument in $Arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        "command=$File $($Arguments -join ' ')`nexit=$($process.ExitCode)`n$output" | Set-Content (Join-Path $work $Log)
        Write-Host $output
        if ($process.ExitCode -ne 0) { throw "AOT command failed ($($process.ExitCode)): $Log" }
        return $output
    } finally { $process.Dispose() }
}
if ($Combined) {
    Import-Module (Join-Path $PSScriptRoot 'native-aot.psm1') -Force -DisableNameChecking
    if ($RuntimeIdentifier -notin @('linux-x64', 'osx-arm64')) { throw "Combined mode supports only linux-x64 and osx-arm64; got $RuntimeIdentifier." }
    # Fail before touching the feed unless this really is the matching native host.
    $nativeHost = Test-NativeAotHost $RuntimeIdentifier
    if (-not $nativeHost.ok) { throw $nativeHost.reason }
    $expectedOs = if ($RuntimeIdentifier -eq 'osx-arm64') { 'osx' } else { 'linux' }
    $expectedArch = if ($RuntimeIdentifier -eq 'osx-arm64') { 'arm64' } else { 'x64' }
    $expectedFormat = if ($RuntimeIdentifier -eq 'osx-arm64') { 'macho-arm64' } else { 'elf-x64' }
    function Get-Sha([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
    $manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repo $Manifest }
    $inventory = Read-PackageInventory $repo $manifestPath
    $entries = @($inventory.consumers | Where-Object { $_.id -ceq $Consumer })
    if ($entries.Count -ne 1) { throw "Consumer must be inventoried exactly once: $Consumer" }
    $entry = $entries[0]
    $closure = @(Get-PackageClosure $inventory @($entry.packageIds))
    Assert-NativeAotParity $repo
    # Inspect every inventory archive and analyzer producer record. Combined mode never repacks or restamps a candidate.
    & (Join-Path $PSScriptRoot 'pack-packages.ps1') -OutputDirectory $Feed -CandidateVersion $CandidateVersion -Manifest $manifestPath -InspectOnly -ReleasePreparation:$ReleasePreparation
    $record = Get-Content -Raw (Join-Path $Feed "package-manifest.$CandidateVersion.json") | ConvertFrom-Json
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $work = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$CandidateVersion/combined-$Framework-$SdkVersion-$RuntimeIdentifier-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))"
    $null = New-Item -ItemType Directory -Force $work
    '<Project />' | Set-Content (Join-Path $work 'Directory.Build.props')
    '<Project />' | Set-Content (Join-Path $work 'Directory.Build.targets')
    @{sdk=@{version=$SdkVersion;rollForward='disable';allowPrerelease=$false}} | ConvertTo-Json | Set-Content (Join-Path $work 'global.json')
    # Only the manifest-declared source files are copied; nothing outside the consumer boundary can satisfy the build.
    $sourceDir = Split-Path (Resolve-RepositoryPath $repo $entry.project)
    $fixture = @()
    foreach ($sourcePath in $entry.sourceFiles) {
        $source = Resolve-RepositoryPath $repo $sourcePath
        $relative = [IO.Path]::GetRelativePath($sourceDir, $source)
        $destination = Join-Path $work $relative
        $null = New-Item -ItemType Directory -Force (Split-Path $destination)
        Copy-Item -LiteralPath $source -Destination $destination
        if ((Get-Sha $source) -ne (Get-Sha $destination)) { throw "Fixture copy differs from source: $sourcePath" }
        $fixture += [ordered]@{ source=$sourcePath; file=$relative; sha256=(Get-Sha $destination) }
    }
    ConvertTo-Json -InputObject $fixture -Depth 5 | Set-Content (Join-Path $work 'fixture-hashes.json')
    # Every inventory ID maps to the candidate feed so no inventoried package can fall through to public restore.
    $escapedFeed = [Security.SecurityElement]::Escape($Feed)
    $patterns = ($inventory.packages | ForEach-Object { "<package pattern=`"$($_.id)`" />" }) -join ''
    "<configuration><packageSources><clear/><add key=`"candidate`" value=`"$escapedFeed`"/><add key=`"dependencies`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"candidate`">$patterns</packageSource><packageSource key=`"dependencies`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" | Set-Content (Join-Path $work 'NuGet.Config')
    $actualSdk = (Execute $dotnet @('--version') 'sdk.log').Trim()
    if ($actualSdk -ne $SdkVersion) { throw "Wrong selected SDK: $actualSdk" }
    $null = Execute $dotnet @('--info') 'host.log'
    $installed = Execute $dotnet @('--list-sdks') 'installed-sdks.log'
    $sdkLine = @($installed -split "`r?`n" | Where-Object { $_.StartsWith("$actualSdk ") })
    if ($sdkLine.Count -ne 1 -or $sdkLine[0] -notmatch '\[(.+)\]$') { throw 'Cannot locate the selected compiler.' }
    $compiler = Join-Path $Matches[1] "$actualSdk/Roslyn/bincore/csc.dll"
    $compilerVersion = (Execute $dotnet @('exec', $compiler, '-version') 'compiler.log').Trim()
    $toolchain = Get-NativeToolchainReceipt
    $toolchain | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'toolchain.json')

    $projectName = [IO.Path]::GetFileName($entry.project)
    $project = Join-Path $work $projectName
    $packages = Join-Path $work 'packages'
    $buildProperties = @('-p:NativeAotSmoke=true', "-p:ConsumerFramework=$Framework", "-p:MeridianPackageVersion=$CandidateVersion")
    Push-Location $work
    try {
        $evaluated = Invoke-ProjectEvaluation $dotnet $project @('LangVersion','TreatWarningsAsErrors','AssemblyName','TargetFramework','PublishAot','PublishTrimmed','TrimmerSingleWarn') @('PackageReference','ProjectReference') ($buildProperties + "-p:RuntimeIdentifier=$RuntimeIdentifier")
    } finally { Pop-Location }
    if (@($evaluated.Items.ProjectReference).Count) { throw 'The native consumer must not use a source ProjectReference.' }
    if ($evaluated.Properties.LangVersion -notin @('12','12.0') -or $evaluated.Properties.TreatWarningsAsErrors -ne 'true') { throw 'The native consumer must enforce C# 12 and warnings as errors.' }
    if ($evaluated.Properties.TargetFramework -ne $Framework) { throw "Wrong consumer target framework: $($evaluated.Properties.TargetFramework)" }
    if ($evaluated.Properties.PublishAot -ne 'true' -or $evaluated.Properties.PublishTrimmed -ne 'true' -or $evaluated.Properties.TrimmerSingleWarn -ne 'false') { throw 'NativeAotSmoke must select PublishAot, PublishTrimmed and per-site trim warnings.' }
    $roots = @($evaluated.Items.PackageReference | Where-Object { $_.Identity -in $inventory.packages.id })
    Assert-SameSet @($roots.Identity) @($entry.packageIds) 'Consumer root dependency mismatch.'
    foreach ($root in $roots) { if ($root.Version -ne "[$CandidateVersion]") { throw "Consumer package version must be exact: $($root.Identity)" } }
    $null = Execute $dotnet (@('restore', $projectName, '-r', $RuntimeIdentifier, '--packages', $packages, '--configfile', 'NuGet.Config') + $buildProperties) 'restore.log'
    $assetsPath = Join-Path $work 'obj/project.assets.json'
    $assets = Get-Content -Raw $assetsPath | ConvertFrom-Json
    Assert-NativeCompilerPackages $assets
    $libraries = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name.Split('/')[0] -in $inventory.packages.id })
    Assert-SameSet @($libraries | ForEach-Object { $_.Name.Split('/')[0] }) @($closure.id) 'Restored inventory closure mismatch.'
    foreach ($library in $libraries) { if ($library.Name.Split('/')[1] -ne $CandidateVersion) { throw "Restored inventory package is not the candidate version: $($library.Name)" } }
    $restoredHashes = @()
    $analyzerInputs = @()
    foreach ($package in $closure) {
        $id = $package.id.ToLowerInvariant()
        $restored = Join-Path $packages "$id/$CandidateVersion/$id.$CandidateVersion.nupkg"
        $packed = Join-Path $Feed "$($package.id).$CandidateVersion.nupkg"
        $recorded = @($record.packages | Where-Object id -EQ $package.id)
        if ($recorded.Count -ne 1) { throw "Package record missing: $($package.id)" }
        $hash = Get-Sha $restored
        if ($hash -ne (Get-Sha $packed) -or $hash -ne $recorded[0].sha256) { throw "Restored package differs from the inspected candidate archive: $($package.id)" }
        $restoredHashes += [ordered]@{ id=$package.id; version=$CandidateVersion; sha256=$hash }
        foreach ($analyzer in $package.analyzerPaths) {
            $actual = Get-Sha (Join-Path $packages "$id/$CandidateVersion/$($analyzer.packagePath)")
            $binding = @($recorded[0].analyzers | Where-Object packagePath -EQ $analyzer.packagePath)
            if ($binding.Count -ne 1 -or $actual -ne $binding[0].sha256 -or $actual -ne $binding[0].producerSha256) { throw "Restored analyzer differs from its recorded producer output: $($analyzer.packagePath)" }
            $analyzerInputs += [ordered]@{ package=$package.id; packagePath=$analyzer.packagePath; sha256=$actual; producer=$analyzer.project }
        }
    }
    $components = @(Read-RestoredComponents $assets $packages)
    $nativeToolchain = @()
    foreach ($targetFramework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($download in @($targetFramework.Value.downloadDependencies)) {
            if (-not $download) { continue }
            $id = $download.name.ToLowerInvariant()
            $version = ([string]$download.version).Trim('[', ']').Split(',')[0].Trim()
            $nupkg = Join-Path $packages "$id/$version/$id.$version.nupkg"
            $nativeToolchain += [ordered]@{ id=$download.name; version=$version; sha256=$(if (Test-Path -LiteralPath $nupkg) { Get-Sha $nupkg } else { $null }) }
        }
    }
    [ordered]@{ restoredInventory=$restoredHashes; analyzerInputs=$analyzerInputs; components=$components; nativeToolchainPackages=$nativeToolchain; assetsSha256=(Get-Sha $assetsPath); nugetConfigSha256=(Get-Sha (Join-Path $work 'NuGet.Config')) } | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $work 'restore-receipt.json')
    Copy-Item -LiteralPath (Join-Path $Feed "package-manifest.$CandidateVersion.json") -Destination (Join-Path $work "package-manifest.$CandidateVersion.json")

    $publishDirectory = Join-Path $work 'publish'
    $null = Execute $dotnet (@('publish', $projectName, '-c', 'Release', '-r', $RuntimeIdentifier, '--no-restore', '-o', $publishDirectory, "-bl:$(Join-Path $work 'publish.binlog')") + $buildProperties) 'publish.log'
    $warnings = @(Find-NativeAotWarnings ((Get-Content -Raw (Join-Path $work 'restore.log')) + "`n" + (Get-Content -Raw (Join-Path $work 'publish.log'))) $RuntimeIdentifier $Framework)
    ConvertTo-Json -InputObject $warnings -Depth 4 | Set-Content (Join-Path $work 'warnings.json')
    $unreviewed = @($warnings | Where-Object disposition -EQ 'unreviewed')
    if ($unreviewed.Count) { throw "Unreviewed compiler/linker warnings ($($unreviewed.Count)): $($unreviewed[0].text)" }
    $generated = @(Get-ChildItem (Join-Path $work 'obj/generated') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { [ordered]@{ path=[IO.Path]::GetRelativePath($work, $_.FullName); sha256=(Get-Sha $_.FullName) } })
    ConvertTo-Json -InputObject $generated -Depth 4 | Set-Content (Join-Path $work 'generated-sources.json')
    foreach ($generator in @('Meridian.Mediator.Generators', 'Meridian.Mapping.Generators')) {
        if (-not @($generated | Where-Object { $_.path -like "*$generator*" }).Count) { throw "No retained generated source from $generator" }
    }

    $binary = Join-Path $publishDirectory $evaluated.Properties.AssemblyName
    $format = Get-NativeBinaryFormat $binary
    if ($format -ne $expectedFormat) { throw "Published executable is $format, expected $expectedFormat." }
    $before = Get-Sha $binary
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($binary)
    $process.StartInfo.WorkingDirectory = $work
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in @('--require-native', '--expect-os', $expectedOs, '--expect-arch', $expectedArch)) { $process.StartInfo.ArgumentList.Add($argument) }
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) { $process.Kill($true); throw 'Native executable timed out after 300 seconds.' }
        $process.WaitForExit()
        $executeOutput = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    } finally { $process.Dispose() }
    "command=$binary --require-native --expect-os $expectedOs --expect-arch $expectedArch`nexit=$exitCode`n$executeOutput" | Set-Content (Join-Path $work 'execute.log')
    Write-Host $executeOutput
    if ((Get-Sha $binary) -ne $before) { throw 'The executable changed while it ran.' }
    $proof = Test-NativeAotCaseOutput $executeOutput (Get-NativeAotExpectedCases) $exitCode $RuntimeIdentifier $Framework
    $retainedFile = 'combined.native-executable'
    Copy-Item -LiteralPath $binary -Destination (Join-Path $work $retainedFile)
    if ((Get-Sha (Join-Path $work $retainedFile)) -ne $before) { throw 'The retained native executable differs from the executed bytes.' }
    [ordered]@{
        mode = 'combined'; consumer = $Consumer; candidateVersion = $CandidateVersion
        framework = $Framework; sdk = $actualSdk; compiler = $compilerVersion; rid = $RuntimeIdentifier
        file = $binary; retainedFile = $retainedFile; sha256 = $before; format = $format; exit = $exitCode
        nativeRuntime = $proof.runtime; caseIds = $proof.caseIds; caseCount = $proof.caseIds.Count
        hostOs = $nativeHost.os; hostArch = $nativeHost.arch
        manifestSha256 = (Get-Sha $manifestPath); packageRecordSha256 = (Get-Sha (Join-Path $Feed "package-manifest.$CandidateVersion.json"))
        warningCount = $warnings.Count; unreviewedWarnings = 0
        fixture = $fixture; closure = @($closure.id)
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $work 'native-result.json')
    Write-Host "PASS combined native consumer: $work"
    return
}
# MappingOnly: unchanged fixture, assertions and result contract.
# Inspection validates existing pack receipts and hashes. It never repacks a candidate.
& (Join-Path $PSScriptRoot 'pack-packages.ps1') -OutputDirectory $Feed -CandidateVersion $CandidateVersion -InspectOnly -ReleasePreparation:$ReleasePreparation
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$work = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$CandidateVersion/$Framework-$SdkVersion-$RuntimeIdentifier-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))"
$null = New-Item -ItemType Directory -Force $work
'<Project />' | Set-Content (Join-Path $work 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $work 'Directory.Build.targets')
@{sdk=@{version=$SdkVersion;rollForward='disable';allowPrerelease=$false}} | ConvertTo-Json | Set-Content (Join-Path $work 'global.json')
$fixture = Join-Path $repo 'tests/consumers/Meridian.GeneratedOnlyConsumer'
Copy-Item (Join-Path $fixture '*.cs') $work
Copy-Item (Join-Path $fixture '*.csproj') $work
@(Get-ChildItem $work -File | ForEach-Object { @{file=$_.Name;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} }) | ConvertTo-Json | Set-Content (Join-Path $work 'fixture-hashes.json')
$escapedFeed = [Security.SecurityElement]::Escape($Feed)
"<configuration><packageSources><clear/><add key=`"candidate`" value=`"$escapedFeed`"/><add key=`"dependencies`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"candidate`"><package pattern=`"Meridian.Mapping`"/></packageSource><packageSource key=`"dependencies`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" | Set-Content (Join-Path $work 'NuGet.Config')
$actualSdk = (Execute $dotnet @('--version') 'sdk.log').Trim()
if ($actualSdk -ne $SdkVersion) { throw "Wrong selected SDK: $actualSdk" }
$null = Execute $dotnet @('--info') 'host.log'
$installed = Execute $dotnet @('--list-sdks') 'installed-sdks.log'
$sdkLine = @($installed -split "`r?`n" | Where-Object { $_.StartsWith("$actualSdk ") })
if ($sdkLine.Count -ne 1 -or $sdkLine[0] -notmatch '\[(.+)\]$') { throw 'Cannot locate the selected compiler.' }
$compiler = Join-Path $Matches[1] "$actualSdk/Roslyn/bincore/csc.dll"
$null = Execute $dotnet @('exec', $compiler, '-version') 'compiler.log'
$project = 'Meridian.GeneratedOnlyConsumer.csproj'
$properties = @("-p:ConsumerFramework=$Framework", "-p:MeridianPackageVersion=$CandidateVersion")
$packages = Join-Path $work 'packages'
$null = Execute $dotnet (@('restore', $project, '-r', $RuntimeIdentifier, '--packages', $packages, '--configfile', 'NuGet.Config') + $properties) 'restore.log'
Assert-NativeCompilerPackages (Get-Content -Raw -LiteralPath (Join-Path $work 'obj/project.assets.json') | ConvertFrom-Json)
$packed = Join-Path $Feed "Meridian.Mapping.$CandidateVersion.nupkg"
$restored = Join-Path $packages "meridian.mapping/$CandidateVersion/meridian.mapping.$CandidateVersion.nupkg"
if ((Get-FileHash $packed).Hash -ne (Get-FileHash $restored).Hash) { throw 'Restored package differs from accepted candidate.' }
@{file=$packed;sha256=(Get-FileHash $packed -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content (Join-Path $work 'package-hash.json')
$null = Execute $dotnet (@('publish', $project, '-c', 'Release', '-r', $RuntimeIdentifier, '--no-restore', '-o', (Join-Path $work 'publish')) + $properties) 'publish.log'
$binary = Join-Path $work 'publish/Meridian.GeneratedOnlyConsumer'
if ($RuntimeIdentifier.StartsWith('win-')) { $binary += '.exe' }
$before = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
$output = Execute $binary @() 'execute.log'
if (-not $output.Contains('generated-only: value=42 cycle=true collectionIdentity=true depth=true limit=true cancellation=true')) { throw 'Native execution did not return the expected semantic proof.' }
if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $before) { throw 'The mapping executable changed while it ran.' }
$retainedFile = 'mapping.native-executable'
Copy-Item -LiteralPath $binary -Destination (Join-Path $work $retainedFile)
if ((Get-FileHash -LiteralPath (Join-Path $work $retainedFile) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $before) { throw 'The retained mapping executable differs from the executed bytes.' }
@{file=$binary;retainedFile=$retainedFile;sha256=$before;sdk=$actualSdk;framework=$Framework;rid=$RuntimeIdentifier} | ConvertTo-Json | Set-Content (Join-Path $work 'native-result.json')
Write-Host "PASS mapping-only native consumer: $work"

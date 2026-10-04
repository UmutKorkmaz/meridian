[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch] $MappingOnly,
    [Parameter(Mandatory)][string] $Feed,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$')][string] $CandidateVersion,
    [Parameter(Mandatory)][ValidateSet('net8.0','net9.0','net10.0')][string] $Framework,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $SdkVersion,
    [Parameter(Mandatory)][ValidateSet('osx-arm64','linux-x64','linux-arm64','win-x64','win-arm64')][string] $RuntimeIdentifier,
    [string] $OutputDirectory = 'artifacts/aot-smoke'
)
$ErrorActionPreference = 'Stop'
if (-not $MappingOnly) { throw 'Select -MappingOnly; combined mediator coverage is not available in this task.' }
if ($SdkVersion.Split('.')[0] -ne $Framework.Substring(3).Split('.')[0]) { throw 'Framework and SDK major version must match.' }
$repo = Split-Path -Parent $PSScriptRoot
$Feed = [IO.Path]::GetFullPath($Feed)
# Inspection validates existing pack receipts and hashes. It never repacks a candidate.
& (Join-Path $PSScriptRoot 'pack-packages.ps1') -OutputDirectory $Feed -CandidateVersion $CandidateVersion -InspectOnly
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
$packed = Join-Path $Feed "Meridian.Mapping.$CandidateVersion.nupkg"
$restored = Join-Path $packages "meridian.mapping/$CandidateVersion/meridian.mapping.$CandidateVersion.nupkg"
if ((Get-FileHash $packed).Hash -ne (Get-FileHash $restored).Hash) { throw 'Restored package differs from accepted candidate.' }
@{file=$packed;sha256=(Get-FileHash $packed -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content (Join-Path $work 'package-hash.json')
$null = Execute $dotnet (@('publish', $project, '-c', 'Release', '-r', $RuntimeIdentifier, '--no-restore', '-o', (Join-Path $work 'publish')) + $properties) 'publish.log'
$binary = Join-Path $work 'publish/Meridian.GeneratedOnlyConsumer'
if ($RuntimeIdentifier.StartsWith('win-')) { $binary += '.exe' }
$output = Execute $binary @() 'execute.log'
if (-not $output.Contains('generated-only: value=42 cycle=true collectionIdentity=true depth=true limit=true cancellation=true')) { throw 'Native execution did not return the expected semantic proof.' }
@{file=$binary;sha256=(Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant();sdk=$actualSdk;framework=$Framework;rid=$RuntimeIdentifier} | ConvertTo-Json | Set-Content (Join-Path $work 'native-result.json')
Write-Host "PASS mapping-only native consumer: $work"

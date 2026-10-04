[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Feed,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$')][string] $CandidateVersion,
    [string] $OutputDirectory = 'artifacts/ef10-compatibility'
)
$ErrorActionPreference = 'Stop'
if (-not $env:MERIDIAN_TEST_POSTGRES -or -not $env:MERIDIAN_TEST_REDIS) { throw 'Both owned MERIDIAN_TEST_POSTGRES and MERIDIAN_TEST_REDIS providers are required.' }
$repo = Split-Path -Parent $PSScriptRoot
$run = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Force $run
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.targets')
'{"sdk":{"version":"10.0.300","rollForward":"disable","allowPrerelease":false}}' | Set-Content (Join-Path $run 'global.json')
$files = @('tests/compatibility/Meridian.Ef10Compatibility/Meridian.Ef10Compatibility.csproj')
$files += @(Get-ChildItem (Join-Path $repo 'tests/Meridian.Mediator.EntityFrameworkCore.Tests') -Filter '*.cs' | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) })
$receipt = @()
foreach ($file in $files) {
    $source = Join-Path $repo $file
    $target = Join-Path $run ([IO.Path]::GetFileName($source))
    Copy-Item -LiteralPath $source -Destination $target
    $receipt += [ordered]@{source=$file;sha256=(Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()}
}
ConvertTo-Json -InputObject $receipt -Depth 5 | Set-Content (Join-Path $run 'source-hashes.json')
$escapedFeed = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($Feed))
"<configuration><packageSources><clear/><add key=`"candidate`" value=`"$escapedFeed`"/><add key=`"dependencies`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"candidate`"><package pattern=`"Meridian.*`"/></packageSource><packageSource key=`"dependencies`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" | Set-Content (Join-Path $run 'NuGet.Config')
Push-Location $run
try {
    $env:DOTNET_ROLL_FORWARD = 'LatestPatch'; $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = '0'
    & dotnet --info | Tee-Object -FilePath host.log
    if ($LASTEXITCODE -ne 0) { throw 'EF10 host probe failed.' }
    & dotnet test Meridian.Ef10Compatibility.csproj -c Release "-p:MeridianPackageVersion=$CandidateVersion" "-p:RestorePackagesPath=$run/packages" --logger 'trx;LogFileName=ef10.trx' --results-directory $run 2>&1 | Tee-Object -FilePath test.log
    if ($LASTEXITCODE -ne 0) { throw 'EF10 compatibility tests failed.' }
} finally { Pop-Location }
Write-Host "PASS EF10 compatibility: $run"

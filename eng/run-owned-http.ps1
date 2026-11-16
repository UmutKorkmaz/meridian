[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HelperAssembly,
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string] $RunArgumentsPath,
    [Parameter(Mandatory)][string] $WorkingDirectory,
    [Parameter(Mandatory)][string] $OwnershipPath,
    [Parameter(Mandatory)][string] $OwnershipToken
)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -Path $HelperAssembly
    $kind = [Meridian.Tools.OwnedHttpProcess]::EstablishOwnership()
    # Publish an atomic private receipt before any inner child can be launched.
    $ownership = @{ pid = $PID; token = $OwnershipToken; kind = $kind } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText("$OwnershipPath.tmp", $ownership)
    [IO.File]::Move("$OwnershipPath.tmp", $OwnershipPath)
    [string[]] $runArguments = Get-Content -LiteralPath $RunArgumentsPath -Raw | ConvertFrom-Json
    $result = [Meridian.Tools.OwnedHttpProcess]::Run($Executable, $runArguments, $WorkingDirectory)
    exit $result
} catch {
    [Console]::Error.WriteLine("Owned HTTP supervisor failed: $_")
    exit 1
}

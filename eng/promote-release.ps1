[CmdletBinding()]
param(
    # candidate.json of a prepared candidate directory.
    [string] $Manifest,
    # SHA-256 of the exact candidate.json bytes that were presented for authorization (the approval anchor).
    [string] $ApprovedManifestSha256,
    # Append-only, hash-chained promotion receipt stream (JSON lines) OUTSIDE the candidate directory. A plan only reads it.
    [string] $Receipts,
    # Where the read-only plan JSON is written (outside the candidate directory).
    [string] $Output,
    # Read nothing remote. The plan is then BLOCKED because remote truth was not observed.
    [switch] $Offline,
    # Request execution. It fails closed (exit 3): no trusted authorization verifier exists in this repository.
    [switch] $Execute,
    # Accepted only to state clearly that a self-authored approval document is never authority.
    [string] $Authorization,
    # Stable candidates need the same explicit intent as preparation; never authorization.
    [switch] $ReleasePreparation
)
# E5 promotion (eng/release_candidate.py plan|execute). The default is a READ-ONLY plan: GET requests to GitHub and the NuGet
# flat container (GITHUB_TOKEN, if set, is sent only to api.github.com), ordered steps and the recovery decision for each
# target. Nothing is built, packed, tagged, uploaded or pushed.
# Exit codes: 0 plan ready (no stop condition), 1 failed validation or stop condition (digest mismatch, lost/corrupted bundle,
# tag elsewhere, collision, corrupted receipts), 2 invalid arguments, 3 BLOCKED (offline/unobservable/uncertain remote state,
# or -Execute without trusted authorization). See docs/release-runbook.md.
$ErrorActionPreference = 'Stop'
if (-not $Manifest -or -not $ApprovedManifestSha256) {
    Write-Host 'FAIL: -Manifest and -ApprovedManifestSha256 (the SHA-256 of the exact candidate.json bytes) are required.'
    exit 2
}
$python = $null
foreach ($name in 'python3', 'python') {
    $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { continue }
    & $command.Source -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>$null
    if ($LASTEXITCODE -eq 0) { $python = $command.Source; break }
}
if (-not $python) { Write-Host 'BLOCKED: Python 3.9 or newer is required (python3 or python on PATH).'; exit 3 }

function Resolve-UserPath([string] $Path) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }

$mode = if ($Execute) { 'execute' } else { 'plan' }
$arguments = @('-I', '-B', (Join-Path $PSScriptRoot 'release_candidate.py'), $mode,
    '--manifest', (Resolve-UserPath $Manifest), '--approved-sha256', $ApprovedManifestSha256)
if ($ReleasePreparation) { $arguments += '--release-preparation' }
if ($Receipts) { $arguments += @('--receipts', (Resolve-UserPath $Receipts)) }
if ($Execute) {
    if ($Authorization) { $arguments += @('--authorization', (Resolve-UserPath $Authorization)) }
} else {
    if ($Offline) { $arguments += '--offline' }
    if ($Output) { $arguments += @('--output', (Resolve-UserPath $Output)) }
}
& $python @arguments
exit $LASTEXITCODE

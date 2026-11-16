[CmdletBinding()]
param(
    # The authoritative ledger: one machine-readable JSON block in docs/deprecations.md.
    [string] $Ledger = 'docs/deprecations.md',
    # Where the meridian-deprecation-validation/1 report is written.
    [string] $Output,
    # Immutable package-manifest.<version>.json; lifecycle authorization/report binds these exact bytes.
    [string] $Candidate,
    # An exact-action authorization record. This repository has no trusted verifier, so a gated action still fails.
    [string] $Authorization,
    # Complete inline NuGet registration responses: both anchored hashes and release contents must match the ledger.
    [string[]] $ReleaseHistoryEvidence = @(),
    # Old/new semantic comparison rows. E4 separately verifies actual package-consumer execution provenance.
    [string] $ConsumerResults
)
# E7 deprecation lifecycle validation. Exit codes: 0 PASS, 1 FAIL (ineligible gated action, invalid metadata or a broken
# retained contract), 2 invalid input. The validator always uses the real UTC clock; there is no clock parameter.
# See docs/deprecations.md and eng/deprecation_policy.py.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function Resolve-RepoPath([string] $Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath([IO.Path]::Combine($repo, $Path))
}
$python = $null
foreach ($name in 'python3', 'python') {
    $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { continue }
    & $command.Source -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>$null
    if ($LASTEXITCODE -eq 0) { $python = $command.Source; break }
}
if (-not $python) { Write-Host 'FAIL: Python 3.9 or newer is required (python3 or python on PATH).'; exit 2 }
$arguments = @('-B', (Join-Path $PSScriptRoot 'deprecation_policy.py'), 'validate', '--ledger', (Resolve-RepoPath $Ledger), '--repo', $repo)
if ($Output) { $arguments += @('--output', (Resolve-RepoPath $Output)) }
if ($Candidate) { $arguments += @('--candidate', (Resolve-RepoPath $Candidate)) }
if ($Authorization) { $arguments += @('--authorization', (Resolve-RepoPath $Authorization)) }
foreach ($path in $ReleaseHistoryEvidence) { $arguments += @('--release-history-evidence', (Resolve-RepoPath $path)) }
if ($ConsumerResults) { $arguments += @('--consumer-results', (Resolve-RepoPath $ConsumerResults)) }
& $python @arguments
exit $LASTEXITCODE

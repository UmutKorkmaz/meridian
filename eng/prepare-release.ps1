[CmdletBinding()]
param(
    # Canonical three-part version of the candidate. A stable identity additionally needs -ReleasePreparation.
    [string] $CandidateVersion,
    # The NEW candidate directory. It must not exist: a new attempt always gets a new directory and candidate id.
    [string] $EvidenceDirectory,
    # The feed eng/pack-packages.ps1 already produced ONCE (packages plus package-manifest.<version>.json). It is adopted
    # byte-for-byte; this script never packs, rebuilds, renames or rewrites a package.
    [string] $Feed,
    # E4 cumulative evidence directory (lanes/<lane>/lane-result.json and retained raw files). eng/candidate_gate.py is
    # re-run in aggregate mode over the adopted copies; a recorded summary file is never trusted.
    [string] $GateEvidence,
    # Intent for a stable local candidate (the same guard as pack-packages.ps1 and verify-*.ps1); never authorization.
    [switch] $ReleasePreparation,
    # Read-only remote availability snapshot (stale-able, not a reservation).
    [string] $AvailabilitySnapshot,
    # meridian-release-receipt/1 documents bound to the source commit (and, for the final review, the package manifest).
    [string] $E6Receipt,
    [string] $FinalReviewReceipt,
    # Read-only `gh api repos/<repo>/branches/<branch>/protection` snapshot and optional rulesets snapshot.
    [string] $GovernanceProtection,
    [string] $GovernanceRulesets,
    # Release notes for this exact version (become metadata/release-notes.md and the GitHub release body).
    [string] $ReleaseNotes,
    [string] $Repository = 'UmutKorkmaz/meridian'
)
# E5 local release preparation (eng/release_candidate.py prepare). No build, pack, upload, tag or other remote action.
# Exit codes: 0 prepared (every required evidence row passed; the approval anchor is the SHA-256 of the exact
# candidate.json bytes, printed and written next to the directory), 1 failed validation (the attempt is quarantined next to
# the requested directory, never resumed), 2 invalid arguments (including a stable version without -ReleasePreparation or a
# prerelease with it), 3 BLOCKED (missing evidence, governance, E7 validator or receipts; quarantined, never prepared).
# See docs/release-runbook.md.
$ErrorActionPreference = 'Stop'
if (-not $CandidateVersion -or -not $EvidenceDirectory -or -not $Feed) {
    Write-Host 'FAIL: -CandidateVersion, -EvidenceDirectory (the new candidate directory) and -Feed (the once-packed feed) are required.'
    exit 2
}
Import-Module (Join-Path $PSScriptRoot 'package-inventory.psm1') -Force -DisableNameChecking
try { Assert-CandidateVersion $CandidateVersion ([bool] $ReleasePreparation) }
catch { Write-Host "FAIL: $($_.Exception.Message)"; exit 2 }

$python = $null
foreach ($name in 'python3', 'python') {
    $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { continue }
    & $command.Source -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>$null
    if ($LASTEXITCODE -eq 0) { $python = $command.Source; break }
}
if (-not $python) { Write-Host 'BLOCKED: Python 3.9 or newer is required (python3 or python on PATH).'; exit 3 }

function Resolve-UserPath([string] $Path) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }

$arguments = @('-I', '-B', (Join-Path $PSScriptRoot 'release_candidate.py'), 'prepare',
    '--candidate-version', $CandidateVersion,
    '--candidate-dir', (Resolve-UserPath $EvidenceDirectory),
    '--feed', (Resolve-UserPath $Feed),
    '--repository', $Repository,
    '--repo', (Split-Path -Parent $PSScriptRoot))
if ($ReleasePreparation) { $arguments += '--release-preparation' }
$optional = [ordered]@{
    '--gate-evidence' = $GateEvidence
    '--availability-snapshot' = $AvailabilitySnapshot
    '--e6-receipt' = $E6Receipt
    '--final-review-receipt' = $FinalReviewReceipt
    '--governance-protection' = $GovernanceProtection
    '--governance-rulesets' = $GovernanceRulesets
    '--release-notes' = $ReleaseNotes
}
foreach ($option in $optional.Keys) {
    if ($optional[$option]) { $arguments += @($option, (Resolve-UserPath $optional[$option])) }
}
& $python @arguments
exit $LASTEXITCODE

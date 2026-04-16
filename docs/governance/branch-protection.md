# Branch Protection — Setup Checklist

GitHub branch protection must be configured in the repository's web UI.
This document exists so the settings are recorded and reproducible if the
repo is ever re-created or migrated.

## Target branch

`main` on `github.com/UmutKorkmaz/meridian`.

## Required settings

Go to `Settings → Branches → Branch protection rules → Add rule`:

- Branch name pattern: `main`
- ✅ Require a pull request before merging
  - ✅ Require approvals — minimum **1** (raise to 2 after a second
    maintainer is onboarded per PLAN.md §8.4)
  - ✅ Dismiss stale pull request approvals when new commits are pushed
  - ✅ Require review from Code Owners (once `CODEOWNERS` is added)
- ✅ Require status checks to pass before merging
  - ✅ Require branches to be up to date before merging
  - Required checks (add as CI workflow lands in Phase 2):
    - `build (net8.0)`
    - `build (net9.0)`
    - `build (net10.0)`
    - `test`
    - `codeql`
    - `scorecard`
- ✅ Require conversation resolution before merging
- ✅ Require signed commits (GPG or SSH signatures)
- ✅ Require linear history (no merge commits — use squash or rebase)
- ✅ Do not allow bypassing the above settings
- ✅ Restrict who can push to matching branches — empty (no direct push)
- ❌ Allow force pushes
- ❌ Allow deletions

## Verification

After configuring:

1. Open a test PR against `main`.
2. Try to merge without approval — should be blocked.
3. Try to push directly to `main` — should be rejected.
4. Check that status-check names in the workflow file exactly match the
   required checks list above (typos silently disable the check).

## Notes

- The CI workflow (Phase 2 of PLAN.md) must run BEFORE enabling "Require
  branches to be up to date" — otherwise new PRs have no checks to satisfy
  and merging is permanently blocked.
- Signed commits are a Scorecard requirement and a SLSA L3 input. Developers
  need a GPG or SSH signing key configured locally; see
  <https://docs.github.com/en/authentication/managing-commit-signature-verification>.
- "Restrict who can push" left empty is intentional — the protection rule is
  the enforcement mechanism, not an allowlist.

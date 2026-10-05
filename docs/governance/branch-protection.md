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
    maintainer is onboarded per [docs/PLAN.md](../PLAN.md) §8.4)
  - ✅ Dismiss stale pull request approvals when new commits are pushed
  - ✅ Require review from Code Owners (`.github/CODEOWNERS` is present)
- ✅ Require status checks to pass before merging
  - ✅ Require branches to be up to date before merging
  - Required checks — use the **exact** GitHub Actions check names:

    From `.github/workflows/ci.yml` (job `name:`):
    - `build + test (ubuntu-latest, net8.0)`
    - `build + test (ubuntu-latest, net9.0)`
    - `build + test (ubuntu-latest, net10.0)`
    - `build + test (windows-latest, net8.0)`
    - `build + test (windows-latest, net9.0)`
    - `build + test (windows-latest, net10.0)`
    - `build + test (macos-latest, net8.0)`
    - `build + test (macos-latest, net9.0)`
    - `build + test (macos-latest, net10.0)`

    From other workflows (enable when those workflows run on PRs/main):
    - CodeQL / Scorecard check names as shown in the Actions UI for
      `codeql.yml` and `scorecard.yml` (do not invent shorter aliases —
      typos silently disable enforcement)

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
4. Confirm required-check names match the Actions UI exactly.

## Notes

- CI already runs on every PR to `main` (see `.github/workflows/ci.yml`).
  Enable “Require branches to be up to date” only after the check names
  above appear at least once on a PR.
- Signed commits are a Scorecard requirement and a SLSA L3 input. Developers
  need a GPG or SSH signing key configured locally; see
  <https://docs.github.com/en/authentication/managing-commit-signature-verification>.
- “Restrict who can push” left empty is intentional — the protection rule is
  the enforcement mechanism, not an allowlist.

## Stable and preview isolation

All nine OS/runtime checks above are required. Each installs SDK/runtime families
8, 9, and 10, selects stable SDK 10 through `global.json`, and limits testhost
roll-forward to patches within the requested runtime family. A missing runtime
or failing test fails that lane. These checks do not establish native SDK 8/9
compiler-host compatibility; that has separate consumer verification.

`preview samples (net11.0, optional)` is deliberately **not** a required check.
It has its own checkout and preview SDK selection, no stable-job dependency,
and job-level `continue-on-error: true`. A preview installation or sample build
failure must not block any stable check or merging once required checks pass.

The repository settings have not been changed by documenting this matrix.
After the new names appear in GitHub Actions, replace the three former OS-only
required check names with the nine names above in branch protection. Verify a
throwaway PR with an intentional test failure is blocked, then remove the
failure and verify a deliberately failing preview job does not block merging.
Local failure injection and workflow validation do not prove GitHub enforcement.

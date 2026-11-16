# Branch Protection — Setup Checklist

> **Superseded for check names, proof levels and the proposed settings by [required-checks.md](required-checks.md).**
> That page lists the exact names of every required lane (runtime, SDK-host consumer, provider, Native AOT, property,
> coverage, warning-ratchet, candidate packages, tooling tests and gate), the read-only evidence of what is enforced on
> `main` (2026-10-23: required status checks and required reviews are **not enabled**, rulesets none, administrator
> enforcement/signatures/linear history/conversation resolution off) and one reconciled proposal. Everything below is a
> proposal for a maintainer; none of it is in effect, and YAML/CODEOWNERS files do not enforce it. The nine
> `build + test` names below remain valid but are only a subset.

GitHub branch protection must be configured in the repository's web UI.
This document exists so the settings are recorded and reproducible if the
repo is ever re-created or migrated.

## Target branch

`main` on `github.com/UmutKorkmaz/meridian`.

## Effective state (read-only evidence)

Read-only `gh` calls on 2026-10-23 (`gh repo view`, `gh api .../branches/main/protection`, `gh api .../rulesets`,
`gh api .../actions/permissions`, all exit 0). No setting was changed by reading them or by writing this page.

| Setting | Observed 2026-10-23 |
| --- | --- |
| Default branch | `main`, repository not archived |
| Require a pull request / required approvals / code-owner review | **not configured** (no `required_pull_request_reviews` block) |
| Required status checks | **not configured** (no `required_status_checks` block), so no CI result blocks a merge |
| Push restrictions | none, so the rule does not stop direct pushes to `main` |
| Enforce for administrators | off |
| Signed commits, linear history, conversation resolution | off |
| Force pushes / deletions | blocked (`allow_force_pushes` and `allow_deletions` false) |
| Branch creation blocking, branch lock | off |
| Rulesets | none (`[]`); no tag protection |
| Actions | enabled, all actions allowed, SHA pinning not required |
| Maintainers | one (`@UmutKorkmaz`); **no second maintainer exists** |

Not re-read on that date, so nothing is claimed about them: secret scanning and push protection, private
vulnerability reporting and the default workflow token permission. Re-read with the calls listed in the
[maintainer checklist](../maintainer-checklist.md#effective-repository-settings-verify-do-not-assume) before relying on
any row.

## The single-maintainer constraint

GitHub does not let the author of a pull request approve it. With one maintainer, a required approval means every PR
that maintainer opens can merge only through an administrator bypass. Until a second maintainer exists:

- keep "Enforce for administrators" (do not allow bypassing) **off**, and treat the owner bypass as explicit and
  documented: the maintainer checks the [manual review checklist](../maintainer-checklist.md#reviewing-a-pull-request)
  before bypassing;
- do **not** enable "Require approval of the most recent reviewable push", because the sole author could then never
  merge;
- never simulate a second reviewer with another account.

## Proposed settings (not in effect)

To be applied by a maintainer with admin rights, either as a branch protection rule or as an equivalent ruleset. Go to
`Settings → Branches → Branch protection rules → Add rule`:

- Branch name pattern: `main`
- ✅ Require a pull request before merging
  - ✅ Require approvals — minimum **1** (two required approvals need at least three available maintainers, so the
    author has two other reviewers; see [maintainer onboarding](../maintainer-checklist.md#second-maintainer-onboarding))
  - ✅ Dismiss stale pull request approvals when new commits are pushed
  - ✅ Require review from Code Owners (`.github/CODEOWNERS` is present)
  - ❌ Require approval of the most recent reviewable push — enable only after an eligible reviewer other than the
    PR author and latest pusher can complete the workflow (see above)
- ✅ Require status checks to pass before merging
  - ✅ Require branches to be up to date before merging
  - Required checks — use the **exact** GitHub Actions check names. The full proposed list is item 2 of
    [required-checks.md](required-checks.md#proposed-settings-for-the-maintainer-proposal-only-nothing-was-changed):
    `candidate packages`, `candidate gate`, `eng tooling tests`, the nine `build + test (...)` checks, the nine
    `package consumers (...)` checks, the three `Required Redis all-package consumers (...)`, `PostgreSQL projections (...)`,
    `Distributed cache Redis (...)` and `EF transactions (...)` checks each, `EF10 compatibility (net10.0)`,
    `HTTP orders, real Redis and SQLite (net10.0)`, `property counts (net10.0)`, `coverage gate (net10.0)` and
    `warning ratchet (net10.0)`. Add the six `native AOT ...` checks **only after** a real linux-x64 and osx-arm64 run
    has succeeded, because a required check that cannot run blocks every merge. Never require
    `preview samples (net11.0, optional)` or any `current servicing ...` check (those are local lanes).

    From `.github/workflows/ci.yml` (job `name:`), the subset this page originally listed:
    - `build + test (ubuntu-latest, net8.0)`
    - `build + test (ubuntu-latest, net9.0)`
    - `build + test (ubuntu-latest, net10.0)`
    - `build + test (windows-latest, net8.0)`
    - `build + test (windows-latest, net9.0)`
    - `build + test (windows-latest, net10.0)`
    - `build + test (macos-latest, net8.0)`
    - `build + test (macos-latest, net9.0)`
    - `build + test (macos-latest, net10.0)`

    CodeQL and Scorecard are not in the reconciled list. Requiring them is a separate decision, and the names must be
    copied from the Actions UI after they appear on a PR (do not invent shorter aliases — typos silently disable
    enforcement).

- ✅ Require conversation resolution before merging
- ✅ Require signed commits (GPG or SSH signatures)
- ✅ Require linear history (no merge commits — use squash or rebase)
- ✅ Do not allow bypassing the above settings once maintainer-authored PRs can satisfy the selected review threshold
  (until then, the documented owner bypass described in required-checks.md applies)
- ✅ Restrict who can push to matching branches — empty (no direct push)
- ❌ Allow force pushes
- ❌ Allow deletions

Related proposals from [required-checks.md](required-checks.md#proposed-settings-for-the-maintainer-proposal-only-nothing-was-changed),
also not in effect:

- Protect `v*` tags with a ruleset (only maintainers may create them; updates and deletion blocked), because
  `release.yml` publishes to NuGet when such a tag is pushed. Keep NuGet publishing behind a protected GitHub
  environment with a required reviewer. Today `release.yml` declares no environment, so this needs a reviewed workflow
  change as well as the setting.
- Turn on "require actions to be pinned to a full commit SHA" once the action references have been converted.
- Review secret scanning, push protection and Dependabot security updates.

## Verification

After configuring:

1. Open a test PR against `main`.
2. Try to merge without approval — should be blocked.
3. Try to push directly to `main` — should be rejected.
4. Confirm required-check names match the Actions UI exactly.
5. Re-read the settings with the same read-only calls as above and record the observed values next to the date.
   Until that re-read shows the change, this page and required-checks.md keep reporting the gap.

## Notes

- CI already runs on every PR to `main` (see `.github/workflows/ci.yml`).
  Enable “Require branches to be up to date” only after the check names
  above appear at least once on a PR.
- Signed commits are a Scorecard requirement and a SLSA L3 input. Developers
  need a GPG or SSH signing key configured locally; see
  <https://docs.github.com/en/authentication/managing-commit-signature-verification>.
- “Restrict who can push” left empty is intentional — the protection rule is
  the enforcement mechanism, not an allowlist.
- When a second maintainer is onboarded, follow
  [Second maintainer onboarding](../maintainer-checklist.md#second-maintainer-onboarding) and update the effective-state
  table only from a fresh read-only re-read.

## Stable and preview isolation

Once configured, all nine OS/runtime checks above would be required (they are not today). Each installs SDK/runtime families
8, 9, and 10, selects stable SDK 10 through `global.json`, and limits testhost
roll-forward to patches within the requested runtime family. A missing runtime
or failing test fails that lane. These checks do not establish native SDK 8/9
compiler-host compatibility; that has separate consumer verification.

`preview samples (net11.0, optional)` is deliberately **not** a required check.
It has its own checkout and preview SDK selection, no stable-job dependency,
and job-level `continue-on-error: true`. A preview installation or sample build
failure must not block any stable check or merging once required checks pass.

The repository settings have not been changed by documenting this matrix, and
no status check is currently required on `main`. After the names appear in GitHub
Actions, a maintainer would add them (with the full list in required-checks.md)
to branch protection. Verify a
throwaway PR with an intentional test failure is blocked, then remove the
failure and verify a deliberately failing preview job does not block merging.
Local failure injection and workflow validation do not prove GitHub enforcement.

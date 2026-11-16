# Maintainer checklist

This page lists the recurring maintenance duties: reviewing, keeping the support policy, refreshing .NET servicing,
responding to security reports and releasing, and it records what is and is not in place today. It is a manual
checklist. No scheduled job, hook or monitor carries it out. Contributor setup is in [CONTRIBUTING](../CONTRIBUTING.md).

## Who maintains Meridian

- There is **one maintainer**: `@UmutKorkmaz`, owner and administrator of `UmutKorkmaz/meridian`. `.github/CODEOWNERS`
  names only that account.
- **There is no second maintainer**, so nobody else can review, respond to security reports, release or act as NuGet
  owner. This is a recorded gap. Do not simulate a second reviewer: no alternate accounts, and no self-approval
  presented as an independent review.
- When a second maintainer joins, follow [Second maintainer onboarding](#second-maintainer-onboarding).

## Effective repository settings: verify, do not assume

Workflow files and `CODEOWNERS` prove nothing about enforcement. Re-read the live settings with read-only calls (all
of them are `GET` requests):

```bash
gh repo view UmutKorkmaz/meridian --json nameWithOwner,defaultBranchRef,url,isArchived
gh api repos/UmutKorkmaz/meridian/branches/main/protection
gh api repos/UmutKorkmaz/meridian/rulesets
gh api repos/UmutKorkmaz/meridian/actions/permissions
```

Last observed on 2026-10-23 (all four calls exited 0):

| Setting | Observed |
| --- | --- |
| Default branch | `main`, repository not archived |
| Required status checks on `main` | **none** (the protection document has no `required_status_checks` block) |
| Required pull-request reviews, code-owner reviews | **none** (no `required_pull_request_reviews` block) |
| Enforce for administrators, signed commits, linear history, conversation resolution | all off |
| Force pushes, deletions | both blocked (`allow_force_pushes` and `allow_deletions` false) |
| Rulesets | none (`[]`) |
| Actions | enabled, all actions allowed, SHA pinning not required |

Not re-read on that date, so nothing is claimed about them: whether private vulnerability reporting is enabled,
secret scanning and push protection, Dependabot security updates, the default workflow token permission, whether the
`NUGET_API_KEY` secret exists, and NuGet package ownership.

The proposed settings are in [branch protection](governance/branch-protection.md) and
[required checks](governance/required-checks.md#proposed-settings-for-the-maintainer-proposal-only-nothing-was-changed).
Changing a setting is a deliberate remote change by the maintainer. Afterwards, re-run the calls above and record what
you observe.

## Reviewing a pull request

While no check or review is required by settings, the maintainer checks the following by hand:

- [ ] CI ran on the PR head commit and every check in
      [required checks](governance/required-checks.md#required-check-names) that the workflow defines passed, with zero
      skipped mandatory cases. `preview samples (net11.0, optional)` does not count. The CI `candidate gate` verdict is
      scoped (`releaseEligible: false`).
- [ ] Compatibility ([policy](compatibility-policy.md)): no public API or default change outside a major release. Target
      frameworks `net8.0;net9.0;net10.0` (generators `netstandard2.0`, Roslyn 4.8 API floor) and the C# 12 consumer
      floor are unchanged. Legacy defaults and the supported aliases still work. Package validation is green.
- [ ] Behavior changes come with tests. Nothing is skipped, quarantined or filtered away. Provider tests still fail
      without their backend.
- [ ] Ratchets only go up. `eng/coverage-baseline.json` and `eng/warning-baseline.json` floors are lowered only by a
      reviewed edit with a stated reason. The coverage policy allows no waivers.
- [ ] Documentation claims stay within the evidence and use the proof-level words (`local-executed`,
      `CI-defined-not-yet-run`, `UNAVAILABLE`). The [capability ledger](capabilities.md) matches.
- [ ] Dependencies: a new or raised direct dependency is visible in the PR, its license is read from the package's own
      metadata, and [`NOTICE`](../NOTICE) and [integrations](integrations.md) are updated. Private build tooling stays
      separate from distributed dependencies.
- [ ] Security-sensitive paths (`src/Meridian.Mapping/Execution/`, `src/Meridian.Mediator/Pipeline/`,
      `src/Meridian.Mediator/Wrappers/`): depth caps, collection caps and reflection paths were considered.
- [ ] Workflows: no new scheduled automation or monitor without a separate reviewed decision. Each workflow's
      security-review header still holds (no untrusted `github.event.*` values in `run:` blocks). Action references
      stay consistent (version tag or full SHA).
- [ ] No committed secret, token or connection string. Provider credentials come only from the environment.
- [ ] `CHANGELOG.md` has an `[Unreleased]` entry for user-visible changes.

## Applying the support and retirement policy

[compatibility-policy.md](compatibility-policy.md) is the authority. How it applies in practice:

| Area | Current boundary | Rule |
| --- | --- | --- |
| Runtime target frameworks | `net8.0`, `net9.0`, `net10.0` | Never removed in a minor release. Retiring one is a separately reviewed major-version decision, announced in the CHANGELOG with migration guidance. |
| Generator target and API floor | `netstandard2.0`, Roslyn 4.8 | Raising either one breaks consumers on older compilers, so it is a major-version decision. |
| Consumer language floor | C# 12, warnings as errors | Generated code and public APIs must compile there. Raising the floor is a major-version decision. |
| Compiler-host proof | SDK 8.0.421 / 9.0.318 / 10.0.300 (`compilerHosts` in `eng/packages.json`) | Pinned historical proof. Changing it is its own reviewed decision. |
| Latest-patch proof | SDK 8.0.428 / 9.0.321 / 10.0.404, runtime 8.0.34 / 9.0.23 / 10.0.15 (released 2026-11-10) | 8/9 are final compatibility patches; refresh supported families (next section). |
| .NET 11 | Optional, non-blocking preview **samples** job | Not a package target, never a required check, no automatic target flip. |
| Direct dependency minimums | Listed in [integrations](integrations.md) and [`NOTICE`](../NOTICE) | Raising one changes every consumer's dependency set. Review it, note it in the CHANGELOG and re-run the package consumers. |
| Providers | Redis 7.4 and PostgreSQL 16.4 at the pinned digests | Anything else is unverified, not supported. |
| Native AOT | Only rows marked PASS in [Native AOT](native-aot.md) | A row needs an executed native binary on a matching real host. |

Microsoft support for .NET 8 and .NET 9 ended on **2026-11-10**. That is Microsoft's runtime servicing date. It is
not Meridian's target-framework retirement, and it does not by itself justify removing `net8.0` or `net9.0` from a
2.x release. After that date, tell consumers that Microsoft no longer patches those runtimes, and take the targets up
in the next reviewed major-release scope.

## Refreshing current servicing

Do this by hand after Microsoft publishes new .NET patches. No job watches for them.

1. Read the official metadata
   `https://builds.dotnet.microsoft.com/dotnet/release-metadata/{8.0,9.0,10.0}/releases.json`. Record the read time,
   the SHA-256 of each document, the latest release date and the latest SDK and runtime versions.
2. In **one change**, update `toolchains.currentServicing` in `eng/required-checks.json` (`asOf`, `releaseDate`,
   metadata hashes, `buildSdk`, `lanes`), the servicing table in
   [required checks](governance/required-checks.md#current-servicing-versus-pinned-compatibility) and the table in
   [CONTRIBUTING](../CONTRIBUTING.md#installing-sdks-and-runtimes). Update the six Native AOT lane SDKs and the
   Native AOT workflow matrix to those SDK patches, then run the hosted matrix on both target platforms.
3. Install the three SDKs into a **new private** dotnet root, using the official downloads, and check each archive's
   SHA-512 against `releases.json`. `dotnet --list-runtimes` must show exactly one patch per family.
4. Run the six local lanes (`current servicing (<tfm>)` and `current servicing package consumers (<tfm>)`) with
   `MERIDIAN_SERVICING_DOTNET_ROOT` pointing at that root and owned providers
   ([provider testing](provider-testing.md)). Keep the receipts. Metadata and downloads alone are not proof.
5. Leave the pinned compatibility lanes and earlier receipts as they are. Do not edit them to match.

## Security response

The reporter-facing policy is [SECURITY.md](../SECURITY.md). With one maintainer there is no backup responder. If a
target is missed, say so to the reporter.

1. Acknowledge the report privately (target: 48 hours) and keep everything in a draft GitHub security advisory.
2. Reproduce and fix on a private branch or the advisory's temporary private fork. No public issue, PR or commit may
   describe the problem before disclosure.
3. Assess severity and the affected versions, and request a CVE through the advisory if one is warranted.
4. Add a regression test and run the gates for every affected package. Proof levels and limitations go into the
   advisory as they are.
5. Release the fix (see the next section). Then publish the advisory and update the supported-versions table in
   `SECURITY.md` and `CHANGELOG.md`, crediting the reporter unless they asked for anonymity.
6. To hide vulnerable versions from search, use the manual workflow `unlist-vulnerable.yml` (`workflow_dispatch`,
   `dry_run` defaults to `true`). It currently lists only the `GHSA-f5v8-v6q3-q4h6` versions (Meridian.Mapping 2.1.0,
   Meridian.Mediator 2.0.0 and 2.0.1). A new advisory needs a reviewed change to that list first. Unlisting keeps a
   version restorable, and running it is a deliberate remote action.

## Releasing

The workflow source in this checkout is a **manual, read-only promotion plan**
([release runbook](release-runbook.md)). It downloads an existing prepared bundle, checks the exact approved
`candidate.json` SHA-256 and inspects remote state. It does not rebuild packages, create tags or releases, or publish.
There is no tag-push trigger in this workflow source. A green plan is not authorization to execute it.

`prepare-release.ps1` assembles the immutable bytes and evidence into a candidate bundle; it requires the complete E4
aggregate, lifecycle validation, maintenance and final-review receipts, effective governance and release notes.
`promote-release.ps1` verifies that bundle before planning. Trusted execution authorization and a production write
transport are not available in this repository, so execution fails closed. Missing infrastructure and external evidence
remain explicit blockers; fixture tests do not supply them.

These statements describe the checked-in implementation. They do not prove that this checkout has been pushed, that the
hosted workflow has changed, or that a candidate is prepared. Verify the effective hosted workflow before any tag or
publication action. A CI scoped verdict (`releaseEligible: false`) is insufficient.

Before any release:

- [ ] The maintainer explicitly authorized this exact version and scope. A green run is never that authorization, and
      neither is `-ReleasePreparation`, which only declares intent for a local stable candidate.
- [ ] The version follows from API, behavior and compiler evidence under SemVer and the compatibility policy.
- [ ] `pwsh eng/verify-candidate.ps1 -Mode Aggregate ...` over the complete evidence for the exact candidate returns
      PASS with `releaseEligible: true`. That means every CI lane from real runners plus the six local current-servicing
      lanes. **This is not achievable today**: the CI lanes have never run on GitHub, and Windows and linux-x64 are
      `UNAVAILABLE` locally.
- [ ] `CHANGELOG.md`, the supported versions in `SECURITY.md`, `NOTICE` and the docs are updated.
- [ ] Never push a `v*` tag as a test, never move or delete a published tag and never reuse a version. NuGet versions
      are immutable, so a bad release is fixed forward with a new version and unlisted if necessary.
- [ ] After publishing, download the packages from nuget.org, check their hashes and contents with a fresh consumer,
      and record the GitHub Release and NuGet results separately. A tag alone is not package publication.

## Workflows and schedules

E6 adds no workflow, schedule, hook or monitor. The existing set:

| Workflow | Triggers |
| --- | --- |
| `ci.yml` | pull requests and pushes to `main`, manual |
| `codeql.yml` | pull requests and pushes to `main`, weekly (Mon 04:00 UTC) |
| `scorecard.yml` | pushes to `main`, branch-protection-rule changes, weekly (Mon 05:00 UTC), manual |
| `fuzz.yml` | nightly (02:00 UTC), manual; pins SDK 10.0.300 and fails when the executed case counts differ from the budgets |
| `benchmarks.yml` | weekly (Mon 03:00 UTC), manual; hosted-runner numbers are for regression spotting only |
| `pages.yml` | pushes to `main` that touch `docs-site/**` or the workflow, manual |
| `release.yml` | manual read-only promotion plan in this checkout; hosted adoption must be verified (see above) |
| `unlist-vulnerable.yml` | manual only |

Dependabot (`.github/dependabot.yml`) opens weekly NuGet and GitHub Actions update PRs. A Dependabot bump of a direct
dependency of a `src/` project raises a dependency minimum and is reviewed as described above.

## Clean-copy setup verification

Require a source-bound receipt from a clean disposable copy for the setup and provider instructions. Whoever runs
this check (the root/maintainer):

1. Clone the accepted commit into a new directory (see
   [CONTRIBUTING](../CONTRIBUTING.md#get-the-source-without-disturbing-other-work)). Do not touch any existing working
   tree.
2. Record the environment ([troubleshooting](troubleshooting.md#record-the-environment-first)).
3. Follow CONTRIBUTING exactly: restore, build, the backend-free runtime-profile tests for all three frameworks and the
   samples. Then follow [provider testing](provider-testing.md) with owned containers, including the cleanup check.
4. Keep every log and TRX. Where reality differs from the docs, fix the docs. Do not quietly work around them.
5. Save the receipts outside the clone before you delete it.

Until that run exists for the source under review, these pages are preparation, not proof. Preserve its receipt
separately; a later source or host needs its own verification.

## Second maintainer onboarding

When a real second maintainer joins (see [PLAN §8.4](PLAN.md)):

1. Add them to `.github/CODEOWNERS`, at least on the security-sensitive paths.
2. Require one independent approval and code-owner review once a maintainer-authored PR can obtain them. Two required
   approvals need at least three available maintainers, because the PR author cannot self-approve. Enable administrator
   enforcement only after that selected threshold can be met. Test the latest-push rule separately: its approver must
   also differ from the latest pusher ([GitHub review rules](https://docs.github.com/en/pull-requests/how-tos/review-pull-requests/approving-a-pull-request-with-required-reviews),
   [branch protection](governance/branch-protection.md)).
3. Share NuGet ownership and release secrets with least privilege, and name a backup security contact in
   `SECURITY.md`.
4. Re-read the settings with the read-only calls above and record the result.

## Known gaps (2026-10-23)

- No required status checks, required reviews or rulesets on `main`. Actions allows all actions without SHA pinning.
- One maintainer and no backup.
- No CI lane has run on GitHub. Windows and linux-x64 are `UNAVAILABLE` on the development host.
- Exact-candidate preparation and read-only promotion planning exist in this checkout; trusted execution, hosted adoption and final release proof remain unavailable.
- At the 2026-10-23 documentation handoff, clean-copy setup and the complete provider recipe were pending; later
  verification must be supported by its own source-bound local receipts.
- The private vulnerability reporting, secret scanning and push protection settings and the default token permission
  were not re-verified.

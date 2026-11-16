# Contributing to Meridian

Thank you for considering a contribution. This page covers setup, the commands that reproduce what the CI lanes run,
the pull-request workflow and what the repository does and does not enforce today.

Related pages:

- [Troubleshooting](./docs/troubleshooting.md): SDK selection, failing samples, generator diagnostics (`MERIDIANGEN*`,
  `MERIDIANMED*`), provider and tooling errors.
- [Provider testing](./docs/provider-testing.md): disposable local Redis and PostgreSQL, secret-free configuration.
- [Required checks](./docs/governance/required-checks.md): the exact CI check names and how strongly each claim is
  evidenced.
- [Compatibility policy](./docs/compatibility-policy.md): what counts as breaking.
- [Maintainer checklist](./docs/maintainer-checklist.md): review, servicing, security and release duties.

These commands follow the scripts and manifests in this repository. Verify them from a clean disposable copy and
retain the exact commit, environment, logs and results using the maintainer checklist. The instructions themselves
are not execution evidence; a local receipt applies only to its recorded source and host.

## Prerequisites

| Tool | What you need | Used for |
| --- | --- | --- |
| .NET SDK 10 | Any 10.0 SDK at or above the `10.0.100` named in `global.json`. `rollForward: latestFeature` picks the **highest installed** 10.0 feature band, so with 10.0.300 and 10.0.404 installed you get 10.0.404. The pinned CI lanes build with exactly 10.0.300. | Building every project |
| .NET runtimes 8.0, 9.0 and 10.0 | One runtime of each family | Running the tests and samples for `net8.0`, `net9.0` and `net10.0` |
| Exact SDKs 8.0.421, 9.0.318 and 10.0.300 | Only for the package-consumer, EF10 and documentation checks. Those scripts write a `global.json` with `rollForward: disable`, so nothing else will do. | `compilerHosts` in [`eng/packages.json`](./eng/packages.json) |
| PowerShell 7 (`pwsh`) | Tested with 7.6.4; older 7.x releases are untested | Every `eng/*.ps1` script |
| Python 3.9 or newer | `eng/verify-candidate.ps1` refuses an older interpreter | The helper scripts `eng/*.py` |
| Python 3.11 or newer and PyYAML 6.0.3 | Only to run the tooling test suite `eng/tests` (3.12 is the tested version) | See [Tooling tests](#tooling-tests) |
| Docker | Only for provider-backed tests | [Provider testing](./docs/provider-testing.md) |
| Git | Any current version | |

### Installing SDKs and runtimes

Microsoft supports only the latest patch of each supported family
([support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)). The official release
metadata read on 2026-11-16 lists the patches released on 2026-11-10:

| Family | Latest patch checked (8/9 are end of support) | Pinned compatibility (CI lanes, kept on purpose) |
| --- | --- | --- |
| 8.0 | SDK 8.0.428, runtime 8.0.34 | SDK 8.0.421, runtime 8.0.27 |
| 9.0 | SDK 9.0.321, runtime 9.0.23 | SDK 9.0.318, runtime 9.0.20 |
| 10.0 | SDK 10.0.404, runtime 10.0.15 | SDK 10.0.300, runtime 10.0.8 |

These rows go out of date. The source of truth is `toolchains` in [`eng/required-checks.json`](./eng/required-checks.json),
and the maintainer checklist describes how they are refreshed. The pinned versions are historical compatibility
proof, not current Microsoft servicing. Microsoft support for .NET 8 and .NET 9 ended on 2026-11-10. That date is not
Meridian's target-framework policy: Meridian keeps `net8.0` and `net9.0` until a separately reviewed major release.

To reproduce a CI lane exactly, put only the SDKs that lane uses into a **private** directory, so that no other
installed SDK or runtime patch can be picked. The official install scripts can do this without touching a system
installation:

```bash
curl -sSLo dotnet-install.sh https://dot.net/v1/dotnet-install.sh
for sdk in 8.0.421 9.0.318 10.0.300; do
  bash dotnet-install.sh --version "$sdk" --install-dir "$HOME/.dotnet-meridian-pinned" --no-path
done
export DOTNET_ROOT="$HOME/.dotnet-meridian-pinned"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_ROLL_FORWARD=LatestPatch DOTNET_ROLL_FORWARD_TO_PRERELEASE=0   # what the CI jobs set
dotnet --list-sdks       # expect exactly 8.0.421, 9.0.318, 10.0.300
dotnet --list-runtimes   # expect exactly one Microsoft.NETCore.App per family (CI pins 8.0.27, 9.0.20, 10.0.8)
```

On Windows, use `dotnet-install.ps1 -Version <sdk> -InstallDir <dir> -NoPath` from <https://dot.net/v1/dotnet-install.ps1>.
The six local `current servicing` lanes use a second private root with SDKs 8.0.428, 9.0.321 and 10.0.404, named by
`MERIDIAN_SERVICING_DOTNET_ROOT` ([details](./docs/governance/required-checks.md)). A download alone proves nothing;
always check `--list-sdks` and `--list-runtimes`. Native AOT also uses these latest-patch SDKs to restore matched ILCompiler packs.

The optional .NET 11 preview applies to samples only (`MeridianIncludeNet11Samples=true`). It is never required and is
not a package target.

## Get the source without disturbing other work

```bash
git clone https://github.com/UmutKorkmaz/meridian.git
cd meridian
```

If you already have a working copy with uncommitted work, do **not** stash, reset or clean it to get a "clean tree".
Make a separate disposable clone instead. A clone takes committed history only, and the original working tree is left
alone:

```bash
commit="$(git -C /path/to/your/meridian rev-parse HEAD)"   # or any other committed SHA to verify
git clone --no-hardlinks /path/to/your/meridian ../meridian-verify
git -C ../meridian-verify checkout --detach "$commit"
```

`eng/verify-candidate.ps1` refuses an uncommitted tree because evidence must name an exact commit. Untracked files
count too. Run Python with `-B` (or set `PYTHONDONTWRITEBYTECODE=1`), because a stray `eng/__pycache__/` is enough to
make the tree dirty.

## Build and run the tests that need no backend

```bash
dotnet --version                     # a 10.0 SDK; CI uses exactly 10.0.300
dotnet restore Meridian.sln
dotnet build Meridian.sln -c Release --no-restore
```

Do **not** run an unfiltered `dotnet test Meridian.sln` without backends. Provider tests never skip: without
`MERIDIAN_TEST_REDIS` / `MERIDIAN_TEST_POSTGRES` they fail on purpose. The suites and the filter below are the
`runtime` profile in `eng/required-checks.json`, which is what each `build + test (<os>, <tfm>)` lane runs. This
PowerShell loop reads both from that manifest:

```powershell
$policy = Get-Content -Raw eng/required-checks.json | ConvertFrom-Json
foreach ($tfm in 'net8.0', 'net9.0', 'net10.0') {
    foreach ($entry in $policy.countProfiles.runtime.$tfm) {
        dotnet test "tests/$($entry.suite)/$($entry.suite).csproj" -c Release -f $tfm --no-build --filter $entry.filter
        if ($LASTEXITCODE -ne 0) { throw "$($entry.suite) ($tfm) failed" }
    }
}
```

The same commands in bash (the suite list as of this writing; if they differ, the manifest wins):

```bash
(
  set -e
  filter='Provider!=PostgreSQL&Provider!=Redis'
  for tfm in net8.0 net9.0 net10.0; do
    for suite in Meridian.Mapping.EntityFramework.Tests Meridian.Mapping.Tests Meridian.Mediator.AspNetCore.Tests \
                 Meridian.Mediator.Caching.Tests Meridian.Mediator.EntityFrameworkCore.Tests \
                 Meridian.Mediator.FluentValidation.Tests Meridian.Mediator.Generators.Tests \
                 Meridian.Mediator.Redis.Tests Meridian.Mediator.Tests; do
      dotnet test "tests/$suite/$suite.csproj" -c Release -f "$tfm" --no-build --filter "$filter"
    done
  done
)
```

The lanes also require a minimum test count for every suite and **zero skipped** tests (`countProfiles` in the
manifest). `tests/Meridian.AspNetCore.Tests` (the orders HTTP API, `net10.0` only) is not in this list because it needs
Redis. Provider suites, the HTTP suite and the EF10 check are covered in [provider testing](./docs/provider-testing.md).

Samples (after the build; six samples on `net8.0`/`net9.0`, seven on `net10.0`, which adds QuickStart):

```bash
pwsh eng/run-samples.ps1 -Framework net10.0 -NoBuild
```

Elevated property-test budgets, as the `property counts (net10.0)` lane and the nightly fuzz workflow use them:

```bash
MERIDIAN_FSCHECK_MAX_TEST=10000 MERIDIAN_FSCHECK_COLLECTION_MAX_TEST=500 \
MERIDIAN_FSCHECK_REPLAY=12345,11400714819323198485 \
dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release -f net10.0 --no-build \
  --filter 'FullyQualifiedName~PropertyBasedTests'
```

`MERIDIAN_FSCHECK_REPLAY` is `seed,gamma`, two unsigned 64-bit integers, and gamma must be odd.

## Package, documentation and candidate checks

These scripts work on a locally packed candidate and never publish, push or upload anything. Use a fresh prerelease
version every time, because the pack step refuses a version that already exists in the output feed. A stable version
needs `-ReleasePreparation`, which records an intent only and is not permission to publish.

```bash
pwsh eng/pack-packages.ps1 -CandidateVersion 2.1.4-local.1 -OutputDirectory artifacts/candidate
pwsh eng/verify-packages.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1
pwsh eng/verify-docs.ps1 -Configuration Release -Feed artifacts/candidate
```

- `verify-packages.ps1` compiles every consumer in `eng/packages.json` with each exact compiler-host SDK, C# 12 and
  warnings as errors, and runs it. It downloads the published 2.1.3 baseline packages from nuget.org unless you pass
  `-BaselineFeed`. `Meridian.RedisIdempotencyConsumer` needs `MERIDIAN_TEST_REDIS` and
  `Meridian.EfTransactionsConsumer` needs `MERIDIAN_TEST_POSTGRES`. Without backends, exclude them inside `pwsh` with
  `-ExcludeConsumerIds Meridian.RedisIdempotencyConsumer, Meridian.EfTransactionsConsumer`. That result is recorded as
  `consumerScope: partial`. It satisfies a declared partial lane only when its exact selected/omitted consumers,
  compiler, candidate and retained evidence pass `verify-candidate.ps1`; the full all-consumer lanes require both
  backends. `-PlanOnly` prints the compiler plan and builds nothing.
- Native AOT: see [docs/native-aot.md](./docs/native-aot.md#reproduce). A native row only counts on a matching real
  host.
- The candidate gate: `pwsh eng/verify-candidate.ps1 -Configuration Release -Feed <feed> -EvidenceDirectory <dir>` runs
  the lanes this host can execute and judges every required lane. A run restricted with `-Lane` ends in `SCOPED-PASS`,
  which is never a candidate verdict. On one machine, lanes bound to another OS or architecture stay `UNAVAILABLE`.
  The six `current servicing` lanes need `MERIDIAN_SERVICING_DOTNET_ROOT`. Exit codes and semantics are described in
  [required checks](./docs/governance/required-checks.md#how-the-gate-decides).

## Tooling tests

The gate, collector, coverage, workflow, inventory and documentation tooling has its own suite under `eng/tests`.

- The production helper scripts (`eng/*.py`) target **Python 3.9 or newer** and must keep 3.9 syntax:
  `from __future__ import annotations`, no `match`, no PEP 701 nested f-strings and no `ExceptionGroup`.
- The test fixtures are not held to that floor. `eng/tests/test_http_runner_timeout.py` is a POSIX fixture that uses
  `ExceptionGroup` and `add_note` and needs **Python 3.11 or newer**. The suite has been run with Python 3.12. CI uses
  the runner's preinstalled `python3` without pinning a version.
- `eng/tests/test_ci_workflows.py` needs PyYAML 6.0.3. A missing module is a failure, not a skip. The suite also needs
  `pwsh` and the pinned SDKs, because it builds and packs fixture projects. The full discovery takes about 25 minutes.

```bash
python3 -m venv "$HOME/.venvs/meridian-eng"            # outside the repository, so the tree stays clean
"$HOME/.venvs/meridian-eng/bin/pip" install pyyaml==6.0.3
"$HOME/.venvs/meridian-eng/bin/python" -B -m unittest discover -s eng/tests -p 'test_*.py'
"$HOME/.venvs/meridian-eng/bin/python" -B -m unittest discover -s eng/tests -p 'test_docs_claims.py'   # one module
```

## How to propose a change

1. **Open an issue first** for any non-trivial change and agree on the scope before writing code. Typos, small doc
   fixes and obvious bug fixes can go straight to a PR.
2. **Fork** and create a topic branch off `main`: `git checkout -b feat/short-description`.
3. **Commit** using [Conventional Commits](https://www.conventionalcommits.org/):
   - `feat:` new capability
   - `fix:` bug fix
   - `perf:` performance improvement without behavior change
   - `refactor:` internal change, no behavior change
   - `docs:` documentation only
   - `test:` tests only
   - `chore:` build, tooling, dependencies
   - Breaking changes: append `!` after the type, for example `feat!:`, and add a `BREAKING CHANGE:` footer.
4. **Open a PR** against `main`, fill in the PR template and link the issue.

## Compatibility rules for every change

Read [docs/compatibility-policy.md](./docs/compatibility-policy.md). In particular:

- Keep the target frameworks: `net8.0;net9.0;net10.0` for runtime packages and `netstandard2.0` with the Roslyn 4.8
  API floor for the two generators. A target framework is never removed in a minor release, and retiring one is a
  separately reviewed major-version decision.
- Keep the **C# 12 consumer floor**. Every packaged consumer compiles with C# 12 and warnings as errors, so generated
  source and public APIs must work there.
- Keep defaults and legacy contracts: safety limits, telemetry privacy, Legacy generated mapping as the default, the
  `AddAutoMapper` aliases and the other APIs that the policy lists as supported. A replacement ships first and the
  deprecation window applies ([details](./docs/compatibility-policy.md#deprecation-window)).
- Do not change a public API or default runtime behavior without a major version and explicit CHANGELOG notes.
  Unexpected package-validation failures (`EnablePackageValidation`, baseline 2.1.3 for the core packages) mean
  something may be breaking.
- Raising a direct dependency minimum changes the dependency set of every consumer. Call it out in the PR, update
  [`NOTICE`](./NOTICE) and [integrations](./docs/integrations.md), and re-run the package consumers.
- A compiler or analyzer warning alone never justifies an API change
  ([warning dispositions](./docs/warning-dispositions.md)).

## What is enforced today

The read-only check on 2026-10-23 found no required status checks, required reviews or rulesets on `main`.
Force pushes and branch deletion were blocked; see
[required checks](./docs/governance/required-checks.md#governance-effective-repository-state-read-only-evidence). CI runs
on pull requests, but a red check does not block a merge, and `.github/CODEOWNERS` requests a review without requiring
one. There is one maintainer. Until settings change, the maintainer checks the items below by hand
([maintainer checklist](./docs/maintainer-checklist.md)). The proposed settings are in
[branch protection](./docs/governance/branch-protection.md).

A PR is ready to merge when:

- every check named in [required checks](./docs/governance/required-checks.md#required-check-names) that ran on the PR
  passed, with zero skipped mandatory cases. `preview samples (net11.0, optional)` does not count. The CI aggregate is a
  scoped verdict and never proves that a candidate is releasable;
- coverage and warning ratchets did not go down (`eng/coverage-baseline.json`, `eng/warning-baseline.json`). Lowering a
  floor is a reviewed edit with a reason;
- the CHANGELOG has an `[Unreleased]` entry for user-visible changes;
- no secret, token, connection string or credential was committed;
- changes to security-sensitive paths (`src/Meridian.Mapping/Execution/`, `src/Meridian.Mediator/Pipeline/`,
  `src/Meridian.Mediator/Wrappers/`) were reviewed for depth caps, collection caps and reflection paths.

## Code style

- Follow `.editorconfig` and the defaults in `Directory.Build.props`: nullable reference types, latest language
  version, `latest-recommended` analysis level and code style enforced in the build.
- Prefer `record` / `record struct` for immutable value-like types.
- Do not use `dynamic` or `Delegate.DynamicInvoke` in new code, since they have caused performance bugs before. Use
  cached compiled expression trees instead. Existing public dynamic APIs remain supported.
- Every public type needs an XML doc comment. `GenerateDocumentationFile` is enabled on packable projects.
- Keep files focused and consider splitting a file once it passes 500 lines.

## Testing

- Unit tests use **xUnit** (2.9.3). Property tests use **FsCheck.Xunit**. The repository does not use FluentAssertions.
- Every behavior change comes with a test. Fuzz-reachable code paths (`tests/Meridian.Mapping.Fuzz`) have extra
  expectations described in that project's `README.md`.
- No quarantine trait exists, and a skipped test fails its required lane. Fix a flaky test or remove it in a reviewed
  change. Never skip it or hide it behind a filter.
- Provider tests fail, never skip, when their backend is missing. Point them only at disposable instances you own
  ([provider testing](./docs/provider-testing.md)), never at a shared or production service.

## Security issues

**Do not open a public issue or PR.** Follow [`SECURITY.md`](./SECURITY.md).

## Code of conduct

Be respectful and discuss ideas, not people. Issues and PRs are welcome in English or Turkish.

## License

By contributing, you agree that your contributions are licensed under the repository's [MIT License](./LICENSE).
Third-party notices are in [`NOTICE`](./NOTICE).

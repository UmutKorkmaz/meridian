# Required checks, support proof levels and governance

This page is the contract for what a Meridian release candidate must prove, which GitHub check names carry that proof,
and how strongly each claim is evidenced **today**. The machine-readable source of truth is
[`eng/required-checks.json`](../../eng/required-checks.json) (lanes, exact check names, pinned toolchains, current
servicing patches, provider images, sample names and property counts, per-suite minimum test counts, the package
inventory, coverage/warning baselines). It is evaluated by `eng/candidate_gate.py` from lane evidence; the workflows in
`.github/workflows/` are checked against it by `eng/tests/test_ci_workflows.py`.

Nothing on this page was executed on GitHub. The workflow YAML has been parsed and structurally tested locally only.
**Proof level** is stated separately for every claim and uses exactly these three words:

| Proof level | Meaning |
| --- | --- |
| `local-executed` | Executed on the development host (Apple Silicon macOS) with receipts under the gitignored `artifacts/`. Receipts are local; the durable proof is the CI artifact of a run. |
| `CI-defined-not-yet-run` | A job exists in a workflow and has passed structural validation, but has never executed on a GitHub runner. |
| `UNAVAILABLE` | No infrastructure to execute it here (for example a real `linux-x64` machine or any Windows host). It is never reported as passed. |

## How the gate decides

`pwsh eng/verify-candidate.ps1 -Configuration Release -Feed <feed> -EvidenceDirectory <dir>` runs the lanes this host can
execute, collects their evidence (`eng/lane_collect.py`) and evaluates **every** required lane (`eng/candidate_gate.py`).
`-Mode Collect|Evaluate|Aggregate` separate collection from judgement (CI and the final cumulative campaign use them).

The evaluator never trusts a lane's recorded summary or status. For every lane it:

* re-hashes the raw files the lane retained under `lanes/<lane>/raw/` (TRX files, sample/consumer receipts, the executed
  native output and receipts, property logs, Cobertura documents, the MSBuild log, `lane-toolchain.json`); an empty
  artifact list, an unlisted, linked, escaping, changed or foreign-kind raw file fails the lane;
* **re-derives** the facts from those files with the same code the collector uses and fails the lane when the recorded
  summary differs from them (a summary edited to all-pass next to a retained failing TRX fails);
* judges the re-derived facts: per-TRX counts (one assembly and one target framework per TRX, ResultSummary counters
  equal to the results, Windows or POSIX test-binary paths matching the lane's operating system), zero failed and zero
  skipped, per-suite minimums, required sample names, consumer coverage of the inventory consumers, native cases from
  the executed output, recomputed coverage and warning verdicts, exact toolchain patches;
* requires the lane to be bound to **exactly** the packed candidate: every feed package with its actual archive hash and
  the package-manifest hash (an empty or partial binding fails), the source commit the candidate was packed from
  (recorded in the package manifest), a clean source tree (`source.dirty` false), a zero producer exit and no collector
  problems.

The feed itself must equal the package inventory (`eng/packages.json`) exactly: archive names are plain
`<id>.<version>.nupkg` files inside the feed (no paths, no symbolic links), no unlisted archive, no duplicate id, every
hash equal to the manifest, the manifest packed from this inventory and from a clean tree. CI exports the manifest hash
from `candidate packages` as an independent anchor (`-PackageManifestSha256`).

* Exit **0 PASS** (`releaseEligible: true`, `wholeCandidate: true`): every required lane has valid passing evidence for
  the same candidate.
* Exit **0 SCOPED-PASS** (`releaseEligible: false`, `wholeCandidate: false`): only named lanes were judged (`-Lane`,
  `-CiLanesOnly`, `--only`). It is labelled "NOT a candidate verdict" and is never release eligible. A CI lane job uses it
  for its own lane; the CI `candidate gate` job is scoped to the CI-defined lanes. Exit 0 is kept so a lane job can be
  green; consumers that need the candidate verdict read `releaseEligible` from `summary.json`.
* Exit **1 FAIL**: any failed lane, any lane with no evidence (`MISSING`), any lane bound to a different OS/architecture
  without evidence (`UNAVAILABLE`, strict mode), a tampered or inconsistent feed, or lanes from different source commits.
* Exit **2**: invalid policy or arguments (including `-AllowPending` with `-Mode Aggregate`, `-Context github-actions`
  outside GitHub Actions, or Collect without `-ProducerExit`).
* Exit **3 PENDING** (`releaseEligible: false`): only with `-AllowPending` in host mode, and only when the *sole*
  non-passing lanes are bound to another OS/architecture and have no evidence. It is never success and cannot hide a
  failing lane; a missing lane without a host binding (provider, coverage, current servicing, ...) is `MISSING`.

`summary.json` / `summary.md` list each check with its state, proof level and re-derived counts, per-framework test
totals re-derived from the retained TRX files, recorded skips, limitations and every candidate package SHA-256.
Counterexamples proving the gate fails closed are in `eng/tests/test_candidate_gate.py` (every passing lane there is
produced by the real collector from real raw files; each negative test changes one input).

## Required check names

Names are exactly the GitHub job names. `candidate packages` packs the candidate once; every lane downloads that feed.
A lane without evidence, or a CI job name that drifts from this table, fails `test_ci_workflows.py`.

| Check name (exact) | Lane id | Kind | CI job |
| --- | --- | --- | --- |
| `candidate packages` | (infrastructure) | pack once | `candidate-pack` |
| `build + test (ubuntu-latest, net8.0)` | `runtime-tests/linux/net8.0` | runtime-tests | `build` |
| `build + test (ubuntu-latest, net9.0)` | `runtime-tests/linux/net9.0` | runtime-tests | `build` |
| `build + test (ubuntu-latest, net10.0)` | `runtime-tests/linux/net10.0` | runtime-tests | `build` |
| `build + test (windows-latest, net8.0)` | `runtime-tests/win/net8.0` | runtime-tests | `build` |
| `build + test (windows-latest, net9.0)` | `runtime-tests/win/net9.0` | runtime-tests | `build` |
| `build + test (windows-latest, net10.0)` | `runtime-tests/win/net10.0` | runtime-tests | `build` |
| `build + test (macos-latest, net8.0)` | `runtime-tests/osx/net8.0` | runtime-tests | `build` |
| `build + test (macos-latest, net9.0)` | `runtime-tests/osx/net9.0` | runtime-tests | `build` |
| `build + test (macos-latest, net10.0)` | `runtime-tests/osx/net10.0` | runtime-tests | `build` |
| `package consumers (ubuntu-latest, SDK 8.0.421)` | `package-consumers/linux/net8.0` | package-consumers | `package-compatibility` |
| `package consumers (ubuntu-latest, SDK 9.0.318)` | `package-consumers/linux/net9.0` | package-consumers | `package-compatibility` |
| `package consumers (ubuntu-latest, SDK 10.0.300)` | `package-consumers/linux/net10.0` | package-consumers | `package-compatibility` |
| `package consumers (windows-latest, SDK 8.0.421)` | `package-consumers/win/net8.0` | package-consumers | `package-compatibility` |
| `package consumers (windows-latest, SDK 9.0.318)` | `package-consumers/win/net9.0` | package-consumers | `package-compatibility` |
| `package consumers (windows-latest, SDK 10.0.300)` | `package-consumers/win/net10.0` | package-consumers | `package-compatibility` |
| `package consumers (macos-latest, SDK 8.0.421)` | `package-consumers/osx/net8.0` | package-consumers | `package-compatibility` |
| `package consumers (macos-latest, SDK 9.0.318)` | `package-consumers/osx/net9.0` | package-consumers | `package-compatibility` |
| `package consumers (macos-latest, SDK 10.0.300)` | `package-consumers/osx/net10.0` | package-consumers | `package-compatibility` |
| `Required Redis all-package consumers (net8.0)` | `package-consumers-redis/net8.0` | package-consumers | `redis-package-compatibility` |
| `Required Redis all-package consumers (net9.0)` | `package-consumers-redis/net9.0` | package-consumers | `redis-package-compatibility` |
| `Required Redis all-package consumers (net10.0)` | `package-consumers-redis/net10.0` | package-consumers | `redis-package-compatibility` |
| `PostgreSQL projections (net8.0)` | `provider-postgres/net8.0` | provider | `projection-postgresql` |
| `PostgreSQL projections (net9.0)` | `provider-postgres/net9.0` | provider | `projection-postgresql` |
| `PostgreSQL projections (net10.0)` | `provider-postgres/net10.0` | provider | `projection-postgresql` |
| `Distributed cache Redis (net8.0)` | `provider-redis-cache/net8.0` | provider | `caching-redis` |
| `Distributed cache Redis (net9.0)` | `provider-redis-cache/net9.0` | provider | `caching-redis` |
| `Distributed cache Redis (net10.0)` | `provider-redis-cache/net10.0` | provider | `caching-redis` |
| `EF transactions (net8.0)` | `ef-transactions/net8.0` | provider | `ef-transactions` |
| `EF transactions (net9.0)` | `ef-transactions/net9.0` | provider | `ef-transactions` |
| `EF transactions (net10.0)` | `ef-transactions/net10.0` | provider | `ef-transactions` |
| `EF10 compatibility (net10.0)` | `ef10-compatibility/net10.0` | provider | `ef10-compatibility` |
| `HTTP orders, real Redis and SQLite (net10.0)` | `http-integration/net10.0` | provider | `http-integration` |
| `native AOT linux-x64 (net8.0)` | `native-aot/linux-x64/net8.0` | native-aot | `native-aot` |
| `native AOT linux-x64 (net9.0)` | `native-aot/linux-x64/net9.0` | native-aot | `native-aot` |
| `native AOT linux-x64 (net10.0)` | `native-aot/linux-x64/net10.0` | native-aot | `native-aot` |
| `native AOT osx-arm64 (net8.0)` | `native-aot/osx-arm64/net8.0` | native-aot | `native-aot` |
| `native AOT osx-arm64 (net9.0)` | `native-aot/osx-arm64/net9.0` | native-aot | `native-aot` |
| `native AOT osx-arm64 (net10.0)` | `native-aot/osx-arm64/net10.0` | native-aot | `native-aot` |
| `property counts (net10.0)` | `property-counts/net10.0` | property-counts | `property-counts` |
| `coverage gate (net10.0)` | `coverage/net10.0` | coverage | `coverage` |
| `warning ratchet (net10.0)` | `warning-ratchet/net10.0` | warning-ratchet | `warning-ratchet` |
| `current servicing (net8.0)` | `current-servicing/net8.0` | current-servicing | none (local) |
| `current servicing (net9.0)` | `current-servicing/net9.0` | current-servicing | none (local) |
| `current servicing (net10.0)` | `current-servicing/net10.0` | current-servicing | none (local) |
| `current servicing package consumers (net8.0)` | `current-servicing-consumers/net8.0` | package-consumers | none (local) |
| `current servicing package consumers (net9.0)` | `current-servicing-consumers/net9.0` | package-consumers | none (local) |
| `current servicing package consumers (net10.0)` | `current-servicing-consumers/net10.0` | package-consumers | none (local) |
| `eng tooling tests` | (infrastructure) | python tests | `eng-tooling-tests` |
| `candidate gate` | (infrastructure) | scoped aggregation | `candidate-gate` |

Not required: `preview samples (net11.0, optional)` (`continue-on-error`, no stable job depends on it; net11 preview
is never a required lane).

### What each check proves

* `build + test (<os>, <tfm>)`: every suite in the `runtime` profile on that operating system, architecture and runtime
  family (counts at or above the recorded minimum, zero failed, **zero skipped**), plus the named samples (6 for net8.0
  and net9.0, 7 for net10.0 which also runs QuickStart). Lanes are bound to the runner architecture (`ubuntu-latest` and
  `windows-latest` x64, `macos-latest` arm64); arm64 Linux evidence does not satisfy an `ubuntu-latest` lane. CI installs
  the exact pinned SDKs into a private `DOTNET_INSTALL_DIR`, so the build SDK is exactly 10.0.300 and each family has
  exactly its pinned runtime patch; the gate checks both from the retained `lane-toolchain.json`.
* `package consumers (<os>, SDK <sdk>)`: the isolated native-compiler consumers restored from the candidate feed,
  compiled with that exact SDK on that operating system, C# 12 and warnings as errors, restored package and analyzer
  hashes equal to the feed. This is the **SDK-host** lane; it is a different job from the runtime lane. Selected plus
  omitted consumers must equal the inventory consumers and every selected consumer variant must have its row. These
  backend-free lanes explicitly omit `Meridian.RedisIdempotencyConsumer` and `Meridian.EfTransactionsConsumer`
  (`consumerScope: partial`); hosted Windows/macOS jobs have neither provider service.
* `Required Redis all-package consumers (<tfm>)`: every manifest consumer, including the Redis idempotency and EF
  transaction consumers, with real pinned Redis **and PostgreSQL** services (`consumerScope: all`). The existing check
  name is retained for stability. Both services are also required by the local current-servicing all-consumer lanes;
  their endpoint and container identity must be collected. A missing service fails the lane.
* `PostgreSQL projections`, `Distributed cache Redis`, `EF transactions`, `EF10 compatibility`,
  `HTTP orders, real Redis and SQLite`: provider contracts against Redis 7.4 / PostgreSQL 16.4 service containers pinned
  by digest. The collector probes the endpoint named by `MERIDIAN_TEST_REDIS` / `MERIDIAN_TEST_POSTGRES` and resolves the
  serving container with `docker ps --no-trunc` / `docker inspect`; the image's `RepoDigests` must contain the pinned
  digest. An unreachable endpoint, another image or a merely claimed image fails the lane. The HTTP lane also requires the
  bounded HTTP smoke row (exit 0, no timeout, ownership validated, process exit and pipe drains observed) and its
  ownership receipt.
* `native AOT <rid> (<tfm>)`: the packaged consumer is published with `PublishAot` and trimming and the **native
  binary is executed** on a matching real runner. The 12 case ids, the SUMMARY marker, the non-JIT NATIVE marker and the
  runtime family are re-read from the executed output; the receipt must name this candidate and its package record, the
  executable format must match the RID, and the unchanged mapping-only consumer must have executed with its semantic
  proof from the candidate's `Meridian.Mapping` archive. Exactly one receipt of each kind is accepted.
* `property counts (net10.0)`: the elevated FsCheck budgets actually executed (three 10000-case and two 500-case
  properties, parsed from the run log; default PR budgets do not satisfy it), no falsified property, and the five
  property tests passed in the retained TRX.
* `coverage gate (net10.0)`: see Coverage below.
* `warning ratchet (net10.0)`: full `--no-incremental` Release build of `Meridian.sln` with the pinned build SDK. The gate
  recomputes the ratchet from the retained build log against `eng/warning-baseline.json`: per-project, per-code warning
  counts may not exceed the baseline, every one of the 30 solution projects must appear as built, the logged command must
  be the non-incremental solution build and its exit 0. Projects outside the solution (consumers, `samples/Meridian.NativeAot`,
  `tests/compatibility`) are outside the ratchet.
* `current servicing (<tfm>)` and `current servicing package consumers (<tfm>)`: see Current servicing below.
* `eng tooling tests`: the python/PowerShell tooling tests (gate, collector, coverage, workflows, inventory, docs).
* `candidate gate`: re-derives the evidence of every CI-defined lane for the one candidate feed (scoped verdict).

## Support tables (proof level separate from claim)

Pinned compatibility lanes: SDK 8.0.421 / 9.0.318 / 10.0.300 with runtimes 8.0.27 / 9.0.20 / 10.0.8 (build SDK
10.0.300). These are genuine historical compatibility proof, **not** Microsoft current servicing.

### Runtime families by operating system (suites + samples)

| Runtime | macOS arm64 | Linux x64 | Windows x64 |
| --- | --- | --- | --- |
| net8.0 | `local-executed` in earlier tasks (E4b/D4 receipts) | `UNAVAILABLE` locally; `CI-defined-not-yet-run` | `UNAVAILABLE` locally; `CI-defined-not-yet-run` |
| net9.0 | `local-executed` in earlier tasks | as above | as above |
| net10.0 | `local-executed` in earlier tasks | as above | as above |

Earlier Linux runs used an ARM64 container; they do not satisfy the x64-bound `ubuntu-latest` lanes. The final
candidate's three-TFM cumulative run is the E4d campaign; these rows describe earlier task evidence plus the lane
definitions, not a final-candidate pass.

### SDK hosts and compilers (package consumers)

| Compiler host (SDK / Roslyn) | macOS arm64 | Linux x64 | Windows x64 |
| --- | --- | --- | --- |
| 8.0.421 (csc 4.11) | `local-executed` (D4: 38 PASS entries, 11 consumers) | `CI-defined-not-yet-run` | `CI-defined-not-yet-run` |
| 9.0.318 (csc 4.14) | `local-executed` | `CI-defined-not-yet-run` | `CI-defined-not-yet-run` |
| 10.0.300 (csc 5.6) | `local-executed` | `CI-defined-not-yet-run` | `CI-defined-not-yet-run` |
| 11.0 preview | not claimed (optional, non-blocking job `preview`) | | |

C# language version 12 and warnings-as-errors are enforced inside every consumer. Roslyn 4.8 as a generator
*compile-time dependency* is not proof that SDK 8.0.100 executes it; A4 tested 8.0.421 explicitly.

### Providers

| Provider (pinned digest) | Proof level |
| --- | --- |
| Redis 7.4-alpine | `local-executed` on owned Docker containers (aarch64 daemon) in earlier tasks; `CI-defined-not-yet-run` as service containers |
| PostgreSQL 16.4-alpine | same |
| Other Redis/PostgreSQL versions, other providers, managed services | not claimed |

### Native AOT

| RID | net8.0 | net9.0 | net10.0 |
| --- | --- | --- | --- |
| osx-arm64 | `local-executed` in earlier tasks (12/12 cases + mapping-only, D4) | `local-executed` | `local-executed` |
| linux-x64 | `UNAVAILABLE` (no real x64 Linux runner; Docker here is an aarch64 daemon); `CI-defined-not-yet-run` on `ubuntu-24.04` | same | same |
| linux-arm64, win-x64, win-arm64, osx-x64 | not claimed | | |

### Current servicing versus pinned compatibility

Microsoft supports only the latest servicing patch of each supported family
([support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)). The official release
metadata (`releases.json` for 8.0, 9.0 and 10.0, read 2026-11-16, SHA-256 recorded in the policy) names the patches
released 2026-11-10:

| Family | Pinned compatibility (kept, unchanged) | Latest patch checked (8/9 are end of support) | Proof level |
| --- | --- | --- | --- |
| net8.0 | SDK 8.0.421 / runtime 8.0.27 | SDK 8.0.428 / runtime 8.0.34 | defined; **not executed** (E4d) |
| net9.0 | SDK 9.0.318 / runtime 9.0.20 | SDK 9.0.321 / runtime 9.0.23 | defined; **not executed** (E4d) |
| net10.0 | SDK 10.0.300 / runtime 10.0.8 | SDK 10.0.404 / runtime 10.0.15 | defined; **not executed** (E4d) |

Six local-only lanes (`ci: null`) verify the candidate under these patches. The .NET 8 and .NET 9 runs are final-patch compatibility checks; .NET 10 is still serviced. They run with one isolated dotnet root named
by `MERIDIAN_SERVICING_DOTNET_ROOT` that holds the current SDKs and exactly one runtime patch per family; no
machine-specific path is committed.

* `current servicing (<tfm>)`: builds the solution with SDK 10.0.404 and runs every runtime-profile suite unfiltered
  (core, adapters and the Redis/PostgreSQL provider tests, so the services are required) on the current runtime patch of
  the family. The retained toolchain receipt must show SDK 10.0.404 selected and the current patch as the **only** runtime
  of that family.
* `current servicing package consumers (<tfm>)`: every inventory consumer (including Redis and EF/PostgreSQL) compiled with the current
  family SDK (8.0.428 / 9.0.321 / 10.0.404) and run on the current runtime patch.

Metadata and downloads are not proof. Re-resolve the latest stable patches at acceptance, update the policy rows and this
table in the same change, and execute the six lanes. Microsoft support for net8 and net9 ended 2026-11-10; Meridian's own
TFM retirement policy is a separate fact and the TFMs are kept until a separately reviewed major release.

### Operating cost (not measured)

A full CI run is about 46 jobs: 9 runtime, 9 + 3 consumer, 3 + 3 + 3 + 1 + 1 provider, 6 native (3 on `macos-15`,
the most expensive runner class), 3 single-purpose lanes, pack, tooling tests and the gate. The consumer lanes restore
from nuget.org. The nine partial lanes compile 11 of the 13 inventory consumers; all-consumer lanes compile all 13 and
require both Redis and PostgreSQL. Native publishes dominate wall time; the tooling tests take about 25
minutes on a developer machine. No runner-minute figure has been measured; do not treat the numbers above as a budget.
The candidate is packed once. Coverage is collected only in the dedicated lane.

## Coverage

Tool: `coverlet.collector 10.0.1` (official nuget.org package, pinned in every runtime-profile test project; added to
the `Mapping.EntityFramework`, `Mediator.EntityFrameworkCore`, `Mediator.AspNetCore` and `Mediator.Generators` test
projects). The coverage lane runs every runtime-profile suite on net10.0 **unfiltered** (real Redis and PostgreSQL), with
TRX and Cobertura retained. `eng/coverage_gate.py` unions the Cobertura output and reports every repository
`src/**/*.cs` file (build output excluded; nothing else excluded). The evaluator recomputes the gate from the retained
documents against the committed `eng/coverage-baseline.json` of the evaluated source; a receipt that disagrees with the
recomputation, or was produced against another baseline, fails the lane. Baseline measurement recorded before and after
the E4 boundary tests: aggregate line 89.51% to 91.76%, branch 78.57% to 80.94% (earlier local measurement; the next
coverage-lane run re-measures the frozen source).

* **Ratchet**: no file's line or branch coverage, and no aggregate, may fall below its recorded floor (rounded down to
  0.1; tolerance 0.5 percentage points because timing-dependent code varies by a few lines between identical runs). A
  file or aggregate measured without lines or branches where a non-zero floor exists fails. `coverage_gate.py baseline`
  only raises floors; lowering one is a reviewed edit of the JSON.
* **Hot paths** (changed core paths: FastPathCompiler, MappingEngine, Mediator, generated registration/composition,
  behaviors, metrics, registration extensions, the notification publishers) must reach **90% line and 80% branch**. A
  baseline may raise these targets but not lower them. A hot path missing from the coverage data, or present without
  measured lines, fails. **There are no waivers**: a baseline that declares any is rejected as invalid input.

| Hot-path file | Before (line / branch %) | After (line / branch %) | Status |
| --- | --- | --- | --- |
| `Meridian.Mapping/Execution/FastPathCompiler.cs` | 96.42 / 81.6 | 96.42 / 81.6 | meets 90 / 80 |
| `Meridian.Mapping/Execution/MappingEngine.cs` | 86.63 / 77.45 | 96.01 / 86.64 | meets 90 / 80 |
| `Meridian.Mediator/Mediator.cs` | 91.43 / 77.33 | 97.14 / 84.67 | meets 90 / 80 |
| `Meridian.Mediator/Generated/GeneratedMediatorBehaviorCapability.cs` | 100.0 / - | 100.0 / - | meets 90 / 80 |
| `Meridian.Mediator/Generated/GeneratedMediatorComposition.cs` | 97.14 / 82.14 | 97.14 / 82.14 | meets 90 / 80 |
| `Meridian.Mediator/Generated/GeneratedMediatorContribution.cs` | 95.45 / 50.0 | 100.0 / 100.0 | meets 90 / 80 |
| `Meridian.Mediator/Generated/GeneratedMediatorRegistration.cs` | 97.24 / 86.9 | 97.24 / 86.9 | meets 90 / 80 |
| `Meridian.Mediator/Generated/GeneratedRequestExceptionProcessorBehavior.cs` | 97.14 / 100.0 | 97.14 / 100.0 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/AuditBehavior.cs` | 98.28 / 66.67 | 98.28 / 91.67 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/AuthorizationBehavior.cs` | 66.67 / 46.88 | 100.0 / 100.0 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/CachingBehavior.cs` | 100.0 / 92.86 | 100.0 / 92.86 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/IdempotencyBehavior.cs` | 98.39 / 95.88 | 98.39 / 95.88 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/LocalizedValidationBehavior.cs` | 91.67 / 83.33 | 91.67 / 83.33 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/RetryBehavior.cs` | 95.0 / 100.0 | 95.0 / 100.0 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/RetryPolicy.cs` | 100.0 / 78.12 | 100.0 / 100.0 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/TransactionBehavior.cs` | 100.0 / 93.75 | 100.0 / 93.75 | meets 90 / 80 |
| `Meridian.Mediator/Behaviors/ValidationBehavior.cs` | 100.0 / 91.67 | 100.0 / 91.67 | meets 90 / 80 |
| `Meridian.Mediator/Diagnostics/MediatorMetrics.cs` | 85.66 / 78.57 | 97.85 / 91.21 | meets 90 / 80 |
| `Meridian.Mediator/Diagnostics/ObservedStream.cs` | 90.96 / 85.53 | 90.96 / 85.53 | meets 90 / 80 |
| `Meridian.Mediator/Extensions/ServiceCollectionExtensions.cs` | 96.35 / 94.83 | 96.35 / 94.83 | meets 90 / 80 |
| `Meridian.Mediator/Extensions/MeridianMediatorConfiguration.cs` | 79.14 / 67.5 | 100.0 / 100.0 | meets 90 / 80 |
| `Meridian.Mediator/Extensions/DynamicRegistrationGuard.cs` | 0.0 / - | not yet re-measured | named boundary test added (no waiver); its only member is now executed directly |
| `Meridian.Mediator/Publishing/TaskWhenAllPublisher.cs` | 85.71 / 68.33 | 98.9 / 100.0 | meets 90 / 80 |

Before the boundary tests the following hot paths were below target and received named tests (no exclusions were
added): `MappingEngine`, `Mediator`, `AuditBehavior`, `AuthorizationBehavior`, `RetryPolicy`, `MediatorMetrics`,
`TaskWhenAllPublisher`, `MeridianMediatorConfiguration`, `GeneratedMediatorContribution` and `DynamicRegistrationGuard`.
The tests are `HotPathBoundaryTests`, `MetricsBoundaryTests` and `MediatorTelemetryBoundaryTests` (Mediator.Tests) and
`MappingEngineBoundaryTests` (Mapping.Tests). `DynamicRegistrationGuard` is reached in production only when
`RuntimeFeature.IsDynamicCodeSupported` is false (Native AOT, case `native-scanning-unsupported`); its failure contract
(operation name and the generated-registry replacement) is now pinned by
`Dynamic_Registration_Guard_Names_The_Operation_And_The_Generated_Registry_Replacement` instead of a waiver. Its
recorded floor stays 0.0 until a coverage-lane run raises it with `coverage_gate.py baseline`.

Within other hot paths a few Native-AOT-only guards, defensive double checks of `ActivitySource.HasListeners()` and
`FrozenDictionary` admission edge cases remain uncovered but the files meet both targets.

### Changed files below target that are not named hot paths

These are ratcheted at their measured floors: `Mapper.cs` (64.6 / 62.5), `MapperConfiguration.cs` (74.9 / 60.6),
`MapperConfigurationExpression.cs`, `Profile.cs`, `ObjectCreator.cs`, `IMapperConfigurationExpression.cs`,
`MediatorHandlerValidation.cs`, the three `Wrappers/*` files and the generators' `MapperModel.cs`. They need real tests
in a later task; raising their floors is the intended outcome of that work.

## Governance: effective repository state (read-only evidence)

Read-only `gh` calls on 2026-10-23 against `UmutKorkmaz/meridian` (default branch `main`, not archived): the branch
protection document, the rulesets list and the Actions permissions. No write call, push, PR or issue was made.
CODEOWNERS and workflow files alone are not proof of enforcement.

| Setting | Observed 2026-10-23 |
| --- | --- |
| Required status checks on `main` | **not enabled** (the protection document has no `required_status_checks` block): no CI check blocks a merge |
| Required pull-request reviews | **not enabled** (no `required_pull_request_reviews` block) |
| Code-owner reviews | not required |
| Enforce for administrators, signed commits, linear history, conversation resolution | all off |
| Force pushes / deletions / branch creation blocking / branch lock | force pushes off, deletions off, creation blocking off, not locked |
| Rulesets | none (`[]`) |
| Actions | enabled, all actions allowed, SHA pinning not required; this repository references actions by major version tag |

Default workflow token permission and secret-scanning state were not re-read on 2026-10-23 and are not claimed here.
Recent remote runs belong to older pushed commits and are not evidence about this candidate.

## Proposed settings for the maintainer (proposal only; nothing was changed)

The only maintainer is the repository administrator; a second reviewer does not exist and must not be simulated. A
maintainer with admin rights would configure (branch protection or an equivalent ruleset) on `main`:

1. Require a pull request with 1 approving review (2 once at least three maintainers are available, allowing two
   reviewers other than the PR author), dismiss stale approvals, require
   review from code owners. With a single maintainer, either add a second reviewer or set an explicit, documented
   bypass for the owner; do not enable "require approval of the latest push" until a second reviewer exists (it would
   make the sole author unable to merge).
2. Require status checks, **strict (up to date)**, with exactly these checks: `candidate packages`, `candidate gate`,
   `eng tooling tests`, every `build + test (...)` (9), every `package consumers (...)` (9),
   every `Required Redis all-package consumers (...)` (3), `PostgreSQL projections (...)` (3),
   `Distributed cache Redis (...)` (3), `EF transactions (...)` (3), `EF10 compatibility (net10.0)`,
   `HTTP orders, real Redis and SQLite (net10.0)`, `property counts (net10.0)`, `coverage gate (net10.0)`,
   `warning ratchet (net10.0)`, and the six `native AOT ...` checks **only after** a real linux-x64 and osx-arm64 run
   has succeeded (a required check that cannot run blocks every merge). Check names must be copied from the table above
   after they first appear on a PR; do not require `preview samples` or any `current servicing` check (local lanes).
3. Require conversation resolution, linear history and signed commits; no force pushes, no deletions; enforce for
   administrators once the owner accepts the friction (until then the documented owner bypass of item 1 applies).
4. Protect `v*` tags with a ruleset (restrict creation to maintainers, block updates and deletion). The local
   `release.yml` accepts a prepared immutable bundle and its approved digest through a manual dispatch, and writes a
   read-only promotion plan. Trusted execution authorization and a production write transport remain unavailable.
   Before enabling publication, require an appropriately protected environment and verify the deployed workflow.
5. Enable "require actions to be pinned to a full commit SHA" after converting the action references (Dependabot can do
   this); until then the structural test only requires a consistent version tag or SHA.
6. Review secret scanning, push protection and Dependabot security updates (state not re-verified here).
7. Verification after changing settings: open a throwaway PR with a failing test and confirm the merge is blocked,
   remove the failure and confirm a failing `preview samples` job does not block it, and re-read the settings with the
   same read-only calls.

## Known gaps and limitations

* Every CI claim is `CI-defined-not-yet-run`; Windows and linux-x64 are `UNAVAILABLE` here.
* The local `release.yml` is a manual, read-only immutable-bundle promotion plan; it has no tag publisher or rebuild.
  Hosted adoption has not been verified. A green CI run remains a **scoped** verdict (`releaseEligible: false`);
  preparation also requires the full aggregate evidence for the exact candidate, and execution remains blocked.
* Candidate summary artifacts record `ciAttempt.runId` and `ciAttempt.runAttempt`. Artifact consumers must require
  these to match the requested attempt; the canonical artifact name alone is insufficient. Failed or skipped
  evaluation writes a fresh FAIL summary, and both summary uploads fail if the files are absent.
* The aggregate CI verdict is scoped; the candidate verdict needs the final cumulative campaign (E4d) including the six
  local `current servicing` lanes.
* Service evidence rests on the runner's Docker daemon (`docker inspect` RepoDigests) and a TCP reachability probe; the
  provider suites themselves prove the service answered. Host identity is recorded by the runner and cross-checked only
  through test-binary path style and native markers.
* A lane's `candidate.packages` binding comes from the feed the lane was given; that the consumers and native publishes
  restored exactly those archives rests on verify-packages / run-aot-smoke (which fail on a mismatch) and, for the
  mapping-only native row, on the retained `package-hash.json`.
* The gate emits the repository's `meridian-restored-package-inventory`. The local release workflow only plans
  promotion of a prepared bundle and does not generate a CycloneDX SBOM. No SBOM publication is claimed.

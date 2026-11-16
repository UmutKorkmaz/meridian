# Capabilities and evidence

This is the claim ledger for Meridian. Every capability that a README, guide or the project site advertises has one
row here that names its package and mode, its version status, the page that explains it, a runnable proof, the regression
matrix rows that define its contract (`docs/superpowers/plans/2026-10-02-regression-matrix.md` in the repository), and the boundary of what has actually been verified. A row describes evidence, not a
promise: a combination that has not been executed is listed as PENDING or unverified, never as supported.

<a id="status-legend"></a>
## How to read the status

| Term | Meaning |
| --- | --- |
| **2.1.3** | Behavior of the published 2.1.3 packages, kept by the candidate. |
| **2.1.3, changed default** | Present in 2.1.3; the candidate hardens the default and keeps a documented opt-out. |
| **Candidate only** | New in the unpublished 2.1.4 prerelease candidate. No stable version ships it yet. |
| **Accepted locally** | The owning task's tests, packed-package consumers and reviews passed on the local host (macOS arm64) and the controller accepted the task. |
| **Cumulative E4 PENDING** | The cross-task release gate (all rows together, Windows/Linux lanes, required checks) has not run yet. |
| **PENDING** | Required infrastructure was not available; the claim is not made until it runs. |

Verified host for every row unless stated otherwise: macOS 26 on Apple silicon (osx-arm64), .NET runtimes 8.0.27,
9.0.20 and 10.0.8, SDKs 8.0.421, 9.0.318 and 10.0.300. Windows and linux-x64 runtime lanes, hosted CI runs and any
publication are PENDING. Evidence records (test result files, package hashes, review reports) are local task receipts
in the release ledger; the repository holds the tests, consumers and scripts that reproduce them.

<a id="runnable-proof"></a>
## Runnable proof used below

| Fixture | What it runs | How |
| --- | --- | --- |
| `tests/consumers/Meridian.DocumentationConsumer` | Every compiled snippet in the guides and core package READMEs, with assertions on values and failure paths | `eng/verify-packages.ps1` on SDK 8/9/10, C# 12, warnings as errors, exact packed packages |
| `tests/consumers/Meridian.LegacyConsumer` | 2.1.3 source compatibility, AutoMapper-shaped aliases, old and migrated variants, compatibility opt-outs | same, plus a published 2.1.3 baseline comparison on SDK 10 |
| `tests/consumers/Meridian.GeneratedConsumer`, `Meridian.GeneratedOnlyConsumer` | Generated Legacy/Structural mapping and the resolver bridge | same |
| `tests/consumers/Meridian.MediatorRegistryConsumer` | Generated mediator registries and strict dispatch | same |
| `tests/consumers/Meridian.NativeAotConsumer` | Mediator + mapping published with `PublishAot` and executed | `eng/run-aot-smoke.ps1 -Combined` |
| Adapter consumers (`FluentValidation`, `Caching`, `DistributedCaching`, `RedisIdempotency`, `EfTransactions`, `Http`) | Each optional package on its own dependency closure | `eng/verify-packages.ps1` with the providers the row names |
| `samples/*` | Console samples with assertions; the ASP.NET Core orders API | `eng/run-samples.ps1` |

<a id="mapping"></a>
## Meridian.Mapping

| ID | Capability | Mode | Version | Guide | Runnable proof | Rows | Boundary and status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| <a id="cap-map-overloads"></a>MAP-01 | `Map` generic, object and runtime-type overloads; null source and nullable defaults | Runtime | 2.1.3 | [Migrate from AutoMapper](migrate-from-automapper.md) | DocumentationConsumer `mapping-usage`; `Meridian.Mapping.Tests` | M01 | net8/9/10. Accepted locally (A6); cumulative E4 PENDING. |
| <a id="cap-map-profiles"></a>MAP-02 | Profiles, assembly scanning, DI registration and `AddAutoMapper` aliases | Runtime | 2.1.3 | [Migrate from AutoMapper](migrate-from-automapper.md) | DocumentationConsumer `mapping-registration`, `mapping-alias`; LegacyConsumer old/migrated | M02, P01 | Aliases are AutoMapper-shaped Meridian APIs, not AutoMapper itself. Accepted locally (A6, E2). |
| <a id="cap-map-members"></a>MAP-03 | `ForMember`, `MapFrom`, `Ignore`, conditions, null substitution | Runtime | 2.1.3 | [Migrate from AutoMapper](migrate-from-automapper.md) | DocumentationConsumer `mapping-profile`; `Meridian.Mapping.Tests` | M03 | Accepted locally (A6, B6). |
| <a id="cap-map-flattening"></a>MAP-04 | Flattening, naming conventions, `IncludeMembers`, `ForPath`, `ReverseMap` | Runtime | 2.1.3 | API reference | `Meridian.Mapping.Tests` | M04, M05 | No unsupported expression reversal is claimed. Accepted locally (A6). |
| <a id="cap-map-constructors"></a>MAP-05 | Constructors, `ForCtorParam`, `ConstructUsing` | Runtime | 2.1.3, changed default | [Package compatibility](package-compatibility.md#runtime-constructor-correction-a6) | `Meridian.Mapping.Tests` | M06 | Missing required constructor arguments now throw instead of silently using type defaults. Accepted locally (A6, B3). |
| <a id="cap-map-extensibility"></a>MAP-06 | Type/member converters, DI resolvers, before/after actions, `Items`/`State` | Runtime | 2.1.3 | API reference | `Meridian.Mapping.Tests` | M07, M08 | Accepted locally (A6, B6). |
| <a id="cap-map-collections"></a>MAP-07 | Collections and dictionaries, map onto existing, inheritance and open generics, `PreserveReferences` | Runtime | 2.1.3 | [Migrate from AutoMapper](migrate-from-automapper.md) | DocumentationConsumer `mapping-usage`; `Meridian.Mapping.Tests` | M09–M12 | Duplicate dictionary keys keep last-write-wins. Accepted locally (A6, B5, B6, B8). |
| <a id="cap-map-conversions"></a>MAP-08 | Scalar, nullable and enum conversions; Turkish culture helpers | Runtime | 2.1.3, changed default | [Package compatibility](package-compatibility.md#api-preservation-and-behavior-migration) | LegacyConsumer; `Meridian.Mapping.Tests` | M13, M16 | Undefined enum values are rejected by default (`AllowUndefinedEnumValues=false`, opt-out kept). Accepted locally (A6, B4). |
| <a id="cap-map-validation"></a>MAP-09 | `AssertConfigurationIsValid` and mapping-plan diagnostics | Runtime | 2.1.3 | API reference | DocumentationConsumer; `Meridian.Mapping.Tests` | M14 | Accepted locally (A6, B6). |
| <a id="cap-map-projection"></a>MAP-10 | `ProjectTo` query projection, executed on SQLite and PostgreSQL | Runtime | 2.1.3; provider proof candidate only | [Projection providers](projection-providers.md) | `tests/Meridian.Mapping.EntityFramework.Tests` | M15, N03 | Only the enumerated shapes and EF/provider versions in that page; requires `MERIDIAN_TEST_POSTGRES` for PostgreSQL. Accepted locally (B7). |
| <a id="cap-map-limits"></a>MAP-11 | Safety limits: depth 64, 10000 collection items, nulls as documented | Runtime | 2.1.3 | [Migrate from AutoMapper](migrate-from-automapper.md#safety-defaults) | DocumentationConsumer `mapping-limits` | M17 | Defaults are a stable contract (see [compatibility policy](compatibility-policy.md)). Accepted locally (A6, B5, B8). |
| <a id="cap-map-fastpath"></a>MAP-12 | Compiled fast path with interpreter fallback; nested inlining | Runtime | 2.1.3; nested inlining candidate only | API reference | `Meridian.Mapping.Tests` (`NestedFastPathParityTests`) | M18, N10 | Correctness accepted (B8). **Performance claim PENDING/INCONCLUSIVE**: measured on a non-dedicated host, re-measurement owned by E3. |
| <a id="cap-gen-legacy"></a>MAP-13 | Direct generated Legacy methods | Generated | 2.1.3 (contract), candidate analyzer | [Generated mapping](generated-mapping.md#legacy) | DocumentationConsumer `generated-legacy`; GeneratedConsumer | M19, M20, P02 | Existing method names and shallow assignment kept. Accepted locally (A3, B1–B3). |
| <a id="cap-gen-structural"></a>MAP-14 | Direct generated Structural mode: constructors, checked conversions, graphs, collections | Generated | Candidate only | [Generated mapping](generated-mapping.md#structural) | DocumentationConsumer `generated-structural`; GeneratedConsumer; GeneratedOnlyConsumer | N01 | Checked `double`→`float` overflow yields infinity, not an exception. Accepted locally (B3–B5). |
| <a id="cap-gen-integration"></a>MAP-15 | Generated resolvers registered with `IMapper` | Generated + runtime | Candidate only | [Generated mapping](generated-mapping.md#runtime-integration) | DocumentationConsumer `generated-precedence`; GeneratedConsumer | N02 | Explicit runtime maps win; create-new only; no full profile equivalence. Accepted locally (B6). |

<a id="mediator"></a>
## Meridian.Mediator

| ID | Capability | Mode | Version | Guide | Runnable proof | Rows | Boundary and status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| <a id="cap-med-requests"></a>MED-01 | Typed and void requests, object `Send`, missing-handler failure | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md) | DocumentationConsumer `mediator-dispatch` | Q01 | Accepted locally (A7, D2). |
| <a id="cap-med-di"></a>MED-02 | DI registration, assembly scanning, scoped dependencies | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md) | DocumentationConsumer `mediator-registration` (scope disposal asserted) | Q02 | Open generic handlers need explicit closed registrations. Accepted locally (A7, D1, D2). |
| <a id="cap-med-pipeline"></a>MED-03 | Open and closed pipeline behaviors, ordering and de-duplication | Runtime | 2.1.3, changed default | [Migrate from MediatR](migrate-from-mediatr.md#behaviors) | DocumentationConsumer `mediator-open-behavior`; LegacyConsumer | Q03 | Explicit order is honored across open/closed registrations and duplicates are removed (see [package compatibility](package-compatibility.md#api-preservation-and-behavior-migration)). Accepted locally (A7, D2, D3). |
| <a id="cap-med-processors"></a>MED-04 | Pre/post processors, request exception handlers and actions | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md#behaviors) | DocumentationConsumer `behavior-processors` | Q04 | Exception handlers run only when `RequestExceptionProcessorBehavior<,>` is registered. Accepted locally (A7, D2). |
| <a id="cap-med-notifications"></a>MED-05 | Notifications; sequential default publisher, concurrent publishers capped at 16 by default | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md#notifications-and-streams) | DocumentationConsumer `mediator-dispatch`; `Meridian.Mediator.Tests` | Q05 | Accepted locally (A7, D3). |
| <a id="cap-med-streams"></a>MED-06 | Streams, stream behaviors, cancellation and disposal | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md#notifications-and-streams) | DocumentationConsumer `mediator-dispatch` (cancellation asserted) | Q06 | Accepted locally (A7, D3). |
| <a id="cap-med-validation"></a>MED-07 | Validation and localized validation behaviors | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md#behaviors) | DocumentationConsumer `mediator-open-behavior`, `standard-pipeline` | Q07 | Accepted locally (A8, C1). |
| <a id="cap-med-authorization"></a>MED-08 | Authorization behavior | Runtime | 2.1.3, changed default | [Migrate from MediatR](migrate-from-mediatr.md#behaviors) | DocumentationConsumer `behavior-composition`; LegacyConsumer | Q08 | A request without an authorization handler is rejected by default; `AllowRequestsWithoutAuthorizationHandlers=true` is the compatibility opt-out. Accepted locally (A8, C6). |
| <a id="cap-med-caching"></a>MED-09 | Caching and invalidation behaviors | Runtime | 2.1.3, changed default | [Migrate from MediatR](migrate-from-mediatr.md#behaviors) | DocumentationConsumer `behavior-composition`; LegacyConsumer | Q09 | Keys are composed per query type; invalidation must use `CacheKeyComposer.Compose<TQuery>`; raw keys need the explicit opt-out. Accepted locally (A2, A8, C2, C3). |
| <a id="cap-med-idempotency"></a>MED-10 | Idempotency behavior with legacy, atomic and leased stores | Runtime | 2.1.3, changed default | [Integrations](integrations.md#redis) | DocumentationConsumer `behavior-composition`; RedisIdempotencyConsumer | Q10, N05 | The legacy check-then-store contract permits concurrent duplicates. No exactly-once guarantee. Accepted locally (A8, C4). |
| <a id="cap-med-retry"></a>MED-11 | Retry behavior | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md#behaviors) | DocumentationConsumer `behavior-composition` | Q11 | Cancellation and non-transient exceptions are not retried; at most 10 retries and 5 minutes backoff by default. Accepted locally (A8, C5). |
| <a id="cap-med-transactions"></a>MED-12 | Transaction behavior | Runtime | 2.1.3 | [Integrations](integrations.md#entity-framework-core) | DocumentationConsumer `behavior-composition`; EfTransactionsConsumer | Q12, N06 | Rollback does not undo external effects. Accepted locally (A8, C5). |
| <a id="cap-med-audit"></a>MED-13 | Audit and correlation behaviors | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md#standard-pipeline) | DocumentationConsumer `standard-pipeline` | Q13 | Accepted locally (A8, D5). |
| <a id="cap-med-privacy"></a>MED-14 | Logging and `Activity` telemetry privacy defaults | Runtime | 2.1.3 | [Compatibility policy](compatibility-policy.md#telemetry-privacy) | LegacyConsumer; DocumentationConsumer `standard-pipeline` | Q14 | `RecordExceptionMessage=false`, `RecordExceptionStackTrace=false` by default. Accepted locally (A8, D5). |
| <a id="cap-med-standard"></a>MED-15 | `AddMeridianStandard`: correlation (-100), audit (-50), localized validation (0) | Runtime | 2.1.3 | [Migrate from MediatR](migrate-from-mediatr.md#standard-pipeline) | DocumentationConsumer `standard-pipeline` | Q15 | Requires an `IStringLocalizer<T>` registration. Accepted locally (A7, A8). |
| <a id="cap-med-metrics"></a>MED-16 | Opt-in bounded metrics | Runtime | Candidate only | [Mediator metrics](mediator-metrics.md) | DocumentationConsumer `mediator-metrics`; `Meridian.Mediator.Tests` | N09 | Disabled by default. Correctness accepted (D5). **Overhead claim PENDING/INCONCLUSIVE** until E3 re-measures on a dedicated host. |
| <a id="cap-med-generated"></a>MED-17 | Generated registries, strict dispatch and standard pipeline (`AddMeridianStandard<TRegistry>`, `IncludeStandardPipeline=true`) | Generated | Candidate only | Package README `Meridian.Mediator.Generators` | MediatorRegistryConsumer (`standard=true`); NativeAotConsumer; RegistryGenerationTests `Pipeline_Capability_Is_Not_Silently_Ignored` | N07 | Requires the optional analyzer package. `MERIDIANMED001` checks declared expectations, `002` duplicate request/stream handlers, `003` malformed declarations/constructors and `005` unsupported constructions; `004` is reserved, with no inferred lifetime-graph guarantee. Accepted locally (D1–D3); compiler/native hosts remain bounded by their support tables. |

<a id="adapters"></a>
## Optional packages

| ID | Capability | Version | Guide | Runnable proof | Rows | Boundary and status |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="cap-fluentvalidation"></a>OPT-01 | `Meridian.Mediator.FluentValidation`: async FluentValidation rules in the validation behaviors | Candidate only | [Integrations](integrations.md#fluentvalidation) | FluentValidationConsumer | N04, Q07 | FluentValidation 12.1.1. Accepted locally (C1). |
| <a id="cap-caching"></a>OPT-02 | `Meridian.Mediator.Caching`: memory and distributed cache providers, scopes, coalescing | Candidate only | [Integrations](integrations.md#caching) | CachingConsumer, DistributedCachingConsumer | N04, Q09 | A memory provider run is not Redis evidence. Accepted locally (C2, C3). |
| <a id="cap-redis"></a>OPT-03 | `Meridian.Mediator.Redis`: leased idempotency and reconciliation | Candidate only | [Integrations](integrations.md#redis) | RedisIdempotencyConsumer; `tests/Meridian.Mediator.Redis.Tests` | N05 | Requires a Redis endpoint (`MERIDIAN_TEST_REDIS`); no exactly-once or failover-loss guarantee. Accepted locally (C4). |
| <a id="cap-ef"></a>OPT-04 | `Meridian.Mediator.EntityFrameworkCore`: owned transactions and savepoints | Candidate only | [Integrations](integrations.md#entity-framework-core) | EfTransactionsConsumer; `tests/Meridian.Mediator.EntityFrameworkCore.Tests` | N06, Q12 | EF Core Relational 8.0.20 minimum; PostgreSQL proof requires `MERIDIAN_TEST_POSTGRES`. Accepted locally (C5). |
| <a id="cap-aspnetcore"></a>OPT-05 | `Meridian.Mediator.AspNetCore`: redacted ProblemDetails; orders HTTP sample | Candidate only | [Integrations](integrations.md#aspnet-core) | HttpConsumer; `samples/Meridian.AspNetCore`; `tests/Meridian.AspNetCore.Tests` | N04, Q08 | The sample's `GET /orders` 200 response is described in OpenAPI as `oneOf` list or single order. Accepted locally (C6). |

<a id="platform"></a>
## Platforms, packaging and evidence

| ID | Capability | Version | Guide | Runnable proof | Rows | Boundary and status |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="cap-tfms"></a>PLT-01 | Target frameworks net8.0, net9.0, net10.0 for every runtime package | 2.1.3 | [Compatibility policy](compatibility-policy.md#support-boundaries) | All consumers on three runtimes | P05 | Executed on osx-arm64 only. **Windows and Linux runtime lanes PENDING** (configured in CI, not run). .NET 11 is an optional, non-blocking preview sample lane, not a package target. |
| <a id="cap-compilers"></a>PLT-02 | Compiler hosts SDK 8.0.421, 9.0.318, 10.0.300 with C# 12 for both analyzers | Candidate only | [Package compatibility](package-compatibility.md) | Every consumer, warnings as errors | P02 | The published 2.1.3 Mapping analyzer fails to load on SDK 8/9 (`CS9057`); the candidate fixes this. osx-arm64 only. |
| <a id="cap-aot"></a>PLT-03 | Native AOT for generated mediator dispatch and direct generated mapping | Candidate only | [Native AOT](native-aot.md) | NativeAotConsumer, GeneratedOnlyConsumer | N08 | osx-arm64 net8/9/10 **PASS**. linux-x64 **PENDING** (no real runner). Runtime `IMapper` and reflective mediator paths have no AOT claim. |
| <a id="cap-samples"></a>PLT-04 | Console samples and the ASP.NET Core sample | 2.1.3 samples, candidate assertions | [Samples](https://github.com/UmutKorkmaz/meridian/blob/main/samples/README.md) | `eng/run-samples.ps1` | P03 | Last accepted run: 6/6 on net8.0 and net9.0, 7/7 on net10.0 (D5). Hosted runs PENDING. |
| <a id="cap-property-tests"></a>PLT-05 | Property-based and fuzz testing | 2.1.3 | Repository | `tests/Meridian.Mapping.Tests` property tests; `tests/Meridian.Mapping.Fuzz` | P04 | Logged case counts are task evidence (A5); AFL fuzzing is labelled separately and not claimed as run here. |
| <a id="cap-packages"></a>PLT-06 | Package inventory, hashes, dependency/SBOM inventory, 2.1.3 API baseline validation | Candidate only | [Package compatibility](package-compatibility.md) | `eng/pack-packages.ps1`, `eng/verify-packages.ps1` | P01, P06 | Signing, provenance attestations and NuGet/site publication are **PENDING** (release task E5, explicit authorization required). |
| <a id="cap-benchmarks"></a>PLT-07 | Benchmarks | Historical | `benchmarks/Meridian.Benchmarks/README.md` | `benchmarks/Meridian.Benchmarks` | N10 | Earlier numbers are historical. Current B8 and D5 timing results are INCONCLUSIVE (non-dedicated host); E3 owns fair re-measurement. No performance claim is made for the candidate. |
| <a id="cap-docs"></a>PLT-08 | Documentation snippets compile and run; site links are checked | Candidate only | This site | `eng/verify-docs.ps1` | — | Generated reference is local output only; publishing the site is a separate authorized release action. |

<a id="unverified"></a>
## Not verified (no claim)

* Windows and linux-x64 execution of any row, and hosted GitHub Actions runs of the configured lanes.
* Native AOT on linux-x64, linux-arm64, linux-musl, Windows, osx-x64, and runtimes where
  `RuntimeFeature.IsDynamicCodeSupported` is false for reasons other than ILCompiler Native AOT (for example Mono
  full AOT on mobile).
* Any untested SDK patch, Roslyn version or C# language version below 12.
* Projection with providers or EF versions other than those listed in [Projection providers](projection-providers.md).
* Redis topologies beyond the single standalone instance used by the tests (replication, failover, cluster).
* Performance relative to other libraries or to earlier Meridian versions.

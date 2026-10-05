# Meridian evolution and compatibility design

Date: 2026-10-02. Status: proposed design for review; implementation has not started.

## Intent and success

The requested outcome is a complete, practical evolution plan for Meridian that covers missing capabilities while protecting previous features. Meridian remains two focused, independently usable .NET libraries, with optional integrations. Success means that every advertised capability has a repeatable acceptance check, known failures are resolved before expansion, and each released package has consumer-level evidence. A green unit suite alone is insufficient. This design establishes a finite implementation scope and a separate extension catalogue; it does not promise that all possible software features can be built or that tests mathematically prove perfection.

Assumptions: compatibility takes precedence over maximum feature count; .NET 8/9/10 support remains; existing runtime configuration and generated method names remain usable; new backends are optional. The user explicitly permits deprecating features that are being updated. This authorizes a documented migration lifecycle, not immediate removal or silent changes to existing behavior. The user requested planning, so these documents are reviewable proposals, not authorization to implement, publish, migrate a database, or operate external services.

## Evidence baseline

Reviewed checkout: the original Meridian working copy (local path omitted), branch fix/audit-2026-08, HEAD b65a7222e5c55bb1eddf47f2aab34303b28ee823, plus pre-existing tracked modifications and untracked source/docs. This working tree, rather than HEAD alone, is the reviewed baseline.

| Evidence from this conversation | Result | Limit |
| --- | --- | --- |
| Release solution build | Success; 477 warnings, zero errors | Warnings remain; not a release gate by itself |
| Mediator tests, .NET 8 and .NET 10 | 152 passed on each runtime | Both compiled using installed SDK 10; native SDK 8/9 consumer compatibility remains unverified |
| Mapping tests, .NET 8 and .NET 10 | 194 passed on each runtime | No real EF Core database provider in current projection tests |
| Seven .NET 10 console samples | QuickStart completes; other six crash | Shared failure: raw invalidation keys rejected by new composed-key behavior |
| Generated init-property probe | CS8852 compilation failure | Isolated probe, not yet a permanent regression test |
| Generated inherited-property probe | Id=42 becomes Id=0 | Type conversions are also skipped, but that is a documented MVP limitation |
| Nightly-style FsCheck setting | Round-trip property reports 100 cases with FsCheck_MaxTest=10000 | One property directly verified; other attributes use explicit smaller limits |
| Code graph | Fresh generation 2026-10-02T10:53:55Z; 56 test-class matches, pagination complete | Best-effort indexing; generated output and build directories excluded |
| NuGet package version indexes | [Mapping index](https://api.nuget.org/v3-flatcontainer/meridian.mapping/index.json) and [Mediator index](https://api.nuget.org/v3-flatcontainer/meridian.mediator/index.json) both list 2.1.3 as latest stable on 2026-10-02 | Package content/consumer parity, cross-OS CI and Native AOT remain unverified |

Primary source anchors: src/Meridian.Mediator/Behaviors/CachingBehavior.cs, src/Meridian.Mapping.Generators/MeridianMapperGenerator.cs, .github/workflows/ci.yml, .github/workflows/fuzz.yml, docs/compatibility-policy.md, and benchmarks/Meridian.Benchmarks/README.md. Local review logs under /tmp/meridian-review-* are temporary evidence; A1 establishes durable candidate evidence before changes.

## Approach decision

1. Recommended: staged, additive releases with mandatory compatibility and consumer gates. This permits focused reviews, safe rollback, and independent delivery of integrations.
2. A single broad rewrite offers interface consistency but combines too many sources of behavioral change and makes regressions difficult to isolate.
3. Reliability-only work repairs adoption failures quickly but does not fulfill the requested feature expansion.

Choose approach 1. Separate subsystem designs and implementation plans prevent one integration or optimization from blocking unrelated stable improvements.

## Global constraints

- Keep existing APIs operational during documented deprecation windows; use additive replacements and reserve removals/default flips for a major release.
- Preserve runtime mapping defaults: DefaultMaxDepth=64, DefaultMaxCollectionItems=10000, AllowNullCollections=false, AllowNullDestinationValues=true, AllowUndefinedEnumValues=false.
- Preserve telemetry defaults: RecordExceptionMessage=false and RecordExceptionStackTrace=false; default notification publication remains sequential and concurrent publisher defaults remain capped at 16.
- Preserve existing generated method names and legacy assignment semantics; structural generation requires explicit opt-in.
- Core libraries continue targeting net8.0;net9.0;net10.0; generators target netstandard2.0; net11.0 preview coverage remains optional and non-blocking.
- Do not introduce FluentValidation, EF Core, Redis, ASP.NET Core, or telemetry-exporter dependencies into either core package.
- Preserve the reviewed dirty working tree; never reset, clean, stash, stage unrelated changes, or assume a worktree from HEAD includes the baseline.
- Publishing, tags, releases, external database changes, and new recurring monitors require explicit user authorization.
- Graph-Loop and Sol Advisor remain disabled; no workflow, custom agent, hook, or completion gate from either framework may be activated.

Additional invariants: existing opt-outs remain available; map-to-existing identity and reference preservation remain correct; cancellation is propagated; error handling cannot erase original failures; user/tenant data, payloads, raw cache keys, and exception detail do not become default telemetry tags. No minor release removes framework targets or changes safe defaults. Compiler-floor changes must be documented and checked with consumer builds before choosing the release version.

## Workstream architecture

| ID | Deliverable | Boundaries | Design | Plan |
| --- | --- | --- | --- | --- |
| A | Reliability and backward compatibility | Core fixes, regression evidence, sample execution, consumer validation | [A design](2026-10-02-reliability-design.md) | [A plan](../plans/2026-10-02-reliability.md) |
| B | Generated and runtime mapping evolution | Mapping-only generation, registration, projection and fast-path optimization | [B design](2026-10-02-mapping-design.md) | [B plan](../plans/2026-10-02-mapping.md) |
| C | Practical integrations | Optional validation, cache, idempotency, transactions, and HTTP packages | [C design](2026-10-02-integrations-design.md) | [C plan](../plans/2026-10-02-integrations.md) |
| D | Mediator AOT and diagnostics | Generated closed dispatch, explicit registry composition, bounded observability | [D design](2026-10-02-mediator-design.md) | [D plan](../plans/2026-10-02-mediator.md) |
| E | Documentation and distribution | API docs, benchmarks, package manifest, supply-chain and release gates | [E design](2026-10-02-distribution-design.md) | [E plan](../plans/2026-10-02-distribution.md) |

Dependencies: A completes before expansion. B and C may progress independently afterward; D starts after A's consumer and scope tests. E's compatibility manifest and documentation inventory can be prepared early, but final claims and publication depend on accepted B/C/D evidence. Only optional isolated test services may run in parallel; no parallel edits to shared core files without explicit ownership.

## Existing-feature protection

The [regression matrix](../plans/2026-10-02-regression-matrix.md) is the acceptance contract for every workstream. It records each feature, current test owners, necessary boundary cases, and additional proof. Preserve all existing tests; changes to assertions require a documented behavior correction and review, not a lower bar to make a new implementation pass.

Use three complementary baselines:

1. The current working-tree suite protects current features, including uncommitted hardening.
2. Published-package consumers and baseline API validation protect actual consumers of the previous released version. Discover and pin the real release; do not assume local version 2.1.3 proves what is published.
3. Differential mapping fixtures compare interpreter, compiled, and generated paths for the capability intersection, including failure type and reference/collection identity. Unsupported features must produce diagnostics or retain the documented fallback.

A candidate can claim a feature only when its row is accepted. Incomplete infrastructure is recorded as unverified and blocks the corresponding claim; skipped integration tests do not count as passes.

## Release acceptance gates

- Build and execute all existing tests on Windows, Linux, and macOS for net8.0/net9.0/net10.0. Install the corresponding runtimes explicitly. Preview SDK jobs do not select the SDK for required jobs.
- Run all seven existing samples on .NET 10 after building. Run the six multi-target samples additionally on .NET 8/9. QuickStart remains net10.0 unless separately designed to multi-target.
- Build fresh consumers from packed NuGet artifacts, including generator analyzer loading on the minimum supported compiler and generated output under the promised C# language floor. Runtime support and SDK support are separate assertions.
- Compare public API with the pinned previous stable NuGet package; record all intentional security hardening and migration consequences.
- Execute EF Core SQL projection tests and Redis claim tests against real, isolated test services where applicable.
- Publish and execute a trimmed/Native AOT consumer before making a corresponding support claim. Direct generated mapping and whole-application support are separate gates.
- Run malformed/deep/wide mapping tests and deterministic concurrency/cancellation tests. Verify claimed property-test counts, rather than only process exit codes.
- Collect coverage. New/modified core hot paths target at least 90% line and 80% branch coverage, with named boundary cases; establish an honest current baseline first. Existing repository-wide coverage may ratchet upward without creating undocumented exclusions.
- Benchmark on dedicated hardware. For modified paths, investigate more than 10% time regression or any unexplained allocation increase; do not gate correctness on shared-host nanoseconds.
- Consume the exact packed artifact, review dependency licenses/advisories, inspect analyzer placement, and attach SBOM/provenance to the authorized release.

## Feature catalogue and deliberate boundaries

### Deprecation lifecycle

Deprecation is allowed when a replacement is implemented, packaged, documented, and migration-tested. Candidates: AutoMapper-named DI aliases, non-atomic idempotency fallback for concurrent workloads, raw-key compatibility paths, and generated mapping's silent-skip legacy mode. Explicit type-pair behavior registration remains supported for dynamic integrations. Core runtime mapping, resolvers/converters, reverse maps, reference preservation, streaming, and meaningful security opt-outs remain supported; a newer path alone is insufficient reason to retire them.

Use four stages: document the preferred replacement; emit an actionable informational analyzer diagnostic; introduce a non-error Obsolete warning in an announced minor release; consider removal or a default flip in a reviewed major release. Preserve the deprecated path for at least two minor releases and six months after a stable replacement ships, whichever is later. Reassess adoption before removal. Warning-as-error consumer builds must have a tested temporary suppression path and a working migration. Non-atomic idempotency can be deprecated in documentation/registration diagnostics without marking its shared base interface obsolete and thereby breaking the atomic contract that inherits it.

An obsolete member has a diagnostic ID, replacement API, migration example, introduction version, earliest removal boundary, and rationale in docs/deprecations.md. Retirement of a target framework follows a separate published support policy; a framework's EOL does not silently remove its package assets. No deprecation is marked complete until old and new consumer fixtures both behave correctly during the transition. E7 owns this lifecycle, with A4/B/C tasks providing the replacements and migration proof.

Committed scope: all A-E requirements, including generated nested/collection/conversion/immutable mapping, explicit generated runtime integration, real provider projections, FluentValidation, memory/distributed cache, Redis leased idempotency, EF Core transactions, ASP.NET Core sample and error integration, generated mediator dispatch, build diagnostics, bounded metrics, nested fast-path optimization, API documentation, fair benchmarks, compatibility automation, and maintainable packaging.

Additional documented extensions, after measured demand and a separate design: opt-in request timeouts/circuit breakers, generated subtype dispatch, immutable collection support, additional SQL/cache providers, incremental-generator IDE tooling, richer configuration visualization, and durable outbox interoperability examples. A-E already include tenant/user key scope, cache miss coalescing, migration diagnostics and static mapping methods. More complex policies remain optional. These are not permission to start unrelated product work.

Durable outbox/broker examples may be documented for interoperability after transaction integration, but Meridian does not promise exactly-once external side effects, durable message transport, event sourcing, sagas, or full AutoMapper parity. A durable transport library, removal of legacy shims, changed runtime defaults, or mandatory ecosystem dependencies requires a separate product decision and, where appropriate, a major release.

### Follow-on feature priorities

These are candidates with explicit reasons and prerequisites, rather than extra unbounded commitments in the 34-task plan.

| Candidate | Value | Prerequisite / acceptance | Priority |
| --- | --- | --- | --- |
| Optional request timeout behavior | Predictable request budgets | Cancellation/disposal tests; distinguish timeout from caller cancellation; no forced replay | Next, after A8 |
| Circuit-breaker/Polly integration | Resilience for transient providers | Optional package; no overlap/double retries; classified failures and concurrency proof | Demand-driven, after C |
| Generated polymorphic/subtype dispatch | Inheritance-heavy DTOs without reflection | Explicit subtype list, ambiguity diagnostics, identity/cap parity and AOT run | Next, after B/D |
| Immutable collections | Immutable model ecosystems | Explicit construction APIs, duplicate-key policy, cap/aliasing tests | Next, after B5 |
| Mapping plan visualizer | Explain profile precedence and fallback | Read-only configuration export, no service/payload capture, stable schema | Next, after E1 |
| IDE diagnostics/code fixes | Faster migration and configuration repair | Stable analyzer IDs; fix preview; compile old/new consumer fixtures | Next, after E7 |
| Additional SQL providers | Broader projection adoption | Real provider/version fixtures, SQL inspection and translation support table | Demand-driven, after B7 |
| Additional cache/idempotency providers | Backend choice | Same serializer/scope/claim conformance suite; state durability honestly documented | Demand-driven, after C4 |
| OpenTelemetry exporter examples | Integrate existing dashboards | Optional example, bounded/privacy-tested tags; no exporter dependency in core | Next, after D5 |
| Generic handler/AOT expansion | Plugin and open-generic applications | Explicit closed roots, constraint diagnostics, real native consumer proof | Demand-driven, after D4 |
| Outbox/broker interoperability example | Coordinated DB/event publication | Durable external library, transaction-bound writes, crash/replay example | Separate design, after C5 |
| Async mapping for external enrichment | Applications that explicitly need I/O | Separate API; cancellation/scoped services; do not turn pure mapping into hidden I/O | Low until demand |
| Native AOT HTTP sample | End-to-end deployment proof | AOT-compatible web/auth/serializer/provider subset; binary HTTP smoke tests | Next, after C6/D4 |
| Configuration startup diagnostics dashboard | Operational support | Reuse existing AssertHandlerRegistration; redact data; no background monitoring | Demand-driven, after D5 |

Do not add durable transport, schedulers, event sourcing or sagas to the core as speculative features. Consumer demand and measured maintenance cost determine which follow-on design is accepted.

## Technical references

- Async validator integration calls ValidateAsync so asynchronous rules are supported: [FluentValidation](https://docs.fluentvalidation.net/en/latest/async.html).
- EF Core distinguishes SQL translation from top-level client evaluation; tests must inspect generated SQL as well as results: [Microsoft](https://learn.microsoft.com/en-us/ef/core/querying/client-eval).
- EF transactions interact with savepoints and execution strategies; the adapter defines ownership explicitly: [Microsoft](https://learn.microsoft.com/en-us/ef/core/saving/transactions).
- Redis claim completion/release must validate ownership; TTL alone is insufficient, and Redis failover does not establish exactly-once side effects: [Redis](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/).
- Generated registration alone is insufficient evidence of full Native AOT compatibility: [Microsoft](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).
- Stable package API comparison needs a baseline package version: [Microsoft](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/baseline-version-validator).

## Planning checklist

- [x] Reflect intent, assumptions, and compatibility constraints.
- [x] Review repository state, current evidence, and framework boundaries.
- [x] Compare approaches and select staged additive development.
- [x] Decompose independent subsystems into their own designs and plans.
- [x] Complete and self-review every linked document and the regression matrix.
- [ ] User reviews the written proposal and selects execution method.
- [ ] Implement and verify workstreams task by task.

Document validation: 13 documents, 34 unique tasks, 41 legacy rows, 10 new rows and 24 local links checked successfully. All 49 existing test-owner paths were verified on disk and checked against fresh graph coverage; no recorded indexing gaps, with the usual best-effort limitation. Self-review corrected manifest/script ownership, generated-option defaults and multi-pair models, registry constructor/validator contracts, conservative leased-failure handling and deprecation scope. This certifies plan consistency, not implementation behavior.

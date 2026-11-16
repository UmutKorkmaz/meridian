# Meridian existing-feature regression and release matrix

Date: 2026-10-02. This is an acceptance contract, not a claim that every row already has complete proof.

Current evidence: 194 mapping and 152 mediator tests pass on both .NET 8 and .NET 10 using SDK 10. .NET 9, native SDK 8/9 package builds, Linux/Windows, real SQL/Redis integration, and Native AOT execution are unverified. QuickStart passes; the other six samples fail on composed-key validation. No implementation task is marked complete by this document.

## Required legacy behavior

Test files below are relative to tests/Meridian.Mapping.Tests (mapping) or tests/Meridian.Mediator.Tests (mediator). Existing fixtures are retained. A named additional test is added only where current coverage does not already prove the specified behavior.

| ID | Feature and preserved contract | Current test owners | Required boundary proof | Owner |
| --- | --- | --- | --- | --- |
| M01 | Basic Map overloads and null/default behavior | BasicMappingTests, EdgeCaseTests | Generic/object/runtime-type overloads agree; nullable-to-value default preserved | A6 |
| M02 | Profiles, assembly discovery, configuration lifetime | ProfileTests, DependencyInjectionTests, CoverageFeatureTests | Multiple profile assemblies, scoped resolvers, immutable configuration under parallel mapping | A6 |
| M03 | ForMember, MapFrom, Ignore, null substitution, conditions | ForMemberTests, HighPriorityFeaturesTests | Null source chains, ignored destination state, precondition versus condition order | A6/B6 |
| M04 | Flattening, naming conventions, IncludeMembers, ForPath | FlatteningTests, HighPriorityFeaturesTests, AutoMapperCompatibilityTests | Deep null chains, overlapping included members, destination path creation | A6 |
| M05 | ReverseMap and unflattening | ReverseMapTests, EnhancedValidationTests | Rename/path reversal and explicit overrides; no unsupported expression reversal claim | A6 |
| M06 | Constructors, ForCtorParam, ConstructUsing | ConstructorMappingTests, ConstructorSelectionTests, EnhancedValidationTests | Required and optional arguments; missing required arguments; no silent widest-constructor default fill | A6/B3 |
| M07 | Type/member converters and DI resolvers | TypeConverterTests, ValueResolverTests, AutoMapperCompatibilityTests | Instance versus DI paths, nested services, unchanged exception propagation | A6 |
| M08 | Before/After map actions, operation Items/State | EnhancedFeatureTests, AutoMapperCompatibilityTests | Action order, nested option propagation, scope isolation, map-to-existing | A6/B6 |
| M09 | Arrays/lists/interfaces/dictionaries/sets and collection null policy | CollectionMappingTests, EnhancedFeatureTests, ConversionConsistencyTests | Source streaming, explicit collection maps, duplicate dictionary keys retain last-write-wins | A6/B5 |
| M10 | Map onto existing and UseDestinationValue | HighPriorityFeaturesTests, MapToExistingDosTests, AutoMapperCompatibilityTests | Destination collection identity; cap/depth cannot be bypassed | A6/B6 |
| M11 | Include/IncludeBase, polymorphism, open generic maps | CoverageFeatureTests, EnhancedFeatureTests, EnhancedValidationTests | Exact derived dispatch, constraints, invalid generic closure; no unbounded type discovery | A6/B6 |
| M12 | PreserveReferences and cycles | EnhancedFeatureTests, SafetyDefaultsTests | Equal-by-value objects remain distinct; diamonds preserve sharing; cycles bounded | A6/B5/B8 |
| M13 | Scalar/nullable/enum conversions | ConversionConsistencyTests, EdgeCaseTests | Undefined enum rejected by default; opt-out honored; overflow/culture parity between paths | A6/B4 |
| M14 | Configuration validation and mapping plan diagnostics | ValidationTests, EnhancedValidationTests, MappingPlanTests, FastPathDiagnosticTests | Unmapped source/destination, nested configuration faults, explain fast-path rejection | A6/B6 |
| M15 | Queryable ProjectTo, explicit expansion | EnhancedFeatureTests, AutoMapperCompatibilityTests | Preserve expression behavior; separately prove SQL projection/select columns with provider | A6/B7 |
| M16 | Value transformers and Turkish culture helpers | EnhancedFeatureTests, TurkishCultureTests | Transformer ordering, null handling, dotted/dotless I regression cases | A6/B4 |
| M17 | Safety defaults and limit exceptions | SafetyDefaultsTests, DosRegressionTests, MapToExistingDosTests, PropertyBasedTests | Depth 63/64/65; items 9999/10000/10001; streaming stops by 10001; default depth truncates | A6/B5/B8 |
| M18 | Compiled fast path and fallback interpreter | FastPathDiagnosticTests, ConversionConsistencyTests | Same values, nulls, failures, identity and limits for common accepted shapes | A6/B6/B8 |
| M19 | Generated matching-property mapping and null input | SourceGeneratorTests | Existing method names, nested containers, same-name classes, multiple pairs remain compatible | A3/B1/B2 |
| M20 | Generated modern/inherited members | Current isolated probes fail | Init/required/record constructor builds; inherited Id=42 remains 42; accessor hiding policy | A3/B3 |
| Q01 | Typed requests, void commands, object Send | RequestResponseTests, EdgeCaseTests | Both void handler forms; cancellation; missing/duplicate handler diagnosis | A7/D2 |
| Q02 | Dependency injection and handler discovery | DependencyInjectionTests, HandlerValidationTests, AdditionalCoverageTests | Repeated registration, multiple apps/providers, scopes, closed versus open generics | A7/D1/D2 |
| Q03 | Pipeline order and deduplication | BehaviorOrderingTests, BehaviorRegistrationCompatibilityTests, MediatorHardeningTests | Explicit/scanned and open/closed mixed ordering; repeat configuration; stream order | A7/D2/D3 |
| Q04 | Pre/post processors and request exception handlers/actions | PrePostProcessorTests, ExceptionHandlingTests | Exact order, handled response, original exception identity, failure within processor | A7/D2 |
| Q05 | Notifications and publisher strategies | NotificationTests, PublishingStrategyTests, PublishingConcurrencyTests | Sequential stop-on-error; all concurrent failures visible; max active handlers <=16 by default | A7/D3 |
| Q06 | Streams and stream behaviors | StreamingTests, TracingTests | Cancellation before/after first yield; early disposal; async failure; scoped handler lives through enumeration | A7/D3 |
| Q07 | Validation and localization | BehaviorTests, LocalizedValidationBehaviorTests, StandardPipelineValidatorScanTests | All validators run as documented; missing resource falls back; async cancellation | A8/C1 |
| Q08 | Authorization | BehaviorTests, MediatorHardeningTests | Missing required authorization handler fails closed; explicit legacy opt-out preserved | A8/C6 |
| Q09 | Caching and invalidation | BehaviorTests, MediatorHardeningTests | Composed query keys, raw opt-out, cross-query isolation, expiry, destination response type | A2/A8/C2/C3 |
| Q10 | Idempotency, legacy and atomic stores | BehaviorTests, MediatorHardeningTests | Same-key concurrency, completed-null atomic replay, claim release on error, documented legacy limitations | A8/C4 |
| Q11 | Retry behavior | RetryBehaviorHardeningTests, BehaviorTests | Cancellation never retried; nontransient exceptions excluded; retries <=10 by default; backoff <=5min | A8/C5 |
| Q12 | Transactions | BehaviorTests | Begin/commit/rollback/dispose order; existing transaction ownership; both handler and cleanup failures retained | A8/C5 |
| Q13 | Audit and correlation | AuditBehaviorTests, StandardPipelineExtensionsTests | Success/failure/validation recorded once, correlation restored, sink+handler failure aggregate | A8/D5 |
| Q14 | Logging, Activity traces and privacy | TelemetryPrivacyTests, TracingTests, BehaviorTests | Exception messages/stacks absent by default, explicit opt-in preserved, cancellation/disposal marks spans correctly | A8/D5 |
| Q15 | Standard pipeline | StandardPipelineExtensionsTests, StandardPipelineValidatorScanTests | Correlation(-100), audit(-50), localized validation(0); custom sink wins; validator executes once | A7/A8 |
| P01 | Public API and compatibility shims | Public API baseline not currently pinned | Old compiled consumer plus source consumer; AddAutoMapper aliases; custom interface implementations | A4/E2 |
| P02 | Analyzer packaging and supported compiler floor | SourceGeneratorTests plus project references | Actual NuGet analyzer loads on promised SDK; runtime consumer independent of generator | A4/B1/E2 |
| P03 | Console samples | QuickStart passes; other six fail | Every sample executes assertions and exits zero; verify side effects in in-memory fixtures | A2/E4 |
| P04 | Property/fuzz coverage claims | PropertyBasedTests; nightly setting ineffective in probe | Actual logged case counts; replayable seeds; genuine AFL evidence labeled separately | A5/E4 |
| P05 | Multi-OS/runtime support | .NET 8/10 local checks only | Windows/Linux/macOS x net8/net9/net10 required; preview lane non-blocking | A1/E4 |
| P06 | Supply chain and package release | Workflow configuration exists; live settings unverified | Exact version manifest, package hashes, API baseline, local-feed consumer, SBOM/provenance | E2/E5 |

## New-capability acceptance

| ID | New capability | Required proof | Owner |
| --- | --- | --- | --- |
| N01 | Structural generated nested/collection/conversion maps | Generated output compiles; matches runtime capability intersection including caps and nulls | B3-B5 |
| N02 | Generated resolver integration | Existing explicit profiles win; create-new eligible pairs use resolver; map-to-existing/actions retain fallback | B6 |
| N03 | SQL projection | SQLite execution plus PostgreSQL CI lane; SQL contains expected selected fields and translations | B7 |
| N04 | Optional validation/cache/HTTP adapters | Cancellation, DI lifetime, scoped tenant separation, typed ProblemDetails; legacy suite unchanged | C1-C3/C6 |
| N05 | Distributed leased idempotency | Two clients, stale-owner denial, lease expiry, completed-null replay, explicit uncertain-outcome handling | C4 |
| N06 | EF transaction adapter | Real relational transaction rollback/savepoint, ambient ownership, execution-strategy composition | C5 |
| N07 | Generated mediator dispatch | Request/notification/stream parity, registry composition, closed scoped dependencies | D1-D3 |
| N08 | Native AOT | Publish and execute packaged consumer binary; no unexpected IL/trim/analyzer warnings in claimed paths | D4/B6/E4 |
| N09 | Diagnostics and metrics | Stable diagnostic IDs; bounded tags; no secrets/payload/raw key; no-listener allocation measured | B2/D1/D5 |
| N10 | Nested runtime optimization | Differential identity/limit/error tests plus same-host benchmark versus baseline | B8/E3 |

## Recording acceptance

An evidence record contains row ID, candidate hash, source-manifest hash if dirty, command, SDK/runtime, OS/architecture, passed/failed/skipped counts, artifact hashes, reviewer result, and limitations. All affected legacy rows must pass for a new row to be accepted. A fixture or in-memory test cannot substitute for N03/N05/N06/N08. Disabled infrastructure is marked unverified and blocks that support claim.

Completion requires every applicable legacy row accepted, every advertised new row accepted, no unexplained regression, and no undocumented API/default change. This is the operational meaning of preserving previous features; it is not an unsupported guarantee that no defect can ever exist.

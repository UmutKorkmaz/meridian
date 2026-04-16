# Changelog

All notable changes to Meridian are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.1.1] - 2026-04-16

### Security
- **[HIGH]** `IMapper.Map(source, destination)` and members configured with `.UseDestinationValue()` now enforce `DefaultMaxCollectionItems` on both the fast-count and streaming-enumeration paths. `MapCollection` and `TryMapCollectionOntoExisting` now share the same guard logic. (`GHSA-XXXX-XXXX-XXXX`)
- **[HIGH]** Collection-item recursion now increments depth before nested mapping on both the new-destination and existing-destination paths, so self-referential collection graphs stop at the configured depth cap instead of reaching stack overflow first. (`GHSA-XXXX-XXXX-XXXX`)
- **[MEDIUM]** `ObjectCreator.CreateWithConstructorMapping` no longer selects the widest constructor when required parameters cannot be resolved. Silent `default(T)` fills now fall back to a narrower constructor or default construction. (`GHSA-XXXX-XXXX-XXXX`)
- **[MEDIUM]** `Mediator` no longer records `exception.stacktrace` by default. Full stack traces are now opt-in through `MediatorTelemetryOptions.RecordExceptionStackTrace`. (`GHSA-XXXX-XXXX-XXXX`)
- **[MEDIUM]** `RetryBehavior` now clamps retry counts, saturates exponential backoff at five minutes, refuses to retry cancellation, and applies a conservative transient-exception filter by default. (`GHSA-XXXX-XXXX-XXXX`)
- **[MEDIUM]** `TaskWhenAllPublisher` and `ResilientTaskWhenAllPublisher` now default to `maxDegreeOfParallelism = 16`; pass `-1` to restore the legacy unbounded fan-out. (`GHSA-XXXX-XXXX-XXXX`)
- **[LOW]** Public mapping exceptions now avoid leaking namespace-qualified type names and no longer concatenate inner exception messages into top-level property-mapping errors. (`GHSA-XXXX-XXXX-XXXX`)
- **[LOW]** Dictionary materialization now uses last-write-wins indexer semantics for duplicate keys instead of throwing an exception that echoes the offending key text. (`GHSA-XXXX-XXXX-XXXX`)
- **[LOW]** Documented the process-lifetime mediator cache growth risk for applications that allow attacker-controlled runtime type materialization to reach `Send`, `Publish`, or `CreateStream`. This remains a consumer-side hardening requirement. (`GHSA-XXXX-XXXX-XXXX`)

### Changed
- Widest-constructor selection now requires every parameter to be resolved by explicit ctor mapping, source-name matching, or a C# optional default value. Consumers relying on silent `default(T)` fills must add `.ForCtorParam(...)` mappings or expose a narrower constructor.
- `Mediator` no longer emits `exception.stacktrace` by default. Opt back in with `new MediatorTelemetryOptions { RecordExceptionStackTrace = true }`.
- `TaskWhenAllPublisher` and `ResilientTaskWhenAllPublisher` now cap notification fan-out at `16` by default. Pass `-1` to either constructor to retain the legacy unbounded behavior.
- Retry caps now default to `RetryPolicy.MaxRetriesCap = 10` and `RetryPolicy.MaxBackoff = TimeSpan.FromMinutes(5)`.
- Duplicate dictionary keys now merge with last-write-wins semantics instead of throwing.

### Fixed
- `MapCollection` and `TryMapCollectionOntoExisting` now share a single collection-limit enforcement path, reducing the chance of future drift between sibling enumeration implementations.

### Added
- `LICENSE`, `SECURITY.md`, `CHANGELOG.md`, `CONTRIBUTING.md`, `CODEOWNERS`,
  PR + issue templates at repository root / `.github/`.
- `MapperConfigurationExpression.DefaultMaxDepth` (global recursion cap,
  default `64`) and `DefaultMaxCollectionItems` (default `10_000`).
- `MeridianMappingLimitException` raised when the collection cap is exceeded.
- Reproducible-build metadata on NuGet packages
  (`ContinuousIntegrationBuild`, `EmbedUntrackedSources`, symbol packages).
- `PackageValidation` enabled on both libraries for public-API diff tracking.
- CI pipeline (`.github/workflows/ci.yml`): matrix build + test across
  ubuntu, windows, macos on every PR; pack + artifact upload.
- CodeQL security analysis workflow (C#, `security-and-quality` query set).
- OpenSSF Scorecard workflow publishing SARIF to the Security tab.
- Release workflow triggered by `vX.Y.Z` tags: deterministic build,
  Sigstore build-provenance attestation via `actions/attest-build-provenance`,
  CycloneDX SBOMs, signed GitHub Release, push to nuget.org.
- Dependabot configuration for NuGet + GitHub Actions weekly updates,
  grouped by logical dependency family.
- `benchmarks/Meridian.Benchmarks` project (BenchmarkDotNet) tracking
  Meridian.Mapping and Meridian.Mediator performance over time, with
  weekly CI runs + `workflow_dispatch`.
- Internal `DelegateCompiler` (typed `Func<>` wrappers via `Expression.Call`
  on delegate `Invoke` method) and `MethodLookupCache` (cached `Convert`/
  `Resolve` method resolution on converter/resolver types).

### Changed
- Eliminated `Delegate.DynamicInvoke` from every hot-path call in
  `Meridian.Mapping.Execution` and `Meridian.Mapping.Configuration`
  (7 sites): `MappingEngine.MapProperties` (5), `ObjectCreator`
  (ctor-param mapping), `ValueTransformerCollection.Apply`. Wrappers are
  cached per delegate instance.
- Cached reflection-based method lookups (`GetMethod("Convert")`,
  `GetMethod("Resolve")`) so each converter/resolver type is resolved at
  most once over the lifetime of the process.

### Performance
- **Meridian.Mediator.Send is 59 ns** on a no-behavior request/handler
  dispatch with equal-allocation steady-state runs.
- **Meridian.Mapping is 2.3× faster than before** thanks to the new
  `FastPathCompiler` that emits a single compiled delegate per TypeMap
  at configuration time (244 ns → 106 ns on a 10-scalar-property +
  nested-object map). The residual gap is one `MappingEngine.Map` dispatch and one
  `ResolutionContext.IncrementDepth` allocation per nested object —
  tracked as P3.7 (inline nested fast paths). See
  `benchmarks/Meridian.Benchmarks/README.md` for full numbers.

### Added (continued)
- `Meridian.Mapping.Execution.FastPathCompiler` — emits a single
  `Func<object, MappingEngine, ResolutionContext, object>` per TypeMap
  for the simple `ForMember + MapFrom` subset used by the common
  profile-based mapping path.
  Falls back to the interpreter for TypeMaps using resolvers, converters,
  conditions, custom ctors, before/after-map actions, inheritance,
  polymorphic dispatch, or PreserveReferences. Exposes
  `DescribeRejection` (internal) for diagnosing why a given map is not
  fast-pathed.
- `TypeMap.CompiledFastPath` property — populated by the fast-path
  compiler during `MapperConfiguration` construction. When non-null,
  `MappingEngine.MapWithTypeMap` prefers it over the per-property
  interpreter.
- `[InternalsVisibleTo]` for `Meridian.Mapping.Tests`,
  `Meridian.Mediator.Tests`, and `Meridian.Benchmarks` so test and
  benchmark projects can exercise diagnostic helpers without widening
  the public API surface.
- `tests/Meridian.Mapping.Tests/DosRegressionTests.cs` — explicit
  attacker-shaped input tests: 100 k-deep graph, 1 M-item collection
  (via `ICollection<T>` fast-path), streaming 10 M-item pull stops at
  10 001, 1 M small maps complete within a budget.
- `tests/Meridian.Mapping.Tests/PropertyBasedTests.cs` — FsCheck
  property-based tests: round-trip equality on scalars, arbitrary
  string payloads never leak undocumented exceptions, bounded/oversized
  collections behave correctly, arbitrary depth does not
  StackOverflow. 100 iterations per case by default; 10 000 nightly
  via the fuzz workflow.
- `tests/Meridian.Mapping.Fuzz` — SharpFuzz harness for local
  coverage-guided fuzzing via AFL++. Seed corpus of 5 inputs covering
  flat, deep, wide, unicode, and all-zero shapes. CI integration
  deferred until a runner with AFL is provisioned — see project README.
- `.github/workflows/fuzz.yml` — nightly workflow running the FsCheck
  property tests at 10 000 iterations. Scaffolding for a self-hosted
  AFL job is in the workflow as commented-out YAML.
- `docs/security-model.md` — threat model, mitigations table, explicit
  non-goals. Linked from `SECURITY.md`.

### Changed
- `MappingEngine.MapCollection` now checks generic `ICollection<T>.Count`
  via a cached reflection lookup when the source does not implement the
  non-generic `System.Collections.ICollection`. Makes the collection
  fast-path reject oversized inputs on any typed collection, not just
  BCL types that happen to implement the legacy interface.

### Added (Phase 6 — recommended pipeline + culture support)
- `Meridian.Mediator.Behaviors.AuditBehavior<TRequest, TResponse>` —
  pipeline behaviour that records an `AuditEntry` for every dispatched
  request via a registered `IAuditSink`. Captures correlation ID,
  request type, wall-clock duration, and success / failure with the
  exception's type + message preserved. Default `LoggerAuditSink`
  emits structured `ILogger` messages; adopters with database / SIEM
  destinations register their own sink. Re-throws unchanged after the
  audit record is written; if both handler AND sink fail, both
  exceptions surface via `AggregateException`.
- `Meridian.Mapping.TurkishCulture` — opt-in helpers for the dotted /
  dotless I pair: `IgnoreCaseComparer`, `ToUpper`, `ToLower`, `Equals`.
  `MapperConfigurationExtensions.WithTurkishCulture()` is the
  discoverable entry point. Eight regression tests cover the five
  canonical Turkish-locale I/i bugs.
- `Meridian.Mediator.Behaviors.LocalizedValidationBehavior<TRequest, TResponse>` —
  validation behaviour that routes every error message through
  `IStringLocalizer<TRequest>` before throwing `ValidationException`.
  Resource keys without a matching .resx fall back to the raw message
  (matches `LocalizedString.ResourceNotFound` semantics) so adopters
  can introduce localisation incrementally.
- `Meridian.Mediator.Extensions.StandardPipelineExtensions.AddMeridianStandard(...)` —
  one-liner DI registration that wires the recommended pipeline
  composition: correlation ID (order -100) → audit (-50) → localised
  validation (0). Default `LoggerAuditSink` registered via `TryAdd`,
  so consumer-supplied sinks win.
- `MeridianMediatorConfiguration.AddAuditBehavior` and
  `AddLocalizedValidationBehavior` extension methods for hand-rolled
  pipeline composition.
- New deps on `Meridian.Mediator`:
  `Microsoft.Extensions.Logging.Abstractions` (default audit sink) and
  `Microsoft.Extensions.Localization.Abstractions` (localised
  validation). Both abstractions-only — no runtime overhead for
  consumers who do not opt into the new behaviours.

### Added (Phase 5 — source generator)
- `src/Meridian.Mapping.Generators` project — Roslyn
  `IIncrementalGenerator` targeting `netstandard2.0`. Shipped as an
  analyzer alongside `Meridian.Mapping` so consumers get it
  transitively from the NuGet package.
- `[GenerateMapper(typeof(TSrc), typeof(TDst))]` attribute, emitted
  into the consumer's compilation by the generator (no runtime
  reference needed). `AllowMultiple = true`, so one container class
  can host every source-gen mapper in a project.
- Generator emits a typed `public static TDst MapTo{TDst}(TSrc source)`
  method per attribute pair, with direct property-by-property
  assignment for same-name same-type members. Null source returns
  `default!`. Mismatched types are skipped with a `// Skipped:` comment
  in the emitted file, leaving the runtime IMapper to handle conversion.
- `tests/Meridian.Mapping.Tests/SourceGeneratorTests.cs` — 5 tests
  covering single-pair mapping, multi-pair containers, null handling,
  type-mismatch fallback, and a perf budget (1 M generated calls in
  under 1 second).
- `docs/source-gen-guide.md` — when to choose source-gen over runtime
  IMapper, AOT implications, MVP scope and known limitations.

### Changed
- **BREAKING (behavioral)**: maps that previously recursed deeper than 64
  levels without explicit `.MaxDepth()` now produce a depth-capped result
  (default `null` for reference types, `default(T)` for value types).
  Callers relying on unlimited recursion must opt in explicitly via
  `cfg.DefaultMaxDepth = int.MaxValue` (not recommended) or raise the
  per-map cap.
- **BREAKING (behavioral)**: collection mappings now throw
  `MeridianMappingLimitException` when the source collection has more than
  `DefaultMaxCollectionItems` items. Raise or disable the cap explicitly
  if this is expected.
- Target framework set aligned with published NuGet metadata:
  `net8.0;net9.0;net10.0`. `net11.0` dropped until GA.
- Package versions are declared explicitly in the project files, and the
  release workflow only packs packages whose declared version matches the
  pushed tag.

### Security
- `MaxDepth` default flip closes the recursion-driven stack-overflow
  class described in [CVE-2026-32933](https://nvd.nist.gov/vuln/detail/CVE-2026-32933)
  for any Meridian user who had not previously configured a per-map cap.

## [2.0.1] - 2026-03-23

### Fixed
- Initial NuGet listing follow-up release (package metadata corrections).

## [2.0.0] - 2026-03-23

### Added
- First public release of `Meridian.Mediator` (in-process CQRS mediator with
  pipeline behaviors, notifications, and streaming).
- First public release of `Meridian.Mapping` (profile-based object-to-object
  mapper with reverse maps, resolvers, converters, and queryable projection).

[Unreleased]: https://github.com/UmutKorkmaz/meridian/compare/v2.1.1...HEAD
[2.1.1]: https://github.com/UmutKorkmaz/meridian/compare/v2.0.1...v2.1.1
[2.0.1]: https://github.com/UmutKorkmaz/meridian/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/UmutKorkmaz/meridian/releases/tag/v2.0.0

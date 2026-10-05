# E — Documentation, distribution, and deprecation design

Status: proposed. Inherits portfolio constraints. Documentation and packaging follow each accepted feature; public claims depend on executable evidence.

## Documentation and capability inventory

Use DocFX for versioned API reference and authored guides. Keep the existing docs-site entry point usable while introducing generated reference pages; publication is a separately authorized action. Every README capability links to a working example, a regression row and its support boundary. Distinguish runtime mapping, direct generated mapping, generated runtime integration, runtime mediator and strict generated mediator. Show projection provider/version coverage, trimming/AOT configurations, compiler requirements and optional package dependencies.

Compile examples and migration snippets as consumers. Cover AutoMapper/MediatR migration, both generated modes, profile precedence, scoped handlers, all standard behaviors, transaction/retry/idempotency composition, personalized caching, custom providers, diagnostic IDs and troubleshooting. Preserve existing terminology and examples during migration. Address XML/API warnings incrementally from A1's recorded baseline rather than suppressing them globally.

## Package manifest and consumer proof

eng/packages.json is the single package inventory, with schemaVersion=1 and entries containing id, project, kind (runtime or analyzer), versionSource, baselineVersion, targetFrameworks, analyzerPaths and directPackageDependencies. versionSource is an MSBuild property name; it is never executable shell content. Each analyzerPaths entry contains project (the producing repository-relative csproj) and packagePath (the safe package-relative analyzer asset). Validate repository paths, evaluated producer outputs and archive entries separately. Core baselines are the verified published 2.1.3 packages; new package baselines remain null until their first stable release.

eng/pack-packages.ps1 accepts Configuration, OutputDirectory and CandidateVersion, validates the manifest against evaluated project properties, then packs and inspects actual nupkg metadata/assets. It never publishes. The core packages remain independent; optional packages list actual minimum backend/dependency versions. Mapping's bundled analyzer and the mediator's separate optional analyzer have explicit, tested packaging layouts. Adapter projects are included as they are introduced, rather than waiting for the final release batch. A4 creates the initial core-only manifest/scripts using this schema; E2 extends the same inventory and verification script. verify-packages.ps1 accepts Feed, CandidateVersion, Manifest=eng/packages.json and optional Framework/SdkVersion for isolated compiler-consumer lanes.

Version selection considers behavior, compiler floor, dependencies and default changes as well as public API differences. An existing NuGet version cannot be overwritten. An unpublished candidate uses a unique prerelease version. Consumer fixtures cover previous source APIs, custom interface implementations, warning-as-error migration, old compiler hosts and fresh local-feed installs. API validation uses PackageValidationBaselineVersion for stable runtime packages.

## Benchmarks and CI

Benchmark manual implementations, Meridian interpreter/compiled/generated paths, mapping alternatives, and mediator alternatives fairly. Use reproducible pinned versions, workload equality, configuration time, cold startup, hot dispatch, nesting, collections, reference preservation, scopes, pipelines and allocations. Include legal/license setup where a competitor requires it; omit and label unavailable runs. Historic benchmark numbers stay labeled historic until rerun. Dedicated-host comparisons investigate more than 10% mean time regression or unexplained allocation growth.

Required CI separates runtime execution from compiler-host tests. Legacy suites and console samples run across supported runtimes and Windows/Linux/macOS; actual compiler consumers run under the promised SDKs. Provider lanes provision isolated SQLite/PostgreSQL/Redis and fail when required services are absent. Native AOT claims require publishing and running the exact package consumer on a matching runner. Coverage ratchets from a measured baseline; changed core paths target 90% line/80% branch with meaningful boundary cases.

Collect test counts/skips, actual property-test counts, coverage, consumer logs, analyzer output, native execution, package hashes, dependencies, SBOM and provenance for the exact candidate. Configure required checks and independently verify repository settings; a workflow file alone is not proof of branch protection. Existing scheduled workflows may be repaired within repository scope; no new scheduled automation or background monitoring is created by this plan.

## Release and maintainability

Create a concrete release candidate locally first. After explicit authorization, follow the repository's reviewed tag/release/NuGet process using exactly the verified packages; attach source identity, hashes, SBOM, signatures/attestations where supported, and a capability/migration report. Promotion checks remote package existence before upload and verifies remotely retrieved artifacts afterward. A partially completed release is resumed from its recorded state rather than rebuilding or changing artifact contents.

Maintain contributor setup, support/SDK policy, vulnerability reporting, dependency updates, review ownership, known limitations and provider reproduction recipes. Do not claim two-maintainer or required-review protection from CODEOWNERS alone; report effective settings and availability separately. Framework retirement is a separately reviewed major-version/support-policy decision.

## Deprecation lifecycle and candidates

A deprecation entry contains a stable diagnostic ID, exact API or behavior, replacement, runnable migration, first stable replacement version/date, deprecation introduction version, earliest removal major/date, support status and compatibility evidence.

| Candidate | Replacement | Transition | Removal boundary |
| --- | --- | --- | --- |
| AddAutoMapper DI aliases | Existing AddMeridianMapping equivalents, with matching overloads verified | Documentation, informational diagnostic; non-error Obsolete only after consumer proof | Reviewed major after minimum support window |
| Silent skipped structural members in legacy generation | Opt-in Structural mode with explicit member/conversion/ignore rules | Keep Mode=Legacy default and method signatures; diagnostic is informational | Default flip only in a reviewed major |
| CachingBehaviorOptions.UseRequestTypeNamespacedKeys=false compatibility | CacheKeyComposer.Compose<TQuery> and provider-level scope decoration | Keep explicit compatibility opt-out working; warn in guidance/registration diagnostics | Removal only after supported key migration and major review |
| Non-atomic idempotency for concurrent workloads | IAtomicIdempotencyStore or the leased Redis adapter with documented uncertainty | Document/diagnose unsafe concurrency use; retain base IIdempotencyStore | No removal of the base interface inherited by richer stores |

AddClosedBehavior and AddClosedStreamBehavior remain supported for dynamic explicit-service registrations; the typed alternatives are preferred examples when equivalent. Do not label these useful public APIs obsolete simply because generated registration exists. Runtime scanning, converters/actions, reverse maps, open generics, custom providers and streaming remain supported.

Order: replacement is implemented, packaged, migration-tested and released stable; then preference/diagnostics; then announced non-error Obsolete where justified. Preserve compatibility for at least two minor releases AND six months after the stable replacement, whichever is later. Earliest removal is a reviewed major release after that window and adoption review, never automatic. Test both old and migrated consumers, including warnings-as-errors with documented diagnostic-specific suppression. Initial E7 can prepare the ledger and fixtures; warning/removal eligibility is evaluated only after real versions/dates exist.

## Tasks and acceptance

E1 API/user docs; E2 package inventory and consumers; E3 benchmark proof; E4 cumulative CI/review gates; E5 authorized candidate promotion; E6 maintainability; E7 deprecation ledger and migration tests. A-E feature work cannot bypass the relevant E2/E4 package gates. No task claims release completion from local packing alone.

# A — Reliability and compatibility design

Status: proposed. Requires the portfolio design and its Global Constraints.

## Outcome and baseline

Repair adoption failures before adding new behavior, then establish a repeatable contract for current and previous-package consumers. Preserve the dirty working-tree hardening as part of the baseline. Existing tests pass locally on .NET 8/10, but six samples fail and two source-generator probes fail. SDK 8/9 analyzer loading, .NET 9 runtime execution, cross-OS execution, and real provider integration remain unverified.

## Requirements

A1 captures tracked/untracked source hashes, build metadata, baseline failures, and the supported OS/runtime/SDK matrix. Required CI installs .NET 8, 9, and 10 runtimes explicitly; preview net11 samples use an isolated non-blocking job.

A2 updates every raw sample invalidation key using the actual cached query type and adds semantic sample assertions. CI executes samples, rather than relying on solution test to run console applications.

A3 corrects generator property discovery and immutable destination emission. Walk base members with most-derived-name precedence, escape identifiers, and exclude inaccessible/indexer/static members. Build a destination using an object initializer for writable/init members. Required members and constructor-only destinations receive an actionable diagnostic until B3 supports them; never emit code known to fail. Existing generated method names, null return behavior, shallow same-type assignment, nested-container support, and overloads remain usable.

A4 pins the real stable-package baseline, validates public API, and builds old/new packaged consumers on SDK 8/9/10. Roslyn compiler-host compatibility must be tested; targeting net8 does not prove analyzer loading in SDK 8. Lower generator API dependencies to the lowest proven compiler host where possible; a necessary compiler-floor increase follows explicit compatibility policy.

A5 makes FsCheck case counts real and visible. Scalar/depth properties can use 10000 nightly cases; expensive collection properties have explicitly documented budgets of 500 cases each. PR defaults remain 100 for cheap properties and current explicit limits for expensive ones. A custom FsCheck.Xunit property attribute reads MERIDIAN_FSCHECK_MAX_TEST and MERIDIAN_FSCHECK_COLLECTION_MAX_TEST, rejecting invalid positive counts; logs prove actual counts. AFL remains explicitly separate from property testing.

A6-A8 complete mapping, mediator, and behavior contract coverage in the regression matrix, adding only missing boundary tests. Deterministic barriers and fake clocks replace timing-based concurrency assertions.

## Interfaces and errors

GeneratorTestHarness.Compile(string source, LanguageVersion languageVersion=LanguageVersion.CSharp12) returns GeneratorCompilationResult containing CSharpCompilation Compilation, ImmutableArray<Diagnostic> Diagnostics, and string GeneratedSource. InvokeStatic<T>(GeneratorCompilationResult result, string typeName, string methodName) executes a fixture's static proof method. The harness is test-only.

No existing public interface gains an abstract member. Core fixes retain existing exception types and cancellation semantics. A handler failure plus audit/cleanup failure remains observable; compatibility-sensitive error tightening is explained with migration notes. Atomic idempotency and legacy non-atomic behavior remain separately tested and described.

## Acceptance

All seven .NET 10 samples and all applicable legacy matrix rows pass; native SDK consumer builds and package baseline validation pass; expensive nightly budgets match logs; current failures have durable reproductions. Changes are reviewed and delivered as a focused reliability candidate before B/C/D expansion.

## Task map

A1 baseline/CI; A2 sample migration; A3 generator correctness; A4 package/compiler compatibility; A5 property counts; A6 mapping contracts; A7 mediator/scoped contracts; A8 behaviors, cancellation and privacy.

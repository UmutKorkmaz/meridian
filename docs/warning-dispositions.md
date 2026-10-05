# Compiler and analyzer warning dispositions

This page records, for the Release build of `Meridian.sln`, which compiler and analyzer warnings were fixed, which are
retained on purpose and why, and how the remaining set is held flat by a ratchet. It is the reviewable companion to the
public-API promises in the [compatibility policy](compatibility-policy.md): **no public name, parameter name, signature,
culture behavior or logging invocation shape was changed merely to clear an analyzer**, no `NoWarn`/`WarningsNotAsErrors`
list was added, and no severity was lowered except the one scoped test-name rule described below.

Counts are *unique locations* (project, file, line, column, code); multi-targeting and MSBuild printing a warning twice do
not inflate them. Evidence for E4b: the inventory before the work was 1069 locations in 16 projects (33 codes); after, 234
locations in 13 projects (22 codes), 0 errors, no code and no project gained a warning.

## The ratchet

`eng/warning-baseline.json` records the per-project, per-code counts of the current set. `eng/warning-inventory.py` parses
MSBuild output and enforces it:

```bash
dotnet build Meridian.sln -c Release --no-incremental -nologo -v:minimal -clp:NoSummary | tee build.log
python3 eng/warning-inventory.py check --repo "$PWD" --baseline eng/warning-baseline.json build.log
```

The check fails when a project gains a warning code, when a code exceeds its recorded count in a project, when a project
that the baseline does not list appears with warnings, when a warning line cannot be parsed, or when a non-empty baseline
meets an empty log (a vanished set is never success). Fewer warnings pass and are reported as improvements; lower the
baseline in the same change that removes them (`python3 eng/warning-inventory.py baseline ...`). Line shifts from unrelated
edits never fail the check, because only counts are compared. The older `eng/docs-warning-baseline.json` (packable runtime
projects only, used by `eng/verify-docs.ps1`) was reduced to the 19 entries that remain.

## Fixed in E4b (behavior neutral)

| Code | Where | Change | Proof |
| --- | --- | --- | --- |
| RS2008 (7) | `Meridian.Mapping.Generators` | Added `AnalyzerReleases.Shipped.md` (MERIDIANGEN001, Warning, first shipped in 2.1.1) and `AnalyzerReleases.Unshipped.md` (MERIDIANGEN002-006), wired as `AdditionalFiles`. No ID, severity, message, category or default-enablement changed. | Generator project builds with 0 warnings; removing an entry makes the build report RS2000 (mutation check, `artifacts/E4b/rs2008-mutation.log`); generator diagnostic tests unchanged and green. |
| RS2001 (1, suppressed) | `MapperDiagnostics.StructuralUnmapped` | `MERIDIANGEN002` is deliberately one ID with two severities (Info in Legacy mode, Error in Structural mode). Release tracking can record only one, so the second descriptor gets a narrow `#pragma warning disable RS2001` with that explanation. Changing the ID or severities would change consumers' diagnostics and warnings-as-errors outcomes. | Same tests; descriptor bytes unchanged. |
| CA1822 (12 in `Meridian.Mapping`) | private helpers of `MappingEngine` (4) and `MapperConfiguration` (8) | Added `static`. Bodies, evaluation order and lifetimes are unchanged; several became flaggable only after their callees were made static. | Mapping suite (767/767 per TFM). |
| CA1859 (3 + 1) | `FastPathCompiler.BuildMapBody`, `TryBuildAssignment`, `ChildContext`; `MappingModelBuilder.GetContainingTypes` | Narrow private return types (`BlockExpression?`, `BinaryExpression?`, `MethodCallExpression`, `Stack<INamedTypeSymbol>`). Null checks, push/enumeration order and expression construction unchanged. | Mapping and generator suites. |
| CA1068 (3) | `TaskWhenAllPublisher.RunBounded`, `ResilientTaskWhenAllPublisher.RunResilient`, `RunBoundedResilient` | Moved the private `CancellationToken` parameter last and updated the four private call sites. Semaphore wait order, release in `finally`, aggregation and cap 16 untouched; public constructors untouched. | Publisher cancellation/concurrency/mixed-fault regressions. |
| Tests | CA1852 (18), CA1822 private (21), CA1725 (30), CA1859 (2), CA1816, xUnit2013 (4), xUnit2031 (5), CA1304/CA1311 (10), CA1305 (6), CA1310 (4), CA2219/CA2201 (3) | Sealed fixture types, `static` helpers, interface-matching parameter names on test doubles, `Assert.Single`/`Assert.Single(coll, predicate)`, `ToUpperInvariant`/ordinal/invariant overloads on ASCII test data, `GC.SuppressFinalize`, and three single-statement pragmas with a written reason for deliberate fault scenarios (a throw from `finally`, two raw `Exception` types). No test was renamed, skipped or weakened; the fault scenarios are unchanged. | All suites green on three TFMs. |
| Import order | `AdapterTests.cs` alias order (C1-M1) and every file touched by E4b | `dotnet format style --diagnostics IMPORTS` clean for touched files. | Formatter verification. |

## Scoped severity: test-name underscores (CA1707)

`tests/.editorconfig` sets `dotnet_diagnostic.CA1707.severity = suggestion` for `*.cs` under `tests/` only. Test and
fixture names use `Subject_Condition_Expected` so that a failure reads as a sentence; about 700 existing names follow it and
renaming them would break filter expressions, TRX history and ledger references. The rule exists for public library API
naming and no test type ships. It stays at its default severity everywhere else, and no other rule is changed there.

## Retained in shipped packages (member-specific compatibility reasons)

Nothing below changes without an additive replacement and a documented deprecation window; each is a deliberate decision,
not a deferral.

| Code | Exact sites | Why it stays | Revisit when |
| --- | --- | --- | --- |
| CA1305 (37) | `Meridian.Mapping`: `PropertyMap.CompileForPathSetter` (`Convert.ChangeType`), `MappingEngine.ConvertValue` (2 `Convert.ChangeType`), `MapperConfiguration.AssertConfigurationIsValid` (6 `AppendLine`), `FormatMappingPlan` (28 `AppendLine`) | Conversion and plan/diagnostic text use the thread's current culture today and that is the shipped contract: `Convert.ChangeType(object, Type)` is defined as current-culture, so an invariant default would change decimal, date, enum and `IConvertible` results, and a blanket explicit provider is unsafe at the two object-bearing sites (`ConstantValue`, `NullSubstitute`) where an `IFormattable` may distinguish a null provider from an explicit one. An explicit `CultureInfo.CurrentCulture` at the three conversion calls is behavior-identical and is the likely eventual fix, but it needs focused en-US/fr-FR/tr-TR proofs, so it was not made under a warning-cleanup change. | A culture-contract task adds those proofs. |
| CA1711 (7) | `ValueTransformerCollection`; delegates `RequestHandlerDelegate<T>`, `StreamHandlerDelegate<T>`; wrappers `NotificationHandlerWrapperImpl<T>`, `RequestHandlerWrapperImpl<TRequest,TResponse>`, `StreamRequestHandlerWrapperImpl<,>`, `UnitRequestHandlerWrapperImpl<TRequest>` | All are public API. Renaming breaks source, binary, reflection, DI and derived-class compatibility (public delegates are distinct CLR types, not aliases); adding collection interfaces only to justify a suffix changes `foreach`, serializer and interface detection. | A major release with an additive successor and deprecation lifecycle. |
| CA1716 (2) | `IPipelineBehavior<TRequest,TResponse>.Handle` and `IStreamPipelineBehavior<TRequest,TResponse>.Handle`, parameter `next` | Renaming breaks every C# caller that uses the named argument `next:` and every implementation that relies on the declared name. The warning is about other languages where `Next` is a keyword (escape it in those languages). | A major release. |
| CA1068 (1) | `RequestHandlerWrapperImpl<TRequest,TResponse>.HandleWithPipeline` | `protected static`, callable by external subclasses; reordering breaks compiled subclasses and positional callers. The three private helpers with the same warning were fixed. | A major release. |
| CA1036 (1), CA1805 (1) | `Unit` | `Unit` is a one-value struct whose `IComparable` always returns 0. Adding `<`, `<=`, `>`, `>=` is additive but changes metadata and recompiled binding, so it needs an API comparison; the redundant `_value = new()` initializer is retained so the cached `ref readonly Value` and `Task<Unit>` initialization is not touched for a cosmetic gain. | An additive API change with API comparison. |
| CA1848 (2), CA1873 (1) | `LoggerAuditSink.RecordAsync` (success `LogInformation`, failure `LogWarning`) | `LoggerMessage.Define` delegates change how a custom `ILogger` observes disabled-level calls and where its exceptions surface; the arguments are cheap record property reads. Template text, level and property order are part of the observable audit contract. | Measured need, with a captured-state comparison against custom loggers. |

## Retained in samples and benchmarks

| Project | Code (count) | Reason |
| --- | --- | --- |
| Four samples | CA1711 (1 each) | Public sample type names (`InventoryReservedDomainEventHandler`, `GetOrderTimelineStream`, `OrderUpdateStream`, `GetTodoActivityStream`) appear in documentation and snippets; their suffix is the sample's domain vocabulary. |
| `Meridian.AspNetCore` sample | CA1848 (1) | Demonstration logging call in sample code. |
| `Meridian.Hexagonal` sample | CA1305 (1) | `decimal.ToString()` is part of the displayed sample output. |
| `Meridian.Benchmarks` | CA1707 (3), CA1711, CA1720, CA1001, CA1051 (1 each) | Benchmark class and method names are result identifiers in recorded BenchmarkDotNet tables; renaming would orphan the recorded results. |

## Retained in test projects

| Code (count) | Why |
| --- | --- |
| CA1861 (108) | Single-use expected-value arrays passed to assertions. The analyzer's own fix hoists them to shared `static readonly` fields, but it generates colliding names (several `expected` fields in one class) and moves each expectation away from its assertion. No performance claim applies to tests. |
| CA1711 (8), CA1000 (4), CA1051 (8), CA2211 (5), CA1715 (4), CA1822 public (2) | Test doubles and fixtures: stream-named stubs, generic trace helpers with static logs, public capture fields and counters that the tests read directly. Renaming or encapsulating them adds no coverage. |
| CS0436 (18) | The Mapping tests deliberately compile the generator's attribute source into the test assembly while also referencing the library, so the friend-assembly type collision is intentional and tested. |
| CA1859 (6), CA2263 (2), CA1866 (1), EF1002 (3) | Casts to `IConfigurationProvider` and the non-generic `IMapper.Map` overload intentionally exercise the interface/overload under test; the EF projection test pins the `string.StartsWith(string)` translation; EF1002 raw SQL is test fixture setup against disposable databases. The analyzer's generic `Map<T>` replacement was evaluated and rejected: for the open-generic destination case it changes the scenario that the test asserts. |

New warnings are not allowed: the ratchet in `eng/warning-baseline.json` fails the build gate when any of the counts above
grows or a new code appears.

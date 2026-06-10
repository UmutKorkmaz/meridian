## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2025-06-10 - Mediator LINQ Allocations
**Learning:** In highly-executed CQRS/Mediator pipelines, using LINQ operations like `.Reverse().Aggregate(...)` for pipeline assembly or `.Select().ToList()` for handler dispatch introduces significant per-request heap allocations due to delegate captures and enumerable state machines. Microsoft.Extensions.DependencyInjection natively returns `T[]` for `IEnumerable<T>`, which provides an opportunity for array-based optimizations.
**Action:** Replace LINQ pipeline assembly in hot paths with backwards `for` loops against pre-sized arrays/collections. Type-check `IEnumerable<T>` for `T[]` or `ICollection<T>` from DI containers to enable zero-allocation enumeration and pipeline construction.

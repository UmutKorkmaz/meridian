## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2026-06-21 - Avoid LINQ .Reverse().Aggregate() for pipeline construction
**Learning:** Using LINQ `.Reverse().Aggregate(...)` to build Russian-doll Request/Stream pipelines allocates enumerators and delegates, impacting per-request heap allocations in hot paths.
**Action:** When iterating over a collection from MS.DI (`IEnumerable<T>`), type-check for `IList<T>` (since MS.DI often returns arrays) and use a backward `for` loop, safely capturing variables in a scoped iteration instead of allocating LINQ operators.

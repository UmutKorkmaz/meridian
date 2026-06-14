## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2026-06-14 - Avoid LINQ .Reverse().Aggregate() for pipeline construction
**Learning:** In hot paths (like building request or stream pipelines from DI behaviors), using LINQ `.Reverse().Aggregate()` forces multiple enumerator and delegate allocations per request.
**Action:** MS.DI typically returns `IEnumerable<T>` as arrays or lists. Always check the resolved enumerable for `IList<T>` or `IReadOnlyList<T>` and construct the pipeline explicitly using a backward `for` loop to achieve zero-allocation pipeline construction.

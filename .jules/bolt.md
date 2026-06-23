## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2026-06-23 - Avoid LINQ .Reverse() and .Aggregate() in Delegate Pipeline Construction
**Learning:** Using LINQ `.Reverse()` and `.Aggregate()` to build delegate pipelines in hot paths (like request dispatchers) incurs significant per-request allocations. It allocates an enumerator for `.Reverse()`, an enumerator for `.Aggregate()`, and delegates for the aggregation function. This can noticeably drop performance and increase GC pressure when multiple pipeline behaviors are registered.
**Action:** Always prefer looping backward via indexers to construct the pipeline when building nested delegates. If using a collection returned by DI, check if it's an `IList<T>` (as they often return lists or arrays) to allow index-based access, and only fallback to `.ToArray()` if it isn't.

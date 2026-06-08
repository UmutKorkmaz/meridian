## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.
## 2024-06-08 - Optimize Pipeline Construction with IList For-loop
**Learning:** LINQ methods like `Reverse()` and `Aggregate()` introduce notable overhead through enumerator allocations and delegate creations when composing handler pipelines. Since `Microsoft.Extensions.DependencyInjection` returns arrays (which implement `IList<T>`) for multiple registrations via `GetServices<T>()`, iterating them in reverse with a `for` loop avoids these allocations.
**Action:** When building nested delegate chains or pipelines from an `IEnumerable<T>`, check if it implements `IList<T>` first to use an allocation-free reverse `for` loop instead of `.Reverse().Aggregate()`.

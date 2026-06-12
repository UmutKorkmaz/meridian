## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2025-03-03 - Avoid LINQ allocations in pipeline construction
**Learning:** Microsoft.Extensions.DependencyInjection returns arrays (`T[]`) for `GetServices<T>()`. Using LINQ methods like `.Reverse()` and `.Aggregate()` on this output creates unnecessary per-request heap allocations for enumerators, delegates, and array copies.
**Action:** Always type-check `IEnumerable<T>` for `IList<T>` (or `ICollection<T>`). Use backward `for` loops (for reverse order) or forward loops and pre-sized lists to build pipelines and avoid LINQ allocations in the hot path.

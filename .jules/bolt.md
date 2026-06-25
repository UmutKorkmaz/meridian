## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2024-06-09 - Fast-path check for zero behaviors
**Learning:** Checking the number of behaviors for a request by first using `behaviors is object[] { Length: 0 }` pattern match is about 2x faster and allocation-free because Microsoft DI containers return arrays for `IEnumerable<T>`, and `object[]` pattern matching skips the interface dispatch overhead of checking `ICollection.Count`.
**Action:** Use `behaviors is object[] { Length: 0 }` for fast-path pipeline execution checks in Mediator implementations to optimize hot paths.

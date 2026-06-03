## 2025-03-03 - Avoid OpenTelemetry Async Wrapper Overhead
**Learning:** Returning `null` from `ActivitySource.StartActivity` (when no listeners exist) is not zero-cost if the subsequent dispatch is wrapped in an `async` state machine. The state machine overhead becomes a measurable bottleneck in hot-path abstractions like MediatR/Meridian dispatches.
**Action:** Always eagerly check `.HasListeners()` and completely bypass any `async` instrumentation wrappers, delegating directly to the underlying `Task`/`IAsyncEnumerable` returned by the handler.
## 2024-10-24 - Unnecessary ToList() allocations

**Learning:** When validating service registration through dependency injection (`IServiceProvider.GetServices(Type)`), calling `.ToList()` materializes an entire array/list unnecessarily if the goal is just to check if *any* handlers exist, or check if *exactly one* handler exists.

**Action:** Use `.Any()` to determine if a collection is empty, and manually use `GetEnumerator()` with `MoveNext()` to evaluate specific counts (like 0, 1, or >1) without allocating collection artifacts when exactly 1 item exists.

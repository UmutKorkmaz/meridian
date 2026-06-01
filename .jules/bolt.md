## 2024-06-01 - OpenTelemetry Fast Path
**Learning:** `ExecuteWithActivityAsync` wrapper adds significant overhead via closures and async state machines, even when no OpenTelemetry listeners are active.
**Action:** Always check `ActivitySource.HasListeners()` before allocating closures or entering complex tracing wrappers to provide a zero-allocation fast path for standard executions.

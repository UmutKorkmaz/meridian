## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2026-06-11 - Stop Bypassing Global Privacy Configurations
**Vulnerability:** `LoggingBehavior` in Meridian.Mediator circumvented the global `MediatorTelemetryOptions.RecordExceptionMessage` privacy flag by unconditionally copying the potentially sensitive `ex.Message` into a new sanitized exception.
**Learning:** Security/privacy configurations must be uniformly applied across all pipeline behaviors and observability hooks. A sanitized exception wrapping the raw message still leaks the data if the global config dictates exception messages should be redacted.
**Prevention:** Ensure that globally configurable privacy flags (like `RecordExceptionMessage`) are injected and respected everywhere exception details are logged, exported, or materialized.

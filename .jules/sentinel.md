## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2024-06-10 - Sensitive Data Leak in LoggingBehavior
**Vulnerability:** The `LoggingBehavior` was blindly wrapping raw exception messages (`ex.Message`) and logging them, which could leak sensitive internal system details (e.g., database connection strings, specific validation states, stack details embedded in messages) into application logs.
**Learning:** `MediatorTelemetryOptions` was introduced to control verbosity and explicitly requires an opt-in (`RecordExceptionMessage = true`) for raw exception details, but `LoggingBehavior` was ignoring this configuration.
**Prevention:** Always verify if a centralized telemetry or privacy configuration (like `MediatorTelemetryOptions`) exists when logging exceptions or handling errors, and ensure raw exception messages are conditionally redacted based on that configuration.

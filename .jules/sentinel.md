## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2025-02-23 - Respect Telemetry Privacy Flags in Logs
**Vulnerability:** Exception messages were unconditionally exposed in application logs inside `LoggingBehavior`, bypassing the established `MediatorTelemetryOptions.RecordExceptionMessage` privacy control.
**Learning:** When a codebase introduces privacy or telemetry flags (like controlling exception detail visibility), they must be applied consistently across *all* telemetry boundaries (Activity tracking, Audit logs, and Application logs). Missing one boundary creates a data leak bypass.
**Prevention:** When handling exceptions in pipeline behaviors, always inject and check the central `MediatorTelemetryOptions` before embedding `ex.Message` in logged outputs or sanitized exceptions.

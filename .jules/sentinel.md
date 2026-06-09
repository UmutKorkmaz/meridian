## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2024-05-18 - Prevent Raw Exception Details Leakage
**Vulnerability:** Found two places (`LoggingBehavior` and `MediatorHandlerValidation`) where raw exception messages (`ex.Message`) were logged without checking the explicit configuration flag `MediatorTelemetryOptions.RecordExceptionMessage`.
**Learning:** Even built-in error handling and initialization mechanisms can leak sensitive internal paths, logic, or dependencies if an underlying exception full message is inadvertently propagated to logs or validation reports that are displayed externally.
**Prevention:** Always verify if exception details should be logged by checking context configurations (like `MediatorTelemetryOptions`) and fall back to generic, redacted error messages when in doubt to ensure secure-by-default behavior.

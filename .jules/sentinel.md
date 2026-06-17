## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.
## 2026-06-17 - Prevent Exception Details Leakage
**Vulnerability:** Exception details (e.g. `ex.Message`) were directly logged or returned in validation messages without checking telemetry options.
**Learning:** Hardcoded `ex.Message` usage can leak sensitive system details or raw exception structures. The codebase has `MediatorTelemetryOptions.RecordExceptionMessage` to control this exact behavior.
**Prevention:** Always check `MediatorTelemetryOptions.RecordExceptionMessage` before exposing `ex.Message` in error handling, logging, or validations. Or simply avoid logging `ex.Message` in public-facing errors entirely.

## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2025-02-23 - Logging Observability Trade-off in LoggingBehavior
**Vulnerability:** Found `LoggingBehavior` logging `ex.Message` directly without checking `MediatorTelemetryOptions.RecordExceptionMessage` (unlike `AuditBehavior` and `Mediator`).
**Learning:** This is not a vulnerability but a deliberate design choice. While stack traces are stripped to prevent sensitive system details from leaking, the exception message is intentionally preserved to maintain application observability. Applying API-layer redaction to internal developer diagnostics causes severe observability regressions and constitutes security theater.
**Prevention:** Do not indiscriminately apply string redaction to application-level diagnostic logs intended for developers; rely on `MediatorTelemetryOptions` only for telemetry/audit sinks (like SIEM or OpenTelemetry) where the audience is broader.

## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2024-05-18 - Prevent Raw Exception Logging Leakage
**Vulnerability:** `LoggingBehavior.cs` logged the raw `ex.Message` directly, potentially leaking sensitive system details (such as database queries, user inputs, or internal paths) in exception messages into the application logs.
**Learning:** `MediatorTelemetryOptions.RecordExceptionMessage` exists specifically to control when raw exception messages are safe to log or audit, but the generic logging behavior bypassed this policy and leaked information regardless of configuration.
**Prevention:** Always check `MediatorTelemetryOptions.RecordExceptionMessage` (or similar project-wide telemetry/security options) before exposing `ex.Message` in error handling or logging to prevent sensitive data leaks. When extending components, use constructor overloading to inject configuration objects without breaking backward compatibility.

## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2025-02-23 - Retain Exception Details in Internal Application Logs
**Vulnerability:** The application was proposed to redact raw `Exception` messages in `LoggingBehavior.cs` based on `MediatorTelemetryOptions`.
**Learning:** While client-facing error responses or telemetry tags should be genericized to prevent information disclosure, **internal server logs** (e.g. `IMediatorLogger`) require detailed context (like the exception message and type) for observability, debugging, and security incident auditing. Attempting to mask internal logs cripples diagnosing security issues without actually preventing the exception from propagating to higher layers.
**Prevention:** Only apply error message redaction to client-facing boundaries or external telemetry/audit sinks, never to internal operational server logs.

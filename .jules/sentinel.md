## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2025-02-23 - Do Not Sanitize Exceptions in Internal Server Logs
**Vulnerability:** Security auditing and observability were compromised because exception stack traces and details were stripped from internal server logs (`IMediatorLogger`) in a misguided attempt to prevent data leakage.
**Learning:** While client-facing responses and external telemetry should be sanitized, internal server logs must retain full exception context (including stack traces) to allow for effective debugging and security incident auditing.
**Prevention:** Never sanitize or redact exception objects passed to internal loggers. Rely on log sink configuration and secure access controls to protect internal logs.

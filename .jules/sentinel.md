## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.
## 2024-05-01 - Exception Message Logging Information Disclosure
**Vulnerability:** LoggingBehavior logged raw exception messages (ex.Message) unconditionally.
**Learning:** This exposes sensitive internal details (stack traces, connection strings) into logs if an unhandled exception occurs in a mediator handler.
**Prevention:** Use MediatorTelemetryOptions (specifically RecordExceptionMessage) to optionally log exception messages, defaulting to a redacted message like "An error occurred during request processing."

## 2025-02-23 - Protect Exception Messages in LoggingBehavior
**Vulnerability:** The `LoggingBehavior` in the Mediator pipeline wrapped the original exception to sanitize the stack trace, but still logged the raw `ex.Message`, which can leak sensitive data (e.g. database query parameters, PII, connection details).
**Learning:** Sanitizing just the stack trace is insufficient; the exception message itself is an untrusted source of internal data. The codebase provides `MediatorTelemetryOptions.RecordExceptionMessage` specifically to control this behavior safely.
**Prevention:** Always verify `MediatorTelemetryOptions.RecordExceptionMessage` (or equivalent telemetry configs) before exposing `ex.Message` in error logging or auditing. Default to a redacted string.

## 2024-06-08 - Prevent Information Disclosure in LoggingBehavior
**Vulnerability:** Information disclosure (leaking internal exception messages to logs) via `LoggingBehavior`.
**Learning:** Behaviors and pipelines that process exceptions must conditionally sanitize raw exception messages since they often contain sensitive system details, internal paths, or database structures. The codebase has an established pattern via `MediatorTelemetryOptions.RecordExceptionMessage` but it was missed in `LoggingBehavior` where an unredacted exception message was unconditionally logged as the `InvalidOperationException` message.
**Prevention:** Always check `MediatorTelemetryOptions.RecordExceptionMessage` before exposing `ex.Message` in error handling or logging, especially in generic or middleware components.

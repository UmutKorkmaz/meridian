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

## 2024-05-18 - Prevent Raw Exception Details Leakage
**Vulnerability:** Found two places (`LoggingBehavior` and `MediatorHandlerValidation`) where raw exception messages (`ex.Message`) were logged without checking the explicit configuration flag `MediatorTelemetryOptions.RecordExceptionMessage`.
**Learning:** Even built-in error handling and initialization mechanisms can leak sensitive internal paths, logic, or dependencies if an underlying exception full message is inadvertently propagated to logs or validation reports that are displayed externally.
**Prevention:** Always verify if exception details should be logged by checking context configurations (like `MediatorTelemetryOptions`) and fall back to generic, redacted error messages when in doubt to ensure secure-by-default behavior.

## 2024-06-10 - Sensitive Data Leak in LoggingBehavior
**Vulnerability:** The `LoggingBehavior` was blindly wrapping raw exception messages (`ex.Message`) and logging them, which could leak sensitive internal system details (e.g., database connection strings, specific validation states, stack details embedded in messages) into application logs.
**Learning:** `MediatorTelemetryOptions` was introduced to control verbosity and explicitly requires an opt-in (`RecordExceptionMessage = true`) for raw exception details, but `LoggingBehavior` was ignoring this configuration.
**Prevention:** Always verify if a centralized telemetry or privacy configuration (like `MediatorTelemetryOptions`) exists when logging exceptions or handling errors, and ensure raw exception messages are conditionally redacted based on that configuration.

## 2026-06-11 - Stop Bypassing Global Privacy Configurations
**Vulnerability:** `LoggingBehavior` in Meridian.Mediator circumvented the global `MediatorTelemetryOptions.RecordExceptionMessage` privacy flag by unconditionally copying the potentially sensitive `ex.Message` into a new sanitized exception.
**Learning:** Security/privacy configurations must be uniformly applied across all pipeline behaviors and observability hooks. A sanitized exception wrapping the raw message still leaks the data if the global config dictates exception messages should be redacted.
**Prevention:** Ensure that globally configurable privacy flags (like `RecordExceptionMessage`) are injected and respected everywhere exception details are logged, exported, or materialized.

## 2025-02-23 - Prevent Exception Message Leakage in Application Logs
**Vulnerability:** The `LoggingBehavior` was wrapping exceptions in an `InvalidOperationException` but was passing the raw `ex.Message` directly, without checking the `MediatorTelemetryOptions.RecordExceptionMessage` configuration. This bypassed the application's configuration intended to prevent leaking sensitive details such as connection strings or user IDs in production logs.
**Learning:** Even when avoiding full stack traces, the raw exception `Message` property itself can contain sensitive internal details or user data. When an application has a structured configuration (like `MediatorTelemetryOptions`) to control exception detail exposure, it must be respected uniformly across all pipeline behaviors (logging, auditing, telemetry).
**Prevention:** Always check global telemetry/security options (e.g., `RecordExceptionMessage`) before exposing `ex.Message` in error handling or logging. Ensure dependencies like `MediatorTelemetryOptions` are injected into behaviors that handle exceptions to enforce this policy.

## 2024-05-18 - Prevent Raw Exception Logging Leakage
**Vulnerability:** `LoggingBehavior.cs` logged the raw `ex.Message` directly, potentially leaking sensitive system details (such as database queries, user inputs, or internal paths) in exception messages into the application logs.
**Learning:** `MediatorTelemetryOptions.RecordExceptionMessage` exists specifically to control when raw exception messages are safe to log or audit, but the generic logging behavior bypassed this policy and leaked information regardless of configuration.
**Prevention:** Always check `MediatorTelemetryOptions.RecordExceptionMessage` (or similar project-wide telemetry/security options) before exposing `ex.Message` in error handling or logging to prevent sensitive data leaks. When extending components, use constructor overloading to inject configuration objects without breaking backward compatibility.

## 2025-06-15 - Leaking exception messages in logs
**Vulnerability:** LoggingBehavior exposes raw exception messages when logging errors.
**Learning:** Behaviors should respect MediatorTelemetryOptions.RecordExceptionMessage before exposing exception details, but LoggingBehavior omitted this check, potentially leaking sensitive data like PII or SQL queries.
**Prevention:** Consistently inject and evaluate MediatorTelemetryOptions in all logging and auditing components to ensure data redaction policies are universally applied.

## 2025-02-23 - Prevent Exception Details Exposure in Application Logs (Mediator)
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by passing either an explicitly mapped message or generic failure message (`MediatorTelemetryOptions.RecordExceptionMessage` acts as the control mechanism) into a base/generic exception wrapper.

## 2026-06-17 - Prevent Exception Details Leakage
**Vulnerability:** Exception details (e.g. `ex.Message`) were directly logged or returned in validation messages without checking telemetry options.
**Learning:** Hardcoded `ex.Message` usage can leak sensitive system details or raw exception structures. The codebase has `MediatorTelemetryOptions.RecordExceptionMessage` to control this exact behavior.
**Prevention:** Always check `MediatorTelemetryOptions.RecordExceptionMessage` before exposing `ex.Message` in error handling, logging, or validations. Or simply avoid logging `ex.Message` in public-facing errors entirely.

## 2025-02-23 - Respect Telemetry Privacy Flags in Logs
**Vulnerability:** Exception messages were unconditionally exposed in application logs inside `LoggingBehavior`, bypassing the established `MediatorTelemetryOptions.RecordExceptionMessage` privacy control.
**Learning:** When a codebase introduces privacy or telemetry flags (like controlling exception detail visibility), they must be applied consistently across *all* telemetry boundaries (Activity tracking, Audit logs, and Application logs). Missing one boundary creates a data leak bypass.
**Prevention:** When handling exceptions in pipeline behaviors, always inject and check the central `MediatorTelemetryOptions` before embedding `ex.Message` in logged outputs or sanitized exceptions.


## 2025-03-05 - Mask Exception Messages in Logging
**Vulnerability:** The application was extracting and logging `Exception.Message` strings in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., query parameters, validation secrets, exact paths) that often leak into `Message` properties of framework exceptions.
**Learning:** Even when sanitizing stack traces by instantiating new base exceptions, reading `ex.Message` and passing it to the logger still triggers Information Exposure vulnerabilities (CWE-532).
**Prevention:** Mask raw exception messages in all logging behaviors (using a fallback string like `"An error occurred during request processing."`) unless the application explicitly overrides this privacy default (e.g. via `MediatorTelemetryOptions.RecordExceptionMessage`).

## 2025-06-25 - Sanitize External Correlation IDs
**Vulnerability:** Ambient `CorrelationId` context was vulnerable to Log Injection (CWE-117) and DoS (CWE-400) due to unvalidated strings.
**Learning:** Properties designed to capture external HTTP headers (like `X-Correlation-Id`) must be treated as untrusted input. Directly passing them to logs or memory without bounds checking or CRLF sanitization exposes the system.
**Prevention:** Always enforce a reasonable max-length limit (e.g., 128 chars) and strip newline characters when accepting correlation IDs or trace contexts from external boundaries.

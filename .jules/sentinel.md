## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2025-02-23 - Prevent Exception Message Leakage in Application Logs
**Vulnerability:** The `LoggingBehavior` was wrapping exceptions in an `InvalidOperationException` but was passing the raw `ex.Message` directly, without checking the `MediatorTelemetryOptions.RecordExceptionMessage` configuration. This bypassed the application's configuration intended to prevent leaking sensitive details such as connection strings or user IDs in production logs.
**Learning:** Even when avoiding full stack traces, the raw exception `Message` property itself can contain sensitive internal details or user data. When an application has a structured configuration (like `MediatorTelemetryOptions`) to control exception detail exposure, it must be respected uniformly across all pipeline behaviors (logging, auditing, telemetry).
**Prevention:** Always check global telemetry/security options (e.g., `RecordExceptionMessage`) before exposing `ex.Message` in error handling or logging. Ensure dependencies like `MediatorTelemetryOptions` are injected into behaviors that handle exceptions to enforce this policy.

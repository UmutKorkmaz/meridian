## 2025-06-15 - Leaking exception messages in logs
**Vulnerability:** LoggingBehavior exposes raw exception messages when logging errors.
**Learning:** Behaviors should respect MediatorTelemetryOptions.RecordExceptionMessage before exposing exception details, but LoggingBehavior omitted this check, potentially leaking sensitive data like PII or SQL queries.
**Prevention:** Consistently inject and evaluate MediatorTelemetryOptions in all logging and auditing components to ensure data redaction policies are universally applied.

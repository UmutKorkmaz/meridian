## 2024-06-02 - Ensure Exception Details are Scrubbed from Audit Logs
**Vulnerability:** The `AuditBehavior` in the Mediator pipeline blindly recorded raw exception messages (`ex.Message`) to the audit sink, which could potentially leak sensitive information like PII, configuration details, or credentials stored inside internal exceptions.
**Learning:** Even internal mechanisms like audit logs require defense in depth. We must ensure consistency with other telemetry options where the exposure of raw exceptions is explicitly configurable and disabled when not requested.
**Prevention:** Always verify if raw exception messages or stack traces are logged or audited. Apply telemetry filtering rules (like `MediatorTelemetryOptions.RecordExceptionMessage`) to all forms of logging.

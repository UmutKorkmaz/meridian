## 2024-05-18 - Exposure of sensitive data in audit logs
**Vulnerability:** Audit logs were recording the raw `ex.Message` on failure.
**Learning:** Exception messages can contain sensitive information like PII, connection strings, or system paths, which should never be exposed in unredacted logs.
**Prevention:** Always log generic error messages or explicitly sanitize/redact exception strings before writing them to sinks.

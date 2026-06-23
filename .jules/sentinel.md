## 2025-02-23 - Prevent Exception Details Exposure in Application Logs
**Vulnerability:** The application was logging raw `Exception` objects in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., stack traces, DB queries, file paths).
**Learning:** Raw exception objects passed directly to loggers commonly trigger the logging of the full stack trace and internal properties (CWE-532).
**Prevention:** Always sanitize exceptions before logging by either passing only the exception message/type or wrapping the message in a generic/base exception type if the logger signature strictly requires an Exception parameter.

## 2025-03-05 - Mask Exception Messages in Logging
**Vulnerability:** The application was extracting and logging `Exception.Message` strings in `LoggingBehavior.cs`, potentially exposing sensitive internal details (e.g., query parameters, validation secrets, exact paths) that often leak into `Message` properties of framework exceptions.
**Learning:** Even when sanitizing stack traces by instantiating new base exceptions, reading `ex.Message` and passing it to the logger still triggers Information Exposure vulnerabilities (CWE-532).
**Prevention:** Mask raw exception messages in all logging behaviors (using a fallback string like `"An error occurred during request processing."`) unless the application explicitly overrides this privacy default (e.g. via `MediatorTelemetryOptions.RecordExceptionMessage`).

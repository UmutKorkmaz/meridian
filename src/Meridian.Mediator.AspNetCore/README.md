# Meridian.Mediator.AspNetCore

Optional .NET 8/9/10 integration with only Meridian.Mediator and the ASP.NET Core shared framework. No EF, Redis, FluentValidation, Mapping or OpenAPI dependency is added to this package or either core library.

<!-- illustrative: configuration fragment, not compiled as shown, runnable equivalent in the packaged consumer; fixture: tests/consumers/Meridian.HttpConsumer/Program.cs -->
```csharp
builder.Services.AddMeridianProblemDetails();
// Authentication, status-code integration and full-request correlation are application responsibilities.
var app = builder.Build();
app.UseExceptionHandler(); // Install in Development and Production.
```

Repeated registration is idempotent. The final handler is stateless and singleton; request writers are resolved from the current request. Existing application handlers and writers are preserved. Register intentional application exception handlers first. See the [sample](../../samples/Meridian.AspNetCore/README.md) for authenticated scopes, nonthrowing status responses and an outer correlation boundary.

| Exception / condition | Status | Public code |
| --- | --- | --- |
| MissingAuthorizationHandlerException | 500 | internal_error |
| Meridian ValidationException | 400 | validation_failed |
| UnauthorizedException without authenticated identity | 401 | authentication_required |
| UnauthorizedException with authenticated identity | 403 | authorization_denied |
| IdempotencyInProgressException | 409 | idempotency_in_progress |
| Direct IdempotencyOutcomeUnknownException | 409 | idempotency_outcome_unknown |
| Aggregate, unclassified or unexpected cancellation | 500 | internal_error |

Problems contain a fixed type/title/status/code and bounded correlationId matching X-Correlation-ID. Exception messages, validation property/message strings, keys, payloads, request-type names, stacks and inner errors are excluded. The typed unknown case has fixed reconciliation guidance. Responses use application/problem+json and no-store. The normal IProblemDetailsService writer is used; unsupported Accept or an unstarted writer failure uses fixed safe JSON. A started/broken transport is aborted; a disconnected request receives no invented 500. Intentional application writers/customizers remain application policy and must preserve privacy.

The package uses a bounded safe HttpContext.TraceIdentifier, or generates a safe fallback. Full-request correlation requires application middleware, including successful responses and authentication challenges. The sample generates IDs on the server, ignores inbound headers, and restores preceding CorrelationContext values.

A failed POST response is not proof of rollback. Redis completion and a database commit are not one atomic transaction. Only directly typed uncertainty maps to 409; an aggregate containing it remains redacted 500. Do not automatically replay business work or infer uncertainty from exception text/Data. Reconcile through explicit application policy after quiescence and storage verification.

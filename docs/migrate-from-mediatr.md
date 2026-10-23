# Migrate from MediatR to Meridian.Mediator

This guide is for applications moving from MediatR-style in-process messaging to **Meridian.Mediator**. Existing
Meridian consumers are unaffected by this document; it only describes a migration path.

> **Which version this page describes.** `dotnet add package Meridian.Mediator` installs the published stable
> **2.1.3** package. The snippets and the behavioral-differences list below are verified against the unpublished
> **2.1.4 candidate** (not on NuGet.org; reproduce it from a locally packed feed). Stable 2.1.3 differs in three
> places that matter for a migration: an `IAuthorizedRequest` without a registered handler is **not** rejected
> (authorization fails open), cache and idempotency keys are used as written (not namespaced by request type), and
> the types `CacheKeyComposer`, `MissingAuthorizationHandlerException`, `AuthorizationBehaviorOptions`,
> `IdempotencyBehaviorOptions` and `MediatorMetricsOptions` do not exist. Items marked **(2.1.4 candidate)** need the
> candidate; the [capabilities ledger](capabilities.md#status-legend) gives the version status of every capability.

Every C# block on this page is a region of `tests/consumers/Meridian.DocumentationConsumer/Program.cs`, compiled with
C# 12 and warnings as errors against the packed packages on SDK 8.0.421, 9.0.318 and 10.0.300 and executed with
assertions by `eng/verify-packages.ps1`. MediatR itself is not executed, so the MediatR side is described in prose and
no behavioral parity with MediatR is claimed.

## Install

```bash
dotnet add package Meridian.Mediator          # stable 2.1.3 from NuGet.org
# remove MediatR packages when ready
```

The 2.1.4 candidate is not published. To follow the candidate-only parts of this guide, pack it locally with
`eng/pack-packages.ps1` and add it from that feed.

<a id="registration"></a>
## DI registration

<!-- snippet: mediator-registration -->
```csharp
var services = new ServiceCollection();
services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<GetPriceHandler>();
});
```

Resolve the focused abstractions (preferred) or `IMediator`:

<!-- snippet: mediator-abstractions -->
```csharp
var sender = scope.ServiceProvider.GetRequiredService<ISender>();
var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
var streams = scope.ServiceProvider.GetRequiredService<IStreamSender>();
```

Assembly scanning registers closed request, notification and stream handlers and closed pipeline contributions found
in the assembly. Open generic handlers are skipped and need explicit closed registrations. Handlers are transient by
default and resolve scoped dependencies from the scope you dispatch in.

<a id="requests"></a>
## Requests and handlers

| MediatR concept | Meridian |
| --------------- | -------- |
| `IRequest` / `IRequest<T>` | `IRequest` / `IRequest<TResponse>` |
| `IRequestHandler<,>` | `IRequestHandler<TRequest, TResponse>` |
| Void commands | `IRequest` + `IRequestHandler<TRequest>` |
| `INotification` / `INotificationHandler<>` | Same shape under Meridian namespaces |
| `IStreamRequest<T>` / `IStreamRequestHandler<,>` | Same shape in `Meridian.Mediator.Streaming` |
| `IPipelineBehavior<,>` | `IPipelineBehavior<TRequest, TResponse>` in `Meridian.Mediator.Pipeline` |

<!-- snippet: mediator-requests -->
```csharp
public sealed record GetPrice(string Sku) : IRequest<decimal>;
public sealed record ArchiveProduct(string Sku) : IRequest;
public sealed record ProductArchived(string Sku) : INotification;
public sealed record ListSkus(int Count) : IStreamRequest<string>;

public sealed class GetPriceHandler : IRequestHandler<GetPrice, decimal>
{
    public Task<decimal> Handle(GetPrice request, CancellationToken cancellationToken) => Task.FromResult(129.5m);
}

public sealed class ArchiveProductHandler(UnitOfWork unitOfWork) : IRequestHandler<ArchiveProduct>
{
    public Task Handle(ArchiveProduct request, CancellationToken cancellationToken)
    {
        unitOfWork.Archived.Add(request.Sku);
        return Task.CompletedTask;
    }
}

public sealed class ListSkusHandler : IStreamRequestHandler<ListSkus, string>
{
    public async IAsyncEnumerable<string> Handle(ListSkus request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return $"sku-{i}";
        }
    }
}
```

<!-- snippet: mediator-dispatch -->
```csharp
decimal price = await sender.Send(new GetPrice("desk"));
await sender.Send(new ArchiveProduct("desk"));
await publisher.Publish(new ProductArchived("desk"));
var skus = new List<string>();
await foreach (var sku in streams.CreateStream(new ListSkus(3)))
    skus.Add(sku);
```

The consumer asserts the typed result (`129.5`), the void command's side effect through a scoped dependency, both
notification handlers, the three streamed values, that canceling the caller's token ends the stream with
`OperationCanceledException` carrying that token, that sending a request without a handler throws
`InvalidOperationException` naming the request type, and that the scoped dependency is disposed with its scope.

## Behaviors

Register open and closed behaviors with the typed APIs. The built-in `ValidationBehavior<,>` runs every registered
`IValidator<TRequest>` and stops the request with `ValidationException` before later behaviors and the handler:

<!-- snippet: mediator-open-behavior -->
```csharp
services.AddTransient<IValidator<CreateProduct>, CreateProductValidator>();
services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<GetPriceHandler>();
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddBehavior<CreateProductTiming>(order: 10);
});
```

Prefer `AddOpenBehavior` / `AddBehavior<T>` over the legacy type-pair shims. `AddClosedBehavior` and
`AddClosedStreamBehavior` remain supported for dynamic explicit registrations.

Meridian ships the cross-cutting behaviors as opt-in registrations. Each one only applies to requests that implement
its marker interface (`IAuthorizedRequest`, `IIdempotentRequest`, `ICacheableQuery`/`ICacheInvalidatingRequest`,
`IRetryableRequest`, `ITransactionalRequest`):

<!-- snippet: behavior-composition -->
```csharp
var services = new ServiceCollection();
services.AddSingleton<ICacheProvider, DictionaryCache>();
services.AddSingleton<IIdempotencyStore, DictionaryIdempotencyStore>();
services.AddSingleton<ITransactionScopeProvider, RecordingTransactions>();
services.AddSingleton<IAuthorizationHandler<ShipOrder>, ShipOrderPolicy>();
services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<ShipOrderHandler>();
    cfg.AddAuthorizationBehavior(order: -300);
    cfg.AddIdempotencyBehavior(order: -200);
    cfg.AddCachingBehavior(order: -100);
    cfg.AddRetryBehavior(order: 0);
    cfg.AddTransactionBehavior(order: 100);
});
```

With this composition on the **2.1.4 candidate** the consumer asserts that an authorized command runs once, a denied command throws
`UnauthorizedException` without running, a request with **no** authorization handler fails closed with
`MissingAuthorizationHandlerException` (2.1.4 candidate; 2.1.3 runs it), a transaction commits on success and rolls back on failure while the original
exception propagates, an idempotent replay returns the stored response without running the handler, a cached query is
served from the cache until a `CacheKeyComposer.Compose<TQuery>` invalidation (2.1.4 candidate; 2.1.3 uses raw keys),
and a transient `TimeoutException` is
retried while an `ArgumentException` is not.

Pre/post processors are registered explicitly, and request exception handlers run only when their processor behavior
is registered:

<!-- snippet: behavior-processors -->
```csharp
var processed = new ServiceCollection();
processed.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<QuoteHandler>();
    cfg.AddRequestPreProcessor(typeof(QuotePreProcessor));
    cfg.AddRequestPostProcessor(typeof(QuotePostProcessor));
    // Exception handlers run only when their processor behavior is registered.
    cfg.AddOpenBehavior(typeof(RequestExceptionProcessorBehavior<,>));
});
```

## Standard pipeline

One call registers correlation (-100), audit (-50) and localized validation (0), scans handlers and validators, and
uses `LoggerAuditSink` unless you registered your own `IAuditSink` first. Localized validation needs an
`IStringLocalizer<T>`:

<!-- snippet: standard-pipeline -->
```csharp
var services = new ServiceCollection();
services.AddSingleton<IAuditSink, RecordingAuditSink>();             // replace the logger sink
services.AddSingleton(typeof(IStringLocalizer<>), typeof(MessageCatalog<>));
services.AddMeridianStandard(typeof(RenameCatalog).Assembly);       // correlation -> audit -> localized validation
```

The consumer asserts that a valid request returns the handler value, an invalid one is rejected with the localized
message without reaching the handler, the audit sink records both requests exactly once with a per-request
correlation ID, and the failure record does not contain the validation message.

## Notifications and streams

* `IPublisher.Publish` runs notification handlers sequentially by default and stops at the first failure.
* Concurrent publishers are opt-in and cap active handlers at 16 by default; pass explicit options only if you need
  a different bound.
* Stream requests use `IStreamRequest<T>` and `IStreamSender`; the scoped handler lives until enumeration completes or
  the enumerator is disposed.

<a id="behavioral-differences"></a>
## Behavioral differences to plan for

1. **Telemetry privacy**: exception messages and stack traces are off unless `MediatorTelemetryOptions` opts in.
2. **Authorization fails closed (2.1.4 candidate; 2.1.3 fails open)**: a request marked `IAuthorizedRequest` without
   a registered handler is rejected; `AuthorizationBehaviorOptions.AllowRequestsWithoutAuthorizationHandlers = true`
   is the explicit compatibility opt-out.
3. **Composed cache and idempotency keys (2.1.4 candidate; 2.1.3 uses raw keys)**: keys are namespaced by request
   type; use `CacheKeyComposer.Compose<TQuery>` for invalidation, or the explicit `UseRequestTypeNamespacedKeys = false`
   opt-outs during migration.
4. **Retry caps**: `IRetryableRequest` defaults to 3 retries, every request is capped at 10 retries and
   5 minutes of backoff, and cancellation or non-transient failures are never retried.
5. **Validation**: `IValidator<T>` is Meridian's own contract. The optional `Meridian.Mediator.FluentValidation`
   package adapts FluentValidation validators (candidate package; see [Optional integrations](integrations.md#fluentvalidation)).

## Non-goals

Meridian.Mediator is **in-process** only. It is not a replacement for message buses, gRPC, or out-of-process sagas.

## Verify

```bash
dotnet test tests/Meridian.Mediator.Tests -c Release
pwsh eng/verify-docs.ps1 -Configuration Release -Feed <packed-candidate-feed>
```

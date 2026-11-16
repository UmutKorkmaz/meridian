# Meridian.Mediator

Lightweight in-process mediator for .NET 8/9/10: requests, notifications, streams, and pipeline behaviors.

## Install

```bash
dotnet add package Meridian.Mediator
```

## Minimal setup

<!-- snippet: mediator-registration -->
```csharp
var services = new ServiceCollection();
services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<GetPriceHandler>();
});
```

<!-- snippet: mediator-abstractions -->
```csharp
var sender = scope.ServiceProvider.GetRequiredService<ISender>();
var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
var streams = scope.ServiceProvider.GetRequiredService<IStreamSender>();
```

## Pipeline

Prefer typed/open registration APIs:

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

Recommended stack (correlation → audit → localized validation):

<!-- snippet: standard-pipeline -->
```csharp
var services = new ServiceCollection();
services.AddSingleton<IAuditSink, RecordingAuditSink>();             // replace the logger sink
services.AddSingleton(typeof(IStringLocalizer<>), typeof(MessageCatalog<>));
services.AddMeridianStandard(typeof(RenameCatalog).Assembly);       // correlation -> audit -> localized validation
```

Built-in behaviors include logging, validation, authorization, caching, retry,
transaction, idempotency, and audit (compose what you need). Authorization fails closed when a request has no
authorization handler, and cache/idempotency keys are composed per request type; both have explicit compatibility
opt-outs. Metrics are opt-in (`MeridianMediatorConfiguration.Metrics`).

## Telemetry privacy

`MediatorTelemetryOptions` defaults keep exception **messages** and **stack traces**
off activity tags unless you opt in. Existing apps that never registered options
keep the privacy-preserving defaults.

Every C# block in this README is compiled and executed against the packed package by the repository's
`tests/consumers/Meridian.DocumentationConsumer`.

## Docs

- Repository: <https://github.com/UmutKorkmaz/meridian>
- Capabilities and evidence: [capabilities.md](https://github.com/UmutKorkmaz/meridian/blob/main/docs/capabilities.md)
- Migration from MediatR: [guide](https://github.com/UmutKorkmaz/meridian/blob/main/docs/migrate-from-mediatr.md)
- Compatibility policy: [policy](https://github.com/UmutKorkmaz/meridian/blob/main/docs/compatibility-policy.md)

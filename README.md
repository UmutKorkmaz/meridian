# Meridian

Meridian ships two focused .NET packages:

| Package | Purpose |
| --- | --- |
| `Meridian.Mapping` | Object-to-object mapping with profiles, member configuration, reverse maps, resolvers, converters, and query projection support. |
| `Meridian.Mediator` | In-process request/response, notifications, streams, and pipeline behaviors for CQRS-style application flow. |

Use either package independently, or use both together in the same application.

## Install

```bash
dotnet add package Meridian.Mapping
dotnet add package Meridian.Mediator
```

## Target Frameworks

The packages currently ship assets for:

- `net8.0`
- `net9.0`
- `net10.0`
- `net11.0`

## Meridian.Mapping

Minimal setup with DI and a profile:

```csharp
using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddMeridianMapping(cfg => cfg.AddProfile<OrderProfile>());

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();
var dto = mapper.Map<OrderDto>(new Order { Id = 42, CustomerName = "Ada" });

public sealed class OrderProfile : Profile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>();
    }
}

public sealed class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
}

public sealed class OrderDto
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
}
```

`Meridian.Mapping` includes:

- `Profile`-based configuration
- `CreateMap`, `ReverseMap`, `ForMember`, `ForPath`, and `IncludeMembers`
- value resolvers, converters, and transformers
- query projection support

## Meridian.Mediator

Minimal setup with assembly scanning:

```csharp
using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Ping>();
});

using var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();

var result = await mediator.Send(new Ping("Meridian"));

public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Hello, {request.Message}");
    }
}
```

`Meridian.Mediator` includes:

- request/response handlers
- notifications and publishers
- stream requests
- pipeline behaviors for validation, logging, retry, caching, transactions, authorization, idempotency, pre-processors, and post-processors
- optional FluentValidation integration through `AddFluentValidationFromAssembly(...)`

## Samples

The repository includes runnable samples for several architecture styles:

- Showcase: https://github.com/UmutKorkmaz/meridian/tree/main/samples/Meridian.Showcase
- Clean Architecture: https://github.com/UmutKorkmaz/meridian/tree/main/samples/Meridian.CleanArchitecture
- Hexagonal: https://github.com/UmutKorkmaz/meridian/tree/main/samples/Meridian.Hexagonal
- Modular Monolith: https://github.com/UmutKorkmaz/meridian/tree/main/samples/Meridian.ModularMonolith
- Vertical Slice: https://github.com/UmutKorkmaz/meridian/tree/main/samples/Meridian.VerticalSlice
- Event-Driven CQRS: https://github.com/UmutKorkmaz/meridian/tree/main/samples/Meridian.EventDrivenCqrs

## Repository

- Source: https://github.com/UmutKorkmaz/meridian

## License

Meridian is released under the MIT License. See `LICENSE` and `NOTICE` in the repository for details.

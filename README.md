# Meridian

Meridian is a lightweight, framework-style toolset for .NET applications:

- `Meridian.Mapping` for high-performance object-to-object mapping
- `Meridian.Mediator` for CQRS-style in-process request/response, notifications, and streams

## Simple Setup

The shortest useful mediator setup is:

```csharp
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
});
```

Use the focused abstractions from DI depending on what you need:

```csharp
var sender = provider.GetRequiredService<ISender>();
var publisher = provider.GetRequiredService<IPublisher>();
var streamSender = provider.GetRequiredService<IStreamSender>();
```

For pipeline registration, prefer the explicit APIs:

```csharp
services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.AddBehavior<MyClosedBehavior>();
    cfg.AddStreamBehavior<MyClosedStreamBehavior>();
    cfg.AddOpenBehavior(typeof(MyOpenBehavior<,>));
    cfg.AddOpenStreamBehavior(typeof(MyOpenStreamBehavior<,>));
});
```

`AddBehavior(Type, Type, ...)` and `AddStreamBehavior(Type, Type)` remain only as compatibility shims. Prefer the typed or explicit closed-registration APIs for new code.

## Getting Started

Install from NuGet:

```bash
dotnet add package Meridian.Mapping
dotnet add package Meridian.Mediator
```

## Samples

This repo includes architecture-focused samples to show different ways to structure a project with the same core primitives:

| Sample | Architecture Style | Focus |
| --- | --- | --- |
| `samples/Meridian.Showcase` | Baseline | End-to-end mapping + mediator feature showcase |
| `samples/Meridian.CleanArchitecture` | Clean Architecture | Domain/use-case boundaries, repository abstraction, transactional command flow |
| `samples/Meridian.Hexagonal` | Hexagonal | Ports-and-adapters separation around mediator-driven application use cases |
| `samples/Meridian.ModularMonolith` | Modular Monolith | Module-level service registration and cross-module notifications |
| `samples/Meridian.VerticalSlice` | Vertical Slice | Feature-first request grouping with per-slice handlers and validation |
| `samples/Meridian.EventDrivenCqrs` | Event-Driven CQRS | Write/read split using commands, notifications, and timeline stream |

Run a sample:

```bash
dotnet run --project samples/Meridian.CleanArchitecture/Meridian.CleanArchitecture.csproj
```

## Testing

From repository root:

```bash
dotnet restore Meridian.sln
dotnet build Meridian.sln -c Release
dotnet test Meridian.sln -c Release
```

Validate every sample:

```bash
dotnet run --project samples/Meridian.Showcase/Meridian.Showcase.csproj -c Release
dotnet run --project samples/Meridian.CleanArchitecture/Meridian.CleanArchitecture.csproj -c Release
dotnet run --project samples/Meridian.Hexagonal/Meridian.Hexagonal.csproj -c Release
dotnet run --project samples/Meridian.ModularMonolith/Meridian.ModularMonolith.csproj -c Release
dotnet run --project samples/Meridian.VerticalSlice/Meridian.VerticalSlice.csproj -c Release
dotnet run --project samples/Meridian.EventDrivenCqrs/Meridian.EventDrivenCqrs.csproj -c Release
```

## Preparing NuGet Packages

```bash
dotnet pack src/Meridian.Mapping/Meridian.Mapping.csproj -c Release --output ./artifacts
dotnet pack src/Meridian.Mediator/Meridian.Mediator.csproj -c Release --output ./artifacts
dotnet nuget push ./artifacts/Meridian.Mapping.*.nupkg --source https://api.nuget.org/v3/index.json --api-key <API_KEY>
dotnet nuget push ./artifacts/Meridian.Mediator.*.nupkg --source https://api.nuget.org/v3/index.json --api-key <API_KEY>
```

Package versions are derived from git release tags via MinVer.

## Notes

- `Meridian` keeps the API surface small, the package footprint lean, and the setup explicit.

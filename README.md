# Meridian

Meridian is a lightweight, framework-style toolset for .NET applications:

- `Meridian.Mapping` for high-performance object-to-object mapping
- `Meridian.Mediator` for CQRS-style in-process request/response, notifications, and streams

## Getting Started

Install from NuGet once published:

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
dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release
dotnet test tests/Meridian.Mediator.Tests/Meridian.Mediator.Tests.csproj -c Release
```

Validate every sample:

```bash
dotnet run --project samples/Meridian.Showcase/Meridian.Showcase.csproj -c Release
dotnet run --project samples/Meridian.CleanArchitecture/Meridian.CleanArchitecture.csproj -c Release
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

## Notes

- `Meridian` aims to provide familiar APIs for users moving from MediatR/AutoMapper patterns, while keeping dependencies small and explicit.

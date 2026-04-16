# Meridian Samples

Six runnable sample projects covering different ways to adopt Meridian.
All samples build on `dotnet build` and exit with code 0 when run.

## Pick the right starting point

| Sample | What it shows | Best for |
| --- | --- | --- |
| **[QuickStart](Meridian.QuickStart/)** | Smoke test of every public feature | First-time visitors who want to see all of Meridian working in 30 seconds |
| **[Showcase](Meridian.Showcase/)** | Comprehensive demo of mapping, mediator, notifications, and streaming | Engineers evaluating whether Meridian covers their feature needs |
| **[CleanArchitecture](Meridian.CleanArchitecture/)** | Clean Architecture (Domain/Application/Infrastructure layers) | Teams adopting Clean Architecture conventions |
| **[EventDrivenCqrs](Meridian.EventDrivenCqrs/)** | Command/query split with notification-based read model | Teams building event-driven systems with projections |
| **[ModularMonolith](Meridian.ModularMonolith/)** | Per-module boundaries inside one process | Teams with a modular monolith that need cross-module messaging |
| **[VerticalSlice](Meridian.VerticalSlice/)** | One file per feature, request + handler + validator co-located | Teams using vertical-slice architecture |

## Feature → sample matrix

If you want to see a specific feature in action:

| Feature | Where |
| --- | --- |
| `IRequest<T>` + `IRequestHandler<,>` | every sample |
| Pipeline behaviors (Logging, Validation, Caching, Retry, Transaction, Authorization, Idempotency) | Showcase, CleanArchitecture |
| Notifications (`INotification`) | Showcase, EventDrivenCqrs, ModularMonolith |
| Streams (`IStreamRequest<T>`) | Showcase, EventDrivenCqrs, VerticalSlice |
| Mapping with profiles + `ForMember` + `MapFrom` | Showcase, CleanArchitecture, EventDrivenCqrs |
| Reverse maps + `IncludeMembers` + `ProjectTo` | Showcase |
| **Safety defaults** (`MaxDepth=64`, `MaxCollectionItems=10_000`) | QuickStart |
| **Source generator** (`[GenerateMapper]`) | QuickStart |
| **Turkish culture helpers** | QuickStart |
| **Standard pipeline** (`AddMeridianStandard`) | QuickStart |
| **Audit behavior** | QuickStart |
| **Localised validation** | QuickStart |

## Running

From the repository root:

```bash
# Run any sample by project name
dotnet run --project samples/Meridian.QuickStart -c Release
dotnet run --project samples/Meridian.Showcase -c Release
dotnet run --project samples/Meridian.CleanArchitecture -c Release
dotnet run --project samples/Meridian.EventDrivenCqrs -c Release
dotnet run --project samples/Meridian.ModularMonolith -c Release
dotnet run --project samples/Meridian.VerticalSlice -c Release
```

All samples are part of `Meridian.sln` and build automatically with the
solution. CI runs the full set on every PR — see
`.github/workflows/ci.yml`.

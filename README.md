# Meridian

[![NuGet Meridian.Mediator](https://img.shields.io/nuget/v/Meridian.Mediator.svg)](https://www.nuget.org/packages/Meridian.Mediator)
[![NuGet Meridian.Mapping](https://img.shields.io/nuget/v/Meridian.Mapping.svg)](https://www.nuget.org/packages/Meridian.Mapping)
[![CI](https://github.com/UmutKorkmaz/meridian/actions/workflows/ci.yml/badge.svg)](https://github.com/UmutKorkmaz/meridian/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)

Lightweight building blocks for .NET **8 / 9 / 10** (package targets `net8.0`, `net9.0`, `net10.0`):

| Package | Role |
| --- | --- |
| **[Meridian.Mediator](https://www.nuget.org/packages/Meridian.Mediator)** | CQRS-style in-process requests, notifications, streams, and pipeline behaviors |
| **[Meridian.Mapping](https://www.nuget.org/packages/Meridian.Mapping)** | Profile-based object mapping with safe-by-default depth/collection caps and a bundled source generator |

The published stable version is **2.1.3**. Generated mediator dispatch, Structural generated mapping, metrics and the
optional `Meridian.Mediator.*` packages (FluentValidation, Caching, Redis, Entity Framework Core, ASP.NET Core,
Generators) exist only in the unpublished candidate; see the [capability ledger](./docs/capabilities.md) for the
version status and verified boundary of every feature.

Docs site: <https://umutkorkmaz.github.io/meridian/>

## Why Meridian

| Concern | Meridian stance |
| --- | --- |
| License | MIT |
| Mediator + mapper | Two focused packages; optional backends live in separate packages |
| Safety defaults | Mapping depth/collection caps; exception messages and stack traces excluded from telemetry by default (2.1.x) |
| Performance | Benchmark harness in `benchmarks/`. Earlier numbers are historical and current candidate timings are inconclusive; no performance claim is made until they are re-measured on a dedicated host. |
| AOT-friendly paths | Opt-in `[GenerateMapper]` methods and generated mediator registries; native execution verified on osx-arm64 only ([details](./docs/native-aot.md)) |
| Supply chain | CodeQL and Scorecard workflows are configured; candidates are packed with recorded hashes and a dependency inventory. Signing, provenance attestations and publication are release steps that have not run for the candidate. |

Compatibility guarantees: [docs/compatibility-policy.md](./docs/compatibility-policy.md).  
Security model: [docs/security-model.md](./docs/security-model.md).  
2.1.x security notes: [CHANGELOG.md](./CHANGELOG.md) (`GHSA-f5v8-v6q3-q4h6`).

## Install

```bash
dotnet add package Meridian.Mediator
dotnet add package Meridian.Mapping
```

## Quick start — Mediator

<!-- snippet: mediator-registration -->
```csharp
var services = new ServiceCollection();
services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<GetPriceHandler>();
});
```

Focused abstractions:

<!-- snippet: mediator-abstractions -->
```csharp
var sender = scope.ServiceProvider.GetRequiredService<ISender>();
var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
var streams = scope.ServiceProvider.GetRequiredService<IStreamSender>();
```

Pipeline registration (prefer typed / open APIs):

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

Recommended composition (correlation → audit → localized validation):

<!-- snippet: standard-pipeline -->
```csharp
var services = new ServiceCollection();
services.AddSingleton<IAuditSink, RecordingAuditSink>();             // replace the logger sink
services.AddSingleton(typeof(IStringLocalizer<>), typeof(MessageCatalog<>));
services.AddMeridianStandard(typeof(RenameCatalog).Assembly);       // correlation -> audit -> localized validation
```

## Quick start — Mapping

<!-- snippet: mapping-registration -->
```csharp
var services = new ServiceCollection();
services.AddMeridianMapping(cfg =>
{
    cfg.AddProfile<CatalogProfile>();
    // Defaults: DefaultMaxDepth = 64, DefaultMaxCollectionItems = 10_000
});
```

<!-- snippet: mapping-profile -->
```csharp
public sealed class CatalogProfile : Profile
{
    public CatalogProfile()
    {
        CreateMap<Product, ProductDto>()
            .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name));
    }
}
```

Optional generated maps (Legacy by default, Structural opt-in, optional `IMapper` integration):
[docs/generated-mapping.md](./docs/generated-mapping.md).

Every C# block above is a compiled, executed region of
[`tests/consumers/Meridian.DocumentationConsumer/Program.cs`](./tests/consumers/Meridian.DocumentationConsumer/Program.cs).

## Samples

| Sample | Architecture |
| --- | --- |
| `samples/Meridian.QuickStart` | Minimal end-to-end mediator + mapping |
| `samples/Meridian.Showcase` | Feature tour |
| `samples/Meridian.CleanArchitecture` | Clean Architecture |
| `samples/Meridian.ModularMonolith` | Modular monolith |
| `samples/Meridian.VerticalSlice` | Vertical slice |
| `samples/Meridian.EventDrivenCqrs` | Event-driven CQRS + streams |
| `samples/Meridian.Hexagonal` | Ports & adapters |
| `samples/Meridian.AspNetCore` | Orders HTTP API with the optional packages (.NET 10, needs Redis) |
| `samples/Meridian.NativeAot` | Generated mediator dispatch and mapping published with Native AOT |

```bash
dotnet run --project samples/Meridian.QuickStart -c Release
```

See [samples/README.md](./samples/README.md).

## Migration

- [MediatR → Meridian.Mediator](./docs/migrate-from-mediatr.md)
- [AutoMapper → Meridian.Mapping](./docs/migrate-from-automapper.md)

## Testing

```bash
dotnet restore Meridian.sln
dotnet build Meridian.sln -c Release
# Run the backend-free suites and filters from the required-check policy (PowerShell):
pwsh -NoProfile -Command '$p = Get-Content -Raw eng/required-checks.json | ConvertFrom-Json; foreach ($t in "net8.0", "net9.0", "net10.0") { foreach ($e in $p.countProfiles.runtime.$t) { dotnet test "tests/$($e.suite)/$($e.suite).csproj" -c Release -f $t --no-build --filter $e.filter; if ($LASTEXITCODE -ne 0) { throw "Runtime tests failed" } } }'
```

Install the exact SDK/runtime families in [CONTRIBUTING](./CONTRIBUTING.md#installing-sdks-and-runtimes).
The runtime profile above deliberately excludes provider cases. The full solution requires both
`MERIDIAN_TEST_REDIS` and `MERIDIAN_TEST_POSTGRES`; missing backends fail rather than skip
([owned provider setup](./docs/provider-testing.md)).

## Documentation index

- [docs/README.md](./docs/README.md) — full doc map
- [docs/capabilities.md](./docs/capabilities.md) — capability and evidence ledger
- [docs/integrations.md](./docs/integrations.md) — optional packages
- [CHANGELOG.md](./CHANGELOG.md)
- [SECURITY.md](./SECURITY.md)
- [CONTRIBUTING.md](./CONTRIBUTING.md)
- [docs/ROADMAP.md](./docs/ROADMAP.md)

Build and check the documentation locally (compiles every snippet against the packed packages, checks links and
builds the API reference under `artifacts/docs/`; nothing is published):

```bash
pwsh eng/pack-packages.ps1 -CandidateVersion 2.1.4-local.1 -OutputDirectory artifacts/candidate
pwsh eng/verify-docs.ps1 -Configuration Release -Feed artifacts/candidate
```

## Packaging (maintainers)

`eng/packages.json` is the single package inventory. Pack and verify a local candidate (never published by these
scripts):

```bash
pwsh eng/pack-packages.ps1 -CandidateVersion 2.1.4-local.1 -OutputDirectory artifacts/candidate
pwsh eng/verify-packages.ps1 -CandidateVersion 2.1.4-local.1 -Feed artifacts/candidate
```

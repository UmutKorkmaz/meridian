# Meridian Samples

Seven runnable sample projects covering different ways to adopt Meridian.
The six architecture samples target .NET 8, 9, and 10; QuickStart targets .NET 10.
Each architecture sample asserts a cache miss, a cache hit that bypasses its query
handler, and refreshed state after an invalidating command. Assertion failures
terminate the sample with a nonzero exit code.

## Pick the right starting point

| Sample | What it shows | Best for |
| --- | --- | --- |
| **[QuickStart](Meridian.QuickStart/)** | Smoke test of every public feature | First-time visitors who want to see all of Meridian working in 30 seconds |
| **[Showcase](Meridian.Showcase/)** | Comprehensive demo of mapping, mediator, notifications, and streaming | Engineers evaluating whether Meridian covers their feature needs |
| **[CleanArchitecture](Meridian.CleanArchitecture/)** | Clean Architecture (Domain/Application/Infrastructure layers) | Teams adopting Clean Architecture conventions |
| **[EventDrivenCqrs](Meridian.EventDrivenCqrs/)** | Command/query split with notification-based read model | Teams building event-driven systems with projections |
| **[ModularMonolith](Meridian.ModularMonolith/)** | Per-module boundaries inside one process | Teams with a modular monolith that need cross-module messaging |
| **[VerticalSlice](Meridian.VerticalSlice/)** | One file per feature, request + handler + validator co-located | Teams using vertical-slice architecture |
| **[Hexagonal](Meridian.Hexagonal/)** | Ports & adapters (domain ports, application handlers, infrastructure adapters) | Teams structuring around hexagonal / ports-and-adapters boundaries |

The additional [Orders HTTP sample](Meridian.AspNetCore/) targets .NET 10 and requires explicit disposable SQLite, dedicated Redis and local signing configuration. Run its bounded host smoke with `eng/run-samples.ps1 -Framework net10.0 -IncludeHttp`; its web host is not part of the console loop.

## Feature → sample matrix

If you want to see a specific feature in action:

| Feature | Where |
| --- | --- |
| `IRequest<T>` + `IRequestHandler<,>` | every sample |
| Pipeline behaviors (Logging, Validation, Caching, Retry, Transaction, Authorization, Idempotency) | Showcase, CleanArchitecture, Hexagonal |
| Notifications (`INotification`) | Showcase, EventDrivenCqrs, ModularMonolith, Hexagonal |
| Streams (`IStreamRequest<T>`) | Showcase, EventDrivenCqrs, VerticalSlice |
| Mapping with profiles + `ForMember` + `MapFrom` | Showcase, CleanArchitecture, EventDrivenCqrs |
| Reverse maps + `IncludeMembers` + `ProjectTo` | Showcase |
| Query caching / cache invalidation markers | Hexagonal |
| **Safety defaults** (`MaxDepth=64`, `MaxCollectionItems=10_000`) | QuickStart |
| **Source generator** (`[GenerateMapper]`) | QuickStart |
| **Turkish culture helpers** | QuickStart |
| **Standard pipeline** (`AddMeridianStandard`) | QuickStart |
| **Audit behavior** | QuickStart |
| **Localised validation** | QuickStart |

## Running

From the repository root:

```powershell
# PowerShell 7; uses the stable SDK selected by global.json.
pwsh -File eng/run-samples.ps1 -Framework net10.0 # seven samples
pwsh -File eng/run-samples.ps1 -Framework net8.0  # six; skips QuickStart
pwsh -File eng/run-samples.ps1 -Framework net9.0  # six; skips QuickStart
```

Install the matching runtime families before running these lanes. The runner
builds each sample in Release, fails with the child's exit code, and stops a
sample's process tree after 300 seconds (exit 124). Use `-TimeoutSeconds` to
change the per-sample limit, `-NoBuild` after a Release solution build, or
`-LogDirectory` to choose an output directory. Both stdout and stderr are saved
under `artifacts/sample-results/<framework>/` by default.

To run an individual sample, specify its framework explicitly:

```bash
dotnet run --project samples/Meridian.QuickStart -c Release -f net10.0
dotnet run --project samples/Meridian.Showcase -c Release -f net10.0
dotnet run --project samples/Meridian.CleanArchitecture -c Release -f net10.0
dotnet run --project samples/Meridian.EventDrivenCqrs -c Release -f net10.0
dotnet run --project samples/Meridian.ModularMonolith -c Release -f net10.0
dotnet run --project samples/Meridian.VerticalSlice -c Release -f net10.0
dotnet run --project samples/Meridian.Hexagonal -c Release -f net10.0
```

All samples are part of `Meridian.sln` and build with the solution. Required CI
executes seven samples on .NET 10 and six on .NET 8/9 on Linux, Windows, and
macOS, and uploads each lane's logs — see
[the CI workflow](../.github/workflows/ci.yml). Optional .NET 11 preview coverage
builds separately and does not gate stable sample execution.

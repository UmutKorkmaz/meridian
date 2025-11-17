# Modular Monolith Sample

This sample demonstrates a module-oriented layout inside one application:

- each module has its own boundary (Catalog, Orders, Billing)
- handlers/commands/queries are still mediated through a shared `IMediator`
- cross-module communication is done by notifications (`OrderPlacedNotification`)
- caching behavior is used for read-side queries and invalidated from commands

Run:

```bash
dotnet run --project samples/Meridian.ModularMonolith/Meridian.ModularMonolith.csproj -c Release
```

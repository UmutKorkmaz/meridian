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

This sample focuses on the architectural pattern. For features added in
Meridian v1.1 — `MaxDepth` / `MaxCollectionItems` safety defaults,
`[GenerateMapper]` source generator, `TurkishCulture` helpers,
`AddMeridianStandard`, `AuditBehavior`, and `LocalizedValidationBehavior`
— see [`Meridian.QuickStart`](../Meridian.QuickStart/) and the
`Safety defaults + culture (v1.1)` demo in
[`Meridian.Showcase`](../Meridian.Showcase/).

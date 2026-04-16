# Clean Architecture Sample

This sample shows Meridian in a layered arrangement:

- `Domain`-style entities and interfaces (`Product`, `IProductCatalog`, value object `Money`)
- `Application`-style use cases (`BrowseCatalogQuery`, `GetProductBySkuQuery`, `ReserveInventoryCommand`)
- Infrastructure abstractions for cache and transaction behavior
- Pipeline behavior usage (`Validation`, `Logging`, `Caching`, `Transaction`)
- Mapping using one profile and value trimming

Run:

```bash
dotnet run --project samples/Meridian.CleanArchitecture/Meridian.CleanArchitecture.csproj -c Release
```

This sample focuses on the architectural pattern. For features added in
Meridian v1.1 — `MaxDepth` / `MaxCollectionItems` safety defaults,
`[GenerateMapper]` source generator, `TurkishCulture` helpers,
`AddMeridianStandard`, `AuditBehavior`, and `LocalizedValidationBehavior`
— see [`Meridian.QuickStart`](../Meridian.QuickStart/) and the
`Safety defaults + culture (v1.1)` demo in
[`Meridian.Showcase`](../Meridian.Showcase/).

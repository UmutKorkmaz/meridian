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

# Meridian Hexagonal Sample

This sample demonstrates a Meridian-oriented **Ports and Adapters (Hexagonal)** layout:

- `Domain` contains ports (`IProductCatalogPort`, `IOrderRepositoryPort`, etc.) and core entities.
- `Application` contains Meridian request/command/query handlers plus validators.
- `Infrastructure/Adapters` contains adapter implementations for the ports (in-memory demo implementations).

It is intentionally framework-integrated so the sample shows how Meridian fits inside a ports-and-adapters structure, rather than a strict framework-free application core.

The sample also demonstrates:

- Query caching (`ICacheableQuery`) and cache invalidation (`ICacheInvalidatingRequest`).
- Transaction and logging behaviors.
- Notification publishing to demonstrate output ports after command handling.
- Validation behavior for both commands and queries.

Run:

```bash
dotnet run --project samples/Meridian.Hexagonal/Meridian.Hexagonal.csproj -c Release
```

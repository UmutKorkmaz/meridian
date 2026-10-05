# Migrate from AutoMapper to Meridian.Mapping

This guide is for applications moving profile-based mapping to **Meridian.Mapping**. Existing Meridian mapping
configurations keep working; this document does not change library defaults.

> **Which version this page describes.** `dotnet add package Meridian.Mapping` installs the published stable **2.1.3**
> package. The snippets and behavior lists below are verified against the unpublished **2.1.4 candidate** (not on
> NuGet.org; reproduce it from a locally packed feed). Stable 2.1.3 differs in what matters for a migration: undefined
> numeric enum values are **not** rejected, so `AllowUndefinedEnumValues` and `MeridianEnumMappingException` do not
> exist, and the generated-mapping APIs used in the [generated mapping guide](generated-mapping.md) (Structural mode,
> `UseGeneratedMappings`, `GeneratedMappingOptions`) are candidate-only. The depth and collection limits,
> `MeridianMappingLimitException` and the `AddAutoMapper` aliases exist in 2.1.3. Items marked
> **(2.1.4 candidate)** need the candidate; the [capabilities ledger](capabilities.md#status-legend) gives the version
> status of every capability.

Every C# block on this page is a region of `tests/consumers/Meridian.DocumentationConsumer/Program.cs`. That consumer
is compiled with C# 12 and warnings as errors against the packed packages on SDK 8.0.421, 9.0.318 and 10.0.300 and
executed with assertions (`eng/verify-packages.ps1`). The AutoMapper side of the migration is described in prose only:
no AutoMapper package is executed, so this guide does not claim behavioral parity with AutoMapper.

## Install

```bash
dotnet add package Meridian.Mapping          # stable 2.1.3 from NuGet.org
# ships the [GenerateMapper] analyzer as part of the package
```

<a id="registration"></a>
## DI registration

<!-- snippet: mapping-registration -->
```csharp
var services = new ServiceCollection();
services.AddMeridianMapping(cfg =>
{
    cfg.AddProfile<CatalogProfile>();
    // Defaults: DefaultMaxDepth = 64, DefaultMaxCollectionItems = 10_000
});
```

AutoMapper-shaped aliases exist for gradual migration. They are Meridian APIs with the same registrations as their
`AddMeridianMapping` equivalents (assembly, marker-type and configuration-delegate overloads), not AutoMapper itself:

<!-- snippet: mapping-alias -->
```csharp
var aliasServices = new ServiceCollection();
aliasServices.AddAutoMapper(cfg => cfg.AddProfile<CatalogProfile>()); // alias of AddMeridianMapping
```

Prefer `AddMeridianMapping` in new code. The aliases are not obsolete and keep working; any future deprecation follows
the [compatibility policy](compatibility-policy.md#deprecation-window).

<a id="profiles"></a>
## Profiles and maps

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

<!-- snippet: mapping-usage -->
```csharp
var mapper = provider.GetRequiredService<IMapper>();
var dto = mapper.Map<ProductDto>(product);
mapper.Map(product, existingDto);
```

The consumer asserts that `dto.DisplayName == "Desk"`, and that mapping onto `existingDto` keeps the same instance and
updates its values.

<a id="safety-defaults"></a>
## Safety defaults (do not surprise production)

| Setting | Default | If you need more |
| ------- | ------- | ---------------- |
| `DefaultMaxDepth` | `64` | Set higher on `IMapperConfigurationExpression` |
| `DefaultMaxCollectionItems` | `10_000` | Raise explicitly; oversize throws `MeridianMappingLimitException` |
| `AllowNullCollections` | `false` | Null source collections become empty destination collections |
| `AllowNullDestinationValues` | `true` | Null source values are assigned as null |
| `AllowUndefinedEnumValues` (2.1.4 candidate) | `false` | Undefined numeric enum values are rejected; opt in explicitly (2.1.3 does not reject them) |

These defaults close DoS classes documented in [security-model.md](security-model.md) and the GHSA notes in the
[CHANGELOG](https://github.com/UmutKorkmaz/meridian/blob/main/CHANGELOG.md). **We will not silently remove them.**
Raise caps in *your* configuration when legitimate payloads exceed them. The example below uses a deliberately small
cap to show the failure you get:

<!-- snippet: mapping-limits -->
```csharp
var limited = new MapperConfiguration(cfg =>
{
    cfg.DefaultMaxCollectionItems = 2; // raise (never lower) only when real payloads need it
    cfg.CreateMap<Order, OrderDto>();
    cfg.CreateMap<OrderLine, OrderLineDto>();
});
try
{
    limited.CreateMapper().Map<OrderDto>(order);
}
catch (MeridianMappingLimitException ex)
{
    Console.WriteLine($"{ex.Limit}: allowed {ex.MaxAllowed}, observed {ex.ObservedValue}");
}
```

The consumer asserts `MaxCollectionItems: allowed 2, observed 3` and that a two-item collection still maps.

<a id="behavior-differences"></a>
## Behavior differences to plan for

* Constructors with unresolved required arguments now throw instead of being called with type defaults; supply them
  with `ForCtorParam` or use `ConstructUsing` ([details](package-compatibility.md#runtime-constructor-correction-a6)).
* **(2.1.4 candidate)** Undefined enum values are rejected by default with `MeridianEnumMappingException`; stable
  2.1.3 maps them.
* Dictionary mapping with duplicate destination keys keeps last-write-wins.
* `ProjectTo` translation is proven only for the shapes and EF Core providers listed in
  [Projection providers](projection-providers.md); runtime resolvers and `ConstructUsing` are rejected in projections.

## Source generator (optional hot path)

For AOT or hot DTO copies, mark pairs with `[GenerateMapper]`; see the [generated mapping guide](generated-mapping.md).
Runtime `IMapper` remains the full-featured path. Opting into generation does not change existing maps, and runtime
configuration is never translated into generated code.

## Feature parity notes

Meridian covers common AutoMapper workflows: profiles, `ForMember`/`MapFrom`, flattening, reverse maps,
resolvers/converters, before/after actions, map-to-existing and projection. Not every AutoMapper extension or
third-party integration exists. Validate with your own test suite; the [capability ledger](capabilities.md#mapping)
lists what is verified and how.

## Verify

```bash
dotnet test tests/Meridian.Mapping.Tests -c Release
pwsh eng/verify-docs.ps1 -Configuration Release -Feed <packed-candidate-feed>
```

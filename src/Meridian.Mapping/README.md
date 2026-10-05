# Meridian.Mapping

Object-to-object mapping for .NET 8/9/10 with **safe-by-default** recursion and collection caps, plus a bundled
source generator for hot paths and Native AOT.

## Install

```bash
dotnet add package Meridian.Mapping
```

## Minimal setup

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

<!-- snippet: mapping-usage -->
```csharp
var mapper = provider.GetRequiredService<IMapper>();
var dto = mapper.Map<ProductDto>(product);
mapper.Map(product, existingDto);
```

## Safety defaults (2.1.x)

| Setting | Default |
| --- | --- |
| `DefaultMaxDepth` | `64` |
| `DefaultMaxCollectionItems` | `10_000` |
| `AllowNullCollections` | `false` |
| `AllowNullDestinationValues` | `true` |
| `AllowUndefinedEnumValues` | `false` |

Raise caps on your configuration if legitimate payloads need more. Oversized
collections throw `MeridianMappingLimitException`. Defaults will not be removed
on a minor release — see the [compatibility policy](https://github.com/UmutKorkmaz/meridian/blob/main/docs/compatibility-policy.md).

## Source generator

<!-- snippet: generated-minimum -->
```csharp
public class CustomerSource
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
}

public class CustomerDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
}

[GenerateMapper(typeof(CustomerSource), typeof(CustomerDto))]
public static partial class CustomerMappers
{
}
```

The generator attributes are emitted into your compilation as internal types (they are not part of this assembly's
public API). Legacy mode is the default; Structural mode and `IMapper` integration are opt-in. Guide:
[generated mapping](https://github.com/UmutKorkmaz/meridian/blob/main/docs/generated-mapping.md).

Every C# block in this README is compiled and executed against the packed package by the repository's
`tests/consumers/Meridian.DocumentationConsumer`.

## Docs

- Repository: <https://github.com/UmutKorkmaz/meridian>
- Capabilities and evidence: [capabilities.md](https://github.com/UmutKorkmaz/meridian/blob/main/docs/capabilities.md)
- Migration from AutoMapper: [guide](https://github.com/UmutKorkmaz/meridian/blob/main/docs/migrate-from-automapper.md)
- Security model: [security-model.md](https://github.com/UmutKorkmaz/meridian/blob/main/docs/security-model.md)

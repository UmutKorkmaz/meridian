# Source-Generated Mappers

This page is kept so that existing links keep working. It summarizes the mapping source generator; the maintained
reference is the [generated mapping guide](generated-mapping.md) (choosing a mode, compiled examples, precedence and
evidence) and the [generated mapping contract](generated-mapping-contract.md) (every option and diagnostic).

Meridian ships a Roslyn incremental source generator inside the `Meridian.Mapping` package. It emits typed mapping
methods at compile time. The generator attributes are emitted into your compilation as `internal` types, so they do
not appear in the `Meridian.Mapping` API reference.

## When to use source-gen

| Situation | Generated method | Runtime `IMapper` |
| --- | :-: | :-: |
| Hot path with a plain DTO shape | ✅ | |
| Native AOT publish target (direct generated calls) | ✅ (see [Native AOT](native-aot.md) for verified rows) | |
| Profile-style shared config, `ForMember` expressions | | ✅ |
| `IValueResolver` / DI-resolved converter | | ✅ |
| Conditions, before/after-map actions | | ✅ |
| Polymorphic dispatch (`Include<>`, `IncludeBase`) | | ✅ |
| Query projection (`ProjectTo`) | | ✅ |

The two paths are not mutually exclusive; see [Combining with runtime IMapper](#combining-with-runtime-imapper).

## The minimum viable example

In any consumer assembly that references `Meridian.Mapping`:

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

The generator emits (abridged) in `CustomerMappers.Meridian.g.cs`:

<!-- illustrative: abridged generated output, not hand-written source; fixture: tests/consumers/Meridian.DocumentationConsumer/Program.cs -->
```csharp
public static partial class CustomerMappers
{
    public static global::CustomerDto MapToCustomerDto(global::CustomerSource source)
    {
        if (source is null) return default!;
        var result = new global::CustomerDto()
        {
            @Id = source.@Id,
            @FullName = source.@FullName,
            @Email = source.@Email,
        };
        return result;
    }
}
```

Call it directly:

<!-- snippet: generated-legacy-call -->
```csharp
var customerDto = CustomerMappers.MapToCustomerDto(customer);
```

## Modes

`[GenerateMapper]` defaults to **Legacy** mode: same-name, same-type public properties are copied, reference and
collection members are shallow aliases, mismatched members are skipped with the informational `MERIDIANGEN002`, and a
null source returns `default`. Existing method names (`MapTo{Destination}`) and this behavior are preserved.

`Mode = GeneratedMappingMode.Structural` opts into checked member coverage, constructor binding, explicit conversions
(`[ConvertMapMember]`), member renames and static converters (`[MapMember]`, `[MapWith]`, `[IgnoreMapMember]`),
declared nested pairs, supported collections with depth/item limits and enum validation. Checked floating-point
narrowing yields infinity instead of throwing; see
[checked numeric conversions](generated-mapping.md#checked-conversions).

## Multiple targets per container class

`GenerateMapperAttribute` is `AllowMultiple = true`, so one container class can host several pairs. The emitted file
is named after the class (`FastMappers.Meridian.g.cs`), one file per container. Nested containers are supported when
every containing type is `partial`; otherwise `MERIDIANGEN001` is reported and the file is skipped.

## Inherited, init-only and required members

Public readable source properties map to public writable or `init` destination properties, walking base types with
most-derived hiding rules. Required destination members need a constructor marked `[SetsRequiredMembers]` in Legacy
mode; Structural mode can bind constructor parameters and required members from source members. Unsupported
construction is reported as `MERIDIANGEN003` and the method is omitted while other pairs in the container are kept.

## AOT safety

Generated methods contain no runtime reflection, `Activator.CreateInstance` or expression compilation. That makes the
generated code itself trim and AOT friendly; it is not a blanket platform claim. Native execution has been verified
only for the rows marked PASS in [Native AOT](native-aot.md) (osx-arm64 net8/9/10; linux-x64 PENDING).

The runtime `IMapper` path compiles expressions (`FastPathCompiler`) and uses reflection; it is **not** AOT safe and
has no AOT claim. If you publish with `<PublishAot>true</PublishAot>`, call generated methods directly.

## Combining with runtime IMapper

You can call generated methods directly on hot paths and keep `IMapper` elsewhere. Since the candidate release you can
also register generated pairs with the runtime mapper: set `GenerateResolver = true` and call
`cfg.UseGeneratedMappings(new MyMaps.GeneratedResolver())`. Explicit runtime maps keep precedence, only create-new maps
of unconfigured pairs use the generated descriptor, and map-to-existing stays on the runtime path. See
[generated runtime integration](generated-mapping.md#runtime-integration) for the compiled example.

## Incrementality

The generator is an `IIncrementalGenerator`. Editing a file that does not change a `[GenerateMapper]` container or its
source/destination types does not regenerate that container's file.

## Inspecting generated code

To see the generated source files on disk, add to your csproj:

```xml
<PropertyGroup>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

Files appear under `obj/Generated/Meridian.Mapping.Generators/...`.

## Reporting a bug

If the generator emits broken or surprising code, attach the contents of `obj/Generated/...` to the bug report along
with the user code that triggered it. See
[SECURITY.md](https://github.com/UmutKorkmaz/meridian/blob/main/SECURITY.md) for security-class issues; everything
else goes through the public issue tracker.

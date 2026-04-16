# Source-Generated Mappers

Meridian ships with a Roslyn incremental source generator that emits
typed, AOT-safe mapping methods at compile time. This guide explains
when to use it, what it can and cannot do today, and how to combine it
with the runtime `IMapper` API.

## When to use source-gen

| Situation | Use source-gen | Use runtime IMapper |
| --- | :-: | :-: |
| Hot path in a microservice | ✅ | |
| Native AOT publish target | ✅ (only AOT-safe path) | |
| Profile-style shared config | | ✅ |
| `IValueResolver` / DI-resolved converter | | ✅ |
| Conditional `.MapFrom`, before/after-map actions | | ✅ |
| Polymorphic dispatch (`Include<>`, `IncludeBase`) | | ✅ |
| Dynamic types (built at runtime) | | ✅ |
| Round-trip equality testing of plain DTOs | ✅ | |

The two paths are not mutually exclusive — see
[Combining source-gen with runtime IMapper](#combining-with-runtime-imapper).

## The minimum viable example

In any consumer assembly that references `Meridian.Mapping`:

```csharp
using Meridian.Mapping;

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

The generator emits, in `CustomerMappers.Meridian.g.cs`:

```csharp
public static partial class CustomerMappers
{
    public static CustomerDto MapToCustomerDto(CustomerSource source)
    {
        if (source is null) return default!;
        var result = new CustomerDto();
        result.Id = source.Id;
        result.FullName = source.FullName;
        result.Email = source.Email;
        return result;
    }
}
```

Call it directly:

```csharp
var dto = CustomerMappers.MapToCustomerDto(source);
```

No runtime configuration, no `MapperConfiguration`, no reflection. The
emitted IL is identical to what you would have hand-written.

## Multiple targets per container class

`GenerateMapperAttribute` is `AllowMultiple = true`, so one container
class can host every source-gen mapper in your project:

```csharp
[GenerateMapper(typeof(CustomerSource), typeof(CustomerDto))]
[GenerateMapper(typeof(OrderSource), typeof(OrderDto))]
[GenerateMapper(typeof(ProductSource), typeof(ProductDto))]
public static partial class FastMappers
{
}
```

The emitted file is named after the class
(`FastMappers.Meridian.g.cs`) so you get one file per container, not
one per pair.

Nested mapper containers are also supported as long as every containing
type is declared `partial`:

```csharp
public partial class MappingModules
{
    [GenerateMapper(typeof(CustomerSource), typeof(CustomerDto))]
    public static partial class CustomerMappers
    {
    }
}
```

If a mapper container is nested inside a non-`partial` type, Meridian
emits warning `MERIDIANGEN001` and skips that generated file rather than
producing an invalid partial declaration.

## What the MVP does NOT yet cover

The current generator deliberately handles only the simple
property-to-property subset. The runtime `IMapper` is the answer for
everything below:

- **`.MapFrom(src => expression)`** — the generator does not parse
  per-property configuration yet. Custom expressions belong on the
  runtime path.
- **Type conversion** — `int → long`, `string → int`, etc. The MVP
  emits a `// Skipped:` comment and leaves the destination member at
  its default. Use the runtime path or convert manually before calling
  the generated method.
- **`IValueResolver` / `ITypeConverter`** — these are runtime services;
  source-gen has no equivalent.
- **Nested complex types** — for nested objects of a different shape,
  the MVP also skips (same-type-only assignment). Hand-call the nested
  `MapTo*` method, or use the runtime path.
- **Profiles, before/after-map actions, conditions** — runtime only.
- **Collections** — destination collection properties are not yet
  emitted by source-gen. Map them manually or via runtime IMapper.

These are tracked as Phase 5 follow-ups; the runtime IMapper continues
to handle them correctly today.

## AOT safety

Generated methods are pure C# IL — no runtime reflection, no
`Activator.CreateInstance`, no `Expression.Lambda` compilation. They
are AOT-safe and trim-safe by construction.

The runtime `IMapper` path uses `Expression.Lambda.Compile()` (in
`FastPathCompiler`) which is **not** AOT-safe. If you are publishing
with `<PublishAot>true</PublishAot>`, every hot map should be marked
`[GenerateMapper]` and called via the generated method directly.

## Combining with runtime IMapper

For projects that mostly use runtime `IMapper` but want one or two
hot paths to be AOT-fast:

```csharp
public sealed class HotPathController : ControllerBase
{
    private readonly IMapper _mapper;
    public HotPathController(IMapper mapper) => _mapper = mapper;

    public IActionResult GetCustomer(int id)
    {
        var entity = _repo.Find(id);
        // Hot path — bypass the runtime mapper entirely.
        var dto = CustomerMappers.MapToCustomerDto(entity);
        return Ok(dto);
    }

    public IActionResult GetWithSideEffects(int id)
    {
        var entity = _repo.Find(id);
        // Cold path — keep using IMapper to pick up profiles, behaviors, etc.
        var dto = _mapper.Map<CustomerEntity, CustomerDto>(entity);
        return Ok(dto);
    }
}
```

A future Phase 5 follow-up will introduce an `IMeridianTypeInfoResolver`
abstraction (modelled on `System.Text.Json`'s
`JsonTypeInfoResolver.Combine`) so that calls through `IMapper.Map` will
automatically prefer source-generated mappers when registered, falling
back to the runtime fast path otherwise. Until then the integration is
manual: call the generated method by name where you want the speed.

## Incrementality

The generator is an `IIncrementalGenerator`, which means Roslyn caches
its output by node equality. Editing a file that does not change a
`[GenerateMapper]`-attributed class — or its source/destination types —
does not regenerate that class's mapper file. This keeps build and
IntelliSense responsive at scale.

You can verify by deleting your `obj/` folder, running
`dotnet build -bl`, and inspecting the binlog: each generator run
should report cache hits for unchanged inputs after the first build.

## Inspecting generated code

To see the generated source files on disk, add to your csproj:

```xml
<PropertyGroup>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

Files appear under `obj/Generated/Meridian.Mapping.Generators/...`. This
is useful when reviewing what the generator emitted before a release.

## Reporting a bug

If the generator emits broken or surprising code, attach the contents
of `obj/Generated/...` to the bug report along with the user code that
triggered it. See `SECURITY.md` for security-class issues; everything
else goes through the public issue tracker.

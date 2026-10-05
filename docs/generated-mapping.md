# Generated mapping guide

Meridian.Mapping offers three distinct ways to map an object. They are separate contracts with separate proof; pick
one per pair and do not assume that a guarantee of one path applies to another.

| Path | How you opt in | Default | Proof (regression rows) |
| --- | --- | --- | --- |
| Runtime `IMapper` (profiles, `ForMember`, resolvers, actions, projection) | `AddMeridianMapping` / `MapperConfiguration` | Always available | M01–M18 |
| Direct generated method, **Legacy** mode | `[GenerateMapper(typeof(S), typeof(D))]` on a partial class | `Mode = Legacy` | M19, M20 |
| Direct generated method, **Structural** mode | `Mode = GeneratedMappingMode.Structural` | Opt-in only | N01 |
| Generated runtime integration | `GenerateResolver = true` plus `cfg.UseGeneratedMappings(...)` | Opt-in only | N02 |

Runtime configuration is never translated into generated code, and generated code never parses runtime profiles.

<a id="generated-symbols"></a>
## Where the generator API lives

`GenerateMapperAttribute`, `GeneratedMappingMode`, `ConvertMapMemberAttribute`, `GeneratedConversionKind`,
`MapMemberAttribute`, `MapWithAttribute` and `IgnoreMapMemberAttribute` are **emitted into your compilation as
`internal` types** by the analyzer that ships inside the `Meridian.Mapping` package (`analyzers/dotnet/cs`). They are
not public types of `Meridian.Mapping.dll`, so the generated API reference does not list them. This guide, the
[generated mapping contract](generated-mapping-contract.md) and the compiled examples are their reference. Every C#
block on this page is a region of `tests/consumers/Meridian.DocumentationConsumer/Program.cs`, compiled with C# 12
and warnings as errors against the packed candidate on SDK 8.0.421, 9.0.318 and 10.0.300 and executed with
assertions by `eng/verify-packages.ps1`.

The runtime types that generated code and the bridge use (`GeneratedMappingOptions`, `IGeneratedMappingResolver`,
`MeridianMappingLimitException`, `MeridianEnumMappingException`, `GeneratedMappingExtensions.UseGeneratedMappings`)
are public and appear in the API reference.

<a id="legacy"></a>
## Direct Legacy mode (default)

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

<!-- snippet: generated-legacy-call -->
```csharp
var customerDto = CustomerMappers.MapToCustomerDto(customer);
```

Legacy preserves the original generator contract: same-name, same-type public properties are assigned, reference and
collection members are shallow aliases, mismatched members are skipped with an informational `MERIDIANGEN002`, and a
null source returns `default`. Method names stay `MapTo{DestinationSimpleName}`. Upgrading the package does not
change the output of an existing Legacy declaration.

<a id="structural"></a>
## Direct Structural mode (opt-in)

Structural mode turns every eligible destination member into a checked obligation: a member without a readable source
or a supported conversion is a compile-time error, constructors are bound from source members, declared nested pairs
and supported collections traverse with depth and item limits, and enums are validated.

<!-- snippet: generated-structural -->
```csharp
[GenerateMapper(typeof(Measurement), typeof(MeasurementDto), Mode = GeneratedMappingMode.Structural)]
[ConvertMapMember("Ratio", GeneratedConversionKind.CheckedNumeric)]
[ConvertMapMember("Count", GeneratedConversionKind.CheckedNumeric)]
public static partial class MeasurementMaps
{
}
```

<a id="checked-conversions"></a>
### Checked numeric conversions: what "checked" means

`GeneratedConversionKind.CheckedNumeric` emits a C# `checked` cast. Its behavior follows the C# language exactly, so it
does **not** guarantee an exception for every out-of-range value:

* Integral and `decimal` narrowing outside the destination range throws `OverflowException`.
* Floating-point to floating-point narrowing (for example `double` to `float`) never throws: a value beyond the
  destination range becomes positive or negative infinity.
* Fractional values cast to integral types are truncated (`1.9` becomes `1`), whereas runtime `Convert.ChangeType`
  rounds.

<!-- snippet: generated-checked-conversions -->
```csharp
var widened = MeasurementMaps.MapToMeasurementDto(new Measurement { Ratio = double.MaxValue, Count = 3 });
// widened.Ratio is float.PositiveInfinity: checked floating-point casts do not throw.
try
{
    MeasurementMaps.MapToMeasurementDto(new Measurement { Ratio = 1, Count = int.MaxValue + 1L });
}
catch (OverflowException)
{
    // Integral narrowing outside the destination range throws.
}
```

The example asserts that `double.MaxValue` maps to `float.PositiveInfinity` and that `int.MaxValue + 1L` throws
`OverflowException`. If you need range enforcement for floating-point values, use `MapWith` with your own static
converter. `InvariantParse` conversions parse with the invariant culture; the runtime mapper parses with the current
culture. Both differences are deliberate opt-in Structural policies, not changed runtime defaults.

<a id="runtime-integration"></a>
## Generated runtime integration (opt-in)

Set `GenerateResolver = true` on a pair to emit a `GeneratedResolver` companion, then register it with the runtime
configuration.

<!-- snippet: generated-resolver -->
```csharp
[GenerateMapper(typeof(Invoice), typeof(InvoiceDto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true)]
[GenerateMapper(typeof(Payment), typeof(PaymentDto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true)]
[MapMember("Label", "Number")]
[MapMember("Reference", "Id")]
public static partial class BillingMaps
{
}
```

<!-- snippet: generated-precedence -->
```csharp
var configuration = new MapperConfiguration(cfg =>
{
    cfg.UseGeneratedMappings(new BillingMaps.GeneratedResolver());
    // An explicit runtime map always wins over a generated descriptor for the same pair.
    cfg.CreateMap<Invoice, InvoiceDto>()
        .ForMember(d => d.Label, o => o.MapFrom(s => "profile:" + s.Number));
});
configuration.AssertConfigurationIsValid();
var mapper = configuration.CreateMapper();
var invoice = mapper.Map<InvoiceDto>(new Invoice { Number = "INV-1" });   // runtime profile
var payment = mapper.Map<PaymentDto>(new Payment { Id = 9, Amount = 12m }); // generated resolver
```

Precedence rules. The documentation consumer asserts the example case of each rule; it does not exercise the base/included-map
case of rule 1:

1. An explicit runtime `TypeMap` (from a profile or `CreateMap`) always wins over a generated descriptor for the same
   pair, including base/included maps. In the example `invoice.Label` is `"profile:INV-1"`.
2. A create-new map of an unconfigured pair uses the registered generated descriptor (`payment.Reference == 9`).
3. Map-to-existing (`mapper.Map(source, existing)`) never uses a generated descriptor. Without a runtime map it fails
   with `Missing mapping configuration for Payment -> PaymentDto` and leaves the destination unchanged.
4. Direct generated calls are independent of registration (`BillingMaps.MapToInvoiceDto` keeps the generated rule).

`AssertConfigurationIsValid()` rejects a pair advertised by more than one registered resolver. Structural resolver
calls take the minimum of the attribute limits and the effective runtime limits, and share the runtime operation's
`Items`/`State`, service scope and reference cache. Full runtime-profile equivalence is not claimed; see
[Registering generated mappings with IMapper](generated-mapping-contract.md#registering-generated-mappings-with-imapper).

<a id="limits"></a>
## Limits and defaults

Generated pairs default to `MaxDepth = 64`, `MaxCollectionItems = 10000`, `AllowNullCollections = false`,
`AllowNullDestinationValues = true` and `AllowUndefinedEnumValues = false`, matching the runtime defaults. Structural
entry methods accept an optional `GeneratedMappingOptions` argument that carries a `CancellationToken` and may set
`MaxDepth` and `MaxCollectionItems` (positive values only; zero or negative throws `ArgumentOutOfRangeException`). On
a direct entry method an explicit option value **replaces** the attribute limit for that call, so it can lower or raise
it; the safety ceiling is the attribute value you chose, not the option. Only calls that enter through the resolver
bridge (`IMapper` with `UseGeneratedMappings`) are clamped: they take the minimum of the attribute limits and the
effective runtime limits. Collection overflow throws `MeridianMappingLimitException`.

<!-- snippet: generated-chain-mapper -->
```csharp
[GenerateMapper(typeof(Chain), typeof(ChainDto), Mode = GeneratedMappingMode.Structural, MaxDepth = 2)]
public static partial class ChainMaps
{
}
```

<!-- snippet: generated-options -->
```csharp
var chain = new Chain { Value = 1, Next = new Chain { Value = 2, Next = new Chain { Value = 3 } } };
var byAttribute = ChainMaps.MapToChainDto(chain);                                             // attribute MaxDepth = 2
var raised = ChainMaps.MapToChainDto(chain, new GeneratedMappingOptions { MaxDepth = 3 });    // option replaces the limit
var lowered = ChainMaps.MapToChainDto(chain, new GeneratedMappingOptions { MaxDepth = 1 });
```

The consumer asserts all three outcomes: depth 2 by attribute, one level deeper with the raised option and one level
shallower with the lowered one.

## Diagnostics

| ID | Meaning | Severity |
| --- | --- | --- |
| `MERIDIANGEN001` | A containing type of a nested mapper is not partial. | Warning |
| `MERIDIANGEN002` | A destination member has no eligible source or the types differ. | Info in Legacy, Error in Structural |
| `MERIDIANGEN003` | The destination cannot be constructed with the supported constructor contract. | Error |
| `MERIDIANGEN004` | Duplicate pair or generated method/member collision. | Error |
| `MERIDIANGEN005` | An unsupported structural shape or capability. | Error |
| `MERIDIANGEN006` | Invalid mode, limits, names, selectors, member configuration or static converter. | Error |

A pair with an error emits no method; other valid pairs in the same container are still generated. Legacy
informational diagnostics never fail a `TreatWarningsAsErrors` build.

<a id="aot"></a>
## Native AOT and trimming boundary

Direct generated methods contain no reflection. The direct Structural subset (conversions, cycles, depth, collection
limits and cancellation) is executed natively by the packaged `Meridian.GeneratedOnlyConsumer` and the
`generated-mapping` case of `Meridian.NativeAotConsumer` on osx-arm64 for net8.0, net9.0 and net10.0. linux-x64 native
execution is PENDING (no real runner). The runtime `IMapper`, the resolver bridge and `MapperConfiguration` compile
expressions and use reflection; they carry **no** Native AOT claim. See [Native AOT support](native-aot.md).

<a id="evidence"></a>
## Evidence and status

| Claim | Runnable proof | Rows | Status |
| --- | --- | --- | --- |
| Legacy method names, shallow aliases, null return | `Meridian.DocumentationConsumer`, `Meridian.GeneratedConsumer` | M19, P02 | Accepted locally (A3, B1, B2); cumulative E4 PENDING |
| Modern/inherited/required members | `Meridian.GeneratedConsumer` | M20 | Accepted locally (A3, B3); cumulative E4 PENDING |
| Structural conversions, graphs, collections | `Meridian.DocumentationConsumer`, `Meridian.GeneratedConsumer`, `Meridian.GeneratedOnlyConsumer` | N01 | Accepted locally (B3–B5); cumulative E4 PENDING |
| Resolver integration and precedence | `Meridian.DocumentationConsumer`, `Meridian.GeneratedConsumer` | N02 | Accepted locally (B6); cumulative E4 PENDING |

Packages are an unpublished candidate; the published 2.1.3 generator has the older Legacy-only contract and its
analyzer does not load on SDK 8/9 hosts (`CS9057`, see [package compatibility](package-compatibility.md)).

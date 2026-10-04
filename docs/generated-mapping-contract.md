# Generated mapping contract

`GenerateMapperAttribute` is injected into the consuming compilation by the packaged analyzer. It and `GeneratedMappingMode` are internal to that compilation. Add the attribute to a partial class; nested containing types must also be partial. Methods are public static, and retain the name `MapTo{DestinationSimpleName}` unless `MethodName` is supplied. Separate partial declarations contribute to one mapper file with the existing metadata-based hint name.

## Modes and defaults

| Property | Default |
| --- | --- |
| `Mode` | `GeneratedMappingMode.Legacy` (`0`; `Structural` is `1`) |
| `MaxDepth` | `64` |
| `MaxCollectionItems` | `10000` |
| `PreserveReferences` | `false` |
| `AllowUndefinedEnumValues` | `false` |
| `AllowNullCollections` | `false` |
| `AllowNullDestinationValues` | `true` |
| `GenerateResolver` | `false` |
| `MethodName` | `null` |

`Legacy` preserves existing same-name, same-type assignments, shallow reference/collection aliases, skipped mismatches, and null-source return of `default`. Public readable source properties map to public writable or init destination properties, including inherited properties with most-derived hiding rules. An accessible unambiguous destination constructor callable without arguments must satisfy required members (for example through `SetsRequiredMembers`). Automatic constructor selection is available only in Structural mode. Explicit member attributes below also work in Legacy.

`Structural` opts into checked member coverage for the currently implemented subset: identity scalar assignments, public constructor binding, and shallow same-type ordinary references when no nested mapping pair is declared. Every eligible destination property without a readable same-name source or with a different source type is an error; a required member is never silently omitted. Required fields and required properties outside the supported writable surface are rejected. Nullability transitions, enums, collections, declared nested mappings and non-default structural traversal/null/enum policies are rejected until their implementation is available. Default limits are configuration values in this phase, not a claim of traversal enforcement; no collection or nested traversal is emitted. Root null-source handling currently retains the legacy `default` return.

Positive limits are required in both modes. Legacy accepts the semantic option values without changing its documented shallow/null behavior; choose Structural when requesting checked transformations, whose pending semantic options produce an error. `GenerateResolver=true` is rejected in either mode until resolver integration is implemented. No generated options overload, runtime registration bridge, automatic numeric conversion, parsing, deep collection mapping, reference tracking or runtime-profile equivalence is claimed by this phase.

```csharp
[GenerateMapper(typeof(Order), typeof(OrderDto),
    Mode = GeneratedMappingMode.Structural, MethodName = "ToOrderDto")]
public static partial class OrderMaps { }
```

`MethodName` must be a valid non-reserved C# identifier without a verbatim (`@`) prefix or Unicode format characters (which C# ignores for identifier identity), and must be unique among mapping names and members declared in the container. Default names may overload across distinct source types. Collision checks treat `dynamic` and `object` as the same signature type, including nested generic arguments and arrays; escaped spellings of existing member names retain their compiler identity. Two destination types with the same simple name and the same source need distinct explicit names. Duplicate source/destination pairs are an error even with different names. Existing container members and the container's own name cannot be replaced by a generated conflicting method. Source/destination types must be nameable, closed and accessible with visibility sufficient for the emitted public method.

## Constructors and explicit member configuration

Structural mode chooses the public constructor with the largest number of resolvable parameters. Parameters bind to explicitly configured members first, then case-insensitive public readable source properties with assignable identity/reference/boxing types, then optional defaults. A narrower constructor remains eligible when a wider constructor has an unresolved required parameter. Equal best candidates produce `MERIDIANGEN003`; inaccessible constructors and ref/out/in parameter constructors are ineligible. Optional constants are emitted with explicit types and invariant literals, including enum and decimal defaults. No runtime reflection or dynamic invocation is generated.

Records, ordinary immutable DTOs and record structs use constructor arguments plus an object initializer. Constructor-consumed properties are not assigned again when their destination type and source binding match the selected argument. Optional defaults never consume an independently mappable initializer; an unconfigured same-type property with no source may remain satisfied by its matching optional parameter default. Explicit constructor-only requests must all be bound by the selected eligible constructor, so an unrelated or parameterless fallback cannot discard them. As required by C#, required properties must appear in the initializer unless the selected constructor has `SetsRequiredMembers`. Public required/init properties must be mapped; an ignore cannot bypass the language's required-member obligation. Required fields remain unsupported. Plain Legacy continues binding the existing lexical `new Destination()` expression, including its optional/params behavior, and never opts into source-fed constructor selection.

The generator injects three container attributes:

```csharp
[GenerateMapper(typeof(Person), typeof(PersonDto), Mode = GeneratedMappingMode.Structural)]
[MapMember("Name", "DisplayName")]
[MapWith("Age", typeof(MemberConverters), "ConvertAge")]
[IgnoreMapMember("Note")]
public static partial class PersonMaps { }
```

`MapMember(destinationMember, sourceMember)` overrides naming convention. `MapWith(destinationMember, converterType, methodName)` calls one accessible, nongeneric static method with one by-value source-member argument and an assignable return value. The two attributes may compose; otherwise the converter receives the convention-matched source property. Identity, reference and boxing assignability are supported without introducing implicit numeric conversions. Inherited static methods are supported with normal member hiding. Instance, generic, abstract/virtual static interface, inaccessible, incompatible or ambiguous methods produce `MERIDIANGEN006`. A custom method is responsible for the conversion it explicitly implements, including collections or enums; automatic traversal and conversion remain pending. An ignore preserves the destination's constructor/initializer value and suppresses the unmapped-member diagnostic. Conflicting or repeated configuration is invalid.

All three attributes expose optional `SourceType` and `DestinationType` selectors. Both selectors target exactly one declared pair; one selector, null selectors or a nonexistent pair are invalid. Without selectors, configuration applies to every declared pair whose supported destination surface contains that name. Other destination shapes are unaffected. A missing source or invalid converter in an applicable pair is diagnosed; configuration matching no declared pair is invalid. Constructor parameter names use case-insensitive matching; property names and explicit source names use exact matching. Attributes across partial declarations participate in the same container.

These APIs apply in both modes and leave unconfigured Legacy behavior unchanged. Runtime expression/lambda parsing, `ForCtorParam`, `ConstructUsing`, DI services and resolver integration are runtime-only. Requesting the pending generated resolver capability produces `MERIDIANGEN005`; invalid static method requests produce `MERIDIANGEN006`.

## Diagnostics

All pair diagnostics point to the original `GenerateMapper` attribute, including attributes in separate partial declarations. `MERIDIANGEN001` retains its container identifier location. Current syntax trees are retained for diagnostics independently of the immutable generation model, preserving `#line`, pragma and suppression behavior across incremental edits.

| ID | Meaning | Severity |
| --- | --- | --- |
| `MERIDIANGEN001` | A containing type of a nested mapper is not partial. | Warning, unchanged |
| `MERIDIANGEN002` | Destination property has no eligible same-name source property, or property types differ. | Info in Legacy; Error in Structural |
| `MERIDIANGEN003` | Destination cannot be constructed with the currently supported constructor contract. | Error |
| `MERIDIANGEN004` | Duplicate pair, generated method collision, or collision with a declared container member/name. | Error |
| `MERIDIANGEN005` | Requested structural member or generated capability is not implemented. | Error |
| `MERIDIANGEN006` | Invalid mode/limits/names/types, insufficient signature visibility, invalid selectors, conflicting member configuration or invalid static converter. | Error |

Legacy informational diagnostics do not become build failures under `TreatWarningsAsErrors=true`. Compiler errors already present in user source remain compiler errors. A pair with an error emits no mapping method; other valid pairs in that container remain available. A nested mapper with a non-partial containing type emits no mapper file. No error path emits a conflicting or knowingly invalid method.

## Verification boundaries

Tests compile and invoke generated sources for legacy aliases, strict scalar mapping, positional records, required/init properties, constructor resolution/defaults, renames, ignores, static methods, selected pairs and incremental updates. Negative cases assert generator diagnostics and the absence of generated compiler errors. Packaged consumer verification uses the existing C# 12 lanes on SDK 8.0.421, 9.0.318 and 10.0.300, while the production analyzer retains its Roslyn 4.8 / netstandard2.0 floor. These checks establish the supported subset; they do not establish trim/AOT compatibility for future structural features or full runtime mapping equivalence.

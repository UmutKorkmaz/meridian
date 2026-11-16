# B — Mapping evolution design

Status: proposed. Inherits the portfolio Global Constraints. A's reliability gate precedes implementation.

## Outcome and compatibility modes

Extend generated mapping into a useful typed, trim-aware option while keeping runtime mapping fully supported. Keep existing generated method names and legacy shallow assignment semantics. An additive GeneratedMappingMode enum has Legacy=0 and Structural=1; GenerateMapperAttribute.Mode defaults to Legacy. Structural mode is opt-in and promises checked supported transformations, not full runtime-profile equivalence.

Legacy mismatched/unmapped members receive informational diagnostics so existing warning-as-error builds are not broken by new warnings. Structural mode emits an error for unsupported required mappings or construction, rather than silently returning partial DTOs. Configured ignores and custom static methods make intentional omissions explicit. Existing MERIDIANGEN001 remains stable; reserve MERIDIANGEN002 unmapped/type mismatch, 003 construction, 004 method collision, 005 unsupported structural member, and 006 inaccessible/invalid configuration. Compile-safe init/inherited fixes from A remain effective in both modes.

## Generation model

Separate discovery, immutable mapping model, conversion classification, diagnostics, and C# emission inside Meridian.Mapping.Generators. Retain netstandard2.0 and compiler-host compatibility proven by A4. GenerateMapper gains Mode=Legacy, MaxDepth=64, MaxCollectionItems=10000, PreserveReferences=false, AllowUndefinedEnumValues=false, AllowNullCollections=false, AllowNullDestinationValues=true, GenerateResolver=false and optional string MethodName=null properties. MethodName must be a valid unique identifier; existing names remain unchanged when omitted. Compile-generated context carries depth and optional reference identity; generated helpers enforce collection limits on both counted and streaming sources.

Support public readable/writable/init properties, required members, public constructors, records with constructor parameters, inherited member hiding, arrays/lists/IEnumerable/IReadOnlyList/ICollection and dictionaries. Constructor selection follows runtime resolution rules; ambiguous construction is diagnosed. Structural conversions initially cover widening numeric, nullable transitions, enums and invariant parsing explicitly matched to runtime test cases. Narrowing conversion is explicit and checked. No custom reflection calls or DynamicInvoke are emitted.

Nested structural maps use attributed source/destination pairs in the same container. Same-type scalar assignment stays direct; same-type reference assignment stays shallow unless structural configuration explicitly selects a nested pair. Recursive traversal respects caps, and PreserveReferences uses reference equality rather than value equality. Mutable destinations are cached before recursion; constructor-only cyclic graphs cannot fulfill that guarantee and receive MERIDIANGEN005 when reference preservation is requested. Noncyclic immutable DTOs remain supported.

Container-level configuration attributes are MapMemberAttribute(string destinationMember,string sourceMember), IgnoreMapMemberAttribute(string destinationMember), MapWithAttribute(string destinationMember,Type converterType,string methodName), and ConvertMapMemberAttribute(string destinationMember,GeneratedConversionKind kind). GeneratedConversionKind has CheckedNumeric and InvariantParse. These attributes expose optional Type SourceType and DestinationType selectors: both supplied selects one declared pair; both omitted applies to all compatible declared pairs; only one selector is an error. Static custom methods take one source-member value and return an assignable destination-member value; inaccessible, instance, ambiguous or incompatible methods are diagnosed. DI resolvers and runtime lambda parsing remain runtime-only.

B5 adds a core GeneratedMappingOptions sealed record with nullable int MaxDepth/MaxCollectionItems overrides (null inherits the attribute default), CancellationToken CancellationToken=default and opaque object? State=null. Structural methods add MapToDestination(Source source,GeneratedMappingOptions options); the old one-argument method delegates with default options. Invalid supplied limits fail before traversal. Cancellation is checked before work and during synchronous enumeration. State is exposed only to the generated context/resolver bridge; this phase does not promise generated DI actions. Structural depth/null/enum failures match the supported runtime contract; oversized collections throw MeridianMappingLimitException.

## Runtime registration contract

Add IGeneratedMappingResolver.TryGetMapping(Type sourceType, Type destinationType, out GeneratedMappingDescriptor? mapping). GeneratedMappingDescriptor is a sealed record with SourceType, DestinationType and Func<object?, ResolutionContext, object?> Map. Add an optional IGeneratedMappingConfiguration interface exposing void AddGeneratedResolver(IGeneratedMappingResolver resolver), implemented by MapperConfigurationExpression, and an optional IGeneratedMappingConfigurationProvider exposing IReadOnlyList<IGeneratedMappingResolver> GeneratedResolvers, implemented by MapperConfiguration. The old configuration interfaces gain no abstract members.

UseGeneratedMappings(this IMapperConfigurationExpression configuration, params IGeneratedMappingResolver[] resolvers) explicitly enables the bridge. GenerateMapperAttribute.GenerateResolver=true emits a companion resolver implementing the interface; direct methods remain available.

Existing explicit TypeMaps always win. Generated descriptors fill create-new mappings lacking an explicit TypeMap. Map-to-existing, profile converters/actions, destination reuse, and unsupported operation features keep existing behavior; the bridge never silently bypasses a configured map. Null/global cap semantics and per-call Items/State are forwarded through ResolutionContext. The bridge uses the stricter of the configured runtime cap and the declared generated cap; per-call options cannot silently raise the runtime boundary. Direct generated calls use attribute caps unless explicit per-call overrides are supplied. Full-runtime Native AOT is not claimed; a packaged generated-only consumer must pass its own trim/AOT gate, and unsupported runtime paths remain documented.

## Projection and performance

ProjectTo keeps its expression-based contract and reports unsupported runtime-only members. Add SQLite and PostgreSQL execution/SQL assertions for supported shapes, with explicit provider capability notes. Never claim server translation from AsQueryable result tests alone.

P3.7 inlines eligible nested compiled maps only after differential tests prove values, identity, failures and safety boundaries. PreserveReferences, custom services/actions and polymorphic paths continue using the existing fallback unless eligibility is separately proved. Avoid replacing public ResolutionContext with a mutable struct or pooling state across calls.

## Acceptance and tasks

B1 incremental models/compiler tests; B2 diagnostics/configuration; B3 immutable constructors/static member mapping; B4 conversions; B5 nested collections/references; B6 resolver integration; B7 SQL projection; B8 safe nested optimization. Every structural feature has generated-source compilation plus runtime-intersection parity. Both mode behavior and actual NuGet analyzer consumption are verified before release. Unsupported capability documentation is explicit.

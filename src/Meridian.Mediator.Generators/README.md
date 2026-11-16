# Meridian.Mediator.Generators

Optional analyzer-only package for explicit closed handler registration. Reference this package alongside Meridian.Mediator. It has no runtime dependency assets.

<!-- illustrative: configuration fragment, not compiled as shown, runnable equivalent in the packaged consumer; fixture: tests/consumers/Meridian.MediatorRegistryConsumer/Program.cs -->
```csharp
[Meridian.Mediator.Generated.GenerateMediatorRegistry(
    typeof(GetOrderHandler), ExpectedRequestTypes = new[] { typeof(GetOrder) })]
public partial class AppRegistry { }

services.AddMeridianMediator(c => c.AddGeneratedRegistry<AppRegistry>());
```

Use a top-level non-generic partial class with no declared constructors. The generator emits a public parameterless constructor. Handlers use one accessible constructor or exactly one accessible ActivatorUtilitiesConstructor. Direct factories support ordinary required dependencies, provider injection, collections and compile-time optional defaults. Open handlers require explicit concrete closed types. Keyed/ref/pointer/ref-like parameters, required members without SetsRequiredMembers and inaccessible constructors are diagnosed.

The generator emits immutable type metadata, direct closed DI factories and closed request, notification and stream wrappers. With `RequireGeneratedDispatch = true` the mediator never creates handler wrappers by reflection: an undeclared request, notification or stream fails with an actionable "No generated ... dispatch" message. Behavior, stream-behavior, validator, pre/post-processor and exception-handler lists make every closed contribution explicit; `IncludeStandardPipeline = true` adds the standard correlation, audit and localized-validation behaviors. Strict mode rejects assembly scanning and open pipeline descriptors that no registry declares.

Native AOT: the packaged consumer in `tests/consumers/Meridian.NativeAotConsumer` is published with `PublishAot` and trimming from the exact packed feed and executed by `eng/run-aot-smoke.ps1 -Combined`. See `docs/native-aot.md` for the supported-configuration table (which OS/architecture/TFM rows are verified and which are pending a real runner). Outside strict generated dispatch, a native binary has no reflective fallback: legacy assembly scanning and non-generated behavior registration throw `NotSupportedException`.

MERIDIANMED001 checks only ExpectedRequestTypes against the same declaration's handlerTypes; omitted expectations assert nothing. MERIDIANMED002 detects duplicate request/stream handlers; multiple notification handlers are valid. MERIDIANMED003 reports malformed declarations and constructor errors; MERIDIANMED005 reports unsupported constructions. 004 is reserved for visible lifetime evidence and does not guess container lifetime graphs.

Target: netstandard2.0; Roslyn API dependency: 4.8.0. Native compiler consumer lanes: SDK 8.0.421/csc 4.11, SDK 9.0.318/csc 4.14, SDK 10.0.300/csc 5.6; C# 12 with warnings as errors. Roslyn dependency version alone is not an execution proof for csc 4.8.

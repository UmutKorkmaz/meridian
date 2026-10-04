# Meridian.Mediator.Generators

Optional analyzer-only package for explicit closed handler registration. Reference this package alongside Meridian.Mediator. It has no runtime dependency assets.

```csharp
[Meridian.Mediator.Generated.GenerateMediatorRegistry(
    typeof(GetOrderHandler), ExpectedRequestTypes = new[] { typeof(GetOrder) })]
public partial class AppRegistry { }

services.AddMeridianMediator(c => c.AddGeneratedRegistry<AppRegistry>());
```

Use a top-level non-generic partial class with no declared constructors. The generator emits a public parameterless constructor. Handlers use one accessible constructor or exactly one accessible ActivatorUtilitiesConstructor. Direct factories support ordinary required dependencies, provider injection, collections and compile-time optional defaults. Open handlers require explicit concrete closed types. Keyed/ref/pointer/ref-like parameters, required members without SetsRequiredMembers and inaccessible constructors are diagnosed.

D1 provides immutable type metadata and direct closed DI registrations. Dispatch currently remains the existing runtime reflection path. RequireGeneratedDispatch and standard pipeline activation fail explicitly; nonempty pipeline option lists receive MERIDIANMED005 until their later implementation. Wrapper lookup members return null at this stage. This package does not yet establish trimming or Native AOT support.

MERIDIANMED001 checks only ExpectedRequestTypes against the same declaration's handlerTypes; omitted expectations assert nothing. MERIDIANMED002 detects duplicate request/stream handlers; multiple notification handlers are valid. MERIDIANMED003 reports malformed declarations and constructor errors; MERIDIANMED005 reports unsupported constructions. 004 is reserved for visible lifetime evidence and does not guess container lifetime graphs.

Target: netstandard2.0; Roslyn API dependency: 4.8.0. Native compiler consumer lanes: SDK 8.0.421/csc 4.11, SDK 9.0.318/csc 4.14, SDK 10.0.300/csc 5.6; C# 12 with warnings as errors. Roslyn dependency version alone is not an execution proof for csc 4.8.

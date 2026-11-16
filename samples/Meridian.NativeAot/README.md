# Meridian.NativeAot

A readable Native AOT example of Meridian.Mediator with Meridian.Mapping: closed generated registries, strict
generated dispatch, the standard validation/audit/correlation pipeline, scoped dependencies, notifications, streams
and generated DTO mapping.

`Program.cs`, `Registry.cs` and `Mapping.cs` are the same files the packaged consumer
(`tests/consumers/Meridian.NativeAotConsumer`) publishes and executes. A test (`eng/tests/test_native_aot.py`)
keeps them byte-identical. This sample uses source references so it is easy to build and debug; source references
are never packaged-artifact evidence.

## Run it

```bash
# Managed (JIT): runs the nine shared self-checks.
dotnet run --project samples/Meridian.NativeAot -c Release -f net10.0

# Native AOT with trimming (source references; needs the platform native toolchain).
dotnet publish samples/Meridian.NativeAot -c Release -f net10.0 -r osx-arm64 -p:NativeAotSmoke=true -o artifacts/native-sample
artifacts/native-sample/Meridian.NativeAot --require-native --expect-os osx --expect-arch arm64
```

`--require-native` also runs three native-only cases and fails unless the process is a real non-JIT native binary
on the expected OS and architecture.

## The packaged proof

`eng/run-aot-smoke.ps1 -Combined` publishes the packaged consumer from an exact local feed and executes the native
binary, comparing the executed case IDs with the expected set:

```powershell
./eng/pack-packages.ps1 -CandidateVersion 2.1.4-local.1 -OutputDirectory artifacts/candidate
./eng/run-aot-smoke.ps1 -Combined -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1 `
    -Framework net10.0 -SdkVersion 10.0.404 -Rid osx-arm64
```

`-Rid` aliases `-RuntimeIdentifier`. The runner refuses a host that is not the matching native OS and process
architecture (no emulation, translation or cross-publication). `-MappingOnly` keeps the original mapping-only
contract. See [docs/native-aot.md](../../docs/native-aot.md) for the verified configuration table, what is not
supported on native, and the exact failure messages.

## What the configuration looks like

```csharp
[GenerateMediatorRegistry(typeof(GetOrderHandler) /* ... every handler ... */,
    BehaviorTypes = new[] { typeof(PassBehavior<,>) },
    ValidatorTypes = new[] { typeof(GetOrderValidator) },
    IncludeStandardPipeline = true,
    ExpectedRequestTypes = new[] { typeof(GetOrder) })]
public partial class AppRegistry { }

services.AddMeridianMediator(c =>
{
    c.Lifetime = ServiceLifetime.Scoped;
    c.RequireGeneratedDispatch = true;       // unknown requests fail; nothing is created by reflection
    c.AddGeneratedRegistry<AppRegistry>();
});
```

Everything the binary dispatches is declared in a registry. Assembly scanning and legacy (non-generated) behavior
registration are not available on native and throw `NotSupportedException` with a pointer to the registry.

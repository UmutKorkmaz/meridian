# Native AOT support

This page states exactly what has been verified for Meridian.Mediator and Meridian.Mapping on Native AOT, what
has not, and how to reproduce it. A row is PASS only when a **packaged consumer was published with `PublishAot` and
trimming from the exact packed feed and its native executable was run on a matching real host, exiting zero with every
self-check recorded**. Publishing alone, compiling, archive inspection, emulation and cross-publication never count.

Run date of the evidence below: 2026-10-04. Candidate packages `2.1.4-d4.20261004.1` (local feed, never published).

## Supported-configuration table

| RID | TFM | SDK (pinned) | C# compiler | Native runtime | Combined mediator + mapping | Mapping-only | ILC/trim/AOT warnings | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| osx-arm64 | net8.0 | 8.0.421 | 4.11.0-3.25569.22 | .NET 8.0.27 | 12/12 cases, exit 0 | PASS | 0 (one Apple linker notice, see below) | **PASS** |
| osx-arm64 | net9.0 | 9.0.318 | 4.14.0-3.26064.1 | .NET 9.0.20 | 12/12 cases, exit 0 | PASS | 0 (one Apple linker notice, see below) | **PASS** |
| osx-arm64 | net10.0 | 10.0.300 | 5.6.0-2.26230.102 | .NET 10.0.8 | 12/12 cases, exit 0 | PASS | 0 | **PASS** |
| linux-x64 | net8.0 | 8.0.421 | n/a | n/a | not run | not run | n/a | **PENDING** (real linux-x64 runner required) |
| linux-x64 | net9.0 | 9.0.318 | n/a | n/a | not run | not run | n/a | **PENDING** (real linux-x64 runner required) |
| linux-x64 | net10.0 | 10.0.300 | n/a | n/a | not run | not run | n/a | **PENDING** (real linux-x64 runner required) |

* osx-arm64 evidence: Apple Silicon macOS 26.5.2 (Darwin 25.5.0), Apple clang 21.0.0, Xcode SDK 26.5, `ld` 1267. The
  runner checked OS, machine and process architecture, rejected translated (Rosetta) processes and read the Mach-O
  header (`macho-arm64`) before executing. Receipts per row are under
  `artifacts/D4/final/aot-combined/2.1.4-d4.20261004.1/combined-<tfm>-<sdk>-osx-arm64-*/` (`native-result.json`,
  `warnings.json`, `restore-receipt.json`, `generated-sources.json`, `fixture-hashes.json`, `toolchain.json`,
  `publish.log`, `publish.binlog`, `execute.log`) and `.../aot-mapping/...` for the unchanged mapping-only runs.
* **linux-x64 is PENDING, not PASS.** The development machine is Apple Silicon with an aarch64 Docker daemon; there is
  no linux-x64 runner. The CI jobs `native-aot-pack` and `native-aot` in `.github/workflows/ci.yml` build the three
  linux-x64 rows on `ubuntu-24.04` and the three osx-arm64 rows on `macos-14` from one immutable feed artifact. They have
  not run on a real runner yet. The runner refuses any host that is not the matching OS and process architecture, so an
  emulated or cross-published binary cannot turn these rows green.
* Unverified and not claimed: linux-arm64, linux-musl, win-x64, win-arm64, osx-x64, .NET 11 preview, other Linux
  distributions/libc versions and macOS deployment targets. A native binary is only claimed for the rows above.

The Apple linker notice `ld: warning: -ld_classic is deprecated and will be removed in a future release` is printed by
the .NET 8 and .NET 9 ILCompiler targets on macOS. It is toolchain-owned (not a Meridian code path), is recorded in
each row's `warnings.json` with disposition `toolchain-known`, and is the only warning the runner tolerates; any other
warning line fails the run (`unreviewed`).

## What the native binary executes

`tests/consumers/Meridian.NativeAotConsumer` (packaged, listed in `eng/packages.json` with exact package versions, no
`ProjectReference`) references `Meridian.Mapping`, `Meridian.Mediator`, `Meridian.Mediator.Generators` (analyzer only)
and `Microsoft.Extensions.DependencyInjection`. It uses the public `IMediator` with real service scopes and the
generated mapping methods; calling a handler directly is never counted. Each case is recorded only after all of its
assertions. The runner compares the executed case IDs with the expected set (no missing, duplicate, failed or unexpected
case) and cross-checks the `SUMMARY` marker, the pinned runtime family and a non-JIT runtime
(`RuntimeFeature.IsDynamicCodeSupported == false`) on the expected OS and architecture.

| Case | Observations asserted | osx-arm64 net8/9/10 | linux-x64 net8/9/10 |
| --- | --- | --- | --- |
| `typed-object-query` | typed and object `Send`, both invocation orders; exact generated mapping result; request and token identity; one handler call per send; behavior and validator ran | PASS | PENDING |
| `void-forms` | `IRequestHandler<Q>` and `IRequestHandler<Q, Unit>` through the void, `Unit` and object entries; side-effect counts; token identity | PASS | PENDING |
| `standard-pipeline` | correlation, audit and localized validation: valid vs invalid request, localized error, handler skipped, redacted audit failure message, caller correlation context restored, no exception message/stack in default telemetry | PASS | PENDING |
| `standard-composition` | `IncludeStandardPipeline`, `AddMeridianStandard<T>` and a plain registry registration, repeated: validator and audit run once per send | PASS | PENDING |
| `scoped-construction` | two providers with different singleton markers; no constructor runs during registration/build; scoped dependency reused per scope, disposed exactly once; concurrent sends keep provider/scope ownership | PASS | PENDING |
| `notification-composition` | two registries sharing one notification (repeated registration): each handler once, order, token identity; sequential publisher stops at the first fault | PASS | PENDING |
| `stream-lifetime` | typed/object streams; completion, early break (inner then outer `finally`), re-enumeration, pre-cancel, cancel after first item, enumerator-token forwarding, deferred handler exception; repeated with an `ActivityListener` attached | PASS | PENDING |
| `generated-mapping` | structural conversion, cycle and element identity, depth bound, collection limit, cancellation | PASS | PENDING |
| `strict-failure` | undeclared request (typed and object), notification and stream fail actionably and run nothing; an unrooted open behavior and assembly scanning fail while strict configuration is built | PASS | PENDING |
| `native-runtime` | native only: no dynamic code, not JIT-compiled, expected OS and architecture | PASS | PENDING |
| `native-nonstrict-unknown` | native only: without strict mode an unknown request, notification or stream fails with the same actionable message instead of trying reflection | PASS | PENDING |
| `native-scanning-unsupported` | native only: `RegisterServicesFromAssembly` throws `NotSupportedException` pointing at the generated registry | PASS | PENDING |

The first nine cases also run managed (JIT) inside `eng/verify-packages.ps1` on every pinned SDK, so the fixture is
part of the ordinary all-consumer package gate. `--require-native` adds the last three.

## Not verified on native (no support claim)

These have no native case; they keep their ordinary managed tests and gain no native claim from this work:
concurrent notification publishers (cap and failure/cancellation parity), typed exception handlers/actions, pre/post
processors, marker-matching/skipped pipeline pairs and explicit ordering, caching/retry/authorization/idempotency/
transaction behaviors, the optional FluentValidation, Redis, Entity Framework Core and ASP.NET Core packages, resource-based
localizers, telemetry exporters, collectible/unloadable contexts, arbitrary container factories and any alternate DI container.

## Unsupported on native and their failures

| Usage | Behavior in a native binary | Message |
| --- | --- | --- |
| Request/notification/stream without a generated registration, strict mode | `InvalidOperationException` | `No generated request dispatch for <type>. Declare its closed handler and register the matching generated registry.` |
| Same, strict mode off | `InvalidOperationException` (the legacy reflective wrapper cannot be created) | `No generated request dispatch for <type>. This runtime cannot create handler wrappers at run time (Native AOT): declare its closed handler in a generated registry and register it with AddGeneratedRegistry.` |
| `RegisterServicesFromAssembly*` | strict: `InvalidOperationException` (`Strict generated dispatch cannot scan assemblies.`); non-strict native: `NotSupportedException` | `RegisterServicesFromAssembly needs run-time reflection or code generation, which this runtime does not support (Native AOT). Declare closed handlers, behaviors and processors in a generated registry ([GenerateMediatorRegistry]), register it with AddGeneratedRegistry<TRegistry>() and set RequireGeneratedDispatch = true.` |
| Behaviors, processors or stream behaviors registered outside a generated registry (legacy `AddBehavior`, `AddClosedBehavior`, pre/post processors, non-strict open behaviors) | Unsupported and not exercised natively. The helper that would perform the legacy registration throws `NotSupportedException` when reached; code that calls these APIs may also surface trim warnings at publish (not verified). Strict mode reports the missing declaration instead | same text with the operation named |
| Open generic handlers, inaccessible/ambiguous constructors, malformed expectations | compile-time generator diagnostics (`MERIDIANMED001`-`005`); they cannot be executed natively by design | see the generator README |

On a runtime that supports dynamic code (ordinary JIT runtimes) none of these guards
trigger and legacy behavior, defaults and constructors are unchanged.

## The narrow core changes behind the native rows

Publishing the packaged consumer before any change failed in ILCompiler with these reachable sites (kept in
`artifacts/D4/red.log`): `Mediator.cs` 232/264/268/271/277/284/298 (IL3050, IL2055, IL2067, IL2070: the legacy wrapper
factory), `ServiceCollectionExtensions.cs` 47/142/152/167/188/199/204/208/214/236 and `GeneratedMediatorRegistration.cs`
299 (IL2026, IL2067, IL2070, IL2072, IL2075: assembly scanning and `Type`-based descriptors), and
`MeridianMediatorConfiguration.cs` 478 (IL2070: open behavior validation).

Fixes, each narrow and compatibility-preserving:

* `Mediator` now tests `RuntimeFeature.IsDynamicCodeSupported` before creating the legacy request, notification or
  stream wrapper. The compiler removes the reflective branch in native binaries; the reflective helpers are annotated
  `RequiresUnreferencedCode`/`RequiresDynamicCode`. Dynamic-code runtimes run the same code as before.
* The legacy registration helpers (`RegisterHandlersFromAssembly` callers, `RegisterOpenOrClosedGeneric`,
  `TryAddPipelineBehavior`, `RegisterStreamBehavior`) test the same switch and otherwise throw the actionable
  `NotSupportedException` above.
* `NotificationPublisherType`, `AddOpenBehavior(Type)`, `AddOpenStreamBehavior(Type)` carry
  `DynamicallyAccessedMembers` annotations on the type they accept, so `typeof(...)` arguments keep exactly the members
  that are used.

No blanket `NoWarn`, `UnconditionalSuppressMessage`, `DynamicDependency` or whole-assembly rooting was added. After the
changes every row publishes with zero Meridian ILC/ILLink warnings (`warnings.json` per row). Core compatibility is
covered by `tests/Meridian.Mediator.Tests/DynamicDispatchCompatibilityTests.cs` and the unchanged runtime suites.

## Reproduce

```powershell
./eng/pack-packages.ps1 -CandidateVersion 2.1.4-local.1 -OutputDirectory artifacts/candidate
./eng/run-aot-smoke.ps1 -Combined -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1 `
    -Framework net10.0 -SdkVersion 10.0.300 -Rid osx-arm64        # or -Rid linux-x64 on a real x64 host
./eng/run-aot-smoke.ps1 -MappingOnly -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1 `
    -Framework net10.0 -SdkVersion 10.0.300 -RuntimeIdentifier osx-arm64
```

`-Combined` and `-MappingOnly` are mutually exclusive and neither is implied. Candidate version, pinned SDK/TFM pair
(from `eng/packages.json`), feed and RID stay mandatory. The Linux toolchain needs `clang` and `zlib1g-dev`; macOS needs
the Xcode command line tools. The readable source-reference equivalent is `samples/Meridian.NativeAot`.

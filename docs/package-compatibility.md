# Packaged compatibility and local compiler consumers

A4 verifies an unpublished local candidate against the published **2.1.3** Mapping and Mediator packages. It does not select the next stable release version or authorize publication. `eng/packages.json` is the single package inventory (schema version 1); it includes the core packages and optional adapters. `analyzerPaths` records both the repository-relative producer project and its package-relative asset path. The pack script verifies evaluated analyzer references/TargetPath, archive bytes, dependencies, README/MIT license and symbol assets.

Run from the repository root, with the SDKs in the manifest installed:

```powershell
./eng/pack-packages.ps1 -Configuration Release -OutputDirectory artifacts/packages -CandidateVersion 2.1.4-local.<unique-id>
./eng/verify-packages.ps1 -Feed artifacts/packages -CandidateVersion 2.1.4-local.<unique-id>
# Or select one native compiler lane (SDK 10 is also required for baseline comparison):
./eng/verify-packages.ps1 -Feed artifacts/packages -CandidateVersion 2.1.4-local.<unique-id> -Framework net8.0 -SdkVersion 8.0.421
```

The consumer fixtures use C# 12, warnings as errors, exact package versions and an isolated `global.json` with SDK roll-forward disabled. Isolated source mapping restores Meridian IDs only from the supplied local feed; dependencies come explicitly from NuGet.org. Each restore has a fresh package directory and its nupkg hashes must match the feed. Verification checks the pack-time SHA-256 manifest before building, saves actual SDK/compiler/runtime details, hashes fixture inputs and compares `COMMON` output lines. Legacy aliases use only diagnostic-specific `CS0618` suppression; migrated examples compile without that suppression. No deprecation attributes are introduced in A4.

The tested native hosts are SDK **8.0.421** / compiler 4.11, **9.0.318** / compiler 4.14 and **10.0.300** / compiler 5.6 on macOS arm64. The generator compiles against Roslyn 4.8 APIs; this reference version alone does not prove any untested SDK patch. Core targets stay `net8.0;net9.0;net10.0`; the bundled Mapping analyzer stays `netstandard2.0`, under `analyzers/dotnet/cs`. Test-driver Roslyn remains 5.3. Windows/Linux compiler lanes are configured in CI but have not been executed locally.

Published 2.1.3 bundles a generator referring to Roslyn 5.3 and fails to load on the tested SDK 8 and 9 hosts (`CS9057`). Those failures are retained without suppression. Operational old/new comparison uses SDK 10, while the candidate must pass every selected native host. The SDK 10 baseline verifies nested mapping, null behavior, shallow same-type assignment and an optional constructor called without arguments. Native candidate lanes assert that the constructor retains its default seed 7 while ordinary mutable member mapping still copies source seed 99. Accessible, unambiguous optional/`params` constructors retain legacy `new Destination()` behavior. Candidate-only assertions additionally cover inherited/init DTOs and an accessible `SetsRequiredMembers` constructor; binding source values to constructor parameters remains outside this task.

## API preservation and behavior migration

Both runtime packages pass SDK package API validation against 2.1.3 for all three TFMs without APICompat suppressions. Source fixtures implement the published interfaces without adding new members. `IMapperConfigurationExpression.AllowUndefinedEnumValues` now has a default getter returning `false`. Its default setter permits `false`; setting `true` throws an actionable `NotSupportedException`. A custom implementation must override the property to support the opt-in. The built-in implementation supports both values. This avoids adding an abstract member or silently ignoring a requested opt-in.

Matching API signatures and matching `COMMON` output do not establish complete behavior parity. The candidate includes intentional hardening inherited from the reviewed working tree:

| Behavior | Published 2.1.3 observation | Candidate and migration option |
| --- | --- | --- |
| Missing authorization handler | Request continues | Reject by default; explicitly set `AuthorizationBehaviorOptions.AllowRequestsWithoutAuthorizationHandlers=true` only for a controlled compatibility transition. |
| Cache and invalidation keys | Raw provider keys | Namespaced query keys; use `CacheKeyComposer.Compose<TQuery>` for invalidation. Explicit `CachingBehaviorOptions.UseRequestTypeNamespacedKeys=false` preserves raw writes/removals during migration. |
| Idempotency keys | Raw store keys | Namespaced request keys; explicit `IdempotencyBehaviorOptions.UseRequestTypeNamespacedKeys=false` preserves raw keys. The base store interface remains supported. |
| Undefined numeric-string enum coercion | The exact compiled member fixture rejects `"99"` with `InvalidCastException` | The candidate rejects with `MeridianEnumMappingException`; built-in `AllowUndefinedEnumValues=true` explicitly permits this coercion. This fixture does not establish generalized legacy acceptance across conversion paths. |
| Stack-only exception telemetry | Stack text also contains the exception message | `RecordExceptionStackTrace=true` with `RecordExceptionMessage=false` omits the message. Explicitly enabling both records both. Both default flags remain `false`. |
| Mixed pipeline ordering/duplicate registration | Fixture records `closed,closed,open,handler,open` | Fixture asserts `open,closed,handler,open`: explicit order is honored across open/closed registrations and the duplicated closed behavior is removed. Audit dependencies on duplicate execution/order when upgrading. |

The packaged fixtures assert authorization/cache/invalidation/idempotency/enum opt-outs, telemetry default/full/stack-only behavior and exact candidate mixed order/count. Mapping defaults remain depth 64, collection cap 10000, null collections false and null destination values true. Notification publication and concurrency policies are outside this A4 fixture's behavior comparison; their approved defaults are not changed here.

The supplementary API inventory found zero removed signature entries and additive members only (18 Mapping and 74 Mediator reflection entries per TFM, including record-generated members/accessors). Its detailed inventory and the authoritative SDK API validation logs are local A4 evidence; reflection output is not a replacement for APICompat. Release version, support lifecycle and deprecation eligibility still require their later documented gates.

## Runtime constructor correction (A6)

Runtime mapping now throws `InvalidOperationException` when every public destination constructor has an unresolved required argument. Previously the fallback could invoke a constructor with type defaults, silently using `Guid.Empty`, zero or null for required values. Parameterless constructors, fully resolved constructors, optional arguments and selection of a narrower viable constructor retain their behavior. Supply required arguments with `ForCtorParam`, or configure `ConstructUsing` for intentional default construction. The separate public `ObjectCreator.CreateInstance` fallback remains unchanged; generated constructor binding is covered separately by B3.

## Dependency licenses and build-time tooling (E4b)

Meridian source is MIT. The root `NOTICE` lists the declared direct dependency licenses of every packed package. This section records how private build tooling relates to what is distributed, so no document claims that every package in a build is MIT.

**What is distributed.** Each of the eight packed packages carries the Meridian `LICENSE` (MIT), its README, its own assemblies and, for the two generator packages, one compiled analyzer assembly under `analyzers/dotnet/cs`. No packed package declares a dependency on, or contains a file from, any package of the private analyzer-build closure; the pack-time file and nuspec inventories for candidate `2.1.4-e4b.20261004.1` show an empty intersection.

**What is private.** `Meridian.Mapping.Generators` and `Meridian.Mediator.Generators` build against `Microsoft.CodeAnalysis.CSharp` 4.8.0, `Microsoft.CodeAnalysis.Analyzers` 5.3.0 and `NETStandard.Library` 2.0.3 with `PrivateAssets=all`. The closure (13 components per generator, recorded in the pack evidence `buildTooling` entries with scope `private-analyzer-build`) is restored only on the build machine. Seven of those components declare a modern NuGet license expression. Six declare only a legacy license URL; their licenses were read from the text embedded in each archive and rehashed against the pack evidence:

| Package | Archive SHA-256 | Embedded license | License text SHA-256 | Identified text | Distributed |
| --- | --- | --- | --- | --- | --- |
| Microsoft.NETCore.Platforms 1.1.0 | `15e338d24b5c39b4099389cc612841eb51ff13c07bb4829f97d39b27420e7023` | `dotnet_library_license.txt` | `f1db688d8481c91a452fabcea5060a23da9ea5088329b58c478a040e2e426297` | Microsoft .NET Library license terms (**not MIT**) | No (private) |
| NETStandard.Library 2.0.3 | `3eb87644f79bcffb3c0331dbdac3c7837265f2cdf58a7bfd93e431776f77c9ba` | `LICENSE.TXT` | `fc95de1436a321aadbfafd21f8a8506a2f4678bfc003bd806d6b59d3088efadc` | MIT | No (private) |
| System.Buffers 4.5.1 | `c30b3dd2c7e2f4cee4b823d692fd42118309b42ab1f5007f923d329a5b0d6b12` | `LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` | MIT | No (private) |
| System.Memory 4.5.5 | `10f43da352a29fb2b3188e4edd4dcf5100194c8b526e4f61fe2e2b5623775a22` | `LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` | MIT | No (private) |
| System.Numerics.Vectors 4.4.0 | `6ae5d02b67e52ff2699c1feb11c01c526e2f60c09830432258e0809486aabb65` | `LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` | MIT | No (private) |
| System.Threading.Tasks.Extensions 4.5.4 | `a304a963cc0796c5179f9c6b7d8022bbce3b2fa7c029eb6196f631f7b462d678` | `LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` | MIT | No (private) |

**Disposition of `Microsoft.NETCore.Platforms` 1.1.0.** It is a runtime-identifier catalog (`runtime.json`, no assembly) pulled in transitively by `NETStandard.Library` 2.0.3. Its license requires acceptance and is the Microsoft .NET Library agreement, not MIT. Because it is private build tooling, no obligation passes to consumers of the Meridian packages and no Meridian file is relicensed; the nuspec metadata of its archive is raw upstream metadata and is left untouched. The obligation is on whoever restores it: redistributing a build environment, container image or package cache that contains the archive is a separate redistribution of that package under its own terms. If a future package starts to ship or depend on any of these packages, this table and `NOTICE` must be revisited before release. No script enforces that yet; comparing every packed nuspec and file list with the private closure is a recommended candidate-verification check (E4c). The six archives are retained unmodified and their original signatures and legacy metadata are not rewritten or relabeled.

Limits: this is an engineering record of what the packages and the restored archives contain, not a legal compliance opinion, and it covers the candidate named above. The packed Meridian packages themselves declare the MIT license expression in their nuspec.

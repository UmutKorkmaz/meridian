# Packaged compatibility and local compiler consumers

A4 verifies an unpublished local candidate against the published **2.1.3** Mapping and Mediator packages. It does not select the next stable release version or authorize publication. `eng/packages.json` is the single package inventory (schema version 1); adapters will extend this inventory as introduced. `analyzerPaths` records both the repository-relative producer project and its package-relative asset path. The pack script verifies evaluated analyzer references/TargetPath, archive bytes, dependencies, README/MIT license and symbol assets.

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

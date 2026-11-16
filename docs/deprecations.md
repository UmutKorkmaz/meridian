# Deprecation ledger

This page is the single authoritative record of Meridian's deprecation candidates. It holds one machine-readable JSON
block, between the `meridian-deprecation-ledger` markers below, plus explanatory prose. `eng/verify-deprecations.ps1`
(implemented by `eng/deprecation_policy.py`) parses that block strictly and validates it against the repository source,
the real UTC clock and the lifecycle rules on this page. Release preparation and promotion run the same validator.

**Current state: every entry is retained and supported.** Nothing on this page is deprecated, obsolete, scheduled for
removal or about to change its default. The entries record *preferred* alternatives and the evidence that would be needed
before any diagnostic, `Obsolete` warning, removal or default flip could even be reviewed.

## What stays supported

All four old paths keep working exactly as they do today:

* All three `AddAutoMapper` overloads (`params Assembly[]`, `params Type[]`, `Action<IMapperConfigurationExpression>`)
  keep forwarding to the matching `AddMeridianMapping` overload, with the same lifetimes.
* Generated mappers keep `GeneratedMappingMode.Legacy` as the default. Generated method names, null returns and shallow
  copy semantics are unchanged. `MERIDIANGEN002` stays informational in Legacy mode.
* `CachingBehaviorOptions.UseRequestTypeNamespacedKeys = false` stays a supported explicit opt-out for raw cache keys and
  raw invalidation keys. Meridian never rewrites, flushes or migrates cache provider data.
* `IIdempotencyStore` stays a supported public interface. The richer `IAtomicIdempotencyStore` and the leased contract
  inherit from it, and custom implementations of the base interface keep working.

These APIs are explicitly **not** deprecation candidates: `AddClosedBehavior`, `AddClosedStreamBehavior`, runtime
assembly scanning (`RegisterServicesFromAssembly`), open behaviors, converters/value resolvers, reverse maps, custom
providers and streaming. Typed examples elsewhere in the docs do not make explicit dynamic registration obsolete.

## The four entries

| Entry | Preferred path | Status |
| --- | --- | --- |
| `alias-addautomapper` | `AddMeridianMapping` with the same argument shape ([migration guide](migrate-from-automapper.md)) | supported; `MERIDIANDEP001` reserved for a future eligible alias diagnostic, not emitted |
| `generated-legacy-skipped-members` | explicit `Mode = GeneratedMappingMode.Structural` with supported member/conversion/ignore configuration ([generated mapping](generated-mapping.md)) | supported; existing `MERIDIANGEN002` behavior unchanged |
| `cache-raw-keys` | default namespaced keys via `CacheKeyComposer.Compose<TQuery>`, plus trusted provider scope decoration where needed ([integrations](integrations.md)) | supported; no diagnostic ID reserved |
| `idempotency-nonatomic-concurrency` | `IAtomicIdempotencyStore` (or the documented leased adapter) for concurrent workloads ([compatibility policy](compatibility-policy.md)) | supported; the deprecated thing would be a *usage pattern*, never the base interface; no diagnostic ID reserved |

Only `MERIDIANDEP001` is reserved, for the DI alias migration. The validator checks that no source emits it while the
ledger says it is not emitted. Behavior guidance would use distinct diagnostic IDs, chosen only after a collision check
against the actual source.

## Lifecycle rules

These are release validation rules, not a schedule. Nothing runs on a timer, and no date triggers a code change.

1. **Guidance (now).** Document preferences, limitations and runnable migrations. Record missing facts as `null`; never
   guess them.
2. **New diagnostic or non-error `Obsolete` warning.** All of the following are needed:
   * the replacement is publicly stable, with a *reviewed* availability anchor;
   * a public transition announcement already exists (with its content hash);
   * the old/new migration fixtures are accepted and consumer impact is reviewed;
   * the version is a minor or a major, never a patch;
   * `IsError = false`, with a reserved diagnostic ID;
   * a trusted authorization for that exact action.
3. **Removal or default flip.** Everything in step 2, **and** all of the following:
   * **six calendar months** from `windowStart = max(reviewed stable-replacement availability, public announcement)`.
     This counts calendar months at the same instant of day, clamping the month end; it is never 180/182/183 days.
   * **Two qualifying higher stable minor lines** in the major of the first stable release that carries the notice.
     They must be released after it and retain both paths. Patches, prereleases, build metadata and majors count zero.
   * A separately reviewed **major** above the notice major, an adoption/review decision, and a trusted exact-action
     authorization.

   Eligibility only permits that review; the change itself is a separate, explicitly authorized action. If a notice
   first ships in a future 3.0, then 3.1 and 3.2 are the candidate qualifying lines, and the earliest removal is a
   reviewed 4.0 or later.

Self-authored fields never authorize anything. Authorization must come from a trusted verifier bound to this exact ledger
content, entry, action, version, affected surface and release candidate. The candidate must have an exact SHA-256.
The trusted service must separately accept the bound migration results, decision reference, replacement artifacts,
announcement and retained-API evidence; accepting an action record alone does not accept those artifacts.
This repository has no trusted verifier, so
every gated action fails closed on the command line. The command line always uses the real UTC clock; there is no
"as of" option.

## Evidence recorded today

* **Meridian.Mapping 2.1.3** (`2026-06-23T16:22:57.407Z`) and **Meridian.Mediator 2.1.3** (`2026-06-23T16:22:56.860Z`)
  are the published stable baselines. The dates come from the NuGet registration documents read 2026-10-23; their
  response hashes are in the block below.
* The 2.1.3 Mapping package (SHA-256 `ae4488c4aa95c3b705b076d2309443ef120896f619a89ae5b589bf51ca7c4c69`) exposes all
  three `AddAutoMapper` aliases and all three `AddMeridianMapping` replacements without `Obsolete`. This is recorded as
  `verifiedAvailableBy`, **not** as the first stable replacement, and is **not reviewed** as a clock anchor.
* **No public transition announcement exists** for any entry. That means no deprecation window has started, and no
  qualifying minor line has been counted.
* The Structural mode, `CacheKeyComposer` namespacing and the atomic/leased idempotency contracts are not in any
  published stable release yet, so their stable-replacement facts are pending.
* **Old/new consumer acceptance is candidate-specific.** `tests/consumers/Meridian.DeprecationConsumer` (variants
  `legacy` and `migrated`, compiled separately) asserts matching intended values. A local execution against one trial
  candidate does not accept another: retain the exact package manifest and E4-validated execution proof for the final
  candidate. No `Obsolete` warning is emitted, so no warning suppression is used or claimed.

Retained registration bytes are checked for package identities, listed stable versions and publication instants,
then compared with the complete recorded projection. A matching response hash alone cannot verify invented releases.
The parser currently accepts complete inline registration indexes; external pages fail closed until their individual
bytes and hashes have a reviewed evidence format. Registration metadata cannot establish that either API path was
retained: that requires separately accepted artifact and execution evidence.

<!-- meridian-deprecation-ledger:begin -->
```json
{
  "schema": "meridian-deprecation-ledger/1",
  "policyVersion": 1,
  "policy": {
    "windowStart": "max(reviewed stable-replacement availability, public announcement)",
    "calendarMonths": 6,
    "qualifyingMinorLines": 2,
    "minorCounting": "distinct higher stable minor lines in the notice major, published after the first stable release carrying the notice, retaining both paths; patches, prereleases, build metadata and majors count zero",
    "warningGate": "reviewed stable replacement, prior public announcement, accepted migration evidence, review, minor or major version, IsError=false, trusted exact-action authorization",
    "removalGate": "warning gate plus six calendar months and two qualifying minor lines, a separately reviewed major above the notice major, review/adoption decision and trusted exact-action authorization",
    "clock": "real UTC clock on every command line and release entry point; injected clocks exist only in isolated tests",
    "authority": "a trusted verifier bound to ledger content, entry, action, version, surface and candidate; self-authored fields are never authority"
  },
  "retainedSupported": [
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.AddClosedBehavior(System.Type,System.Type,System.Int32)",
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.AddClosedStreamBehavior(System.Type,System.Type)",
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.RegisterServicesFromAssembly(System.Reflection.Assembly)",
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.AddOpenBehavior(System.Type,System.Int32)",
    "M:Meridian.Mediator.Extensions.MeridianMediatorConfiguration.AddOpenStreamBehavior(System.Type)",
    "T:Meridian.Mediator.Behaviors.IIdempotencyStore",
    "T:Meridian.Mapping.Converters.ITypeConverter`2",
    "T:Meridian.Mapping.Converters.IValueResolver`3",
    "T:Meridian.Mediator.Behaviors.ICacheProvider"
  ],
  "releaseHistory": {
    "packages": [
      {
        "id": "Meridian.Mapping",
        "authority": "https://api.nuget.org/v3/registration5-gz-semver2/meridian.mapping/index.json",
        "retrievedAt": "2026-10-23T10:43:39.681445Z",
        "responseSha256": "c55f67510bb4f04c2da14b0997dbbde91e9155ac93793a6ad6efc6baad56968d",
        "stableReleases": [
          {"version": "1.0.0", "publishedAt": "2026-03-23T09:41:45.69Z", "retainsBothPaths": null},
          {"version": "1.0.1", "publishedAt": "2026-03-23T10:04:41.55Z", "retainsBothPaths": null},
          {"version": "2.1.0", "publishedAt": "2026-04-16T06:18:25.977Z", "retainsBothPaths": null},
          {"version": "2.1.1", "publishedAt": "2026-04-16T11:56:32.557Z", "retainsBothPaths": null},
          {"version": "2.1.2", "publishedAt": "2026-04-17T12:24:28.12Z", "retainsBothPaths": null},
          {"version": "2.1.3", "publishedAt": "2026-06-23T16:22:57.407Z", "retainsBothPaths": null}
        ]
      },
      {
        "id": "Meridian.Mediator",
        "authority": "https://api.nuget.org/v3/registration5-gz-semver2/meridian.mediator/index.json",
        "retrievedAt": "2026-10-23T10:43:39.694789Z",
        "responseSha256": "024ca3a93264a621ac1f3e5b68197f5ae77de64d383ad4374cd068264e7b7194",
        "stableReleases": [
          {"version": "1.0.0", "publishedAt": "2026-03-23T09:41:05.4Z", "retainsBothPaths": null},
          {"version": "1.0.1", "publishedAt": "2026-03-23T10:04:41.547Z", "retainsBothPaths": null},
          {"version": "2.0.0", "publishedAt": "2026-04-15T13:23:15.847Z", "retainsBothPaths": null},
          {"version": "2.0.1", "publishedAt": "2026-04-15T13:43:01.887Z", "retainsBothPaths": null},
          {"version": "2.1.1", "publishedAt": "2026-04-16T11:56:32.917Z", "retainsBothPaths": null},
          {"version": "2.1.2", "publishedAt": "2026-04-17T12:23:14.637Z", "retainsBothPaths": null},
          {"version": "2.1.3", "publishedAt": "2026-06-23T16:22:56.86Z", "retainsBothPaths": null}
        ]
      }
    ],
    "limit": "Registration metadata only (published/listed): no artifact contents, API retention, announcement or eligibility is asserted by this history. retainsBothPaths is null until a release's contents are verified."
  },
  "entries": [
    {
      "id": "alias-addautomapper",
      "title": "AddAutoMapper DI aliases",
      "kind": "api",
      "package": "Meridian.Mapping",
      "targets": [
        "M:Meridian.Mapping.Extensions.ServiceCollectionExtensions.AddAutoMapper(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Reflection.Assembly[])",
        "M:Meridian.Mapping.Extensions.ServiceCollectionExtensions.AddAutoMapper(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Type[])",
        "M:Meridian.Mapping.Extensions.ServiceCollectionExtensions.AddAutoMapper(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Meridian.Mapping.IMapperConfigurationExpression})"
      ],
      "replacements": [
        "M:Meridian.Mapping.Extensions.ServiceCollectionExtensions.AddMeridianMapping(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Reflection.Assembly[])",
        "M:Meridian.Mapping.Extensions.ServiceCollectionExtensions.AddMeridianMapping(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Type[])",
        "M:Meridian.Mapping.Extensions.ServiceCollectionExtensions.AddMeridianMapping(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Meridian.Mapping.IMapperConfigurationExpression})"
      ],
      "retainedInvariants": [
        "every alias overload forwards to the matching AddMeridianMapping overload",
        "IConfigurationProvider singleton, IMapper scoped, scanned converters/resolvers transient",
        "the marker overload deduplicates assemblies; the Action overload does not scan converters"
      ],
      "migration": {"guide": "docs/migrate-from-automapper.md", "fixtures": ["alias-assembly-array", "alias-type-array", "alias-action"]},
      "diagnostic": {"reservedId": "MERIDIANDEP001", "status": "reserved-not-emitted", "existing": null},
      "facts": {
        "firstStableReplacement": null,
        "verifiedAvailableBy": {
          "package": "Meridian.Mapping",
          "version": "2.1.3",
          "publishedAt": "2026-06-23T16:22:57.407Z",
          "retrievedAt": "2026-10-23T10:43:39.681445Z",
          "authority": "https://api.nuget.org/v3/registration5-gz-semver2/meridian.mapping/index.json",
          "responseSha256": "c55f67510bb4f04c2da14b0997dbbde91e9155ac93793a6ad6efc6baad56968d",
          "artifactSha256": "ae4488c4aa95c3b705b076d2309443ef120896f619a89ae5b589bf51ca7c4c69",
          "clockAnchorReviewed": false,
          "note": "replacement API presence in the published 2.1.3 package (reflection of DLL 2d4a396b156a0e713630aa6a128b71f65d71bf95fc5f5d4d29e33a4bd3ff3053); not the first version and not a reviewed clock anchor"
        },
        "announcement": null,
        "noticeRelease": null,
        "migrationEvidence": null,
        "diagnosticIntroducedIn": null,
        "obsoleteShippedIn": null
      },
      "proposed": {"action": "retain", "version": null, "isError": null, "affectedSurface": null},
      "review": null,
      "supportStatus": "supported"
    },
    {
      "id": "generated-legacy-skipped-members",
      "title": "Legacy generated mapping that silently skips unmapped members",
      "kind": "behavior",
      "package": "Meridian.Mapping",
      "targets": [
        "generated:GenerateMapperAttribute.Mode=GeneratedMappingMode.Legacy (default) skipped destination members"
      ],
      "replacements": [
        "generated:GenerateMapperAttribute.Mode=GeneratedMappingMode.Structural with explicit member/conversion/ignore configuration"
      ],
      "retainedInvariants": [
        "Legacy stays the default mode",
        "generated method names, null return and shallow reference semantics are unchanged",
        "MERIDIANGEN002 stays Info in Legacy and Error only for Structural unmapped members"
      ],
      "migration": {"guide": "docs/generated-mapping.md", "fixtures": ["generated-legacy-default", "generated-structural-opt-in"]},
      "diagnostic": {"reservedId": null, "status": "none", "existing": "MERIDIANGEN002"},
      "facts": {
        "firstStableReplacement": null,
        "verifiedAvailableBy": null,
        "announcement": null,
        "noticeRelease": null,
        "migrationEvidence": null,
        "diagnosticIntroducedIn": null,
        "obsoleteShippedIn": null
      },
      "proposed": {"action": "retain", "version": null, "isError": null, "affectedSurface": null},
      "review": null,
      "supportStatus": "supported"
    },
    {
      "id": "cache-raw-keys",
      "title": "Raw (non-namespaced) cache and invalidation keys",
      "kind": "behavior",
      "package": "Meridian.Mediator",
      "targets": [
        "P:Meridian.Mediator.Behaviors.CachingBehaviorOptions.UseRequestTypeNamespacedKeys=false (explicit raw-key opt-out)"
      ],
      "replacements": [
        "M:Meridian.Mediator.Behaviors.CacheKeyComposer.Compose``1(System.String)",
        "M:Meridian.Mediator.Caching.ServiceCollectionExtensions.AddMeridianCacheScope``1(Microsoft.Extensions.DependencyInjection.IServiceCollection)"
      ],
      "retainedInvariants": [
        "UseRequestTypeNamespacedKeys=false keeps raw logical keys for caching and invalidation",
        "with the default true, raw invalidation keys are rejected before the handler executes",
        "Meridian never rewrites, flushes or migrates cache provider data; scope decoration is applied once and is not access control"
      ],
      "migration": {"guide": "docs/integrations.md", "fixtures": ["cache-raw-and-composed", "cache-scope-schema"]},
      "diagnostic": {"reservedId": null, "status": "none", "existing": null},
      "facts": {
        "firstStableReplacement": null,
        "verifiedAvailableBy": null,
        "announcement": null,
        "noticeRelease": null,
        "migrationEvidence": null,
        "diagnosticIntroducedIn": null,
        "obsoleteShippedIn": null
      },
      "proposed": {"action": "retain", "version": null, "isError": null, "affectedSurface": null},
      "review": null,
      "supportStatus": "supported"
    },
    {
      "id": "idempotency-nonatomic-concurrency",
      "title": "Non-atomic check-then-store idempotency under concurrent workloads",
      "kind": "usage-pattern",
      "package": "Meridian.Mediator",
      "targets": [
        "usage:IIdempotencyStore check-then-store relied on for concurrent duplicate suppression"
      ],
      "replacements": [
        "T:Meridian.Mediator.Behaviors.IAtomicIdempotencyStore",
        "T:Meridian.Mediator.Behaviors.ILeasedIdempotencyStore"
      ],
      "retainedInvariants": [
        "IIdempotencyStore stays public and supported; IAtomicIdempotencyStore and ILeasedIdempotencyStore inherit it",
        "legacy sequential replay of non-null responses keeps working; legacy successful null responses are not stored",
        "no unconditional warning on a base-typed field that could hold a richer store"
      ],
      "migration": {"guide": "docs/compatibility-policy.md", "fixtures": ["idempotency-base-and-atomic"]},
      "diagnostic": {"reservedId": null, "status": "none", "existing": null},
      "facts": {
        "firstStableReplacement": null,
        "verifiedAvailableBy": null,
        "announcement": null,
        "noticeRelease": null,
        "migrationEvidence": null,
        "diagnosticIntroducedIn": null,
        "obsoleteShippedIn": null
      },
      "proposed": {"action": "retain", "version": null, "isError": null, "affectedSurface": null},
      "review": null,
      "supportStatus": "supported"
    }
  ]
}
```
<!-- meridian-deprecation-ledger:end -->

## Validating the ledger

```bash
pwsh eng/verify-deprecations.ps1 -Output artifacts/deprecation-validation.json
# equivalently
python3 eng/deprecation_policy.py validate --output artifacts/deprecation-validation.json
```

Exit codes: 0 PASS, 1 FAIL (an ineligible gated action, invalid metadata or a broken retained contract), 2 invalid
input. The report (`meridian-deprecation-validation/1`) records:

* the checked UTC time and the ledger file and content hashes;
* each entry's proposed action and state (`retain_pass`, `diagnostic_eligible`, `diagnostic_ineligible`,
  `removal_eligible`, `removal_ineligible`, `invalid_metadata`), its failed prerequisites, and the derived window start,
  calendar floor and qualifying minor lines;
* the retained-contract source check;
* the old/new semantic comparison status (`pending` without matching rows, `matched` only when every supplied lane has
  both variants and all nine declared fixtures, otherwise `failed`). Supplied rows must have unique execution identities,
  exact package and SDK versions, integer exit zero and matching complete `COMMON` contract lines.

The semantic comparator always records `provenanceVerified: false`: equal JSON rows do not prove execution. E4 must
separately validate the candidate, source, package bytes, compiled consumer and actual runtime execution before a release
can rely on a `matched` comparison. The source attribute guard is also bounded; actual warnings-as-errors consumer builds
remain necessary to verify C# semantics.

Release preparation passes `--candidate` with the immutable, once-packed `package-manifest.<version>.json`, and
`--consumer-results` with one complete package-consumer lane already validated by E4. The report records both exact
file hashes and `candidateKind: package-manifest`. E4 separately requires every policy consumer lane and checks its
source, compiler, runtime, binary and restored package bytes. A single lifecycle comparison does not replace that coverage.

The enclosing E5 `candidate.json` includes this lifecycle report and its raw evidence. Its approval digest binds the
whole bundle, while the lifecycle leaf binds the immutable package manifest. These are separate identities: the leaf
cannot hash an enclosing manifest that itself contains the leaf hash. Promotion re-derives lifecycle/source/semantic
facts from the retained bytes before relying on the report. Neither digest supplies a trusted authorization verifier.

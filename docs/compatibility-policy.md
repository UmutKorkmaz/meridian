# Compatibility Policy

**Audience:** library consumers and contributors  
**Applies to:** `Meridian.Mapping`, `Meridian.Mediator` (and the Mapping source generator shipped as an analyzer), and
the optional `Meridian.Mediator.*` packages once they ship a stable version

Meridian follows [Semantic Versioning](https://semver.org/).

## What we consider breaking

- Removing or renaming public types/members
- Changing method signatures or generic constraints on public APIs
- Changing **default** runtime outcomes in ways that break working apps
  (for example lowering safety caps, flipping telemetry privacy defaults to more verbose, or changing last-write-wins dictionary semantics again)
- Removing published compatibility shims without a major version
  (for example `AddAutoMapper` aliases on the Mapping DI extensions)
- Changing the names or assignment semantics of existing generated mapping methods, or making Structural generation
  the default

## What we consider safe (minor / patch)

- Performance improvements that preserve observable results
- Bug fixes that restore documented behavior
- New optional APIs, attributes, behaviors, or packages
- Documentation, samples, benchmarks, and CI
- Internal refactors behind existing public surfaces
- Security hardening that only affects attacker-shaped inputs when an opt-out remains available

## Support boundaries

| Area | Boundary | Verified so far |
| --- | --- | --- |
| Runtime packages | `net8.0`, `net9.0`, `net10.0` (no silent target removal in a minor release) | All three runtimes on macOS arm64; Windows and Linux runtime lanes PENDING |
| Analyzers | `netstandard2.0`, Roslyn 4.8 API floor | Compiler consumers on SDK 8.0.421, 9.0.318 and 10.0.300 with C# 12 (macOS arm64) |
| .NET 11 | Optional, non-blocking preview **sample** lane; not a package target | Not part of any support claim |
| Native AOT | Only the RID/TFM rows marked PASS in [Native AOT](native-aot.md) | osx-arm64 net8/9/10; linux-x64 PENDING |
| Optional backends | The dependency minimums in [Optional integrations](integrations.md) | As listed per package |

A combination that has not been executed is unverified, not supported. The [capability ledger](capabilities.md)
records the evidence for every advertised feature. Retiring a target framework is a separately reviewed
major-version/support-policy decision.

## Safety defaults (stable contract)

Since 2.1.x, Mapping applies:

| Setting | Default | Consumer opt-out |
| ------- | ------- | ---------------- |
| `DefaultMaxDepth` | `64` | Raise explicitly on configuration (positive values only; there is no disabled setting) |
| `DefaultMaxCollectionItems` | `10_000` | Raise explicitly (positive values only; there is no disabled setting); oversized inputs throw `MeridianMappingLimitException` |
| `AllowNullCollections` | `false` | Set explicitly |
| `AllowNullDestinationValues` | `true` | Set explicitly |
| `AllowUndefinedEnumValues` | `false` | Set `true` explicitly to accept undefined numeric enum values |

These defaults will not be tightened further on a minor release without a security advisory and clear upgrade notes.

<a id="telemetry-privacy"></a>
## Telemetry privacy (stable contract)

`MediatorTelemetryOptions` defaults:

- `RecordExceptionMessage = false`
- `RecordExceptionStackTrace = false`

Consumers opt in explicitly. Logging/audit behaviors respect these flags. We will not re-enable sensitive detail by
default on a minor release. Notification publication stays sequential by default, and the concurrent publishers keep
their default cap of 16 active handlers. Mediator metrics are opt-in (`MediatorMetricsOptions.Enabled = false`).

<a id="deprecation-window"></a>
## Deprecations

A replacement ships first, is packaged, migration-tested and released as stable. Only then can guidance prefer it,
and only after that can an announced, non-error `Obsolete` diagnostic be added. The old API keeps working for at least
two minor releases **and** six months after the stable replacement, whichever is later; removal happens only in a
reviewed major release. No current API is marked obsolete; the `AddAutoMapper` aliases, `AddClosedBehavior`,
`AddClosedStreamBehavior`, runtime scanning and the base `IIdempotencyStore` remain supported.

## Package validation

Packable projects enable `EnablePackageValidation` so unintentional public API surface drift fails packaging. The core
packages validate against the published 2.1.3 baseline. Contributors should treat unexpected pack validation failures
as breaking-change signals.

## Analyzer warnings do not justify API changes

A compiler or analyzer warning is never, by itself, a reason to rename a public type or parameter, change a signature,
switch a culture, or change how a log call is invoked. Behavior-neutral internal fixes are made; every public-surface
warning is retained with a member-specific written reason, and the remaining set is held by a ratchet. See
[warning dispositions](warning-dispositions.md).

## Reporting regressions

If an upgrade within the same major version breaks a previously supported scenario:

1. File a bug with a minimal repro (or a private security report if sensitive).
2. See [SECURITY.md](https://github.com/UmutKorkmaz/meridian/blob/main/SECURITY.md) for vulnerability reporting.
3. Check the [CHANGELOG](https://github.com/UmutKorkmaz/meridian/blob/main/CHANGELOG.md) for intentional behavioral
   notes (especially 2.1.x security defaults).

## Behavior failure composition (reliability candidate)

A single handler/provider failure retains its exception type, instance, and stack. If claim release, rollback, transaction disposal, or failure-audit recording also fails, callers receive an `AggregateException` containing the original failures in execution order. Inspect `InnerExceptions` rather than assuming the cleanup error replaces the handler error.

Claim release, rollback, and failure-audit recording receive an independent five-second cancellation budget. Caller cancellation does not cancel this cleanup budget. Providers must honor the token; Meridian awaits their completion and does not abandon in-flight work. `IAsyncDisposable.DisposeAsync` has no token, so transaction providers must bound disposal themselves.

Meridian's retry behavior excludes cancellation/nontransient failures even inside exception wrappers and aggregates. Completion/store/commit/disposal or success-audit errors after a successful handler suppress Meridian retries using exception-local metadata. Failed cleanup also suppresses retries. Reusing the same marked exception object remains conservative; create a new exception for a separate failure. A custom exception with read-only or inaccessible `Data` is conservatively excluded from retry. Custom retry loops outside Meridian must apply their own uncertainty policy.

An atomic provider must implement claim/check/completion transitions atomically and durably at its required consistency level. A failed completion or inner commit retains the claim for provider-specific reconciliation; Meridian cannot determine whether external effects committed. The legacy check-then-store contract permits concurrent duplicate execution and does not record null responses. Neither contract promises exactly-once execution. Leased claims with renewal and explicit reconciliation are provided by the optional `Meridian.Mediator.Redis` package (candidate; see [Optional integrations](integrations.md#redis)). Register stores and transaction providers with lifetimes compatible with their scoped dependencies.

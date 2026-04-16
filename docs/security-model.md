# Meridian Security Model

**Audience:** library maintainers, security researchers, and adopters
evaluating Meridian for use in regulated environments.
**Last reviewed:** 2026-04-16

This document describes what Meridian defends against, what it does not,
and how its defences are enforced. For vulnerability reporting, see
[SECURITY.md](../SECURITY.md).

## Scope

Meridian ships two packages:

| Package               | Role                                       |
| --------------------- | ------------------------------------------ |
| `Meridian.Mapping`    | Reflection/source-gen object-to-object mapper |
| `Meridian.Mediator`   | In-process CQRS mediator with pipeline     |

Both are loaded in-process inside the consuming application. They do not
open sockets, spawn processes, or reach outside the host runtime. The
threat surface is therefore the **set of values the application passes
to the mapper or mediator**.

## Threat Model

### Assumptions

1. The application registers its own `TypeMap`, `Profile`, and
   `IRequestHandler` types at startup. Attackers do not get to modify
   the registered configuration at runtime.
2. The application already authenticates and authorises inbound
   requests before calling Meridian. Meridian is a downstream component
   after the application's boundary has validated identity.
3. The CLR runtime, the .NET BCL, and the operating system are trusted.
4. NuGet package integrity is verifiable: every public release carries
   a Sigstore build-provenance attestation and a CycloneDX SBOM. Tampering
   with the package contents between build and install would invalidate
   the attestation.

### Attacker capabilities

Under the assumptions above, the worst realistic attacker can:

- Supply arbitrary deserialised content as a source object for a map
  (e.g. a deeply nested JSON payload deserialised into a CLR graph and
  then passed to `IMapper.Map`).
- Trigger the same mapping repeatedly to amplify costs.
- Send arbitrary values into `IRequestHandler` invocations via the
  mediator.

### Out-of-scope attackers

- Anyone with the ability to modify the hosting process's memory or
  registered types. That is a host-level compromise, not a Meridian
  concern.
- Anyone with the ability to push malicious packages under the Meridian
  names. Covered by Sigstore attestations + signed NuGet packages, but
  the enforcement is on the consumer side (verify before installing).

## Defences

### DoS via uncontrolled recursion (CVE class — CVE-2026-32933)

**Risk**: a deeply nested source graph maps into a deeply nested
destination graph. Recursion depth of ~25 000 frames exceeds the default
.NET stack size, triggering a `StackOverflowException`, which cannot be
caught — the process terminates.

**Mitigation**:

- `MapperConfigurationExpression.DefaultMaxDepth` defaults to **64**,
  matching `System.Text.Json.JsonSerializerOptions.MaxDepth` and
  `Newtonsoft.Json` v13+.
- The cap is enforced in `MappingEngine.MapWithTypeMap` before either
  the interpreter or the fast path executes. Exceeding the depth
  produces `default(TDestination)` with the cap applied by default.
- The fast path (`FastPathCompiler`) threads a `ResolutionContext`
  through every nested mapping call and calls `IncrementDepth()` on
  entry, so the cap fires even when the nested type also has a fast
  path.
- Collection-item recursion now increments depth on both
  `IMapper.Map(source)` and `IMapper.Map(source, destination)`,
  including `.UseDestinationValue()` collection reuse. The
  existing-destination path therefore obeys the same recursion bound as
  the new-destination path.
- Regression coverage: `DosRegressionTests.Attacker_Crafted_100k_Deep_Graph_*`
  plus `PropertyBasedTests.Arbitrary_Depth_Never_Stack_Overflows` and
  `MapToExistingDosTests.UseDestinationValue_SelfReferential_Collections_Honor_MaxDepth`.

**Non-goal**: we do not promise that an attacker-supplied graph of
1 000 000 levels will return quickly. We promise the process will not
crash. The depth cap is enforced via interpretation of the first 64
levels; the remaining levels in the attacker's graph are simply not
visited.

### DoS via unbounded allocation

**Risk**: an attacker supplies a collection with millions of items as
the source of a mapping. Without a cap, Meridian would allocate a
corresponding destination collection, potentially exhausting memory.

**Mitigation**:

- `MapperConfigurationExpression.DefaultMaxCollectionItems` defaults
  to **10 000**. Exceeding this throws
  `MeridianMappingLimitException` before any destination allocation.
- For sources implementing `System.Collections.ICollection` or
  `ICollection<T>`, the count is checked via the `Count` property
  before enumeration. For pure `IEnumerable<T>` streams, the count is
  checked mid-enumeration at `maxItems + 1` to avoid pulling the
  entire attacker stream.
- The same width cap now applies to `IMapper.Map(source, destination)`
  and `.UseDestinationValue()` collection reuse. `MapCollection` and
  `TryMapCollectionOntoExisting` share one enforcement helper so the
  two enumeration paths cannot drift silently.
- Regression coverage: `DosRegressionTests.Attacker_Crafted_1M_Item_Collection_*`
  and `PropertyBasedTests.Oversized_Collection_Always_Throws`, plus
  `MapToExistingDosTests.MapToExisting_Oversized_*`.

### Reflection-based type confusion

**Risk**: a `ValueResolverType`, `ITypeConverter`, or `MemberValueResolverType`
registered via configuration points to a type the attacker chose (e.g.
via deserialised configuration). Meridian would instantiate it via
`Activator.CreateInstance` and call a method named `Convert` or `Resolve`
on it.

**Current posture**:

- Meridian does not read configuration from untrusted sources. The
  `TypeConverterType` / `ValueResolverType` / `MemberValueResolverType`
  fields are populated at application startup by the code that registers
  the mapping configuration.
- The `Activator.CreateInstance` fallback in
  `MappingEngine.ResolveService` only activates for types the
  configuration code chose; it is not a generic dispatch from runtime
  input.
- Constructor mapping no longer treats unresolved parameters as
  silently valid bindings. `ObjectCreator.CreateWithConstructorMapping`
  only selects the widest constructor when every parameter is satisfied
  by explicit configuration, source-name matching, or a C# optional
  default value. Otherwise it falls back to a narrower constructor or
  default construction, preserving destination invariants more
  predictably.

**Non-goal**: we do not defend against an application that lets
attackers register resolver types at runtime. That is a configuration
integrity problem, not a mapper concern.

### Info disclosure via telemetry

**Risk**: request or notification failures propagate sensitive context
into shared tracing backends via `exception.message` or
`exception.stacktrace`.

**Mitigation**:

- `Meridian.Mediator` always records exception type information, but
  full stack traces are now opt-in through
  `MediatorTelemetryOptions.RecordExceptionStackTrace`.
- Exception-message recording is independently controllable via
  `MediatorTelemetryOptions.RecordExceptionMessage`. When disabled,
  mediator spans still report `ActivityStatusCode.Error` without a
  status description.
- Regression coverage: `TelemetryPrivacyTests`.

### DoS via retry amplification

**Risk**: attacker-triggered failures can amplify CPU and latency if a
request retries indefinitely or computes a backoff that overflows.

**Mitigation**:

- `RetryBehavior` clamps per-request retry counts to
  `RetryPolicy.MaxRetriesCap` (default `10`).
- Exponential backoff saturates at `RetryPolicy.MaxBackoff` (default
  `TimeSpan.FromMinutes(5)`), preventing `TimeSpan` overflow.
- Cancellation is not retried, and the default retry gate rejects
  `ArgumentException`, `ValidationException`, and
  `UnauthorizedAccessException` as non-transient.
- Regression coverage: `RetryBehaviorHardeningTests`.

### DoS via notification fan-out

**Risk**: a large handler set can cause bursty CPU and memory pressure if
every notification handler starts concurrently.

**Mitigation**:

- `TaskWhenAllPublisher` and `ResilientTaskWhenAllPublisher` now default
  to `maxDegreeOfParallelism = 16`.
- Applications that intentionally rely on the legacy behavior must opt
  in explicitly with `-1`, making the tradeoff visible at registration
  time.
- Regression coverage: `PublishingConcurrencyTests`.

### DoS via static mediator cache pinning

**Risk**: `Mediator` maintains process-lifetime static caches keyed by
runtime request and notification types. Applications that allow
attacker-controlled type materialization can pin unbounded numbers of
closed generic types into those caches.

**Current posture**:

- The caches are intentionally process-scoped for steady-state
  performance.
- Meridian does not instantiate request types from untrusted payloads.
  Consumers must prevent unsafe polymorphic or type-name-based
  deserialization from reaching `Send`, `Publish`, or `CreateStream`.

**Consumer guidance**:

- Treat attacker-controlled runtime type materialization as
  deserialization of untrusted data (CWE-502 / OWASP Deserialization of
  Untrusted Data).
- Restrict polymorphic deserialization to an allowlist of known request
  contracts before invoking Meridian.

### Silent data corruption

**Risk**: a mapping produces a destination value that differs from the
source in a non-obvious way (truncation, sign flip, lossy conversion).

**Mitigation**:

- Numeric conversions use `Expression.Convert`, which enforces the
  CLR's unchecked-convert semantics (truncation for narrowing, no
  overflow exception). Callers that need checked arithmetic should
  validate source values at their application boundary.
- The fast path's final `Expression.Assign` verifies source and
  destination types match exactly, forcing an explicit `Convert` step
  rather than silently mis-assigning.
- Round-trip equality is covered by
  `PropertyBasedTests.RoundTrip_Preserves_All_Fields` with 100 generated
  cases per run.

## Defence-in-depth: the test matrix

| Class of threat | Tests | Invocation |
| --- | --- | --- |
| Deep-recursion DoS | `DosRegressionTests`, `PropertyBasedTests.Arbitrary_Depth_*`, `MapToExistingDosTests.UseDestinationValue_*` | Every CI run |
| Wide-collection DoS | `DosRegressionTests`, `PropertyBasedTests.Oversized_Collection_*`, `MapToExistingDosTests.MapToExisting_Oversized_*` | Every CI run |
| Undocumented exception leak | `PropertyBasedTests.Arbitrary_Payloads_Never_Throw_Undocumented` | Every CI run |
| Round-trip equality | `PropertyBasedTests.RoundTrip_Preserves_All_Fields` | Every CI run |
| Byte-level fuzz (SharpFuzz) | `tests/Meridian.Mapping.Fuzz` | Nightly — manual AFL runner |

## Explicit non-goals

- **Native AOT confidentiality.** Meridian's reflection path is not
  designed to hide type metadata from disassembly. The fast path and the
  planned `[GenerateMapper]` source generator produce the same IL that
  AOT tools can trim — no obfuscation layer.
- **Cryptographic operations.** Meridian does not perform signing,
  hashing, or encryption. If a mapped value is security-sensitive, the
  caller must apply appropriate controls before or after mapping.
- **Tamper-evidence at mapping time.** Meridian does not record who
  mapped what. Use the built-in `CorrelationIdBehavior` in
  `Meridian.Mediator` for request-level traceability.
- **Isolation from cooperating code in the same process.** Any in-process
  library that shares the CLR heap can inspect Meridian's caches and
  compiled delegates. If you need isolation, use OS-level boundaries.

## Reporting a suspected issue

If you believe you have found a security defect in Meridian, follow the
private reporting flow in [SECURITY.md](../SECURITY.md). Do not open a
public GitHub issue for unpatched security findings.

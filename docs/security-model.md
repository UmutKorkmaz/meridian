# Meridian Security Model

**Audience:** library maintainers, security researchers, and adopters
evaluating Meridian for use in regulated environments.
**Last reviewed:** 2026-04-14

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
- Regression coverage: `DosRegressionTests.Attacker_Crafted_100k_Deep_Graph_*`
  plus `PropertyBasedTests.Arbitrary_Depth_Never_Stack_Overflows`.

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
- Regression coverage: `DosRegressionTests.Attacker_Crafted_1M_Item_Collection_*`
  and `PropertyBasedTests.Oversized_Collection_Always_Throws`.

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

**Non-goal**: we do not defend against an application that lets
attackers register resolver types at runtime. That is a configuration
integrity problem, not a mapper concern.

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
| Deep-recursion DoS | `DosRegressionTests`, `PropertyBasedTests.Arbitrary_Depth_*` | Every CI run |
| Wide-collection DoS | `DosRegressionTests`, `PropertyBasedTests.Oversized_Collection_*` | Every CI run |
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

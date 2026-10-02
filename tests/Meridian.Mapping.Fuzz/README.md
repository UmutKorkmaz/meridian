# Meridian.Mapping.Fuzz

Coverage-guided byte-level fuzzer for `Meridian.Mapping`, built on
[SharpFuzz](https://github.com/Metalnem/sharpfuzz) and driven by
[AFL++](https://aflplus.plus/).

The property-based tests in `tests/Meridian.Mapping.Tests/PropertyBasedTests.cs`
cover the common case on every CI run. This project exists for the
slower, deeper fuzzing that only makes sense with a local AFL instance
and hours of CPU time — typically before a release, or when triaging a
suspected DoS class bug.

## What the harness does

Each iteration receives a raw byte buffer from AFL and maps it through
a deliberately diverse set of TypeMaps: a recursive tree, a large flat
collection, and a composite root that contains both. The harness claims
success iff the mapping either completes or throws
`MeridianMappingLimitException`. Any other exception — in particular
`StackOverflowException` or `OutOfMemoryException` — is saved by AFL
as a reproducer.

## Running locally

### Prerequisites

- .NET SDK 10 (see `global.json`)
- [AFL++](https://aflplus.plus/) installed (`brew install afl-fuzz` on
  macOS, `apt install afl++` on Linux)
- SharpFuzz CLI: `dotnet tool install --global SharpFuzz.CommandLine`

### One-shot run

```bash
cd tests/Meridian.Mapping.Fuzz

# Build the harness.
dotnet build -c Release

# Instrument the mapping assembly in-place.
sharpfuzz bin/Release/net10.0/Meridian.Mapping.dll

# Run AFL against the harness binary. @@ is the AFL placeholder for the
# input file the fuzzer will hand to the binary.
afl-fuzz -i corpus -o findings -- \
    bin/Release/net10.0/Meridian.Mapping.Fuzz @@
```

Leave it running for at least an hour per release-candidate branch.

### Seed corpus

A small hand-picked seed corpus lives under `corpus/`:

| File            | Shape                                          |
| --------------- | ---------------------------------------------- |
| `flat-small`    | 0-depth tree, 2-item collection, short string |
| `deep-65`       | Depth 65 — one past `DefaultMaxDepth`          |
| `wide-10001`    | Collection size 10001 — one past `DefaultMaxCollectionItems` |
| `unicode-heavy` | Large multi-byte-codepoint payload             |
| `all-zero`      | All-null shape — edge case for nullability     |

AFL will minimise and mutate from these to build a faster-converging
corpus under `findings/queue/`. Commit interesting new seeds back.

## CI integration — deferred

The PLAN.md Phase 4.2 calls for a nightly CI workflow. We are deferring
that because AFL is an OS-level dependency and GitHub-hosted runners
install it inconsistently. The `.github/workflows/fuzz.yml` workflow in
this repository runs the property-based tests in FsCheck with a high
iteration count every night at 02:00 UTC and on manual dispatch. These
FsCheck runs cover mapping invariants at lower fidelity; they do not execute
AFL or establish coverage-guided fuzzing results.

### Property budgets and evidence

`MeridianPropertyAttribute` reads `MERIDIAN_FSCHECK_MAX_TEST` for scalar and
depth properties. `CollectionMeridianPropertyAttribute` separately reads
`MERIDIAN_FSCHECK_COLLECTION_MAX_TEST`. Unset variables preserve the ordinary
PR budgets: round-trip 100, payload 100, bounded collection 50, oversized
collection 10, and depth 25. Overrides must be positive 32-bit integers;
empty, malformed, zero, negative, and overflowing values fail explicitly.
Constructor default budgets replace named `MaxTest` assignments, which would
otherwise overwrite an environment override after attribute construction.

Nightly CI uses 10000 scalar/depth cases and 500 cases for each collection
property on net10.0. The 30-minute job budget and 1 GiB managed heap limit
(`DOTNET_GCHeapHardLimit=40000000`, hexadecimal bytes) keep the run bounded.
The managed heap limit is not a process RSS limit.

Success output includes FsCheck's **actual** `Ok, passed ... tests.` count
for each property. Each property also records elapsed milliseconds, the
shared test host's measured peak working set when the OS reports it, managed
heap bytes in use, and the runtime's effective managed heap budget. A zero
peak from the OS is logged as `unavailable` (observed with .NET on macOS).
Peak working set belongs to the entire host and cannot be attributed to one
property. Managed heap usage is sampled at disposal and shared by the host;
it is not a per-property allocation total. Elapsed time spans the test
instance lifetime.
Detailed console logs, TRX execution durations, and SDK/runtime information
are uploaded in `fuzz-property-results`, including on failure.

FsCheck's normal success message omits replay seeds. CI therefore generates
an unsigned 64-bit seed for each run and supplies the optional
`MERIDIAN_FSCHECK_REPLAY=seed,gamma` to the base FsCheck attribute. Gamma must
be an odd unsigned 64-bit integer; CI uses `11400714819323198485`. All five
properties receive that replay pair. The pair printed in property evidence
is the configured pair consumed by FsCheck (covered by a runner-configuration
test), not an inferred seed from success output. Unset replay preserves
FsCheck's ordinary PR randomness. Replay validation is intentionally limited
to this two-component form, without an optional start size.

To reproduce the nightly property suite in a fresh process:

```bash
MERIDIAN_FSCHECK_MAX_TEST=10000 \
MERIDIAN_FSCHECK_COLLECTION_MAX_TEST=500 \
MERIDIAN_FSCHECK_REPLAY=12345,11400714819323198485 \
DOTNET_GCHeapHardLimit=40000000 \
dotnet test tests/Meridian.Mapping.Tests -c Release -f net10.0 \
    --filter FullyQualifiedName~PropertyBasedTests \
    --logger 'console;verbosity=detailed'
```

Use the recorded nightly replay pair to investigate a particular run.
Environment variables are read only; configuration tests use an injected
reader rather than changing process-global variables during discovery.

When a dedicated runner with AFL installed is available, the workflow
can call this harness directly:

```yaml
- name: Install SharpFuzz CLI
  run: dotnet tool install --global SharpFuzz.CommandLine
- name: Build harness
  run: dotnet build tests/Meridian.Mapping.Fuzz -c Release
- name: Instrument
  run: sharpfuzz tests/Meridian.Mapping.Fuzz/bin/Release/net10.0/Meridian.Mapping.dll
- name: Fuzz (1 hour budget)
  run: |
    afl-fuzz -V 3600 -i tests/Meridian.Mapping.Fuzz/corpus \
      -o findings -- tests/Meridian.Mapping.Fuzz/bin/Release/net10.0/Meridian.Mapping.Fuzz @@
- name: Upload findings
  uses: actions/upload-artifact@v4
  with:
    name: fuzz-findings
    path: findings/
```

## Triaging a crash

A genuine crashing input lands under `findings/crashes/`. Reproduce:

```bash
bin/Release/net10.0/Meridian.Mapping.Fuzz findings/crashes/id:000000,*
```

If the stack trace points into `MappingEngine` or `FastPathCompiler`,
file a private advisory via `SECURITY.md` before opening a public issue.

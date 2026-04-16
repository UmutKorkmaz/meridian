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
iteration count every night; those cover the same invariants at lower
fidelity.

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

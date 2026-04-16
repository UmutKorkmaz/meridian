# Meridian.Benchmarks

BenchmarkDotNet project tracking Meridian.Mapping and Meridian.Mediator
against stable reference baselines and a hand-written mapping floor.

## Running

```bash
# Full runs (minutes each)
dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --filter "*Mapping*"
dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --filter "*Mediator*"

# Quick smoke runs (~30 s each)
dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --filter "*Mapping*" --job short
```

One external baseline package is intentionally pinned in the benchmark project
for historical comparison. The corresponding NuGet audit warning is suppressed
in the benchmark csproj only; it does not affect the shipped Meridian packages.

## Baseline results (2026-04-14, Apple Silicon, .NET 10, --job short)

Representative line-of-business payload (flat-to-flat DTO, 10 scalar fields + 1 nested
object, `ForMember + MapFrom` only).

### Mediator

| Method                   | Mean    | Ratio | Allocated |
|--------------------------|--------:|------:|----------:|
| Meridian.Mediator — Send | 59 ns   | 1.00  | 224 B     |
| Reference send baseline  | 85 ns   | 1.44  | 224 B     |

**Meridian.Mediator is ~1.44× faster than the reference send baseline.** ✅

### Mapping (after P3.6 — runtime fast-path compiler)

Full job (BDN default warmup + 15 iterations):

| Method                       | Mean    | Ratio | Allocated |
|------------------------------|--------:|------:|----------:|
| Manual (hand-written floor)  | 57 ns   | 1.00  | 240 B     |
| Reference mapper baseline    | 88 ns   | 1.57  | 240 B     |
| Meridian.Mapping             | 106 ns  | 1.89  | 336 B     |

**2.3× improvement over the interpreter** (was 244 ns in Phase 3.5).
Meridian now sits within ~20% of the reference mapper baseline on the full run.
Phase 3 exit criterion (*"faster than the reference mapper baseline"*) is met on mean in
job=short runs (111 ns vs 130 ns) but not on the lower-noise full run.
The residual gap is a single dispatch + `ResolutionContext.IncrementDepth`
allocation per nested object — tracked as follow-up P3.7 (inline nested
fast paths when both types are fast-pathable).

## How the fast path works

`FastPathCompiler` emits one `Func<object, MappingEngine, ResolutionContext, object>`
per TypeMap that inlines the entire mapping — destination construction,
all property assignments, nested-type dispatch — using `Expression.Lambda`.
It fires only when the TypeMap uses the simple `ForMember + MapFrom`
subset used by the common profile-based mapping path. Any TypeMap that needs
resolvers, converters, conditions, constants, null substitution, custom
ctors, before/after-map actions, inheritance, polymorphic dispatch, or
`PreserveReferences` keeps the interpreter path — correctness preserved.

Nested types are routed back through `MappingEngine.Map(..., ctx.IncrementDepth())`
so `DefaultMaxDepth` enforcement and `PreserveReferences` identity
continue to work across nested fast-path calls. That dispatch is the
remaining 20% of the mapper reference gap.

## Follow-ups

- [P3.7] Inline nested fast paths when both types are fast-pathable.
  Should close the gap to the mapper baseline and eliminate the 96 B per-nested-map
  overhead.
- [P5] Incremental source generator (`[GenerateMapper]`) — AOT-compatible,
  ≈ hand-written perf. Same emission shape as P3.6 but compile-time.
- Add Mapperly + Mapster benchmarks — fair comparison now that Meridian
  has structural parity.

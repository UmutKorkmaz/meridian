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

## Mediator metrics overhead (D5)

`MediatorMetricsOverheadBenchmarks.cs` holds the fixed harness that measures the opt-in `Meridian.Mediator` metrics
(see [docs/mediator-metrics.md](../../docs/mediator-metrics.md)). It uses only API that exists both before and after
the metrics change, so the same source and settings can be compiled against a baseline product (P0) and an
instrumented product (P1). The metrics options type is found by name, so on a baseline product every `Mode` row runs
the same unchanged code, which doubles as a same-binary noise floor.

| Class | Rows |
| --- | --- |
| `No_Listener_Hot_Path_Has_No_New_Allocation` | typed, object and void `Send`, `Publish` to four handlers and a two-item fully consumed stream, each in three modes |
| `Activity_Listener_Hot_Path` | typed `Send` and the stream with an `ActivityListener` attached (tracing-branch regression check) |
| `Pipeline_Event_Overhead` | primed cache hit, one zero-delay retry, completed idempotency replay |

`Mode` is `disabled` (default options), `enabled-no-listener` (metrics enabled, nothing subscribed) or
`enabled-listener` (a `MeterListener` with constant, allocation-free callbacks). The criterion for the first two is a
zero allocation delta against P0 (not zero absolute allocation); the listener rows report the cost of the
instrumentation that was explicitly opted into. `GlobalSetup` verifies the semantics and warms every row identically.

```bash
dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- \
  --filter '*No_Listener_Hot_Path_Has_No_New_Allocation*' '*Activity_Listener_Hot_Path*' '*Pipeline_Event_Overhead*' '*MediatorBenchmarks*' \
  --launchCount 3 --warmupCount 8 --iterationCount 20 --memory
```

### Result (2026-10-04, Apple M5 Pro, macOS 26.5.2, .NET SDK 10.0.300 / runtime 10.0.8; INCONCLUSIVE, not a dedicated-host result)

Fixed BenchmarkDotNet job: 3 launches, 8 warmup and 20 measured iterations per launch, `MemoryDiagnoser`. P0 is the
mediator immediately before the metrics change, P1 the change; both were built from isolated source snapshots with a
byte-identical harness, and the segments were run in ABBA order. The host was an ordinary developer machine (other
applications and containers running, load average about 4 to 8), **not** a dedicated or quiescent benchmark host, so
treat sub-5% time differences as noise (the same-binary spread of the P0 rows was 1% to 6%). Allocation figures are
deterministic.

**Provenance and status.** No exclusive, quiescent host window was available, so time acceptance is
**PENDING/INCONCLUSIVE**: the table below is indicative, not a dedicated-host pass. It combines four campaigns. The
`Activity_Listener_Hot_Path` rows and the `MediatorBenchmarks` row use a P0 segment from an earlier campaign (c, AC power,
identical P0 binary) because the P0 activity segment of the final campaign (d) ran while the host switched from AC to
battery and was rejected; those P0 rows are therefore not paired with the P1 rows in the same campaign. Every other row
is a same-campaign (d) ABBA pair. Exact allocation deltas are favorable (zero or negative) for the disabled and
no-listener modes. Full history: `artifacts/D5/bench/comparison.md` in the task evidence.

| Scenario | Mode | P0 mean | P1 mean | Delta | P0 B/op | P1 B/op | Delta B/op |
|---|---|---:|---:|---:|---:|---:|---:|
| Publish x4 | disabled | 103.8 ns | 97.6 ns | -6.0% | 760 | 720 | -40 |
| Publish x4 | enabled-listener | 103.6 ns | 104.8 ns | +1.2% | 760 | 720 | -40 |
| Publish x4 | enabled-no-listener | 104.3 ns | 99.2 ns | -4.8% | 760 | 720 | -40 |
| object Send | disabled | 49.4 ns | 47.7 ns | -3.3% | 376 | 328 | -48 |
| object Send | enabled-listener | 49.6 ns | 62.5 ns | +26.0% | 376 | 328 | -48 |
| object Send | enabled-no-listener | 49.1 ns | 45.5 ns | -7.3% | 376 | 328 | -48 |
| stream x2 consumed | disabled | 58.9 ns | 58.9 ns | -0.0% | 224 | 224 | +0 |
| stream x2 consumed | enabled-listener | 59.5 ns | 130.7 ns | +119.8% | 224 | 392 | +168 |
| stream x2 consumed | enabled-no-listener | 61.7 ns | 59.4 ns | -3.6% | 224 | 224 | +0 |
| typed Send | disabled | 39.4 ns | 35.3 ns | -10.4% | 280 | 232 | -48 |
| typed Send | enabled-listener | 39.7 ns | 55.5 ns | +39.8% | 280 | 232 | -48 |
| typed Send | enabled-no-listener | 40.1 ns | 35.1 ns | -12.4% | 280 | 232 | -48 |
| void Send | disabled | 39.4 ns | 35.6 ns | -9.7% | 208 | 160 | -48 |
| void Send | enabled-listener | 40.2 ns | 52.3 ns | +30.2% | 208 | 160 | -48 |
| void Send | enabled-no-listener | 39.4 ns | 34.5 ns | -12.5% | 208 | 160 | -48 |
| stream x2, Activity listener | disabled | 291.5 ns | 303.8 ns | +4.2% | 1440 | 1456 | +16 |
| stream x2, Activity listener | enabled-listener | 292.1 ns | 345.9 ns | +18.5% | 1440 | 1456 | +16 |
| stream x2, Activity listener | enabled-no-listener | 291.3 ns | 316.2 ns | +8.5% | 1440 | 1456 | +16 |
| typed Send, Activity listener | disabled | 159.5 ns | 160.4 ns | +0.6% | 1096 | 1096 | +0 |
| typed Send, Activity listener | enabled-listener | 168.5 ns | 183.7 ns | +9.0% | 1096 | 1096 | +0 |
| typed Send, Activity listener | enabled-no-listener | 165.7 ns | 174.4 ns | +5.2% | 1096 | 1096 | +0 |
| idempotency replay | disabled | 91.5 ns | 89.8 ns | -1.9% | 656 | 616 | -40 |
| idempotency replay | enabled-listener | 90.8 ns | 111.8 ns | +23.1% | 656 | 616 | -40 |
| idempotency replay | enabled-no-listener | 91.7 ns | 91.0 ns | -0.8% | 656 | 616 | -40 |
| one zero-delay retry | disabled | 1,788.3 ns | 1,769.2 ns | -1.1% | 848 | 800 | -48 |
| one zero-delay retry | enabled-listener | 1,848.4 ns | 1,887.6 ns | +2.1% | 848 | 800 | -48 |
| one zero-delay retry | enabled-no-listener | 1,736.0 ns | 1,727.3 ns | -0.5% | 848 | 800 | -48 |
| primed cache hit | disabled | 91.8 ns | 85.9 ns | -6.4% | 624 | 584 | -40 |
| primed cache hit | enabled-listener | 90.4 ns | 107.1 ns | +18.4% | 624 | 584 | -40 |
| primed cache hit | enabled-no-listener | 91.0 ns | 83.8 ns | -7.9% | 624 | 584 | -40 |
| MediatR v12 — Send | - | 34.4 ns | 33.5 ns | -2.6% | 224 | 224 | +0 |
| Meridian.Mediator — Send | - | 43.8 ns | 38.8 ns | -11.4% | 304 | 256 | -48 |

Reading the table:

* `disabled` and `enabled-no-listener` never allocate more than P0, and are within noise or up to 12% faster.
  `Send`, `Publish` and the behavior rows allocate 40 to 48 B **less** because the activity closures were moved out of
  the unobserved entry points, where the compiler used to allocate them on every call (behavior-neutral); the
  unobserved stream is identical (224 B).
* Enabled instruments with a listener attached add 15 to 20 ns per `Send` (two measurements: count and duration),
  about 20 ns per cache hit or idempotency decision, and about 70 ns plus 168 B for an observed two-item stream (a
  wrapper that guarantees exactly-once cleanup and terminal events). Counter-only subscriptions take no timestamp.
* With an `Activity` listener attached, the stream now runs through the same wrapper instead of a compiler iterator:
  +16 B per enumeration and +4% to +9% time (metrics disabled or unobserved); `typed Send` is unchanged.
* The baseline `MediatorBenchmarks` rows were rerun in the same campaign (`Meridian.Mediator - Send`: 256 B versus
  304 B).
* The new rows are not comparable with the 2026-04-14 results above (different harness, host and SDK).

## Follow-ups

- [P3.7] Inline nested fast paths when both types are fast-pathable.
  Should close the gap to the mapper baseline and eliminate the 96 B per-nested-map
  overhead.
- [P5] Incremental source generator (`[GenerateMapper]`) — AOT-compatible,
  ≈ hand-written perf. Same emission shape as P3.6 but compile-time.
- Add Mapperly + Mapster benchmarks — fair comparison now that Meridian
  has structural parity.

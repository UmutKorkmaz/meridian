# Meridian.Benchmarks

BenchmarkDotNet project for `Meridian.Mapping` and `Meridian.Mediator`. It holds three kinds of material:

1. **E3 equal-work scenarios** (classes whose name starts with `E3`): mapping, mediator dispatch, pipelines, startup and
   fresh-process first use, compared with a hand-written floor and with Mapster, Mapperly and MediatR.
2. **A semantic preflight** that fails a run before any timing when a contender does not produce the expected result.
3. **Frozen harnesses** from earlier tasks (B8 nested fast path `H1`, D5 metrics overhead) that exist so that a baseline
   product and a candidate product can be measured by byte-identical code.

**No universal performance claim is made anywhere in this project.** A number is only meaningful together with the
exact run that produced it: host, power and load conditions, job, candidate source identity and competitor versions.
The selected, reproducible summary of the current evidence is [results/README.md](../results/README.md); raw BenchmarkDotNet
output stays in the ignored `artifacts/` directory of the run that produced it. Timings from shared CI runners or from a
busy machine are not release evidence.

## Running

Use the run script. It builds an isolated copy of the sources, runs the preflight, records provenance, runs
BenchmarkDotNet and summarizes the result. It fails (non-zero exit, `manifest.json` with `status: failed`) when the filter
matches nothing, the preflight fails, the output directory is not empty or overlaps the source tree, the build fails,
BenchmarkDotNet fails, or no report is produced; it never turns a missing result into success.

```powershell
pwsh eng/run-benchmarks.ps1 -Filter '*.E3MappingNested*' -OutputDirectory artifacts/my-run -WindowName 'my measurement window'
pwsh eng/run-benchmarks.ps1 -Filter '*.E3*' -OutputDirectory artifacts/full-run -WindowName 'named window'   # the full E3 set
pwsh eng/run-benchmarks.ps1 -Filter '*.E3*' -OutputDirectory artifacts/smoke -Profile Smoke                  # execution proof only
```

| Output (in `-OutputDirectory`) | Content |
| --- | --- |
| `manifest.json` | schema version, run id and UTC interval, status and failures, filter and exact command, host (name, OS, CPU, memory, SDK, runtime, BenchmarkDotNet version), window (name, power source, load average before, during and after, top processes), runtime configuration (GC mode, tiering), candidate identity (git HEAD, git status hash, product-source hash, harness hash, assembly hashes, lane), competitor versions and licenses, preflight result, exclusions |
| `bdn/results/*-report-full.json` | the unmodified raw BenchmarkDotNet reports (every measurement of every launch) plus CSV, GitHub markdown and HTML |
| `summary.md`, `summary.json` | mean, 99.9% confidence half-width, standard deviation, coefficient of variation, launch spread, allocation per operation and Gen0 collections for every row |
| `build.log`, `list.txt`, `preflight.log`, `run.log`, `run.stderr.log`, `product-source-manifest.txt`, `harness-source-manifest.txt` | the evidence behind the manifest |

Paired candidate-versus-baseline comparison of two runs of the **same** harness (flags every mean regression above 10% and
every allocation increase; it never decides acceptance):

```bash
python3 eng/benchmark-summary.py compare --baseline <runA>/bdn/results --candidate <runB>/bdn/results --output compare.md
```

The semantic preflight can also be run alone: `dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --e3-preflight [ClassName ...]`
(and `--e3-preflight-selftest`, which proves that wrong results are rejected). Do not call `dotnet run -- --filter` directly
inside a repository that contains other copies of this project (for example frozen task snapshots): BenchmarkDotNet then
cannot find a unique project file. The run script avoids this by building an isolated workspace.

## The one fixed job

Every E3 scenario class declares its job in `Program.cs` (`E3HotConfig`, `E3ColdConfig`); nothing is chosen on the command
line and a run never mixes jobs.

| | Hot (steady state) | Cold (fresh process) |
| --- | --- | --- |
| Launches | 3 | 10 |
| Warmup / measured iterations | 6 / 15 (250 ms each) | 0 / 1 (one invocation per launch) |
| Configured runtime settings | concurrent server GC, tiered compilation on, `MemoryDiagnoser`; measured-child settings were not independently verified | same |
| Measures | one complete operation per call after full warmup | the first operation in a fresh process; process launch and runtime start are excluded, JIT, type loading and static initialization of the library code are included |

This is a deliberately short fixed job so that the complete scenario set has a bounded runtime. It is a reviewed starting
configuration, not a promise of adequate precision for every row; the summary therefore always reports the standard
deviation, the coefficient of variation and the spread between launch means next to each mean. The cold lane runs its
semantic checks in a separate preflight process so they cannot warm the measured process.

## Scenario matrix (all verified equal-work before timing)

| Class | Shapes and cases | Contenders (method names) | What is timed |
| --- | --- | --- | --- |
| `E3MappingFlat` | ten scalars | `Manual` (floor), `MeridianInterpreted`, `MeridianCompiled`, `MeridianGeneratedDirect`, `MeridianGeneratedRuntime`, `Mapster`, `Mapperly` | one complete new-result map |
| `E3MappingNested` | ten renamed scalars plus one nested address | same seven | same |
| `E3MappingCollectionPayload` | basket with 0, 1, 32 and 256 lines, each with a nested product | same seven | same |
| `E3MappingDeep` | chain of 4 and 16 nested objects (explicit pair per level) | same seven | same |
| `E3MappingReferences` | repeated-reference graph: one source node used twice plus an equal-valued distinct node | `Manual` (with identity tracking), `MeridianRuntime`, `MeridianGeneratedDirect`, `Mapster`, `Mapperly` | same; identity is asserted |
| `E3MediatorDispatch` | request, handler, response; no behaviors | `DirectHandler` (floor), `MeridianRuntime`, `MeridianStrictGenerated`, `MediatRV12` | one `Send` |
| `E3MediatorScopeExisting` | scoped handler dependency inside one existing scope; both handlers registered by type | `DiResolvedHandler`, `MeridianRuntime`, `MediatRV12` | one `Send` |
| `E3MediatorScopePerOperation` | create, use and dispose a scope per operation | same three | scope + `Send` |
| `E3MediatorPipelines` | none, 1 or 4 pass-through behaviors, closed or open registration | `MeridianRuntime`, `MediatRV12` | one `Send` |
| `E3MediatorCacheBehavior` | disabled, primed in-memory hit, synthetic always-miss | `Send` | behavior overhead only |
| `E3MediatorIdempotencyBehavior` | disabled, legacy replay, atomic replay, synthetic always-claim | `Send` | behavior overhead only |
| `E3MediatorMetrics` | disabled, enabled with no listener, enabled with an in-process listener | `Send` | one `Send` |
| `E3NotificationPublish` | three handlers, default sequential publisher | `MeridianPublish`, `MediatRV12Publish` | one `Publish` |
| `E3NotificationTaskWhenAll` | 10 and 100 handlers, opt-in task-when-all publisher | `MeridianTaskWhenAllUnbounded` (`new TaskWhenAllPublisher(-1)`, baseline, like for like with MediatR), `MeridianTaskWhenAllBounded16` (the default cap of 16, a different contract), `MediatRV12TaskWhenAll` (unbounded) | one `Publish`; synchronous handlers only |
| `E3StartupMapping` | configuration only, and ready plus first map (warmed process) | Meridian, Meridian generated runtime, Mapster | one construction / one first map |
| `E3StartupMediator` | registration only, and provider build plus first `Send` plus disposal (warmed process) | Meridian runtime, Meridian strict generated, MediatR | one construction / one first send |
| `E3ColdMapping`, `E3ColdMediator` | first use in a fresh process | the same contenders plus manual, generated direct and Mapperly first maps, and a direct handler call | one invocation per fresh launch |

Equal-work rules enforced by the code:

- **Same work.** Every contender maps the same members, preserves collection order, creates new nested objects (never aliases
  them) and produces the same observable response. Hand-written code implements the same reference-identity tracking. Every
  handler and behavior performs the identical advertised work (it counts its invocation in a plain static counter).
- **Validate before timing.** `GlobalSetup` verifies every contender against expected values written independently of any
  mapper (literal or formula-computed), including source immutability, and the preflight repeats this for every parameter
  combination in a separate process before the run script starts BenchmarkDotNet. Mediator preflights verify the response
  value, the exact handler call count, the exact behavior order, scope sharing and disposal, the cache and idempotency
  interaction contracts and the exact metric events. `Preflight()` of a scenario is also invoked by its `GlobalSetup`.
- **Consume outputs.** Every benchmark method returns its result (or `Task` of it), which BenchmarkDotNet consumes. Nothing
  in a timed method validates, sums or branches on the result.
- **Path selection is proven.** Meridian "interpreted" is the same specification on an independently created configuration whose
  `TypeMap.CompiledFastPath` is cleared in setup, and the setup asserts how many fast paths each configuration has. The
  generated runtime row asserts that its configuration contains no explicit runtime `TypeMap`.
- **Same DI path.** The scoped-handler rows register the Meridian and the MediatR handler by type with the same lifetime (no
  factory delegate, no instance); the direct floor resolves the concrete type through its own separate registration. A
  factory that re-resolves the concrete type adds a delegate call and a second provider lookup (about 8 ns in an existing
  scope on the evaluated host) to one contender only, so the preflight rejects any asymmetric registration shape.
- **Same publisher contract.** Meridian's `TaskWhenAllPublisher` has a concurrency cap of 16 by default: per publication it
  allocates a `SemaphoreSlim` and one async state machine per handler to honor the cap. MediatR 12.5.0's publisher is an
  unbounded `Task.WhenAll`. The like-for-like comparison is therefore Meridian **unbounded** (`new TaskWhenAllPublisher(-1)`)
  against MediatR; the **bounded** (cap of 16) row is shown separately and is a different contract, so the cost of the cap is not
  read as the publisher's own fan-out overhead. The preflight reads each row's publisher and requires the labeled cap.
- **Resources.** Providers, scopes and listeners created in setup are disposed in cleanup. Scope creation and disposal is
  inside the timed region for every contender or for none.
- **No drift.** Cache and idempotency rows use either a prepopulated bounded store (hit/replay) or a deterministic synthetic
  double (always-miss, always-claim) so the state cannot drift from a miss into a hit during the measurement.

## Competitors

All are benchmark-only references of this non-packable project; none flows into a Meridian package. Versions are exact pins
and the licenses were read from the nuspec of exactly the restored version (also recorded in every `manifest.json`).

| Library | Version | License | Label |
| --- | --- | --- | --- |
| Mapster | 10.0.13 | MIT | current stable |
| Riok.Mapperly | 4.3.1 | Apache-2.0 | current stable (a source generator: an analyzer in this project only) |
| MediatR | 12.5.0 | Apache-2.0 | **historical pin**: the last permissively licensed release (Apache-2.0, not MIT); not the current MediatR |

AutoMapper 14.0.0 was retired from the benchmark project after additional vulnerabilities were disclosed. Existing
`AutoMapperV14` measurements are available in the historical results. [GHSA-6vhm-j9h7-f9gh](https://github.com/advisories/GHSA-6vhm-j9h7-f9gh), [GHSA-gpwp-5797-mjff](https://github.com/advisories/GHSA-gpwp-5797-mjff)

`CS0436` is suppressed in the benchmark project because the repository's friend-assembly setup makes the
generator-emitted internal attribute types appear twice.

## Omitted on purpose (recorded, not hidden)

- Mapping into an **existing destination**: the generated path does not support it, so no four-way equal-work comparison exists.
- **Generated runtime integration for reference graphs**: only the direct generated call is timed for `E3MappingReferences`.
- **Mapperly startup** (`E3StartupMapping`): it has no runtime configuration; its cost is compile time. **Source-generator build
  time** is a build cost and is not measured.
- **Assembly-scanning registration**: the two mediators would scan different handler inventories in this assembly, which is not equal
  work. MediatR's API requires one assembly to be named, so its startup rows include a scan of the small MediatR assembly itself.
- **Configure-only startup rows are not comparable across libraries**: the Mapster benchmark calls `Compile()` explicitly and
  Meridian compiles when the configuration is constructed. The mapping "ready" rows (configuration plus first result)
  and mapping cold rows cover comparable work. Mediator startup and cold rows retain MediatR's extra assembly scan and cannot
  establish an equal-work startup ranking.
- **Asynchronous latency, parallel throughput and I/O**: every handler completes synchronously; the task-when-all rows measure
  the publisher's own fan-out (unbounded rows) and the cost of its concurrency cap (bounded row) only. No Redis, EF Core or other provider latency is measured: cache and idempotency rows are
  behavior overhead with in-process providers.
- **Release-package performance**: all runs use project references to source. No run of the packed candidate packages exists.
- Cycles and null/limit/enum-failure behavior: correctness suites, not throughput scenarios.

## Measurement windows and decisions

The machine used here is a developer laptop that is also doing other work. A run is only called *quiet* when it was on AC
power and the 1-minute load average stayed at or below 3 (about 15% of its 18 logical cores) for the whole timed region.
`manifest.json` records the power source, load before, during (every 30 s) and after, and the busiest processes, so a result
can always be classified afterwards; a result from a window that was not quiet is labeled **INCONCLUSIVE for time** in the
results README. Allocation observations apply to the measured runtime, architecture, configuration and workload.
Rules applied to every paired comparison:

- identical harness, scenario, runtime and settings on both sides; a different harness is never compared;
- `(candidate mean / baseline mean - 1) * 100`; **more than 10% regression is investigated even when the row is noisy**, and so is
  any allocation increase; nothing is averaged across unrelated workloads and no launch is selected;
- the same-binary or code-identical control rows set the noise floor, so a delta inside it is not a finding;
- the outcome (accepted, rejected or inconclusive) and the reason are written down together with the variance.

## Frozen harnesses (paired baseline/candidate runs)

| Harness | Classes | Job | Used for |
| --- | --- | --- | --- |
| B8 `H1` (`MappingBenchmarks.cs`) | `FlatMappingBenchmarks`, `OneLevelNestedMappingBenchmarks`, `DeepBoundedMappingBenchmarks`, `CollectionPayloadMappingBenchmarks` | `NestedFastPathJobConfig`: 3 launches, 8 warmup, 20 iterations | the nested fast-path inlining of the compiled mapping path |
| D5 (`MediatorMetricsOverheadBenchmarks.cs`) | `No_Listener_Hot_Path_Has_No_New_Allocation`, `Activity_Listener_Hot_Path`, `Pipeline_Event_Overhead` | 3 launches, 8 warmup, 20 iterations, `--memory` | the opt-in mediator metrics on the metrics-disabled hot path |

These live copies are not part of the E3 evidence set. The byte-identical frozen copies used for the baseline and candidate
products are kept with the task artifacts; to run one of them through the same script and provenance, point `-SourceRoot` at an
isolated tree that has the repository layout and pass the job on the command line for harnesses that do not declare it:

```powershell
pwsh eng/run-benchmarks.ps1 -Filter '*No_Listener_Hot_Path_Has_No_New_Allocation*disabled*' -OutputDirectory artifacts/d5-run `
  -SourceRoot artifacts/E3/pairs/d5-cur -SkipPreflightReason 'frozen D5 harness predates the E3 preflight' `
  -BenchmarkArguments --launchCount,3,--warmupCount,8,--iterationCount,20,--memory
```

## Historical results (2026-04-14): superseded, not comparable

These tables were produced by an earlier harness (classes `MappingBenchmarks` and `MediatorBenchmarks`, since replaced by the E3
classes), on a different host and SDK, with `--job short` or the default full job, and with destination DTOs that allocated a
nested default object. They are kept only so the history is visible. **No claim is derived from them**, and nothing on this page
or in [results/README.md](../results/README.md) may be compared with them. The "reference" libraries were AutoMapper 14.0.0 and
MediatR 12.5.0.

| Method (Apple Silicon, .NET 10, `--job short`) | Mean | Ratio | Allocated |
|--------------------------|--------:|------:|----------:|
| Meridian.Mediator Send | 59 ns | 1.00 | 224 B |
| Reference send baseline (MediatR 12.5.0) | 85 ns | 1.44 | 224 B |

| Method (full job: default warmup, 15 iterations) | Mean | Ratio | Allocated |
|------------------------------|--------:|------:|----------:|
| Manual (hand-written floor) | 57 ns | 1.00 | 240 B |
| Reference mapper baseline (AutoMapper 14.0.0) | 88 ns | 1.57 | 240 B |
| Meridian.Mapping | 106 ns | 1.89 | 336 B |

## How the fast path works

`FastPathCompiler` emits one `Func<object, MappingEngine, ResolutionContext, object>` per TypeMap that inlines the destination
construction and all property assignments using `Expression.Lambda`. It fires only when the TypeMap uses the simple
`ForMember + MapFrom` subset used by the common profile-based mapping path. Any TypeMap that needs resolvers, converters,
conditions, constants, null substitution, custom constructors, before/after-map actions, inheritance, polymorphic dispatch or
`PreserveReferences` keeps the interpreter path, so correctness is preserved.

Since the B8 change, eligible nested exact-pair maps are inlined into the parent delegate (bounded to four levels and 32 inlined
maps per root); other nested objects are routed back through `MappingEngine.Map` with a validated child context, so
`DefaultMaxDepth` enforcement and `PreserveReferences` identity keep working. Inlining is a compile-time snapshot: replacing a
nested `TypeMap.CompiledFastPath` later does not change an already compiled parent. B8's performance acceptance is
**PENDING** (see [results/README.md](../results/README.md)); only its allocation reduction (88 B per inlined nested map) is
deterministic evidence.

## Mediator metrics overhead (D5, frozen harness)

> **E3 note.** The metrics-disabled rows of this frozen D5 harness were re-measured on the current product in the E3 window; the
> time verdict stays **INCONCLUSIVE** (not a quiet window), allocation never increased. See [results/README.md](../results/README.md#d5-metrics-disabled-hot-path-re-measured-here).

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

- Re-run the whole E3 set, the B8 `H1` pair and the D5 pair in a real exclusive window (AC power, competing work paused), then replace
  the INCONCLUSIVE time verdicts in [results/README.md](../results/README.md). The B8 pair is still PENDING.
- Investigate the unfavorable workloads recorded in the results (Meridian compiled and generated-runtime mapping against Mapster and
  Mapperly, fixed collection overhead, the opt-in task-when-all publisher, strict generated dispatch and its cold first send).
- Run the same scenarios against the packed candidate packages to support any release statement (all current runs use project references).
- Structural generation already supports nested types, collections and conversions through explicit opt-in; see the maintained
  [source-generator guide](../../docs/source-gen-guide.md). The performance follow-ups above remain open.

Roadmap tracking: [docs/ROADMAP.md](../../docs/ROADMAP.md).

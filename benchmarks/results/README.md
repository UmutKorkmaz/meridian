# Benchmark results (E3, refreshed 2026-10-05 after fairness fixes)

Selected, reproducible summary of the E3 performance evidence for `Meridian.Mapping` and `Meridian.Mediator`.
**This is not a performance claim.** Every figure below was measured once, on one machine, under heavy competing load
(see [Measurement window](#measurement-window)), so **all time figures are INCONCLUSIVE as release evidence**: they are
indicative for this host and window only. Allocation observations (`Alloc B/op`) apply to the measured runtime,
architecture, configuration and workload.
Nothing here is a statement about other hardware, other runtimes, released packages, asynchronous or I/O-bound handlers, or
the current (commercially licensed) AutoMapper and MediatR releases. How the scenarios work and what they omit is documented in
[../Meridian.Benchmarks/README.md](../Meridian.Benchmarks/README.md).

The historical April 2026 numbers in the benchmark README were produced by an earlier harness (different payload, host and
SDK, `--job short`); they are **historical** and are not comparable with anything on this page.

**Fairness corrections in this refresh (fix round 1).** An earlier pass of this page (window W1, battery power) contained two
unfair comparisons that are corrected here and whose numbers are no longer shown:

- *Scoped-handler rows.* The Meridian handler was registered through a factory that re-resolved the concrete type, while the
  MediatR handler was registered by type, so only Meridian paid an extra delegate call and provider lookup on every `Send`
  (about 8 ns in an existing scope in a separate probe). Both handlers are now registered by type, and the preflight rejects any asymmetric registration shape.
- *Task-when-all rows.* Meridian's default `TaskWhenAllPublisher` has a concurrency cap of 16 (a `SemaphoreSlim` and one async
  state machine per handler per publication); MediatR 12.5.0's publisher is an unbounded `Task.WhenAll`. The earlier page
  compared the bounded Meridian default with the unbounded MediatR publisher and attributed the gap to the publisher's own
  overhead. There is now a like-for-like unbounded Meridian row (`new TaskWhenAllPublisher(-1)`) and the bounded default
  (cap of 16) is shown as a different contract.

All rows below come from one full rerun (W2) of the corrected harness.

## Reproduce

```powershell
pwsh eng/run-benchmarks.ps1 -Filter '*.E3*' -OutputDirectory artifacts/E3/my-run -WindowName 'my named window'
```

Raw BenchmarkDotNet output of the run summarized here is kept (not committed; `artifacts/` is ignored) in
`artifacts/E3-fix1/w2-e3-full/` of the evaluated worktree: `bdn/results/*-report-full.json` (every measurement of every launch),
`manifest.json`, `summary.md`, `summary.json`, `build.log`, `preflight.log`, `run.log`. (The first summary written by the run script, without a ratio baseline for the task-when-all class, is kept as `summary.first-pass.md`; the final summary was regenerated from the same raw JSON after the script's baseline list gained that class. The earlier W1 run in `artifacts/E3/w1-e3-full/` is superseded and kept only for audit.) Results differ between machines;
a different host will not reproduce these numbers, only their structure.

| | |
| --- | --- |
| Run id | `1daaf780dead4b5b8e5919be8de68228` (profile `Fixed`, filter `*.E3*`, 133 benchmark cases: 122 hot, 11 cold) |
| Candidate | project references to source; git HEAD `329b2443fd002532d1fcdd6534f13872fb7bbd55` plus the inherited uncommitted worktree and the uncommitted fix-round-1 harness changes (git status sha256 `077bd051...`); product source sha256 `00cb0758331bfcac708d21f68381d70c1e6c15c0c8aaab70753e6808e5820f3d` (unchanged from W1); harness sha256 `8b4920633242d5cb2edeafe733890ecf210dd08868a0ecc1729237576e9da36c` (documentation files were edited after the workspace copy). **Source lane only: no packed-package run exists.** |
| Toolchain | .NET SDK 10.0.300, runtime 10.0.8 (Arm64 RyuJIT), BenchmarkDotNet 0.15.8; harness configured for concurrent server GC and tiered compilation, measured-child settings not independently verified |
| Job | hot: 3 launches, 6 warmup, 15 iterations of 250 ms; cold: 10 fresh launches, 1 invocation each; `MemoryDiagnoser` |
| Preflight | passed for all 18 scenario classes before timing (`preflight.log`); its self-test rejects 12 classes of wrong result, including a factory-registered scoped handler and a publisher whose cap differs from its row label |
| Competitors | Mapster 10.0.13 (MIT), Riok.Mapperly 4.3.1 (Apache-2.0), **AutoMapper 14.0.0 (MIT, historical pin, advisory GHSA-rvv3-g6hj-g44x)**, **MediatR 12.5.0 (Apache-2.0, historical pin; not MIT)** |

## Measurement window

| | |
| --- | --- |
| Name | E3-fix1-W2, this Mac (`DTUMKORKMAZMACOS`, Apple M5 Pro, 18 logical cores, 48 GB, macOS 26.5.2) |
| Interval | 2026-10-05 12:24 to 13:14 (+03:00), 2981 s timed |
| Power | AC power for 93 of 98 samples; the five samples between 12:27 and 12:29 read battery (the charger connection was flapping); battery 16% at the start, 82% at the end, charging throughout |
| Load | 1-minute load average 9.7 at the start and 8.7 at the end; 98 samples every 30 s: minimum 3.6, **median 7.1, maximum 27.4**, no sample at or below 3, 15 samples above 10 |
| Other activity | a Docker/Virtualization VM with the user's containers, WindowServer, Spotlight indexing, Codex, Cursor and Claude helpers and other agents; none were stopped or paused. `manifest.json` lists the busiest processes |

Quiet criteria declared before measuring (AC power and 1-minute load at most 3 for the whole timed region) were **not met**:
power was mostly AC but the load never reached the threshold, so this is neither an exclusive nor a quiet window and the host was
not dedicated. Time results are therefore INCONCLUSIVE. For orientation only: the median coefficient of variation of the hot
rows is 2.1%, the median launch spread of all rows 3.0%, and 28 of 133 rows (cold, startup, `Items=0` and a few others) have a
coefficient of variation above 5%; this does not turn the window into a quiet one. W2 was run on AC and W1 on battery, so the
two windows are not comparable with each other (W2 times are lower across the board).

## Findings and decisions

No baseline exists for the new E3 rows, so there is no regression decision for them; the entries below record what the data
shows, including unfavorable workloads. Owners decide whether any of it needs work. All statements are for this window.
Contenders ran in separate processes across a drifting load/power window, rather than an interleaved comparison.
Small differences between rows are indicative; they establish neither equivalence nor release acceptance.

1. **Mapping, Meridian runtime versus the others.** Mapperly is close to the hand-written floor in these measured rows (ratio 0.95 to
   1.18; one noisy row, `Items=1` at 17.7% CV, shows 1.38) and Mapster is 1.4 to 2.6 times the floor on single objects and 3.0 on the reference graph. Meridian compiled is 2.7 to
   3.8 times the floor on flat, nested and deep payloads; on collections it carries a fixed cost (about 220 ns for an empty basket),
   so its ratio ranges from 3.0 (256 lines) to 19 (empty). Meridian interpreted is 19 to 29 times the floor on single objects.
   AutoMapper 14.0.0 is roughly equal to Meridian compiled on flat and nested payloads (28 and 39 ns versus 27 and 37 ns), faster
   on collections (32 lines: 477 ns versus 1,284 ns) and slower on the deep chains (16 levels: 578 ns versus 172 ns).
   Unfavorable for Meridian: the compiled path allocates 320 B on the flat payload against 120 B for the floor, Mapster and Mapperly.
2. **Meridian generated mapping.** The direct generated call is 1.2 to 1.6 times the floor and allocates exactly 88 B more than
   the floor in every non-reference shape (flat 208 B versus 120 B; consistent with the per-call generated context described in the generated-mapping
   contract, not separately profiled). The generated runtime integration (`UseGeneratedMappings`) is 4 to 13 times the floor: it measures the resolver bridge, which is a different row from the direct call.
3. **Reference graphs.** With identity preserved by every contender, the generated direct call is 1.6 times the hand-written
   floor, Mapster 3.0, Mapperly 4.2, AutoMapper 14 5.0 and Meridian runtime 7.2.
4. **Mediator dispatch.** Direct handler 6.1 ns; MediatR 12.5.0 33.1 ns (224 B); Meridian runtime 38.7 ns (256 B); Meridian strict
   generated 40.7 ns (256 B). In this window Meridian is about 17% slower than MediatR 12.5.0 on a plain send, and strict
   generated dispatch is not faster than the runtime path (about 5% slower, 2 to 3% CV on each). With one or four pass-through
   behaviors Meridian is faster (54 versus 68 ns and 96 versus 115 ns); closed and open registration are indistinguishable for
   both libraries. **Scoped handlers, both registered by type (like for like):** inside an existing scope Meridian takes 47.6 ns
   against 42.8 ns for MediatR (+11%; floor 19.7 ns); with one scope per operation 133.9 ns against 112.1 ns (+19%; floor 81.2 ns).
   Meridian is still slower in both rows, by less than the factory-registered pass this page replaced (+25% and +27% in
   window W1, a different window), and allocates 32 to 80 B more. The remaining gap is a Meridian-side cost for owners to
   investigate; it is not a registration artifact.
5. **Notifications.** Default sequential publication is equal within noise (78.5 versus 78.8 ns). **Task-when-all, like for like
   (Meridian unbounded `new TaskWhenAllPublisher(-1)` against MediatR's unbounded publisher):** 262 versus 245 ns for 10 handlers (+7%)
   and 2,229 versus 1,983 ns for 100 handlers (+12%); allocation 1,680 versus 1,832 B (10 handlers) and 14,640 versus 14,072 B
   (100 handlers, +4%). **The Meridian default is bounded (cap of 16) and is a different contract:** 429 ns and 3,550 ns, 1.6 times the
   unbounded Meridian row, because it allocates a `SemaphoreSlim` and one async state machine per handler to honor the cap. The earlier
   "twice as slow as MediatR" comparison mixed the two contracts and is withdrawn. Handlers complete synchronously, so these figures say nothing
   about asynchronous handlers or about what a cap buys. The controller ruling on multi-fault preservation in the publisher
   (progress ledger) named this path for allocation measurement; no regression decision is possible without a baseline, and the
   cost of the bounded default stays recorded as a follow-up for owners.
6. **Behavior overhead (in-process providers).** A primed in-memory cache hit costs 96.8 ns against 30.5 ns with no behavior
   (+576 B versus 160 B); a synthetic miss 92.2 ns (+600 B); idempotency replay 101 to 112 ns and a synthetic new claim 105 ns
   (608 to 656 B). These are behavior overhead, not provider latency.
7. **Metrics.** Enabled-without-listener (39.1 ns, 256 B) is close to disabled (38.4 ns, 256 B) in this window; an attached
   in-process listener adds about 20 ns and no allocation. The exact expected events were verified before timing.
8. **Startup.** Warmed process, ready for the first map: AutoMapper 14.0.0 0.64 ms, Mapster 0.92 ms, Meridian runtime 1.19 ms (236 KB
   allocated), Meridian generated runtime 0.29 us (nothing to compile). First mediator send including provider build: Meridian runtime 2.8 us,
   Meridian strict generated 5.4 us, MediatR 12.5.0 7.6 us. **The "configure" rows are not comparable across libraries**:
   AutoMapper defers compilation to the first map, Mapster compiles eagerly, Meridian compiles at construction; compare the "ready"
   mapping rows. MediatR startup and cold rows also include an assembly scan, so mediator startup is not an equal-work ranking.
   Cold, fresh process, first map including configuration: Meridian generated runtime 6.3 ms, Meridian runtime 19.0 ms, AutoMapper
   27.5 ms, Mapster 34.1 ms; first mediator send: MediatR 12.5 ms, Meridian runtime 16.5 ms, Meridian strict generated 28.2 ms (an
   unfavorable result). Extra generated first-use work is a hypothesis requiring profiling. Cold rows include JIT, type loading and
   static initialization; their individual contributions were not profiled. The direct-handler cold row has a launch spread above 100%.

Variance policy: rows with a coefficient of variation above 5% or a launch spread above 10% (several startup, cold, `Items=0`,
reference and metrics rows) should be read as order of magnitude only.

## Paired re-measurements of earlier accepted work

### D5 metrics-disabled hot path (re-measured here)

Baseline P0 (mediator immediately before the metrics change, source hash `dbf2212f...`) against the current accepted product
(source hash `fcfe27cf...`), byte-identical frozen D5 harness (`MediatorMetricsOverheadBenchmarks.cs`, sha256 `09299d92...37ab6`),
same job (3 launches, 8 warmup, 20 iterations, `MemoryDiagnoser`), five metrics-`disabled` rows, four segments in ABBA order
(P0, current, current, P0), each built in its own isolated workspace. The window was the same battery-powered, loaded host
(1-minute load 2.7 to 6.5, battery 31% to 19%), so **the verdict is INCONCLUSIVE** by the declared window rule; it is recorded
together with what the data shows:

| Row (mode disabled) | P0 mean (seg 1 / seg 4) | Current mean (seg 2 / seg 3) | Delta (pair 1 / pair 2) | Alloc B/op P0 -> current |
|---|---:|---:|---:|---:|
| typed `Send` | 56.5 / 57.4 ns | 50.8 / 50.3 ns | -10.2% / -12.3% | 280 -> 232 |
| `object` `Send` | 70.2 / 70.7 ns | 67.1 / 65.8 ns | -4.3% / -7.0% | 376 -> 328 |
| void `Send` | 56.8 / 57.3 ns | 50.4 / 50.0 ns | -11.4% / -12.8% | 208 -> 160 |
| `Publish` x4 | 151.5 / 151.0 ns | 140.9 / 140.3 ns | -7.0% / -7.1% | 760 -> 720 |
| stream x2 consumed | 86.2 / 86.2 ns | 85.6 / 85.6 ns | -0.7% / -0.8% | 224 -> 224 |

The two P0 segments agree within 1.4% and the two current segments within 2.0% (the same-binary noise floor), no row regressed
(none above +10%), and allocation never increased (deterministic: 40 to 48 B fewer, stream unchanged). Decision: **INCONCLUSIVE
for time (no exclusive quiet window), allocation criterion satisfied**. The enabled-without-listener mode was not re-measured here
(only the metrics-disabled rows were; the E3 metrics rows above are a different harness), so its earlier D5 result stands as recorded in the D5 report. Raw artifacts:
`artifacts/E3/d5/seg{1,2,3,4}-*/` (`manifest.json`, `bdn/results`, `summary.md`) and the comparisons `cmp-*.md`.

### B8 nested fast-path pair: PENDING

The B8 report made an exclusive-window rerun of the frozen `H1` harness against the preserved P0 and the current product a blocking
gate for any published B8 performance claim. It was **not run**: in the first pass the laptop was on battery at 19% after the E3 run and the D5 re-measurement, and in the
fix-round rerun (on AC) the 1-minute load never fell below 3.6 (competing agent work could not be paused), so the exclusive-window
precondition was again not met. B8 therefore stays **PENDING**; no B8 time figure may be quoted. The trees and the runner are prepared
(`artifacts/E3/make-trees.sh`, `artifacts/E3/run-pairs.sh b8`): run it on AC power with competing work paused. Allocation
evidence for B8 (deterministic, minus 88 B per inlined nested map) is unchanged and lives in the B8 report.

## Results

Columns: mean, half-width of the 99.9% confidence interval, coefficient of variation across measured iterations, spread between
the three (or ten) launch means, `Ratio` to the floor of the same class and parameters (hand-written code, direct handler or, for pipelines,
the Meridian runtime row, and for task-when-all the unbounded Meridian row; derived from the means, no error bar), allocated bytes per operation.
Method names ending in `V14` or `V12` are the historical pins. Complete data including standard deviation and Gen0 collections:
`summary.md` and `summary.json` of the run.

### Mapping: flat (ten scalars)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14 | 28.09 ns | 0.33 ns | 2.1% | 3.6% | 3.93 | 120 |
| Manual | 7.14 ns | 0.07 ns | 1.8% | 1.7% | 1.00 (baseline) | 120 |
| Mapperly | 7.20 ns | 0.12 ns | 3.0% | 3.6% | 1.01 | 120 |
| Mapster | 13.36 ns | 0.32 ns | 4.5% | 8.6% | 1.87 | 120 |
| MeridianCompiled | 27.38 ns | 0.33 ns | 2.1% | 4.3% | 3.83 | 320 |
| MeridianGeneratedDirect | 11.42 ns | 0.09 ns | 1.5% | 2.8% | 1.60 | 208 |
| MeridianGeneratedRuntime | 93.15 ns | 0.54 ns | 1.1% | 2.1% | 13.04 | 832 |
| MeridianInterpreted | 134.00 ns | 0.50 ns | 0.7% | 1.0% | 18.76 | 576 |

### Mapping: nested (ten renamed scalars plus one nested address)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14 | 38.70 ns | 0.28 ns | 1.4% | 1.9% | 2.80 | 184 |
| Manual | 13.82 ns | 0.30 ns | 4.0% | 4.6% | 1.00 (baseline) | 184 |
| Mapperly | 13.87 ns | 0.09 ns | 1.1% | 2.1% | 1.00 | 184 |
| Mapster | 19.68 ns | 0.11 ns | 1.0% | 1.7% | 1.42 | 184 |
| MeridianCompiled | 37.20 ns | 0.30 ns | 1.5% | 2.9% | 2.69 | 384 |
| MeridianGeneratedDirect | 18.21 ns | 0.33 ns | 3.4% | 6.5% | 1.32 | 272 |
| MeridianGeneratedRuntime | 119.76 ns | 0.45 ns | 0.7% | 1.0% | 8.67 | 984 |
| MeridianInterpreted | 305.31 ns | 10.00 ns | 6.1% | 14.4% | 22.09 | 728 |

### Mapping: collection payload (0, 1, 32, 256 lines, each with a nested product)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14(Items=0) | 48.08 ns | 0.42 ns | 1.6% | 1.7% | 4.13 | 104 |
| AutoMapperV14(Items=1) | 48.19 ns | 1.57 ns | 5.7% | 3.6% | 1.46 | 272 |
| AutoMapperV14(Items=256) | 3,312.25 ns | 40.35 ns | 2.2% | 4.3% | 1.23 | 33008 |
| AutoMapperV14(Items=32) | 476.92 ns | 5.31 ns | 2.0% | 2.0% | 1.40 | 4264 |
| Manual(Items=0) | 11.64 ns | 1.33 ns | 20.3% | 50.9% | 1.00 (baseline) | 104 |
| Manual(Items=1) | 33.01 ns | 1.79 ns | 9.8% | 10.4% | 1.00 (baseline) | 248 |
| Manual(Items=256) | 2,691.60 ns | 16.30 ns | 1.1% | 0.4% | 1.00 (baseline) | 30848 |
| Manual(Items=32) | 341.09 ns | 3.49 ns | 1.8% | 1.1% | 1.00 (baseline) | 3968 |
| Mapperly(Items=0) | 11.09 ns | 0.54 ns | 9.1% | 8.7% | 0.95 | 104 |
| Mapperly(Items=1) | 45.49 ns | 4.29 ns | 17.7% | 13.2% | 1.38 | 248 |
| Mapperly(Items=256) | 2,739.59 ns | 37.77 ns | 2.6% | 3.1% | 1.02 | 30848 |
| Mapperly(Items=32) | 343.82 ns | 6.21 ns | 3.3% | 5.2% | 1.01 | 3968 |
| Mapster(Items=0) | 32.79 ns | 2.85 ns | 15.9% | 23.9% | 2.82 | 104 |
| Mapster(Items=1) | 33.45 ns | 0.42 ns | 2.4% | 3.2% | 1.01 | 248 |
| Mapster(Items=256) | 2,863.09 ns | 33.20 ns | 2.1% | 1.3% | 1.06 | 30848 |
| Mapster(Items=32) | 366.43 ns | 2.89 ns | 1.5% | 1.1% | 1.07 | 3968 |
| MeridianCompiled(Items=0) | 221.23 ns | 11.15 ns | 9.5% | 5.9% | 19.01 | 576 |
| MeridianCompiled(Items=1) | 247.92 ns | 2.40 ns | 1.7% | 0.3% | 7.51 | 928 |
| MeridianCompiled(Items=256) | 8,141.75 ns | 59.83 ns | 1.3% | 1.3% | 3.02 | 60280 |
| MeridianCompiled(Items=32) | 1,283.89 ns | 23.72 ns | 3.5% | 3.9% | 3.76 | 8168 |
| MeridianGeneratedDirect(Items=0) | 15.56 ns | 0.32 ns | 3.7% | 8.0% | 1.34 | 192 |
| MeridianGeneratedDirect(Items=1) | 45.30 ns | 1.63 ns | 6.7% | 7.6% | 1.37 | 336 |
| MeridianGeneratedDirect(Items=256) | 3,450.45 ns | 53.52 ns | 2.9% | 4.5% | 1.28 | 30936 |
| MeridianGeneratedDirect(Items=32) | 435.45 ns | 6.91 ns | 3.0% | 3.4% | 1.28 | 4056 |
| MeridianGeneratedRuntime(Items=0) | 154.83 ns | 22.56 ns | 27.4% | 69.2% | 13.31 | 816 |
| MeridianGeneratedRuntime(Items=1) | 155.86 ns | 1.48 ns | 1.7% | 2.2% | 4.72 | 1176 |
| MeridianGeneratedRuntime(Items=256) | 11,827.54 ns | 113.51 ns | 1.8% | 1.5% | 4.39 | 76656 |
| MeridianGeneratedRuntime(Items=32) | 1,619.81 ns | 32.39 ns | 3.7% | 5.3% | 4.75 | 10352 |
| MeridianInterpreted(Items=0) | 204.57 ns | 2.88 ns | 2.6% | 4.0% | 17.58 | 576 |
| MeridianInterpreted(Items=1) | 533.37 ns | 143.76 ns | 49.3% | 137.5% | 16.16 | 1192 |
| MeridianInterpreted(Items=256) | 33,152.74 ns | 379.27 ns | 2.1% | 2.3% | 12.32 | 127864 |
| MeridianInterpreted(Items=32) | 4,388.89 ns | 40.21 ns | 1.6% | 3.0% | 12.87 | 16616 |

### Mapping: deep chain (4 and 16 levels)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14(Depth=16) | 578.48 ns | 6.52 ns | 2.1% | 4.3% | 9.15 | 888 |
| AutoMapperV14(Depth=4) | 110.19 ns | 0.52 ns | 0.8% | 1.4% | 6.97 | 224 |
| Manual(Depth=16) | 63.23 ns | 0.30 ns | 0.9% | 0.6% | 1.00 (baseline) | 888 |
| Manual(Depth=4) | 15.81 ns | 0.34 ns | 4.1% | 3.7% | 1.00 (baseline) | 224 |
| Mapperly(Depth=16) | 74.76 ns | 0.43 ns | 1.1% | 0.6% | 1.18 | 888 |
| Mapperly(Depth=4) | 16.86 ns | 0.31 ns | 3.3% | 3.4% | 1.07 | 224 |
| Mapster(Depth=16) | 163.12 ns | 0.94 ns | 1.1% | 0.9% | 2.58 | 888 |
| Mapster(Depth=4) | 25.27 ns | 0.60 ns | 4.5% | 10.8% | 1.60 | 224 |
| MeridianCompiled(Depth=16) | 171.73 ns | 0.69 ns | 0.8% | 0.9% | 2.72 | 1352 |
| MeridianCompiled(Depth=4) | 42.46 ns | 0.28 ns | 1.2% | 2.1% | 2.69 | 424 |
| MeridianGeneratedDirect(Depth=16) | 77.15 ns | 0.37 ns | 0.9% | 1.6% | 1.22 | 976 |
| MeridianGeneratedDirect(Depth=4) | 22.77 ns | 0.23 ns | 1.9% | 0.9% | 1.44 | 312 |
| MeridianGeneratedRuntime(Depth=16) | 415.62 ns | 1.08 ns | 0.5% | 0.2% | 6.57 | 2920 |
| MeridianGeneratedRuntime(Depth=4) | 158.59 ns | 2.96 ns | 3.5% | 7.2% | 10.03 | 1200 |
| MeridianInterpreted(Depth=16) | 1,801.68 ns | 18.02 ns | 1.9% | 3.8% | 28.50 | 3392 |
| MeridianInterpreted(Depth=4) | 323.17 ns | 2.25 ns | 1.3% | 2.5% | 20.44 | 1000 |

### Mapping: repeated-reference graph

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14 | 174.96 ns | 0.36 ns | 0.4% | 0.3% | 5.02 | 392 |
| Manual | 34.87 ns | 0.20 ns | 1.1% | 1.3% | 1.00 (baseline) | 320 |
| Mapperly | 144.61 ns | 1.27 ns | 1.6% | 3.4% | 4.15 | 848 |
| Mapster | 105.31 ns | 1.73 ns | 3.1% | 7.1% | 3.02 | 448 |
| MeridianGeneratedDirect | 55.19 ns | 0.32 ns | 1.1% | 1.3% | 1.58 | 456 |
| MeridianRuntime | 251.34 ns | 2.32 ns | 1.7% | 4.0% | 7.21 | 856 |

### Mediator: dispatch with no behaviors

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DirectHandler | 6.06 ns | 0.03 ns | 0.9% | 0.5% | 1.00 (baseline) | 96 |
| MediatRV12 | 33.06 ns | 0.32 ns | 1.9% | 2.7% | 5.46 | 224 |
| MeridianRuntime | 38.70 ns | 0.42 ns | 2.0% | 3.3% | 6.39 | 256 |
| MeridianStrictGenerated | 40.72 ns | 0.71 ns | 3.3% | 4.1% | 6.73 | 256 |

### Mediator: scoped handlers, existing scope (both handlers registered by type)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DiResolvedHandler | 19.67 ns | 0.27 ns | 2.6% | 1.9% | 1.00 (baseline) | 120 |
| MediatRV12 | 42.81 ns | 0.48 ns | 2.0% | 2.4% | 2.18 | 224 |
| MeridianRuntime | 47.61 ns | 0.84 ns | 3.3% | 6.1% | 2.42 | 256 |

### Mediator: scoped handlers, scope per operation (both handlers registered by type)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DiResolvedHandler | 81.22 ns | 1.48 ns | 3.5% | 4.3% | 1.00 (baseline) | 616 |
| MediatRV12 | 112.08 ns | 2.99 ns | 4.8% | 8.3% | 1.38 | 752 |
| MeridianRuntime | 133.87 ns | 1.35 ns | 1.9% | 1.6% | 1.65 | 832 |

### Mediator: closed and open pipelines (ratio is to the Meridian runtime row of the same shape)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12(Shape=1-closed) | 68.09 ns | 0.85 ns | 2.4% | 2.1% | 1.25 | 464 |
| MediatRV12(Shape=1-open) | 65.82 ns | 0.67 ns | 1.8% | 1.3% | 1.21 | 464 |
| MediatRV12(Shape=4-closed) | 115.40 ns | 1.23 ns | 1.9% | 2.5% | 1.20 | 896 |
| MediatRV12(Shape=4-open) | 114.32 ns | 1.61 ns | 2.5% | 2.8% | 1.17 | 896 |
| MediatRV12(Shape=none) | 32.45 ns | 0.39 ns | 2.2% | 2.7% | 0.82 | 224 |
| MeridianRuntime(Shape=1-closed) | 54.44 ns | 0.59 ns | 1.9% | 1.6% | 1.00 (baseline) | 416 |
| MeridianRuntime(Shape=1-open) | 54.19 ns | 0.35 ns | 1.1% | 0.6% | 1.00 (baseline) | 416 |
| MeridianRuntime(Shape=4-closed) | 95.80 ns | 1.34 ns | 2.5% | 3.7% | 1.00 (baseline) | 824 |
| MeridianRuntime(Shape=4-open) | 98.02 ns | 1.80 ns | 3.5% | 4.5% | 1.00 (baseline) | 824 |
| MeridianRuntime(Shape=none) | 39.43 ns | 1.05 ns | 5.0% | 8.9% | 1.00 (baseline) | 256 |

### Mediator: cache behavior overhead (in-process providers; not a Redis or production number)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| Send(Mode=disabled) | 30.50 ns | 0.22 ns | 1.3% | 1.1% | - | 160 |
| Send(Mode=primed-hit) | 96.81 ns | 1.45 ns | 2.8% | 4.8% | - | 576 |
| Send(Mode=synthetic-miss) | 92.15 ns | 0.99 ns | 1.9% | 2.7% | - | 600 |

### Mediator: idempotency behavior overhead (in-process stores; not a Redis or EF number)

The truncated `atomi(...)claim [22]` label below is the synthetic always-claim scenario; the retained report label is unchanged.

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| Send(Mode=atomi(...)claim [22]) | 104.88 ns | 1.27 ns | 2.2% | 2.5% | - | 656 |
| Send(Mode=atomic-replay) | 111.77 ns | 0.59 ns | 1.0% | 0.1% | - | 632 |
| Send(Mode=disabled) | 30.82 ns | 0.27 ns | 1.6% | 1.4% | - | 160 |
| Send(Mode=legacy-replay) | 101.11 ns | 1.57 ns | 2.9% | 4.0% | - | 608 |

### Mediator: metrics disabled and enabled

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| Send(Mode=disabled) | 38.40 ns | 0.52 ns | 2.5% | 1.9% | - | 256 |
| Send(Mode=enabled-listener) | 58.23 ns | 0.55 ns | 1.8% | 2.3% | - | 256 |
| Send(Mode=enabled-no-listener) | 39.14 ns | 0.48 ns | 2.2% | 0.6% | - | 256 |

### Notifications: three handlers, default sequential publisher

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12Publish | 78.77 ns | 0.82 ns | 1.9% | 1.1% | - | 592 |
| MeridianPublish | 78.47 ns | 1.00 ns | 2.4% | 1.6% | - | 560 |

### Notifications: task-when-all publisher, synchronous handlers (Meridian unbounded is the like-for-like row for MediatR; bounded is the default cap of 16, a different contract; ratio is to Meridian unbounded)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12TaskWhenAll(HandlerCount=10) | 244.56 ns | 3.39 ns | 2.6% | 2.1% | - | 1832 |
| MediatRV12TaskWhenAll(HandlerCount=100) | 1,982.83 ns | 38.00 ns | 3.5% | 5.5% | - | 14072 |
| MeridianTaskWhenAllBounded16(HandlerCount=10) | 428.81 ns | 19.47 ns | 8.3% | 19.5% | - | 1768 |
| MeridianTaskWhenAllBounded16(HandlerCount=100) | 3,550.38 ns | 38.79 ns | 2.0% | 2.3% | - | 14728 |
| MeridianTaskWhenAllUnbounded(HandlerCount=10) | 261.82 ns | 2.86 ns | 2.1% | 2.6% | - | 1680 |
| MeridianTaskWhenAllUnbounded(HandlerCount=100) | 2,228.86 ns | 28.12 ns | 2.4% | 0.5% | - | 14640 |

### Startup, warmed process: mapping (configure-only rows are not comparable across libraries, see notes)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperConfigure | 86,956.91 ns | 11,757.38 ns | 23.7% | 28.0% | - | 87203 |
| AutoMapperReady | 641.82 us | 88,926.60 ns | 25.7% | 26.1% | - | 182794 |
| MapsterConfigure | 990.91 us | 199.99 us | 38.4% | 4.3% | - | 905009 |
| MapsterReady | 923.26 us | 191.84 us | 38.5% | 57.9% | - | 912446 |
| MeridianConfigure | 1.147 ms | 62,511.87 ns | 10.2% | 11.9% | - | 234106 |
| MeridianGeneratedRuntimeConfigure | 160.15 ns | 0.92 ns | 1.0% | 1.0% | - | 1992 |
| MeridianGeneratedRuntimeReady | 288.27 ns | 1.37 ns | 0.9% | 1.3% | - | 3048 |
| MeridianReady | 1.186 ms | 70,037.71 ns | 11.2% | 15.6% | - | 235858 |

### Startup, warmed process: mediator

MediatR includes an extra assembly scan; these rows do not establish an equal-work startup ranking.

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12Configure | 5,712.63 ns | 40.50 ns | 1.3% | 2.3% | - | 29504 |
| MediatRV12Ready | 7,623.38 ns | 133.32 ns | 3.3% | 2.1% | - | 39002 |
| MeridianRuntimeConfigure | 543.87 ns | 31.80 ns | 10.7% | 14.9% | - | 3400 |
| MeridianRuntimeReady | 2,806.27 ns | 25.27 ns | 1.7% | 2.2% | - | 16968 |
| MeridianStrictGeneratedConfigure | 2,435.95 ns | 21.76 ns | 1.6% | 1.5% | - | 13648 |
| MeridianStrictGeneratedReady | 5,433.46 ns | 26.14 ns | 0.9% | 0.5% | - | 30032 |

### Cold: first use in a fresh process, mapping (10 fresh launches)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14ReadyFirstMap | 27.541 ms | 3.433 ms | 8.2% | 23.4% | 72.12 | 320408 |
| ManualFirstMap | 381.90 us | 56,220.17 ns | 9.7% | 38.3% | 1.00 (baseline) | 184 |
| MapperlyFirstMap | 379.76 us | 29,165.61 ns | 5.1% | 17.3% | 0.99 | 184 |
| MapsterReadyFirstMap | 34.139 ms | 1.941 ms | 3.8% | 10.0% | 89.39 | 1017832 |
| MeridianGeneratedDirectFirstMap | 854.86 us | 99,253.02 ns | 7.7% | 31.4% | 2.24 | 272 |
| MeridianGeneratedRuntimeReadyFirstMap | 6.317 ms | 312.75 us | 3.3% | 11.1% | 16.54 | 3080 |
| MeridianReadyFirstMap | 19.013 ms | 1.887 ms | 6.6% | 24.4% | 49.79 | 249488 |

### Cold: first use in a fresh process, mediator (10 fresh launches)

MediatR includes an extra assembly scan. The direct-handler floor is unstable (CV 31.7%, launch spread 139.4%),
so derived ratios are omitted; these means cannot support an equal-work startup ranking.

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DirectHandlerFirstCall | 559.04 us | 267.96 us | 31.7% | 139.4% | - | 120 |
| MediatRV12ReadyFirstSend | 12.483 ms | 2.571 ms | 13.6% | 50.8% | - | 56240 |
| MeridianRuntimeReadyFirstSend | 16.541 ms | 2.762 ms | 11.0% | 37.7% | - | 18720 |
| MeridianStrictGeneratedReadyFirstSend | 28.215 ms | 1.772 ms | 4.2% | 13.7% | - | 41864 |

## Not covered (recorded, not hidden)

- No quiet or dedicated window (AC power, but load never below 3.6); time results are INCONCLUSIVE. B8 pair PENDING. D5 enabled-without-listener mode not re-measured.
- No packed-package run, no other operating system or architecture, no net8.0 or net9.0 runtime (the benchmark project targets net10.0).
- Omitted scenarios (existing-destination mapping, generated runtime integration for reference graphs, Mapperly startup, source-generator
  build time, assembly-scanning registration, asynchronous or I/O handlers) are listed with reasons in the benchmark README.
- Cold rows are a single run of ten fresh launches and are dominated by JIT; treat them as order of magnitude.

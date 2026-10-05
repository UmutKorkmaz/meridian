# Benchmark results (E3, 2026-10-05)

Selected, reproducible summary of the E3 performance evidence for `Meridian.Mapping` and `Meridian.Mediator`.
**This is not a performance claim.** Every figure below was measured once, on one machine, on battery power and under load
(see [Measurement window](#measurement-window)), so **all time figures are INCONCLUSIVE as release evidence**: they are
indicative for this host and window only. Allocation figures (`Alloc B/op`) are deterministic and host independent.
Nothing here is a statement about other hardware, other runtimes, released packages, asynchronous or I/O-bound handlers, or
the current (commercially licensed) AutoMapper and MediatR releases. How the scenarios work and what they omit is documented in
[../Meridian.Benchmarks/README.md](../Meridian.Benchmarks/README.md).

The historical April 2026 numbers in the benchmark README were produced by an earlier harness (different payload, host and
SDK, `--job short`); they are **historical** and are not comparable with anything on this page.

## Reproduce

```powershell
pwsh eng/run-benchmarks.ps1 -Filter '*.E3*' -OutputDirectory artifacts/E3/my-run -WindowName 'my named window'
```

Raw BenchmarkDotNet output of the run summarized here is kept (not committed; `artifacts/` is ignored) in
`artifacts/E3/w1-e3-full/` of the evaluated worktree: `bdn/results/*-report-full.json` (every measurement of every launch),
`manifest.json`, `summary.md`, `summary.json`, `build.log`, `preflight.log`, `run.log`. Results differ between machines;
a different host will not reproduce these numbers, only their structure.

| | |
| --- | --- |
| Run id | `0ce42f200fab4ff3a6ee44e990b6c4ba` (profile `Fixed`, filter `*.E3*`, 131 benchmark cases: 120 hot, 11 cold) |
| Candidate | project references to source; git HEAD `2e19ab7410e8774f8feb002790844b7363b30551` plus the inherited uncommitted worktree (git status sha256 `dd7cfaa5...cef`); product source sha256 `00cb0758331bfcac708d21f68381d70c1e6c15c0c8aaab70753e6808e5820f3d`; harness sha256 `a47c5b58e8d689e3dceb232ad4088109a53a96401c6e773b7e38b0f3314c1719`. **Source lane only: no packed-package run exists.** |
| Toolchain | .NET SDK 10.0.300, runtime 10.0.8 (Arm64 RyuJIT), BenchmarkDotNet 0.15.8, concurrent server GC, tiered compilation on |
| Job | hot: 3 launches, 6 warmup, 15 iterations of 250 ms; cold: 10 fresh launches, 1 invocation each; `MemoryDiagnoser` |
| Preflight | passed for all 18 scenario classes before timing (`preflight.log`); its self-test rejects 10 classes of wrong result |
| Competitors | Mapster 10.0.13 (MIT), Riok.Mapperly 4.3.1 (Apache-2.0), **AutoMapper 14.0.0 (MIT, historical pin, advisory GHSA-rvv3-g6hj-g44x)**, **MediatR 12.5.0 (Apache-2.0, historical pin; not MIT)** |

## Measurement window

| | |
| --- | --- |
| Name | E3-W1, this Mac (`DTUMKORKMAZMACOS`, Apple M5 Pro, 18 logical cores, 48 GB, macOS 26.5.2) |
| Interval | 2026-10-05 10:47 to 11:36 (+03:00), 2959 s timed |
| Power | **battery throughout**, 54% falling to 31% (not plugged in) |
| Load | 1-minute load average 5.9 at the start and 4.4 at the end; 98 samples every 30 s: minimum 2.1, median 4.5, **maximum 8.2** |
| Other activity | a Docker/Virtualization VM with the user's containers running, WindowServer, Claude and Codex helpers, other agents and editors; none were stopped. `manifest.json` lists the busiest processes |

Quiet criteria declared before measuring (AC power and 1-minute load at most 3 for the whole timed region) were **not met**, so
this is not an exclusive or quiet window, and the host was not dedicated. Time results are therefore INCONCLUSIVE. For
orientation only: most hot rows have a coefficient of variation of 1 to 2% and a launch spread below 4% (median launch spread
of all rows 2.2%), which is consistent with a usable measurement but does not turn the window into a quiet one. Cold rows
(one invocation per launch) vary much more.

## Findings and decisions

No baseline exists for the new E3 rows, so there is no regression decision for them; the entries below record what the data
shows, including unfavorable workloads. Owners decide whether any of it needs work. All statements are for this window.

1. **Mapping, Meridian runtime versus the others.** Mapperly is indistinguishable from the hand-written floor (ratio 0.99 to
   1.18) and Mapster is 1.05 to 2.6 times the floor (3.0 on the reference graph). Meridian compiled is 2.6 to 3.9 times the
   floor on flat, nested and deep payloads; on collections it carries a fixed cost (about 300 ns for an empty basket), so its
   ratio ranges from 3 (256 lines) to 21 (empty). Meridian interpreted is 19 to 29 times the floor on single objects.
   AutoMapper 14.0.0 is roughly equal to Meridian compiled on flat and nested payloads (43 and 57 ns versus 40 and 52 ns), faster
   on collections (32 lines: 678 ns versus 1,814 ns) and slower on the deep chains (16 levels: 861 ns versus 258 ns).
   Unfavorable for Meridian: the compiled path allocates 320 B on the flat payload against 120 B for the floor, Mapster and Mapperly.
2. **Meridian generated mapping.** The direct generated call is 1.2 to 1.6 times the floor and allocates exactly 88 B more than
   the floor in every non-reference shape (flat 208 B versus 120 B; consistent with the per-call generated context described in the generated-mapping
   contract, not separately profiled). The generated runtime integration (`UseGeneratedMappings`) is 4 to 14 times the floor: it measures the resolver bridge, which is a different row from the direct call.
3. **Reference graphs.** With identity preserved by every contender, the generated direct call is 1.6 times the hand-written
   floor, Mapster 3.0, Mapperly 4.3, AutoMapper 14 5.1 and Meridian runtime 7.2.
4. **Mediator dispatch.** Direct handler 8.5 ns; MediatR 12.5.0 47.0 ns (224 B); Meridian runtime 54.8 ns (256 B); Meridian strict
   generated 59.7 ns (256 B). In this window Meridian is about 17% slower than MediatR 12.5.0 on a plain send, and strict
   generated dispatch is not faster than the runtime path (about 9% slower, 2% CV on each). With one or four pass-through
   behaviors Meridian is faster (76 versus 98 ns and 139 versus 162 ns); closed and open registration are indistinguishable for
   both libraries. With one scope per operation Meridian is slower (205 versus 161 ns); inside an existing scope it is also
   slower (75 versus 60 ns).
5. **Notifications.** Default sequential publication is equal within noise (115 versus 113 ns). The opt-in task-when-all
   publisher is about twice as slow as MediatR's in Meridian (697 versus 355 ns for 10 handlers, 6.3 versus 2.7 us for 100);
   handlers complete synchronously, so this is the publisher's own overhead and says nothing about asynchronous handlers. The
   controller ruling on multi-fault preservation in the publisher (progress ledger) named this path for allocation measurement; allocation is
   1,768 B versus 1,832 B (10 handlers) and 14,728 B versus 14,072 B (100): no regression decision is possible without a
   baseline, and it is recorded as a follow-up.
6. **Behavior overhead (in-process providers).** A primed in-memory cache hit costs 138 ns against 45 ns with no behavior
   (+576 B versus 160 B); a synthetic miss 132 ns (+600 B); idempotency replay 145 to 160 ns and a synthetic new claim 147 ns
   (608 to 656 B). These are behavior overhead, not provider latency.
7. **Metrics.** Enabled-without-listener (55.0 ns, 256 B) is indistinguishable from disabled (55.5 ns, 256 B); an attached
   in-process listener adds about 25 ns and no allocation. The exact expected events were verified before timing.
8. **Startup.** Warmed process, ready for the first map: AutoMapper 14.0.0 0.91 ms, Mapster 1.48 ms, Meridian runtime 1.60 ms (236 KB
   allocated), Meridian generated runtime 0.42 us (nothing to compile). First mediator send including provider build: Meridian runtime 4.0 us,
   Meridian strict generated 7.8 us, MediatR 12.5.0 11.0 us. **The "configure" rows are not comparable across libraries**:
   AutoMapper defers compilation to the first map, Mapster compiles eagerly, Meridian compiles at construction; compare the "ready"
   rows. Cold, fresh process, first map including configuration: Meridian generated runtime 8.6 ms, Meridian runtime 27.0 ms, AutoMapper
   36.8 ms, Mapster 47.8 ms; first mediator send: MediatR 15.8 ms, Meridian runtime 23.1 ms, Meridian strict generated 39.2 ms (an
   unfavorable result; the generated registry path does more first-use work than the reflection path). Cold rows include JIT and are
   dominated by it; the direct-handler cold row has a coefficient of variation above 100%.

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
(only the metrics-disabled rows were), so its earlier D5 result stands as recorded in the D5 report. Raw artifacts:
`artifacts/E3/d5/seg{1,2,3,4}-*/` (`manifest.json`, `bdn/results`, `summary.md`) and the comparisons `cmp-*.md`.

### B8 nested fast-path pair: PENDING

The B8 report made an exclusive-window rerun of the frozen `H1` harness against the preserved P0 and the current product a blocking
gate for any published B8 performance claim. It was **not run**: after the E3 run (about 50 minutes) and the D5 re-measurement
the laptop was on battery at 19% and about 40 minutes of runtime, the machine runs the user's containers, and a longer run would risk
losing them. B8 therefore stays **PENDING**; no B8 time figure may be quoted. The trees and the runner are prepared
(`artifacts/E3/make-trees.sh`, `artifacts/E3/run-pairs.sh b8`): run it on AC power with competing work paused. Allocation
evidence for B8 (deterministic, minus 88 B per inlined nested map) is unchanged and lives in the B8 report.

## Results

Columns: mean, half-width of the 99.9% confidence interval, coefficient of variation across measured iterations, spread between
the three (or ten) launch means, `Ratio` to the floor of the same class and parameters (hand-written code, direct handler or, for pipelines,
the Meridian runtime row; derived from the means, no error bar), allocated bytes per operation.
Method names ending in `V14` or `V12` are the historical pins. Complete data including standard deviation and Gen0 collections:
`summary.md` and `summary.json` of the run.

### Mapping: flat (ten scalars)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14 | 42.67 ns | 0.62 ns | 2.8% | 4.8% | 4.13 | 120 |
| Manual | 10.33 ns | 0.03 ns | 0.6% | 0.6% | 1.00 (baseline) | 120 |
| Mapperly | 10.46 ns | 0.13 ns | 2.2% | 2.8% | 1.01 | 120 |
| Mapster | 19.72 ns | 0.34 ns | 3.3% | 7.9% | 1.91 | 120 |
| MeridianCompiled | 40.43 ns | 0.60 ns | 2.7% | 6.6% | 3.91 | 320 |
| MeridianGeneratedDirect | 16.85 ns | 0.10 ns | 1.1% | 1.8% | 1.63 | 208 |
| MeridianGeneratedRuntime | 140.19 ns | 4.59 ns | 6.0% | 12.3% | 13.57 | 832 |
| MeridianInterpreted | 194.56 ns | 1.02 ns | 1.0% | 1.9% | 18.83 | 576 |

### Mapping: nested (ten renamed scalars plus one nested address)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14 | 57.22 ns | 0.45 ns | 1.5% | 2.7% | 2.88 | 184 |
| Manual | 19.84 ns | 0.17 ns | 1.6% | 3.2% | 1.00 (baseline) | 184 |
| Mapperly | 20.36 ns | 0.07 ns | 0.6% | 0.3% | 1.03 | 184 |
| Mapster | 28.75 ns | 0.08 ns | 0.5% | 0.2% | 1.45 | 184 |
| MeridianCompiled | 52.14 ns | 0.34 ns | 1.2% | 2.1% | 2.63 | 384 |
| MeridianGeneratedDirect | 26.78 ns | 0.17 ns | 1.2% | 2.0% | 1.35 | 272 |
| MeridianGeneratedRuntime | 172.95 ns | 0.35 ns | 0.4% | 0.3% | 8.72 | 984 |
| MeridianInterpreted | 446.69 ns | 15.24 ns | 6.3% | 15.7% | 22.51 | 728 |

### Mapping: collection payload (0, 1, 32, 256 lines, each with a nested product)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14(Items=0) | 47.14 ns | 0.50 ns | 2.0% | 3.0% | 3.38 | 104 |
| AutoMapperV14(Items=1) | 68.37 ns | 0.21 ns | 0.6% | 0.6% | 2.15 | 272 |
| AutoMapperV14(Items=256) | 4,792.61 ns | 31.63 ns | 1.3% | 2.6% | 1.23 | 33008 |
| AutoMapperV14(Items=32) | 677.60 ns | 2.41 ns | 0.7% | 0.5% | 1.37 | 4264 |
| Manual(Items=0) | 13.97 ns | 0.07 ns | 1.0% | 1.2% | 1.00 (baseline) | 104 |
| Manual(Items=1) | 31.87 ns | 0.20 ns | 1.2% | 2.0% | 1.00 (baseline) | 248 |
| Manual(Items=256) | 3,900.77 ns | 14.72 ns | 0.7% | 0.3% | 1.00 (baseline) | 30848 |
| Manual(Items=32) | 495.67 ns | 4.32 ns | 1.6% | 3.3% | 1.00 (baseline) | 3968 |
| Mapperly(Items=0) | 15.50 ns | 0.11 ns | 1.3% | 2.1% | 1.11 | 104 |
| Mapperly(Items=1) | 32.02 ns | 0.10 ns | 0.6% | 0.9% | 1.00 | 248 |
| Mapperly(Items=256) | 3,890.88 ns | 13.81 ns | 0.7% | 0.4% | 1.00 | 30848 |
| Mapperly(Items=32) | 493.07 ns | 1.72 ns | 0.7% | 0.1% | 0.99 | 3968 |
| Mapster(Items=0) | 30.70 ns | 0.62 ns | 3.8% | 9.1% | 2.20 | 104 |
| Mapster(Items=1) | 47.35 ns | 0.40 ns | 1.6% | 1.8% | 1.49 | 248 |
| Mapster(Items=256) | 4,100.45 ns | 17.25 ns | 0.8% | 0.9% | 1.05 | 30848 |
| Mapster(Items=32) | 525.60 ns | 1.76 ns | 0.6% | 0.5% | 1.06 | 3968 |
| MeridianCompiled(Items=0) | 299.98 ns | 2.38 ns | 1.5% | 3.0% | 21.48 | 576 |
| MeridianCompiled(Items=1) | 363.91 ns | 3.57 ns | 1.8% | 2.7% | 11.42 | 928 |
| MeridianCompiled(Items=256) | 11.82 us | 50.07 ns | 0.8% | 1.0% | 3.03 | 60280 |
| MeridianCompiled(Items=32) | 1,813.51 ns | 5.40 ns | 0.5% | 0.3% | 3.66 | 8168 |
| MeridianGeneratedDirect(Items=0) | 22.38 ns | 0.13 ns | 1.1% | 1.4% | 1.60 | 192 |
| MeridianGeneratedDirect(Items=1) | 43.87 ns | 0.21 ns | 0.9% | 0.9% | 1.38 | 336 |
| MeridianGeneratedDirect(Items=256) | 4,924.06 ns | 36.35 ns | 1.4% | 2.6% | 1.26 | 30936 |
| MeridianGeneratedDirect(Items=32) | 627.60 ns | 3.17 ns | 0.9% | 1.2% | 1.27 | 4056 |
| MeridianGeneratedRuntime(Items=0) | 151.49 ns | 3.51 ns | 4.3% | 5.8% | 10.85 | 816 |
| MeridianGeneratedRuntime(Items=1) | 225.03 ns | 0.79 ns | 0.7% | 0.9% | 7.06 | 1176 |
| MeridianGeneratedRuntime(Items=256) | 17.25 us | 150.09 ns | 1.6% | 3.4% | 4.42 | 76656 |
| MeridianGeneratedRuntime(Items=32) | 2,292.78 ns | 23.04 ns | 1.9% | 3.9% | 4.63 | 10352 |
| MeridianInterpreted(Items=0) | 298.81 ns | 2.16 ns | 1.3% | 2.3% | 21.39 | 576 |
| MeridianInterpreted(Items=1) | 523.85 ns | 2.50 ns | 0.9% | 1.1% | 16.43 | 1192 |
| MeridianInterpreted(Items=256) | 48.83 us | 269.88 ns | 1.0% | 2.2% | 12.52 | 127864 |
| MeridianInterpreted(Items=32) | 6,488.90 ns | 31.12 ns | 0.9% | 1.5% | 13.09 | 16616 |

### Mapping: deep chain (4 and 16 levels)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14(Depth=16) | 860.75 ns | 6.70 ns | 1.4% | 3.2% | 9.19 | 888 |
| AutoMapperV14(Depth=4) | 170.34 ns | 3.90 ns | 4.1% | 9.1% | 7.55 | 224 |
| Manual(Depth=16) | 93.70 ns | 0.80 ns | 1.6% | 1.5% | 1.00 (baseline) | 888 |
| Manual(Depth=4) | 22.57 ns | 0.09 ns | 0.7% | 1.0% | 1.00 (baseline) | 224 |
| Mapperly(Depth=16) | 110.70 ns | 0.58 ns | 1.0% | 0.4% | 1.18 | 888 |
| Mapperly(Depth=4) | 23.70 ns | 0.09 ns | 0.7% | 1.3% | 1.05 | 224 |
| Mapster(Depth=16) | 241.41 ns | 1.26 ns | 1.0% | 1.0% | 2.58 | 888 |
| Mapster(Depth=4) | 38.22 ns | 0.77 ns | 3.9% | 9.6% | 1.69 | 224 |
| MeridianCompiled(Depth=16) | 258.37 ns | 1.48 ns | 1.0% | 2.3% | 2.76 | 1352 |
| MeridianCompiled(Depth=4) | 61.67 ns | 0.25 ns | 0.7% | 0.1% | 2.73 | 424 |
| MeridianGeneratedDirect(Depth=16) | 114.27 ns | 0.37 ns | 0.6% | 0.8% | 1.22 | 976 |
| MeridianGeneratedDirect(Depth=4) | 33.00 ns | 0.18 ns | 1.0% | 1.1% | 1.46 | 312 |
| MeridianGeneratedRuntime(Depth=16) | 622.34 ns | 2.80 ns | 0.8% | 1.5% | 6.64 | 2920 |
| MeridianGeneratedRuntime(Depth=4) | 232.45 ns | 3.08 ns | 2.5% | 5.5% | 10.30 | 1200 |
| MeridianInterpreted(Depth=16) | 2,729.73 ns | 28.29 ns | 1.9% | 3.7% | 29.13 | 3392 |
| MeridianInterpreted(Depth=4) | 489.30 ns | 4.71 ns | 1.8% | 4.2% | 21.68 | 1000 |

### Mapping: repeated-reference graph

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14 | 257.00 ns | 1.34 ns | 1.0% | 2.2% | 5.10 | 392 |
| Manual | 50.41 ns | 0.29 ns | 1.1% | 2.0% | 1.00 (baseline) | 320 |
| Mapperly | 214.74 ns | 2.65 ns | 2.3% | 2.6% | 4.26 | 848 |
| Mapster | 151.79 ns | 1.09 ns | 1.4% | 1.8% | 3.01 | 448 |
| MeridianGeneratedDirect | 81.92 ns | 1.22 ns | 2.8% | 5.4% | 1.63 | 456 |
| MeridianRuntime | 360.54 ns | 4.28 ns | 2.2% | 5.4% | 7.15 | 856 |

### Mediator: dispatch with no behaviors

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DirectHandler | 8.46 ns | 0.03 ns | 0.6% | 1.0% | 1.00 (baseline) | 96 |
| MediatRV12 | 46.97 ns | 0.16 ns | 0.6% | 0.2% | 5.55 | 224 |
| MeridianRuntime | 54.76 ns | 0.61 ns | 2.0% | 3.3% | 6.47 | 256 |
| MeridianStrictGenerated | 59.73 ns | 0.67 ns | 2.1% | 3.7% | 7.06 | 256 |

### Mediator: scoped handlers, existing scope

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DiResolvedHandler | 28.58 ns | 0.17 ns | 1.1% | 1.5% | 1.00 (baseline) | 120 |
| MediatRV12 | 60.16 ns | 0.55 ns | 1.7% | 3.2% | 2.11 | 224 |
| MeridianRuntime | 74.94 ns | 0.46 ns | 1.2% | 1.8% | 2.62 | 256 |

### Mediator: scoped handlers, scope per operation

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DiResolvedHandler | 112.85 ns | 1.36 ns | 2.3% | 3.9% | 1.00 (baseline) | 616 |
| MediatRV12 | 161.31 ns | 1.56 ns | 1.7% | 2.8% | 1.43 | 752 |
| MeridianRuntime | 204.88 ns | 1.18 ns | 1.1% | 1.6% | 1.82 | 832 |

### Mediator: closed and open pipelines (ratio is to the Meridian runtime row of the same shape)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12(Shape=1-closed) | 98.09 ns | 0.94 ns | 1.8% | 1.6% | 1.29 | 464 |
| MediatRV12(Shape=1-open) | 96.34 ns | 1.04 ns | 2.0% | 3.7% | 1.26 | 464 |
| MediatRV12(Shape=4-closed) | 162.11 ns | 1.07 ns | 1.2% | 1.6% | 1.17 | 896 |
| MediatRV12(Shape=4-open) | 163.42 ns | 1.67 ns | 1.9% | 2.9% | 1.20 | 896 |
| MediatRV12(Shape=none) | 46.89 ns | 0.25 ns | 1.0% | 1.2% | 0.84 | 224 |
| MeridianRuntime(Shape=1-closed) | 76.15 ns | 0.64 ns | 1.5% | 1.4% | 1.00 (baseline) | 416 |
| MeridianRuntime(Shape=1-open) | 76.68 ns | 0.66 ns | 1.6% | 1.4% | 1.00 (baseline) | 416 |
| MeridianRuntime(Shape=4-closed) | 138.66 ns | 1.70 ns | 2.3% | 4.1% | 1.00 (baseline) | 824 |
| MeridianRuntime(Shape=4-open) | 135.81 ns | 1.61 ns | 2.2% | 4.5% | 1.00 (baseline) | 824 |
| MeridianRuntime(Shape=none) | 55.50 ns | 0.26 ns | 0.9% | 1.1% | 1.00 (baseline) | 256 |

### Mediator: cache behavior overhead (in-process providers; not a Redis or production number)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| Send(Mode=disabled) | 44.94 ns | 0.24 ns | 1.0% | 1.6% | - | 160 |
| Send(Mode=primed-hit) | 138.38 ns | 1.80 ns | 2.4% | 3.0% | - | 576 |
| Send(Mode=synthetic-miss) | 131.57 ns | 1.83 ns | 2.6% | 5.2% | - | 600 |

### Mediator: idempotency behavior overhead (in-process stores; not a Redis or EF number)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| Send(Mode=atomi(...)claim [22]) | 146.96 ns | 1.23 ns | 1.5% | 2.2% | - | 656 |
| Send(Mode=atomic-replay) | 159.78 ns | 0.78 ns | 0.9% | 0.7% | - | 632 |
| Send(Mode=disabled) | 44.94 ns | 0.43 ns | 1.7% | 3.1% | - | 160 |
| Send(Mode=legacy-replay) | 145.14 ns | 1.50 ns | 1.9% | 4.2% | - | 608 |

### Mediator: metrics disabled and enabled

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| Send(Mode=disabled) | 55.48 ns | 1.41 ns | 4.6% | 9.4% | - | 256 |
| Send(Mode=enabled-listener) | 80.44 ns | 0.63 ns | 1.5% | 2.8% | - | 256 |
| Send(Mode=enabled-no-listener) | 55.02 ns | 0.34 ns | 1.2% | 1.5% | - | 256 |

### Notifications: three handlers, default sequential publisher

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12Publish | 112.69 ns | 0.78 ns | 1.3% | 1.6% | - | 592 |
| MeridianPublish | 115.01 ns | 3.25 ns | 5.2% | 9.5% | - | 560 |

### Notifications: task-when-all publisher, synchronous handlers

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12TaskWhenAll(HandlerCount=10) | 355.18 ns | 4.60 ns | 2.4% | 3.5% | - | 1832 |
| MediatRV12TaskWhenAll(HandlerCount=100) | 2,739.28 ns | 14.75 ns | 1.0% | 0.2% | - | 14072 |
| MeridianTaskWhenAll(HandlerCount=10) | 697.22 ns | 5.43 ns | 1.5% | 3.1% | - | 1768 |
| MeridianTaskWhenAll(HandlerCount=100) | 6,325.97 ns | 16.97 ns | 0.5% | 0.6% | - | 14728 |

### Startup, warmed process: mapping (configure-only rows are not comparable across libraries, see notes)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperConfigure | 145.17 us | 26.77 us | 33.7% | 37.1% | - | 87493 |
| AutoMapperReady | 913.64 us | 124.11 us | 25.2% | 15.3% | - | 183518 |
| MapsterConfigure | 1.515 ms | 258.18 us | 32.4% | 15.3% | - | 905880 |
| MapsterReady | 1.478 ms | 272.32 us | 35.1% | 17.4% | - | 905984 |
| MeridianConfigure | 1.577 ms | 62.17 us | 7.5% | 1.6% | - | 234579 |
| MeridianGeneratedRuntimeConfigure | 232.02 ns | 0.97 ns | 0.8% | 0.9% | - | 1992 |
| MeridianGeneratedRuntimeReady | 417.22 ns | 1.13 ns | 0.5% | 0.5% | - | 3048 |
| MeridianReady | 1.600 ms | 59.65 us | 7.1% | 2.8% | - | 236147 |

### Startup, warmed process: mediator

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| MediatRV12Configure | 8,259.80 ns | 29.89 ns | 0.7% | 0.8% | - | 29504 |
| MediatRV12Ready | 10.97 us | 90.88 ns | 1.6% | 3.2% | - | 39001 |
| MeridianRuntimeConfigure | 755.70 ns | 3.71 ns | 0.9% | 1.4% | - | 3400 |
| MeridianRuntimeReady | 3,972.26 ns | 24.06 ns | 1.2% | 1.0% | - | 16968 |
| MeridianStrictGeneratedConfigure | 3,514.40 ns | 20.34 ns | 1.1% | 1.5% | - | 13648 |
| MeridianStrictGeneratedReady | 7,840.95 ns | 38.16 ns | 0.9% | 0.4% | - | 30032 |

### Cold: first use in a fresh process, mapping (10 fresh launches)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| AutoMapperV14ReadyFirstMap | 36.779 ms | 3.478 ms | 6.3% | 21.8% | 68.90 | 320672 |
| ManualFirstMap | 533.82 us | 34.56 us | 4.3% | 16.2% | 1.00 (baseline) | 184 |
| MapperlyFirstMap | 536.46 us | 43.45 us | 5.4% | 17.4% | 1.00 | 184 |
| MapsterReadyFirstMap | 47.828 ms | 5.789 ms | 8.0% | 27.5% | 89.60 | 1042744 |
| MeridianGeneratedDirectFirstMap | 1.141 ms | 146.17 us | 8.5% | 34.9% | 2.14 | 272 |
| MeridianGeneratedRuntimeReadyFirstMap | 8.562 ms | 1.489 ms | 11.5% | 39.5% | 16.04 | 3080 |
| MeridianReadyFirstMap | 26.996 ms | 6.046 ms | 14.8% | 49.0% | 50.57 | 248352 |

### Cold: first use in a fresh process, mediator (10 fresh launches)

| Scenario | Mean | +- 99.9% CI | CV | Launch spread | Ratio | Alloc B/op |
|---|---:|---:|---:|---:|---:|---:|
| DirectHandlerFirstCall | 1.081 ms | 1.926 ms | 117.9% | 664.2% | 1.00 (baseline) | 120 |
| MediatRV12ReadyFirstSend | 15.813 ms | 535.75 us | 2.2% | 8.0% | 14.63 | 43904 |
| MeridianRuntimeReadyFirstSend | 23.121 ms | 2.788 ms | 8.0% | 28.2% | 21.39 | 18720 |
| MeridianStrictGeneratedReadyFirstSend | 39.181 ms | 1.262 ms | 2.1% | 5.8% | 36.25 | 29528 |


## Not covered (recorded, not hidden)

- No quiet or dedicated window; time results are INCONCLUSIVE. B8 pair PENDING. D5 enabled-without-listener mode not re-measured.
- No packed-package run, no other operating system or architecture, no net8.0 or net9.0 runtime (the benchmark project targets net10.0).
- Omitted scenarios (existing-destination mapping, generated runtime integration for reference graphs, Mapperly startup, source-generator
  build time, assembly-scanning registration, asynchronous or I/O handlers) are listed with reasons in the benchmark README.
- Cold rows are a single run of ten fresh launches and are dominated by JIT; treat them as order of magnitude.

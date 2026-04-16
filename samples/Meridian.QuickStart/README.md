# Meridian.QuickStart

Smallest possible runnable sample that exercises every public Meridian
feature end-to-end. Doubles as a smoke test that the public API is
usable from a fresh consumer project.

```bash
dotnet run --project samples/Meridian.QuickStart -c Release
```

Output:

```text
✓ Mediator pipeline (correlation + audit + localised validation)
✓ Runtime mapper (config-time compiled fast path)
✓ Source-generated mapper ([GenerateMapper] attribute)
✓ Turkish culture helpers (dotted/dotless I)
✓ Safety defaults (depth cap returns truncated, collection cap throws)

All Meridian QuickStart demos completed successfully.
```

Exit code **0** when every demo passes. Exit code **1** with a `Failures:`
report when one breaks — the failing feature is identified by name so
CI can surface it without parsing logs.

## What each demo proves

1. **Mediator pipeline** — `services.AddMeridianStandard(typeof(Program).Assembly)`
   wires correlation → audit → localised validation in the right order;
   the standard pipeline auto-discovers handlers AND validators from
   the assembly.
2. **Runtime mapper** — `MapperConfiguration` + `.ForMember + .MapFrom`,
   exercises the runtime fast-path compiler that ships in Phase 3.6.
3. **Source-generated mapper** — `[GenerateMapper(...)]` on a partial
   class, calling the generated `MapToProductDto` method directly. No
   reflection, AOT-safe.
4. **Turkish culture** — `TurkishCulture.ToUpper("istanbul") == "İSTANBUL"`,
   the canonical dotted/dotless I bug that invariant case folding gets
   wrong.
5. **Safety defaults** — pushes a 1000-deep graph (truncates to 64 by
   default) and an 11k-item collection (throws
   `MeridianMappingLimitException`).

## Why this is in CI

If a refactor breaks the public API in a way the unit tests don't
catch — for example, a missing extension method, a moved namespace, or
a behaviour that compiles but no longer registers — this sample will
fail the CI build with a clear feature-level failure message. It costs
~2 seconds per run and catches a real class of regressions.

# Meridian Roadmap

Status as of 2026-07-21. **No breaking public API or default-behavior changes** without a major version bump. See [compatibility-policy.md](./compatibility-policy.md).

This historical snapshot is superseded by the maintained [capability ledger](./capabilities.md) and
[required checks](./governance/required-checks.md). Its old open rows do not describe today's missing features or proof.

## Now (P0–P1) — adoption & truthfulness

- [x] Restore `docs/PLAN.md` / this roadmap
- [x] Align samples inventory (including Hexagonal)
- [x] Fix docs drift (license footer, NOTICE, CHANGELOG anchors, CONTRIBUTING claims)
- [x] Product-oriented root README + package READMEs
- [x] Migration guides (MediatR, AutoMapper)
- [x] Compatibility policy document
- [ ] Optional: DocFX API site generated from XML docs

## Next (P2) — product bets without breaking callers

| Item | Compatibility stance |
| ---- | -------------------- |
| **P3.7** Inline nested fast paths when both TypeMaps are fast-pathable | Perf-only; same mapping results |
| **Source-gen Phase 5+** Nested types, collections, basic conversions | Opt-in attributes only; runtime `IMapper` unchanged |
| Mapperly / Mapster benchmark rows | Docs/bench only |
| Optional `Meridian.Mediator.FluentValidation` adapter package | New package; core unchanged |
| Coverage publishing / soft gate on hot paths | CI only |

## Later (P3) — distribution

- ASP.NET Core sample (minimal API host)
- Comparison blog post + Pages site expansion
- NuGet verified publisher / brand assets
- Second maintainer onboarding (PLAN §8.4)

## Explicitly deferred

- Changing default `DefaultMaxDepth` / `DefaultMaxCollectionItems`
- Changing default telemetry privacy flags
- Removing MediatR/AutoMapper-shaped compatibility shims without a major version
- Unbounded notification fan-out defaults (remain capped unless caller opts out)

## Release discipline

1. Additive APIs preferred on minor releases.
2. Behavioral default changes require major version + CHANGELOG `BREAKING`.
3. Security fixes may tighten validation when required; document consumer opt-outs.

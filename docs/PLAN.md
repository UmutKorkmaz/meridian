# Meridian Plan (historical)

This document records the phased plan that shaped Meridian 2.x.
Forward work lives in [ROADMAP.md](./ROADMAP.md).
Compatibility rules live in [compatibility-policy.md](./compatibility-policy.md).

## Goals

1. Ship a lightweight **Mediator** and **Mapping** pair for .NET 8–10.
2. Prefer **safe-by-default** behavior on attacker-shaped inputs (depth, collection width, telemetry privacy).
3. Keep hot paths allocation-aware without breaking public APIs.
4. Provide architecture samples, benchmarks, and supply-chain hygiene suitable for open-source adoption.

## Phase summary

| Phase | Theme | Status |
| ----- | ----- | ------ |
| 1 | Safety defaults (`DefaultMaxDepth`, `DefaultMaxCollectionItems`, limit exceptions) | ✅ Shipped (2.1.x) |
| 2 | CI matrix, CodeQL, Scorecard, Dependabot, release provenance | ✅ Shipped |
| 3 | Mapping fast path (`FastPathCompiler`), delegate/method caches | ✅ Shipped (P3.6); P3.7 nested inline still open |
| 4 | Property-based tests, fuzz harness, security-model docs | ✅ Shipped (AFL CI deferred) |
| 5 | Source generator MVP (`[GenerateMapper]`) | ✅ MVP shipped; expansions open |
| 6 | Standard pipeline, audit, localized validation, Turkish culture helpers | ✅ Shipped |
| 7 | Docs site, migration guides, adoption packaging | 🔄 In progress |

## Quality gates (ongoing)

- Multi-OS `build + test` CI on `main` and PRs
- Package validation on packable projects (`EnablePackageValidation`)
- CodeQL + OpenSSF Scorecard
- Conventional Commits on PRs
- Security-sensitive paths require CODEOWNERS attention:
  - `src/Meridian.Mapping/Execution/`
  - `src/Meridian.Mediator/Pipeline/`
  - `src/Meridian.Mediator/Wrappers/`

## §8.4 Second maintainer

When a second maintainer is onboarded:

1. Add them to [`.github/CODEOWNERS`](../.github/CODEOWNERS) on the security-sensitive paths above.
2. Raise branch-protection required approvals from 1 → 2 (see [governance/branch-protection.md](./governance/branch-protection.md)).
3. Share release secrets / NuGet ownership via least privilege.

Until then, `@UmutKorkmaz` remains sole required owner.

## Non-goals (historical)

- Out-of-process messaging buses
- Full AutoMapper feature parity on day one
- Guaranteeing unlimited recursion or unbounded collections by default

## Related docs

- [ROADMAP.md](./ROADMAP.md)
- [security-model.md](./security-model.md)
- [CHANGELOG.md](../CHANGELOG.md)

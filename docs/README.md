# Meridian documentation

The product guides listed in `toc.yml` are also built into a local reference site (authored pages plus API reference
generated from the packed candidate assemblies) by `pwsh eng/verify-docs.ps1 -Configuration Release -Feed <feed>`. The site output goes
under `artifacts/docs/` and is never committed or published by that command.

| Doc | Purpose |
| --- | ------- |
| [capabilities.md](./capabilities.md) | Claim ledger: every advertised capability with version status, runnable proof, regression rows and verified boundary |
| [migrate-from-automapper.md](./migrate-from-automapper.md) | AutoMapper → Meridian.Mapping (compiled examples) |
| [migrate-from-mediatr.md](./migrate-from-mediatr.md) | MediatR → Meridian.Mediator (compiled examples) |
| [generated-mapping.md](./generated-mapping.md) | Runtime vs direct Legacy/Structural vs generated runtime integration |
| [generated-mapping-contract.md](./generated-mapping-contract.md) | Detailed `[GenerateMapper]` contract and diagnostics |
| [source-gen-guide.md](./source-gen-guide.md) | Short summary kept for existing links; points to the guides above |
| [integrations.md](./integrations.md) | Optional packages, dependency minimums and backend requirements |
| [mediator-metrics.md](./mediator-metrics.md) | Opt-in bounded mediator metrics |
| [native-aot.md](./native-aot.md) | Native AOT rows that were published and executed, and what is PENDING |
| [projection-providers.md](./projection-providers.md) | `ProjectTo` provider/version coverage |
| [compatibility-policy.md](./compatibility-policy.md) | SemVer, default-behavior, support and deprecation guarantees |
| [package-compatibility.md](./package-compatibility.md) | Packed-candidate compatibility with the published 2.1.3 packages, dependency licenses and build-time tooling |
| [warning-dispositions.md](./warning-dispositions.md) | Which compiler/analyzer warnings were fixed or deliberately retained, and the warning ratchet |
| [deprecations.md](./deprecations.md) | Retained APIs, preferred migrations and evidence required before any diagnostic or removal |
| [troubleshooting.md](./troubleshooting.md) | Repository guide to diagnosing setup and execution failures |
| [provider-testing.md](./provider-testing.md) | Repository recipes for owned Redis/PostgreSQL and mandatory provider tests |
| [maintainer-checklist.md](./maintainer-checklist.md) | Repository maintenance duties, effective settings and open evidence gates |
| [release-runbook.md](./release-runbook.md) | Repository exact-candidate preparation, read-only planning and promotion boundaries |
| [security-model.md](./security-model.md) | Threat model and mitigations |
| [governance/branch-protection.md](./governance/branch-protection.md) | Required checks checklist (maintainers) |
| [governance/required-checks.md](./governance/required-checks.md) | Exact required check names, support tables with proof levels, coverage ratchet, effective repository settings and proposals |

Planning history that is not product guidance stays in the repository only: `docs/PLAN.md`, `docs/ROADMAP.md` and
`docs/superpowers/` (evolution plans, specs and the regression matrix).

Also see the repository root:

- [README.md](../README.md)
- [CHANGELOG.md](../CHANGELOG.md)
- [SECURITY.md](../SECURITY.md)
- [CONTRIBUTING.md](../CONTRIBUTING.md)
- Samples under [`samples/`](../samples/README.md)

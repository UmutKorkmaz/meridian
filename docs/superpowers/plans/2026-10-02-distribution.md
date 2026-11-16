# Documentation and Distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Make every capability discoverable, verifiable and safely distributable, with a working deprecation lifecycle.
**Architecture:** One package manifest drives packing/consumers; evidence-backed documentation and cumulative CI gates apply to each staged release.
**Tech Stack:** DocFX, MSBuild/NuGet baseline validation, PowerShell, GitHub Actions, BenchmarkDotNet, SBOM/provenance tooling and compiled C# consumers.
**Spec:** [E design](../specs/2026-10-02-distribution-design.md) and the portfolio design.

## Global Constraints

- Keep existing APIs operational during documented deprecation windows; use additive replacements and reserve removals/default flips for a major release.
- Preserve runtime mapping defaults: DefaultMaxDepth=64, DefaultMaxCollectionItems=10000, AllowNullCollections=false, AllowNullDestinationValues=true, AllowUndefinedEnumValues=false.
- Preserve telemetry defaults: RecordExceptionMessage=false and RecordExceptionStackTrace=false; default notification publication remains sequential and concurrent publisher defaults remain capped at 16.
- Preserve existing generated method names and legacy assignment semantics; structural generation requires explicit opt-in.
- Core libraries continue targeting net8.0;net9.0;net10.0; generators target netstandard2.0; net11.0 preview coverage remains optional and non-blocking.
- Do not introduce FluentValidation, EF Core, Redis, ASP.NET Core, or telemetry-exporter dependencies into either core package.
- Preserve the reviewed dirty working tree; never reset, clean, stash, stage unrelated changes, or assume a worktree from HEAD includes the baseline.
- Publishing, tags, releases, external database changes, and new recurring monitors require explicit user authorization.
- Graph-Loop and Sol Advisor remain disabled; no workflow, custom agent, hook, or completion gate from either framework may be activated.

## Review Focus

- Packing only the two original projects while silently excluding new adapters — E2.
- A supported runtime being mistaken for a supported analyzer/compiler host — E2/E4.
- Historical or unequal benchmark work being presented as current performance — E3.
- A release uploaded from a rebuilt artifact different from the verified candidate — E5.
- Deprecation warnings breaking consumers before a supported replacement exists — E7.

## Task E1: Publish an accurate documentation source

**Files:** Create docs/docfx.json, docs/toc.yml, docs/capabilities.md, docs/generated-mapping.md, docs/integrations.md, docs/native-aot.md and eng/verify-docs.ps1; extend docs/README.md, compatibility/migration guides, package READMEs and docs-site/index.html. Create tests/consumers/Meridian.DocumentationConsumer/Meridian.DocumentationConsumer.csproj and Program.cs.
**Interfaces:** verify-docs.ps1 -Configuration Release -Feed <directory> validates local links and compiles documentation snippets against packed packages. It generates local reference output; it does not publish.
- [ ] Inventory every advertised feature and connect it to the regression matrix, versioned support notes and runnable sample.
- [ ] Add compile/run fixtures for both migration guides, profile/generated precedence, scoped dispatch, standard behaviors and each adapter; assertions verify returned values and expected failure/status paths.
- [ ] Configure DocFX reference/authored pages and incremental XML documentation improvements; compare warning inventory to A1 and forbid new unexplained warnings.
- [ ] Run the documentation consumer and link/API-reference check; require zero broken internal links and zero snippet compilation failures. Generated output is reproducible and excluded from source staging unless repository policy requires it.
- [ ] Preview the local site in a browser, verify navigation/code rendering at desktop and narrow widths, and record rendered evidence. Site publication remains a separate authorized release action.
- [ ] Review claims against accepted evidence; commit task-owned source documentation only.

## Task E2: Centralize package inventory and previous-version checks

**Files:** Extend the eng/packages.json, eng/packages.schema.json, eng/pack-packages.ps1 and eng/verify-packages.ps1 files introduced by A4; extend core csproj validation properties and .github/workflows/release.yml. Create tests/consumers/Meridian.PackageConsumer/Meridian.PackageConsumer.csproj and Program.cs; extend A4 consumers.
**Interfaces:** Manifest schema and pack command match E design. verify-packages.ps1 -Feed <directory> -CandidateVersion <version> verifies metadata/assets/dependencies, builds/runs local-feed consumers and emits a machine-readable package/hash report; optional Framework/SdkVersion and Manifest retain A4's interface.
- [ ] Add validation cases for duplicate IDs, missing projects, wrong TFMs, unlisted analyzer assets, executable version strings and unlisted optional dependencies; each must fail clearly.
- [ ] Record pinned 2.1.3 public API baselines for the cores and inspect the actual packages. Include all optional package entries before their first pack; no new package pretends to have a stable API baseline.
- [ ] Pack a unique local prerelease candidate, inspect nupkg/nuspec/analyzer assets and test fresh consumption under SDK 8/9/10 plus the promised language floor. Preserve custom interface implementations.
- [ ] Generate license/dependency/SBOM inventory and verify optional backends remain outside both core dependency graphs; Redis's reuse of the Caching contract registry is an explicit optional dependency.
- [ ] Require baseline API validation to pass or an individually reviewed breaking-change record; avoid blanket suppressions. Behavioral compatibility remains A's separate gate.
- [ ] Review exact hashes, dependency manifests and compiler consumers; commit only owned manifest/workflow/project changes.

## Task E3: Refresh fair performance evidence

**Files:** Extend benchmarks/Meridian.Benchmarks/Meridian.Benchmarks.csproj, MappingBenchmarks.cs, MediatorBenchmarks.cs and README.md; create GeneratedMappingBenchmarks.cs, StartupBenchmarks.cs, PipelineBenchmarks.cs, eng/run-benchmarks.ps1 and benchmarks/results/README.md.
**Interfaces:** run-benchmarks.ps1 -Filter <scenario-pattern> -OutputDirectory <directory> records SDK/runtime/OS/CPU, candidate source/package identity and pinned competitor versions.
- [ ] Implement equal-work scenarios for manual/runtime/compiled/generated mapping and mediator dispatch; return/consume outputs so work is not optimized away.
- [ ] Cover configuration/startup, nested/collection/reference mapping, scoped handlers, closed/open pipelines, cache/idempotency overhead and disabled/enabled metrics. Validate output equivalence before timing.
- [ ] Add current Mapster/Mapperly comparisons and explicitly pinned AutoMapper/MediatR comparisons where license/setup permits. Record omitted scenarios and historical runs as such.
- [ ] Run on a named dedicated host with repeatable warmup/iteration settings and export raw BenchmarkDotNet artifacts. Do not infer release speed from shared CI timings.
- [ ] Compare modified paths with the accepted candidate baseline; investigate >10% mean regression and any unexplained allocation increase. Record variance and decision rather than hiding unfavorable workloads.
- [ ] Review benchmark fairness and documentation; commit scenario code and selected reproducible reports, with no universal performance claim.

## Task E4: Enforce cumulative regression and package gates

**Files:** Extend .github/workflows/ci.yml, fuzz.yml and release.yml; create eng/verify-candidate.ps1 and docs/governance/required-checks.md; extend run-samples, consumer, provider and AOT scripts.
**Interfaces:** verify-candidate.ps1 -Configuration Release -Feed <directory> -EvidenceDirectory <directory> emits a summary of required checks, counts, skips, hashes and limitations. It cannot convert missing infrastructure into success.
- [ ] Separate Windows/Linux/macOS runtime lanes from SDK-host consumer lanes; install exact required SDK/runtime families and keep preview selection out of required jobs.
- [ ] Require A's seven .NET 10/six .NET 8/9 samples, all legacy suites, actual property counts and package consumers. Require real provider and native-binary lanes only for the capabilities in that candidate.
- [ ] Establish honest coverage and ratchet thresholds; changed core hot paths require 90% line/80% branch plus named boundary tests. Do not add exclusions to conceal failures.
- [ ] Attach precise provider/compiler/AOT support tables, dependency/SBOM/license checks and reproducible artifacts. Fail required lanes with missing services, skipped mandatory cases or mismatched candidate hashes.
- [ ] Verify effective branch-protection/required-review settings using available read-only repository APIs; propose any unavailable setting change for authorized maintainers. CODEOWNERS/workflow files alone are insufficient proof.
- [ ] Review gate names for stability and operational cost; repair existing schedules without adding new monitoring; commit task-owned workflow/scripts.

## Task E5: Prepare and promote an exact release candidate

**Files:** Create eng/prepare-release.ps1, eng/promote-release.ps1 and docs/release-runbook.md; update CHANGELOG.md, version properties and the release workflow for an accepted scope.
**Interfaces:** prepare-release.ps1 takes CandidateVersion and EvidenceDirectory, produces immutable local artifacts plus a hash manifest without upload. promote-release.ps1 requires the approved candidate manifest and rejects altered files, reused versions or incomplete required evidence.
- [ ] Select a patch/minor/major version from A4 behavior/API/compiler evidence, not only local version fields; write exact feature, security and migration notes.
- [ ] Build/pack once, consume the exact packages in fresh projects, verify E4 evidence, and prepare release notes/SBOM/provenance. Require source identity and all artifact hashes to match.
- [ ] Present the concrete candidate for explicit tag/release/NuGet/site publication authorization. Until then, promotion remains pending and local preparation is complete.
- [ ] After authorization, execute the reviewed repository release order with the same artifacts; inspect existing remote state first and never rebuild to resume a partial upload.
- [ ] Retrieve published packages and verify hashes/contents plus a fresh consumer. Record GitHub release and NuGet/site results separately; a tag alone is not package publication.
- [ ] Document rollback to a previous verified package and provider-schema constraints; never erase uncertain idempotency records or reverse business effects automatically.
- [ ] Review the release receipt; commit authorized task-owned release records only.

## Task E6: Make maintenance repeatable

**Files:** Extend CONTRIBUTING.md, SECURITY.md, NOTICE, .github/CODEOWNERS, docs/compatibility-policy.md and docs/governance/branch-protection.md; create docs/troubleshooting.md, docs/provider-testing.md and docs/maintainer-checklist.md.
**Interfaces:** Setup and reproduction commands use manifest-driven scripts and isolated provider fixtures; support tables state runtime/compiler/provider/AOT proof separately.
- [ ] Document new-contributor setup, exact failing-sample/generator/provider repros, SDK/runtime installation, compiler diagnostic IDs and secret-free local provider configuration.
- [ ] Define framework/compiler/dependency support and retirement policy, security reporting, review ownership and release responsibilities. No silent TFM removal in a minor release.
- [ ] Verify setup from a clean disposable copy of the accepted baseline; ensure instructions preserve local dirty work and do not point to production services.
- [ ] Verify documented review/settings claims against effective repository state; record unavailable second-maintainer or protection proof candidly.
- [ ] Run link/setup/provider recipe checks and confirm no disabled optional workflow has been reintroduced.
- [ ] Review and commit task-owned maintenance documentation.

## Task E7: Introduce tested, reversible deprecations

**Files:** Create docs/deprecations.md and tests/consumers/Meridian.DeprecationConsumer/Meridian.DeprecationConsumer.csproj plus OldApi.cs/NewApi.cs; extend migration guides and existing source-generator diagnostics. Modify AddAutoMapper aliases only after stable-replacement eligibility is established.
**Interfaces:** Ledger fields and lifecycle follow E design. Reserve MERIDIANDEP001 for eligible DI alias migration; behavior guidance uses distinct diagnostic IDs. An Obsolete member is non-error with diagnostic-specific suppression documentation.
- [ ] Create ledger entries for the four candidates in E design, with exact symbols/options and migration fixtures. Inspect all AddAutoMapper overloads before selecting equivalents.
- [ ] Execute old and new consumers; assert matching intended values, pipeline order, cache invalidation and retained supported opt-outs. Cover warnings-as-errors with a documented narrowly scoped suppression and a warning-free migrated consumer.
- [ ] Keep explicit dynamic AddClosedBehavior/AddClosedStreamBehavior, runtime scanning and shared IIdempotencyStore supported. Do not deprecate a base interface inherited by atomic/leased contracts.
- [ ] Prepare preference guidance immediately; introduce diagnostics/Obsolete only after the replacement has shipped stable and the announced transition is eligible. Record real release versions/dates at that point.
- [ ] Assert the ledger cannot permit removal/default flip before both two minor releases and six months, and that earliest removal is a separately reviewed major. This is a release validation rule, not a newly scheduled monitor.
- [ ] Keep Legacy generation/defaults and compatibility flags working throughout the window; publish supported migration examples instead of silently changing semantics.
- [ ] Review consumer impact/adoption and commit task-owned eligible migration changes. Removal remains pending until its actual boundary and explicit scope approval.

## Verification commands and pass criteria

Run from the repository root after each relevant script exists. All scripts propagate a failing child exit code.

```powershell
# E1: no broken local links, compiled/runnable snippets, successful local reference build
pwsh eng/verify-docs.ps1 -Configuration Release -Feed artifacts/candidate
# E2: exact manifest, analyzer assets, API baselines, dependency and consumer success
pwsh eng/pack-packages.ps1 -Configuration Release -OutputDirectory artifacts/candidate -CandidateVersion 2.1.4-plan.1
pwsh eng/verify-packages.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-plan.1
# E3: equal results before timing; hardware/variance and allocations recorded
pwsh eng/run-benchmarks.ps1 -Filter '*' -OutputDirectory artifacts/benchmarks
# E4: every required check succeeds; mandatory skipped/missing cases fail
pwsh eng/verify-candidate.ps1 -Configuration Release -Feed artifacts/candidate -EvidenceDirectory artifacts/evidence
# E7: both legacy and migrated consumers execute successfully
dotnet run --project tests/consumers/Meridian.DeprecationConsumer/Meridian.DeprecationConsumer.csproj -c Release -f net10.0
```

2.1.4-plan.1 is a local illustrative prerelease only; actual version selection is E5. E5/E6 use the runbook and clean-copy reproduction because external promotion and governance verification cannot be certified by a unit test.

Dependencies: E2 starts after A4 and precedes any optional package pack; E1/E4 run cumulatively with features; E3 follows accepted correctness; E7 preparation starts after A4 but warning/removal steps wait for actual stable replacement releases. E5 promotion follows all gates relevant to that candidate and current explicit authorization.

# Reliability and Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Repair the current regressions and establish consumer-level proof for every existing feature.
**Architecture:** Keep behavior stable while adding permanent failure reproductions, explicit CI runtime lanes and packed-consumer tests. Changes to core code are tied to observed contract failures.
**Tech Stack:** C#/.NET 8/9/10, xUnit/FsCheck, Roslyn, PowerShell, GitHub Actions, NuGet API validation.
**Spec:** [A design](../specs/2026-10-02-reliability-design.md) and [portfolio design](../specs/2026-10-02-meridian-evolution-design.md).

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

- SDK 8/9 compiler hosts loading the packaged generator — A4.
- Equal-value distinct source nodes and reference cache sharing — A6.
- Two providers and early-disposed streams retaining scope isolation — A7.
- Cancellation plus cleanup errors retaining original failures — A8.
- Old raw-key and non-atomic idempotency behavior remaining documented and testable during migration — A2/A8.

## Task A1: Preserve the baseline and make the runtime matrix explicit

**Files:** Create eng/capture-baseline.sh and docs/verification/2026-10-02-baseline.md; modify .github/workflows/ci.yml, global.json, and docs/governance/branch-protection.md only for required SDK/runtime lane isolation.
**Interfaces:** capture-baseline.sh --output <directory> produces a source-manifest JSON, git status, SDK/runtime metadata, and build/test logs. It never alters tracked source. Required CI selects a stable SDK; preview CI is separate.
- [ ] Capture tracked and non-ignored untracked paths/hashes and record pre-existing ownership before editing anything.
- [ ] Reproduce dotnet build Meridian.sln -c Release and the two explicit legacy test-project commands in the verification section on net8.0, net9.0, and net10.0; record missing runtimes as unverified rather than rolling forward testhosts.
- [ ] Configure stable required jobs on ubuntu-latest/windows-latest/macos-latest with installed runtime families 8/9/10; isolate MeridianIncludeNet11Samples=true in a non-required preview job.
- [ ] Verify required lane failure for an intentionally failing test in a disposable branch, and preview failure cannot mask or fail stable lanes. Remove the intentional failure.
- [ ] Review the baseline manifest and commit only task-owned documentation/workflow/script changes.

## Task A2: Repair all examples and execute them in CI

**Files:** Modify samples/Meridian.Showcase/Demos/CommandMediatorDemo.cs, samples/Meridian.CleanArchitecture/Application/InventoryFeature.cs, samples/Meridian.EventDrivenCqrs/Application/Commands.cs, samples/Meridian.ModularMonolith/Order/Module.cs, samples/Meridian.VerticalSlice/Features/Backlog.cs, samples/Meridian.Hexagonal/Application/OrderFeature.cs, their Program.cs files as needed, samples/README.md, and .github/workflows/ci.yml. Create eng/run-samples.ps1.
**Interfaces:** run-samples.ps1 -Framework net10.0 executes all seven samples and fails on any nonzero exit; net8.0/net9.0 execute the six multi-target samples.
- [ ] Add sample assertions: a cached query hits once, its invalidating command succeeds, and a subsequent query observes changed state. Capture the existing six failures.
- [ ] Replace each raw invalidation key with CacheKeyComposer.Compose<TCachedQuery>(rawKey), using the actual query class that owns the entry.
- [ ] Add timeouts and explicit exit-code propagation to the runner; do not mask a failing dotnet process with a subsequent successful tail/echo command.
- [ ] Run pwsh -File eng/run-samples.ps1 -Framework net10.0; expect seven successes. Run net8.0/net9.0 in their lanes; expect six successes per lane.
- [ ] Add the runner to required CI and correct the README's execution claims; review and commit task-owned changes.

## Task A3: Repair inherited and init-only generated mapping

**Files:** Modify src/Meridian.Mapping.Generators/MeridianMapperGenerator.cs and tests/Meridian.Mapping.Tests/SourceGeneratorTests.cs. Create tests/Meridian.Mapping.Tests/{GeneratorTestHarness.cs,GeneratorCompilationResult.cs}; update docs/source-gen-guide.md.
**Interfaces:** Implement the two test-harness methods from the A design. Production entry point EmitMapMethod remains private and generated MapTo{Destination}(Source source) signatures remain unchanged.
- [ ] Add Inherited_Id_Is_Preserved and Init_Destination_Compiles fixtures using Source.Id=42 and Dto.Value init; assert compiled diagnostics contain no errors and proof methods return 42.
- [ ] Add Most_Derived_Member_Wins, Keyword_Property_Is_Escaped, Private_Setter_Is_Not_Assigned, and Unsupported_Required_Constructor_Has_Actionable_Diagnostic fixtures; preserve existing multiple-pair and nested-container tests.
- [ ] Run dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~SourceGeneratorTests; confirm the first two fail before implementation.
- [ ] Traverse base symbols with derived-first precedence and emit escaped object-initializer assignments. Reject unsupported construction with a stable diagnostic instead of invalid source.
- [ ] Rerun the focused suite and existing null/mismatch/same-type aliasing fixtures; repeat under promised language/compiler lanes from A4.
- [ ] Review emitted C# and compatibility notes; commit only these changes.

## Task A4: Establish stable-package and compiler compatibility

**Files:** Modify Directory.Build.targets and src/Meridian.Mapping.Generators/Meridian.Mapping.Generators.csproj compiler references when necessary. Create the initial core-only eng/packages.json, eng/packages.schema.json, eng/pack-packages.ps1 and eng/verify-packages.ps1 using E's schema; create tests/consumers/Meridian.LegacyConsumer/{Meridian.LegacyConsumer.csproj,Program.cs,global.json} and tests/consumers/Meridian.GeneratedConsumer/{Meridian.GeneratedConsumer.csproj,Program.cs,global.json}; update .github/workflows/ci.yml.
**Interfaces:** verify-packages.ps1 -Feed <directory> -CandidateVersion <version> -Framework <tfm> -SdkVersion <version> -Manifest eng/packages.json builds and runs consumers using exact local package versions and hashes. Manifest defaults to eng/packages.json; Framework/SdkVersion select an isolated compiler-consumer lane and are optional when the complete manifest matrix is requested. E2 extends these same files for adapters and supply-chain validation; it creates no second inventory or conflicting script.
- [ ] Revalidate the NuGet indexes, then pin 2.1.3 for both core PackageValidationBaselineVersion values; both indexes were checked on 2026-10-02. Retain previous-package consumer inputs as immutable fixtures and record hashes of actual downloaded baseline packages.
- [ ] Create legacy consumers for mapping profiles/shims, custom IConfigurationProvider implementations, request/void dispatch and mixed behaviors; add generated consumers for nested containers and init/inherited DTOs.
- [ ] Pack both core projects locally and inspect analyzers/dotnet/cs, dependency groups, README/license and symbol assets. Restore consumers exclusively from the local feed plus explicit dependency sources.
- [ ] Build with promised SDK 8/9/10 hosts and C# 12 output. If the bundled Roslyn 5.3 generator fails an older host, reduce its API reference floor or explicitly change the proposed support contract before release; never hide loader warnings.
- [ ] Run API baseline validation and compare old/new consumer outputs. Document intentional security/default differences separately from public-member diffs and test the applicable opt-outs.
- [ ] Verify legacy deprecation suppression and migrated consumers both compile with TreatWarningsAsErrors=true; do not add deprecation attributes before E7's lifecycle gate.
- [ ] Review generated analyzer compatibility, manifest hashes, and API diff; commit only task-owned changes.

## Task A5: Make nightly property counts observable

**Files:** Create tests/Meridian.Mapping.Tests/MeridianPropertyAttribute.cs; modify PropertyBasedTests.cs, .github/workflows/fuzz.yml, and tests/Meridian.Mapping.Fuzz/README.md.
**Interfaces:** MeridianPropertyAttribute derives from FsCheck.Xunit.PropertyAttribute and uses MERIDIAN_FSCHECK_MAX_TEST; CollectionMeridianPropertyAttribute uses MERIDIAN_FSCHECK_COLLECTION_MAX_TEST. Positive integer validation and category-specific defaults are explicit.
- [ ] Reproduce RoundTrip_Preserves_All_Fields reporting 100 tests with the existing FsCheck_MaxTest=10000 setting.
- [ ] Add Count_Override_Is_Consumed, Invalid_Count_Is_Rejected, and Collection_Budget_Is_Separate configuration tests; use 10000 scalar/depth cases and 500 collection cases in the nightly job.
- [ ] Replace the ineffective environment wiring with the attributes and clear actual-count output, keeping current cheap PR defaults and expensive PR limits.
- [ ] Run MERIDIAN_FSCHECK_MAX_TEST=10000 dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~RoundTrip_Preserves_All_Fields --logger 'console;verbosity=detailed'; expect exactly 10000 reported cases.
- [ ] Run the full property suite with nightly settings in isolated CI; verify each property's reported count, replay seed, execution time and memory budget.
- [ ] Review naming and docs; keep AFL harness coverage claims distinct; commit task-owned changes.

## Task A6: Protect runtime mapping contracts

**Files:** Existing mapping test owners from M01-M18; create tests/Meridian.Mapping.Tests/MappingPathParityTests.cs and ConcurrentMappingTests.cs. Modify relevant MappingEngine/FastPathCompiler/ResolutionContext code only if new tests reproduce a defect.
**Interfaces:** Existing IMapper overloads, MappingEngine.Map/MapToExisting and ResolutionContext remain the contract. Use test-only path selection to compare compiled and interpreter results; do not add a public force-interpreter switch.
- [ ] Identify each unproved M01-M18 boundary; retain existing tests and add only missing assertions.
- [ ] Add parity cases for nullable defaults, invalid enums with and without opt-out, map-to-existing identity, options propagation, constructor choice, null chains and polymorphic mapping.
- [ ] Add equal-value distinct nodes, diamond references, cycles, depth 63/64/65 and collection 9999/10000/10001 across new/existing destinations; assert the streaming source stops by item 10001.
- [ ] Add concurrent scoped-resolver/options tests using barriers; assert no scope/state crosses a mapping operation.
- [ ] Run the A6 commands below on net8.0/net9.0/net10.0 in all required OS lanes; fix only demonstrated defects and rerun affected rows.
- [ ] Record row-level acceptance and review changes; commit task-owned changes.

## Task A7: Protect mediator dispatch and scope contracts

**Files:** Existing mediator test owners Q01-Q06/Q15; create tests/Meridian.Mediator.Tests/MultiProviderIsolationTests.cs and StreamLifetimeTests.cs. Modify core dispatch/registration only for reproduced defects.
**Interfaces:** ISender/IPublisher/IStreamSender and existing wrapper contracts remain intact; no cached wrapper may capture a handler, scoped provider or service instance.
- [ ] Add Same_Request_Type_In_Two_Providers_Uses_Own_Handler and Scoped_Stream_Disposes_After_Enumeration; assert independent responses and disposal after early stream termination.
- [ ] Add repeated standard registration with mixed explicit/scanned/open/closed behaviors; assert one validator/audit/transaction invocation and documented order.
- [ ] Assert sequential notification failure stops later handlers; concurrent publisher exposes all failures and stays within default active-handler limit 16.
- [ ] Assert both void handler forms, object Send, exception actions/handlers, pre/post processors, and cancellation before/after yield retain behavior.
- [ ] Run the complete mediator suite on every required lane and record Q01-Q06/Q15 acceptance; fix only reproducible contract violations.
- [ ] Review static caches and DI lifetimes; commit task-owned changes.

## Task A8: Protect behavior failures, cleanup and privacy

**Files:** BehaviorTests.cs, MediatorHardeningTests.cs, RetryBehaviorHardeningTests.cs, AuditBehaviorTests.cs, TelemetryPrivacyTests.cs, TracingTests.cs, and new tests/Meridian.Mediator.Tests/BehaviorFailureCompositionTests.cs. Core behavior changes are limited to reproduced failures.
**Interfaces:** Current authorization/cache/idempotency/retry/transaction/audit contracts and options remain supported; new leased idempotency belongs to C4.
- [ ] Pin fail-closed authorization, type-namespaced keys, raw-key opt-out, atomic completed-null replay, legacy non-atomic concurrency limitations, retry caps and no cancellation retries.
- [ ] Add Handler_And_Cleanup_Failures_Are_Both_Observable and Canceled_Handler_Releases_Claim_With_Bounded_Cleanup; capture both exception instances and prove a later request can claim after safe failure.
- [ ] Preserve uncertain outcomes after successful external side effects: a failed completion/commit does not automatically replay business work. Document the exact provider guarantee instead of claiming exactly-once execution.
- [ ] Assert exception messages/stacks and payloads remain absent from default audit/log/Activity output; explicit privacy opt-ins retain behavior.
- [ ] Run all existing suites and samples in required lanes, then mark applicable legacy matrix rows accepted with candidate hashes and limitations.
- [ ] Review compatibility/security migration notes, scoped stores, and failure composition; commit task-owned changes and prepare the first reliability candidate.


## Verification commands and assertion anchors

The code below contains assertion excerpts for the named fixtures; result locals are arranged by those fixtures, not new production APIs. Retain the full existing suite as well as the focused checks.

| Task | Named fixture | Required C# assertions |
| --- | --- | --- |
| A2 | Invalidating_Command_Refreshes_Cached_Query | `if (handlerCallsBeforeInvalidation != 1 || handlerCallsAfterInvalidation != 2 || refreshedValue != updatedValue) throw new InvalidOperationException("Cache invalidation sample failed");` |
| A3 | Inherited_Id_Is_Preserved / Init_Destination_Compiles | `Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error); Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Proof", "Run"));` |
| A4 | Custom_ConfigurationProvider_Still_Compiles | `Assert.Equal(0, consumerBuildExitCode); Assert.Equal(oldResult, candidateResult);` |
| A5 | Count_Override_Is_Consumed | `Assert.Equal(10000, scalarAttribute.MaxTest); Assert.Equal(500, collectionAttribute.MaxTest); Assert.Throws<ArgumentException>(() => CreateAttributeWithInvalidCount());` |
| A6 | Reference_Identity_And_Default_Caps_Agree | `Assert.Same(mapped.SharedLeft, mapped.SharedRight); Assert.NotSame(mapped.EqualLeft, mapped.EqualRight); Assert.Equal(64, configuration.DefaultMaxDepth); Assert.Equal(10000, configuration.DefaultMaxCollectionItems);` |
| A7 | Same_Request_Type_In_Two_Providers_Uses_Own_Handler | `Assert.Equal("provider-A", responseA); Assert.Equal("provider-B", responseB); Assert.True(maxConcurrentNotifications <= 16); Assert.True(streamHandlerDisposed);` |
| A8 | Handler_And_Cleanup_Failures_Are_Both_Observable | `Assert.Contains(handlerFailure, observedFailures); Assert.Contains(cleanupFailure, observedFailures); Assert.False(options.RecordExceptionMessage); Assert.False(options.RecordExceptionStackTrace);` |

```powershell
# A1: capture before any edit; do not reinterpret known failures as success
bash eng/capture-baseline.sh --output artifacts/baseline
dotnet build Meridian.sln -c Release
# A6-A8: zero failures and no newly skipped cases on each runtime/OS lane
foreach ($tfm in @('net8.0','net9.0','net10.0')) {
  dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release -f $tfm
  if ($LASTEXITCODE -ne 0) { throw "Mapping failed on $tfm" }
  dotnet test tests/Meridian.Mediator.Tests/Meridian.Mediator.Tests.csproj -c Release -f $tfm
  if ($LASTEXITCODE -ne 0) { throw "Mediator failed on $tfm" }
}
# A2: seven .NET 10 successes, six successes on each earlier runtime
pwsh eng/run-samples.ps1 -Framework net10.0
pwsh eng/run-samples.ps1 -Framework net8.0
pwsh eng/run-samples.ps1 -Framework net9.0
# A3: compiled fixtures preserve inherited/init values; zero generator errors
dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release -f net10.0 --filter 'FullyQualifiedName~SourceGeneratorTests'
# A4: exact packed versions/API baselines and all promised compiler hosts
pwsh eng/pack-packages.ps1 -Configuration Release -OutputDirectory artifacts/candidate -CandidateVersion 2.1.4-plan.1
pwsh eng/verify-packages.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-plan.1 -Manifest eng/packages.json
# A5: detailed output reports 10000 actual cases, not the previous 100
$env:MERIDIAN_FSCHECK_MAX_TEST='10000'
$env:MERIDIAN_FSCHECK_COLLECTION_MAX_TEST='500'
dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release -f net10.0 --filter 'FullyQualifiedName~PropertyBasedTests' --logger 'console;verbosity=detailed'
```

A1's script uses Bash available on all CI runners and hashes the dirty baseline without copying secrets into artifacts. Consumer scripts enter each fixture's SDK-pinned directory so root global.json cannot select a newer host silently. 2.1.4-plan.1 is illustrative local prerelease only. Actual release numbers are decided after compatibility review.

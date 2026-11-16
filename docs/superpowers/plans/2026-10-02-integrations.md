# Optional Integrations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Provide practical validation, cache, idempotency, transaction and HTTP integrations as optional packages.
**Architecture:** Adapters implement existing contracts; richer behavior uses optional interfaces and explicit options. Provider guarantees are proved against isolated real services and do not change core defaults.
**Tech Stack:** .NET 8/9/10, FluentValidation, Microsoft.Extensions.Caching, System.Text.Json metadata, StackExchange.Redis, EF Core, ASP.NET Core, xUnit.
**Spec:** [C design](../specs/2026-10-02-integrations-design.md) and the portfolio design.

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

- Unknown remote type contracts must not instantiate arbitrary types — C3.
- Tenant scope alone does not isolate private per-user cache entries — C3/C6.
- Lease expiry and Redis failover must not imply exactly-once external effects — C4.
- Ambient transactions and execution strategies must not commit or replay work they do not own — C5.
- Shared cache factories must not outlive request-scoped dependencies or cross authorization scopes — C2/C6.

## Task C1: Ship FluentValidation adapters

**Files:** Create src/Meridian.Mediator.FluentValidation/{Meridian.Mediator.FluentValidation.csproj,FluentValidationAdapter.cs,ServiceCollectionExtensions.cs,README.md}; tests/Meridian.Mediator.FluentValidation.Tests/{Meridian.Mediator.FluentValidation.Tests.csproj,AdapterTests.cs}; add solution/package-manifest entries.
**Interfaces:** FluentValidationAdapter<T> : Meridian.Mediator.Behaviors.IValidator<T>, constructor IEnumerable<FluentValidation.IValidator<T>>; Task<Meridian.Mediator.Behaviors.ValidationResult> ValidateAsync(T instance,CancellationToken cancellationToken). AddMeridianFluentValidation returns IServiceCollection.
- [ ] Add Async_Rule_Is_Awaited, Cancellation_Reaches_Rule, Multiple_Validators_Aggregate and Duplicate_Registration_Does_Not_Double_Validate; assert exact property/message errors and handler invocation count.
- [ ] Implement adapters with ValidateAsync and deduplicated registrations; retain consumer-supplied Meridian validators and localized validation behavior.
- [ ] Run the adapter suite and Q07/Q15 legacy suites on each required TFM; pack and execute a local-feed consumer.
- [ ] Document async usage and dependency boundaries; add manifest entry before first pack; review and commit task-owned files.

## Task C2: Provide memory caching and optional miss coalescing

**Files:** Create src/Meridian.Mediator.Caching/{Meridian.Mediator.Caching.csproj,MemoryCacheProvider.cs,CacheProviderOptions.cs,ServiceCollectionExtensions.cs,README.md}; src/Meridian.Mediator/Behaviors/IConcurrentCacheProvider.cs; tests/Meridian.Mediator.Caching.Tests/{Meridian.Mediator.Caching.Tests.csproj,MemoryCacheTests.cs,CoalescingTests.cs}. Modify CachingBehavior.cs only for opt-in richer-provider use.
**Interfaces:** Existing ICacheProvider methods are unchanged. Optional core IConcurrentCacheProvider : ICacheProvider adds Task<object?> GetOrCreateAsync(string key,Func<CancellationToken,Task<object?>> factory,TimeSpan? duration,CancellationToken cancellationToken). EnableRequestCoalescing=false follows C design; AddMeridianMemoryCaching registers the provider once.
- [ ] Add Hit_Returns_Exact_Type, Expiry_Becomes_Miss, Invalidating_Command_Removes_Composed_Key and Raw_Key_OptOut_Remains_Working using fake time.
- [ ] Add Twenty_Misses_Run_One_Factory_When_Enabled, Disabled_Coalescing_Retains_Prior_Path and Waiter_Cancellation_Does_Not_Cancel_Owner; assert counts with barriers.
- [ ] Implement memory provider first, then optional coalescing; remove completed/faulted coalescing state and bound active key state by active operations.
- [ ] Keep null-response behavior unchanged; the first factory's cancellation may fault its shared result, while a later request starts a fresh operation.
- [ ] Run cache/legacy Q09/scoped tests and package consumer; measure no-option behavior allocation change.
- [ ] Review factory lifetime and concurrency cleanup; commit task-owned changes.

## Task C3: Add distributed cache with explicit contracts and scopes

**Files:** Create src/Meridian.Mediator.Caching/{DistributedCacheProvider.cs,ICacheValueSerializer.cs,JsonCacheValueSerializer.cs,CacheContractRegistry.cs,ICacheKeyScope.cs,ScopedCacheProvider.cs}; tests/Meridian.Mediator.Caching.Tests/{DistributedCacheTests.cs,ScopeIsolationTests.cs,SerializationContractTests.cs}; add provider integration consumer.
**Interfaces:** Serializer GetContractId(Type), Serialize(object?,string) -> byte[], Deserialize(ReadOnlyMemory<byte>,string) -> object?; RegisterContract<T>(string id,int schemaVersion,JsonTypeInfo<T> typeInfo). ICacheKeyScope.GetScope() -> string. AddMeridianDistributedCaching accepts configured contract registry/options.
- [ ] Add Cache_RoundTrip_Uses_Registered_JsonTypeInfo, Unknown_Contract_Is_Miss, Schema_Mismatch_Is_Miss and Transport_Failure_Is_Propagated; assert no Type.GetType/assembly-qualified payload activation path.
- [ ] Add Two_Tenants_Same_Query_Key_Do_Not_Share_Value and Personalized_User_Keys_Do_Not_Share_Value; assert invalidation affects only the intended effective scoped key.
- [ ] Implement versioned envelope/allowlisted contract lookup and consistent application/tenant prefix decoration; remove malformed/incompatible entries with a bounded diagnostic.
- [ ] Run tests against an isolated actual distributed-cache backend plus deterministic fake-provider unit cases; require same contract IDs across independent clients.
- [ ] Pack/run a JSON-source-generated consumer and prove old core package dependency list remains unchanged.
- [ ] Review serializer type safety, user scope and key migration instructions; commit task-owned changes.

## Task C4: Ship owned, leased Redis idempotency

**Files:** Create src/Meridian.Mediator/{Behaviors/ILeasedIdempotencyStore.cs,Behaviors/IdempotencyLease.cs,Behaviors/LeasedIdempotencyClaim.cs,Behaviors/IdempotencyOutcomeUnknownException.cs}; modify IdempotencyBehavior.cs to prefer explicitly supplied leased store. Create src/Meridian.Mediator.Redis/{Meridian.Mediator.Redis.csproj,RedisLeasedIdempotencyStore.cs,RedisIdempotencyOptions.cs,RedisIdempotencyScripts.cs,ServiceCollectionExtensions.cs,README.md}; tests/Meridian.Mediator.Redis.Tests/{Meridian.Mediator.Redis.Tests.csproj,LeasedIdempotencyTests.cs,RedisFixture.cs}.
**Interfaces:** ClaimAsync(key,responseType,ct) -> Task<LeasedIdempotencyClaim>, CompleteAsync(lease,response,ct)/ReleaseAsync(lease,ct) -> Task, RenewAsync(lease,ct) -> Task<bool>, and adapter ReconcileAsync(key,expectedOwnerToken,resolution,response,ct) -> Task use C design's exact types. Create IdempotencyReconciliation.cs beside the adapter for Completed/SafeToRetry. Store implements legacy base methods for completed-result compatibility, with documented rejection of writes that overwrite an owned pending claim.
- [ ] Add Two_Clients_Same_Key_Execute_Once_While_Lease_Is_Valid, Completed_Null_Is_Replayed, Stale_Owner_Cannot_Complete_Or_Release, Expired_Pending_Is_OutcomeUnknown, Two_Tenants_Same_Idempotency_Key_Are_Isolated and Response_Contract_Mismatch_Is_Rejected. Assert effective lease keys are never double-prefixed during renewal/reconciliation.
- [ ] Add Canceled_Handler_Cleanup_Is_Bounded, Unknown_Handler_Failure_Is_Not_Released, Safe_Failure_Reconciliation_Allows_Retry, Lost_Response_After_Completion_Is_Replayed and Failed_Completion_Does_Not_Automatically_Rerun_Handler; assert original exceptions and uncertainty status. A thrown/canceled handler alone cannot prove absence of business effects.
- [ ] Implement Lua transitions with owner tokens, schema/response contracts, renewal and pending tombstone retention. Default 30-second leases and 24-hour completed retention are adapter options; configure fake/test durations explicitly.
- [ ] Define bounded renewal lifecycle tied to the request. On unknown handler failure/cancellation, stop renewal and retain the pending record without automatic expiry. ReleaseAsync is not called merely because next() threw; application reconciliation owns the explicit SafeToRetry decision. Do not delete or reacquire uncertain expired work automatically.
- [ ] Run real Redis tests with two independent connections and process-level crash/restart fixtures in isolated CI; a missing service fails this support gate.
- [ ] Retain old atomic/non-atomic tests; verify core selects the new path only when the store implements the optional contract. No base interface is marked obsolete here.
- [ ] Document Redis failover/retention limits and application reconciliation responsibility; review and commit task-owned changes.

## Task C5: Add scoped EF Core transactions

**Files:** Create src/Meridian.Mediator.EntityFrameworkCore/{Meridian.Mediator.EntityFrameworkCore.csproj,EfTransactionScopeProvider.cs,EfTransactionScope.cs,EfTransactionOptions.cs,ServiceCollectionExtensions.cs,README.md}; core optional Behaviors/ITransactionExecutionProvider.cs; tests/Meridian.Mediator.EntityFrameworkCore.Tests/{Meridian.Mediator.EntityFrameworkCore.Tests.csproj,TransactionIntegrationTests.cs}; modify TransactionBehavior.cs for richer provider delegation.
**Interfaces:** AddMeridianEfCoreTransactions<TContext> where TContext:DbContext; ITransactionExecutionProvider.ExecuteAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken ct) -> Task<T>. Existing BeginAsync/CommitAsync/RollbackAsync/DisposeAsync contract remains.
- [ ] Add Failed_Handler_Rolls_Back_Real_Db_Write, Successful_Handler_Commits, Ambient_Transaction_Is_Not_Committed_Or_Disposed and Nested_Failure_Rolls_Back_To_Savepoint.
- [ ] Add Execution_Strategy_Is_OptIn, Handler_And_Rollback_Failure_Both_Surface and Cancellation_Is_Propagated; no default automatic replay of arbitrary handler side effects.
- [ ] Implement scoped transaction ownership, savepoint join policy and optional whole-delegate strategy. JoinWithoutSavepoint and UseExecutionStrategy default false.
- [ ] Execute SQLite rollback tests and PostgreSQL savepoint tests, including provider-version lanes; external notifications are either outside the transaction or explicitly coordinated by the example.
- [ ] Verify Q11/Q12 legacy contracts and package dependency isolation; document retry/idempotency composition.
- [ ] Review ownership, cleanup, execution strategy and integration evidence; commit task-owned changes.

## Task C6: Ship HTTP integration and a realistic sample

**Files:** Create src/Meridian.Mediator.AspNetCore/{Meridian.Mediator.AspNetCore.csproj,MeridianExceptionHandler.cs,MeridianHttpExtensions.cs,README.md}; samples/Meridian.AspNetCore/{Meridian.AspNetCore.csproj,Program.cs,Orders/Features.cs,Infrastructure/OrdersDbContext.cs,README.md}; tests/Meridian.AspNetCore.Tests/{Meridian.AspNetCore.Tests.csproj,OrderApiTests.cs,ApiFixture.cs}; update samples index and CI runner.
**Interfaces:** AddMeridianProblemDetails(this IServiceCollection) registers IExceptionHandler/ProblemDetails integration. Example GET/POST /orders and GET /orders/events use ISender/IPublisher/IStreamSender, profiles/generated DTOs and isolated SQLite.
- [ ] Add Valid_Order_Creates_And_Reads, Invalid_Request_Returns_400, Missing_Identity_Returns_401, Denied_Authorization_Returns_403, Duplicate_InFlight_Returns_409 and Unexpected_Error_Is_Redacted_500.
- [ ] Add Tenant_Cache_Isolation, Canceled_Stream_Disposes_Handler and Correlation_Is_Returned_Without_Internal_Error_Detail. Preserve authentication/authorization before cache lookup.
- [ ] Implement explicit HTTP error mapping and OpenAPI status/stream metadata; no controller/endpoint requires knowledge of mapper internals.
- [ ] Exercise the complete HTTP flows with WebApplicationFactory and a launched-host smoke test against isolated data; never use a production database or send external messages.
- [ ] Run all seven prior console samples and both legacy suites; pack the optional HTTP package and run a local-feed consumer.
- [ ] Review public error codes, docs and dependency manifest; commit task-owned changes.


## Verification commands and assertion anchors

| Task | Named fixture | Required C# assertions |
| --- | --- | --- |
| C1 | Async_Rule_Is_Awaited | `Assert.True(asyncRuleCompleted); Assert.Equal("Email", result.Errors.Single().PropertyName); Assert.Equal(0, invalidHandlerCalls);` |
| C2 | Twenty_Misses_Run_One_Factory_When_Enabled | `Assert.Equal(1, factoryCalls); Assert.Equal(20, results.Length); Assert.All(results, value => Assert.Equal(expectedValue, value));` |
| C3 | Two_Tenants_Same_Query_Key_Do_Not_Share_Value | `Assert.Equal("tenant-A", responseA.Owner); Assert.Equal("tenant-B", responseB.Owner); Assert.Equal(expectedValue, afterOtherTenantInvalidation);` |
| C4 | Stale_Owner_Cannot_Complete_Or_Release | `Assert.False(renewedByStaleOwner); Assert.Equal(LeasedIdempotencyStatus.OutcomeUnknown, expired.Status); Assert.Equal(1, handlerCalls); Assert.Null(replayedNull.CachedResponse);` |
| C5 | Ambient_Transaction_Is_Not_Committed_Or_Disposed | `Assert.Equal(0, adapterOwnedCommitCalls); Assert.False(ambientDisposed); Assert.Empty(rowsAfterRollback); Assert.Contains(originalFailure, observedFailures);` |
| C6 | Invalid_Request_Returns_400 / Unexpected_Error_Is_Redacted_500 | `Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal(HttpStatusCode.InternalServerError, unexpected.StatusCode); Assert.DoesNotContain(secretExceptionText, problemJson);` |

Run each relevant project under every advertised TFM; commands below show net10.0. Required isolated PostgreSQL/Redis services are provisioned by the test lane before invocation. Service absence fails mandatory provider checks.

```powershell
dotnet test tests/Meridian.Mediator.FluentValidation.Tests/Meridian.Mediator.FluentValidation.Tests.csproj -c Release -f net10.0
dotnet test tests/Meridian.Mediator.Caching.Tests/Meridian.Mediator.Caching.Tests.csproj -c Release -f net10.0
dotnet test tests/Meridian.Mediator.Redis.Tests/Meridian.Mediator.Redis.Tests.csproj -c Release -f net10.0
dotnet test tests/Meridian.Mediator.EntityFrameworkCore.Tests/Meridian.Mediator.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
dotnet test tests/Meridian.AspNetCore.Tests/Meridian.AspNetCore.Tests.csproj -c Release -f net10.0
# Legacy gates follow every core-interface/behavior change
dotnet test tests/Meridian.Mediator.Tests/Meridian.Mediator.Tests.csproj -c Release -f net10.0
pwsh eng/run-samples.ps1 -Framework net10.0
pwsh eng/verify-packages.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-plan.1
```

C3 tests use Microsoft.Extensions.Caching.StackExchangeRedis as a test-only IDistributedCache backend; production Caching depends on the abstraction and configured serialization, not Redis. C4's Redis adapter explicitly depends on the optional Caching package for contract serialization. C5 owns tests/Meridian.Mediator.EntityFrameworkCore.Ef10.Tests/{Meridian.Mediator.EntityFrameworkCore.Ef10.Tests.csproj,TransactionCompatibilityTests.cs} as the isolated net10/EF Core 10 lane, preventing version conflicts in the EF Core 8 project. Every command must exit zero, with mandatory named tests executed and provider outputs recorded.

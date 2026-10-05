# Meridian EF Core transactions

Register the same scoped context used by the handler, then opt into the transaction behavior:

<!-- illustrative: configuration fragment, not compiled as shown, runnable equivalent in the packaged consumer; fixture: tests/consumers/Meridian.EfTransactionsConsumer/Program.cs -->
```csharp
services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
services.AddMeridianEfCoreTransactions<AppDbContext>();
// Configure Meridian with AddTransactionBehavior(); requests implement ITransactionalRequest.
```

The adapter targets net8.0/net9.0/net10.0 and depends on EF Core Relational **8.0.20**. Core Meridian packages do not depend on EF. An isolated net10.0 package-consumer suite additionally tests EF **10.0.8** / Npgsql EF **10.0.0**; this does not raise the adapter's mandatory EF major. EF8 PostgreSQL tests use Npgsql EF8.0.11; tests use SQLite8.0.20 and SQLitePCLRaw2.1.13.

`AddMeridianEfCoreTransactions<TContext>` registers scoped contracts resolving the same provider/context. The first registration for a context snapshots its options; repeated calls do not add wrappers or reconfigure it. The first base `ITransactionScopeProvider` registration wins when multiple contexts are registered. Existing application contract registrations are preserved. If an existing base provider lacks the optional execution contract, resolving `ITransactionExecutionProvider` explicitly fails; TransactionBehavior continues its existing legacy path. Do not concurrently execute commands on the same DbContext/provider.

## Ownership and failure

An adapter-owned transaction is committed on handler success, rolled back on failure, and disposed after either. An existing `Database.CurrentTransaction` is joined through a unique savepoint: the adapter never commits or disposes that transaction. Nested success releases its own savepoint; nested failure rolls back only its boundary. The caller owns the ultimate ambient commit/rollback. Savepoint support must be present unless `JoinWithoutSavepoint=true` is explicitly configured. That option cannot undo failed handler writes; the caller must roll back the whole transaction. `System.Transactions` ambient/enlisted transactions and foreign retrying execution-strategy ownership are rejected before handler entry. An existing caller transaction on a retry-enabled context also cannot enter a new adapter-owned strategy; use an adapter-owned outer execution frame for nested calls.

Database rollback does not restore tracked objects, generated IDs, accepted entity states, navigation collections, or external effects. The adapter does not clear the tracker and discard outer pending work. After a caught child rollback, the application must reconcile affected tracked state before saving again, or discard its context.

Rollback and savepoint cleanup receive an independent cooperative five-second cancellation budget. Tokenless `DisposeAsync` is awaited to observe its real completion and failures; there is no universal hard disposal deadline. A single failure retains its exception instance and original stack. Multiple failures remain observable in occurrence order. Transaction/savepoint acquisition errors at the public `BeginAsync` provider-operation boundary, commit/savepoint-release uncertainty, or failed cleanup poison this scoped provider; discard its scope. Known preflight rejections (such as unsupported savepoints) do not poison a manually used provider. An ambiguous acquisition does not transfer ownership of an unreturned or caller-owned transaction to the adapter; discard the context and reconcile it through the owning provider/application. A handler cannot catch a child's cleanup failure and make the owner transaction report success.

## Execution strategy and idempotency

`UseExecutionStrategy=false` and `JoinWithoutSavepoint=false` are defaults. A context configured with retrying EF execution strategy requires explicit `UseExecutionStrategy=true`. The actual EF strategy encloses the whole transaction lifecycle, but only a failed pre-handler connection open that leaves the connection closed and no transaction acquired is eligible for EF retry. Once the handler is entered, **it is never automatically replayed**, even for a transient error with successful rollback. Generic scoped handlers cannot reconstruct all per-attempt application state. Begin/commit/rollback/release/disposal errors are terminal outcomes hidden from EF's retry classifier and rethrown after the strategy boundary. Final optional-provider errors also suppress outer Meridian Retry.

Place leased idempotency outside transactions so successful completion is stored only after transaction cleanup. A transaction error leaves the leased record retained for explicit reconciliation, including a real commit followed by an acknowledgement failure. Redis and the database are separate systems; no distributed atomicity or exactly-once external side effects are promised. Keep external notifications outside the transaction; where atomic business-data/notification intent is required, persist an application outbox row in the same database transaction and dispatch it separately.

Tests require `MERIDIAN_TEST_POSTGRES` and `MERIDIAN_TEST_REDIS` for selected provider cases. Missing endpoints fail; they never silently skip. Ordinary solution runs include the provider cases unless explicitly filtered for a separate CI lane. The dedicated CI job provisions both providers, runs the required EF8 suite on all target frameworks, packages the inventory, and runs the isolated EF10 package-consumer suite.

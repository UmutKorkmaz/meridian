using System.Data.Common;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Meridian.Mediator.EntityFrameworkCore.Tests;

public sealed class TransactionFaultTests
{
    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Execution_Strategy_Is_OptIn()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(true, true);
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
        Assert.Equal(0, calls);
        Assert.False(new EfTransactionOptions().UseExecutionStrategy);
        Assert.False(new EfTransactionOptions().JoinWithoutSavepoint);
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Actual_Ef_Strategy_Retries_Only_Closed_Connection_PreHandler_Failure()
    {
        var fault = new OpenFault();
        await using var db = await DatabaseFixture.CreateFaultAsync(true, true, fault);
        fault.Remaining = 1;
        var calls = 0;
        Assert.Equal(9, await Provider(db).ExecuteAsync(async ct => { calls++; await db.WriteAsync("once", ct); return 9; }, default));
        Assert.Equal(1, calls); Assert.Equal(1, fault.Failures);
        Assert.Single(await db.ReadIndependentAsync());
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Exhausted_PreHandler_Strategy_Preserves_Last_Error_Identity()
    {
        var fault = new OpenFault();
        await using var db = await DatabaseFixture.CreateFaultAsync(true, true, fault);
        fault.Remaining = 10;
        var calls = 0;
        var error = await Record.ExceptionAsync(() => Provider(db).ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
        Assert.Same(fault.Failure, error); Assert.Equal(3, fault.Failures); Assert.Equal(0, calls);
        fault.Remaining = 0;
    }

    [Theory, Trait("Provider", "PostgreSQL")]
    [InlineData("handler")]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    [InlineData("dispose")]
    public async Task Actual_Strategy_And_Outer_Retry_Never_Replay_After_Handler_Entry(string boundary)
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(true, true);
        var fault = Transient();
        if (boundary == "before-commit") db.Context.Faults.BeforeCommit = fault;
        if (boundary == "after-commit") db.Context.Faults.AfterCommit = fault;
        if (boundary == "dispose") db.Context.Faults.Dispose = fault;
        var provider = Provider(db);
        var behavior = new TransactionBehavior<Command, int>(provider);
        var request = new Command(); var calls = 0;
        var error = await Record.ExceptionAsync(() => new RetryBehavior<Command, int>().Handle(request,
            () => behavior.Handle(request, async () => { calls++; await db.WriteAsync("row"); if (boundary == "handler") throw fault; return 1; }, default), default));
        Assert.NotNull(error);
        if (error is AggregateException aggregate) Assert.Same(fault, aggregate.InnerExceptions[0]); else Assert.Same(fault, error);
        Assert.Equal(1, calls); Assert.Equal(1, db.Context.Faults.Disposals);
        Assert.Equal(boundary is "after-commit" or "dispose" ? 1 : 0, (await db.ReadIndependentAsync()).Length);
        if (boundary != "handler")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task Handler_And_Rollback_Failure_Both_Surface()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        var handler = new IOException("handler"); var rollback = new IOException("rollback"); var disposal = new IOException("dispose");
        db.Context.Faults.Rollback = rollback; db.Context.Faults.Dispose = disposal;
        var error = await Assert.ThrowsAsync<AggregateException>(() => Provider(db).ExecuteAsync<int>(async ct => { await db.WriteAsync("row", ct); throw handler; }, default));
        Assert.Equal(new Exception[] { handler, rollback, disposal }, error.InnerExceptions);
        Assert.Empty(await db.ReadIndependentAsync());
    }

    [Fact]
    public async Task Cleanup_Budget_Expires_Independently_And_Disposal_Is_Joined()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        var primary = new IOException("handler");
        db.Context.Faults.BlockRollback = true;
        var operation = Provider(db).ExecuteAsync<int>(ct => Task.FromException<int>(primary), default);
        var error = await Assert.ThrowsAsync<AggregateException>(() => operation);
        Assert.Same(primary, error.InnerExceptions[0]);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerExceptions[1]);
        Assert.True(db.Context.Faults.RollbackToken.CanBeCanceled);
        Assert.Equal(1, db.Context.Faults.Disposals);
    }

    [Fact]
    public async Task Disposal_Is_Actually_Awaited_Before_Failure_Returns()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.Context.Faults.DisposeGate = async () => { entered.SetResult(); await release.Task; };
        var failure = new IOException("dispose"); db.Context.Faults.Dispose = failure;
        var operation = Provider(db).ExecuteAsync(ct => Task.FromResult(1), default);
        Assert.Same(failure, await ObserveOperationAsync(operation, async () =>
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
        }, () => release.TrySetResult()));
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Caught_Child_Cleanup_Failure_Poisons_Owner_And_Later_Entry()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(true, true);
        var provider = Provider(db); var primary = new IOException("child"); var rollback = Transient();
        db.Context.Faults.SavepointRollback = rollback;
        Exception? child = null;
        var error = await Record.ExceptionAsync(() => provider.ExecuteAsync(async ct =>
        {
            await db.WriteAsync("outer", ct);
            child = await Record.ExceptionAsync(() => provider.ExecuteAsync<int>(async token => { await db.WriteAsync("child", token); throw primary; }, ct));
            return 1;
        }, default));
        Assert.Same(child, error);
        Assert.Equal(new Exception[] { primary, rollback }, Assert.IsType<AggregateException>(error).InnerExceptions);
        Assert.Empty(await db.ReadIndependentAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => Task.FromResult(1), default));
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Foreign_Execution_Strategy_Is_Rejected_Before_Handler()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(true, true);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Context.Database.CreateExecutionStrategy().ExecuteAsync(
            () => Provider(db).ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task System_Transactions_Ambient_Is_Rejected_Before_Handler()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        using var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(db).ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_Savepoints_Require_Explicit_Join_And_Do_Not_Promise_Rollback(bool join)
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        db.Context.Faults.NoSavepoints = true;
        await using var ambient = await db.Context.Database.BeginTransactionAsync();
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context, new() { JoinWithoutSavepoint = join });
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync<int>(async ct => { calls++; await db.WriteAsync("unisolated", ct); throw new InvalidOperationException("handler"); }, default));
        Assert.Equal(join ? 1 : 0, calls); Assert.Same(ambient, db.Context.Database.CurrentTransaction);
        Assert.Equal(join ? 1 : 0, await db.Context.Rows.CountAsync());
        await ambient.RollbackAsync(); Assert.Empty(await db.ReadIndependentAsync());
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Savepoint_Rollback_Preserves_Outer_Tracker_But_Does_Not_Restore_Child_Clr_State()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(true);
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        var outer = new TestRow { Id = 1, Name = "pending-outer" }; TestRow? child = null;
        await provider.ExecuteAsync(async ct =>
        {
            db.Context.Rows.Add(outer);
            await Assert.ThrowsAsync<IOException>(() => provider.ExecuteAsync<int>(async token =>
            {
                await db.WriteAsync("child", token); child = db.Context.Rows.Local.Single(x => x.Name == "child"); throw new IOException("child");
            }, ct));
            Assert.Contains(outer, db.Context.Rows.Local);
            Assert.Contains(child!, db.Context.Rows.Local);
            Assert.Equal(EntityState.Unchanged, db.Context.Entry(child!).State);
            Assert.Same(child, await db.Context.Rows.FindAsync([child!.Id], ct));
            Assert.Empty(await db.Context.Rows.AsNoTracking().ToArrayAsync(ct));
            // Explicit application reconciliation: rollback cannot restore accepted tracked state.
            db.Context.Entry(child).State = EntityState.Detached;
            db.Context.Entry(outer).State = EntityState.Added;
            await db.Context.SaveChangesAsync(ct);
            return 1;
        }, default);
        Assert.Equal("pending-outer", Assert.Single(await db.ReadIndependentAsync()));
    }

    [Fact]
    public async Task Begin_And_Commit_Cancellation_Preserve_Original_Identity()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        using var source = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(source.Token);
        db.Context.Faults.BeforeCommit = cancellation;
        Assert.Same(cancellation, await Record.ExceptionAsync(() => Provider(db).ExecuteAsync(ct => Task.FromResult(1), source.Token)));
        source.Cancel(); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(db).ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, source.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Manual_Scope_Commit_Uncertainty_Poisons_Provider()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        var provider = Provider(db); var error = new IOException("manual commit");
        db.Context.Faults.BeforeCommit = error;
        await using (var scope = await provider.BeginAsync(default))
            Assert.Same(error, await Record.ExceptionAsync(() => scope.CommitAsync(default)));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
        Assert.Equal(0, calls);
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Sibling_And_Recursive_Savepoints_Are_Unique_And_Ambient_Remains_Usable()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(true);
        await using var ambient = await db.Context.Database.BeginTransactionAsync();
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        await provider.ExecuteAsync(async ct =>
        {
            await provider.ExecuteAsync(async token => { await db.WriteAsync("one", token); return 1; }, ct);
            await provider.ExecuteAsync(async token => { await db.WriteAsync("two", token); return 2; }, ct);
            return 0;
        }, default);
        var names = db.Context.Faults.Savepoints.Where(x => x.StartsWith("meridian_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, names.Length); Assert.Equal(3, names.Distinct().Count());
        Assert.Same(ambient, db.Context.Database.CurrentTransaction); Assert.Equal(0, db.Context.Faults.Disposals);
        await ambient.CommitAsync(); Assert.Equal(2, (await db.ReadIndependentAsync()).Length);
    }

    [Fact]
    public async Task Caught_Child_Uncertainty_And_Later_Handler_Failure_Both_Surface_In_Occurrence_Order()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        var provider = Provider(db);
        var child = new IOException("child"); var cleanup = new IOException("savepoint"); var parent = new IOException("parent");
        db.Context.Faults.SavepointRollback = cleanup;
        var error = await Assert.ThrowsAsync<AggregateException>(() => provider.ExecuteAsync<int>(async ct =>
        {
            await Record.ExceptionAsync(() => provider.ExecuteAsync<int>(token => Task.FromException<int>(child), ct));
            throw parent;
        }, default));
        Assert.Equal(new Exception[] { child, cleanup }, Assert.IsType<AggregateException>(error.InnerExceptions[0]).InnerExceptions);
        Assert.Same(parent, error.InnerExceptions[1]);
        Assert.Equal(3, error.Flatten().InnerExceptions.Count);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("commit")]
    public async Task Actual_Cancellation_During_Provider_Operation_Is_Observed_And_Cleaned(string boundary)
    {
        var gate = new ConnectionGate();
        await using var db = await DatabaseFixture.CreateFaultAsync(false, false, gate);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationCanceledException? original = null;
        async Task Block(CancellationToken ct)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException error) { original = error; throw; }
        }
        if (boundary == "begin") gate.Operation = Block;
        else db.Context.Faults.CommitGate = Block;
        var calls = 0;
        var operation = Provider(db).ExecuteAsync(async ct => { calls++; await db.WriteAsync("canceled", ct); return 1; }, cancellation.Token);
        var failure = await ObserveOperationAsync(operation,
            () => entered.Task.WaitAsync(TimeSpan.FromSeconds(5)), cancellation.Cancel);
        Assert.NotNull(original); Assert.Same(original, failure);
        Assert.Equal(boundary == "begin" ? 0 : 1, calls);
        if (boundary == "commit") { Assert.NotEqual(cancellation.Token, db.Context.Faults.RollbackToken); Assert.True(db.Context.Faults.RollbackToken.CanBeCanceled); }
        gate.Operation = null;
        Assert.Empty(await db.ReadIndependentAsync());
    }

    [Fact]
    public async Task Original_Handler_Aggregate_Is_Not_Flattened_Or_Replaced()
    {
        await using var db = await DatabaseFixture.CreateAsync(false);
        var original = new AggregateException(new IOException("original nested"));
        Assert.Same(original, await Record.ExceptionAsync(() => Provider(db).ExecuteAsync<int>(ct => Task.FromException<int>(original), default)));
    }

    [Fact]
    public async Task Caught_Manual_Scope_Uncertainty_Also_Prevents_Owner_Commit()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        var provider = Provider(db); var failure = new IOException("manual savepoint release");
        await Assert.ThrowsAsync<IOException>(() => provider.ExecuteAsync(async ct =>
        {
            await using var child = await provider.BeginAsync(ct);
            db.Context.Faults.Release = failure;
            await Assert.ThrowsAsync<IOException>(() => child.CommitAsync(ct));
            db.Context.Faults.Release = null;
            return 1;
        }, default));
        Assert.Empty(await db.ReadIndependentAsync());
    }

    [Fact]
    public async Task Caught_Manual_Begin_PostSavepoint_Failure_Rolls_Back_Owner_And_Blocks_Reuse()
    {
        var fault = new AcquisitionFault();
        await using var db = await DatabaseFixture.CreateAsync(false, fault);
        var provider = Provider(db); var calls = 0;
        var transaction = new TransactionBehavior<Command, int>(provider);
        var request = new Command();
        var error = await Record.ExceptionAsync(() => new RetryBehavior<Command, int>().Handle(request,
            () => transaction.Handle(request, async () =>
            {
                calls++; await db.WriteAsync("outer");
                fault.FailSavepoint = true;
                Assert.Same(fault.Failure, await Record.ExceptionAsync(() => provider.BeginAsync(default)));
                return 42;
            }, default), default));
        Assert.Same(fault.Failure, error);
        Assert.Equal(1, calls); Assert.Equal(1, fault.CreatedSavepoints);
        Assert.Empty(await db.ReadIndependentAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.BeginAsync(default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Manual_Begin_PostSavepoint_Failure_Preserves_Caller_Ownership_And_Blocks_Reuse()
    {
        var fault = new AcquisitionFault();
        await using var db = await DatabaseFixture.CreateAsync(false, fault);
        await using var ambient = await db.Context.Database.BeginTransactionAsync();
        await db.WriteAsync("outer");
        var provider = Provider(db); fault.FailSavepoint = true;
        Assert.Same(fault.Failure, await Record.ExceptionAsync(() => provider.BeginAsync(default)));
        Assert.Same(ambient, db.Context.Database.CurrentTransaction);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
        Assert.Equal(0, calls); Assert.Equal(1, await db.Context.Rows.CountAsync());
        await ambient.RollbackAsync(); Assert.Empty(await db.ReadIndependentAsync());
    }

    [Fact]
    public async Task Manual_Owned_Begin_PostAcquisition_Failure_Blocks_Later_Handler()
    {
        var fault = new AcquisitionFault();
        await using var db = await DatabaseFixture.CreateAsync(false, fault);
        var provider = Provider(db); fault.FailTransaction = true;
        try
        {
            Assert.Same(fault.Failure, await Record.ExceptionAsync(() => provider.BeginAsync(default)));
            Assert.NotNull(fault.AcquiredTransaction);
            fault.FailTransaction = false;
            // The interceptor retains the underlying acquired transaction solely for fixture cleanup.
            // The adapter must not adopt an unreturned transaction or allow subsequent handler entry.
            await fault.AcquiredTransaction!.RollbackAsync();
            await fault.AcquiredTransaction.DisposeAsync(); fault.AcquiredTransaction = null;
            var calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
            Assert.Equal(0, calls);
        }
        finally
        {
            if (fault.AcquiredTransaction is not null) await fault.AcquiredTransaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task Known_Manual_Begin_Preflight_Rejection_Does_Not_Poison_Context()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        db.Context.Faults.NoSavepoints = true;
        await using var ambient = await db.Context.Database.BeginTransactionAsync();
        var provider = Provider(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.BeginAsync(default));
        db.Context.Faults.NoSavepoints = false;
        await using var scope = await provider.BeginAsync(default);
        await scope.CommitAsync(default);
        Assert.Same(ambient, db.Context.Database.CurrentTransaction);
        await ambient.RollbackAsync();
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("begin")]
    [InlineData("commit")]
    public async Task Forced_Watchdog_Timeout_Joins_Actual_Operation_And_Preserves_Both_Failures(string boundary)
    {
        var connectionGate = new ConnectionGate();
        await using var db = await DatabaseFixture.CreateFaultAsync(false, false, connectionGate);
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = new IOException("forced disposal failure");
        Exception? operationFailure = null; TimeoutException? watchdogFailure = null;
        var gateExited = false;
        async Task CancelGate(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException error) { operationFailure = error; throw; }
            finally { gateExited = true; }
        }
        if (boundary == "dispose")
        {
            db.Context.Faults.DisposeGate = async () => { await release.Task; gateExited = true; };
            db.Context.Faults.Dispose = disposal; operationFailure = disposal;
        }
        else if (boundary == "begin") connectionGate.Operation = CancelGate;
        else db.Context.Faults.CommitGate = CancelGate;
        var operation = Provider(db).ExecuteAsync(ct => Task.FromResult(1), cancellation.Token);
        async Task ForceActualTimeout()
        {
            try { await new TaskCompletionSource().Task.WaitAsync(TimeSpan.Zero); }
            catch (TimeoutException error) { watchdogFailure = error; throw; }
        }
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => ObserveOperationAsync(operation, ForceActualTimeout,
                () => { release.TrySetResult(); cancellation.Cancel(); }));
            Assert.Same(watchdogFailure, error.InnerExceptions[0]);
            Assert.Same(operationFailure, error.InnerExceptions[1]);
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.True(operation.IsCompleted); Assert.True(gateExited);
            if (boundary != "begin") Assert.Equal(1, db.Context.Faults.Disposals);
            connectionGate.Operation = null;
            Assert.Empty(await db.ReadIndependentAsync());
        }
        finally
        {
            release.TrySetResult(); cancellation.Cancel();
            await Record.ExceptionAsync(() => operation);
        }
    }

    private static async Task<Exception?> ObserveOperationAsync(Task operation, Func<Task> waitAndAssert, Action release)
    {
        var failures = new List<Exception>();
        try { await waitAndAssert(); }
        catch (Exception error) { failures.Add(error); }
        try { release(); }
        catch (Exception error) { failures.Add(error); }
        // Join the actual operation before any fixture teardown, including failed watchdog/assertion paths.
        var operationFailure = await Record.ExceptionAsync(() => operation);
        if (failures.Count == 0) return operationFailure;
        if (operationFailure is not null) failures.Add(operationFailure);
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Watchdog/assertion and observed transaction operation failures.", failures);
    }

    private static EfTransactionScopeProvider<TestContext> Provider(DatabaseFixture db) => new(db.Context, new() { UseExecutionStrategy = true });
    internal static PostgresException Transient() => new("serialization seam", "ERROR", "ERROR", "40001");
    private sealed class Command : ITransactionalRequest, IRetryableRequest
    { public int MaxRetries => 2; public TimeSpan RetryDelay => TimeSpan.Zero; public bool ShouldRetry(Exception exception) => true; }
}

internal sealed class TransactionFaults
{
    internal Exception? BeforeCommit, AfterCommit, Rollback, SavepointRollback, Dispose, Release;
    internal bool NoSavepoints, BlockRollback;
    internal int Disposals;
    internal CancellationToken RollbackToken;
    internal Func<Task>? DisposeGate;
    internal Func<CancellationToken, Task>? CommitGate;
    public List<string> Savepoints { get; } = [];
}

public sealed class FaultTransactionFactory(RelationalTransactionFactoryDependencies dependencies) : RelationalTransactionFactory(dependencies)
{
    public override RelationalTransaction Create(IRelationalConnection connection, DbTransaction transaction, Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned)
        => new FaultTransaction(connection, transaction, transactionId, logger, transactionOwned, Dependencies.SqlGenerationHelper, ((TestContext)connection.Context).Faults);
}

internal sealed class FaultTransaction(IRelationalConnection connection, DbTransaction transaction, Guid transactionId,
    IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned, ISqlGenerationHelper helper, TransactionFaults faults)
    : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, helper)
{
    public override bool SupportsSavepoints => !faults.NoSavepoints;
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    { if (faults.CommitGate is not null) await faults.CommitGate(cancellationToken); if (faults.BeforeCommit is not null) throw faults.BeforeCommit; await base.CommitAsync(cancellationToken); if (faults.AfterCommit is not null) throw faults.AfterCommit; }
    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    { faults.RollbackToken = cancellationToken; if (faults.BlockRollback) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); if (faults.Rollback is not null) throw faults.Rollback; await base.RollbackAsync(cancellationToken); }
    public override Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
    { if (faults.SavepointRollback is not null) throw faults.SavepointRollback; return base.RollbackToSavepointAsync(name, cancellationToken); }
    public override Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
    { if (faults.Release is not null) throw faults.Release; return base.ReleaseSavepointAsync(name, cancellationToken); }
    public override Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
    { faults.Savepoints.Add(name); return base.CreateSavepointAsync(name, cancellationToken); }
    public override async ValueTask DisposeAsync()
    { faults.Disposals++; if (faults.DisposeGate is not null) await faults.DisposeGate(); await base.DisposeAsync(); if (faults.Dispose is not null) throw faults.Dispose; }
}
internal sealed class OpenFault : DbConnectionInterceptor
{
    public int Remaining, Failures;
    public PostgresException Failure { get; } = TransactionFaultTests.Transient();
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    { if (Remaining-- > 0) { Failures++; throw Failure; } return ValueTask.FromResult(result); }
}

internal sealed class ConnectionGate : DbConnectionInterceptor
{
    internal Func<CancellationToken, Task>? Operation;
    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    { if (Operation is not null) await Operation(cancellationToken); return result; }
}

internal sealed class AcquisitionFault : DbTransactionInterceptor
{
    internal readonly IOException Failure = new("post-acquisition uncertainty");
    internal bool FailSavepoint, FailTransaction;
    internal int CreatedSavepoints;
    internal DbTransaction? AcquiredTransaction;
    public override Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    { if (FailSavepoint) { CreatedSavepoints++; throw Failure; } return Task.CompletedTask; }
    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    { if (FailTransaction) { AcquiredTransaction = result; throw Failure; } return ValueTask.FromResult(result); }
}

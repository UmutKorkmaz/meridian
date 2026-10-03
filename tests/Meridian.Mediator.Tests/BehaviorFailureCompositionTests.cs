using Meridian.Mediator.Behaviors;

namespace Meridian.Mediator.Tests;

public class BehaviorFailureCompositionTests
{
    [Fact]
    public async Task Handler_And_Cleanup_Failures_Are_Both_Observable()
    {
        var primary = new InvalidOperationException("handler-private");
        var cleanup = new IOException("release-private");
        var store = new ClaimStore { ReleaseFailure = cleanup };
        var behavior = new IdempotencyBehavior<Command, string>(store);
        var error = await Assert.ThrowsAsync<AggregateException>(() => behavior.Handle(
            new Command(), () => Task.FromException<string>(primary), default));
        Assert.Equal(new Exception[] { primary, cleanup }, error.InnerExceptions);
        Assert.True(store.Claimed);
    }

    [Fact]
    public async Task Canceled_Handler_Releases_Claim_With_Bounded_Cleanup()
    {
        using var caller = new CancellationTokenSource();
        var primary = new OperationCanceledException(caller.Token);
        var store = new ClaimStore();
        var behavior = new IdempotencyBehavior<Command, string>(store);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => behavior.Handle(
            new Command(), () => { caller.Cancel(); return Task.FromException<string>(primary); }, caller.Token));
        Assert.Same(primary, error);
        Assert.True(store.ReleaseToken.CanBeCanceled);
        Assert.NotEqual(caller.Token, store.ReleaseToken);
        Assert.False(store.Claimed);
        Assert.Equal("later", await behavior.Handle(new Command(), () => Task.FromResult("later"), default));
    }

    [Fact]
    public async Task Transaction_Handler_Rollback_And_Disposal_Failures_Are_All_Observable()
    {
        var primary = new InvalidOperationException("handler");
        var rollback = new IOException("rollback");
        var disposal = new IOException("dispose");
        var scope = new Scope { RollbackFailure = rollback, DisposeFailure = disposal };
        var behavior = new TransactionBehavior<Command, string>(new Provider(scope));
        var error = await Assert.ThrowsAsync<AggregateException>(() => behavior.Handle(
            new Command(), () => Task.FromException<string>(primary), default));
        Assert.Equal(new Exception[] { primary, rollback, disposal }, error.Flatten().InnerExceptions);
    }

    [Fact]
    public async Task Canceled_Transaction_Rolls_Back_With_Independent_Cleanup_Token()
    {
        using var caller = new CancellationTokenSource();
        var primary = new OperationCanceledException(caller.Token);
        var scope = new Scope();
        var behavior = new TransactionBehavior<Command, string>(new Provider(scope));
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => behavior.Handle(
            new Command(), () => { caller.Cancel(); return Task.FromException<string>(primary); }, caller.Token));
        Assert.Same(primary, error);
        Assert.True(scope.RolledBack);
        Assert.True(scope.Disposed);
        Assert.True(scope.RollbackToken.CanBeCanceled);
        Assert.NotEqual(caller.Token, scope.RollbackToken);
    }

    [Fact]
    public async Task Failed_Commit_Does_Not_Replay_Successful_Handler_Through_Retry()
    {
        var failure = new IOException("uncertain commit");
        var scope = new Scope { CommitFailure = failure };
        var transaction = new TransactionBehavior<Command, string>(new Provider(scope));
        var retry = new RetryBehavior<Command, string>();
        var calls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => retry.Handle(new Command(),
            () => transaction.Handle(new Command(), () => { calls++; return Task.FromResult("side effect"); }, default), default));
        Assert.Same(failure, error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failed_Atomic_Completion_Preserves_Claim_And_Original_Failure_Through_Retry()
    {
        var failure = new IOException("uncertain completion");
        var store = new ClaimStore { CompleteFailure = failure };
        var idempotency = new IdempotencyBehavior<Command, string>(store);
        var retry = new RetryBehavior<Command, string>();
        var calls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => retry.Handle(new Command(),
            () => idempotency.Handle(new Command(), () => { calls++; return Task.FromResult("side effect"); }, default), default));
        Assert.Same(failure, error);
        Assert.Equal(1, calls);
        Assert.True(store.Claimed);
        await Assert.ThrowsAsync<IdempotencyInProgressException>(() => idempotency.Handle(
            new Command(), () => Task.FromResult("must not replay"), default));
    }

    [Fact]
    public async Task Successful_Handler_Audit_Failure_Is_Recorded_Once_And_Not_Retried()
    {
        var failure = new IOException("audit unavailable");
        var sink = new Sink(failure);
        var audit = new AuditBehavior<Command, string>(sink);
        var retry = new RetryBehavior<Command, string>();
        var calls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => retry.Handle(new Command(),
            () => audit.Handle(new Command(), () => { calls++; return Task.FromResult("side effect"); }, default), default));
        Assert.Same(failure, error);
        Assert.Equal(1, calls);
        Assert.Single(sink.Entries);
        Assert.True(sink.Entries[0].Success);
    }

    [Fact]
    public async Task Legacy_NonAtomic_Store_Allows_Concurrent_Duplicate_Execution()
    {
        var store = new LegacyStore();
        var behavior = new IdempotencyBehavior<Command, string>(store);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        async Task<string> Handler()
        {
            if (Interlocked.Increment(ref executions) == 2) bothEntered.SetResult();
            await release.Task;
            return "completed";
        }
        var first = behavior.Handle(new Command(), Handler, default);
        var second = behavior.Handle(new Command(), Handler, default);
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, executions);
        }
        finally { release.SetResult(); }
        Assert.Equal(new[] { "completed", "completed" }, await Task.WhenAll(first, second));
    }

    private sealed class LegacyStore : IIdempotencyStore
    {
        private object? _response;
        public Exception? Failure;
        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
            Task.FromResult((_response is not null, _response));
        public Task StoreAsync(string key, object response, CancellationToken ct)
        {
            if (Failure is not null) return Task.FromException(Failure);
            _response = response;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Failed_Legacy_Store_Does_Not_Replay_Successful_Handler()
    {
        var failure = new IOException("store");
        var behavior = new IdempotencyBehavior<Command, string>(new LegacyStore { Failure = failure });
        var calls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => new RetryBehavior<Command, string>().Handle(
            new Command(), () => behavior.Handle(new Command(), () => { calls++; return Task.FromResult("done"); }, default), default));
        Assert.Same(failure, error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failed_Disposal_After_Commit_Does_Not_Replay_Successful_Handler()
    {
        var failure = new IOException("dispose");
        var behavior = new TransactionBehavior<Command, string>(new Provider(new Scope { DisposeFailure = failure }));
        var calls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => new RetryBehavior<Command, string>().Handle(
            new Command(), () => behavior.Handle(new Command(), () => { calls++; return Task.FromResult("done"); }, default), default));
        Assert.Same(failure, error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_And_Cleanup_Failure_Aggregate_Is_Not_Retried()
    {
        var primary = new OperationCanceledException();
        var cleanup = new IOException("cleanup");
        var calls = 0;
        var error = await Assert.ThrowsAsync<AggregateException>(() => new RetryBehavior<Command, string>().Handle(
            new Command(), () => { calls++; return Task.FromException<string>(new AggregateException(primary, cleanup)); }, default));
        Assert.Same(primary, error.InnerExceptions[0]);
        Assert.Same(cleanup, error.InnerExceptions[1]);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("rollback")]
    [InlineData("audit")]
    public async Task Cleanup_Budget_Actually_Cancels_Cooperative_Provider(string boundary)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("primary");
        OperationCanceledException? cleanup = null;
        async Task WaitForBudget(CancellationToken token)
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException ex) { cleanup = ex; throw; }
        }
        var request = new Command();
        Task<string> operation = boundary switch
        {
            "release" => new IdempotencyBehavior<Command, string>(new ClaimStore { Release = WaitForBudget })
                .Handle(request, () => Task.FromException<string>(primary), default),
            "rollback" => new TransactionBehavior<Command, string>(new Provider(new Scope { Rollback = WaitForBudget }))
                .Handle(request, () => Task.FromException<string>(primary), default),
            _ => new AuditBehavior<Command, string>(new BudgetSink(WaitForBudget))
                .Handle(request, () => Task.FromException<string>(primary), default)
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<AggregateException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.Same(primary, error.InnerExceptions[0]);
        Assert.NotNull(cleanup);
        Assert.Same(cleanup, error.InnerExceptions[1]);
        Assert.True(cleanup.CancellationToken.IsCancellationRequested);
    }

    private sealed class BudgetSink(Func<CancellationToken, Task> record) : IAuditSink
    {
        public Task RecordAsync(AuditEntry entry, CancellationToken ct) => record(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outer_Atomic_Claim_Is_Retained_When_Inner_Commit_Is_Uncertain(bool wrap)
    {
        var failure = new IOException("commit");
        var store = new ClaimStore();
        var idempotency = new IdempotencyBehavior<Command, string>(store);
        var transaction = new TransactionBehavior<Command, string>(new Provider(new Scope { CommitFailure = failure }));
        var calls = 0;
        var error = await Record.ExceptionAsync(() => idempotency.Handle(new Command(), async () =>
        {
            try { return await transaction.Handle(new Command(), () => { calls++; return Task.FromResult("done"); }, default); }
            catch (Exception ex) when (wrap) { throw new AggregateException(ex); }
        }, default));
        Assert.NotNull(error);
        if (wrap) Assert.Same(failure, Assert.IsType<AggregateException>(error).InnerException);
        else Assert.Same(failure, error);
        Assert.True(store.Claimed);
        await Assert.ThrowsAsync<IdempotencyInProgressException>(() => idempotency.Handle(new Command(),
            () => { calls++; return Task.FromResult("replayed"); }, default));
        Assert.Equal(1, calls);
    }

    private sealed record Command : IIdempotentRequest, ITransactionalRequest, IRetryableRequest
    {
        public string IdempotencyKey => "same";
        public int MaxRetries => 2;
        public TimeSpan RetryDelay => TimeSpan.Zero;
    }

    private sealed class ClaimStore : IAtomicIdempotencyStore
    {
        public bool Claimed;
        public Exception? ReleaseFailure;
        public Exception? CompleteFailure;
        public CancellationToken ReleaseToken;
        public Func<CancellationToken, Task>? Release;
        public Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct)
        {
            var result = new IdempotencyClaimResult(Claimed ? IdempotencyClaimStatus.InFlight : IdempotencyClaimStatus.Claimed);
            Claimed = true;
            return Task.FromResult(result);
        }
        public Task CompleteAsync(string key, object? response, CancellationToken ct) =>
            CompleteFailure is null ? Task.CompletedTask : Task.FromException(CompleteFailure);
        public Task ReleaseAsync(string key, CancellationToken ct)
        {
            ReleaseToken = ct;
            if (Release is not null) return Release(ct);
            ct.ThrowIfCancellationRequested();
            if (ReleaseFailure is not null) return Task.FromException(ReleaseFailure);
            Claimed = false;
            return Task.CompletedTask;
        }
        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task StoreAsync(string key, object response, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Provider(Scope scope) : ITransactionScopeProvider
    {
        public Task<ITransactionScope> BeginAsync(CancellationToken ct) => Task.FromResult<ITransactionScope>(scope);
    }

    private sealed class Scope : ITransactionScope
    {
        public Exception? CommitFailure;
        public Exception? RollbackFailure;
        public Exception? DisposeFailure;
        public bool RolledBack;
        public bool Disposed;
        public CancellationToken RollbackToken;
        public Func<CancellationToken, Task>? Rollback;
        public Task CommitAsync(CancellationToken ct) => CommitFailure is null ? Task.CompletedTask : Task.FromException(CommitFailure);
        public Task RollbackAsync(CancellationToken ct)
        {
            RollbackToken = ct;
            if (Rollback is not null) return Rollback(ct);
            ct.ThrowIfCancellationRequested();
            if (RollbackFailure is not null) return Task.FromException(RollbackFailure);
            RolledBack = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class Sink(Exception failure) : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];
        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.FromException(failure);
        }
    }
}

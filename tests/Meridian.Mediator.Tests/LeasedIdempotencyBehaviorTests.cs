#pragma warning disable CA1707 // Contract test names are deliberately descriptive.
using Meridian.Mediator.Behaviors;

namespace Meridian.Mediator.Tests;

public class LeasedIdempotencyBehaviorTests
{
    [Fact]
    public async Task Leased_Completed_Null_Is_Replayed_Without_Handler()
    {
        var store = new Store { Claim = new(LeasedIdempotencyStatus.Completed) };
        var calls = 0;
        var result = await new IdempotencyBehavior<Request, string?>(store).Handle(new(), () => { calls++; return Task.FromResult<string?>("wrong"); }, default);
        Assert.Null(result); Assert.Equal(0, calls); Assert.Equal(0, store.LegacyCalls);
    }

    [Fact]
    public async Task Unknown_Handler_Failure_Is_Not_Released_And_Suppresses_Retry()
    {
        var store = new Store(); var original = new IOException("effect may have happened");
        var thrown = await Assert.ThrowsAsync<IOException>(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), () => throw original, default));
        Assert.Same(original, thrown); Assert.False(RetryPolicy.TransientOnly(thrown)); Assert.Equal(0, store.Releases); Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task Failed_Completion_Does_Not_Automatically_Rerun_Handler()
    {
        var original = new IOException("completion failed"); var store = new Store { CompletionFailure = original }; var calls = 0;
        var thrown = await Assert.ThrowsAsync<IOException>(() => new RetryBehavior<Request, string>().Handle(new(), () => new IdempotencyBehavior<Request, string>(store).Handle(new(), () => { calls++; return Task.FromResult("done"); }, default), default));
        Assert.Same(original, thrown); Assert.False(RetryPolicy.TransientOnly(thrown)); Assert.Equal(1, calls); Assert.Equal(0, store.Releases);
    }

    [Theory]
    [InlineData(LeasedIdempotencyStatus.InFlight)]
    [InlineData(LeasedIdempotencyStatus.OutcomeUnknown)]
    [InlineData((LeasedIdempotencyStatus)99)]
    public async Task Retained_Or_Invalid_Claims_Never_Execute_Or_Retry(LeasedIdempotencyStatus status)
    {
        var store = new Store { Claim = new(status) }; var calls = 0;
        var error = await Record.ExceptionAsync(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), () => { calls++; return Task.FromResult("bad"); }, default));
        Assert.NotNull(error); Assert.False(RetryPolicy.TransientOnly(error)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Unusable_Lease_And_Malformed_Claim_Retain_Protection()
    {
        foreach (var claim in new[] { new LeasedIdempotencyClaim(LeasedIdempotencyStatus.Claimed), new(LeasedIdempotencyStatus.Completed, "x", new("k", "o", "c", DateTimeOffset.UtcNow)), new(LeasedIdempotencyStatus.Claimed, Lease: new("k", "o", "c", DateTimeOffset.MinValue)) })
        {
            var store = new Store { Claim = claim }; var calls = 0;
            var error = await Record.ExceptionAsync(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), () => { calls++; return Task.FromResult("bad"); }, default));
            Assert.NotNull(error); Assert.Equal(0, calls); Assert.Equal(0, store.Releases);
        }
    }

    [Fact]
    public async Task Canceled_Handler_Cleanup_Is_Bounded()
    {
        using var request = new CancellationTokenSource(); request.Cancel(); var original = new OperationCanceledException(request.Token); var store = new Store();
        var task = new IdempotencyBehavior<Request, string>(store).Handle(new(), () => throw original, request.Token);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(original, error); Assert.Equal(0, store.Releases); Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task Caller_Cancellation_After_Effect_Still_Attempts_Independent_Completion()
    {
        using var request = new CancellationTokenSource(); var store = new Store();
        var response = await new IdempotencyBehavior<Request, string>(store).Handle(new(), () => { request.Cancel(); return Task.FromResult("effect"); }, request.Token);
        Assert.Equal("effect", response); Assert.False(store.CompletionToken.IsCancellationRequested); Assert.True(store.CompletionToken.CanBeCanceled); Assert.Equal(1, store.Completions);
    }

    [Fact]
    public async Task Renewal_Loss_Does_Not_Reexecute_Or_Complete_Handler()
    {
        var store = new Store(); var renewed = Barrier(); var calls = 0;
        store.Renew = (_, _) => { renewed.TrySetResult(); return Task.FromResult(false); };
        var error = await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => Run(store, async () => { calls++; await Watch(renewed.Task); return "effect"; }));
        Assert.False(RetryPolicy.TransientOnly(error)); Assert.Equal(1, calls); Assert.Equal(0, store.Completions); Assert.Equal(0, store.Releases);
    }

    [Fact]
    public async Task Handler_And_Renewal_Failures_Preserve_Exact_References_Primary_First()
    {
        var store = new Store(); var renewed = Barrier();
        var secondary = new IOException("renew"); var primary = new IOException("handler");
        store.Renew = (_, _) => { renewed.TrySetResult(); return Task.FromException<bool>(secondary); };
        var error = await Assert.ThrowsAsync<AggregateException>(() => Run(store, async () => { await Watch(renewed.Task); throw primary; }));
        Assert.Collection(error.InnerExceptions, ex => Assert.Same(primary, ex), ex => Assert.Same(secondary, ex)); Assert.False(RetryPolicy.TransientOnly(error));
    }

    [Fact]
    public async Task Renewal_Is_Sequential_And_Uses_Same_Immutable_Lease()
    {
        var store = new Store(); var entered = Barrier(); var release = Barrier(); var twice = Barrier(); var active = 0; var calls = 0;
        store.Renew = async (lease, ct) =>
        {
            Assert.Same(store.Claim.Lease, lease); Assert.Equal(1, Interlocked.Increment(ref active));
            try { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Watch(release.Task); } else twice.TrySetResult(); return true; }
            finally { Interlocked.Decrement(ref active); }
        };
        var task = Run(store, async () => { await Watch(twice.Task); return "done"; });
        try
        {
            await Watch(entered.Task); clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(1, calls); Assert.Equal(1, active);
            release.TrySetResult(); await clock.WaitForScheduledCount(3); clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal("done", await Watch(task)); Assert.Equal(0, active); Assert.Equal(1, store.Completions);
        }
        finally { release.TrySetResult(); twice.TrySetResult(); await Observe(task); }
    }

    [Fact]
    public async Task Noncooperative_Provider_Keeps_Request_Alive_Until_It_Settles()
    {
        var store = new Store(); var entered = Barrier(); var handler = Barrier();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Renew = (_, _) => { entered.TrySetResult(); return gate.Task; };
        var task = Run(store, async () => { await Watch(handler.Task); return "effect"; });
        try
        {
            await Watch(entered.Task); clock.Advance(TimeSpan.FromSeconds(10)); Assert.False(task.IsCompleted); Assert.Equal(0, store.Completions);
            gate.TrySetResult(true); handler.TrySetResult();
            await Assert.ThrowsAsync<TimeoutException>(() => Watch(task)); Assert.Equal(0, store.Completions);
        }
        finally { gate.TrySetResult(true); handler.TrySetResult(); await Observe(task); }
    }

    [Fact]
    public async Task False_Renewal_Racing_Stop_Is_Not_Erased()
    {
        var store = new Store(); var entered = Barrier(); var stopped = Barrier();
        store.Renew = async (_, ct) => { using var reg = ct.Register(() => stopped.TrySetResult()); entered.TrySetResult(); await Watch(stopped.Task); return false; };
        var task = Run(store, async () => { await Watch(entered.Task); return "effect"; });
        try { await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => Watch(task)); Assert.Equal(0, store.Completions); }
        finally { entered.TrySetResult(); stopped.TrySetResult(); await Observe(task); }
    }

    [Fact]
    public async Task Expected_Stop_Cancellation_Does_Not_Invent_A_Secondary_Failure()
    {
        var store = new Store(); var entered = Barrier(); var original = new IOException("original");
        store.Renew = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return true; };
        var actual = await Assert.ThrowsAsync<IOException>(() => Run(store, async () => { await Watch(entered.Task); throw original; }));
        Assert.Same(original, actual);
    }

    [Fact]
    public async Task Caller_Cancellation_While_Renewal_Is_Blocked_Stops_And_Joins_Before_Return()
    {
        using var caller = new CancellationTokenSource(); var store = new Store();
        var entered = Barrier(); var cancelled = Barrier(); var release = Barrier(); var handler = Barrier(); var joined = false;
        store.Renew = async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return true; }
            finally { cancelled.TrySetResult(); await Watch(release.Task); joined = true; }
        };
        var task = Run(store, async () => { await Watch(handler.Task); return "effect"; }, caller.Token);
        try
        {
            await Watch(entered.Task); caller.Cancel(); await Watch(cancelled.Task); handler.TrySetResult();
            Assert.False(task.IsCompleted); Assert.False(joined); Assert.Equal(0, store.Completions);
            release.TrySetResult(); Assert.Equal("effect", await Watch(task)); Assert.True(joined);
            Assert.Equal(1, store.Completions); Assert.False(store.CompletionToken.IsCancellationRequested);
        }
        finally { caller.Cancel(); handler.TrySetResult(); release.TrySetResult(); await Observe(task); }
    }

    [Fact]
    public async Task Throwing_Stop_Callback_Remains_Observable_And_Renewal_Is_Joined()
    {
        var store = new Store(); var entered = Barrier(); var original = new IOException("callback"); var finished = false;
        store.Renew = async (_, ct) =>
        {
            using var registration = ct.Register(() => throw original);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return true; }
            finally { finished = true; }
        };
        var error = await Assert.ThrowsAsync<AggregateException>(() => Run(store, async () => { await Watch(entered.Task); return "effect"; }));
        Assert.Contains(original, error.Flatten().InnerExceptions); Assert.True(finished); Assert.Equal(0, store.Completions); Assert.False(RetryPolicy.TransientOnly(error));
    }

    [Fact]
    public async Task Unexpected_Cancellation_Is_Preserved_Without_Fabricating_Completion()
    {
        var store = new Store(); var entered = Barrier(); var original = new OperationCanceledException("provider cancellation");
        store.Renew = (_, _) => { entered.TrySetResult(); return Task.FromException<bool>(original); };
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => Run(store, async () => { await Watch(entered.Task); return "effect"; }));
        Assert.Same(original, error); Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task Noncooperative_Renewal_Is_Joined_Even_After_Cleanup_Deadline()
    {
        var store = new Store(); var entered = Barrier(); var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Renew = (_, _) => { entered.TrySetResult(); return gate.Task; };
        var task = Run(store, async () => { await Watch(entered.Task); return "effect"; });
        try
        {
            await Watch(entered.Task); await clock.WaitForScheduledCount(3); clock.Advance(TimeSpan.FromSeconds(10));
            Assert.False(task.IsCompleted); gate.TrySetResult(true);
            var error = await Assert.ThrowsAsync<AggregateException>(() => Watch(task));
            Assert.Collection(error.InnerExceptions, ex => Assert.IsType<TimeoutException>(ex), ex => Assert.IsType<TimeoutException>(ex)); Assert.Equal(0, store.Completions);
        }
        finally { gate.TrySetResult(true); await Observe(task); }
    }

    [Fact]
    public async Task Virtual_Clock_Drives_Renewal_Before_Wall_Clock_Interval()
    {
        var store = new Store(); var entered = Barrier(); var release = Barrier();
        store.Renew = (_, _) => { entered.TrySetResult(); return Task.FromResult(true); };
        var task = Run(store, async () => { await Watch(release.Task); return "done"; });
        try { await Watch(entered.Task); }
        finally { release.TrySetResult(); await Watch(task); }
    }

    private readonly ManualTimeProvider clock = new();
    private Task<string> Run(Store store, RequestHandlerDelegate<string> handler, CancellationToken token = default)
    {
        store.Claim = store.Claim with { Lease = store.Claim.Lease! with { ExpiresAt = clock.GetUtcNow().AddSeconds(30) } };
        var task = new IdempotencyBehavior<Request, string>(store) { LeaseTimeProvider = clock }.Handle(new(), handler, token);
        clock.Advance(TimeSpan.FromSeconds(10));
        return Watch(task);
    }
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Watch(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static Task<T> Watch<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    // Failure-path gate release is followed by a bounded join; the assertion path owns the failure.
    private static async Task Observe(Task task) { try { await Watch(task); } catch { } }

    public sealed record Request : IIdempotentRequest, IRetryableRequest { public string IdempotencyKey => "key"; }
    internal sealed class Store : ILeasedIdempotencyStore
    {
        internal LeasedIdempotencyClaim Claim = new(LeasedIdempotencyStatus.Claimed, Lease: new("effective", "owner", "text", DateTimeOffset.UtcNow.AddSeconds(30)));
        internal int LegacyCalls, Releases, Completions;
        internal Exception? CompletionFailure;
        internal CancellationToken CompletionToken;
        internal Func<IdempotencyLease, CancellationToken, Task<bool>>? Renew;
        public Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct) => Task.FromResult(Claim);
        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) { LegacyCalls++; return Task.FromResult<(bool, object?)>((false, null)); }
        public Task StoreAsync(string key, object response, CancellationToken ct) { LegacyCalls++; return Task.CompletedTask; }
        public Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct) { Completions++; CompletionToken = ct; return CompletionFailure is null ? Task.CompletedTask : Task.FromException(CompletionFailure); }
        public Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct) { Releases++; return Task.CompletedTask; }
        public Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct) => Renew?.Invoke(lease, ct) ?? Task.FromResult(true);
    }
}

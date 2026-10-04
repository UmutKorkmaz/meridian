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
        var store = new Store(); store.ShortLease(); var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Renew = (_, _) => { renewed.SetResult(); return Task.FromResult(false); }; var calls = 0;
        var error = await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { calls++; await renewed.Task; return "effect"; }, default));
        Assert.False(RetryPolicy.TransientOnly(error)); Assert.Equal(1, calls); Assert.Equal(0, store.Completions); Assert.Equal(0, store.Releases);
    }

    [Fact]
    public async Task Handler_And_Renewal_Failures_Preserve_Exact_References_Primary_First()
    {
        var store = new Store(); store.ShortLease(); var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondary = new IOException("renew"); var primary = new IOException("handler");
        store.Renew = (_, _) => { renewed.SetResult(); return Task.FromException<bool>(secondary); };
        var error = await Assert.ThrowsAsync<AggregateException>(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await renewed.Task; throw primary; }, default));
        Assert.Collection(error.InnerExceptions, ex => Assert.Same(primary, ex), ex => Assert.Same(secondary, ex)); Assert.False(RetryPolicy.TransientOnly(error));
    }

    [Fact]
    public async Task Renewal_Is_Sequential_And_Uses_Same_Immutable_Lease()
    {
        var store = new Store(); store.ShortLease(); var twice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var active = 0; var calls = 0;
        store.Renew = async (lease, ct) => { Assert.Same(store.Claim.Lease, lease); Assert.Equal(1, Interlocked.Increment(ref active)); await Task.Delay(5, ct); Interlocked.Decrement(ref active); if (Interlocked.Increment(ref calls) == 2) twice.TrySetResult(); return true; };
        Assert.Equal("done", await new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await twice.Task; return "done"; }, default));
        Assert.Equal(0, active); Assert.Equal(1, store.Completions);
    }

    [Fact]
    public async Task Noncooperative_Provider_Keeps_Request_Alive_Until_It_Settles()
    {
        var store = new Store(); store.ShortLease(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Renew = (_, _) => { entered.SetResult(); return gate.Task; };
        var request = new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await entered.Task; return "effect"; }, default);
        await entered.Task; await Task.Delay(160); Assert.False(request.IsCompleted); Assert.Equal(0, store.Completions);
        gate.SetResult(true); await Assert.ThrowsAsync<TimeoutException>(() => request); Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task False_Renewal_Racing_Stop_Is_Not_Erased()
    {
        var store = new Store(); store.ShortLease(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Renew = async (_, ct) => { using var reg = ct.Register(() => stopped.TrySetResult()); entered.SetResult(); await stopped.Task; return false; };
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await entered.Task; return "effect"; }, default));
        Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task Expected_Stop_Cancellation_Does_Not_Invent_A_Secondary_Failure()
    {
        var store = new Store(); store.ShortLease(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var original = new IOException("original");
        store.Renew = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return true; };
        var actual = await Assert.ThrowsAsync<IOException>(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await entered.Task; throw original; }, default));
        Assert.Same(original, actual);
    }

    [Fact]
    public async Task Throwing_Stop_Callback_Remains_Observable_And_Renewal_Is_Joined()
    {
        var store = new Store(); store.ShortLease(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var original = new IOException("callback"); var finished = false;
        store.Renew = async (_, ct) =>
        {
            using var registration = ct.Register(() => throw original);
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return true; }
            finally { finished = true; }
        };
        var error = await Assert.ThrowsAsync<AggregateException>(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await entered.Task; return "effect"; }, default));
        Assert.Contains(original, error.Flatten().InnerExceptions); Assert.True(finished); Assert.Equal(0, store.Completions); Assert.False(RetryPolicy.TransientOnly(error));
    }

    [Fact]
    public async Task Unexpected_Cancellation_Is_Preserved_Without_Fabricating_Completion()
    {
        var store = new Store(); store.ShortLease(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var original = new OperationCanceledException("provider cancellation");
        store.Renew = (_, _) => { entered.SetResult(); return Task.FromException<bool>(original); };
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await entered.Task; return "effect"; }, default));
        Assert.Same(original, error); Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task Noncooperative_Renewal_Is_Joined_Even_After_Cleanup_Deadline()
    {
        var store = new Store(); store.ShortLease(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Renew = (_, _) => { entered.SetResult(); return gate.Task; };
        var task = new IdempotencyBehavior<Request, string>(store).Handle(new(), async () => { await entered.Task; return "effect"; }, default);
        await entered.Task; await Task.Delay(5200); Assert.False(task.IsCompleted); gate.SetResult(true);
        var error = await Assert.ThrowsAsync<AggregateException>(() => task); Assert.Collection(error.InnerExceptions, ex => Assert.IsType<TimeoutException>(ex), ex => Assert.IsType<TimeoutException>(ex)); Assert.All(error.InnerExceptions, ex => Assert.IsType<TimeoutException>(ex)); Assert.Equal(0, store.Completions);
    }

    public sealed record Request : IIdempotentRequest, IRetryableRequest { public string IdempotencyKey => "key"; }
    internal sealed class Store : ILeasedIdempotencyStore
    {
        internal LeasedIdempotencyClaim Claim = new(LeasedIdempotencyStatus.Claimed, Lease: new("effective", "owner", "text", DateTimeOffset.UtcNow.AddSeconds(30)));
        internal int LegacyCalls, Releases, Completions;
        internal Exception? CompletionFailure;
        internal CancellationToken CompletionToken;
        internal Func<IdempotencyLease, CancellationToken, Task<bool>>? Renew;
        internal void ShortLease() => Claim = Claim with { Lease = Claim.Lease! with { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(180) } };
        public Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct) => Task.FromResult(Claim);
        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) { LegacyCalls++; return Task.FromResult<(bool, object?)>((false, null)); }
        public Task StoreAsync(string key, object response, CancellationToken ct) { LegacyCalls++; return Task.CompletedTask; }
        public Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct) { Completions++; CompletionToken = ct; return CompletionFailure is null ? Task.CompletedTask : Task.FromException(CompletionFailure); }
        public Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct) { Releases++; return Task.CompletedTask; }
        public Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct) => Renew?.Invoke(lease, ct) ?? Task.FromResult(true);
    }
}

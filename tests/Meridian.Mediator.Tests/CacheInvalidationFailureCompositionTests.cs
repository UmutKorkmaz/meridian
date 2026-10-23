using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Tests;

public class CacheInvalidationFailureCompositionTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Failed_Invalidation_Does_Not_Replay_A_Successful_Command(bool synchronousFailure, int failureOnCall)
    {
        var failure = new IOException("cache removal failed after command success");
        var cache = new Cache(failure, synchronousFailure, failureOnCall);
        var invalidation = new CacheInvalidationBehavior<Command, string>(cache);
        var retry = new RetryBehavior<Command, string>();
        var request = new Command(MultipleKeys: true);
        var effects = 0;

        var error = await Record.ExceptionAsync(() => retry.Handle(request,
            () => invalidation.Handle(request, () =>
            {
                effects++;
                return Task.FromResult("committed");
            }, default), default));

        Assert.Same(failure, error);
        Assert.Equal(1, effects);
        Assert.Equal(request.CacheKeysToInvalidate.Take(failureOnCall), cache.RemovedKeys);
        Assert.False(RetryPolicy.TransientOnly(failure));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_Invalidation_Retains_An_Outer_Atomic_Claim(bool cancellation)
    {
        Exception failure = cancellation
            ? new OperationCanceledException("cache removal canceled after command success")
            : new IOException("cache removal failed after command success");
        var cache = new Cache(failure);
        var store = new ClaimStore();
        var idempotency = new IdempotencyBehavior<Command, string>(store);
        var invalidation = new CacheInvalidationBehavior<Command, string>(cache);
        var retry = new RetryBehavior<Command, string>();
        var request = new Command();
        var effects = 0;
        Task<string> Handler()
        {
            effects++;
            return Task.FromResult("committed");
        }

        var error = await Record.ExceptionAsync(() => retry.Handle(request,
            () => idempotency.Handle(request,
                () => invalidation.Handle(request, Handler, default), default), default));

        Assert.Same(failure, error);
        Assert.Equal(1, effects);
        Assert.True(store.Claimed);
        Assert.Equal(0, store.Completions);
        Assert.Equal(0, store.Releases);
        Assert.Equal(request.CacheKeysToInvalidate, cache.RemovedKeys);
        await Assert.ThrowsAsync<IdempotencyInProgressException>(() =>
            idempotency.Handle(request, Handler, default));
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task Handler_Failure_Before_Success_Remains_Retryable_And_Then_Invalidates()
    {
        var failure = new IOException("transient handler failure before success");
        var cache = new Cache();
        var invalidation = new CacheInvalidationBehavior<Command, string>(cache);
        var retry = new RetryBehavior<Command, string>();
        var request = new Command();
        var calls = 0;

        var result = await retry.Handle(request, () => invalidation.Handle(request,
            () => ++calls == 1 ? Task.FromException<string>(failure) : Task.FromResult("committed"), default), default);

        Assert.Equal("committed", result);
        Assert.Equal(2, calls);
        Assert.True(RetryPolicy.TransientOnly(failure));
        Assert.Equal(request.CacheKeysToInvalidate, cache.RemovedKeys);
    }

    [Fact]
    public async Task Caller_Cancellation_After_Success_Retains_The_Outer_Atomic_Claim()
    {
        using var caller = new CancellationTokenSource();
        var cache = new Cache(honorCancellation: true);
        var store = new ClaimStore();
        var idempotency = new IdempotencyBehavior<Command, string>(store);
        var invalidation = new CacheInvalidationBehavior<Command, string>(cache);
        var retry = new RetryBehavior<Command, string>();
        var request = new Command();
        var effects = 0;
        Task<string> Handler()
        {
            effects++;
            caller.Cancel();
            return Task.FromResult("committed");
        }

        var error = await Record.ExceptionAsync(() => retry.Handle(request,
            () => idempotency.Handle(request,
                () => invalidation.Handle(request, Handler, caller.Token), caller.Token), caller.Token));

        var cancellation = Assert.IsType<OperationCanceledException>(error);
        Assert.Same(cache.CancellationFailure, cancellation);
        Assert.Equal(caller.Token, cancellation.CancellationToken);
        Assert.Equal(1, effects);
        Assert.True(store.Claimed);
        Assert.Equal(0, store.Completions);
        Assert.Equal(0, store.Releases);
        Assert.Equal(request.CacheKeysToInvalidate, cache.RemovedKeys);
        await Assert.ThrowsAsync<IdempotencyInProgressException>(() =>
            idempotency.Handle(request, Handler, default));
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task Invalid_Keys_Before_Execution_Release_The_Outer_Atomic_Claim()
    {
        var cache = new Cache();
        var store = new ClaimStore();
        var idempotency = new IdempotencyBehavior<Command, string>(store);
        var invalidation = new CacheInvalidationBehavior<Command, string>(cache);
        var request = new Command(RawKeys: true);
        var effects = 0;
        var error = await Record.ExceptionAsync(() => idempotency.Handle(request,
            () => invalidation.Handle(request, () =>
            {
                effects++;
                return Task.FromResult("must not execute");
            }, default), default));

        var validation = Assert.IsType<InvalidOperationException>(error);
        Assert.False(BehaviorFailure.IsRetrySuppressed(validation));
        Assert.Equal(0, effects);
        Assert.Empty(cache.RemovedKeys);
        Assert.False(store.Claimed);
        Assert.Equal(0, store.Completions);
        Assert.Equal(1, store.Releases);
        Assert.Equal("later", await idempotency.Handle(new Command(), () => Task.FromResult("later"), default));
    }

    private sealed record Query : IRequest<string>, ICacheableQuery
    {
        public string CacheKey => "same";
        public TimeSpan? CacheDuration => null;
    }

    private sealed record Command(bool RawKeys = false, bool MultipleKeys = false) : IRequest<string>, ICacheInvalidatingRequest, IIdempotentRequest, IRetryableRequest
    {
        public string[] CacheKeysToInvalidate => RawKeys ? ["unscoped"] : MultipleKeys
            ? [CacheKeyComposer.Compose<Query>("same"), CacheKeyComposer.Compose<Query>("second"), CacheKeyComposer.Compose<Query>("trailing")]
            : [CacheKeyComposer.Compose<Query>("same")];
        public string IdempotencyKey => "same";
        public int MaxRetries => 2;
        public TimeSpan RetryDelay => TimeSpan.Zero;
    }

    private sealed class Cache(Exception? failure = null, bool synchronousFailure = false, int failureOnCall = 1, bool honorCancellation = false) : ICacheProvider
    {
        public List<string> RemovedKeys { get; } = [];
        public OperationCanceledException? CancellationFailure { get; private set; }

        public Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            RemovedKeys.Add(key);
            if (honorCancellation)
            {
                try { cancellationToken.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException exception)
                {
                    CancellationFailure = exception;
                    throw;
                }
            }
            if (failure is null || RemovedKeys.Count != failureOnCall) return Task.CompletedTask;
            if (synchronousFailure) throw failure;
            return Task.FromException(failure);
        }

        public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Invalidation must not read the cache.");

        public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Invalidation must not write the cache.");
    }

    private sealed class ClaimStore : IAtomicIdempotencyStore
    {
        public bool Claimed { get; private set; }
        public int Completions { get; private set; }
        public int Releases { get; private set; }

        public Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct)
        {
            var status = Claimed ? IdempotencyClaimStatus.InFlight : IdempotencyClaimStatus.Claimed;
            Claimed = true;
            return Task.FromResult(new IdempotencyClaimResult(status));
        }

        public Task CompleteAsync(string key, object? response, CancellationToken ct)
        {
            Completions++;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string key, CancellationToken ct)
        {
            Releases++;
            Claimed = false;
            return Task.CompletedTask;
        }

        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
            throw new NotSupportedException("The atomic path must claim the request.");

        public Task StoreAsync(string key, object response, CancellationToken ct) =>
            throw new NotSupportedException("The atomic path must complete its claim.");
    }
}

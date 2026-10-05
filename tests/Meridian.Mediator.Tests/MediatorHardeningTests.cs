using System.Diagnostics;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public sealed record ScannedOrderProbeRequest(string Value) : IRequest<string>;

public sealed class ScannedOrderProbeHandler : IRequestHandler<ScannedOrderProbeRequest, string>
{
    public Task<string> Handle(ScannedOrderProbeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult($"handled:{request.Value}");
}

public static class ScannedOrderProbeLog
{
    public static List<string> Events { get; } = [];
}

public sealed class ScannedClosedOrderProbeBehavior : IPipelineBehavior<ScannedOrderProbeRequest, string>
{
    public async Task<string> Handle(
        ScannedOrderProbeRequest request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        ScannedOrderProbeLog.Events.Add("closed");
        return await next().ConfigureAwait(false);
    }
}

public sealed class EarlyOpenOrderProbeBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ScannedOrderProbeLog.Events.Add("open-early");
        return await next().ConfigureAwait(false);
    }
}

public sealed class LateOpenOrderProbeBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ScannedOrderProbeLog.Events.Add("open-late");
        return await next().ConfigureAwait(false);
    }
}

[Collection("Pipeline")]
public class MediatorHardeningTests
{
    [Fact]
    public async Task AuthorizationBehaviorRejectsAuthorizedRequestWhenNoHandlerIsRegistered()
    {
        var behavior = new AuthorizationBehavior<AuthorizedCommand, string>([]);
        var handlerRan = false;

        var exception = await Assert.ThrowsAsync<MissingAuthorizationHandlerException>(() => behavior.Handle(
            new AuthorizedCommand("user"),
            () =>
            {
                handlerRan = true;
                return Task.FromResult("TOP SECRET DATA");
            },
            CancellationToken.None));

        Assert.False(handlerRan);
        Assert.IsAssignableFrom<UnauthorizedException>(exception);
        Assert.Contains(nameof(AuthorizedCommand), exception.Message);
        Assert.Contains("IAuthorizationHandler", exception.Message);
    }

    [Fact]
    public async Task AuthorizationBehaviorCompatibilityOptionAllowsMissingHandler()
    {
        var behavior = new AuthorizationBehavior<AuthorizedCommand, string>(
            [],
            new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = true });

        var result = await behavior.Handle(new AuthorizedCommand("user"), () => Task.FromResult("legacy result"), CancellationToken.None);

        Assert.Equal("legacy result", result);
    }

    [Fact]
    public async Task AuthorizationBehaviorUsesCollectionCountBeforeEnumeratingAnEmptyHandlerCollection()
    {
        var behavior = new AuthorizationBehavior<AuthorizedCommand, string>(new NonEnumerableEmptyAuthorizationHandlers());

        await Assert.ThrowsAsync<MissingAuthorizationHandlerException>(() => behavior.Handle(
            new AuthorizedCommand("user"),
            () => Task.FromResult("should-not-run"),
            CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizationBehaviorCompatibilityOptionIsResolvedFromDi()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = true });
        services.AddMeridianMediator(configuration => configuration.AddAuthorizationBehavior());
        services.AddTransient<IRequestHandler<AuthorizedCommand, string>, AuthorizedCommandHandler>();

        var result = await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new AuthorizedCommand("user"));

        Assert.Equal("Authorized:user", result);
    }

    [Fact]
    public async Task CachingBehaviorDoesNotShareValueBetweenRequestTypesWithSameUserKey()
    {
        var cache = new HardeningCacheProvider();
        var queryA = new CachingBehavior<CacheTypeA, string>(cache);
        var queryB = new CachingBehavior<CacheTypeB, string>(cache);

        var first = await queryA.Handle(new CacheTypeA("k"), () => Task.FromResult("A-data"), CancellationToken.None);
        var second = await queryB.Handle(new CacheTypeB("k"), () => Task.FromResult("B-data"), CancellationToken.None);

        Assert.Equal("A-data", first);
        Assert.Equal("B-data", second);
        Assert.All(cache.AccessedKeys, key => Assert.StartsWith("meridian:cache:v1:", key));
    }

    [Fact]
    public async Task CachingBehaviorCompatibilityOptionUsesVerbatimUserKey()
    {
        var cache = new HardeningCacheProvider();
        var behavior = new CachingBehavior<CacheTypeA, string>(
            cache,
            new CachingBehaviorOptions { UseRequestTypeNamespacedKeys = false });

        var executions = 0;
        var first = await behavior.Handle(
            new CacheTypeA("legacy-key"),
            () => Task.FromResult($"value-{++executions}"),
            CancellationToken.None);
        var second = await behavior.Handle(
            new CacheTypeA("legacy-key"),
            () => Task.FromResult($"value-{++executions}"),
            CancellationToken.None);

        Assert.Equal("value-1", first);
        Assert.Equal("value-1", second);
        Assert.Equal(1, executions);
        Assert.All(cache.AccessedKeys, key => Assert.Equal("legacy-key", key));
    }

    [Fact]
    public async Task IdempotencyBehaviorDoesNotShareResponseBetweenRequestTypesWithSameUserKey()
    {
        var store = new HardeningIdempotencyStore();
        var commandA = new IdempotencyBehavior<IdempotencyTypeA, string>(store);
        var commandB = new IdempotencyBehavior<IdempotencyTypeB, string>(store);

        var first = await commandA.Handle(new IdempotencyTypeA("k"), () => Task.FromResult("A-data"), CancellationToken.None);
        var second = await commandB.Handle(new IdempotencyTypeB("k"), () => Task.FromResult("B-data"), CancellationToken.None);

        Assert.Equal("A-data", first);
        Assert.Equal("B-data", second);
    }

    [Fact]
    public async Task IdempotencyBehaviorCompatibilityOptionUsesVerbatimUserKey()
    {
        var store = new HardeningIdempotencyStore();
        var behavior = new IdempotencyBehavior<IdempotencyTypeA, string>(
            store,
            new IdempotencyBehaviorOptions { UseRequestTypeNamespacedKeys = false });

        var executions = 0;
        var first = await behavior.Handle(
            new IdempotencyTypeA("legacy-key"),
            () => Task.FromResult($"value-{++executions}"),
            CancellationToken.None);
        var second = await behavior.Handle(
            new IdempotencyTypeA("legacy-key"),
            () => Task.FromResult($"value-{++executions}"),
            CancellationToken.None);

        Assert.Equal("value-1", first);
        Assert.Equal("value-1", second);
        Assert.Equal(1, executions);
        Assert.All(store.AccessedKeys, key => Assert.Equal("legacy-key", key));
    }

    [Fact]
    public async Task AtomicIdempotencyExecutesOnlyOneConcurrentRequestAndRejectsInFlightCallers()
    {
        var store = new AtomicHardeningIdempotencyStore();
        var behavior = new IdempotencyBehavior<IdempotencyTypeA, string>(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var first = behavior.Handle(new IdempotencyTypeA("same-key"), async () =>
        {
            Interlocked.Increment(ref executions);
            entered.SetResult();
            await release.Task;
            return "completed";
        }, default);
        await entered.Task;
        try
        {
            var contenders = Enumerable.Range(0, 19).Select(_ =>
                Assert.ThrowsAsync<IdempotencyInProgressException>(() => behavior.Handle(
                    new IdempotencyTypeA("same-key"), () =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult("unexpected");
                    }, default)));
            await Task.WhenAll(contenders);
            Assert.Equal(1, executions);
        }
        finally
        {
            release.SetResult();
        }
        Assert.Equal("completed", await first);
        Assert.Equal("completed", await behavior.Handle(new IdempotencyTypeA("same-key"),
            () => throw new InvalidOperationException("completed response must replay"), default));
    }

    [Fact]
    public async Task AtomicIdempotencyReturnsCompletedNullResponseWithoutReexecuting()
    {
        var store = new AtomicHardeningIdempotencyStore();
        var behavior = new IdempotencyBehavior<IdempotencyNullableType, string?>(store);
        var key = IdempotencyKeyComposer.Compose<IdempotencyNullableType>("different-key");
        await store.ClaimAsync(key, CancellationToken.None);
        await store.CompleteAsync(key, null, CancellationToken.None);
        var executed = false;

        var response = await behavior.Handle(
            new IdempotencyNullableType("different-key"),
            () =>
            {
                executed = true;
                return Task.FromResult<string?>("should-not-run");
            },
            CancellationToken.None);

        Assert.Null(response);
        Assert.False(executed);
    }

    [Fact]
    public void NamespacedKeysUseStableFullRequestNameRatherThanAssemblyQualifiedName()
    {
        var typeIdentity = typeof(CacheTypeA).FullName!;
        var cacheKey = CacheKeyComposer.Compose<CacheTypeA>("user-key");
        var idempotencyKey = IdempotencyKeyComposer.Compose<IdempotencyTypeA>("user-key");

        Assert.Contains($":{typeIdentity.Length}:{typeIdentity}:user-key", cacheKey);
        Assert.DoesNotContain("Version=", cacheKey);
        Assert.DoesNotContain("Version=", idempotencyKey);
    }

    [Fact]
    public async Task CacheInvalidationRemovesEntryWrittenByCachingBehavior()
    {
        var cache = new HardeningCacheProvider();
        var caching = new CachingBehavior<CacheTypeA, string>(cache);
        var invalidation = new CacheInvalidationBehavior<InvalidatesCacheTypeACommand, string>(cache);
        const string userKey = "user:42";

        await caching.Handle(new CacheTypeA(userKey), () => Task.FromResult("cached value"), CancellationToken.None);
        await invalidation.Handle(
            new InvalidatesCacheTypeACommand(userKey),
            () => Task.FromResult("updated"),
            CancellationToken.None);

        var entry = await cache.GetAsync(CacheKeyComposer.Compose<CacheTypeA>(userKey), CancellationToken.None);
        Assert.False(entry.Found);
    }

    [Fact]
    public async Task CacheInvalidationAcceptsComposedKeysWhenNamespacingIsEnabled()
    {
        var cache = new HardeningCacheProvider();
        var behavior = new CacheInvalidationBehavior<InvalidatesCacheTypeACommand, string>(cache);
        var composedKey = CacheKeyComposer.Compose<CacheTypeA>("composed-key");
        await cache.SetAsync(composedKey, "cached value", null, CancellationToken.None);

        await behavior.Handle(
            new InvalidatesCacheTypeACommand("composed-key"),
            () => Task.FromResult("updated"),
            CancellationToken.None);

        var entry = await cache.GetAsync(composedKey, CancellationToken.None);
        Assert.False(entry.Found);
    }

    [Fact]
    public async Task CacheInvalidationRejectsRawKeysWhenNamespacingIsEnabled()
    {
        var cache = new HardeningCacheProvider();
        var behavior = new CacheInvalidationBehavior<InvalidatesCacheTypeAWithRawKeyCommand, string>(cache);
        var handlerRan = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(
            new InvalidatesCacheTypeAWithRawKeyCommand("legacy:cache-key"),
            () =>
            {
                handlerRan = true;
                return Task.FromResult("updated");
            },
            CancellationToken.None));

        Assert.False(handlerRan);
        Assert.Contains("legacy:cache-key", exception.Message);
        Assert.Contains(nameof(InvalidatesCacheTypeAWithRawKeyCommand), exception.Message);
        Assert.Contains("CacheKeyComposer.Compose<TQuery>(key)", exception.Message);
        Assert.Contains("UseRequestTypeNamespacedKeys = false", exception.Message);
    }

    [Fact]
    public async Task CacheInvalidationCompatibilityOptionAllowsRawKeys()
    {
        var cache = new HardeningCacheProvider();
        var caching = new CachingBehavior<CacheTypeA, string>(
            cache,
            new CachingBehaviorOptions { UseRequestTypeNamespacedKeys = false });
        var invalidation = new CacheInvalidationBehavior<InvalidatesCacheTypeAWithRawKeyCommand, string>(
            cache,
            new CachingBehaviorOptions { UseRequestTypeNamespacedKeys = false });
        const string rawKey = "legacy:cache-key";

        await caching.Handle(new CacheTypeA(rawKey), () => Task.FromResult("cached value"), CancellationToken.None);
        await invalidation.Handle(
            new InvalidatesCacheTypeAWithRawKeyCommand(rawKey),
            () => Task.FromResult("updated"),
            CancellationToken.None);

        var entry = await cache.GetAsync(rawKey, CancellationToken.None);
        Assert.False(entry.Found);
    }

    [Fact]
    public async Task ScanAndExplicitRegistrationDoNotRunSameClosedBehaviorTwice()
    {
        ClosedApiBehavior.InvocationCount = 0;
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration => configuration
            .RegisterServicesFromAssemblyContaining<ClosedApiBehavior>()
            .AddBehavior<ClosedApiBehavior>());
        services.AddTransient<IRequestHandler<ClosedApiRequest, string>, ClosedApiRequestHandler>();

        var result = await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new ClosedApiRequest("once"));

        Assert.Equal("handled:once", result);
        Assert.Equal(1, ClosedApiBehavior.InvocationCount);
    }

    [Fact]
    public async Task AddMeridianMediatorTwiceDoesNotDuplicateOpenBehavior()
    {
        AlphaBehavior<OrderTestRequest, string>.Log.Clear();
        var services = new ServiceCollection();
        Action<MeridianMediatorConfiguration> configure = configuration =>
            configuration.AddOpenBehavior(typeof(AlphaBehavior<,>));

        services.AddMeridianMediator(configure);
        services.AddMeridianMediator(configure);
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();

        await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new OrderTestRequest("once"));

        Assert.Equal(["Alpha-Before", "Alpha-After"], AlphaBehavior<OrderTestRequest, string>.Log);
    }

    [Fact]
    public async Task ClosedAndOpenBehaviorsAreOrderedTogether()
    {
        AlphaBehavior<OrderTestRequest, string>.Log.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration =>
        {
            configuration.AddClosedBehavior(
                typeof(IPipelineBehavior<OrderTestRequest, string>),
                typeof(ClosedOrderBehavior),
                order: 100);
            configuration.AddOpenBehavior(typeof(AlphaBehavior<,>), order: -100);
        });
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();

        await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new OrderTestRequest("order"));

        Assert.Equal(
            ["Alpha-Before", "Closed-Before", "Closed-After", "Alpha-After"],
            AlphaBehavior<OrderTestRequest, string>.Log);
    }

    [Fact]
    public async Task SameOpenBehaviorAtTwoDifferentOrdersRunsTwice()
    {
        AlphaBehavior<OrderTestRequest, string>.Log.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration =>
        {
            configuration.AddOpenBehavior(typeof(AlphaBehavior<,>), order: -10);
            configuration.AddOpenBehavior(typeof(AlphaBehavior<,>), order: 10);
        });
        services.AddTransient<IRequestHandler<OrderTestRequest, string>, OrderTestHandler>();

        await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new OrderTestRequest("twice"));

        Assert.Equal(2, AlphaBehavior<OrderTestRequest, string>.Log.Count(entry => entry == "Alpha-Before"));
        Assert.Equal(2, AlphaBehavior<OrderTestRequest, string>.Log.Count(entry => entry == "Alpha-After"));
    }

    [Fact]
    public async Task AssemblyScannedClosedBehaviorIsOrderedBetweenEarlierAndLaterOpenBehaviors()
    {
        ScannedOrderProbeLog.Events.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration => configuration
            .RegisterServicesFromAssemblyContaining<ScannedClosedOrderProbeBehavior>()
            .AddOpenBehavior(typeof(EarlyOpenOrderProbeBehavior<,>), order: -100)
            .AddOpenBehavior(typeof(LateOpenOrderProbeBehavior<,>), order: 100));

        await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new ScannedOrderProbeRequest("a"));

        Assert.Equal(["open-early", "closed", "open-late"], ScannedOrderProbeLog.Events);
    }

    [Fact]
    public async Task AssemblyScannedClosedBehaviorIsOrderedAfterAnEarlierOpenBehavior()
    {
        ScannedOrderProbeLog.Events.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration => configuration
            .RegisterServicesFromAssemblyContaining<ScannedClosedOrderProbeBehavior>()
            .AddOpenBehavior(typeof(EarlyOpenOrderProbeBehavior<,>), order: -100));

        await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new ScannedOrderProbeRequest("b"));

        Assert.Equal(["open-early", "closed"], ScannedOrderProbeLog.Events);
    }

    [Fact]
    public async Task ExplicitOrderOverridesTheDefaultOrderOfTheSameAssemblyScannedClosedBehavior()
    {
        ScannedOrderProbeLog.Events.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(configuration => configuration
            .RegisterServicesFromAssemblyContaining<ScannedClosedOrderProbeBehavior>()
            .AddBehavior<ScannedClosedOrderProbeBehavior>(order: 100)
            .AddOpenBehavior(typeof(EarlyOpenOrderProbeBehavior<,>), order: -100));

        await services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new ScannedOrderProbeRequest("c"));

        Assert.Equal(["open-early", "closed"], ScannedOrderProbeLog.Events);
    }

    [Fact]
    public async Task StackTraceOptionDoesNotLeakExceptionMessageWhenMessageRecordingIsDisabled()
    {
        const string secret = "credential=super-secret-value";
        using var collector = new HardeningActivityCollector();
        var services = new ServiceCollection();
        services.AddSingleton(new MediatorTelemetryOptions
        {
            RecordExceptionMessage = false,
            RecordExceptionStackTrace = true
        });
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IRequestHandler<SecretFailingRequest, string>, SecretFailingRequestHandler>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.BuildServiceProvider().GetRequiredService<IMediator>().Send(new SecretFailingRequest()));

        var activity = collector.SingleStopped("Mediator.Send SecretFailingRequest");
        Assert.Contains("SecretFailingRequestHandler", activity.GetTagItem("exception.stacktrace") as string);
        Assert.DoesNotContain(secret, activity.StatusDescription ?? string.Empty);
        Assert.DoesNotContain(secret, activity.Tags.Select(tag => tag.Value?.ToString() ?? string.Empty));
    }

    private sealed record CacheTypeA(string Key) : IRequest<string>, ICacheableQuery
    {
        public string CacheKey => Key;
        public TimeSpan? CacheDuration => null;
    }

    private sealed record CacheTypeB(string Key) : IRequest<string>, ICacheableQuery
    {
        public string CacheKey => Key;
        public TimeSpan? CacheDuration => null;
    }

    private sealed record IdempotencyTypeA(string Key) : IRequest<string>, IIdempotentRequest
    {
        public string IdempotencyKey => Key;
    }

    private sealed record IdempotencyTypeB(string Key) : IRequest<string>, IIdempotentRequest
    {
        public string IdempotencyKey => Key;
    }

    private sealed record IdempotencyNullableType(string Key) : IRequest<string?>, IIdempotentRequest
    {
        public string IdempotencyKey => Key;
    }

    private sealed record InvalidatesCacheTypeACommand(string UserKey) : IRequest<string>, ICacheInvalidatingRequest
    {
        public string[] CacheKeysToInvalidate => [CacheKeyComposer.Compose<CacheTypeA>(UserKey)];
    }

    private sealed record InvalidatesCacheTypeAWithRawKeyCommand(string UserKey) : IRequest<string>, ICacheInvalidatingRequest
    {
        public string[] CacheKeysToInvalidate => [UserKey];
    }

    private sealed class NonEnumerableEmptyAuthorizationHandlers : ICollection<IAuthorizationHandler<AuthorizedCommand>>
    {
        public int Count => 0;
        public bool IsReadOnly => true;

        public void Add(IAuthorizationHandler<AuthorizedCommand> item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(IAuthorizationHandler<AuthorizedCommand> item) => false;
        public void CopyTo(IAuthorizationHandler<AuthorizedCommand>[] array, int arrayIndex) { }
        public bool Remove(IAuthorizationHandler<AuthorizedCommand> item) => throw new NotSupportedException();
        public IEnumerator<IAuthorizationHandler<AuthorizedCommand>> GetEnumerator() =>
            throw new InvalidOperationException("The empty collection should not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class HardeningCacheProvider : ICacheProvider
    {
        private readonly Dictionary<string, object> _values = [];
        public List<string> AccessedKeys { get; } = [];

        public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
        {
            AccessedKeys.Add(key);
            return Task.FromResult(_values.TryGetValue(key, out var value) ? (true, (object?)value) : (false, (object?)null));
        }

        public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
        {
            AccessedKeys.Add(key);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class HardeningIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, object> _values = [];
        public List<string> AccessedKeys { get; } = [];

        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct)
        {
            AccessedKeys.Add(key);
            return Task.FromResult(_values.TryGetValue(key, out var value) ? (true, (object?)value) : (false, (object?)null));
        }

        public Task StoreAsync(string key, object response, CancellationToken ct)
        {
            AccessedKeys.Add(key);
            _values[key] = response;
            return Task.CompletedTask;
        }
    }

    private sealed class AtomicHardeningIdempotencyStore : IAtomicIdempotencyStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, (bool IsCompleted, object? Response)> _entries = [];

        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct)
        {
            lock (_sync)
            {
                return Task.FromResult(_entries.TryGetValue(key, out var entry) && entry.IsCompleted
                    ? (true, entry.Response)
                    : (false, (object?)null));
            }
        }

        public Task StoreAsync(string key, object response, CancellationToken ct) => CompleteAsync(key, response, ct);

        public Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    _entries[key] = (false, null);
                    return Task.FromResult(new IdempotencyClaimResult(IdempotencyClaimStatus.Claimed));
                }

                return Task.FromResult(entry.IsCompleted
                    ? new IdempotencyClaimResult(IdempotencyClaimStatus.Completed, entry.Response)
                    : new IdempotencyClaimResult(IdempotencyClaimStatus.InFlight));
            }
        }

        public Task CompleteAsync(string key, object? response, CancellationToken ct)
        {
            lock (_sync)
            {
                _entries[key] = (true, response);
            }

            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string key, CancellationToken ct)
        {
            lock (_sync)
            {
                _entries.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ClosedOrderBehavior : IPipelineBehavior<OrderTestRequest, string>
    {
        public async Task<string> Handle(OrderTestRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            AlphaBehavior<OrderTestRequest, string>.Log.Add("Closed-Before");
            var response = await next();
            AlphaBehavior<OrderTestRequest, string>.Log.Add("Closed-After");
            return response;
        }
    }

    private sealed record SecretFailingRequest : IRequest<string>;

    private sealed class SecretFailingRequestHandler : IRequestHandler<SecretFailingRequest, string>
    {
        public Task<string> Handle(SecretFailingRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("credential=super-secret-value");
    }

    private sealed class HardeningActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _stopped = [];
        private readonly object _sync = new();

        public HardeningActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "Meridian.Mediator",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_sync)
                    {
                        _stopped.Add(activity);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();

        public Activity SingleStopped(string operationName)
        {
            lock (_sync)
            {
                return Assert.Single(_stopped, activity => activity.OperationName == operationName);
            }
        }
    }
}

using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Interface for requests whose results should be cached.
/// Implement this on query-type requests to enable automatic result caching.
/// </summary>
public interface ICacheableQuery
{
    /// <summary>
    /// Gets the cache key used to store and retrieve the cached result.
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// Gets the optional cache duration. If null, the cache provider's default duration is used.
    /// </summary>
    TimeSpan? CacheDuration { get; }
}

/// <summary>
/// Abstract cache provider. Users implement for their cache (Memory, Redis, etc.).
/// </summary>
public interface ICacheProvider
{
    /// <summary>
    /// Attempts to retrieve a cached value by key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating whether the value was found and the cached value.</returns>
    Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a value in the cache with an optional duration.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="duration">Optional cache duration. If null, the provider's default is used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a cached value by key.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Controls provider-key composition for <see cref="CachingBehavior{TRequest, TResponse}"/>.
/// </summary>
public sealed record CachingBehaviorOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether cache provider keys include the request type identity.
    /// Defaults to <c>true</c>. The namespaced format is
    /// <c>meridian:cache:v1:{typeIdentityLength}:{fullRequestTypeName}:{userKey}</c>.
    /// Set to <c>false</c> only to retain verbatim cache keys from versions up to and including 2.1.3
    /// while a shared cache is migrated.
    /// </summary>
    public bool UseRequestTypeNamespacedKeys { get; init; } = true;

    /// <summary>Gets or sets whether misses use an optional concurrent provider. Defaults to false.</summary>
    public bool EnableRequestCoalescing { get; init; }
}

/// <summary>
/// Marker interface for requests that invalidate cache entries.
/// Implement this on command-type requests that modify data which is cached by queries.
/// </summary>
public interface ICacheInvalidatingRequest
{
    /// <summary>
    /// Gets the provider keys that should be invalidated after successful execution.
    /// When <see cref="CachingBehaviorOptions.UseRequestTypeNamespacedKeys"/> is enabled (the default),
    /// use <see cref="CacheKeyComposer.Compose{TQuery}(string)"/> with the cached query type to
    /// compose each key. Set the caching option to <c>false</c> only when this property must retain
    /// legacy verbatim cache keys.
    /// </summary>
    string[] CacheKeysToInvalidate { get; }
}

/// <summary>
/// Composes cache-provider keys for cached query types.
/// Use this from <see cref="ICacheInvalidatingRequest"/> implementations to invalidate entries
/// written by <see cref="CachingBehavior{TRequest, TResponse}"/> with its default options.
/// </summary>
public static class CacheKeyComposer
{
    internal const string NamespacedKeyPrefix = "meridian:cache:v1:";

    /// <summary>
    /// Composes the namespaced provider key for <typeparamref name="TQuery"/> and a consumer cache key.
    /// </summary>
    /// <typeparam name="TQuery">The query type that implements <see cref="ICacheableQuery"/>.</typeparam>
    /// <param name="userKey">The cache key supplied by the query.</param>
    /// <returns>The effective cache-provider key.</returns>
    public static string Compose<TQuery>(string userKey)
        where TQuery : ICacheableQuery => Compose(typeof(TQuery), userKey);

    /// <summary>
    /// Composes the namespaced provider key for a query type and a consumer cache key.
    /// </summary>
    /// <param name="queryType">The query type that implements <see cref="ICacheableQuery"/>.</param>
    /// <param name="userKey">The cache key supplied by the query.</param>
    /// <returns>The effective cache-provider key.</returns>
    public static string Compose(Type queryType, string userKey)
    {
        ArgumentNullException.ThrowIfNull(queryType);
        ArgumentNullException.ThrowIfNull(userKey);
        return RequestKeyComposer.Compose("cache", queryType, userKey);
    }
}

/// <summary>
/// Pipeline behavior that caches query results for <see cref="ICacheableQuery"/> requests.
/// On cache hit, the cached result is returned without executing the handler.
/// On cache miss, the handler is executed and the result is cached.
/// </summary>
/// <typeparam name="TRequest">Request type (must implement <see cref="ICacheableQuery"/>).</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheableQuery
{
    private readonly ICacheProvider _cacheProvider;
    private readonly CachingBehaviorOptions _options;
    private readonly MediatorMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="cacheProvider">The cache provider.</param>
    public CachingBehavior(ICacheProvider cacheProvider)
        : this(cacheProvider, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingBehavior{TRequest, TResponse}"/> class
    /// with provider-key composition options.
    /// </summary>
    /// <param name="cacheProvider">The cache provider.</param>
    /// <param name="options">Caching options. A null value uses namespaced provider keys.</param>
    public CachingBehavior(ICacheProvider cacheProvider, CachingBehaviorOptions? options)
        : this(cacheProvider, options, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingBehavior{TRequest, TResponse}"/> class that reports
    /// its initial typed lookup decision to a provider-owned metrics component.
    /// </summary>
    /// <param name="cacheProvider">The cache provider.</param>
    /// <param name="options">Caching options. A null value uses namespaced provider keys.</param>
    /// <param name="metrics">The metrics component. A null value leaves metrics disabled.</param>
    public CachingBehavior(ICacheProvider cacheProvider, CachingBehaviorOptions? options = null, MediatorMetrics? metrics = null)
    {
        _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
        _options = options ?? new CachingBehaviorOptions();
        _metrics = metrics ?? MediatorMetrics.Disabled;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var key = _options.UseRequestTypeNamespacedKeys
            ? CacheKeyComposer.Compose<TRequest>(request.CacheKey)
            : request.CacheKey;
        bool found;
        object? cachedValue;
        try
        {
            (found, cachedValue) = await _cacheProvider.GetAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordLookup(
                ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? CacheLookupState.Cancellation
                    : CacheLookupState.Error);
            throw;
        }

        // The initial typed lookup is the only event: a missing or incompatible stored value is a miss, and a
        // coalesced factory result is deliberately not reinterpreted as a hit or a follower.
        if (found && cachedValue is TResponse typedValue)
        {
            RecordLookup(CacheLookupState.Hit);
            return typedValue;
        }

        RecordLookup(CacheLookupState.Miss);

        if (!found && _options.EnableRequestCoalescing && _cacheProvider is IConcurrentCacheProvider concurrentProvider)
        {
            return await HandleCoalescedAsync(request, next, key, concurrentProvider, cancellationToken).ConfigureAwait(false);
        }

        var response = await next();

        if (response is not null)
        {
            await _cacheProvider.SetAsync(key, response, request.CacheDuration, cancellationToken);
        }

        return response;
    }

    // The gate is checked before typeof(TRequest) so a disabled or unobserved lookup does no work.
    private void RecordLookup(CacheLookupState state)
    {
        if (_metrics.CacheLookupEnabled)
        {
            _metrics.RecordCacheLookup(typeof(TRequest), state);
        }
    }

    private async Task<TResponse> HandleCoalescedAsync(TRequest request, RequestHandlerDelegate<TResponse> next,
        string key, IConcurrentCacheProvider concurrentProvider, CancellationToken cancellationToken)
    {
        var factoryCompleted = false;
        TResponse ownResponse = default!;
        var shared = await concurrentProvider.GetOrCreateAsync(key, async _ =>
        {
            ownResponse = await next().ConfigureAwait(false);
            factoryCompleted = true;
            return ownResponse;
        }, request.CacheDuration, cancellationToken).ConfigureAwait(false);

        if (shared is TResponse compatible)
        {
            return compatible;
        }
        if (factoryCompleted)
        {
            // Our successful factory supplies provenance even when its response is null.
            return ownResponse;
        }

        // Untyped shared null or an incompatible response is a typed miss for this caller.
        // Do not re-enter coalescing: a second-read hit may stay incompatible indefinitely.
        var response = await next().ConfigureAwait(false);
        if (response is not null)
        {
            await _cacheProvider.SetAsync(key, response, request.CacheDuration, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }
}

internal static class RequestKeyComposer
{
    internal static string Compose(string purpose, Type requestType, string userKey)
    {
        // Request types live in consumer assemblies, whose versions commonly change each deployment.
        // Including the assembly-qualified name would fragment shared cache/idempotency keys by version.
        var typeIdentity = requestType.FullName ?? requestType.Name;
        return $"meridian:{purpose}:v1:{typeIdentity.Length}:{typeIdentity}:{userKey}";
    }
}

/// <summary>
/// Pipeline behavior that invalidates cache entries for <see cref="ICacheInvalidatingRequest"/> requests.
/// After the handler executes successfully, all specified cache keys are removed.
/// A subsequent removal failure is propagated without replaying the successful handler through Meridian retries.
/// With namespaced provider keys enabled, raw invalidation keys are rejected before the handler executes
/// so a cache-key migration cannot silently leave stale entries behind.
/// </summary>
/// <typeparam name="TRequest">Request type (must implement <see cref="ICacheInvalidatingRequest"/>).</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class CacheInvalidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheInvalidatingRequest
{
    private readonly ICacheProvider _cacheProvider;
    private readonly CachingBehaviorOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheInvalidationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="cacheProvider">The cache provider.</param>
    public CacheInvalidationBehavior(ICacheProvider cacheProvider)
        : this(cacheProvider, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheInvalidationBehavior{TRequest, TResponse}"/> class
    /// with provider-key composition options.
    /// </summary>
    /// <param name="cacheProvider">The cache provider.</param>
    /// <param name="options">Caching options. A null value uses namespaced provider keys.</param>
    public CacheInvalidationBehavior(ICacheProvider cacheProvider, CachingBehaviorOptions? options)
    {
        _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
        _options = options ?? new CachingBehaviorOptions();
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var keysToInvalidate = request.CacheKeysToInvalidate;
        if (keysToInvalidate is { Length: > 0 })
        {
            ValidateInvalidationKeys(keysToInvalidate);
        }

        var response = await next();

        if (keysToInvalidate is { Length: > 0 })
        {
            try
            {
                foreach (var key in keysToInvalidate)
                {
                    await _cacheProvider.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                BehaviorFailure.SuppressRetry(exception);
                throw;
            }
        }

        return response;
    }

    private void ValidateInvalidationKeys(IEnumerable<string> keys)
    {
        if (!_options.UseRequestTypeNamespacedKeys)
        {
            return;
        }

        foreach (var key in keys)
        {
            if (key.StartsWith(CacheKeyComposer.NamespacedKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var requestTypeName = typeof(TRequest).FullName ?? typeof(TRequest).Name;
            throw new InvalidOperationException(
                $"Cache invalidation key '{key}' on request '{requestTypeName}' is not a composed Meridian cache key. " +
                "Use CacheKeyComposer.Compose<TQuery>(key) with the cached query type, or set " +
                "CachingBehaviorOptions.UseRequestTypeNamespacedKeys = false to retain legacy raw cache keys.");
        }
    }
}

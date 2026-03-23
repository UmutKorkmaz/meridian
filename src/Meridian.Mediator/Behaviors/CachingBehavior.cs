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
/// Marker interface for requests that invalidate cache entries.
/// Implement this on command-type requests that modify data which is cached by queries.
/// </summary>
public interface ICacheInvalidatingRequest
{
    /// <summary>
    /// Gets the cache keys that should be invalidated after successful execution.
    /// </summary>
    string[] CacheKeysToInvalidate { get; }
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="cacheProvider">The cache provider.</param>
    public CachingBehavior(ICacheProvider cacheProvider)
    {
        _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var (found, cachedValue) = await _cacheProvider.GetAsync(request.CacheKey, cancellationToken);
        if (found && cachedValue is TResponse typedValue)
        {
            return typedValue;
        }

        var response = await next();

        if (response is not null)
        {
            await _cacheProvider.SetAsync(request.CacheKey, response, request.CacheDuration, cancellationToken);
        }

        return response;
    }
}

/// <summary>
/// Pipeline behavior that invalidates cache entries for <see cref="ICacheInvalidatingRequest"/> requests.
/// After the handler executes successfully, all specified cache keys are removed.
/// </summary>
/// <typeparam name="TRequest">Request type (must implement <see cref="ICacheInvalidatingRequest"/>).</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class CacheInvalidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheInvalidatingRequest
{
    private readonly ICacheProvider _cacheProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheInvalidationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="cacheProvider">The cache provider.</param>
    public CacheInvalidationBehavior(ICacheProvider cacheProvider)
    {
        _cacheProvider = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next();

        var keysToInvalidate = request.CacheKeysToInvalidate;
        if (keysToInvalidate is { Length: > 0 })
        {
            foreach (var key in keysToInvalidate)
            {
                await _cacheProvider.RemoveAsync(key, cancellationToken);
            }
        }

        return response;
    }
}

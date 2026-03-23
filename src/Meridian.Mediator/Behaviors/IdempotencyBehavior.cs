using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Interface for requests that should be executed at most once.
/// If a request with the same idempotency key has already been processed,
/// the cached response is returned instead of re-executing the handler.
/// </summary>
public interface IIdempotentRequest
{
    /// <summary>
    /// Gets the idempotency key that uniquely identifies this request instance.
    /// </summary>
    string IdempotencyKey { get; }
}

/// <summary>
/// Store for tracking idempotent request executions and their responses.
/// Users implement this for their storage backend (in-memory, Redis, database, etc.).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Checks whether a request with the given idempotency key has already been processed.
    /// </summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple indicating whether the key exists and the cached response, if any.</returns>
    Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct);

    /// <summary>
    /// Stores the response for a processed idempotent request.
    /// </summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="response">The response to cache.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreAsync(string key, object response, CancellationToken ct);
}

/// <summary>
/// Pipeline behavior that ensures <see cref="IIdempotentRequest"/> requests are executed at most once.
/// If a cached response exists for the idempotency key, it is returned directly.
/// Otherwise, the handler is executed and the response is stored for future deduplication.
/// </summary>
/// <typeparam name="TRequest">Request type (must implement <see cref="IIdempotentRequest"/>).</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class IdempotencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IIdempotentRequest
{
    private readonly IIdempotencyStore _idempotencyStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="idempotencyStore">The idempotency store.</param>
    public IdempotencyBehavior(IIdempotencyStore idempotencyStore)
    {
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var (exists, cachedResponse) = await _idempotencyStore.CheckAsync(request.IdempotencyKey, cancellationToken);
        if (exists && cachedResponse is TResponse typedResponse)
        {
            return typedResponse;
        }

        var response = await next();

        if (response is not null)
        {
            await _idempotencyStore.StoreAsync(request.IdempotencyKey, response, cancellationToken);
        }

        return response;
    }
}

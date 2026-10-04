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
        if (_idempotencyStore is ILeasedIdempotencyStore leasedStore)
        {
            return await HandleLeased(leasedStore, key, next, cancellationToken).ConfigureAwait(false);
        }

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
    private static async Task<TResponse> HandleLeased(
        ILeasedIdempotencyStore store, string key, RequestHandlerDelegate<TResponse> next, CancellationToken requestToken)
    {
        try
        {
            var claim = await store.ClaimAsync(key, typeof(TResponse), requestToken).ConfigureAwait(false);
            if (claim is null || (claim.Status != LeasedIdempotencyStatus.Claimed && claim.Lease is not null)
                || (claim.Status != LeasedIdempotencyStatus.Completed && claim.CachedResponse is not null))
                throw new InvalidOperationException("Invalid leased idempotency claim shape.");
            switch (claim.Status)
            {
                case LeasedIdempotencyStatus.Completed: return GetCompletedResponse(claim.CachedResponse);
                case LeasedIdempotencyStatus.InFlight: throw new IdempotencyInProgressException(typeof(TRequest));
                case LeasedIdempotencyStatus.OutcomeUnknown: throw new IdempotencyOutcomeUnknownException();
                case LeasedIdempotencyStatus.Claimed: break;
                default: throw new InvalidOperationException("Unsupported leased idempotency status.");
            }
            var lease = claim.Lease;
            if (lease is null || string.IsNullOrWhiteSpace(lease.Key) || string.IsNullOrWhiteSpace(lease.OwnerToken)
                || string.IsNullOrWhiteSpace(lease.ResponseContractId))
                throw new InvalidOperationException("Invalid idempotency lease.");
            var intervalMs = Math.Floor((lease.ExpiresAt - TimeProvider.System.GetUtcNow()).TotalMilliseconds / 3);
            if (intervalMs < 1 || intervalMs > uint.MaxValue - 1)
                throw new IdempotencyOutcomeUnknownException();
            var interval = TimeSpan.FromMilliseconds(intervalMs);
            using var stop = new CancellationTokenSource();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestToken, stop.Token);
            var renewal = RenewUntilStopped(store, lease, interval, lifetime.Token);
            TResponse response = default!;
            var failures = new List<Exception>();
            try { response = await next().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            using (var cleanup = BehaviorFailure.CreateCleanupBudget())
            {
                // Cancellation is cooperative: never abandon a provider that still owns scoped work.
                try { stop.Cancel(); }
                catch (Exception error) { failures.Add(error); }
                var budgetExpired = Task.Delay(Timeout.InfiniteTimeSpan, cleanup.Token);
                var first = await Task.WhenAny(renewal, budgetExpired).ConfigureAwait(false);
                // A noncooperative provider is still joined to protect scoped lifetime.
                var renewalFailure = await renewal.ConfigureAwait(false);
                if (renewalFailure is not null) failures.Add(renewalFailure);
                if (first == budgetExpired)
                    failures.Add(new TimeoutException("Stopping idempotency renewal exceeded the cooperative cleanup budget."));
                cleanup.Cancel();
            }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Idempotent execution and lease maintenance failed.", failures);
            using var completion = BehaviorFailure.CreateCleanupBudget();
            await store.CompleteAsync(lease, response, completion.Token).ConfigureAwait(false);
            return response;
        }
        catch (Exception failure)
        {
            // Claim ambiguity, retained busy states and any post-handler fault must not busy-loop or rerun work.
            BehaviorFailure.SuppressRetry(failure);
            throw;
        }
    }

    private static async Task<Exception?> RenewUntilStopped(
        ILeasedIdempotencyStore store, IdempotencyLease lease, TimeSpan interval, CancellationToken lifetime)
    {
        while (true)
        {
            try { await Task.Delay(interval, lifetime).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
            using var deadline = new CancellationTokenSource(interval);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(lifetime, deadline.Token);
            try
            {
                var renewed = await store.RenewAsync(lease, call.Token).ConfigureAwait(false);
                if (!renewed) return new IdempotencyOutcomeUnknownException();
                if (deadline.IsCancellationRequested) return new TimeoutException("Idempotency renewal exceeded its deadline.");
            }
            catch (OperationCanceledException error)
            {
                if (deadline.IsCancellationRequested) return error;
                if (lifetime.IsCancellationRequested && error.CancellationToken == call.Token) return null;
                return error;
            }
            catch (Exception error) { return error; }
        }
    }

}

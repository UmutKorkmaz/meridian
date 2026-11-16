using Meridian.Mediator.Diagnostics;
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
/// Controls provider-key composition for <see cref="IdempotencyBehavior{TRequest, TResponse}"/>.
/// </summary>
public sealed record IdempotencyBehaviorOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether idempotency store keys include the request type identity.
    /// Defaults to <c>true</c>. The namespaced format is
    /// <c>meridian:idempotency:v1:{typeIdentityLength}:{fullRequestTypeName}:{userKey}</c>.
    /// Set to <c>false</c> only to retain verbatim idempotency keys from versions up to and including
    /// 2.1.3 while an existing store is migrated.
    /// </summary>
    public bool UseRequestTypeNamespacedKeys { get; init; } = true;
}

/// <summary>
/// Composes idempotency-store keys for request types.
/// </summary>
public static class IdempotencyKeyComposer
{
    /// <summary>
    /// Composes the namespaced provider key for <typeparamref name="TRequest"/> and a consumer idempotency key.
    /// </summary>
    /// <typeparam name="TRequest">The request type that implements <see cref="IIdempotentRequest"/>.</typeparam>
    /// <param name="userKey">The idempotency key supplied by the request.</param>
    /// <returns>The effective idempotency-store key.</returns>
    public static string Compose<TRequest>(string userKey)
        where TRequest : IIdempotentRequest
    {
        ArgumentNullException.ThrowIfNull(userKey);
        return RequestKeyComposer.Compose("idempotency", typeof(TRequest), userKey);
    }
}

/// <summary>
/// The state returned by an atomic idempotency claim attempt.
/// </summary>
public enum IdempotencyClaimStatus
{
    /// <summary>The caller owns the new claim and must execute the request.</summary>
    Claimed,

    /// <summary>The request was already completed and its response is available.</summary>
    Completed,

    /// <summary>Another caller currently owns the claim and is executing the request.</summary>
    InFlight
}

/// <summary>
/// Result of an atomic idempotency claim attempt. A completed result may contain a null response.
/// </summary>
/// <param name="Status">The claim state.</param>
/// <param name="CachedResponse">The completed response when the status is <see cref="IdempotencyClaimStatus.Completed"/>.</param>
public sealed record IdempotencyClaimResult(IdempotencyClaimStatus Status, object? CachedResponse = null);

/// <summary>
/// Optional idempotency-store contract that atomically claims a key before request execution.
/// Implement this interface in addition to <see cref="IIdempotencyStore"/> to prevent concurrent
/// callers from executing the same idempotent request.
/// </summary>
public interface IAtomicIdempotencyStore : IIdempotencyStore
{
    /// <summary>
    /// Atomically claims a key, reports a completed response, or reports that another caller is in flight.
    /// </summary>
    /// <param name="key">The effective idempotency provider key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the atomic claim attempt.</returns>
    Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct);

    /// <summary>
    /// Marks an owned claim as completed, including when <paramref name="response"/> is null.
    /// </summary>
    /// <param name="key">The effective idempotency provider key.</param>
    /// <param name="response">The completed response, which may be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CompleteAsync(string key, object? response, CancellationToken ct);

    /// <summary>
    /// Releases an owned claim when request execution fails, allowing a later retry to claim it.
    /// </summary>
    /// <param name="key">The effective idempotency provider key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseAsync(string key, CancellationToken ct);
}

/// <summary>
/// Exception thrown when an idempotent request is already executing in another caller.
/// The request handler is not executed for this outcome.
/// </summary>
public sealed class IdempotencyInProgressException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyInProgressException"/> class.
    /// </summary>
    /// <param name="requestType">The type of request that is currently executing.</param>
    public IdempotencyInProgressException(Type requestType)
        : base($"An idempotent request of type '{requestType.FullName}' is already in progress.")
    {
    }
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
    private readonly IdempotencyBehaviorOptions _options;
    private readonly MediatorMetrics _metrics;
    internal TimeProvider LeaseTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="idempotencyStore">The idempotency store.</param>
    public IdempotencyBehavior(IIdempotencyStore idempotencyStore)
        : this(idempotencyStore, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyBehavior{TRequest, TResponse}"/> class
    /// with provider-key composition options.
    /// </summary>
    /// <param name="idempotencyStore">The idempotency store.</param>
    /// <param name="options">Idempotency options. A null value uses namespaced provider keys.</param>
    public IdempotencyBehavior(IIdempotencyStore idempotencyStore, IdempotencyBehaviorOptions? options)
        : this(idempotencyStore, options, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyBehavior{TRequest, TResponse}"/> class that reports
    /// confirmed idempotency decisions to a provider-owned metrics component.
    /// </summary>
    /// <param name="idempotencyStore">The idempotency store.</param>
    /// <param name="options">Idempotency options. A null value uses namespaced provider keys.</param>
    /// <param name="metrics">The metrics component. A null value leaves metrics disabled.</param>
    public IdempotencyBehavior(
        IIdempotencyStore idempotencyStore, IdempotencyBehaviorOptions? options = null, MediatorMetrics? metrics = null)
    {
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _options = options ?? new IdempotencyBehaviorOptions();
        _metrics = metrics ?? MediatorMetrics.Disabled;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var key = _options.UseRequestTypeNamespacedKeys
            ? IdempotencyKeyComposer.Compose<TRequest>(request.IdempotencyKey)
            : request.IdempotencyKey;

        if (_idempotencyStore is ILeasedIdempotencyStore leasedStore)
        {
            return await HandleLeased(leasedStore, key, next, cancellationToken).ConfigureAwait(false);
        }

        if (_idempotencyStore is IAtomicIdempotencyStore atomicStore)
        {
            return await HandleAtomically(atomicStore, key, next, cancellationToken).ConfigureAwait(false);
        }

        bool exists;
        object? cachedResponse;
        try
        {
            (exists, cachedResponse) = await _idempotencyStore.CheckAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordBeforeHandlerFailure(IdempotencyMode.Legacy, ex, cancellationToken);
            throw;
        }

        if (exists)
        {
            TResponse replay;
            try
            {
                replay = GetCompletedResponse(cachedResponse);
            }
            catch (Exception)
            {
                Record(IdempotencyMode.Legacy, IdempotencyState.Error);
                throw;
            }

            Record(IdempotencyMode.Legacy, IdempotencyState.Replayed);
            return replay;
        }

        Record(IdempotencyMode.Legacy, IdempotencyState.Miss);
        var response = await next();

        if (response is not null)
        {
            try
            {
                await _idempotencyStore.StoreAsync(key, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BehaviorFailure.SuppressRetry(ex);
                Record(IdempotencyMode.Legacy, IdempotencyState.Uncertain);
                throw;
            }

            // A null response is never stored, so it is never reported as completed.
            Record(IdempotencyMode.Legacy, IdempotencyState.Completed);
        }

        return response;
    }

    private void Record(IdempotencyMode mode, IdempotencyState state)
    {
        if (_metrics.IdempotencyEnabled)
        {
            _metrics.RecordIdempotency(typeof(TRequest), mode, state);
        }
    }

    private void RecordBeforeHandlerFailure(IdempotencyMode mode, Exception failure, CancellationToken cancellationToken) =>
        Record(
            mode,
            failure is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? IdempotencyState.Cancellation
                : IdempotencyState.Error);

    // Marks a confirmed lease loss (a renewal that returned false) without guessing from exception types.
    private sealed class LeaseProbe
    {
        internal volatile bool Lost;
    }

    private async Task<TResponse> HandleLeased(
        ILeasedIdempotencyStore store, string key, RequestHandlerDelegate<TResponse> next, CancellationToken requestToken)
    {
        // Metrics phase: before an owned claim every failure is a provider/contract error; after it the store may
        // still hold pending ownership, so any failure leaves the outcome uncertain. Never changes control flow.
        var owned = false;
        var reported = false;
        try
        {
            var claim = await store.ClaimAsync(key, typeof(TResponse), requestToken).ConfigureAwait(false);
            if (claim is null || (claim.Status != LeasedIdempotencyStatus.Claimed && claim.Lease is not null)
                || (claim.Status != LeasedIdempotencyStatus.Completed && claim.CachedResponse is not null))
                throw new InvalidOperationException("Invalid leased idempotency claim shape.");
            switch (claim.Status)
            {
                case LeasedIdempotencyStatus.Completed:
                {
                    var replay = GetCompletedResponse(claim.CachedResponse);
                    Record(IdempotencyMode.Leased, IdempotencyState.Replayed);
                    return replay;
                }
                case LeasedIdempotencyStatus.InFlight:
                    reported = true;
                    Record(IdempotencyMode.Leased, IdempotencyState.InFlight);
                    throw new IdempotencyInProgressException(typeof(TRequest));
                case LeasedIdempotencyStatus.OutcomeUnknown:
                    reported = true;
                    Record(IdempotencyMode.Leased, IdempotencyState.Uncertain);
                    throw new IdempotencyOutcomeUnknownException();
                case LeasedIdempotencyStatus.Claimed: break;
                default: throw new InvalidOperationException("Unsupported leased idempotency status.");
            }
            var lease = claim.Lease;
            if (lease is null || string.IsNullOrWhiteSpace(lease.Key) || string.IsNullOrWhiteSpace(lease.OwnerToken)
                || string.IsNullOrWhiteSpace(lease.ResponseContractId))
                throw new InvalidOperationException("Invalid idempotency lease.");
            owned = true;
            Record(IdempotencyMode.Leased, IdempotencyState.Claimed);
            var intervalMs = Math.Floor((lease.ExpiresAt - LeaseTimeProvider.GetUtcNow()).TotalMilliseconds / 3);
            if (intervalMs < 1 || intervalMs > uint.MaxValue - 1)
                throw new IdempotencyOutcomeUnknownException();
            var interval = TimeSpan.FromMilliseconds(intervalMs);
            using var stop = new CancellationTokenSource();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestToken, stop.Token);
            var probe = _metrics.IdempotencyEnabled ? new LeaseProbe() : null;
            var renewal = RenewUntilStopped(store, lease, interval, probe, lifetime.Token);
            TResponse response = default!;
            var failures = new List<Exception>();
            try { response = await next().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), LeaseTimeProvider))
            {
                // Cancellation is cooperative: never abandon a provider that still owns scoped work.
                try { stop.Cancel(); }
                catch (Exception error) { failures.Add(error); }
                var budgetExpired = Task.Delay(Timeout.InfiniteTimeSpan, LeaseTimeProvider, cleanup.Token);
                var first = await Task.WhenAny(renewal, budgetExpired).ConfigureAwait(false);
                // A noncooperative provider is still joined to protect scoped lifetime.
                var renewalFailure = await renewal.ConfigureAwait(false);
                if (renewalFailure is not null) failures.Add(renewalFailure);
                if (probe is { Lost: true }) Record(IdempotencyMode.Leased, IdempotencyState.LeaseLost);
                if (first == budgetExpired)
                    failures.Add(new TimeoutException("Stopping idempotency renewal exceeded the cooperative cleanup budget."));
                cleanup.Cancel();
            }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Idempotent execution and lease maintenance failed.", failures);
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5), LeaseTimeProvider);
            await store.CompleteAsync(lease, response, completion.Token).ConfigureAwait(false);
            Record(IdempotencyMode.Leased, IdempotencyState.Completed);
            return response;
        }
        catch (Exception failure)
        {
            // Claim ambiguity, retained busy states and any post-handler fault must not busy-loop or rerun work.
            BehaviorFailure.SuppressRetry(failure);
            if (owned)
            {
                Record(IdempotencyMode.Leased, IdempotencyState.Uncertain);
            }
            else if (!reported)
            {
                RecordBeforeHandlerFailure(IdempotencyMode.Leased, failure, requestToken);
            }

            throw;
        }
    }

    private async Task<Exception?> RenewUntilStopped(
        ILeasedIdempotencyStore store, IdempotencyLease lease, TimeSpan interval, LeaseProbe? probe, CancellationToken lifetime)
    {
        while (true)
        {
            try { await Task.Delay(interval, LeaseTimeProvider, lifetime).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
            using var deadline = new CancellationTokenSource(interval, LeaseTimeProvider);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(lifetime, deadline.Token);
            try
            {
                var renewed = await store.RenewAsync(lease, call.Token).ConfigureAwait(false);
                if (!renewed)
                {
                    if (probe is not null) probe.Lost = true;
                    return new IdempotencyOutcomeUnknownException();
                }
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

    private async Task<TResponse> HandleAtomically(
        IAtomicIdempotencyStore atomicStore,
        string key,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        IdempotencyClaimResult claim;
        try
        {
            claim = await atomicStore.ClaimAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordBeforeHandlerFailure(IdempotencyMode.Atomic, ex, cancellationToken);
            throw;
        }

        if (claim.Status == IdempotencyClaimStatus.Completed)
        {
            TResponse replay;
            try
            {
                replay = GetCompletedResponse(claim.CachedResponse);
            }
            catch (Exception)
            {
                Record(IdempotencyMode.Atomic, IdempotencyState.Error);
                throw;
            }

            Record(IdempotencyMode.Atomic, IdempotencyState.Replayed);
            return replay;
        }

        if (claim.Status == IdempotencyClaimStatus.InFlight)
        {
            Record(IdempotencyMode.Atomic, IdempotencyState.InFlight);
            throw new IdempotencyInProgressException(typeof(TRequest));
        }

        if (claim.Status != IdempotencyClaimStatus.Claimed)
        {
            Record(IdempotencyMode.Atomic, IdempotencyState.Error);
            throw new InvalidOperationException($"Unsupported {nameof(IdempotencyClaimStatus)} value '{claim.Status}'.");
        }

        Record(IdempotencyMode.Atomic, IdempotencyState.Claimed);
        TResponse response;
        try
        {
            response = await next().ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            // Inner commit/completion failure leaves business work uncertain.
            if (BehaviorFailure.IsRetrySuppressed(primary))
            {
                // Ownership is retained, so nothing is released and the outcome stays uncertain.
                Record(IdempotencyMode.Atomic, IdempotencyState.Uncertain);
                throw;
            }

            using var cleanup = BehaviorFailure.CreateCleanupBudget();
            try
            {
                await atomicStore.ReleaseAsync(key, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception releaseFailure)
            {
                var failure = new AggregateException("Request handler and claim release failed.", primary, releaseFailure);
                BehaviorFailure.SuppressRetry(failure);
                Record(IdempotencyMode.Atomic, IdempotencyState.Uncertain);
                throw failure;
            }

            Record(IdempotencyMode.Atomic, IdempotencyState.Released);
            throw;
        }

        try
        {
            await atomicStore.CompleteAsync(key, response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BehaviorFailure.SuppressRetry(ex);
            Record(IdempotencyMode.Atomic, IdempotencyState.Uncertain);
            throw;
        }

        Record(IdempotencyMode.Atomic, IdempotencyState.Completed);
        return response;
    }

    private static TResponse GetCompletedResponse(object? cachedResponse)
    {
        if (cachedResponse is TResponse typedResponse)
        {
            return typedResponse;
        }

        if (cachedResponse is null && default(TResponse) is null)
        {
            return default!;
        }

        throw new InvalidOperationException(
            $"The idempotency store contains a response incompatible with '{typeof(TResponse).FullName}'.");
    }
}

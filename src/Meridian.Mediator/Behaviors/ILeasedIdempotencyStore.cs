namespace Meridian.Mediator.Behaviors;

/// <summary>Optional owned lease contract. Expired pending work remains uncertain until application reconciliation.</summary>
/// <remarks>Operations must honor cooperative cancellation. Renewal cannot cancel an already running handler.</remarks>
public interface ILeasedIdempotencyStore : IIdempotencyStore
{
    /// <summary>Claims a logical key using the declared response type, or reports retained state.</summary>
    Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct);
    /// <summary>Conditionally records the live owner's result, including known-contract null.</summary>
    Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct);
    /// <summary>Explicitly releases live ownership only when the caller has established no effects.</summary>
    Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct);
    /// <summary>Extends live ownership by its original duration; false indicates lost ownership.</summary>
    Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct);
}

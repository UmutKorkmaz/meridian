namespace Meridian.Mediator.Behaviors;

/// <summary>Retained idempotency claim status.</summary>
public enum LeasedIdempotencyStatus
{
    /// <summary>New ownership authorizes one handler invocation.</summary>
    Claimed,
    /// <summary>Another live owner is pending.</summary>
    InFlight,
    /// <summary>A completed response is available, including null.</summary>
    Completed,
    /// <summary>Expired pending work requires application reconciliation.</summary>
    OutcomeUnknown
}

/// <summary>Claim result. Only Claimed carries a lease; only Completed carries a cached response.</summary>
public sealed record LeasedIdempotencyClaim(LeasedIdempotencyStatus Status, object? CachedResponse = null, IdempotencyLease? Lease = null);

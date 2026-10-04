namespace Meridian.Mediator.Behaviors;

/// <summary>Immutable ownership and conservative local acquisition snapshot. Renewal does not update ExpiresAt.</summary>
/// <param name="Key">Effective backend key; never decorate again for owner operations.</param>
/// <param name="OwnerToken">Opaque ownership token.</param>
/// <param name="ResponseContractId">Stable declared response contract.</param>
/// <param name="ExpiresAt">Conservative local acquisition expiry used once to schedule renewal; the backend remains authoritative.</param>
public sealed record IdempotencyLease(string Key, string OwnerToken, string ResponseContractId, DateTimeOffset ExpiresAt);

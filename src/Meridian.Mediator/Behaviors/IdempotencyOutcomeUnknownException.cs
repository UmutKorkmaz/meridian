namespace Meridian.Mediator.Behaviors;

/// <summary>The retained operation has an uncertain outcome and requires application reconciliation before retry.</summary>
public sealed class IdempotencyOutcomeUnknownException : InvalidOperationException
{
    /// <summary>Creates a redacted uncertainty error.</summary>
    public IdempotencyOutcomeUnknownException() : base("The idempotent operation outcome is unknown; application reconciliation is required.") { }
}

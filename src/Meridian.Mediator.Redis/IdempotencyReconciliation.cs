namespace Meridian.Mediator.Redis;

/// <summary>Application decision after the expired owner's execution and issued commands are quiescent.</summary>
public enum IdempotencyReconciliation
{
    /// <summary>Record the externally established result under the retained declared contract.</summary>
    Completed,
    /// <summary>Delete retained pending protection after proving that retry is safe.</summary>
    SafeToRetry
}

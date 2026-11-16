namespace Meridian.Mediator.EntityFrameworkCore;

/// <summary>Explicit opt-ins for transaction composition.</summary>
public sealed class EfTransactionOptions
{
    /// <summary>Encloses the lifecycle in EF's strategy. Only safe pre-handler acquisition can retry.</summary>
    public bool UseExecutionStrategy { get; set; }
    /// <summary>Allows joining without a savepoint; failure then requires caller-owned rollback.</summary>
    public bool JoinWithoutSavepoint { get; set; }
}

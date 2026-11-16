namespace Meridian.Mediator.Behaviors;

/// <summary>Optional provider that owns transaction execution and its complete retry policy.</summary>
public interface ITransactionExecutionProvider
{
    /// <summary>Executes an action within the provider's transaction lifecycle.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="action">Action receiving the original request cancellation token.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <returns>The action result after successful transaction cleanup.</returns>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}

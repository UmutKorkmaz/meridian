namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Defines an exception action for a request. Executes when an exception occurs but cannot provide a response.
/// Useful for logging, metrics, and other side effects.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TException">Exception type.</typeparam>
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : notnull
    where TException : Exception
{
    /// <summary>
    /// Called when an exception of type <typeparamref name="TException"/> is thrown during request processing.
    /// </summary>
    /// <param name="request">The failed request.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}

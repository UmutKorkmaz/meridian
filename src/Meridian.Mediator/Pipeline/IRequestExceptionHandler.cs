namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Defines an exception handler for a request. Can provide a response when an exception occurs.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
/// <typeparam name="TException">Exception type.</typeparam>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : notnull
    where TException : Exception
{
    /// <summary>
    /// Called when an exception is thrown during request processing.
    /// The handler can set the state to handled and provide a response.
    /// </summary>
    /// <param name="request">The failed request.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="state">The exception handler state to mark as handled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Handle(TRequest request, TException exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);
}

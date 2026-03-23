namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Defines a post-processor for a request. Runs after the request handler.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Process method executed after calling the handler for the request.
    /// </summary>
    /// <param name="request">Incoming request.</param>
    /// <param name="response">Response from the handler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}

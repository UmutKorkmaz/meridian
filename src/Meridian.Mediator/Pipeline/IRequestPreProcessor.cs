namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Defines a pre-processor for a request. Runs before the request handler.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : notnull
{
    /// <summary>
    /// Process method executed before calling the handler for the request.
    /// </summary>
    /// <param name="request">Incoming request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Process(TRequest request, CancellationToken cancellationToken);
}

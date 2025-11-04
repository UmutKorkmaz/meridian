namespace Meridian.Mediator;

/// <summary>
/// Send a request through the mediator pipeline to be handled by a single handler.
/// </summary>
public interface ISender
{
    /// <summary>
    /// Asynchronously send a request to a single handler.
    /// </summary>
    /// <typeparam name="TResponse">Response type.</typeparam>
    /// <param name="request">Request object.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that represents the send operation. The task result contains the handler response.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously send a request to a single handler with no response.
    /// </summary>
    /// <typeparam name="TRequest">Request type.</typeparam>
    /// <param name="request">Request object.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that represents the send operation.</returns>
    Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest;

    /// <summary>
    /// Asynchronously send an object request to a single handler via runtime type dispatch.
    /// </summary>
    /// <param name="request">Request object.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that represents the send operation. The task result contains the handler response.</returns>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);
}

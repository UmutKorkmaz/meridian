namespace Meridian.Mediator.Streaming;

/// <summary>
/// Defines a handler for a stream request that returns an <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
/// <typeparam name="TRequest">The type of stream request being handled.</typeparam>
/// <typeparam name="TResponse">The type of each item in the response stream.</typeparam>
public interface IStreamRequestHandler<in TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Handles a stream request.
    /// </summary>
    /// <param name="request">The stream request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of response items.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

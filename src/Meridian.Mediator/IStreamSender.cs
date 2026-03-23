using Meridian.Mediator.Streaming;

namespace Meridian.Mediator;

/// <summary>
/// Creates streams of responses from stream request handlers.
/// </summary>
public interface IStreamSender
{
    /// <summary>
    /// Creates a stream of responses for a stream request.
    /// </summary>
    /// <typeparam name="TResponse">The type of each item in the response stream.</typeparam>
    /// <param name="request">The stream request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of response items.</returns>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a stream of responses for a stream request via runtime type dispatch.
    /// </summary>
    /// <param name="request">The stream request object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of response objects.</returns>
    IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default);
}

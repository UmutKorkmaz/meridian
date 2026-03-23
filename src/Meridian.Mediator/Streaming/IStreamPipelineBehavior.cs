namespace Meridian.Mediator.Streaming;

/// <summary>
/// Stream pipeline behavior to surround the inner stream handler.
/// Implementations add additional behavior and iterate the next delegate.
/// </summary>
/// <typeparam name="TRequest">Stream request type.</typeparam>
/// <typeparam name="TResponse">The type of each item in the response stream.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Stream pipeline handler. Perform any additional behavior and yield from the <paramref name="next"/> delegate as necessary.
    /// </summary>
    /// <param name="request">Incoming stream request.</param>
    /// <param name="next">Awaitable delegate for the next action in the stream pipeline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of response items.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

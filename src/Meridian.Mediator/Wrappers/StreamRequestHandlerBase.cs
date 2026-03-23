namespace Meridian.Mediator.Wrappers;

/// <summary>
/// Abstract base class for stream request handler wrappers.
/// </summary>
public abstract class StreamRequestHandlerBase
{
    /// <summary>
    /// Handles a stream request by resolving the appropriate handler from the service provider.
    /// </summary>
    /// <param name="request">The stream request object.</param>
    /// <param name="serviceProvider">The service provider for resolving handlers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of response objects.</returns>
    public abstract IAsyncEnumerable<object?> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

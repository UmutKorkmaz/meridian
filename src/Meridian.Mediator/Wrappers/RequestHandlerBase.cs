namespace Meridian.Mediator.Wrappers;

/// <summary>
/// Abstract base class for request handler wrappers.
/// Wrappers handle type erasure for the mediator's cached handler lookups.
/// </summary>
public abstract class RequestHandlerBase
{
    /// <summary>
    /// Handles a request by resolving the appropriate handler and pipeline behaviors from the service provider.
    /// </summary>
    /// <param name="request">The request object.</param>
    /// <param name="serviceProvider">The service provider for resolving handlers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handler response as an object.</returns>
    public abstract Task<object?> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

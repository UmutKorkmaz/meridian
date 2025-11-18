namespace Meridian.Mediator.Wrappers;

/// <summary>
/// Abstract wrapper base class for typed request handlers.
/// </summary>
/// <typeparam name="TResponse">Response type.</typeparam>
public abstract class RequestHandlerWrapper<TResponse> : RequestHandlerBase
{
    /// <summary>
    /// Handles a request with strongly-typed response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The typed response.</returns>
    public abstract Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

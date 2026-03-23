using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Wrappers;

internal sealed class OpenRequestHandlerAdapter<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly OpenRequestHandlerRegistry _registry;
    private readonly IServiceProvider _serviceProvider;

    public OpenRequestHandlerAdapter(OpenRequestHandlerRegistry registry, IServiceProvider serviceProvider)
    {
        _registry = registry;
        _serviceProvider = serviceProvider;
    }

    public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken)
    {
        if (!_registry.TryResolveHandlerType(typeof(TRequest), typeof(TResponse), out var handlerType))
        {
            throw new InvalidOperationException(
                $"No open generic request handler registered for request '{typeof(TRequest)}' and response '{typeof(TResponse)}'.");
        }

        var handler = ActivatorUtilities.CreateInstance(_serviceProvider, handlerType);
        if (handler is not IRequestHandler<TRequest, TResponse> typedHandler)
        {
            throw new InvalidOperationException(
                $"Resolved type '{handlerType}' for request '{typeof(TRequest)}' does not implement " +
                $"'{typeof(IRequestHandler<,>).MakeGenericType(typeof(TRequest), typeof(TResponse))}'.");
        }

        return typedHandler.Handle(request, cancellationToken);
    }
}

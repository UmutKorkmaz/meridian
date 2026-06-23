using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Wrappers;

/// <summary>
/// Concrete implementation of <see cref="RequestHandlerWrapper{TResponse}"/> for a specific request type.
/// Resolves the handler and pipeline behaviors from the service provider and builds the pipeline chain.
/// Skips pipeline construction when no behaviors are registered (fast path).
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc/>
    public override async Task<object?> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        return await Handle((IRequest<TResponse>)request, serviceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

        return HandleWithPipeline((TRequest)request, serviceProvider, cancellationToken,
            () => handler.Handle((TRequest)request, cancellationToken));
    }

    protected static Task<TResponse> HandleWithPipeline(
        TRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken,
        RequestHandlerDelegate<TResponse> handler)
    {
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

        // Fast path: skip pipeline construction when no behaviors are registered.
        // ICollection<T>.Count is O(1) for List/Array (the common DI return types).
        if (behaviors is ICollection<IPipelineBehavior<TRequest, TResponse>> { Count: 0 })
        {
            return handler();
        }

        RequestHandlerDelegate<TResponse> current = handler;

        if (behaviors is IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> behaviorList)
        {
            for (int i = behaviorList.Count - 1; i >= 0; i--)
            {
                var behavior = behaviorList[i];
                var next = current;
                current = () => behavior.Handle((TRequest)request, next, cancellationToken);
            }
        }
        else
        {
            var behaviorArray = behaviors.ToArray();

            for (int i = behaviorArray.Length - 1; i >= 0; i--)
            {
                var behavior = behaviorArray[i];
                var next = current;
                current = () => behavior.Handle((TRequest)request, next, cancellationToken);
            }
        }

        return current();
    }
}

/// <summary>
/// Concrete implementation of <see cref="RequestHandlerWrapper{TResponse}"/> for requests that do not return a value.
/// Supports both <see cref="IRequestHandler{TRequest}"/> and <see cref="IRequestHandler{TRequest,TResponse}"/> registrations.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
public sealed class UnitRequestHandlerWrapperImpl<TRequest> : RequestHandlerWrapperImpl<TRequest, Unit>
    where TRequest : IRequest
{
    /// <inheritdoc/>
    public override Task<Unit> Handle(IRequest<Unit> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetService<IRequestHandler<TRequest>>();

        if (handler is not null)
        {
            return HandleWithPipeline(typedRequest, serviceProvider, cancellationToken, async () =>
            {
                await handler.Handle(typedRequest, cancellationToken).ConfigureAwait(false);
                return Unit.Value;
            });
        }

        return base.Handle(request, serviceProvider, cancellationToken);
    }
}

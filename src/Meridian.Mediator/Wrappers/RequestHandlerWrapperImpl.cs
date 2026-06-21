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

        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

        // Fast path: skip pipeline construction when no behaviors are registered.
        // ICollection<T>.Count is O(1) for List/Array (the common DI return types).
        if (behaviors is ICollection<IPipelineBehavior<TRequest, TResponse>> { Count: 0 })
        {
            return handler.Handle((TRequest)request, cancellationToken);
        }

        Task<TResponse> Handler() => handler.Handle((TRequest)request, cancellationToken);
        RequestHandlerDelegate<TResponse> next = Handler;

        // ⚡ Bolt: Eliminate LINQ .Reverse().Aggregate() for zero-allocation pipeline construction.
        // M.E.DI typically returns IList<T> or T[] for GetServices. We index backwards directly.
        if (behaviors is IList<IPipelineBehavior<TRequest, TResponse>> list)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var behavior = list[i];
                var currentNext = next;
                next = () => behavior.Handle((TRequest)request, currentNext, cancellationToken);
            }
        }
        else
        {
            // Fallback for non-indexable enumerables
            var fallbackList = new List<IPipelineBehavior<TRequest, TResponse>>();
            foreach (var behavior in behaviors)
            {
                fallbackList.Add(behavior);
            }
            for (var i = fallbackList.Count - 1; i >= 0; i--)
            {
                var behavior = fallbackList[i];
                var currentNext = next;
                next = () => behavior.Handle((TRequest)request, currentNext, cancellationToken);
            }
        }

        return next();
    }
}

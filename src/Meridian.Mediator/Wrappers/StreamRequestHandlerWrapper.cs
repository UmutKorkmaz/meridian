using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace Meridian.Mediator.Wrappers;

/// <summary>
/// Abstract wrapper for typed stream request handlers.
/// </summary>
/// <typeparam name="TResponse">The type of each item in the response stream.</typeparam>
public abstract class StreamRequestHandlerWrapper<TResponse> : StreamRequestHandlerBase
{
    /// <summary>
    /// Handles a stream request with strongly-typed response items.
    /// </summary>
    /// <param name="request">The stream request.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of typed response items.</returns>
    public abstract IAsyncEnumerable<TResponse> Handle(IStreamRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

/// <summary>
/// Concrete implementation of <see cref="StreamRequestHandlerWrapper{TResponse}"/> for a specific stream request type.
/// </summary>
/// <typeparam name="TRequest">Stream request type.</typeparam>
/// <typeparam name="TResponse">The type of each item in the response stream.</typeparam>
public class StreamRequestHandlerWrapperImpl<TRequest, TResponse> : StreamRequestHandlerWrapper<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <inheritdoc/>
    public override async IAsyncEnumerable<object?> Handle(
        object request, IServiceProvider serviceProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in Handle((IStreamRequest<TResponse>)request, serviceProvider, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <inheritdoc/>
    public override IAsyncEnumerable<TResponse> Handle(
        IStreamRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetRequiredService<IStreamRequestHandler<TRequest, TResponse>>();

        IAsyncEnumerable<TResponse> Handler() => handler.Handle((TRequest)request, cancellationToken);

        var behaviors = serviceProvider.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>();
        StreamHandlerDelegate<TResponse> next = Handler;

        // Optimization: Microsoft.Extensions.DependencyInjection returns arrays for multiple services.
        // We can type-check and use a backward for-loop to build the pipeline delegate chain
        // with zero enumerator or LINQ allocations in the hot path.
        if (behaviors is IList<IStreamPipelineBehavior<TRequest, TResponse>> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var behavior = list[i];
                var currentNext = next;
                next = () => behavior.Handle((TRequest)request, currentNext, cancellationToken);
            }
        }
        else
        {
            // Fallback for custom DI containers returning unmaterialized IEnumerables
            next = behaviors.Reverse().Aggregate(next, (current, behavior) => () => behavior.Handle((TRequest)request, current, cancellationToken));
        }

        return next();
    }
}

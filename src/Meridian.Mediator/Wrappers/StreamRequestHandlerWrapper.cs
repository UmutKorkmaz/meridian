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
        StreamHandlerDelegate<TResponse> next = Handler;

        var behaviors = serviceProvider.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>();

        // ⚡ Bolt: Eliminate LINQ .Reverse().Aggregate() for zero-allocation stream pipeline construction.
        // M.E.DI typically returns IList<T> or T[] for GetServices. We index backwards directly.
        if (behaviors is IList<IStreamPipelineBehavior<TRequest, TResponse>> list)
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
            var fallbackList = new List<IStreamPipelineBehavior<TRequest, TResponse>>();
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

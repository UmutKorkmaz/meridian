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

        // Optimization: Microsoft.Extensions.DependencyInjection returns an array for GetServices<T>().
        // Arrays implement IList<T>, allowing us to avoid LINQ allocations (.Reverse() and .Aggregate())
        // and build the pipeline chain faster with a simple reverse for-loop.
        if (behaviors is IList<IStreamPipelineBehavior<TRequest, TResponse>> list)
        {
            StreamHandlerDelegate<TResponse> next = Handler;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var behavior = list[i];
                var prevNext = next;
                next = () => behavior.Handle((TRequest)request, prevNext, cancellationToken);
            }
            return next();
        }

        return behaviors
            .Reverse()
            .Aggregate(
                (StreamHandlerDelegate<TResponse>)Handler,
                (next, behavior) => () => behavior.Handle((TRequest)request, next, cancellationToken))();
    }
}

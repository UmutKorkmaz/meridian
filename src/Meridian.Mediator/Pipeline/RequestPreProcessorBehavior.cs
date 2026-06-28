namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Built-in pipeline behavior that executes all registered <see cref="IRequestPreProcessor{TRequest}"/>
/// instances before calling the next behavior in the pipeline.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class RequestPreProcessorBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IRequestPreProcessor<TRequest>> _preProcessors;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestPreProcessorBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="preProcessors">The registered pre-processors.</param>
    public RequestPreProcessorBehavior(IEnumerable<IRequestPreProcessor<TRequest>> preProcessors)
    {
        _preProcessors = preProcessors;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // ⚡ Bolt: Fast path for zero pre-processors (array covariance check is fastest)
        if (_preProcessors is object[] { Length: 0 } || _preProcessors is ICollection<IRequestPreProcessor<TRequest>> { Count: 0 })
        {
            return await next().ConfigureAwait(false);
        }

        // ⚡ Bolt: Zero-allocation enumeration via IList/IReadOnlyList for-loop
        if (_preProcessors is IReadOnlyList<IRequestPreProcessor<TRequest>> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                await list[i].Process(request, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (_preProcessors is IList<IRequestPreProcessor<TRequest>> ilist)
        {
            for (int i = 0; i < ilist.Count; i++)
            {
                await ilist[i].Process(request, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            foreach (var processor in _preProcessors)
            {
                await processor.Process(request, cancellationToken).ConfigureAwait(false);
            }
        }

        return await next().ConfigureAwait(false);
    }
}

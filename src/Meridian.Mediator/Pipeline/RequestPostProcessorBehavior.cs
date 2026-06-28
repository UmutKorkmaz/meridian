namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Built-in pipeline behavior that executes all registered <see cref="IRequestPostProcessor{TRequest, TResponse}"/>
/// instances after calling the next behavior in the pipeline.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class RequestPostProcessorBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IRequestPostProcessor<TRequest, TResponse>> _postProcessors;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestPostProcessorBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="postProcessors">The registered post-processors.</param>
    public RequestPostProcessorBehavior(IEnumerable<IRequestPostProcessor<TRequest, TResponse>> postProcessors)
    {
        _postProcessors = postProcessors;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next().ConfigureAwait(false);

        // ⚡ Bolt: Fast path for zero post-processors (array covariance check is fastest)
        if (_postProcessors is object[] { Length: 0 } || _postProcessors is ICollection<IRequestPostProcessor<TRequest, TResponse>> { Count: 0 })
        {
            return response;
        }

        // ⚡ Bolt: Zero-allocation enumeration via IList/IReadOnlyList for-loop
        if (_postProcessors is IReadOnlyList<IRequestPostProcessor<TRequest, TResponse>> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                await list[i].Process(request, response, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (_postProcessors is IList<IRequestPostProcessor<TRequest, TResponse>> ilist)
        {
            for (int i = 0; i < ilist.Count; i++)
            {
                await ilist[i].Process(request, response, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            foreach (var processor in _postProcessors)
            {
                await processor.Process(request, response, cancellationToken).ConfigureAwait(false);
            }
        }

        return response;
    }
}

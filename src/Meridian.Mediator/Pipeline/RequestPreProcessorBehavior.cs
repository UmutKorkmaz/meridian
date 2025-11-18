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
        foreach (var processor in _preProcessors)
        {
            await processor.Process(request, cancellationToken).ConfigureAwait(false);
        }

        return await next().ConfigureAwait(false);
    }
}

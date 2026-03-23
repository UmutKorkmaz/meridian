using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// AsyncLocal-based correlation ID context for distributed tracing.
/// Provides ambient access to the current correlation ID across async flows.
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> _correlationId = new();

    /// <summary>
    /// Gets or sets the current correlation ID for the async flow.
    /// </summary>
    public static string? CorrelationId
    {
        get => _correlationId.Value;
        set => _correlationId.Value = value;
    }

    /// <summary>
    /// Ensures a correlation ID exists. If one is not set, a new one is generated.
    /// </summary>
    /// <returns>The current or newly generated correlation ID.</returns>
    public static string EnsureCorrelationId()
    {
        if (string.IsNullOrEmpty(_correlationId.Value))
            _correlationId.Value = Guid.NewGuid().ToString("N");
        return _correlationId.Value!;
    }
}

/// <summary>
/// Pipeline behavior that ensures a correlation ID exists for every request.
/// If no correlation ID is set in the current async context, a new one is generated.
/// This enables distributed tracing across mediator request chains.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class CorrelationIdBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var existingId = CorrelationContext.CorrelationId;
        if (string.IsNullOrEmpty(existingId))
            CorrelationContext.CorrelationId = Guid.NewGuid().ToString("N");

        return await next();
    }
}

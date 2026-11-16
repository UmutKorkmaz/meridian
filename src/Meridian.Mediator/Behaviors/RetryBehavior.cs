using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Interface for requests that should be retried on transient failures.
/// Provides configurable retry policies with defaults.
/// </summary>
public interface IRetryableRequest
{
    /// <summary>
    /// Gets the maximum number of retry attempts. Defaults to 3.
    /// </summary>
    int MaxRetries => 3;

    /// <summary>
    /// Gets the base delay between retry attempts. Used with exponential backoff. Defaults to 200ms.
    /// </summary>
    TimeSpan RetryDelay => TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Determines whether the request should be retried for the given exception. Defaults to true for all exceptions.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <returns>True if the request should be retried; otherwise, false.</returns>
    bool ShouldRetry(Exception exception) => true;
}

/// <summary>
/// Pipeline behavior that retries requests on transient failures with exponential backoff.
/// The retry policy is configured by the <see cref="IRetryableRequest"/> interface on the request.
/// </summary>
/// <typeparam name="TRequest">Request type (must implement <see cref="IRetryableRequest"/>).</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class RetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRetryableRequest
{
    private readonly MediatorMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryBehavior{TRequest, TResponse}"/> class with metrics disabled.
    /// </summary>
    public RetryBehavior()
        : this(null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryBehavior{TRequest, TResponse}"/> class that reports
    /// actual additional attempts to a provider-owned metrics component.
    /// </summary>
    /// <param name="metrics">The metrics component. A null value leaves metrics disabled.</param>
    [ActivatorUtilitiesConstructor]
    public RetryBehavior(MediatorMetrics? metrics)
    {
        _metrics = metrics ?? MediatorMetrics.Disabled;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var maxRetries = Math.Min(Math.Max(0, request.MaxRetries), Math.Max(0, RetryPolicy.MaxRetriesCap));
        var attempt = 0;

        while (true)
        {
            try
            {
                return await next().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && ShouldRetry(request, ex))
            {
                attempt++;
                var delay = RetryPolicy.CalculateDelay(request.RetryDelay, attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                // Counted immediately before the additional invocation actually runs: a retry whose delay was
                // canceled never reaches this point.
                if (_metrics.RetryEnabled)
                {
                    _metrics.RecordRetryAttempt(typeof(TRequest));
                }
            }
        }
    }

    private static bool ShouldRetry(TRequest request, Exception exception)
    {
        return RetryPolicy.TransientOnly(exception) && request.ShouldRetry(exception);
    }
}

using Meridian.Mediator.Pipeline;

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
            }
        }
    }

    private static bool ShouldRetry(TRequest request, Exception exception)
    {
        return RetryPolicy.TransientOnly(exception) && request.ShouldRetry(exception);
    }
}

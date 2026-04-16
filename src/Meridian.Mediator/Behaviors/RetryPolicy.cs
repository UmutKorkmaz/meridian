namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Shared retry-policy helpers and safety caps used by <see cref="RetryBehavior{TRequest, TResponse}"/>.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Global upper bound for request-specified retry counts. Defaults to 10.
    /// </summary>
    public static int MaxRetriesCap { get; set; } = 10;

    /// <summary>
    /// Global upper bound for exponential backoff delays. Defaults to 5 minutes.
    /// </summary>
    public static TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    internal static Func<double> JitterProvider { get; set; } = static () => Random.Shared.NextDouble();

    /// <summary>
    /// Conservative transient-exception filter suitable as a default retry predicate.
    /// </summary>
    public static bool TransientOnly(Exception exception)
    {
        return exception is not (OperationCanceledException or ArgumentException or ValidationException or UnauthorizedAccessException);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseDelay, int attempt)
    {
        var cappedBackoffMs = Math.Max(0d, MaxBackoff.TotalMilliseconds);
        var baseDelayMs = Math.Max(0d, baseDelay.TotalMilliseconds);
        var exponentialDelayMs = Math.Min(baseDelayMs * Math.Pow(2, attempt - 1), cappedBackoffMs);
        var jitterMs = exponentialDelayMs * 0.2 * JitterProvider();
        return TimeSpan.FromMilliseconds(Math.Min(exponentialDelayMs + jitterMs, cappedBackoffMs));
    }
}

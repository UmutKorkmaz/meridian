using System.Security.Cryptography;

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

    internal static Func<double> JitterProvider { get; set; } = static () =>
        RandomNumberGenerator.GetInt32(0, int.MaxValue) / (double)int.MaxValue;

    /// <summary>
    /// Conservative transient-exception filter suitable as a default retry predicate.
    /// </summary>
    public static bool TransientOnly(Exception exception)
    {
        if (exception is OperationCanceledException or ArgumentException or ValidationException or UnauthorizedAccessException
            || BehaviorFailure.IsRetrySuppressed(exception))
        {
            return false;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.All(TransientOnly);
        }

        return exception.InnerException is null || TransientOnly(exception.InnerException);
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

// Exception-local metadata preserves the public exception type and identity. It only
// governs Meridian retries; applications and providers must resolve uncertain outcomes.
internal static class BehaviorFailure
{
    private static readonly object RetrySuppressedKey = new();

    internal static CancellationTokenSource CreateCleanupBudget() => new(TimeSpan.FromSeconds(5));

    internal static void SuppressRetry(Exception exception)
    {
        lock (exception)
        {
            try
            {
                var data = exception.Data;
                if (!data.Contains(RetrySuppressedKey)) data[RetrySuppressedKey] = true;
            }
            catch (Exception)
            {
                // Classification probes this same key and conservatively rejects
                // metadata that cannot retain it; never mask the original failure.
            }
        }
    }

    internal static bool IsRetrySuppressed(Exception exception)
    {
        // Serialize our accesses when the same exception reaches concurrent calls.
        // The lock covers only metadata operations, never recursion or awaited work.
        lock (exception)
        {
            try
            {
                var data = exception.Data;
                if (data.IsReadOnly || data.Contains(RetrySuppressedKey)) return true;

                // Probe the actual suppression key: a different key could be writable
                // even when a custom dictionary rejects this one. Remove only our key.
                data[RetrySuppressedKey] = true;
                if (data[RetrySuppressedKey] is not true) return true;
                data.Remove(RetrySuppressedKey);
                if (data.Contains(RetrySuppressedKey)) return true;
            }
            catch (Exception) { return true; }
        }

        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(IsRetrySuppressed);
        return exception.InnerException is not null && IsRetrySuppressed(exception.InnerException);
    }
}

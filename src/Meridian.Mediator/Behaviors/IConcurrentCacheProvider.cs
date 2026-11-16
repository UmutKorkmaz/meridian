namespace Meridian.Mediator.Behaviors;

/// <summary>Optional cache capability for sharing an active cache-miss operation.</summary>
public interface IConcurrentCacheProvider : ICacheProvider
{
    /// <summary>
    /// Returns a hit or shares one live owner's factory per provider instance and key.
    /// A waiter's cancellation cancels only its wait. Owner failures reach all waiters;
    /// after retirement a later caller may retry. Null results are never stored.
    /// </summary>
    /// <param name="key">The effective cache-provider key.</param>
    /// <param name="factory">The owner's live request factory.</param>
    /// <param name="duration">Optional entry duration.</param>
    /// <param name="cancellationToken">This caller's cancellation token.</param>
    /// <returns>The cached or factory result, which callers must validate for compatibility.</returns>
    Task<object?> GetOrCreateAsync(string key, Func<CancellationToken, Task<object?>> factory,
        TimeSpan? duration, CancellationToken cancellationToken);
}

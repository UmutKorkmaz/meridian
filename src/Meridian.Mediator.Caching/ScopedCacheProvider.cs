using Meridian.Mediator.Behaviors;

namespace Meridian.Mediator.Caching;

/// <summary>Single ordinary-cache decoration boundary borrowing an inner provider. Does not advertise concurrency when its backend cannot supply it.</summary>
public class ScopedCacheProvider : ICacheProvider
{
    private readonly ICacheProvider _inner;
    private readonly string? _prefix;
    private readonly ICacheKeyScope? _scope;
    private readonly object _gate = new();
    private string? _captured;
    /// <summary>Creates explicit composition over a raw provider. Never wrap an already decorated provider.</summary>
    public ScopedCacheProvider(ICacheProvider inner, CacheProviderOptions? options = null, ICacheKeyScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (inner is ScopedCacheProvider) throw new InvalidOperationException("Cache scope decoration must be applied once.");
        _prefix = options?.ApplicationKeyPrefix; CacheKeyDecorator.ValidateDimension(_prefix, nameof(options)); _inner = inner; _scope = scope;
    }
    /// <summary>Creates manual composition, preserving only the inner provider's actual concurrency capability.</summary>
    public static ScopedCacheProvider Create(ICacheProvider inner, CacheProviderOptions? options = null, ICacheKeyScope? scope = null) =>
        inner is IConcurrentCacheProvider concurrent ? new ConcurrentScopedCacheProvider(concurrent, options, scope) : new ScopedCacheProvider(inner, options, scope);
    internal string EffectiveKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string? partition = null;
        if (_scope is not null)
        {
            partition = _scope.GetScope();
            if (string.IsNullOrWhiteSpace(partition)) throw new InvalidOperationException("Authenticated cache scope is absent or changed.");
            try { CacheKeyDecorator.ValidateDimension(partition, nameof(partition)); }
            catch (ArgumentException) { throw new InvalidOperationException("Authenticated cache scope is absent or changed."); }
            lock (_gate)
            {
                if (_captured is null) _captured = partition;
                else if (!StringComparer.Ordinal.Equals(_captured, partition)) throw new InvalidOperationException("Authenticated cache scope is absent or changed.");
            }
        }
        return CacheKeyDecorator.Compose(key, _prefix, partition);
    }
    /// <inheritdoc/>
    public async Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        var effective = EffectiveKey(key);
        var result = await _inner.GetAsync(effective, cancellationToken).ConfigureAwait(false);
        EffectiveKey(key);
        return result;
    }
    /// <inheritdoc/>
    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken) => _inner.SetAsync(EffectiveKey(key), value, duration, cancellationToken);
    /// <inheritdoc/>
    public Task RemoveAsync(string key, CancellationToken cancellationToken) => _inner.RemoveAsync(EffectiveKey(key), cancellationToken);
}

internal sealed class ConcurrentScopedCacheProvider(IConcurrentCacheProvider inner, CacheProviderOptions? options, ICacheKeyScope? scope)
    : ScopedCacheProvider(inner, options, scope), IConcurrentCacheProvider
{
    public Task<object?> GetOrCreateAsync(string key, Func<CancellationToken, Task<object?>> factory, TimeSpan? duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var effective = EffectiveKey(key);
        return ValidateResultAsync(inner.GetOrCreateAsync(effective, async ct =>
        {
            var result = await factory(ct).ConfigureAwait(false);
            // Verify the authenticated partition before the raw provider can persist the result.
            EffectiveKey(key);
            return result;
        }, duration, cancellationToken), key);
    }
    private async Task<object?> ValidateResultAsync(Task<object?> operation, string key)
    {
        var result = await operation.ConfigureAwait(false);
        EffectiveKey(key);
        return result;
    }

}

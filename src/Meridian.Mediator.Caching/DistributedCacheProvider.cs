using System.Diagnostics.Metrics;
using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.Caching.Distributed;

namespace Meridian.Mediator.Caching;

/// <summary>Borrowed distributed backend with allowlisted framing. Only explicit data rejection is removed and treated as a miss.</summary>
public sealed class DistributedCacheProvider : ICacheProvider
{
    private static readonly Meter Diagnostics = new("Meridian.Mediator.Caching", "1.0");
    private static readonly Counter<long> Rejections = Diagnostics.CreateCounter<long>("meridian.cache.entry_rejections");
    private readonly IDistributedCache _cache;
    private readonly ICacheValueSerializer _serializer;
    private readonly CacheValueCodec _codec;
    private readonly TimeSpan _duration;
    /// <summary>Creates a provider without taking backend disposal ownership or introducing distributed coalescing.</summary>
    public DistributedCacheProvider(IDistributedCache cache, ICacheValueSerializer serializer, CacheValueCodec codec, CacheProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(serializer); ArgumentNullException.ThrowIfNull(codec);
        _duration = options?.DefaultDuration ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_duration, TimeSpan.Zero, nameof(options));
        _cache = cache; _serializer = serializer; _codec = codec;
    }
    /// <inheritdoc/>
    public async Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key); cancellationToken.ThrowIfCancellationRequested();
        var bytes = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return (false, null);
        object? value;
        try { value = _codec.Decode(bytes); if (value is null) throw new CacheValueDataException(CacheValueRejectionReason.NullEntry); }
        catch (CacheValueDataException error)
        {
            Rejections.Add(1, new KeyValuePair<string, object?>("reason", error.Reason.ToString()));
            // IDistributedCache has no compare/delete: a racing good writer may lose a hit.
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false); return (false, null);
        }
        return (true, value);
    }
    /// <inheritdoc/>
    public async Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(value); cancellationToken.ThrowIfCancellationRequested();
        var retention = duration ?? _duration;
        if (retention <= TimeSpan.Zero) { await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false); return; }
        var bytes = _codec.Encode(value, _serializer.GetContractId(value.GetType()));
        await _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = retention }, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc/>
    public Task RemoveAsync(string key, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(key); cancellationToken.ThrowIfCancellationRequested(); return _cache.RemoveAsync(key, cancellationToken); }
}

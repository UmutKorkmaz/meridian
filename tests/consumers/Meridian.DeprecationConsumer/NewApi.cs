using System.Reflection;
using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.Extensions.DependencyInjection;

// Migrated variant: the documented replacements from docs/deprecations.md. It must reach every COMMON value of the legacy
// variant; only the OBSERVED provider keys, generated mode and idempotency concurrency/null handling differ.
internal static partial class Variant
{
    internal const string Name = "migrated";
    internal const string FlatMode = "Structural(explicit ignore)";
    // An atomic claim rejects the in-flight duplicate, and a completed null response is replayed.
    internal const string ExpectedConcurrentDuplicate = "rejected-in-flight";
    internal const int ExpectedNullExecutions = 1;

    internal static partial void RegisterAssemblies(IServiceCollection services, Assembly[] assemblies) => services.AddMeridianMapping(assemblies);
    internal static partial void RegisterMarkers(IServiceCollection services, Type[] markerTypes) => services.AddMeridianMapping(markerTypes);
    internal static partial void RegisterConfiguration(IServiceCollection services, Action<IMapperConfigurationExpression> configure) => services.AddMeridianMapping(configure);
    internal static partial GenDto? MapLegacyDefault(GenSource source) => LegacyDefaultMaps.MapToGenDto(source);
    internal static partial GenFlatDto MapFlat(GenFlatSource source) => FlatMaps.MapToGenFlatDto(source);
    internal static partial CachingBehaviorOptions CacheOptions() => new();
    internal static partial string ProviderKey(OrderQuery query) => CacheKeyComposer.Compose<OrderQuery>(query.CacheKey);
    // The authenticated scope partitions tenants, so the logical key no longer embeds the tenant.
    internal static partial string TenantCacheKey(string tenant, int id) => $"orders:{id}";
    internal static partial string TenantProviderKey(string tenant, int id) =>
        CacheKeyDecorator.Compose(CacheKeyComposer.Compose<TenantOrderQuery>(TenantCacheKey(tenant, id)), null, tenant);
    internal static partial void RegisterCacheScope(IServiceCollection services) => services.AddMeridianCacheScope<TenantScope>();
    internal static partial IIdempotencyStore CreateIdempotencyStore() => new AtomicMemoryIdempotencyStore();
}

// The same destination keeps the Legacy method name, null return and shallow references when the mode is explicit.
[GenerateMapper(typeof(GenSource), typeof(GenDto), Mode = GeneratedMappingMode.Legacy)]
public static partial class LegacyDefaultMaps { }

// Structural opt-in: the member Legacy skipped silently is now an explicit ignore; any other unmapped member is an error.
[GenerateMapper(typeof(GenFlatSource), typeof(GenFlatDto), Mode = GeneratedMappingMode.Structural)]
[IgnoreMapMember("Note")]
public static partial class FlatMaps { }

public sealed class AtomicMemoryIdempotencyStore : IAtomicIdempotencyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (bool Completed, object? Response)> _entries = new(StringComparer.Ordinal);

    public Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
                return Task.FromResult(new IdempotencyClaimResult(entry.Completed ? IdempotencyClaimStatus.Completed : IdempotencyClaimStatus.InFlight, entry.Response));
            _entries[key] = (false, null);
            return Task.FromResult(new IdempotencyClaimResult(IdempotencyClaimStatus.Claimed));
        }
    }

    public Task CompleteAsync(string key, object? response, CancellationToken ct)
    {
        lock (_gate) _entries[key] = (true, response);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken ct)
    {
        lock (_gate) _entries.Remove(key);
        return Task.CompletedTask;
    }

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<(bool Exists, object? CachedResponse)>(
                _entries.TryGetValue(key, out var entry) && entry.Completed ? (true, entry.Response) : (false, null));
        }
    }

    public Task StoreAsync(string key, object response, CancellationToken ct) => CompleteAsync(key, response, ct);
}

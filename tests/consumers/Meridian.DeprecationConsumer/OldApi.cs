using System.Collections.Concurrent;
using System.Reflection;
using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.DependencyInjection;

// Legacy variant: the retained surfaces exactly as existing applications call them today. This file must compile under
// warnings as errors with no suppression; an Obsolete attribute or a default flip on any of them breaks this build.
internal static partial class Variant
{
    internal const string Name = "legacy";
    internal const string FlatMode = "Legacy(default)";
    // IIdempotencyStore check-then-store does not suppress a concurrent duplicate, and a successful null is not stored.
    internal const string ExpectedConcurrentDuplicate = "executed";
    internal const int ExpectedNullExecutions = 2;

    internal static partial void RegisterAssemblies(IServiceCollection services, Assembly[] assemblies) => services.AddAutoMapper(assemblies);
    internal static partial void RegisterMarkers(IServiceCollection services, Type[] markerTypes) => services.AddAutoMapper(markerTypes);
    internal static partial void RegisterConfiguration(IServiceCollection services, Action<IMapperConfigurationExpression> configure) => services.AddAutoMapper(configure);
    internal static partial GenDto? MapLegacyDefault(GenSource source) => LegacyDefaultMaps.MapToGenDto(source);
    internal static partial GenFlatDto MapFlat(GenFlatSource source) => FlatMaps.MapToGenFlatDto(source);
    internal static partial CachingBehaviorOptions CacheOptions() => new() { UseRequestTypeNamespacedKeys = false };
    internal static partial string ProviderKey(OrderQuery query) => query.CacheKey;
    // Without an authenticated scope, legacy applications partition tenants inside the raw logical key.
    internal static partial string TenantCacheKey(string tenant, int id) => $"{tenant}:orders:{id}";
    internal static partial string TenantProviderKey(string tenant, int id) => TenantCacheKey(tenant, id);
    internal static partial void RegisterCacheScope(IServiceCollection services) { }
    internal static partial IIdempotencyStore CreateIdempotencyStore() => new CheckThenStoreIdempotencyStore();
}

// Generated mapping in the default (Legacy) mode: GenDto.Note and GenFlatDto.Note have no source member, so the
// generator reports MERIDIANGEN002 as Info and leaves their initializers in place.
[GenerateMapper(typeof(GenSource), typeof(GenDto))]
public static partial class LegacyDefaultMaps { }

[GenerateMapper(typeof(GenFlatSource), typeof(GenFlatDto))]
public static partial class FlatMaps { }

public sealed class CheckThenStoreIdempotencyStore : IIdempotencyStore
{
    // Thread-safe storage only: the check and the store remain two separate, non-atomic steps.
    private readonly ConcurrentDictionary<string, object> _responses = new(StringComparer.Ordinal);

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
        Task.FromResult<(bool Exists, object? CachedResponse)>(_responses.TryGetValue(key, out var response) ? (true, response) : (false, null));

    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _responses[key] = response;
        return Task.CompletedTask;
    }
}

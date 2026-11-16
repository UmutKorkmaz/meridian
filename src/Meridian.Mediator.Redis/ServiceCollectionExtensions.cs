using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Meridian.Mediator.Redis;

/// <summary>Explicit scoped Redis idempotency registration over a borrowed application-lifetime multiplexer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers a scoped store and allowlisted serializer. First option snapshot wins; repeated calls validate configuration and preserve custom stores/serializers.</summary>
    public static IServiceCollection AddMeridianRedisIdempotency(this IServiceCollection services, CacheContractRegistry registry, Action<RedisIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(registry);
        var input = new RedisIdempotencyOptions(); configure?.Invoke(input);
        RedisIdempotencyOptions.Milliseconds(input.LeaseDuration, nameof(input.LeaseDuration));
        RedisIdempotencyOptions.Milliseconds(input.CompletedRetention, nameof(input.CompletedRetention));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(input.MaximumEntryBytes, 0);
        CacheKeyDecorator.Compose("", input.ApplicationKeyPrefix, null);
        var snapshot = new RedisIdempotencyOptions { LeaseDuration = input.LeaseDuration, CompletedRetention = input.CompletedRetention, ApplicationKeyPrefix = input.ApplicationKeyPrefix, MaximumEntryBytes = input.MaximumEntryBytes };
        var existing = services.SingleOrDefault(d => d.ServiceType == typeof(CacheContractRegistry));
        if (existing is not null && !ReferenceEquals(existing.ImplementationInstance, registry)) throw new InvalidOperationException("The supplied Redis contract registry conflicts with an existing registration.");
        // Freeze public registry metadata without registering/capturing this temporary serializer.
        _ = new JsonCacheValueSerializer(registry);
        services.TryAddSingleton(registry); services.TryAddSingleton(snapshot);
        services.TryAddScoped<ICacheValueSerializer, JsonCacheValueSerializer>();
        if (services.Any(d => d.ServiceType == typeof(IIdempotencyStore))) return services;
        if (services.Any(d => d.ServiceType == typeof(RedisLeasedIdempotencyStore) || d.ServiceType == typeof(ILeasedIdempotencyStore)))
            throw new InvalidOperationException("Compose an existing leased store explicitly as IIdempotencyStore.");
        services.AddScoped(sp => new RedisLeasedIdempotencyStore(sp.GetRequiredService<IConnectionMultiplexer>(), registry,
            sp.GetRequiredService<ICacheValueSerializer>(), sp.GetRequiredService<RedisIdempotencyOptions>(), sp.GetService<ICacheKeyScope>()));
        services.AddScoped<ILeasedIdempotencyStore>(sp => sp.GetRequiredService<RedisLeasedIdempotencyStore>());
        services.AddScoped<IIdempotencyStore>(sp => sp.GetRequiredService<RedisLeasedIdempotencyStore>());
        return services;
    }
}

using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Mediator.Caching;

/// <summary>Memory caching registration extensions.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers one default singleton provider and a DI-managed memory cache, preserving custom
    /// providers and backends. The first options registration wins; each configure call is validated.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Optional adapter configuration.</param>
    /// <returns>The supplied collection.</returns>
    public static IServiceCollection AddMeridianMemoryCaching(this IServiceCollection services, Action<CacheProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new CacheProviderOptions();
        configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DefaultDuration, TimeSpan.Zero, nameof(configure));
        // Copy the duration so retaining the configure object cannot change the registration later.
        services.TryAddSingleton(new CacheProviderOptions { DefaultDuration = options.DefaultDuration });
        services.AddMemoryCache();
        services.TryAddSingleton<ICacheProvider, MemoryCacheProvider>();
        return services;
    }
}

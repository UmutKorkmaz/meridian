using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Mediator.Caching;

/// <summary>Explicit cache backend and authenticated scope registration.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers one memory composition, preserving deliberate custom providers. First options win; every call is validated.</summary>
    public static IServiceCollection AddMeridianMemoryCaching(this IServiceCollection services, Action<CacheProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = Options(configure); var registration = State(services);
        Select(registration, Backend.Memory);
        CheckCustom(services, registration, options.ApplicationKeyPrefix is not null);
        services.TryAddSingleton(options); services.AddMemoryCache();
        Normalize(services, registration); return services;
    }
    /// <summary>Registers scoped distributed providers/codecs over a supplied IDistributedCache. Uses explicit contracts and preserves scoped custom serializers.</summary>
    public static IServiceCollection AddMeridianDistributedCaching(this IServiceCollection services, CacheContractRegistry registry, Action<CacheProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(registry);
        var options = Options(configure); var registration = State(services);
        Select(registration, Backend.Distributed);
        CheckCustom(services, registration, options.ApplicationKeyPrefix is not null);
        if (registration.Registry is not null && !ReferenceEquals(registration.Registry, registry)) throw new InvalidOperationException("Distributed caching already selected a different contract registry.");
        var existingRegistry = services.SingleOrDefault(d => d.ServiceType == typeof(CacheContractRegistry));
        if (existingRegistry is not null && !ReferenceEquals(existingRegistry.ImplementationInstance, registry)) throw new InvalidOperationException("The supplied contract registry conflicts with an existing registration.");
        registration.Registry = registry; registry.Freeze();
        services.TryAddSingleton(options); services.TryAddSingleton(registry);
        services.TryAddScoped<ICacheValueSerializer, JsonCacheValueSerializer>();
        services.TryAddScoped<CacheValueCodec>(); Normalize(services, registration); return services;
    }
    /// <summary>Opts into a scoped authenticated partition. Repeated identical selection is idempotent; unowned automatic composition is rejected.</summary>
    public static IServiceCollection AddMeridianCacheScope<TScope>(this IServiceCollection services) where TScope : class, ICacheKeyScope
    {
        ArgumentNullException.ThrowIfNull(services); var registration = State(services);
        if (registration.ScopeType is not null && registration.ScopeType != typeof(TScope)) throw new InvalidOperationException("A different authenticated cache scope was already selected.");
        CheckCustom(services, registration, true);
        var scopes = services.Where(d => d.ServiceType == typeof(ICacheKeyScope)).ToArray();
        if (scopes.Any(d => d.ImplementationType != typeof(TScope) || d.Lifetime != ServiceLifetime.Scoped)) throw new InvalidOperationException("Authenticated cache scope must use the explicitly selected scoped type.");
        registration.ScopeType = typeof(TScope); services.TryAddScoped<ICacheKeyScope, TScope>(); Normalize(services, registration); return services;
    }
    private static CacheProviderOptions Options(Action<CacheProviderOptions>? configure)
    {
        var options = new CacheProviderOptions(); configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DefaultDuration, TimeSpan.Zero, nameof(configure));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumEntryBytes, 0, nameof(configure));
        CacheKeyDecorator.ValidateDimension(options.ApplicationKeyPrefix, nameof(configure));
        return new() { DefaultDuration = options.DefaultDuration, ApplicationKeyPrefix = options.ApplicationKeyPrefix, MaximumEntryBytes = options.MaximumEntryBytes };
    }
    private static Registration State(IServiceCollection services)
    {
        var state = services.SingleOrDefault(d => d.ServiceType == typeof(Registration))?.ImplementationInstance as Registration;
        if (state is not null) return state;
        state = new(); services.AddSingleton(state); return state;
    }
    private static void Select(Registration state, Backend backend)
    {
        if (state.Backend is not null && state.Backend != backend) throw new InvalidOperationException("Choose one Meridian cache backend; memory and distributed registrations conflict.");
        state.Backend = backend;
    }
    private static bool HasCustom(IServiceCollection services, Registration state) => services.Any(d => d.ServiceType == typeof(ICacheProvider) && !state.Owned.Contains(d));
    private static void CheckCustom(IServiceCollection services, Registration state, bool decorate)
    {
        if (HasCustom(services, state) && (decorate || state.ScopeType is not null)) throw new InvalidOperationException("Automatic scope decoration requires a library-owned provider. Compose a custom provider explicitly with ScopedCacheProvider.Create.");
    }
    private static void Normalize(IServiceCollection services, Registration state)
    {
        foreach (var descriptor in state.Owned) services.Remove(descriptor); state.Owned.Clear();
        if (state.Backend is null || HasCustom(services, state)) return;
        var optionsDescriptor = services.Single(d => d.ServiceType == typeof(CacheProviderOptions));
        if (optionsDescriptor.Lifetime != ServiceLifetime.Singleton || optionsDescriptor.ImplementationInstance is not CacheProviderOptions options)
            throw new InvalidOperationException("Cache options must be a singleton configuration snapshot.");
        var decorated = options.ApplicationKeyPrefix is not null || state.ScopeType is not null;
        var lifetime = state.ScopeType is not null || state.Backend == Backend.Distributed ? ServiceLifetime.Scoped : ServiceLifetime.Singleton;
        var rawType = state.Backend == Backend.Memory ? typeof(MemoryCacheProvider) : typeof(DistributedCacheProvider);
        if (services.Any(d => d.ServiceType == rawType)) throw new InvalidOperationException("A concrete cache provider already has an unowned registration; register a custom ICacheProvider explicitly.");
        Add(ServiceDescriptor.Describe(rawType, rawType, lifetime));
        Add(ServiceDescriptor.Describe(typeof(ICacheProvider), sp =>
        {
            var raw = (ICacheProvider)sp.GetRequiredService(rawType);
            return decorated ? ScopedCacheProvider.Create(raw, options, state.ScopeType is null ? null : sp.GetRequiredService<ICacheKeyScope>()) : raw;
        }, lifetime));
        void Add(ServiceDescriptor descriptor) { services.Add(descriptor); state.Owned.Add(descriptor); }
    }
    private enum Backend { Memory, Distributed }
    private sealed class Registration
    {
        internal Backend? Backend { get; set; }
        internal Type? ScopeType { get; set; }
        internal CacheContractRegistry? Registry { get; set; }
        internal List<ServiceDescriptor> Owned { get; } = [];
    }
}

using Microsoft.Extensions.DependencyInjection;
namespace Meridian.Mediator.Generated;
/// <summary>Registration support called by generated consumer code.</summary>
public static class GeneratedMediatorRegistration
{
    /// <summary>Registers declared closed handler factories, deduplicated by explicit type metadata.</summary>
    public static void RegisterServices(IServiceCollection services, IReadOnlyList<GeneratedMediatorContribution> contributions, ServiceLifetime handlerLifetime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contributions);
        foreach (var item in contributions)
        {
            if (item.Activation == GeneratedMediatorActivation.StandardOnly) continue;
            if (item.Phase != GeneratedMediatorPhase.Handler)
                throw new NotSupportedException("Generated pipeline contributions require the generated dispatch capability.");
            var lifetime = item.Lifetime switch { GeneratedMediatorLifetime.Transient => ServiceLifetime.Transient, GeneratedMediatorLifetime.Singleton => ServiceLifetime.Singleton, _ => handlerLifetime };
            if (services.Any(d => d.ServiceType == typeof(FactoryMarker) && d.ImplementationInstance is FactoryMarker m && m.Service == item.ServiceType && m.Implementation == item.ImplementationType && m.Lifetime == lifetime)) continue;
            if (services.Any(d => d.ServiceType == item.ServiceType && d.ImplementationType == item.ImplementationType && d.Lifetime == lifetime)) continue;
            services.Add(new ServiceDescriptor(item.ServiceType, item.Factory, lifetime));
            services.AddSingleton(new FactoryMarker(item.ServiceType, item.ImplementationType, lifetime));
        }
    }
    internal static void RegisterRegistries(IServiceCollection services, IReadOnlyList<IGeneratedMediatorRegistry> registries, ServiceLifetime lifetime, bool strict)
    {
        if (strict) throw new NotSupportedException("RequireGeneratedDispatch needs generated wrapper dispatch; this registry package currently supplies closed DI registration metadata only.");
        var old = services.Where(d => d.ServiceType == typeof(RegistryMarker)).Select(d => (RegistryMarker)d.ImplementationInstance!).ToArray();
        var pending = new List<IGeneratedMediatorRegistry>();
        foreach (var registry in registries)
        {
            if (old.Any(m => (m.Lifetime != lifetime || m.Strict != strict))) throw new InvalidOperationException("Repeated generated registry registration has conflicting lifetime or strictness.");
            var all = old.Select(m => m.Registry).Concat(pending).ToArray();
            if (all.Any(r => r.RegistryId == registry.RegistryId && r.GetType() != registry.GetType())) throw new InvalidOperationException("Generated registry ID collision: " + registry.RegistryId);
            if (all.Any(r => r.GetType() == registry.GetType() && r.RegistryId == registry.RegistryId)) continue;
            if (registry is IGeneratedMediatorContributions { IncludesStandardPipeline: true }) throw new NotSupportedException("IncludeStandardPipeline requires generated pipeline dispatch support.");
            if (all.Any(r => r.RequestTypes.Intersect(registry.RequestTypes).Any() || r.StreamTypes.Intersect(registry.StreamTypes).Any())) throw new InvalidOperationException("Generated registries declare duplicate request or stream slots.");
            pending.Add(registry);
        }
        foreach (var registry in pending)
        {
            registry.RegisterServices(services, lifetime);
            services.AddSingleton(new RegistryMarker(registry, lifetime, strict));
            services.AddSingleton<IGeneratedMediatorRegistry>(registry);
        }
    }
    private sealed record FactoryMarker(Type Service, Type Implementation, ServiceLifetime Lifetime);
    private sealed record RegistryMarker(IGeneratedMediatorRegistry Registry, ServiceLifetime Lifetime, bool Strict);
}

using Microsoft.Extensions.DependencyInjection;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Meridian.Mediator.Generated;
/// <summary>Registration support called by generated consumer code.</summary>
public static class GeneratedMediatorRegistration
{
    /// <summary>Registers declared closed handler factories, deduplicated by explicit type metadata.</summary>
    public static void RegisterServices(IServiceCollection services, IReadOnlyList<GeneratedMediatorContribution> contributions, ServiceLifetime handlerLifetime)
    {
        RegisterServices(services, contributions, handlerLifetime, false);
    }

    /// <summary>Registers finite closed factories, optionally activating standard contributions.</summary>
    public static void RegisterServices(IServiceCollection services, IReadOnlyList<GeneratedMediatorContribution> contributions, ServiceLifetime handlerLifetime, bool includeStandardPipeline)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contributions);
        if (includeStandardPipeline) RegisterAuditSink(services);
        foreach (var item in contributions.Where(i => i.Activation != GeneratedMediatorActivation.StandardOnly || includeStandardPipeline)
                     .OrderBy(i => i.Phase).ThenBy(i => i.Order).ThenBy(i => i.DeclarationOrdinal))
            RegisterContribution(services,item,handlerLifetime);
    }

    private static void RegisterAuditSink(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(Behaviors.IAuditSink)))
            services.AddSingleton<Behaviors.IAuditSink>(sp => new Behaviors.LoggerAuditSink(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Behaviors.LoggerAuditSink>>()));
    }

    private static void RegisterContribution(IServiceCollection services, GeneratedMediatorContribution item, ServiceLifetime handlerLifetime, int? occurrence = null, ServiceLifetime? lifetimeOverride = null)
    {
        var lifetime = lifetimeOverride ?? ( item.Lifetime switch { GeneratedMediatorLifetime.Transient => ServiceLifetime.Transient, GeneratedMediatorLifetime.Singleton => ServiceLifetime.Singleton, _ => handlerLifetime });
        // Notification enumeration follows TryAddEnumerable: first implementation wins regardless of lifetime.
        var identityLifetime = item.Category == GeneratedMediatorCategory.NotificationHandler ? (ServiceLifetime?)null : lifetime;
        if (services.Any(d => d.ImplementationInstance is FactoryMarker m && m.Service == item.ServiceType && m.Implementation == item.ImplementationType &&
            (identityLifetime == null || m.Lifetime == identityLifetime) && m.Occurrence == occurrence && services.Any(candidate => ReferenceEquals(candidate,m.Descriptor)))) return;
        if (occurrence == null && services.Any(d => d.ServiceType == item.ServiceType && d.ImplementationType == item.ImplementationType && (identityLifetime == null || d.Lifetime == identityLifetime))) return;
        var descriptor = new ServiceDescriptor(item.ServiceType,item.Factory,lifetime);
        services.Add(descriptor);
        services.AddSingleton(new FactoryMarker(item.ServiceType,item.ImplementationType,lifetime,descriptor,item,occurrence));
    }
    // Legacy scanning can see identities erased by the public ServiceDescriptor factory overload.
    // Opaque user factories are deliberately not treated as equivalent implementations.
    internal static bool HasGeneratedRegistration(IServiceCollection services, Type serviceType, Type implementationType, ServiceLifetime? lifetime = null) =>
        services.Any(d => d.ServiceType == typeof(FactoryMarker) && d.ImplementationInstance is FactoryMarker m &&
            m.Service == serviceType && m.Implementation == implementationType && (lifetime == null || m.Lifetime == lifetime) &&
            services.Any(candidate => ReferenceEquals(candidate, m.Descriptor)));

    internal static void RegisterRegistries(IServiceCollection services, MeridianMediatorConfiguration config)
    {
        var registries = config.GeneratedRegistries;
        var lifetime = config.Lifetime;
        var strict = config.RequireGeneratedDispatch;
        var effectiveStrict = strict || services.Any(d => d.ImplementationInstance is StrictMarker { Strict: true });
        if (effectiveStrict && config.AssembliesToScan.Count != 0)
            throw new InvalidOperationException("Strict generated dispatch cannot scan assemblies.");

        if (effectiveStrict && services.Any(d => d.ServiceType == typeof(IPipelineBehavior<,>) || d.ServiceType == typeof(IStreamPipelineBehavior<,>) || d.ImplementationType is { IsConstructedGenericType: true } implementation && implementation.GetGenericTypeDefinition() == typeof(RequestExceptionProcessorBehavior<,>)))
            throw new InvalidOperationException("Strict generated dispatch cannot use externally registered open pipeline descriptors; declare closed behavior factories in the registry.");

        var old = services.Where(d => d.ServiceType == typeof(RegistryMarker)).Select(d => (RegistryMarker)d.ImplementationInstance!).ToArray();
        var pending = new List<IGeneratedMediatorRegistry>();
        foreach (var registry in registries)
        {
            if (old.Any(m => (m.Lifetime != lifetime || m.Strict != strict))) throw new InvalidOperationException("Repeated generated registry registration has conflicting lifetime or strictness.");
            var all = old.Select(m => m.Registry).Concat(pending).ToArray();
            if (all.Any(r => r.RegistryId == registry.RegistryId && r.GetType() != registry.GetType())) throw new InvalidOperationException("Generated registry ID collision: " + registry.RegistryId);
            if (all.Any(r => r.GetType() == registry.GetType() && r.RegistryId == registry.RegistryId)) continue;

            if (all.Any(r => r.RequestTypes.Intersect(registry.RequestTypes).Any() || r.StreamTypes.Intersect(registry.StreamTypes).Any())) throw new InvalidOperationException("Generated registries declare duplicate request or stream slots.");
            pending.Add(registry);
        }
        _ = new GeneratedMediatorComposition(old.Select(m => m.Registry).Concat(pending), strict);
        if (registries.Count != 0 || strict)
        {
            if (services.Any(d => d.ImplementationInstance is StrictMarker m && (m.Strict != strict || m.Lifetime != lifetime)))
                throw new InvalidOperationException("Repeated generated registry registration has conflicting lifetime or strictness.");
            services.AddSingleton(new StrictMarker(strict,lifetime));
        }
        if (config.ActivateGeneratedStandardPipeline)
            foreach (var registry in registries) services.AddSingleton(new StandardMarker(registry.GetType(),registry.RegistryId));
        foreach (var registry in pending)
        {
            registry.RegisterServices(services, lifetime);
            services.AddSingleton(new RegistryMarker(registry, lifetime, strict));
            services.AddSingleton<IGeneratedMediatorRegistry>(registry);
        }
    }
    internal static bool NormalizePipeline(IServiceCollection services, MeridianMediatorConfiguration config)
    {
        var previous = services.Where(d => d.ImplementationInstance is ConfigurationMarker).Select(d => (ConfigurationMarker)d.ImplementationInstance!).ToArray();
        var current = new ConfigurationMarker(config.PipelineBehaviorRegistrations.ToArray(), config.PreProcessorTypes.ToArray(),config.PostProcessorTypes.ToArray());
        var configurations = previous.Append(current).ToArray();
        var registries = services.Where(d => d.ImplementationInstance is RegistryMarker).Select(d => (RegistryMarker)d.ImplementationInstance!).ToArray();
        if (registries.Length == 0)
        {
            if (services.Any(d => d.ImplementationInstance is StrictMarker {Strict:true}) &&
                configurations.Any(c => c.Behaviors.Length != 0 || c.Pre.Length != 0 || c.Post.Length != 0))
                throw new InvalidOperationException("Strict generated dispatch requires a registry declaring every configured behavior and processor.");
            services.AddSingleton(current);
            return false;
        }
        var strict = services.Any(d => d.ImplementationInstance is StrictMarker {Strict:true});
        var standard = services.Where(d => d.ImplementationInstance is StandardMarker).Select(d => (StandardMarker)d.ImplementationInstance!).ToArray();
        var all = registries.Where(r => r.Registry is IGeneratedMediatorContributions).SelectMany(r =>
        {
            var metadata = (IGeneratedMediatorContributions)r.Registry;
            var activate = metadata.IncludesStandardPipeline || standard.Any(s => s.Type == r.Registry.GetType() && s.Id == r.Registry.RegistryId);
            return metadata.Contributions.Select(c => (Contribution:c, r.Lifetime, Active: c.Activation != GeneratedMediatorActivation.StandardOnly || activate));
        }).ToArray();
        var behaviors = all.Where(c=>c.Contribution.Category == GeneratedMediatorCategory.Behavior)
            .GroupBy(c=>(c.Contribution.ServiceType,c.Contribution.ImplementationType)).Select(g=>(g.First().Contribution,g.First().Lifetime,Active:g.Any(c=>c.Active))).ToArray();
        var capabilities = registries.SelectMany(r => r.Registry is IGeneratedMediatorBehaviorCapabilities metadata ? metadata.BehaviorCapabilities : Array.Empty<GeneratedMediatorBehaviorCapability>()).ToArray();
        foreach(var group in capabilities.GroupBy(c=>(c.RequestType,c.ResponseType,c.DefinitionType,c.Category)))
            if(group.Select(c=>c.Applies).Distinct().Count()!=1) throw new InvalidOperationException($"Conflicting generated applicability metadata for {group.Key}.");
        var knownPairs = all.Where(c=>c.Contribution.Category == GeneratedMediatorCategory.RequestHandler)
            .Select(c=>c.Contribution.ServiceType.GenericTypeArguments).Select(a=>(Request:a[0],Response:a.Length == 2 ? a[1] : typeof(Unit))).Distinct().ToArray();
        var explicitEntries = configurations.SelectMany(c=>c.Behaviors).ToArray();
        var plan = new List<(GeneratedMediatorContribution Item, ServiceLifetime Lifetime,int Order,int Index,int? Occurrence)>();
        var fallback = new List<((Type ServiceType, Type ImplementationType, int Order, int RegistrationIndex, ServiceLifetime Lifetime) Entry,int Index)>();
        var index = 0;
        var legacyOpenDefinitions = !strict ? explicitEntries.Where(e=>e.ServiceType == typeof(IPipelineBehavior<,>)).Select(e=>e.ImplementationType).ToHashSet() : new HashSet<Type>();
        foreach(var entry in explicitEntries)
        {
            if (!strict && entry.ServiceType == typeof(IPipelineBehavior<,>))
            {
                fallback.Add((entry,index++));
                continue;
            }
            var matches = behaviors.Where(b=>b.Contribution.ImplementationType == entry.ImplementationType && b.Contribution.ServiceType == entry.ServiceType ||
                entry.ServiceType == typeof(IPipelineBehavior<,>) && b.Contribution.DefinitionType == entry.ImplementationType ||
                entry.ImplementationType.IsConstructedGenericType && b.Contribution.DefinitionType == typeof(RequestExceptionProcessorBehavior<,>) && entry.ImplementationType.GetGenericTypeDefinition() == typeof(RequestExceptionProcessorBehavior<,>) && b.Contribution.ServiceType == entry.ServiceType).ToArray();
            if (strict && entry.ServiceType == typeof(IPipelineBehavior<,>))
            {
                foreach(var pair in knownPairs)
                {
                    var evidence = capabilities.Where(c=>c.RequestType == pair.Request && c.ResponseType == pair.Response && c.DefinitionType == entry.ImplementationType && c.Category == GeneratedMediatorCategory.Behavior).ToArray();
                    var rooted = matches.Any(m=>m.Contribution.ServiceType.GenericTypeArguments[0] == pair.Request && m.Contribution.ServiceType.GenericTypeArguments[1] == pair.Response);
                    if(evidence.Length == 0 && !rooted || evidence.Any(c=>c.Applies) && !rooted || evidence.Any(c=>!c.Applies) && rooted)
                        throw new InvalidOperationException($"Behavior {entry.ImplementationType} has no consistent generated closure for {pair.Request} / {pair.Response}; declare this finite pair.");
                }
            }
            if (matches.Length == 0 && !(entry.ServiceType == typeof(IPipelineBehavior<,>) && knownPairs.Length != 0 && knownPairs.All(p=>capabilities.Any(c=>c.RequestType == p.Request && c.ResponseType == p.Response && c.DefinitionType == entry.ImplementationType && c.Category == GeneratedMediatorCategory.Behavior && !c.Applies))))
            {
                if(strict) throw new InvalidOperationException($"Behavior {entry.ImplementationType} has no generated closed contribution; declare it in BehaviorTypes for every known request.");
                fallback.Add((entry,index++));
                continue;
            }
            foreach(var match in matches)
                plan.Add((match.Contribution,entry.Lifetime,entry.Order,index,entry.ServiceType == typeof(IPipelineBehavior<,>) ? entry.Order : null));
            index++;
        }
        var fallbackPre = !strict && configurations.SelectMany(c => c.Pre).Any(processor =>
            !all.Any(c => c.Contribution.Category == GeneratedMediatorCategory.PreProcessor && c.Contribution.ImplementationType == processor));
        var fallbackPost = !strict && configurations.SelectMany(c => c.Post).Any(processor =>
            !all.Any(c => c.Contribution.Category == GeneratedMediatorCategory.PostProcessor && c.Contribution.ImplementationType == processor));
        foreach(var behavior in behaviors)
        {
            if (behavior.Contribution.DefinitionType != null && legacyOpenDefinitions.Contains(behavior.Contribution.DefinitionType)) continue;
            // An open fallback driver enumerates the same processors for known and plugin requests.
            // Suppress only the automatic closed occurrence; explicit ordered occurrences remain intact.
            if (behavior.Contribution.Phase == GeneratedMediatorPhase.AutomaticProcessor &&
                behavior.Contribution.ImplementationType.IsConstructedGenericType &&
                (fallbackPre && behavior.Contribution.ImplementationType.GetGenericTypeDefinition() == typeof(RequestPreProcessorBehavior<,>) ||
                 fallbackPost && behavior.Contribution.ImplementationType.GetGenericTypeDefinition() == typeof(RequestPostProcessorBehavior<,>))) continue;
            if (!behavior.Active || plan.Any(p=>p.Item.ServiceType == behavior.Contribution.ServiceType && p.Item.ImplementationType == behavior.Contribution.ImplementationType)) continue;
            plan.Add((behavior.Contribution,behavior.Contribution.Lifetime == GeneratedMediatorLifetime.Transient ? ServiceLifetime.Transient : behavior.Contribution.Lifetime == GeneratedMediatorLifetime.Singleton ? ServiceLifetime.Singleton : behavior.Lifetime,behavior.Contribution.Order,index++,null));
        }
        foreach(var processor in configurations.SelectMany(c=>c.Pre.Concat(c.Post)))
            if (strict && !all.Any(c=>c.Contribution.ImplementationType == processor && c.Contribution.Category is GeneratedMediatorCategory.PreProcessor or GeneratedMediatorCategory.PostProcessor))
                throw new InvalidOperationException($"Processor {processor} has no generated closed contribution; declare it in the registry.");
        // Remove only descriptors whose exact identity we own. A replacement opaque factory survives.
        foreach(var descriptor in services.Where(d=>d.ImplementationInstance is FactoryMarker m && m.Contribution.Category == GeneratedMediatorCategory.Behavior).ToArray())
        {
            var marker = (FactoryMarker)descriptor.ImplementationInstance!;
            services.Remove(marker.Descriptor);
            services.Remove(descriptor);
        }
        ServiceCollectionExtensions.RemoveOwnedPipelineDescriptors(services);
        if (standard.Length != 0 || registries.Any(r=>r.Registry is IGeneratedMediatorContributions {IncludesStandardPipeline:true})) RegisterAuditSink(services);
        foreach(var item in all.Where(c=>c.Active && c.Contribution.Category != GeneratedMediatorCategory.Behavior)) RegisterContribution(services,item.Contribution,item.Lifetime);
        var ordered = plan.Select(p => (Phase:p.Item.Phase,Order:p.Order,Index:p.Index,Apply:(Action)(()=>RegisterContribution(services,p.Item,p.Lifetime,p.Occurrence,p.Lifetime))))
            .Concat(fallback.Select(f=>(Phase:GeneratedMediatorPhase.Request,Order:f.Entry.Order,Index:f.Index,Apply:(Action)(()=>ServiceCollectionExtensions.TryAddPipelineBehavior(services,f.Entry.ServiceType,f.Entry.ImplementationType,f.Entry.Order,f.Entry.Lifetime)))));
        foreach(var item in ordered.OrderBy(p=>p.Phase).ThenBy(p=>p.Order).ThenBy(p=>p.Index)) item.Apply();
        if (!strict)
        {
            foreach(var processor in configurations.SelectMany(c=>c.Pre).Distinct())
                if(!all.Any(c=>c.Contribution.Category == GeneratedMediatorCategory.PreProcessor && c.Contribution.ImplementationType == processor))
                {
                    ServiceCollectionExtensions.RegisterOpenOrClosedGeneric(services,typeof(IRequestPreProcessor<>),processor);
                    ServiceCollectionExtensions.TryAddPipelineBehavior(services,typeof(IPipelineBehavior<,>),typeof(RequestPreProcessorBehavior<,>),0);
                }
            foreach(var processor in configurations.SelectMany(c=>c.Post).Distinct())
                if(!all.Any(c=>c.Contribution.Category == GeneratedMediatorCategory.PostProcessor && c.Contribution.ImplementationType == processor))
                {
                    ServiceCollectionExtensions.RegisterOpenOrClosedGeneric(services,typeof(IRequestPostProcessor<,>),processor);
                    ServiceCollectionExtensions.TryAddPipelineBehavior(services,typeof(IPipelineBehavior<,>),typeof(RequestPostProcessorBehavior<,>),0);
                }
        }
        services.AddSingleton(current);
        return true;
    }
    internal static bool NormalizeStreams(IServiceCollection services, MeridianMediatorConfiguration config)
    {
        var current = new StreamConfigurationMarker(config.ClosedStreamBehaviors.ToArray(), config.OpenStreamBehaviors.ToArray());
        var configurations = services.Where(d => d.ImplementationInstance is StreamConfigurationMarker)
            .Select(d => (StreamConfigurationMarker)d.ImplementationInstance!).Append(current).ToArray();
        var registries = services.Where(d => d.ImplementationInstance is RegistryMarker)
            .Select(d => (RegistryMarker)d.ImplementationInstance!).ToArray();
        var strict = services.Any(d => d.ImplementationInstance is StrictMarker { Strict: true });
        if (registries.Length == 0)
        {
            if (strict && configurations.Any(c => c.Closed.Length != 0 || c.Open.Length != 0))
                throw new InvalidOperationException("Strict generated dispatch requires a registry declaring every configured stream behavior.");
            services.AddSingleton(current);
            return false;
        }
        var contributions = registries.Where(r => r.Registry is IGeneratedMediatorContributions)
            .SelectMany(r => ((IGeneratedMediatorContributions)r.Registry).Contributions.Select(c => (Item: c, r.Lifetime))).ToArray();
        var behaviors = contributions.Where(c => c.Item.Category == GeneratedMediatorCategory.StreamBehavior)
            .GroupBy(c => (c.Item.ServiceType, c.Item.ImplementationType)).Select(g => g.First()).ToArray();
        var pairs = contributions.Where(c => c.Item.Category == GeneratedMediatorCategory.StreamHandler)
            .Select(c => c.Item.ServiceType.GenericTypeArguments).Select(a => (Request: a[0], Response: a[1])).Distinct().ToArray();
        var capabilities = registries.SelectMany(r => r.Registry is IGeneratedMediatorBehaviorCapabilities metadata
            ? metadata.BehaviorCapabilities : Array.Empty<GeneratedMediatorBehaviorCapability>()).ToArray();
        var closed = configurations.SelectMany(c => c.Closed).Distinct().ToArray();
        var open = configurations.SelectMany(c => c.Open).Distinct().ToArray();
        var plan = new List<(GeneratedMediatorContribution? Item, Type Service, Type Implementation, ServiceLifetime Lifetime, GeneratedMediatorPhase Phase)>();
        foreach (var entry in closed)
        {
            var match = behaviors.FirstOrDefault(b => b.Item.ServiceType == entry.ServiceType && b.Item.ImplementationType == entry.ImplementationType);
            if (match.Item == null && strict)
                throw new InvalidOperationException($"Stream behavior {entry.ImplementationType} has no generated closed contribution; declare it in StreamBehaviorTypes.");
            plan.Add((match.Item, entry.ServiceType, entry.ImplementationType, ServiceLifetime.Transient, GeneratedMediatorPhase.ClosedStream));
        }
        foreach (var definition in open)
        {
            if (!strict)
            {
                plan.Add((null, typeof(IStreamPipelineBehavior<,>), definition, ServiceLifetime.Transient, GeneratedMediatorPhase.OpenStream));
                continue;
            }
            var matches = behaviors.Where(b => b.Item.DefinitionType == definition).ToArray();
            if (pairs.Length == 0 && matches.Length == 0)
                throw new InvalidOperationException($"Stream behavior {definition} has no generated finite stream pairs; declare it in StreamBehaviorTypes.");
            foreach (var pair in pairs)
            {
                var evidence = capabilities.Where(c => c.RequestType == pair.Request && c.ResponseType == pair.Response && c.DefinitionType == definition && c.Category == GeneratedMediatorCategory.StreamBehavior).ToArray();
                var rooted = matches.Any(m => m.Item.ServiceType.GenericTypeArguments[0] == pair.Request && m.Item.ServiceType.GenericTypeArguments[1] == pair.Response);
                if (evidence.Length == 0 && !rooted || evidence.Any(c => c.Applies) && !rooted || evidence.Any(c => !c.Applies) && rooted)
                    throw new InvalidOperationException($"Stream behavior {definition} has no consistent generated closure for {pair.Request} / {pair.Response}; declare this finite pair.");
            }
            foreach (var match in matches)
                plan.Add((match.Item, match.Item.ServiceType, match.Item.ImplementationType, ServiceLifetime.Transient, GeneratedMediatorPhase.OpenStream));
        }
        foreach (var behavior in behaviors)
        {
            if (!strict && behavior.Item.DefinitionType != null && open.Contains(behavior.Item.DefinitionType)) continue;
            if (plan.Any(p => p.Service == behavior.Item.ServiceType && p.Implementation == behavior.Item.ImplementationType)) continue;
            var lifetime = behavior.Item.Lifetime switch { GeneratedMediatorLifetime.Transient => ServiceLifetime.Transient, GeneratedMediatorLifetime.Singleton => ServiceLifetime.Singleton, _ => behavior.Lifetime };
            plan.Add((behavior.Item, behavior.Item.ServiceType, behavior.Item.ImplementationType, lifetime, behavior.Item.Phase));
        }
        // Only exact descriptors owned by registration are removed; user factories survive.
        foreach (var descriptor in services.Where(d => d.ImplementationInstance is FactoryMarker { Contribution.Category: GeneratedMediatorCategory.StreamBehavior } || d.ImplementationInstance is StreamDescriptorMarker).ToArray())
        {
            var owned = descriptor.ImplementationInstance is FactoryMarker factory ? factory.Descriptor : ((StreamDescriptorMarker)descriptor.ImplementationInstance!).Descriptor;
            services.Remove(owned);
            services.Remove(descriptor);
        }
        foreach (var item in plan.OrderBy(p => p.Phase))
        {
            if (item.Item != null) RegisterContribution(services, item.Item, item.Lifetime, lifetimeOverride: item.Lifetime);
            else RegisterStreamBehavior(services, item.Service, item.Implementation);
        }
        services.AddSingleton(current);
        return true;
    }

    internal static void RegisterStreamBehavior(IServiceCollection services, Type service, Type implementation)
    {
        var descriptor = ServiceDescriptor.Transient(service, implementation);
        services.TryAddEnumerable(descriptor);
        if (services.Any(d => ReferenceEquals(d, descriptor))) services.AddSingleton(new StreamDescriptorMarker(descriptor));
    }

    private sealed record StreamConfigurationMarker((Type ServiceType, Type ImplementationType)[] Closed, Type[] Open);
    private sealed record StreamDescriptorMarker(ServiceDescriptor Descriptor);

    private sealed record ConfigurationMarker((Type ServiceType,Type ImplementationType,int Order,int RegistrationIndex,ServiceLifetime Lifetime)[] Behaviors,Type[] Pre,Type[] Post);

    internal static GeneratedMediatorComposition CreateComposition(IServiceProvider provider) => new(
        provider.GetServices<IGeneratedMediatorRegistry>(), provider.GetServices<StrictMarker>().Any(m => m.Strict));
    private sealed record StandardMarker(Type Type,string Id);
    private sealed record StrictMarker(bool Strict,ServiceLifetime Lifetime);
    private sealed record FactoryMarker(Type Service, Type Implementation, ServiceLifetime Lifetime, ServiceDescriptor Descriptor, GeneratedMediatorContribution Contribution, int? Occurrence);
    private sealed record RegistryMarker(IGeneratedMediatorRegistry Registry, ServiceLifetime Lifetime, bool Strict);
}

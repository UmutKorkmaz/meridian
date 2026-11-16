using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Publishing;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Mediator.Extensions;

/// <summary>
/// Extension methods for registering Meridian Mediator services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Meridian Mediator services including the mediator, handlers, pipeline behaviors,
    /// and notification publishers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMeridianMediator(
        this IServiceCollection services,
        Action<MeridianMediatorConfiguration> configure)
    {
        var config = new MeridianMediatorConfiguration();
        configure(config);

        Generated.GeneratedMediatorRegistration.RegisterRegistries(services, config);

        services.TryAddSingleton(Generated.GeneratedMediatorRegistration.CreateComposition);

        // Provider-owned, opt-in metrics: one immutable component per root provider (disabled unless opted in).
        if (config.Metrics is not null)
        {
            services.TryAddSingleton(config.Metrics);
        }

        services.TryAddSingleton(CreateMetrics);

        // Register the mediator itself
        services.TryAddTransient<IMediator>(sp =>
        {
            var publisher = ResolvePublisher(sp, config);
            var telemetryOptions = sp.GetService<MediatorTelemetryOptions>() ?? MediatorTelemetryOptions.Default;
            return new Mediator(
                sp,
                publisher,
                telemetryOptions,
                sp.GetRequiredService<Generated.GeneratedMediatorComposition>(),
                sp.GetRequiredService<MediatorMetrics>());
        });
        services.TryAddTransient<ISender>(sp => sp.GetRequiredService<IMediator>());
        services.TryAddTransient<IPublisher>(sp => sp.GetRequiredService<IMediator>());
        services.TryAddTransient<IStreamSender>(sp => sp.GetRequiredService<IMediator>());

        // Register notification publisher if configured as a type
        if (config.NotificationPublisherType != null)
        {
            services.TryAddTransient(typeof(INotificationPublisher), config.NotificationPublisherType);
        }

        // Scan assemblies for handlers
        foreach (var assembly in config.AssembliesToScan.Distinct())
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
                throw DynamicRegistrationGuard.Unsupported("RegisterServicesFromAssembly");
            RegisterHandlersFromAssembly(services, assembly, config);
        }

        var generatedPipeline = Generated.GeneratedMediatorRegistration.NormalizePipeline(services,config);

        // Register closed and open pipeline behaviors in a single ordered sequence.
        if (!generatedPipeline) foreach (var (serviceType, implementationType, order, _, lifetime) in config.PipelineBehaviorRegistrations
                     .OrderBy(behavior => behavior.Order)
                     .ThenBy(behavior => behavior.RegistrationIndex))
        {
            TryAddPipelineBehavior(services, serviceType, implementationType, order, lifetime);
        }

        // Normalize finite stream contributions with the legacy closed-before-open phases.
        var generatedStreams = Generated.GeneratedMediatorRegistration.NormalizeStreams(services, config);
        if (!generatedStreams) foreach (var (serviceType, implType) in config.ClosedStreamBehaviors)
        {
            Generated.GeneratedMediatorRegistration.RegisterStreamBehavior(services, serviceType, implType);
        }

        if (!generatedStreams) foreach (var openStreamBehavior in config.OpenStreamBehaviors)
        {
            Generated.GeneratedMediatorRegistration.RegisterStreamBehavior(services, typeof(IStreamPipelineBehavior<,>), openStreamBehavior);
        }

        // Register pre-processors and post-processors
        if (!generatedPipeline) foreach (var preProcessorType in config.PreProcessorTypes)
        {
            RegisterOpenOrClosedGeneric(services, typeof(IRequestPreProcessor<>), preProcessorType);
        }

        if (!generatedPipeline) foreach (var postProcessorType in config.PostProcessorTypes)
        {
            RegisterOpenOrClosedGeneric(services, typeof(IRequestPostProcessor<,>), postProcessorType);
        }

        // Register built-in pre/post processor behaviors if needed
        if (!generatedPipeline && config.RegisterPreProcessorBehavior)
        {
            TryAddPipelineBehavior(services, typeof(IPipelineBehavior<,>), typeof(RequestPreProcessorBehavior<,>), 0);
        }

        if (!generatedPipeline && config.RegisterPostProcessorBehavior)
        {
            TryAddPipelineBehavior(services, typeof(IPipelineBehavior<,>), typeof(RequestPostProcessorBehavior<,>), 0);
        }

        // Snapshot the closed message types known now. Repeated calls each contribute their own snapshot and the
        // provider merges them before admission; types registered after this call use the overflow label.
        services.AddSingleton(new MediatorMetricsKnownTypes(CollectClosedMessageTypes(services)));

        return services;
    }

    private static MediatorMetrics CreateMetrics(IServiceProvider sp)
    {
        var options = sp.GetService<MediatorMetricsOptions>() ?? MediatorMetricsOptions.Default;
        if (!options.Enabled)
        {
            return new MediatorMetrics(options, Array.Empty<Type>());
        }

        var known = new List<Type>();
        foreach (var contribution in sp.GetServices<MediatorMetricsKnownTypes>())
        {
            known.AddRange(contribution.Types);
        }

        foreach (var registry in sp.GetServices<Generated.IGeneratedMediatorRegistry>())
        {
            known.AddRange(registry.RequestTypes);
            known.AddRange(registry.NotificationTypes);
            known.AddRange(registry.StreamTypes);
        }

        return new MediatorMetrics(options, known, sp.GetService<IMeterFactory>());
    }

    private static List<Type> CollectClosedMessageTypes(IServiceCollection services)
    {
        var types = new List<Type>();
        foreach (var descriptor in services)
        {
            var serviceType = descriptor.ServiceType;
            if (!serviceType.IsGenericType || serviceType.ContainsGenericParameters)
            {
                continue;
            }

            var definition = serviceType.GetGenericTypeDefinition();
            if (definition == typeof(IRequestHandler<,>) || definition == typeof(IRequestHandler<>) ||
                definition == typeof(INotificationHandler<>) || definition == typeof(IStreamRequestHandler<,>))
            {
                types.Add(serviceType.GetGenericArguments()[0]);
            }
        }

        return types;
    }

    /// <summary>Immutable per-call snapshot of the closed message types known to the registration.</summary>
    internal sealed class MediatorMetricsKnownTypes
    {
        internal MediatorMetricsKnownTypes(IReadOnlyList<Type> types) => Types = types;

        internal IReadOnlyList<Type> Types { get; }
    }

    private static INotificationPublisher ResolvePublisher(IServiceProvider sp, MeridianMediatorConfiguration config)
    {
        if (config.NotificationPublisher != null)
        {
            return config.NotificationPublisher;
        }

        if (config.NotificationPublisherType != null)
        {
            return sp.GetRequiredService<INotificationPublisher>();
        }

        return new ForeachAwaitPublisher();
    }

    [RequiresUnreferencedCode("Assembly scanning reads every type of the assembly by reflection.")]
    [RequiresDynamicCode("Assembly scanning registers reflection-activated implementations.")]
    private static void RegisterHandlersFromAssembly(
        IServiceCollection services,
        Assembly assembly,
        MeridianMediatorConfiguration config)
    {
        var lifetime = config.Lifetime;
        var handlerInterfaceTypes = new[]
        {
            typeof(IRequestHandler<,>),
            typeof(IRequestHandler<>),
            typeof(INotificationHandler<>),
            typeof(IStreamRequestHandler<,>),
        };

        var pipelineInterfaceTypes = new[]
        {
            typeof(IPipelineBehavior<,>),
            typeof(IStreamPipelineBehavior<,>),
            typeof(IRequestPreProcessor<>),
            typeof(IRequestPostProcessor<,>),
            typeof(IRequestExceptionHandler<,,>),
            typeof(IRequestExceptionAction<,>),
        };

        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            // Skip open generic type definitions — they cannot be resolved by MSDI's
            // built-in open generic support for complex nested generic mappings.
            // Projects with open generic handlers (e.g. workflow / saga / batch
            // request patterns) must register closed constructions manually using
            // MakeGenericType at startup. This matches MediatR 12.x behavior.
            if (type.IsGenericTypeDefinition)
                continue;

            foreach (var interfaceType in type.GetInterfaces())
            {
                if (!interfaceType.IsGenericType) continue;

                // Also skip interfaces with unresolved type parameters (partially-closed generics)
                // which occur when a non-generic-definition type inherits from a generic base
                // that leaves type parameters open.
                if (interfaceType.ContainsGenericParameters)
                    continue;

                var genericDef = interfaceType.GetGenericTypeDefinition();

                // Register request handlers
                if (handlerInterfaceTypes.Contains(genericDef))
                {
                    var descriptor = new ServiceDescriptor(interfaceType, type, lifetime);
                    if (genericDef == typeof(INotificationHandler<>))
                    {
                        if (!Generated.GeneratedMediatorRegistration.HasGeneratedRegistration(services, interfaceType, type))
                            services.TryAddEnumerable(descriptor);
                    }
                    else
                    {
                        services.TryAdd(descriptor);
                    }
                }

                // Register pipeline-related types
                if (pipelineInterfaceTypes.Contains(genericDef))
                {
                    if (genericDef == typeof(IPipelineBehavior<,>))
                    {
                        config.RegisterScannedClosedBehavior(interfaceType, type, lifetime);
                    }
                    else
                    {
                        services.TryAddEnumerable(new ServiceDescriptor(interfaceType, type, lifetime));
                    }
                }
            }
        }
    }

    internal static void RegisterOpenOrClosedGeneric(IServiceCollection services, Type openServiceType, Type implementationType)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw DynamicRegistrationGuard.Unsupported("Registering a request processor outside a generated registry");
        if (implementationType.IsGenericTypeDefinition)
        {
            services.TryAddEnumerable(ServiceDescriptor.Transient(openServiceType, implementationType));
        }
        else
        {
            // Find the matching closed generic interface
            foreach (var iface in implementationType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openServiceType)
                {
                    services.TryAddEnumerable(ServiceDescriptor.Transient(iface, implementationType));
                    return;
                }
            }

            // Fallback: register directly
            services.TryAddTransient(implementationType);
        }
    }

    internal static void TryAddPipelineBehavior(
        IServiceCollection services,
        Type serviceType,
        Type implementationType,
        int order,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw DynamicRegistrationGuard.Unsupported("Registering a pipeline behavior outside a generated registry");
        var permitDistinctOrders = serviceType == typeof(IPipelineBehavior<,>) && implementationType.IsGenericTypeDefinition;
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(PipelineBehaviorRegistration) &&
                descriptor.ImplementationInstance is PipelineBehaviorRegistration registration &&
                registration.ServiceType == serviceType &&
                registration.ImplementationType == implementationType &&
                (!permitDistinctOrders || registration.Order == order)))
        {
            return;
        }

        var descriptor = new ServiceDescriptor(serviceType, implementationType, lifetime);
        services.Add(descriptor);
        services.AddSingleton(new PipelineBehaviorRegistration(serviceType, implementationType, order, descriptor));
    }

    internal static void RemoveOwnedPipelineDescriptors(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ImplementationInstance is PipelineBehaviorRegistration).ToArray())
        {
            services.Remove(((PipelineBehaviorRegistration)descriptor.ImplementationInstance!).Descriptor);
            services.Remove(descriptor);
        }
    }
    private sealed record PipelineBehaviorRegistration(Type ServiceType, Type ImplementationType, int Order, ServiceDescriptor Descriptor);
}

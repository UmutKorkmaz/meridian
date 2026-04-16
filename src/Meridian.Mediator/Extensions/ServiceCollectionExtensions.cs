using System.Reflection;
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

        // Register the mediator itself
        services.TryAddTransient<IMediator>(sp =>
        {
            var publisher = ResolvePublisher(sp, config);
            var telemetryOptions = sp.GetService<MediatorTelemetryOptions>() ?? MediatorTelemetryOptions.Default;
            return new Mediator(sp, publisher, telemetryOptions);
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
            RegisterHandlersFromAssembly(services, assembly, config.Lifetime);
        }

        // Register closed pipeline behaviors (sorted by order, then registration sequence)
        foreach (var (serviceType, implType, _) in config.ClosedBehaviors.OrderBy(b => b.Order))
        {
            services.AddTransient(serviceType, implType);
        }

        // Register open pipeline behaviors (sorted by order, then registration sequence)
        foreach (var (behaviorType, _) in config.OpenBehaviors.OrderBy(b => b.Order))
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), behaviorType);
        }

        // Register stream behaviors
        foreach (var (serviceType, implType) in config.ClosedStreamBehaviors)
        {
            services.AddTransient(serviceType, implType);
        }

        foreach (var openStreamBehavior in config.OpenStreamBehaviors)
        {
            services.AddTransient(typeof(IStreamPipelineBehavior<,>), openStreamBehavior);
        }

        // Register pre-processors and post-processors
        foreach (var preProcessorType in config.PreProcessorTypes)
        {
            RegisterOpenOrClosedGeneric(services, typeof(IRequestPreProcessor<>), preProcessorType);
        }

        foreach (var postProcessorType in config.PostProcessorTypes)
        {
            RegisterOpenOrClosedGeneric(services, typeof(IRequestPostProcessor<,>), postProcessorType);
        }

        // Register built-in pre/post processor behaviors if needed
        if (config.RegisterPreProcessorBehavior)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestPreProcessorBehavior<,>));
        }

        if (config.RegisterPostProcessorBehavior)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestPostProcessorBehavior<,>));
        }

        return services;
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

    private static void RegisterHandlersFromAssembly(IServiceCollection services, Assembly assembly, ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
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
                    services.Add(new ServiceDescriptor(interfaceType, type, lifetime));
                }
            }
        }
    }

    private static void RegisterOpenOrClosedGeneric(IServiceCollection services, Type openServiceType, Type implementationType)
    {
        if (implementationType.IsGenericTypeDefinition)
        {
            services.AddTransient(openServiceType, implementationType);
        }
        else
        {
            // Find the matching closed generic interface
            foreach (var iface in implementationType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openServiceType)
                {
                    services.AddTransient(iface, implementationType);
                    return;
                }
            }

            // Fallback: register directly
            services.AddTransient(implementationType);
        }
    }
}

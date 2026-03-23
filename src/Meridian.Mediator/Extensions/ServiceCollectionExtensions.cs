using System.Reflection;
using System.Text;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Publishing;
using Meridian.Mediator.Streaming;
using Meridian.Mediator.Wrappers;
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
            return new Mediator(sp, publisher);
        });
        services.TryAddTransient<ISender>(sp => sp.GetRequiredService<IMediator>());
        services.TryAddTransient<IPublisher>(sp => sp.GetRequiredService<IMediator>());

        // Register notification publisher if configured as a type
        if (config.NotificationPublisherType != null)
        {
            services.TryAddTransient(typeof(INotificationPublisher), config.NotificationPublisherType);
        }

        // Scan assemblies for handlers
        var handlerAssemblies = config.AssembliesToScan.Distinct().ToArray();
        var openRequestHandlerRegistry = new OpenRequestHandlerRegistry();
        foreach (var assembly in handlerAssemblies)
        {
            RegisterHandlersFromAssembly(services, assembly, openRequestHandlerRegistry, config.Lifetime);
        }
        if (openRequestHandlerRegistry.HasEntries)
        {
            services.AddSingleton(openRequestHandlerRegistry);
            services.AddTransient(typeof(IRequestHandler<,>), typeof(OpenRequestHandlerAdapter<,>));
        }

        // Register FluentValidation adapters only when explicitly requested.
        if (config.FluentValidationAssembliesToScan.Count > 0)
        {
            var fluentValidationAssemblies = config.FluentValidationAssembliesToScan.Distinct().ToArray();
            RegisterFluentValidationAdapters(services, fluentValidationAssemblies);
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

        if (config.EnableHandlerDiagnostics)
        {
            ValidateHandlersAtStartup(services, handlerAssemblies, config.ThrowOnStartupHandlerValidationFailure);
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

    private static void RegisterHandlersFromAssembly(
        IServiceCollection services,
        Assembly assembly,
        OpenRequestHandlerRegistry openRequestHandlerRegistry,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
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
            foreach (var interfaceType in type.GetInterfaces())
            {
                if (!interfaceType.IsGenericType) continue;

                var genericDef = interfaceType.GetGenericTypeDefinition();

                // Register request handlers
                if (handlerInterfaceTypes.Contains(genericDef))
                {
                    if (genericDef == typeof(IRequestHandler<,>) &&
                        interfaceType.ContainsGenericParameters &&
                        type.IsGenericTypeDefinition)
                    {
                        openRequestHandlerRegistry.Add(type, interfaceType);
                        continue;
                    }

                    if (!interfaceType.ContainsGenericParameters)
                    {
                        RegisterRequestHandler(services, interfaceType, type, genericDef, lifetime);
                    }

                    continue;
                }

                if (interfaceType.ContainsGenericParameters)
                {
                    continue;
                }

                // Register pipeline-related types
                if (pipelineInterfaceTypes.Contains(genericDef))
                {
                    services.Add(new ServiceDescriptor(interfaceType, type, lifetime));
                }
            }
        }
    }

    private static void RegisterRequestHandler(
        IServiceCollection services,
        Type serviceType,
        Type implementationType,
        Type handlerInterfaceType,
        ServiceLifetime lifetime)
    {
        var descriptor = new ServiceDescriptor(serviceType, implementationType, lifetime);
        if (handlerInterfaceType == typeof(INotificationHandler<>))
        {
            services.TryAddEnumerable(descriptor);
            return;
        }

        services.TryAdd(descriptor);
    }

    private static void RegisterFluentValidationAdapters(IServiceCollection services, IEnumerable<Assembly> assemblies)
    {
        var fluentValidatorInterface = typeof(global::FluentValidation.IValidator<>);
        var hasFluentValidators = false;

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
            {
                var hasValidatorInterface = false;
                foreach (var iface in type.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == fluentValidatorInterface)
                    {
                        services.AddTransient(iface, type);
                        hasValidatorInterface = true;
                    }
                }

                if (hasValidatorInterface)
                {
                    hasFluentValidators = true;
                }
            }
        }

        if (hasFluentValidators)
        {
            services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IValidator<>), typeof(FluentValidationAdapter<>)));
        }
    }

    private static void ValidateHandlersAtStartup(
        IServiceCollection services,
        Assembly[] handlerAssemblies,
        bool throwOnFailure)
    {
        if (handlerAssemblies.Length == 0)
        {
            return;
        }

        using var provider = services.BuildServiceProvider();
        var hasIssues = provider.TryGetHandlerRegistrationDiagnostics(
            out var errors,
            out var warnings,
            handlerAssemblies);

        if (!hasIssues)
        {
            return;
        }

        var message = BuildDiagnosticMessage(errors, warnings);

        if (errors.Count > 0)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException(message);
            }

            System.Diagnostics.Trace.WriteLine(message);
            return;
        }

        System.Diagnostics.Trace.WriteLine(message);
    }

    private static string BuildDiagnosticMessage(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        var message = new StringBuilder();

        if (errors.Count > 0)
        {
            message.Append($"Meridian Mediator handler registration diagnostics found {errors.Count} error(s):\n");
            foreach (var (entry, index) in errors.Select((item, index) => (item, index + 1)))
            {
                message.AppendLine($"  {index}. {entry}");
            }
        }

        if (warnings.Count > 0)
        {
            if (message.Length == 0)
            {
                message.Append($"Meridian Mediator handler registration diagnostics found {warnings.Count} warning(s):\n");
            }
            else
            {
                message.Append($"\nAdditionally, {warnings.Count} warning(s):\n");
            }

            foreach (var (entry, index) in warnings.Select((item, index) => (item, index + 1)))
            {
                message.AppendLine($"  {index}. {entry}");
            }
        }

        return message.ToString().TrimEnd();
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

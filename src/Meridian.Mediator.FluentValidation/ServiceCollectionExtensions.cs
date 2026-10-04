using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Mediator.FluentValidation;

/// <summary>Registers FluentValidation adapters for Meridian validation pipelines.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Scans public validators in the supplied assemblies and adapts all currently registered
    /// closed typed FluentValidation validator services. Repeated calls preserve existing registrations.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">Optional assemblies to scan for public validators with scoped lifetimes.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMeridianFluentValidation(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Any(assembly => assembly is null))
        {
            throw new ArgumentException("Assemblies must not contain null entries.", nameof(assemblies));
        }

        foreach (var assembly in assemblies.Distinct())
        {
            services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped);
        }

        var requestTypes = services.Select(descriptor => descriptor.ServiceType)
            .Where(type => type.IsGenericType && !type.ContainsGenericParameters
                && type.GetGenericTypeDefinition() == typeof(global::FluentValidation.IValidator<>))
            .Select(type => type.GetGenericArguments()[0])
            .Distinct()
            .ToArray();

        foreach (var requestType in requestTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped(
                typeof(Behaviors.IValidator<>).MakeGenericType(requestType),
                typeof(FluentValidationAdapter<>).MakeGenericType(requestType)));
        }

        return services;
    }
}

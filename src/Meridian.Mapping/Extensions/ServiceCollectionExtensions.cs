using System.Reflection;
using Meridian.Mapping.Converters;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Extensions;

/// <summary>
/// Extension methods for registering Meridian.Mapping services with
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Meridian.Mapping services by scanning assemblies for <see cref="Profile"/> subclasses.
    /// Registers <see cref="IConfigurationProvider"/> as Singleton and <see cref="IMapper"/> as Scoped.
    /// Also scans for and registers <see cref="ITypeConverter{TSource, TDestination}"/>,
    /// <see cref="IValueResolver{TSource, TDestination, TDestMember}"/>, and
    /// <see cref="IValueConverter{TSourceMember, TDestMember}"/> as Transient.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies to scan for profiles and converters.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMeridianMapping(this IServiceCollection services, params Assembly[] assemblies)
    {
        var configExpression = new MapperConfigurationExpression();

        foreach (var assembly in assemblies)
        {
            // Discover and add profiles
            var profileTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(Profile).IsAssignableFrom(t));

            foreach (var profileType in profileTypes)
            {
                var profile = (Profile)Activator.CreateInstance(profileType)!;
                configExpression.AddProfile(profile);
            }

            // Register converters and resolvers as transient
            RegisterConverters(services, assembly);
        }

        var config = new MapperConfiguration(configExpression);

        services.AddSingleton<IConfigurationProvider>(config);
        services.AddScoped<IMapper>(sp => new Mapper(config, sp));

        return services;
    }

    /// <summary>
    /// Adds Meridian.Mapping services using a configuration action.
    /// Registers <see cref="IConfigurationProvider"/> as Singleton and <see cref="IMapper"/> as Scoped.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure mappings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMeridianMapping(this IServiceCollection services, Action<IMapperConfigurationExpression> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var config = new MapperConfiguration(configure);

        services.AddSingleton<IConfigurationProvider>(config);
        services.AddScoped<IMapper>(sp => new Mapper(config, sp));

        return services;
    }

    private static void RegisterConverters(IServiceCollection services, Assembly assembly)
    {
        var converterInterfaces = new[]
        {
            typeof(ITypeConverter<,>),
            typeof(IValueResolver<,,>),
            typeof(IMemberValueResolver<,,,>),
            typeof(IValueConverter<,>)
        };

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract)
                continue;

            foreach (var iface in type.GetInterfaces())
            {
                if (!iface.IsGenericType)
                    continue;

                var genericDef = iface.GetGenericTypeDefinition();
                if (converterInterfaces.Contains(genericDef))
                {
                    services.AddTransient(type, type);
                    services.AddTransient(iface, type);
                    break; // One registration per implementation type
                }
            }
        }
    }
}

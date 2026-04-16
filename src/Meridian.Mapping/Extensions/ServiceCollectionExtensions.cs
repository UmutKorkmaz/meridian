using System.Linq;
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
            configExpression.AddMaps(assembly);

            // Register converters and resolvers as transient
            RegisterConverters(services, assembly);
        }

        var config = new MapperConfiguration(configExpression);

        services.AddSingleton<IConfigurationProvider>(config);
        services.AddScoped<IMapper>(sp => new Mapper(config, sp));

        return services;
    }

    /// <summary>
    /// Adds Meridian.Mapping services using marker types to discover assemblies.
    /// </summary>
    public static IServiceCollection AddMeridianMapping(this IServiceCollection services, params Type[] markerTypes)
    {
        ArgumentNullException.ThrowIfNull(markerTypes);
        return AddMeridianMapping(services, markerTypes.Select(static t => t.Assembly).Distinct().ToArray());
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

    /// <summary>
    /// AutoMapper-style alias for <see cref="AddMeridianMapping(IServiceCollection,Assembly[])"/>.
    /// </summary>
    public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Assembly[] assemblies)
    {
        return AddMeridianMapping(services, assemblies);
    }

    /// <summary>
    /// AutoMapper-style alias for <see cref="AddMeridianMapping(IServiceCollection,Type[])"/>.
    /// </summary>
    public static IServiceCollection AddAutoMapper(this IServiceCollection services, params Type[] markerTypes)
    {
        return AddMeridianMapping(services, markerTypes);
    }

    /// <summary>
    /// AutoMapper-style alias for <see cref="AddMeridianMapping(IServiceCollection,Action{IMapperConfigurationExpression})"/>.
    /// </summary>
    public static IServiceCollection AddAutoMapper(this IServiceCollection services, Action<IMapperConfigurationExpression> configure)
    {
        return AddMeridianMapping(services, configure);
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

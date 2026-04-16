using System.Linq;
using System.Reflection;
using Meridian.Mapping.Configuration;

namespace Meridian.Mapping;

/// <summary>
/// Implementation of <see cref="IMapperConfigurationExpression"/>. Collects all
/// mapping expressions and profiles during configuration, before they are
/// compiled into <see cref="Execution.TypeMap"/> instances.
/// </summary>
public class MapperConfigurationExpression : IMapperConfigurationExpression
{
    internal Dictionary<(Type Source, Type Dest), object> MappingExpressions { get; } = new();
    internal Dictionary<(Type Source, Type Dest), object> OpenGenericMappings { get; } = new();

    /// <inheritdoc />
    public bool AllowNullCollections { get; set; }

    /// <inheritdoc />
    public bool AllowNullDestinationValues { get; set; } = true;

    /// <inheritdoc />
    public int DefaultMaxDepth { get; set; } = 64;

    /// <inheritdoc />
    public int DefaultMaxCollectionItems { get; set; } = 10_000;

    /// <inheritdoc />
    public ValueTransformerCollection ValueTransformers { get; } = new();

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        var key = (typeof(TSource), typeof(TDestination));
        var expression = new MappingExpression<TSource, TDestination>(RegisterReverseMap);
        MappingExpressions[key] = expression;
        return expression;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> CreateProjection<TSource, TDestination>()
    {
        return CreateMap<TSource, TDestination>();
    }

    /// <inheritdoc />
    public void CreateMap(Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (sourceType.IsGenericTypeDefinition || destinationType.IsGenericTypeDefinition)
        {
            // Store as open generic mapping for on-demand compilation
            OpenGenericMappings[(sourceType, destinationType)] = new object();
        }
        else
        {
            // Create a closed MappingExpression via reflection
            var exprType = typeof(MappingExpression<,>).MakeGenericType(sourceType, destinationType);
            var expression = Activator.CreateInstance(exprType, new Action<Type, Type, object>(RegisterReverseMap))!;
            MappingExpressions[(sourceType, destinationType)] = expression;
        }
    }

    /// <inheritdoc />
    public void AddProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ApplyTo(this);
    }

    /// <inheritdoc />
    public void AddProfile<TProfile>() where TProfile : Profile, new()
    {
        AddProfile(new TProfile());
    }

    /// <inheritdoc />
    public void AddProfiles(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var profileTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(Profile).IsAssignableFrom(t));

            foreach (var profileType in profileTypes)
            {
                var profile = (Profile)Activator.CreateInstance(profileType)!;
                AddProfile(profile);
            }
        }
    }

    /// <inheritdoc />
    public void AddMaps(params Assembly[] assemblies)
    {
        AddProfiles(assemblies);
    }

    /// <inheritdoc />
    public void AddMaps(params Type[] markerTypes)
    {
        ArgumentNullException.ThrowIfNull(markerTypes);
        AddMaps(markerTypes.Select(static t => t.Assembly).Distinct().ToArray());
    }

    /// <inheritdoc />
    public void AddMaps(params string[] assemblyNames)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);

        var assemblies = assemblyNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(Assembly.Load)
            .ToArray();

        AddMaps(assemblies);
    }

    internal void RegisterReverseMap(Type sourceType, Type destType, object expression)
    {
        var key = (sourceType, destType);
        if (!MappingExpressions.ContainsKey(key))
        {
            MappingExpressions[key] = expression;
        }
    }
}

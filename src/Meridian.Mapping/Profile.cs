using Meridian.Mapping.Configuration;

namespace Meridian.Mapping;

/// <summary>
/// Base class for organizing mapping configurations. Derive from this class
/// and configure mappings in the constructor using <see cref="CreateMap{TSource, TDestination}"/>.
/// Profiles are discovered automatically when scanning assemblies.
/// </summary>
/// <example>
/// <code>
/// public class MyProfile : Profile
/// {
///     public MyProfile()
///     {
///         CreateMap&lt;Source, Dest&gt;()
///             .ForMember(d =&gt; d.Name, opt =&gt; opt.MapFrom(s =&gt; s.FullName));
///     }
/// }
/// </code>
/// </example>
public abstract class Profile
{
    internal Dictionary<(Type Source, Type Dest), object> MappingExpressions { get; } = new();
    internal Dictionary<(Type Source, Type Dest), object> OpenGenericMappings { get; } = new();

    /// <summary>
    /// Gets or sets whether null source collections map to null instead of empty.
    /// Default is false.
    /// </summary>
    public bool AllowNullCollections { get; set; }

    /// <summary>
    /// Gets or sets whether null destination values are allowed.
    /// Default is true.
    /// </summary>
    public bool AllowNullDestinationValues { get; set; } = true;

    /// <summary>
    /// Creates a mapping between <typeparamref name="TSource"/> and <typeparamref name="TDestination"/>.
    /// Call this method in the constructor of your derived profile class.
    /// </summary>
    /// <typeparam name="TSource">The source type.</typeparam>
    /// <typeparam name="TDestination">The destination type.</typeparam>
    /// <returns>A mapping expression for further configuration.</returns>
    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        var key = (typeof(TSource), typeof(TDestination));
        var expression = new MappingExpression<TSource, TDestination>(RegisterReverseMap);
        MappingExpressions[key] = expression;
        return expression;
    }

    /// <summary>
    /// Creates a mapping between two types specified at runtime, supporting open generic types.
    /// </summary>
    /// <param name="sourceType">The source type (may be an open generic type definition).</param>
    /// <param name="destinationType">The destination type (may be an open generic type definition).</param>
    protected void CreateMap(Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (sourceType.IsGenericTypeDefinition || destinationType.IsGenericTypeDefinition)
        {
            OpenGenericMappings[(sourceType, destinationType)] = new object();
        }
        else
        {
            var exprType = typeof(MappingExpression<,>).MakeGenericType(sourceType, destinationType);
            var expression = Activator.CreateInstance(exprType, new Action<Type, Type, object>(RegisterReverseMap))!;
            MappingExpressions[(sourceType, destinationType)] = expression;
        }
    }

    private void RegisterReverseMap(Type sourceType, Type destType, object expression)
    {
        var key = (sourceType, destType);
        if (!MappingExpressions.ContainsKey(key))
        {
            MappingExpressions[key] = expression;
        }
    }

    /// <summary>
    /// Applies this profile's collected mappings to the given configuration expression.
    /// Called internally when the profile is added to a configuration.
    /// </summary>
    /// <param name="configExpression">The configuration expression to merge mappings into.</param>
    internal void ApplyTo(MapperConfigurationExpression configExpression)
    {
        configExpression.AllowNullCollections = AllowNullCollections;
        configExpression.AllowNullDestinationValues = AllowNullDestinationValues;

        foreach (var (key, expression) in MappingExpressions)
        {
            if (!configExpression.MappingExpressions.ContainsKey(key))
            {
                configExpression.MappingExpressions[key] = expression;
            }
        }

        foreach (var (key, value) in OpenGenericMappings)
        {
            if (!configExpression.OpenGenericMappings.ContainsKey(key))
            {
                configExpression.OpenGenericMappings[key] = value;
            }
        }
    }
}

using System.Linq.Expressions;
using Meridian.Mapping.Configuration;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Queryable;

namespace Meridian.Mapping;

/// <summary>
/// Default implementation of <see cref="IMapper"/>. Delegates to the
/// <see cref="MappingEngine"/> for actual mapping execution.
/// Registered as Scoped in DI to support per-request service resolution.
/// </summary>
public class Mapper : IMapper
{
    private readonly MappingEngine _engine;
    private readonly IServiceProvider? _serviceProvider;

    /// <inheritdoc />
    public IConfigurationProvider ConfigurationProvider { get; }

    /// <summary>
    /// Creates a mapper with no DI support.
    /// </summary>
    /// <param name="configurationProvider">The configuration provider.</param>
    public Mapper(IConfigurationProvider configurationProvider)
    {
        ConfigurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _engine = new MappingEngine(configurationProvider);
    }

    /// <summary>
    /// Creates a mapper with no DI support and optional value transformers.
    /// </summary>
    /// <param name="configurationProvider">The configuration provider.</param>
    /// <param name="valueTransformers">Optional global value transformers.</param>
    public Mapper(IConfigurationProvider configurationProvider, ValueTransformerCollection? valueTransformers)
    {
        ConfigurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _engine = new MappingEngine(configurationProvider, valueTransformers);
    }

    /// <summary>
    /// Creates a mapper with DI support for resolving converters and resolvers.
    /// </summary>
    /// <param name="configurationProvider">The configuration provider.</param>
    /// <param name="serviceProvider">The service provider for DI resolution.</param>
    public Mapper(IConfigurationProvider configurationProvider, IServiceProvider serviceProvider)
        : this(configurationProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Creates a mapper with DI support and optional value transformers.
    /// </summary>
    /// <param name="configurationProvider">The configuration provider.</param>
    /// <param name="serviceProvider">The service provider for DI resolution.</param>
    /// <param name="valueTransformers">Optional global value transformers.</param>
    public Mapper(IConfigurationProvider configurationProvider, IServiceProvider serviceProvider, ValueTransformerCollection? valueTransformers)
        : this(configurationProvider, valueTransformers)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public TDestination Map<TDestination>(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var context = CreateContext();
        var result = _engine.Map(source, source.GetType(), typeof(TDestination), context);
        return (TDestination)result!;
    }

    /// <inheritdoc />
    public TDestination Map<TSource, TDestination>(TSource source)
    {
        if (source == null)
        {
            return default!;
        }

        var context = CreateContext();
        var result = _engine.Map(source, typeof(TSource), typeof(TDestination), context);
        return (TDestination)result!;
    }

    /// <inheritdoc />
    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var context = CreateContext();
        var result = _engine.MapToExisting(source!, destination!, typeof(TSource), typeof(TDestination), context);
        return (TDestination)result;
    }

    /// <inheritdoc />
    public object Map(object source, Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        var context = CreateContext();
        return _engine.Map(source, sourceType, destinationType, context)!;
    }

    /// <inheritdoc />
    public IQueryable<TDestination> ProjectTo<TDestination>(
        IQueryable source,
        params Expression<Func<TDestination, object>>[]? membersToExpand)
    {
        return QueryableExtensions.ProjectTo(source, ConfigurationProvider, membersToExpand);
    }

    private ResolutionContext CreateContext()
    {
        return new ResolutionContext(this, depth: 0, _serviceProvider);
    }
}

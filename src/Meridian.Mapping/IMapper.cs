using System.Linq.Expressions;

namespace Meridian.Mapping;

/// <summary>
/// Main entry point for performing object-to-object mappings.
/// Obtain instances via DI (registered as Scoped) or from
/// <see cref="MapperConfiguration.CreateMapper()"/>.
/// </summary>
public interface IMapper
{
    /// <summary>
    /// Maps the source object to a new instance of <typeparamref name="TDestination"/>.
    /// The source type is inferred from the runtime type of the object.
    /// </summary>
    /// <typeparam name="TDestination">The destination type.</typeparam>
    /// <param name="source">The source object.</param>
    /// <returns>A new mapped instance of <typeparamref name="TDestination"/>.</returns>
    TDestination Map<TDestination>(object source);

    /// <summary>
    /// Maps from <typeparamref name="TSource"/> to a new <typeparamref name="TDestination"/>.
    /// </summary>
    /// <typeparam name="TSource">The source type.</typeparam>
    /// <typeparam name="TDestination">The destination type.</typeparam>
    /// <param name="source">The source object.</param>
    /// <returns>A new mapped instance of <typeparamref name="TDestination"/>.</returns>
    TDestination Map<TSource, TDestination>(TSource source);

    /// <summary>
    /// Maps from <typeparamref name="TSource"/> to an existing <typeparamref name="TDestination"/> instance.
    /// Updates the destination in place rather than creating a new one.
    /// </summary>
    /// <typeparam name="TSource">The source type.</typeparam>
    /// <typeparam name="TDestination">The destination type.</typeparam>
    /// <param name="source">The source object.</param>
    /// <param name="destination">The existing destination to update.</param>
    /// <returns>The updated destination object.</returns>
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);

    /// <summary>
    /// Maps using runtime types. Useful when types are not known at compile time.
    /// </summary>
    /// <param name="source">The source object.</param>
    /// <param name="sourceType">The source type.</param>
    /// <param name="destinationType">The destination type.</param>
    /// <returns>A new mapped destination object.</returns>
    object Map(object source, Type sourceType, Type destinationType);

    /// <summary>
    /// Gets the configuration provider that created this mapper.
    /// </summary>
    IConfigurationProvider ConfigurationProvider { get; }

    /// <summary>
    /// Projects the source queryable to the destination type using the mapping
    /// configuration. Translates mapping config into expression trees for use
    /// with EF Core or other LINQ providers.
    /// </summary>
    /// <typeparam name="TDestination">The destination/DTO type to project to.</typeparam>
    /// <param name="source">The source queryable.</param>
    /// <param name="membersToExpand">Optional members to explicitly expand.</param>
    /// <returns>A projected queryable.</returns>
    IQueryable<TDestination> ProjectTo<TDestination>(
        IQueryable source,
        params Expression<Func<TDestination, object>>[]? membersToExpand);
}

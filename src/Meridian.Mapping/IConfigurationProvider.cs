using Meridian.Mapping.Execution;

namespace Meridian.Mapping;

/// <summary>
/// Provides access to the compiled mapping configuration.
/// Holds all <see cref="TypeMap"/> definitions and serves as the factory for
/// <see cref="IMapper"/> instances. Registered as Singleton in DI.
/// </summary>
public interface IConfigurationProvider
{
    /// <summary>
    /// Finds the <see cref="TypeMap"/> for the given source and destination type pair.
    /// </summary>
    /// <param name="sourceType">The source type.</param>
    /// <param name="destinationType">The destination type.</param>
    /// <returns>The type map, or null if no mapping is configured.</returns>
    TypeMap? FindTypeMap(Type sourceType, Type destinationType);

    /// <summary>
    /// Creates a new <see cref="IMapper"/> instance bound to this configuration.
    /// </summary>
    /// <returns>A new mapper instance.</returns>
    IMapper CreateMapper();

    /// <summary>
    /// Creates a new <see cref="IMapper"/> instance with a custom service provider for DI resolution.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving converters and resolvers.</param>
    /// <returns>A new mapper instance.</returns>
    IMapper CreateMapper(IServiceProvider serviceProvider);

    /// <summary>
    /// Validates that all configured mappings are complete. Throws a descriptive
    /// exception if any destination members are unmapped.
    /// </summary>
    void AssertConfigurationIsValid();

    /// <summary>
    /// Gets all configured type maps.
    /// </summary>
    IReadOnlyCollection<TypeMap> GetAllTypeMaps();

    /// <summary>
    /// Gets whether null source collections should map to null instead of empty collections.
    /// Default is false (null collections map to empty).
    /// </summary>
    bool AllowNullCollections { get; }

    /// <summary>
    /// Gets whether null values are allowed on destination members.
    /// Default is true.
    /// </summary>
    bool AllowNullDestinationValues { get; }

    /// <summary>
    /// Returns a human-readable mapping plan for the given source/destination type pair.
    /// Useful for debugging, logging, and understanding how properties are mapped.
    /// </summary>
    /// <typeparam name="TSource">The source type.</typeparam>
    /// <typeparam name="TDestination">The destination type.</typeparam>
    /// <returns>A multi-line string describing the mapping plan.</returns>
    string GetMappingPlan<TSource, TDestination>();

    /// <summary>
    /// Returns a human-readable mapping plan for the given source/destination type pair.
    /// </summary>
    /// <param name="sourceType">The source type.</param>
    /// <param name="destinationType">The destination type.</param>
    /// <returns>A multi-line string describing the mapping plan.</returns>
    string GetMappingPlan(Type sourceType, Type destinationType);
}

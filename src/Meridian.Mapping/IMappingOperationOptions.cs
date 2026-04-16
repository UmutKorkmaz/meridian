namespace Meridian.Mapping;

/// <summary>
/// Represents per-call mapping options that flow through a single mapping operation.
/// </summary>
public interface IMappingOperationOptions
{
    /// <summary>
    /// Gets a mutable dictionary of ad-hoc items available to resolvers, converters,
    /// mapping actions, and nested maps for the lifetime of the operation.
    /// </summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Gets or sets a single opaque state object for the mapping operation.
    /// </summary>
    object? State { get; set; }
}

/// <summary>
/// Generic per-call mapping options for a specific source/destination pair.
/// </summary>
public interface IMappingOperationOptions<TSource, TDestination> : IMappingOperationOptions
{
}

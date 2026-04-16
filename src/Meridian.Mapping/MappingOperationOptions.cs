namespace Meridian.Mapping;

/// <summary>
/// Default implementation of <see cref="IMappingOperationOptions"/>.
/// </summary>
public class MappingOperationOptions : IMappingOperationOptions
{
    /// <inheritdoc />
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <inheritdoc />
    public object? State { get; set; }
}

/// <summary>
/// Default generic implementation of <see cref="IMappingOperationOptions{TSource,TDestination}"/>.
/// </summary>
public sealed class MappingOperationOptions<TSource, TDestination> : MappingOperationOptions, IMappingOperationOptions<TSource, TDestination>
{
}

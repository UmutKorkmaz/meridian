namespace Meridian.Mapping;

/// <summary>
/// Thrown when a safety limit configured on the mapper is exceeded during mapping.
/// Currently raised by the collection-size cap
/// (<see cref="IMapperConfigurationExpression.DefaultMaxCollectionItems"/>).
/// Recursion-depth caps do not throw — they return the destination default instead,
/// matching AutoMapper's <c>MaxDepth</c> semantics.
/// </summary>
/// <remarks>
/// The exception is deliberately a distinct type so applications can catch it at
/// the API boundary and translate to a 400-level response rather than letting it
/// surface as a generic <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class MeridianMappingLimitException : InvalidOperationException
{
    /// <summary>The limit that was exceeded.</summary>
    public MeridianMappingLimit Limit { get; }

    /// <summary>The configured maximum value.</summary>
    public int MaxAllowed { get; }

    /// <summary>The observed value that exceeded the maximum.</summary>
    public int ObservedValue { get; }

    /// <summary>Source type being mapped when the limit was hit (may be null for context-free limits).</summary>
    public Type? SourceType { get; }

    /// <summary>Destination type being mapped when the limit was hit (may be null for context-free limits).</summary>
    public Type? DestinationType { get; }

    /// <summary>
    /// Initializes a new <see cref="MeridianMappingLimitException"/>.
    /// </summary>
    public MeridianMappingLimitException(
        MeridianMappingLimit limit,
        int maxAllowed,
        int observedValue,
        Type? sourceType,
        Type? destinationType)
        : base(BuildMessage(limit, maxAllowed, observedValue, sourceType, destinationType))
    {
        Limit = limit;
        MaxAllowed = maxAllowed;
        ObservedValue = observedValue;
        SourceType = sourceType;
        DestinationType = destinationType;
    }

    private static string BuildMessage(
        MeridianMappingLimit limit,
        int maxAllowed,
        int observedValue,
        Type? sourceType,
        Type? destinationType)
    {
        var context = sourceType is not null && destinationType is not null
            ? $" while mapping {sourceType.Name} -> {destinationType.Name}"
            : string.Empty;

        return limit switch
        {
            MeridianMappingLimit.MaxCollectionItems =>
                $"Collection size {observedValue} exceeds the configured " +
                $"{nameof(IMapperConfigurationExpression.DefaultMaxCollectionItems)} of {maxAllowed}{context}. " +
                $"Raise the cap globally via cfg.DefaultMaxCollectionItems = ... or per-map " +
                $"via CreateMap<...>().MaxCollectionItems(...).",
            _ =>
                $"Mapping limit {limit} exceeded: observed {observedValue} > allowed {maxAllowed}{context}.",
        };
    }
}

/// <summary>
/// Identifies which configured limit was exceeded.
/// </summary>
public enum MeridianMappingLimit
{
    /// <summary>
    /// The collection-size cap configured via
    /// <see cref="IMapperConfigurationExpression.DefaultMaxCollectionItems"/>.
    /// </summary>
    MaxCollectionItems = 1,
}

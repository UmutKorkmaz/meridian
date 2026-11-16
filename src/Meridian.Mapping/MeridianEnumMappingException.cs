namespace Meridian.Mapping;

/// <summary>
/// The exception thrown when a string value cannot be mapped to a defined enum value.
/// </summary>
public sealed class MeridianEnumMappingException : Exception
{
    /// <summary>
    /// Gets the source string that could not be mapped.
    /// </summary>
    public string SourceValue { get; }

    /// <summary>
    /// Gets the enum type requested by the destination member.
    /// </summary>
    public Type DestinationEnumType { get; }

    /// <summary>
    /// Gets the destination member receiving the enum value, or <c>&lt;root&gt;</c>
    /// when the enum was the root mapping destination.
    /// </summary>
    public string DestinationMember { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MeridianEnumMappingException"/> class.
    /// </summary>
    /// <param name="sourceValue">The source string that could not be mapped.</param>
    /// <param name="destinationEnumType">The requested destination enum type.</param>
    /// <param name="destinationMember">The destination member receiving the enum value.</param>
    public MeridianEnumMappingException(string sourceValue, Type destinationEnumType, string? destinationMember)
        : base(CreateMessage(sourceValue, destinationEnumType, destinationMember))
    {
        SourceValue = sourceValue;
        DestinationEnumType = destinationEnumType;
        DestinationMember = destinationMember ?? "<root>";
    }

    private static string CreateMessage(string sourceValue, Type destinationEnumType, string? destinationMember)
    {
        ArgumentNullException.ThrowIfNull(sourceValue);
        ArgumentNullException.ThrowIfNull(destinationEnumType);

        return $"Cannot map source value '{sourceValue}' to defined member(s) of enum " +
               $"'{destinationEnumType.FullName ?? destinationEnumType.Name}' for destination member " +
               $"'{destinationMember ?? "<root>"}'.";
    }
}

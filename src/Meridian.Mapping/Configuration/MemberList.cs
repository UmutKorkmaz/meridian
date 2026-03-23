namespace Meridian.Mapping.Configuration;

/// <summary>
/// Specifies which member list to use for configuration validation.
/// Controls how unmapped members are validated when calling
/// <see cref="MapperConfiguration.AssertConfigurationIsValid"/>.
/// </summary>
public enum MemberList
{
    /// <summary>
    /// Validate that all destination members are mapped.
    /// </summary>
    Destination = 0,

    /// <summary>
    /// Validate that all source members are used.
    /// </summary>
    Source = 1,

    /// <summary>
    /// Skip member validation entirely.
    /// </summary>
    None = 2
}

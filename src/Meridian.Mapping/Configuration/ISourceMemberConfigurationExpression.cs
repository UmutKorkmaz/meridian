namespace Meridian.Mapping.Configuration;

/// <summary>
/// Fluent API for configuring a source member.
/// </summary>
public interface ISourceMemberConfigurationExpression
{
    /// <summary>
    /// Excludes the source member from source-member validation.
    /// </summary>
    void DoNotValidate();
}

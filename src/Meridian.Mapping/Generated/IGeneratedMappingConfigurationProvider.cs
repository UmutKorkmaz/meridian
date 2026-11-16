namespace Meridian.Mapping;

/// <summary>Optional capability exposing explicitly registered generated resolvers.</summary>
public interface IGeneratedMappingConfigurationProvider
{
    /// <summary>Gets the registered resolvers in registration order.</summary>
    IReadOnlyList<IGeneratedMappingResolver> GeneratedResolvers { get; }
}

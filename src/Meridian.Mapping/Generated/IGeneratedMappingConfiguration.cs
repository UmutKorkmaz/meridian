namespace Meridian.Mapping;

/// <summary>Optional capability for registering generated mapping resolvers.</summary>
public interface IGeneratedMappingConfiguration
{
    /// <summary>Adds a resolver to the configuration.</summary>
    void AddGeneratedResolver(IGeneratedMappingResolver resolver);
}

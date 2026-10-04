namespace Meridian.Mapping;

/// <summary>Resolves a generated create-new mapping for an exact type pair.</summary>
public interface IGeneratedMappingResolver
{
    /// <summary>Returns a descriptor when this resolver supports the requested pair.</summary>
    bool TryGetMapping(Type sourceType, Type destinationType, out GeneratedMappingDescriptor? mapping);
}

/// <summary>Optional finite inventory used to validate resolver overlaps before mapping.</summary>
public interface IGeneratedMappingDescriptorCatalog
{
    /// <summary>Gets the descriptors advertised by this resolver.</summary>
    IReadOnlyList<GeneratedMappingDescriptor> GeneratedMappings { get; }
}

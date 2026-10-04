namespace Meridian.Mapping;

/// <summary>Opt-in integration of generated mappings with runtime configuration.</summary>
public static class GeneratedMappingExtensions
{
    /// <summary>Registers generated resolvers on a configuration supporting this optional capability.</summary>
    public static void UseGeneratedMappings(this IMapperConfigurationExpression configuration, params IGeneratedMappingResolver[] resolvers)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(resolvers);
        if (configuration is not IGeneratedMappingConfiguration generated)
            throw new NotSupportedException("This configuration does not support generated mapping registration.");
        foreach (var resolver in resolvers) ArgumentNullException.ThrowIfNull(resolver);
        foreach (var resolver in resolvers) generated.AddGeneratedResolver(resolver);
    }

    internal static GeneratedMappingDescriptor? Resolve(IReadOnlyList<IGeneratedMappingResolver> resolvers, Type source, Type destination)
    {
        GeneratedMappingDescriptor? result = null;
        foreach (var resolver in resolvers)
        {
            if (!resolver.TryGetMapping(source, destination, out var mapping)) continue;
            if (mapping is null || mapping.SourceType != source || mapping.DestinationType != destination || mapping.Map is null)
                throw new InvalidOperationException($"Invalid generated mapping descriptor for {source} -> {destination}.");
            if (result is not null)
                throw new InvalidOperationException($"Ambiguous generated mapping for {source} -> {destination}: multiple resolvers support this pair.");
            result = mapping;
        }
        return result;
    }

    internal static void Validate(IReadOnlyList<IGeneratedMappingResolver> resolvers)
    {
        var pairs = new HashSet<(Type Source, Type Destination)>();
        foreach (var catalog in resolvers.OfType<IGeneratedMappingDescriptorCatalog>())
        {
            var advertised = new HashSet<(Type Source, Type Destination)>();
            foreach (var mapping in catalog.GeneratedMappings)
            {
                if (mapping is null || mapping.SourceType is null || mapping.DestinationType is null || mapping.Map is null)
                    throw new InvalidOperationException("A generated mapping catalog contains an invalid descriptor.");
                if (!advertised.Add((mapping.SourceType, mapping.DestinationType)))
                    throw new InvalidOperationException($"Duplicate generated mapping catalog entry for {mapping.SourceType} -> {mapping.DestinationType}.");
                if (pairs.Add((mapping.SourceType, mapping.DestinationType)))
                    Resolve(resolvers, mapping.SourceType, mapping.DestinationType);
            }
        }
    }
}

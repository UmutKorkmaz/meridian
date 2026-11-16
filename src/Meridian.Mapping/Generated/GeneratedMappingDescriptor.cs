using Meridian.Mapping.Execution;

namespace Meridian.Mapping;

/// <summary>A generated create-new mapping and its exact supported type pair.</summary>
/// <param name="SourceType">The declared source type.</param>
/// <param name="DestinationType">The destination type.</param>
/// <param name="Map">The mapping operation, using the caller's existing context.</param>
public sealed record GeneratedMappingDescriptor(Type SourceType, Type DestinationType, Func<object?, ResolutionContext, object?> Map);

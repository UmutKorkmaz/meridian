using System.Threading;

namespace Meridian.Mapping;

/// <summary>Operation settings for a structural generated mapping. Null limits inherit the entry mapping's attribute defaults.</summary>
public sealed record GeneratedMappingOptions
{
    /// <summary>Maximum nested mapping depth. When supplied, must be positive.</summary>
    public int? MaxDepth { get; init; }
    /// <summary>Maximum items in each mapped collection. When supplied, must be positive.</summary>
    public int? MaxCollectionItems { get; init; }
    /// <summary>Cancellation observed before mapping and while enumerating collections.</summary>
    public CancellationToken CancellationToken { get; init; }
    /// <summary>Caller state carried by the generated operation; resolver integration is not yet supported.</summary>
    public object? State { get; init; }
}

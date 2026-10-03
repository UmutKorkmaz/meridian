namespace Meridian.Mapping.Generators.Models;

/// <summary>Per-pair immutable values; defaults match the generated attribute contract.</summary>
internal sealed record GeneratedOptions(
    int Mode = 0, int MaxDepth = 64, int MaxCollectionItems = 10000,
    bool PreserveReferences = false, bool AllowUndefinedEnumValues = false,
    bool AllowNullCollections = false, bool AllowNullDestinationValues = true,
    bool GenerateResolver = false, string? MethodName = null);

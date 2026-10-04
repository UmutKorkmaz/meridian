namespace Meridian.Mapping.Generators.Models;

/// <summary>Compiler-independent scalar conversion plan.</summary>
internal sealed record ConversionModel(bool IsIdentity, string SourceDisplayName, string DestinationDisplayName,
    string Kind = "Unsupported", string SourceType = "", string DestinationType = "", string EffectiveDestinationType = "",
    bool SourceNullable = false, bool SourceReference = false, string EnumValidation = "true");

namespace Meridian.Mapping.Generators.Models;

/// <summary>Only identity assignments are emitted in the legacy mapping contract.</summary>
internal sealed record ConversionModel(bool IsIdentity, string SourceDisplayName, string DestinationDisplayName);

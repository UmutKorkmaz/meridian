namespace Meridian.Mapping.Generators.Models;

internal sealed record MemberModel(string SourceName, string DestinationName,
    string SourceTypeIdentity, string DestinationTypeIdentity, ConversionModel Conversion,
    string? Expression = null, bool IsInitializer = true, bool IsRequired = false, bool IsIgnored = false,
    string? Failure = null, bool IsConfigured = false, bool RequiresStructuralSupport = false);

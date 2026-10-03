namespace Meridian.Mapping.Generators.Models;

internal sealed record MemberModel(string SourceName, string DestinationName,
    string SourceTypeIdentity, string DestinationTypeIdentity, ConversionModel Conversion);

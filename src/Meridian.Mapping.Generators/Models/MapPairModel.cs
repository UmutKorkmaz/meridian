using System.Collections.Immutable;

namespace Meridian.Mapping.Generators.Models;

internal sealed record MapPairModel(
    string SourceIdentity, string DestinationIdentity, string SourceName, string DestinationName,
    string? SourceNamespace, string? DestinationNamespace, ImmutableArray<MemberModel> Members,
    ConstructorModel Constructor, GeneratedOptions Options, bool ReferenceTypes = false)
{
    public bool Equals(MapPairModel? other) => other is not null && SourceIdentity == other.SourceIdentity &&
        DestinationIdentity == other.DestinationIdentity && SourceName == other.SourceName &&
        DestinationName == other.DestinationName && SourceNamespace == other.SourceNamespace &&
        DestinationNamespace == other.DestinationNamespace && ModelEquality.Equal(Members, other.Members) &&
        Constructor == other.Constructor && Options == other.Options && ReferenceTypes == other.ReferenceTypes;

    public override int GetHashCode() => ModelEquality.Hash(SourceIdentity, DestinationIdentity, SourceName,
        DestinationName, SourceNamespace, DestinationNamespace, ModelEquality.Hash(Members), Constructor, Options, ReferenceTypes);
}

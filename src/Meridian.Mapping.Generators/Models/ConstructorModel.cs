using System.Collections.Immutable;

namespace Meridian.Mapping.Generators.Models;

/// <summary>Value-only construction plan, including explicitly typed constructor arguments.</summary>
internal sealed record ConstructorModel(bool IsSupported, ImmutableArray<ConstructorArgumentModel> Arguments = default,
    bool SetsRequiredMembers = false, string? Failure = null)
{
    public bool Equals(ConstructorModel? other) => other is not null && IsSupported == other.IsSupported &&
        SetsRequiredMembers == other.SetsRequiredMembers && Failure == other.Failure && ModelEquality.Equal(Arguments, other.Arguments);
    public override int GetHashCode() => ModelEquality.Hash(IsSupported, SetsRequiredMembers, Failure, ModelEquality.Hash(Arguments));
}

internal sealed record ConstructorArgumentModel(string ParameterName, string Expression, string? DestinationMember, bool RequiresStructuralSupport = false);

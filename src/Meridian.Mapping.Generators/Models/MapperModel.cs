using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Meridian.Mapping.Generators.Models;

/// <summary>Value descriptions only: no compilation, syntax tree, symbol or Diagnostic survives discovery here.</summary>
internal sealed record MapperModel(
    string? Namespace,
    ImmutableArray<TypeDeclarationInfo> ContainingTypes,
    TypeDeclarationInfo TargetType,
    string MetadataPath,
    ImmutableArray<MapPairModel> Pairs,
    bool CanGenerate)
{
    public bool Equals(MapperModel? other) => other is not null && Namespace == other.Namespace &&
        ModelEquality.Equal(ContainingTypes, other.ContainingTypes) && TargetType == other.TargetType &&
        MetadataPath == other.MetadataPath && ModelEquality.Equal(Pairs, other.Pairs) && CanGenerate == other.CanGenerate;

    public override int GetHashCode() => ModelEquality.Hash(Namespace, ModelEquality.Hash(ContainingTypes),
        TargetType, MetadataPath, ModelEquality.Hash(Pairs), CanGenerate);
}

internal sealed record TypeDeclarationInfo(
    string Name, string DisplayName, string Accessibility, string Keyword, string Modifiers,
    string TypeParameters, ImmutableArray<string> ConstraintClauses, bool IsPartial)
{
    public bool Equals(TypeDeclarationInfo? other) => other is not null && Name == other.Name &&
        DisplayName == other.DisplayName && Accessibility == other.Accessibility && Keyword == other.Keyword &&
        Modifiers == other.Modifiers && TypeParameters == other.TypeParameters && IsPartial == other.IsPartial &&
        ModelEquality.Equal(ConstraintClauses, other.ConstraintClauses);

    public override int GetHashCode() => ModelEquality.Hash(Name, DisplayName, Accessibility, Keyword,
        Modifiers, TypeParameters, ModelEquality.Hash(ConstraintClauses), IsPartial);
}

/// <summary>ImmutableArray's default equality compares storage; models compare ordered contents instead.</summary>
internal static class ModelEquality
{
    internal static bool Equal<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefault == right.IsDefault && (left.IsDefault || left.SequenceEqual(right));

    internal static int Hash<T>(ImmutableArray<T> values)
    {
        if (values.IsDefault) return 0;
        var hash = 17;
        foreach (var value in values)
            hash = unchecked(hash * 31 + (value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(value)));
        return hash;
    }

    internal static int Hash(params object?[] values)
    {
        var hash = 17;
        foreach (var value in values) hash = unchecked(hash * 31 + (value?.GetHashCode() ?? 0));
        return hash;
    }
}

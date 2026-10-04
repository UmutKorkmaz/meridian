using System;
using System.Collections.Generic;
using System.Linq;
using Meridian.Mapping.Generators.Emission;
using Meridian.Mapping.Generators.Models;
using Microsoft.CodeAnalysis;

namespace Meridian.Mapping.Generators.Discovery;

/// <summary>Resolves structural member traversal while symbols are available; only text and pair indexes enter cached models.</summary>
internal static class StructuralBindingBuilder
{
    internal static bool TryBuild(ITypeSymbol source, ITypeSymbol destination, AttributeData[] pairs,
        GeneratedOptions options, string name, string input, string member, out string? expression,
        out string? helper, out string dependencies)
    {
        expression = null;
        helper = null;
        dependencies = "";
        for (var index = 0; index < pairs.Length; index++)
            if (SymbolEqualityComparer.Default.Equals(source, pairs[index].ConstructorArguments[0].Value as ITypeSymbol) &&
                SymbolEqualityComparer.Default.Equals(destination, pairs[index].ConstructorArguments[1].Value as ITypeSymbol))
            {
                expression = "__MeridianMap" + index + "(" + input + "!, context)";
                dependencies = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
        if (!TryShape(destination, out var destElement, out var key, out var value, out var array) ||
            !TryElement(source, out var sourceElement)) return false;
        string? elementExpression;
        string? keyExpression = null;
        var helpers = new List<string>();
        var edges = new List<string>();
        if (key is not null)
        {
            if (sourceElement is not INamedTypeSymbol { Name: "KeyValuePair", Arity: 2 } entry ||
                entry.ContainingNamespace.ToDisplayString() != "System.Collections.Generic") return false;
            if (!Element(entry.TypeArguments[0], key, name + "Key", "item.Key", out keyExpression) ||
                !Element(entry.TypeArguments[1], value!, name + "Value", "item.Value", out elementExpression)) return false;
        }
        else if (!Element(sourceElement!, destElement!, name + "Element", "item", out elementExpression)) return false;
        expression = name + "(" + input + ", context)";
        helper = CollectionEmitter.Emit(name, Type(source.WithNullableAnnotation(NullableAnnotation.NotAnnotated)), Type(sourceElement!), Type(destination.WithNullableAnnotation(NullableAnnotation.NotAnnotated)),
            destElement is null ? "" : Type(destElement), key is null ? null : Type(key), value is null ? null : Type(value),
            elementExpression!, keyExpression, string.Join("\n", helpers), array, options.AllowNullCollections);
        dependencies = string.Join(",", edges.Where(e => e.Length > 0));
        return true;

        bool Element(ITypeSymbol s, ITypeSymbol d, string helperName, string item, out string? mapped)
        {
            if (TryBuild(s, d, pairs, options, helperName, item, member, out mapped, out var childHelper, out var childEdges))
            {
                if (childHelper is not null) helpers.Add(childHelper);
                edges.Add(childEdges);
                return true;
            }
            // Unknown enumerable shapes cannot silently fall back to reference aliasing.
            if (IsCollection(d)) return false;
            var conversion = ConversionClassifier.Classify(s, d, options);
            if (conversion.Kind == "Unsupported") return false;
            mapped = helperName + "(" + item + ")";
            helpers.Add(ConversionEmitter.Emit(conversion, helperName, member));
            return true;
        }
    }

    internal static bool IsCollection(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return IsCollection(nullable.TypeArguments[0]);
        return type.SpecialType != SpecialType.System_String &&
            (type is IArrayTypeSymbol || type.SpecialType == SpecialType.System_Collections_IEnumerable ||
             type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable));
    }

    private static bool TryElement(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = type is IArrayTypeSymbol { Rank: 1 } array ? array.ElementType : null;
        if (element is not null) return true;
        if (type is IArrayTypeSymbol) return false;
        var enumerable = type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
            ? named : type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        element = enumerable?.TypeArguments[0];
        return element is not null;
    }

    private static bool TryShape(ITypeSymbol type, out ITypeSymbol? element, out ITypeSymbol? key, out ITypeSymbol? value, out bool array)
    {
        element = key = value = null;
        array = type is IArrayTypeSymbol { Rank: 1 };
        if (array) { element = ((IArrayTypeSymbol)type).ElementType; return true; }
        if (type is not INamedTypeSymbol named || named.ContainingNamespace.ToDisplayString() != "System.Collections.Generic") return false;
        if (named.Arity == 1 && named.Name is "List" or "IEnumerable" or "IReadOnlyList" or "ICollection" or "IList" or "IReadOnlyCollection")
        { element = named.TypeArguments[0]; return true; }
        if (named.Arity == 2 && named.Name is "Dictionary" or "IDictionary" or "IReadOnlyDictionary")
        { key = named.TypeArguments[0]; value = named.TypeArguments[1]; return true; }
        return false;
    }

    private static string Type(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Meridian.Mapping.Generators.Diagnostics;
using Meridian.Mapping.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Generators.Discovery;

/// <summary>Symbol-bound discovery only. The returned members contain comparable values, never compiler state.</summary>
internal static class MemberConfigurationBuilder
{
    internal static ImmutableArray<MemberModel> Build(INamedTypeSymbol container, ITypeSymbol source, ITypeSymbol destination,
        Compilation compilation, bool structural, AttributeData[] pairs, Action<DiagnosticDescriptor, object[]> report)
    {
        var readable = MappingModelBuilder.GetWritableNameLookup(source, false);
        var writable = MappingModelBuilder.GetWritableNameLookup(destination, true);
        var targets = new List<(string Name, ITypeSymbol Type, bool Initializer, bool Required)>();
        targets.AddRange(writable.Values.Select(p => (p.Name, p.Type, true, p.IsRequired)));
        if (structural && destination is INamedTypeSymbol named)
            foreach (var parameter in named.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).SelectMany(c => c.Parameters))
                if (!targets.Any(t => !t.Initializer && t.Name == parameter.Name && SymbolEqualityComparer.Default.Equals(t.Type, parameter.Type)))
                    targets.Add((parameter.Name, parameter.Type, false, false));

        var configurations = new List<AttributeData>();
        foreach (var attribute in container.GetAttributes().Where(IsMemberAttribute))
        {
            var selectedSource = attribute.NamedArguments.FirstOrDefault(p => p.Key == "SourceType");
            var selectedDestination = attribute.NamedArguments.FirstOrDefault(p => p.Key == "DestinationType");
            var hasSource = selectedSource.Key is not null;
            var hasDestination = selectedDestination.Key is not null;
            if (hasSource != hasDestination || hasSource && (selectedSource.Value.Value is not ITypeSymbol || selectedDestination.Value.Value is not ITypeSymbol))
            { Invalid("member selectors must supply both SourceType and DestinationType"); continue; }
            if (hasSource)
            {
                if (!pairs.Any(p => SymbolEqualityComparer.Default.Equals(p.ConstructorArguments[0].Value as ITypeSymbol, selectedSource.Value.Value as ITypeSymbol) &&
                    SymbolEqualityComparer.Default.Equals(p.ConstructorArguments[1].Value as ITypeSymbol, selectedDestination.Value.Value as ITypeSymbol)))
                { Invalid("member selectors must name a declared mapping pair"); continue; }
                if (!SymbolEqualityComparer.Default.Equals(source, selectedSource.Value.Value as ITypeSymbol) ||
                    !SymbolEqualityComparer.Default.Equals(destination, selectedDestination.Value.Value as ITypeSymbol)) continue;
            }
            var targetName = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (string.IsNullOrEmpty(targetName)) { Invalid("destination member must be a nonempty name"); continue; }
            if (!targets.Any(t => Matches(t.Name, targetName!, t.Initializer)))
            {
                // Unselected configuration applies to compatible declared pairs only.
                var compatibleElsewhere = !hasSource && pairs.Any(p => HasTarget(p.ConstructorArguments[1].Value as ITypeSymbol, targetName!, p.NamedArguments.Any(n => n.Key == "Mode" && n.Value.Value is int mode && mode == 1)));
                if (!compatibleElsewhere) Invalid("destination member '" + targetName + "' is not mapped by any compatible declared pair");
                continue;
            }
            if (attribute.AttributeClass!.Name == "MapMemberAttribute" &&
                (attribute.ConstructorArguments.Length != 2 || attribute.ConstructorArguments[1].Value is not string sourceMember || string.IsNullOrEmpty(sourceMember)))
            { Invalid("configured source member must be a nonempty name"); continue; }
            configurations.Add(attribute);
        }

        var result = ImmutableArray.CreateBuilder<MemberModel>();
        foreach (var target in targets)
        {
            var configs = configurations.Where(a => Matches(target.Name, (string)a.ConstructorArguments[0].Value!, target.Initializer)).ToArray();
            var renames = configs.Where(a => a.AttributeClass!.Name == "MapMemberAttribute").ToArray();
            var ignores = configs.Where(a => a.AttributeClass!.Name == "IgnoreMapMemberAttribute").ToArray();
            var converters = configs.Where(a => a.AttributeClass!.Name == "MapWithAttribute").ToArray();
            if (renames.Length > 1 || ignores.Length > 1 || converters.Length > 1 || ignores.Length > 0 && configs.Length > 1)
                Invalid("conflicting configuration for destination member '" + target.Name + "'");
            var sourceName = renames.FirstOrDefault()?.ConstructorArguments[1].Value as string ?? target.Name;
            var matches = readable.Values.Where(p => string.Equals(p.Name, sourceName,
                target.Initializer || renames.Length > 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)).ToArray();
            var property = matches.Length == 1 ? matches[0] : null;
            var ignored = ignores.Length > 0;
            var failure = property is null ? "no unambiguous public readable source property with this name" : null;
            var identity = property is not null && SymbolEqualityComparer.Default.Equals(property.Type, target.Type);
            var assignable = property is not null && IsAssignable(compilation, property.Type, target.Type);
            string? expression = property is not null && (identity || !target.Initializer && assignable) ? "source.@" + property.Name : null;
            if (property is not null && expression is null)
                failure = "source type '" + property.Type.ToDisplayString() + "' differs from destination type '" + target.Type.ToDisplayString() + "'";
            if (!ignored && renames.Length > 0 && property is null) Invalid("configured source member '" + sourceName + "' is not readable");
            if (!ignored && converters.Length > 0)
            {
                var converter = converters[0];
                var type = converter.ConstructorArguments[1].Value as INamedTypeSymbol;
                var methodName = converter.ConstructorArguments[2].Value as string;
                var methods = type is null ? null : GetMethods(type, methodName ?? "").Where(m => m.IsStatic && !m.IsAbstract && !m.IsVirtual && m.MethodKind == MethodKind.Ordinary &&
                    !m.IsGenericMethod && m.Parameters.Length == 1 && m.Parameters[0].RefKind == RefKind.None && !m.ReturnsByRef && !m.ReturnsByRefReadonly &&
                    compilation.IsSymbolAccessibleWithin(m, container) && property is not null &&
                    IsAssignable(compilation, property.Type, m.Parameters[0].Type) && IsAssignable(compilation, m.ReturnType, target.Type)).ToArray();
                if (type is null || type.IsUnboundGenericType || IsFileLocal(type) || !compilation.IsSymbolAccessibleWithin(type, container) || methods?.Length != 1)
                {
                    failure = "static converter for '" + target.Name + "' must be accessible, unambiguous, nongeneric and accept one source value with an assignable return type";
                    if (target.Initializer) Invalid(failure);
                    expression = null;
                }
                else expression = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + methods[0].Name + "((" +
                    methods[0].Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")source.@" + property!.Name + ")";
            }
            if (expression is not null) failure = null;
            result.Add(new MemberModel(property?.Name ?? sourceName, target.Name,
                property?.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "", target.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                new ConversionModel(identity, property?.Type.ToDisplayString() ?? "", target.Type.ToDisplayString()), expression,
                target.Initializer, target.Required, ignored, failure, configs.Length > 0,
                structural && !ignored && converters.Length == 0 && property is not null && expression is not null &&
                (identity && !SymbolEqualityComparer.IncludeNullability.Equals(property.Type, target.Type) ||
                 property.Type.NullableAnnotation == NullableAnnotation.Annotated && target.Type.NullableAnnotation == NullableAnnotation.NotAnnotated ||
                 NeedsStructuralSupport(target.Type) || pairs.Any(p => SymbolEqualityComparer.Default.Equals(p.ConstructorArguments[0].Value as ITypeSymbol, property.Type) &&
                    SymbolEqualityComparer.Default.Equals(p.ConstructorArguments[1].Value as ITypeSymbol, target.Type)))));
        }
        foreach (var configuration in configurations.Where(a => a.AttributeClass!.Name is "MapWithAttribute" or "MapMemberAttribute"))
        {
            var name = (string)configuration.ConstructorArguments[0].Value!;
            var applicable = result.Where(m => Matches(m.DestinationName, name, m.IsInitializer)).ToArray();
            if (applicable.Length > 0 && applicable.All(m => m.Expression is null) && applicable.All(m => !m.IsInitializer))
                Invalid(applicable[0].Failure ?? "no constructor can bind the explicitly configured member");
        }
        return result.ToImmutable();
        void Invalid(string reason) => report(MapperDiagnostics.InvalidConfiguration, new object[] { reason });
    }

    private static bool IsFileLocal(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
            if (current.IsFileLocal) return true;
        return false;
    }

    private static IEnumerable<IMethodSymbol> GetMethods(INamedTypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var members = current.GetMembers(name);
            if (members.Length > 0) return members.OfType<IMethodSymbol>();
        }
        return Enumerable.Empty<IMethodSymbol>();
    }

    private static bool NeedsStructuralSupport(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return NeedsStructuralSupport(nullable.TypeArguments[0]);
        return type.TypeKind == TypeKind.Enum || type is IArrayTypeSymbol ||
            type.SpecialType != SpecialType.System_String && type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable) ||
            type.SpecialType == SpecialType.System_Collections_IEnumerable;
    }

    internal static bool IsAssignable(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var conversion = ((CSharpCompilation)compilation).ClassifyConversion(source, destination);
        return conversion.IsIdentity || conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing);
    }

    private static bool IsMemberAttribute(AttributeData attribute) => attribute.AttributeClass?.ToDisplayString() is
        "Meridian.Mapping.MapMemberAttribute" or "Meridian.Mapping.IgnoreMapMemberAttribute" or "Meridian.Mapping.MapWithAttribute";
    private static bool Matches(string name, string configured, bool initializer) => string.Equals(name, configured,
        initializer ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    private static bool HasTarget(ITypeSymbol? destination, string name, bool structural) => destination is INamedTypeSymbol type &&
        (MappingModelBuilder.GetWritableNameLookup(type, true).ContainsKey(name) || structural && type.InstanceConstructors.Any(c =>
            c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Any(p => Matches(p.Name, name, false))));
}

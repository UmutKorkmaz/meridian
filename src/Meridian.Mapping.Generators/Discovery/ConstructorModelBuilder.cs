using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Meridian.Mapping.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Generators.Discovery;

internal static class ConstructorModelBuilder
{
    internal static ConstructorModel Select(INamedTypeSymbol destination, ImmutableArray<MemberModel> members)
    {
        if (destination.TypeKind is not (TypeKind.Class or TypeKind.Struct) || destination.IsAbstract || destination.IsStatic)
            return new ConstructorModel(false, Failure: "the destination is not a concrete class or struct");
        var configuredConstructorTargets = members.Where(m => !m.IsInitializer && m.IsConfigured && !m.IsIgnored &&
            !members.Any(initializer => initializer.IsInitializer && initializer.IsConfigured &&
                string.Equals(initializer.DestinationName, m.DestinationName, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.DestinationName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new List<ConstructorModel>();
        var bestCount = -1;
        foreach (var constructor in destination.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public))
        {
            var arguments = ImmutableArray.CreateBuilder<ConstructorArgumentModel>();
            var eligible = true;
            foreach (var parameter in constructor.Parameters)
            {
                if (parameter.RefKind != RefKind.None) { eligible = false; break; }
                var member = members.FirstOrDefault(m => !m.IsInitializer &&
                    string.Equals(m.DestinationName, parameter.Name, StringComparison.OrdinalIgnoreCase) &&
                    m.DestinationTypeIdentity == parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (member?.Expression is not null && !member.IsIgnored)
                    arguments.Add(new ConstructorArgumentModel(parameter.Name, type, "(" + type + ")(" + member.Expression + ")", member));
                else if (parameter.HasExplicitDefaultValue && (member is null || !member.IsConfigured || member.IsIgnored))
                    arguments.Add(new ConstructorArgumentModel(parameter.Name, type, DefaultExpression(parameter)));
                else { eligible = false; break; }
            }
            if (!eligible || configuredConstructorTargets.Any(name => !arguments.Any(argument =>
                argument.Binding is { IsConfigured: true } binding && string.Equals(binding.DestinationName, name, StringComparison.OrdinalIgnoreCase))) ||
                constructor.Parameters.Length < bestCount) continue;
            if (constructor.Parameters.Length > bestCount) { candidates.Clear(); bestCount = constructor.Parameters.Length; }
            candidates.Add(new ConstructorModel(true, arguments.ToImmutable(), constructor.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")));
        }
        return candidates.Count == 1 ? candidates[0] : new ConstructorModel(false, Failure: candidates.Count == 0
            ? "no public constructor has resolvable required parameters" : "multiple public constructors have the same best resolved parameter count",
            HasUnfulfilledConfiguration: candidates.Count == 0 && configuredConstructorTargets.Length > 0);
    }

    private static string DefaultExpression(IParameterSymbol parameter)
    {
        var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var value = parameter.ExplicitDefaultValue;
        if (value is null) return "default(" + type + ")";
        var literal = value switch
        {
            string text => SymbolDisplay.FormatLiteral(text, true),
            char character => SymbolDisplay.FormatLiteral(character, true),
            bool boolean => boolean ? "true" : "false",
            float number when float.IsNaN(number) => "global::System.Single.NaN",
            float number when float.IsPositiveInfinity(number) => "global::System.Single.PositiveInfinity",
            float number when float.IsNegativeInfinity(number) => "global::System.Single.NegativeInfinity",
            double number when double.IsNaN(number) => "global::System.Double.NaN",
            double number when double.IsPositiveInfinity(number) => "global::System.Double.PositiveInfinity",
            double number when double.IsNegativeInfinity(number) => "global::System.Double.NegativeInfinity",
            float number => number.ToString("R", CultureInfo.InvariantCulture) + "F",
            double number => number.ToString("R", CultureInfo.InvariantCulture) + "D",
            decimal number => number.ToString(CultureInfo.InvariantCulture) + "M",
            ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
            long number => number.ToString(CultureInfo.InvariantCulture) + "L",
            uint number => number.ToString(CultureInfo.InvariantCulture) + "U",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        };
        return "(" + type + ")(" + literal + ")";
    }
}

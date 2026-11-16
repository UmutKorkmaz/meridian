using System;
using System.Globalization;
using System.Linq;
using Meridian.Mapping.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Generators.Discovery;

/// <summary>Converts compiler symbols to a value-only scalar conversion plan.</summary>
internal static class ConversionClassifier
{
    private static readonly CSharpCompilation NumericConversions = CSharpCompilation.Create("MeridianNumericConversions");

    internal static ConversionModel Classify(ITypeSymbol source, ITypeSymbol destination, GeneratedOptions options)
    {
        var s = Unwrap(source);
        var d = Unwrap(destination);
        var identity = SymbolEqualityComparer.Default.Equals(source, destination);
        var kind = "Unsupported";
        if (options.ConversionKind == 0)
        {
            if (IsNumber(s) && IsNumber(d)) kind = "Checked";
        }
        else if (options.ConversionKind == 1)
        {
            if (s.SpecialType == SpecialType.System_String && (IsNumber(d) || d.SpecialType == SpecialType.System_DateTime || d.TypeKind == TypeKind.Enum))
                kind = d.TypeKind == TypeKind.Enum ? "EnumParse" : "Parse";
        }
        else if (options.Mode == 1)
        {
            if (d.TypeKind == TypeKind.Enum && (s.TypeKind == TypeKind.Enum || IsIntegral(s))) kind = "Enum";
            else if (SymbolEqualityComparer.Default.Equals(s, d)) kind = "Identity";
            else if (IsNumber(s) && IsNumber(d) && NumericConversions.ClassifyConversion(s, d).IsImplicit) kind = "Checked";
            else if (s is INamedTypeSymbol { TypeKind: TypeKind.Enum } e && IsNumber(d) &&
                NumericConversions.ClassifyConversion(e.EnumUnderlyingType!, d).IsImplicit) kind = "Checked";
        }
        else if (identity) kind = "Identity";

        var enumValidation = "true";
        if (d is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType && !options.AllowUndefinedEnumValues)
        {
            var fields = enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToArray();
            var name = d.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (enumType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
            {
                ulong mask = 0;
                foreach (var field in fields)
                    mask |= field.ConstantValue is ulong unsigned ? unsigned : unchecked((ulong)Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture));
                enumValidation = "(unchecked((ulong)(" + enumType.EnumUnderlyingType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")converted) & ~" + mask.ToString(CultureInfo.InvariantCulture) + "UL) == 0";
            }
            else enumValidation = fields.Length == 0 ? "false" : string.Join(" || ", fields.Select(f => "converted == " + name + ".@" + f.Name));
        }
        return new ConversionModel(identity, source.ToDisplayString(), destination.ToDisplayString(), kind,
            source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)), destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)),
            d.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), !SymbolEqualityComparer.Default.Equals(source, s),
            source.IsReferenceType, enumValidation);
    }

    private static ITypeSymbol Unwrap(ITypeSymbol type) => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type;
    private static bool IsIntegral(ITypeSymbol type) => type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Char;
    private static bool IsNumber(ITypeSymbol type) => IsIntegral(type) || type.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;
}

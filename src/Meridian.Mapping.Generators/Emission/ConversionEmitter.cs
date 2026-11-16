using Meridian.Mapping.Generators.Models;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Generators.Emission;

/// <summary>Emits typed local helpers so nullable getters and parse inputs are evaluated once.</summary>
internal static class ConversionEmitter
{
    internal static string Emit(ConversionModel model, string name, string destinationMember)
    {
        var value = model.SourceNullable ? "value.Value" : "value";
        var body = model.SourceNullable || model.SourceReference ? "if (value is null) return default!; " : "";
        if (model.Kind == "Identity") body += "return value;";
        else if (model.Kind == "Checked") body += "return checked((" + model.DestinationType + ")" + value + ");";
        else if (model.Kind == "Parse") body += "return " + model.EffectiveDestinationType + (model.EffectiveDestinationType == "char" ? ".Parse(value);" : ".Parse(value, global::System.Globalization.CultureInfo.InvariantCulture);");
        else
        {
            var error = "throw new global::Meridian.Mapping.MeridianEnumMappingException(global::System.Convert.ToString(value, global::System.Globalization.CultureInfo.InvariantCulture)!, typeof(" + model.EffectiveDestinationType + "), " + SymbolDisplay.FormatLiteral(destinationMember, true) + ");";
            if (model.Kind == "EnumParse")
                body += "if (!global::System.Enum.TryParse<" + model.EffectiveDestinationType + ">(value, true, out var converted)) { " + error + " } ";
            else body += "var converted = checked((" + model.EffectiveDestinationType + ")" + value + "); ";
            body += "if (!(" + model.EnumValidation + ")) { " + error + " } return converted;";
        }
        // Nullable value identity needs an explicit destination cast for T? -> T.
        if (model.Kind == "Identity" && model.SourceNullable)
            body = "if (value is null) return default!; return (" + model.DestinationType + ")value.Value;";
        return "static " + model.DestinationType + " " + name + "(" + model.SourceType + " value) { " + body + " }";
    }
}

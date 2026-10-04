using System.Linq;
using System.Text;
using Meridian.Mapping.Generators.Models;

namespace Meridian.Mapping.Generators.Emission;

internal static class ConstructionEmitter
{
    internal static void Emit(StringBuilder builder, string indent, MapPairModel pair)
    {
        builder.Append(indent).Append("    var result = new ").Append(pair.DestinationIdentity).Append('(');
        if (!pair.Constructor.Arguments.IsDefaultOrEmpty)
            builder.Append(string.Join(", ", pair.Constructor.Arguments.Select(a => "@" + a.ParameterName + ": " + a.Expression)));
        builder.AppendLine(")");
        builder.Append(indent).AppendLine("    {");
        foreach (var member in pair.Members.Where(m => m.IsInitializer && !m.IsIgnored))
        {
            if (member.Expression is not null && !member.SkipNullAssignment)
                builder.Append(indent).Append("        @").Append(member.DestinationName).Append(" = ").Append(member.Expression).AppendLine(",");
            else if (!member.Conversion.IsIdentity)
                builder.Append(indent).Append("    // Skipped: ").Append(member.DestinationName)
                    .Append(" — source is ").Append(member.Conversion.SourceDisplayName)
                    .Append(", destination is ").Append(member.Conversion.DestinationDisplayName)
                    .AppendLine(". Use runtime IMapper for type conversion.");
        }
        builder.Append(indent).AppendLine("    };");
        var index = 0;
        foreach (var member in pair.Members.Where(m => m.SkipNullAssignment && !m.IsIgnored && m.Expression is not null))
        {
            var local = "__meridianValue" + index++;
            builder.Append(indent).Append("    var ").Append(local).Append(" = ").Append(member.Expression).AppendLine(";");
            builder.Append(indent).Append("    if (").Append(local).Append(" is not null) result.@").Append(member.DestinationName).Append(" = ").Append(local).AppendLine(";");
        }
        var bindings = pair.Constructor.Arguments.IsDefault ? pair.Members : pair.Members.Concat(pair.Constructor.Arguments.Where(a => a.Binding is not null).Select(a => a.Binding!));
        foreach (var helper in bindings.Select(m => m.ConversionHelper).Where(h => h is not null).Distinct())
            builder.Append(indent).Append("    ").AppendLine(helper);
    }
}

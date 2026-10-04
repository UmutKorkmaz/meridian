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
            if (member.Expression is not null)
                builder.Append(indent).Append("        @").Append(member.DestinationName).Append(" = ").Append(member.Expression).AppendLine(",");
            else if (!member.Conversion.IsIdentity)
                builder.Append(indent).Append("    // Skipped: ").Append(member.DestinationName)
                    .Append(" — source is ").Append(member.Conversion.SourceDisplayName)
                    .Append(", destination is ").Append(member.Conversion.DestinationDisplayName)
                    .AppendLine(". Use runtime IMapper for type conversion.");
        }
        builder.Append(indent).AppendLine("    };");
    }
}

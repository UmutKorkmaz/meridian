using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using static Meridian.Mediator.Generators.MeridianMediatorGenerator;

namespace Meridian.Mediator.Generators.Emission;

internal static class StreamRegistryEmitter
{
    internal static void Emit(StringBuilder builder, IEnumerable<Entry> entries)
    {
        builder.Append("public global::Meridian.Mediator.Wrappers.StreamRequestHandlerBase? FindStreamHandler(global::System.Type requestType) {\n");
        foreach (var entry in entries.Where(e => e.Category == "StreamHandler").GroupBy(e => TypeName(e.Contract.TypeArguments[0])).Select(g => g.First()))
        {
            var name = TypeName(entry.Contract.TypeArguments[0]);
            var response = entry.Contract.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
            builder.Append("if (requestType == typeof(").Append(name).Append(")) return new global::Meridian.Mediator.Wrappers.StreamRequestHandlerWrapperImpl<").Append(name).Append(", ").Append(response).Append(">();\n");
        }
        builder.Append("return null; }\n");
    }
}

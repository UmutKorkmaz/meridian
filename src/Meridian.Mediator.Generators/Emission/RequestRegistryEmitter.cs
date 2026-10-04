using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Meridian.Mediator.Generators.Emission;

internal static class RequestRegistryEmitter
{
    internal static void Emit(StringBuilder builder, IEnumerable<MeridianMediatorGenerator.Entry> entries)
    {
        builder.Append("public global::Meridian.Mediator.Wrappers.RequestHandlerBase? FindRequestHandler(global::System.Type requestType) {\n");
        foreach (var entry in entries.Where(e => e.Category == "RequestHandler").GroupBy(e => MeridianMediatorGenerator.TypeName(e.Contract.TypeArguments[0])).Select(g => g.First()))
        {
            var request = (INamedTypeSymbol)entry.Contract.TypeArguments[0];
            var name = MeridianMediatorGenerator.TypeName(request);
            var isVoid = request.AllInterfaces.Any(i => MeridianMediatorGenerator.Is(i, "Meridian.Mediator.IRequest"));
            builder.Append("if (requestType == typeof(").Append(name).Append(")) return new global::Meridian.Mediator.Wrappers.");
            if (isVoid) builder.Append("UnitRequestHandlerWrapperImpl<").Append(name);
            else builder.Append("RequestHandlerWrapperImpl<").Append(name).Append(", ").Append(entry.Contract.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)));
            builder.Append(">();\n");
        }
        builder.Append("return null; }\n");
    }
}

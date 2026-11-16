using System.Text;
using Meridian.Mapping.Generators.Models;

namespace Meridian.Mapping.Generators.Emission;

internal static class NestedMapperEmitter
{
    internal static void Emit(StringBuilder builder, string indent, MapPairModel pair, int index)
    {
        var method = pair.Options.MethodName ?? "MapTo" + pair.DestinationName;
        if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(method) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ||
            Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetContextualKeywordKind(method) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) method = "@" + method;
        var source = pair.SourceIdentity;
        var destination = pair.DestinationIdentity;
        if (pair.Options.Mode == 1)
        {
            builder.AppendLine($"{indent}public static {destination} {method}({source} source) => {method}(source, default(global::Meridian.Mapping.GeneratedMappingOptions));");
            builder.AppendLine($"{indent}public static {destination} {method}({source} source, global::Meridian.Mapping.GeneratedMappingOptions? options)");
            builder.AppendLine(indent + "{");
            builder.AppendLine($"{indent}    var context = new GeneratedMappingContext(options, {pair.Options.MaxDepth}, {pair.Options.MaxCollectionItems});");
            builder.AppendLine($"{indent}    return __MeridianMap{index}(source, context);");
            builder.AppendLine(indent + "}");
        }
        builder.AppendLine($"{indent}private static {destination} __MeridianMap{index}({source} source, GeneratedMappingContext context)");
        builder.AppendLine(indent + "{");
        builder.AppendLine(indent + "    context.CancellationToken.ThrowIfCancellationRequested();");
        builder.AppendLine(indent + "    if (context.Depth >= context.MaxDepth" + (pair.SourceCanBeNull ? " || source is null" : "") + ") return default!;");
        builder.AppendLine($"{indent}    if (context.ConfiguredMap is {{ }} configured)");
        builder.AppendLine(indent + "    {");
        builder.AppendLine($"{indent}        var mapped = configured(source!, typeof({source}), typeof({destination}), context.Depth);");
        builder.AppendLine($"{indent}        if (mapped.Found) return ({destination})mapped.Value!;");
        builder.AppendLine(indent + "    }");
        if (pair.Options.Mode == 0)
            builder.AppendLine($"{indent}    return {method}(source);");
        else
        {
            if (pair.Options.PreserveReferences && pair.ReferenceTypes)
                builder.AppendLine($"{indent}    if (context.TryGet(source, typeof({destination}), out var cached)) return ({destination})cached!;");
            builder.AppendLine(indent + "    context.Depth++;");
            builder.AppendLine(indent + "    try");
            builder.AppendLine(indent + "    {");
            ConstructionEmitter.Emit(builder, indent + "    ", pair);
            builder.AppendLine(indent + "        return result;");
            builder.AppendLine(indent + "    }");
            builder.AppendLine(indent + "    finally { context.Depth--; }");
        }
        builder.AppendLine(indent + "}");
    }
}

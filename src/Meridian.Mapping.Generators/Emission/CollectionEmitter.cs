namespace Meridian.Mapping.Generators.Emission;

/// <summary>Emits a bounded collection helper from symbol-free type and element plans.</summary>
internal static class CollectionEmitter
{
    internal static string Emit(string name, string source, string sourceElement, string destination,
        string destinationElement, string? dictionaryKey, string? dictionaryValue, string expression,
        string? keyExpression, string helpers, bool array, bool allowNull)
    {
        var storage = dictionaryKey is null ? "global::System.Collections.Generic.List<" + destinationElement + ">" :
            "global::System.Collections.Generic.Dictionary<" + dictionaryKey + ", " + dictionaryValue + ">";
        var empty = allowNull ? "default!" : array ? "global::System.Array.Empty<" + destinationElement + ">()" : "new " + storage + "()";
        var add = dictionaryKey is null ? "result.Add(" + expression + ");" : "result[" + keyExpression + "] = " + expression + ";";
        return $$"""
            static {{destination}} {{name}}({{source}}? value, GeneratedMappingContext context)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (context.Depth >= context.MaxDepth) return default!;
                if (value is null) return {{empty}};
                int count = value is global::System.Collections.Generic.ICollection<{{sourceElement}}> counted ? counted.Count :
                    value is global::System.Collections.Generic.IReadOnlyCollection<{{sourceElement}}> readOnly ? readOnly.Count :
                    value is global::System.Collections.ICollection untyped ? untyped.Count : 0;
                context.CheckCount(count, typeof({{source}}), typeof({{destination}}));
                var result = new {{storage}}(count);
                int seen = 0;
                context.Depth++;
                try
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    using var enumerator = ((global::System.Collections.Generic.IEnumerable<{{sourceElement}}>)value).GetEnumerator();
                    while (true)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        if (!enumerator.MoveNext()) break;
                        context.CheckCount(++seen, typeof({{source}}), typeof({{destination}}));
                        var item = enumerator.Current;
                        {{add}}
                    }
                }
                finally { context.Depth--; }
                return {{(array ? "result.ToArray()" : "result")}};
                {{helpers}}
            }
            """;
    }
}

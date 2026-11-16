using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Meridian.Mapping.Generators.Models;

namespace Meridian.Mapping.Generators.Emission;

internal static class ResolverEmitter
{
    private static readonly char[] TraversalSeparators = { ',' };
    internal static void Emit(StringBuilder builder, MapperModel model)
    {
        var pairs = model.Pairs.Select((pair, index) => (pair, index))
            .Where(item => item.pair.Options.GenerateResolver && item.pair.Constructor.IsSupported).ToArray();
        if (pairs.Length == 0) return;
        builder.AppendLine("""
            public sealed class GeneratedResolver : global::Meridian.Mapping.IGeneratedMappingResolver, global::Meridian.Mapping.IGeneratedMappingDescriptorCatalog
            {
                public global::System.Collections.Generic.IReadOnlyList<global::Meridian.Mapping.GeneratedMappingDescriptor> GeneratedMappings { get; } =
                    global::System.Array.AsReadOnly(new global::Meridian.Mapping.GeneratedMappingDescriptor[] {
            """);
        foreach (var (pair, index) in pairs)
        {
            bool RequiresInitializerPolicy(int current, HashSet<int> visited)
            {
                if (!visited.Add(current)) return false;
                var candidate = model.Pairs[current];
                if (candidate.Options.Mode != 1) return false;
                if (candidate.Members.Any(member => !member.IsIgnored && member.IsInitializer && member.DestinationReference && (member.IsInitOnly || member.IsRequired))) return true;
                var bindings = candidate.Members.Concat(candidate.Constructor.Arguments.IsDefault ? Enumerable.Empty<MemberModel>() :
                    candidate.Constructor.Arguments.Where(argument => argument.Binding is not null).Select(argument => argument.Binding!));
                return bindings.SelectMany(member => member.TraversalPairs.Split(TraversalSeparators, StringSplitOptions.RemoveEmptyEntries))
                    .Any(next => RequiresInitializerPolicy(int.Parse(next, System.Globalization.CultureInfo.InvariantCulture), visited));
            }
            var policyGuard = RequiresInitializerPolicy(index, new HashSet<int>())
                ? "if (!configuration.AllowNullDestinationValues) throw new global::System.NotSupportedException(\"Generated init/required reference assignments cannot omit null values; use an explicit runtime TypeMap for this null policy.\");" : "";
            builder.AppendLine($$"""
                new global::Meridian.Mapping.GeneratedMappingDescriptor(typeof({{pair.SourceIdentity}}), typeof({{pair.DestinationIdentity}}), (source, operation) =>
                {
                    var configuration = operation.Mapper.ConfigurationProvider;
                    {{policyGuard}}
                    var context = new GeneratedMappingContext(new global::Meridian.Mapping.GeneratedMappingOptions {
                        MaxDepth = global::System.Math.Min({{pair.Options.MaxDepth}}, operation.MaxDepth),
                        MaxCollectionItems = global::System.Math.Min({{pair.Options.MaxCollectionItems}}, operation.MaxCollectionItems),
                        State = operation.State
                    }, {{pair.Options.MaxDepth}}, {{pair.Options.MaxCollectionItems}});
                    context.Depth = operation.Depth;
                    context.AllowNullCollections = configuration.AllowNullCollections;
                    context.AllowNullDestinationValues = configuration.AllowNullDestinationValues;
                    context.ConfiguredMap = (value, sourceType, destinationType, depth) =>
                    {
                        var found = operation.TryMapConfigured(value, sourceType, destinationType, depth - operation.Depth, out var mapped, context.MaxDepth, context.MaxCollectionItems);
                        return (found, mapped);
                    };
                    context.GetReference = (value, destination) => operation.TryGetMapped(value, destination, out var mapped) ? mapped : null;
                    context.SetReference = operation.CacheMapped;
                    return __MeridianMap{{index}}(({{pair.SourceIdentity}})source!, context);
                }),
                """);
        }
        builder.AppendLine("""
                });
                public bool TryGetMapping(global::System.Type sourceType, global::System.Type destinationType, out global::Meridian.Mapping.GeneratedMappingDescriptor? mapping)
                {
                    foreach (var candidate in GeneratedMappings)
                        if (candidate.SourceType == sourceType && candidate.DestinationType == destinationType) { mapping = candidate; return true; }
                    mapping = null;
                    return false;
                }
            }
            """);
    }
}

using System.Text;

namespace Meridian.Mapping.Generators.Emission;

internal static class GeneratedContextEmitter
{
    internal static void Emit(StringBuilder builder) => builder.AppendLine("""
        private sealed class GeneratedMappingContext
        {
            internal int Depth;
            internal readonly int MaxDepth;
            internal readonly int MaxCollectionItems;
            internal readonly global::System.Threading.CancellationToken CancellationToken;
            internal readonly object? State;
            internal bool? AllowNullCollections = null;
            internal bool? AllowNullDestinationValues = null;
            internal global::System.Func<object, global::System.Type, global::System.Type, int, (bool Found, object? Value)>? ConfiguredMap = null;
            internal global::System.Func<object, global::System.Type, object?>? GetReference = null;
            internal global::System.Action<object, global::System.Type, object>? SetReference = null;
            private global::System.Collections.Generic.Dictionary<(object Source, global::System.Type Destination), object>? references;
            internal GeneratedMappingContext(global::Meridian.Mapping.GeneratedMappingOptions? options, int maxDepth, int maxCollectionItems)
            {
                if (options?.MaxDepth is <= 0) throw new global::System.ArgumentOutOfRangeException(nameof(options.MaxDepth));
                if (options?.MaxCollectionItems is <= 0) throw new global::System.ArgumentOutOfRangeException(nameof(options.MaxCollectionItems));
                MaxDepth = options?.MaxDepth ?? maxDepth;
                MaxCollectionItems = options?.MaxCollectionItems ?? maxCollectionItems;
                CancellationToken = options?.CancellationToken ?? default;
                State = options?.State;
                CancellationToken.ThrowIfCancellationRequested();
            }
            internal bool TryGet(object source, global::System.Type destination, out object? value)
            {
                value = GetReference?.Invoke(source, destination);
                if (value is not null) return true;
                return references is not null && references.TryGetValue((source, destination), out value);
            }
            internal void Cache(object source, global::System.Type destination, object value)
            {
                SetReference?.Invoke(source, destination, value);
                (references ??= new global::System.Collections.Generic.Dictionary<(object, global::System.Type), object>(new ReferenceComparer()))[(source, destination)] = value;
            }
            internal void CheckCount(int count, global::System.Type source, global::System.Type destination)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (count > MaxCollectionItems) throw new global::Meridian.Mapping.MeridianMappingLimitException(
                    global::Meridian.Mapping.MeridianMappingLimit.MaxCollectionItems, MaxCollectionItems, count, source, destination);
            }
            private sealed class ReferenceComparer : global::System.Collections.Generic.IEqualityComparer<(object Source, global::System.Type Destination)>
            {
                public bool Equals((object Source, global::System.Type Destination) x, (object Source, global::System.Type Destination) y) =>
                    global::System.Object.ReferenceEquals(x.Source, y.Source) && x.Destination == y.Destination;
                public int GetHashCode((object Source, global::System.Type Destination) value) =>
                    unchecked(global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Source) * 397 ^ value.Destination.GetHashCode());
            }
        }
        """);
}

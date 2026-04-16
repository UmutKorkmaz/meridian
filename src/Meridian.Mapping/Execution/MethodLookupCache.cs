using System.Collections.Concurrent;
using System.Reflection;

namespace Meridian.Mapping.Execution;

/// <summary>
/// Caches <see cref="MethodInfo"/> lookups on <see cref="Type"/> instances to
/// avoid repeated reflection cost on the mapping hot path. Meridian resolves
/// the same <c>Convert</c> and <c>Resolve</c> methods on the same converter
/// and resolver types across millions of calls — caching makes the lookup
/// O(1) after warmup.
/// </summary>
/// <remarks>
/// Falls back to scanning implemented interfaces so converters and resolvers
/// that only satisfy the method via an interface (rather than declaring it
/// directly) still resolve. This mirrors the original non-cached lookup
/// behaviour byte-for-byte.
/// </remarks>
internal static class MethodLookupCache
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), MethodInfo?> _cache = new();

    /// <summary>
    /// Resolves a method by name on <paramref name="type"/>, falling back to
    /// methods declared on implemented interfaces. Returns <c>null</c> if no
    /// method of that name exists anywhere on the type.
    /// </summary>
    public static MethodInfo? GetInvocableMethod(Type type, string name)
    {
        return _cache.GetOrAdd((type, name), static key =>
        {
            var (t, n) = key;
            return t.GetMethod(n)
                ?? t.GetInterfaces()
                    .SelectMany(i => i.GetMethods())
                    .FirstOrDefault(m => m.Name == n);
        });
    }
}

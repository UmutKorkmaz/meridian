using System.Text.Json.Serialization.Metadata;

namespace Meridian.Mediator.Caching;

/// <summary>Explicit allowlist of stable contracts. The first serializer/codec freezes registrations.</summary>
public sealed class CacheContractRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Contract> _contracts = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _types = new();
    private bool _frozen;

    /// <summary>Registers an exact local type, positive schema and explicit JSON metadata. Exact repeated metadata registrations are idempotent before freeze.</summary>
    public CacheContractRegistry RegisterContract<T>(string id, int schemaVersion, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (!IsValidId(id)) throw new ArgumentException("Contract IDs require 1-128 ASCII letters, digits, dots, underscores or hyphens.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(schemaVersion, 0);
        lock (_gate)
        {
            if (_frozen) throw new InvalidOperationException("Cache contract registrations are frozen.");
            if (_contracts.TryGetValue(id, out var existing))
            {
                if (existing.Type == typeof(T) && existing.Schema == schemaVersion && ReferenceEquals(existing.Metadata, typeInfo)) return this;
                throw new InvalidOperationException("Conflicting cache contract registration.");
            }
            if (_types.ContainsKey(typeof(T))) throw new InvalidOperationException("A local type can have only one cache contract ID.");
            _contracts.Add(id, new(typeof(T), schemaVersion, typeInfo));
            _types.Add(typeof(T), id);
        }
        return this;
    }

    /// <summary>Gets the schema for a locally registered contract; unknown local IDs are configuration failures.</summary>
    public int GetSchemaVersion(string contractId) => Get(contractId).Schema;
    internal void Freeze() { lock (_gate) { foreach (var c in _contracts.Values) c.Metadata.MakeReadOnly(); _frozen = true; } }
    internal Contract Get(string id) { ArgumentNullException.ThrowIfNull(id); lock (_gate) { return _contracts.TryGetValue(id, out var c) ? c : throw new InvalidOperationException("Cache contract is not registered locally."); } }
    internal bool TryGet(string id, out Contract contract) { lock (_gate) { return _contracts.TryGetValue(id, out contract!); } }
    internal string GetId(Type type) { ArgumentNullException.ThrowIfNull(type); lock (_gate) { return _types.TryGetValue(type, out var id) ? id : throw new InvalidOperationException("Cache value type is not registered locally."); } }
    internal static bool IsValidId(string? id) => id is { Length: > 0 and <= 128 } && id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');
    internal sealed record Contract(Type Type, int Schema, JsonTypeInfo Metadata)
    {
        internal bool AllowsNull => !Type.IsValueType || Nullable.GetUnderlyingType(Type) is not null;
        internal bool Accepts(object value) => Type.IsInstanceOfType(value) || Nullable.GetUnderlyingType(Type)?.IsInstanceOfType(value) == true;
    }
}

using System.Text.Json;

namespace Meridian.Mediator.Caching;

/// <summary>JSON serialization using explicit JsonTypeInfo only, without reflection fallback or remote type activation.</summary>
public sealed class JsonCacheValueSerializer : ICacheValueSerializer
{
    private readonly CacheContractRegistry _registry;
    /// <summary>Creates a serializer and freezes the supplied allowlist.</summary>
    public JsonCacheValueSerializer(CacheContractRegistry registry) { ArgumentNullException.ThrowIfNull(registry); registry.Freeze(); _registry = registry; }
    /// <inheritdoc/>
    public string GetContractId(Type type) => _registry.GetId(type);
    /// <inheritdoc/>
    public byte[] Serialize(object? value, string contractId)
    {
        var c = _registry.Get(contractId);
        if (value is null ? !c.AllowsNull : !c.Accepts(value)) throw new InvalidOperationException("Value does not match the registered cache contract.");
        return JsonSerializer.SerializeToUtf8Bytes(value, c.Metadata);
    }
    /// <inheritdoc/>
    public object? Deserialize(ReadOnlyMemory<byte> payload, string contractId)
    {
        var c = _registry.Get(contractId);
        object? value;
        // Converters must reserve JsonException for input-data rejection, not programming faults.
        try { value = JsonSerializer.Deserialize(payload.Span, c.Metadata); }
        catch (JsonException) { throw new CacheValueDataException(CacheValueRejectionReason.MalformedBody); }
        if (value is null && !c.AllowsNull) throw new CacheValueDataException(CacheValueRejectionReason.MalformedBody);
        return value;
    }
}

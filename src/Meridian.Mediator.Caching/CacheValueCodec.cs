using System.Buffers.Binary;
using System.Text;

namespace Meridian.Mediator.Caching;

/// <summary>Shared MRCV v1 framing over an opaque serializer body. C4 consumes rejection directly and must fail closed rather than apply ordinary-cache eviction.</summary>
public sealed class CacheValueCodec
{
    private readonly CacheContractRegistry _registry;
    private readonly ICacheValueSerializer _serializer;
    private readonly int _maximumEntryBytes;
    /// <summary>Creates a codec, freezing the registry and copying the positive total entry limit (default 1 MiB).</summary>
    public CacheValueCodec(CacheContractRegistry registry, ICacheValueSerializer serializer, CacheProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(serializer);
        _maximumEntryBytes = options?.MaximumEntryBytes ?? 1024 * 1024;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_maximumEntryBytes, 0, nameof(options));
        registry.Freeze(); _registry = registry; _serializer = serializer;
    }
    /// <summary>Frames a compatible value (including known reference/nullable null) under a local contract. Local failures propagate.</summary>
    public byte[] Encode(object? value, string contractId)
    {
        var c = _registry.Get(contractId);
        if (value is null ? !c.AllowsNull : !c.Accepts(value)) throw new InvalidOperationException("Value does not match the registered cache contract.");
        var body = _serializer.Serialize(value, contractId) ?? throw new InvalidOperationException("Cache serializer returned a null body.");
        var size = 11L + contractId.Length + body.Length;
        if (size > _maximumEntryBytes) throw new InvalidOperationException("Cache entry exceeds the configured maximum size.");
        var bytes = new byte[(int)size]; "MRCV"u8.CopyTo(bytes); bytes[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(5), (ushort)contractId.Length);
        Encoding.ASCII.GetBytes(contractId, bytes.AsSpan(7));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(7 + contractId.Length), c.Schema);
        body.CopyTo(bytes, 11 + contractId.Length); return bytes;
    }
    /// <summary>Validates framing, immutable allowlist and schema before body deserialization; expected local IDs are configuration, never remote type names.</summary>
    public object? Decode(ReadOnlyMemory<byte> envelope, string? expectedContractId = null)
    {
        if (expectedContractId is not null) _registry.Get(expectedContractId);
        if (envelope.Length > _maximumEntryBytes) throw new CacheValueDataException(CacheValueRejectionReason.OversizedEntry);
        var bytes = envelope.Span;
        if (bytes.Length < 11 || !bytes[..4].SequenceEqual("MRCV"u8)) throw new CacheValueDataException(CacheValueRejectionReason.MalformedEnvelope);
        if (bytes[4] != 1) throw new CacheValueDataException(CacheValueRejectionReason.UnsupportedVersion);
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[5..]);
        if (length is 0 or > 128 || bytes.Length < 11 + length) throw new CacheValueDataException(CacheValueRejectionReason.MalformedEnvelope);
        var idBytes = bytes.Slice(7, length);
        foreach (var b in idBytes) if (b > 127) throw new CacheValueDataException(CacheValueRejectionReason.MalformedEnvelope);
        var id = Encoding.ASCII.GetString(idBytes);
        if (!CacheContractRegistry.IsValidId(id)) throw new CacheValueDataException(CacheValueRejectionReason.MalformedEnvelope);
        var schema = BinaryPrimitives.ReadInt32BigEndian(bytes[(7 + length)..]);
        if (schema <= 0) throw new CacheValueDataException(CacheValueRejectionReason.MalformedEnvelope);
        if (!_registry.TryGet(id, out var c)) throw new CacheValueDataException(CacheValueRejectionReason.UnknownContract);
        if (schema != c.Schema) throw new CacheValueDataException(CacheValueRejectionReason.SchemaMismatch);
        if (expectedContractId is not null && !StringComparer.Ordinal.Equals(id, expectedContractId)) throw new CacheValueDataException(CacheValueRejectionReason.ContractMismatch);
        var value = _serializer.Deserialize(envelope[(11 + length)..], id);
        if (value is null && !c.AllowsNull) throw new CacheValueDataException(CacheValueRejectionReason.MalformedBody);
        if (value is not null && !c.Accepts(value)) throw new InvalidOperationException("Cache serializer returned an incompatible local value.");
        return value;
    }
}

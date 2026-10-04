namespace Meridian.Mediator.Caching;

/// <summary>Serializes opaque cache bodies using explicitly registered stable contracts.</summary>
public interface ICacheValueSerializer
{
    /// <summary>Returns the contract for the exact registered local runtime type.</summary>
    string GetContractId(Type type);
    /// <summary>Serializes a value under its known allowlisted contract.</summary>
    byte[] Serialize(object? value, string contractId);
    /// <summary>Deserializes only the validated registered contract. Data rejection may throw <see cref="CacheValueDataException"/>; programming failures must propagate.</summary>
    object? Deserialize(ReadOnlyMemory<byte> payload, string contractId);
}

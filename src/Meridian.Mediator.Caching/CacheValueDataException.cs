namespace Meridian.Mediator.Caching;

/// <summary>Finite redacted remote data rejection codes; never include identity, key or payload.</summary>
public enum CacheValueRejectionReason
{
    /// <summary>Invalid or truncated outer frame.</summary>
    MalformedEnvelope,
    /// <summary>Unknown outer framing version.</summary>
    UnsupportedVersion,
    /// <summary>Remote ID is absent from the local allowlist.</summary>
    UnknownContract,
    /// <summary>Remote schema differs from the registered schema.</summary>
    SchemaMismatch,
    /// <summary>Remote contract differs from the known expected contract.</summary>
    ContractMismatch,
    /// <summary>Serializer rejects input data.</summary>
    MalformedBody,
    /// <summary>Configured total entry limit exceeded.</summary>
    OversizedEntry,
    /// <summary>Ordinary cache cannot retain a null response.</summary>
    NullEntry
}

/// <summary>Explicit remote-data failure boundary. Unrelated serializer/programming failures must use ordinary exceptions.</summary>
public sealed class CacheValueDataException : Exception
{
    /// <summary>Creates a redacted rejection without retaining the original payload exception.</summary>
    public CacheValueDataException(CacheValueRejectionReason reason) : base("Cache entry data was rejected.")
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Reason = reason;
    }
    /// <summary>Gets the finite rejection reason.</summary>
    public CacheValueRejectionReason Reason { get; }
}

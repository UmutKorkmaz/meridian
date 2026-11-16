using System.Buffers.Binary;
using System.Text;

namespace Meridian.Mediator.Caching;

/// <summary>Versioned injective encoding among decorated key tuples. Raw passthrough does not reserve a separate backend namespace.</summary>
public static class CacheKeyDecorator
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>Composes application/scope/key with explicit presence and byte lengths. With neither dimension, returns the raw key verbatim.</summary>
    /// <remarks>Raw clients can address any decorated output verbatim. Private scoped values require trusted uniformly decorated clients with excluded untrusted raw access, or an isolated physical backend namespace; a prefix is not access control.</remarks>
    public static string Compose(string key, string? applicationPrefix, string? scope)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateDimension(applicationPrefix, nameof(applicationPrefix)); ValidateDimension(scope, nameof(scope));
        if (applicationPrefix is null && scope is null) return key;
        var prefix = applicationPrefix is null ? [] : Utf8.GetBytes(applicationPrefix);
        var partition = scope is null ? [] : Utf8.GetBytes(scope);
        var logical = Utf8.GetBytes(key);
        var bytes = new byte[checked(14 + prefix.Length + partition.Length + logical.Length)];
        bytes[0] = applicationPrefix is null ? (byte)0 : (byte)1;
        bytes[1] = scope is null ? (byte)0 : (byte)1;
        var offset = 2;
        foreach (var segment in new[] { prefix, partition, logical })
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), segment.Length); offset += 4;
            segment.CopyTo(bytes, offset); offset += segment.Length;
        }
        return "MRCKey1:" + Convert.ToBase64String(bytes);
    }
    internal static void ValidateDimension(string? value, string name)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Cache namespace dimensions must be nonempty.", name);
        if (value is not null) Utf8.GetByteCount(value);
    }
}

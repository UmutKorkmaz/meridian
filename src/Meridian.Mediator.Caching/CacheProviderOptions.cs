namespace Meridian.Mediator.Caching;

/// <summary>Options copied by the optional cache adapters.</summary>
public sealed class CacheProviderOptions
{
    /// <summary>Gets or sets the positive default entry duration. Defaults to five minutes.</summary>
    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets or sets an explicit nonempty application namespace; absent preserves existing raw/composed keys.</summary>
    public string? ApplicationKeyPrefix { get; set; }
    /// <summary>Gets or sets the positive maximum total distributed envelope size. Defaults to 1 MiB.</summary>
    public int MaximumEntryBytes { get; set; } = 1024 * 1024;
}

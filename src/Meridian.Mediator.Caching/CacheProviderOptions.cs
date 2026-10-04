namespace Meridian.Mediator.Caching;

/// <summary>Options for the optional memory cache adapter.</summary>
public sealed class CacheProviderOptions
{
    /// <summary>Gets or sets the positive default entry duration. Defaults to five minutes.</summary>
    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMinutes(5);
}

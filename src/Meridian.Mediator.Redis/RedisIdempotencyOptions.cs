namespace Meridian.Mediator.Redis;

/// <summary>Snapshot options for retained owned records. Pending claims never receive an expiry TTL.</summary>
public sealed class RedisIdempotencyOptions
{
    /// <summary>Live lease duration; defaults to thirty seconds.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Completed replay retention; defaults to twenty-four hours.</summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Optional application namespace, composed exactly once at logical entry points.</summary>
    public string? ApplicationKeyPrefix { get; set; }
    /// <summary>Maximum framed payload bytes; defaults to one MiB.</summary>
    public int MaximumEntryBytes { get; set; } = 1024 * 1024;

    internal static long Milliseconds(TimeSpan value, string name)
    {
        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0 || value.TotalMilliseconds < 1 || value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(name, "Duration must be whole positive milliseconds no greater than Int32.MaxValue.");
        return (long)value.TotalMilliseconds;
    }
}

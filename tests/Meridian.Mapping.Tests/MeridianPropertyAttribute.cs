using System.Globalization;
using FsCheck.Xunit;

namespace Meridian.Mapping.Tests;

/// <summary>FsCheck scalar/depth budgets, optionally overridden for nightly runs.</summary>
public class MeridianPropertyAttribute : PropertyAttribute
{
    public MeridianPropertyAttribute(int defaultMaxTest = 100)
        : this(defaultMaxTest, Environment.GetEnvironmentVariable) { }

    internal MeridianPropertyAttribute(int defaultMaxTest, Func<string, string?> readEnvironment)
        : this(defaultMaxTest, "MERIDIAN_FSCHECK_MAX_TEST", readEnvironment) { }

    protected MeridianPropertyAttribute(int defaultMaxTest, string countVariable,
        Func<string, string?> readEnvironment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(defaultMaxTest);
        var count = readEnvironment(countVariable);
        if (count is null)
        {
            MaxTest = defaultMaxTest;
        }
        else if (int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            // Use the base setter: FsCheck consumes its internal configuration.
            MaxTest = parsed;
        }
        else
        {
            throw new ArgumentException($"{countVariable} must be a positive 32-bit integer.");
        }

        var replay = readEnvironment("MERIDIAN_FSCHECK_REPLAY");
        if (replay is null)
        {
            return; // Preserve FsCheck's random seed on ordinary PR runs.
        }

        var parts = replay.Split(',');
        if (parts.Length != 2
            || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var gamma)
            || (gamma & 1) == 0)
        {
            throw new ArgumentException("MERIDIAN_FSCHECK_REPLAY must be seed,gamma (unsigned 64-bit integers; gamma must be odd).");
        }

        Replay = replay;
        QuietOnSuccess = false;
    }
}

/// <summary>Collection budgets are independent of scalar/depth overrides.</summary>
public sealed class CollectionMeridianPropertyAttribute : MeridianPropertyAttribute
{
    public CollectionMeridianPropertyAttribute(int defaultMaxTest = 50)
        : this(defaultMaxTest, Environment.GetEnvironmentVariable) { }

    internal CollectionMeridianPropertyAttribute(int defaultMaxTest, Func<string, string?> readEnvironment)
        : base(defaultMaxTest, "MERIDIAN_FSCHECK_COLLECTION_MAX_TEST", readEnvironment) { }
}

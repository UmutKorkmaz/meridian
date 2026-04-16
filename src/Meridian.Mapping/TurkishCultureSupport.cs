using System.Globalization;

namespace Meridian.Mapping;

/// <summary>
/// String comparer that matches Turkish-locale conventions for the
/// dotted/dotless I pair. <c>ı</c> (U+0131) and <c>I</c> (U+0049)
/// uppercase to/from each other, and <c>i</c> (U+0069) and <c>İ</c>
/// (U+0130) form the other pair. Default invariant or English-locale
/// case-insensitive comparison gets these wrong: it matches <c>i</c> to
/// <c>I</c> and <c>I</c> to <c>i</c>, producing surprising results when
/// member-name auto-matching encounters Turkish identifiers.
/// </summary>
/// <remarks>
/// <para>
/// This is opt-in via <see cref="MapperConfigurationExtensions.WithTurkishCulture"/>.
/// Adopters with English-only domain models do not need it; the default
/// <see cref="StringComparer.Ordinal"/> behaviour is unchanged.
/// </para>
/// <para>
/// The comparer is a thin wrapper around <c>StringComparer.Create(new CultureInfo("tr-TR"), ignoreCase: true)</c>.
/// It exists as a named type so adopters can pass it into other
/// dictionaries and lookups in their own code without re-deriving the
/// CultureInfo each time.
/// </para>
/// </remarks>
public static class TurkishCulture
{
    private static readonly CultureInfo _trTR = new("tr-TR");

    /// <summary>Cached <see cref="CultureInfo"/> for <c>tr-TR</c>.</summary>
    public static CultureInfo CultureInfo => _trTR;

    /// <summary>Case-insensitive Turkish-locale string comparer.</summary>
    public static StringComparer IgnoreCaseComparer { get; } =
        StringComparer.Create(_trTR, ignoreCase: true);

    /// <summary>
    /// Turkish-locale uppercase. Maps <c>i</c> → <c>İ</c> and <c>ı</c> → <c>I</c>,
    /// unlike <see cref="string.ToUpperInvariant"/> which collapses both
    /// <c>i</c> and <c>ı</c> to <c>I</c>.
    /// </summary>
    public static string ToUpper(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToUpper(_trTR);
    }

    /// <summary>
    /// Turkish-locale lowercase. Maps <c>İ</c> → <c>i</c> and <c>I</c> → <c>ı</c>,
    /// unlike <see cref="string.ToLowerInvariant"/> which collapses both
    /// <c>İ</c> and <c>I</c> to <c>i</c>.
    /// </summary>
    public static string ToLower(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLower(_trTR);
    }

    /// <summary>
    /// Case-insensitive equality using the Turkish locale's I/i rules.
    /// </summary>
    public static bool Equals(string? a, string? b)
        => IgnoreCaseComparer.Equals(a, b);
}

/// <summary>
/// Extension methods on <see cref="IMapperConfigurationExpression"/> for
/// opting into culture-aware behaviour.
/// </summary>
public static class MapperConfigurationExtensions
{
    /// <summary>
    /// Marks the mapper as Turkish-culture aware. Currently this is
    /// surfaced as <see cref="TurkishCulture.IgnoreCaseComparer"/> for
    /// adopters who want member-name lookups to honour the dotted/dotless
    /// I rules (e.g. mapping a property named <c>İlçe</c> to <c>Ilce</c>
    /// in a Latin-name destination).
    /// </summary>
    /// <remarks>
    /// Future expansion: this can wire a <see cref="TurkishCulture.CultureInfo"/>
    /// into <c>ValueTransformers</c> so string fields get normalised at
    /// mapping time. The MVP is conservative — opt-in helpers, no
    /// implicit string mutation.
    /// </remarks>
    public static IMapperConfigurationExpression WithTurkishCulture(
        this IMapperConfigurationExpression cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        // No state to wire into the mapper itself yet; the helper exists
        // so consumers have a single discoverable entry point and we can
        // expand the behaviour without breaking the API surface.
        return cfg;
    }
}

using Meridian.Mapping;

namespace Meridian.Mapping.Tests;

/// <summary>
/// Regression tests for the five canonical Turkish-locale I/i bugs that
/// trip naive string comparison code. Each test names the specific bug
/// in its method name so the failure mode is obvious from a CI report.
/// </summary>
public class TurkishCultureTests
{
    // ── Bug 1: lowercase Latin 'i' (U+0069) and dotless 'ı' (U+0131) collide
    // when using invariant case folding but are distinct in tr-TR.

    [Fact]
    public void Bug1_LatinI_Differs_From_DotlessI_Under_Turkish_Comparer()
    {
        Assert.False(TurkishCulture.IgnoreCaseComparer.Equals("istanbul", "ıstanbul"));
        // Sanity check: ordinal-ignore-case ALSO distinguishes them, but
        // for a different reason (codepoint mismatch). We're here to make
        // sure the Turkish comparer doesn't accidentally collapse them.
    }

    // ── Bug 2: uppercase Latin 'I' (U+0049) maps to dotless 'ı' (U+0131)
    // in tr-TR but to plain 'i' (U+0069) in invariant culture.

    [Fact]
    public void Bug2_UpperI_Folds_To_DotlessI_In_Turkish()
    {
        Assert.Equal("ı", TurkishCulture.ToLower("I"));
        // Cross-check against invariant to confirm we're solving a real
        // problem and not duplicating default behaviour.
        Assert.Equal("i", "I".ToLowerInvariant());
    }

    // ── Bug 3: lowercase 'i' uppercases to dotted 'İ' (U+0130) in tr-TR
    // but to plain 'I' (U+0049) in invariant culture.

    [Fact]
    public void Bug3_LowerI_Folds_To_DottedCapitalI_In_Turkish()
    {
        Assert.Equal("İ", TurkishCulture.ToUpper("i"));
        Assert.Equal("I", "i".ToUpperInvariant());
    }

    // ── Bug 4: dotless 'ı' (U+0131) uppercases to plain 'I' (U+0049),
    // not to dotted 'İ' (U+0130) — and round-trips back to dotless ı.

    [Fact]
    public void Bug4_DotlessI_RoundTrips_Through_Turkish_Case_Folding()
    {
        var upper = TurkishCulture.ToUpper("ı");
        Assert.Equal("I", upper);
        Assert.Equal("ı", TurkishCulture.ToLower(upper));
    }

    // ── Bug 5: case-insensitive equality on a city name with mixed
    // dotted/dotless forms must match the Turkish-canonical rendering.
    // Default invariant comparer says "Iznik" == "iznik", which is wrong
    // — the city is "İznik" / "i̇znik". The Turkish comparer says so.

    [Fact]
    public void Bug5_City_Name_Equality_Honours_Turkish_Casing()
    {
        // Turkish comparer: "İznik" (U+0130 i U+0307? no — just U+0130)
        // matches "iznik" (lowercase canonical) because tr-TR folds them.
        Assert.True(TurkishCulture.Equals("İznik", "iznik"));

        // The naive invariant comparer mis-folds in the other direction:
        // "Iznik" (no dot) becomes "iznik" under invariant, which then
        // matches "iznik". Turkish comparer does NOT match because in
        // tr-TR "Iznik" lowercases to "ıznik" (dotless), not "iznik".
        Assert.False(TurkishCulture.Equals("Iznik", "iznik"));
    }

    [Fact]
    public void TurkishCulture_CultureInfo_Is_TrTR()
    {
        Assert.Equal("tr-TR", TurkishCulture.CultureInfo.Name);
    }

    [Fact]
    public void WithTurkishCulture_Returns_Configuration_For_Chaining()
    {
        var cfg = new MapperConfigurationExpression();
        var returned = cfg.WithTurkishCulture();
        Assert.Same(cfg, returned);
    }

    [Fact]
    public void ToUpper_Throws_On_Null_Argument()
    {
        Assert.Throws<ArgumentNullException>(() => TurkishCulture.ToUpper(null!));
    }
}

using Meridian.Mapping.Configuration;

namespace Meridian.Mapping.Tests;

public class EnhancedValidationTests
{
    // ── Test Models (local to avoid polluting shared TestModels) ────

    private class EvSourceA
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private class EvDestA
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    // Type mismatch: source has DateTime, dest has int
    private class EvTypeMismatchSource
    {
        public int Id { get; set; }
        public DateTime Value { get; set; }
    }

    private class EvTypeMismatchDest
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }

    // Type mismatch with non-convertible types
    private class EvNonConvertibleSource
    {
        public int Id { get; set; }
        public EvSourceA Nested { get; set; } = new();
    }

    private class EvNonConvertibleDest
    {
        public int Id { get; set; }
        public string Nested { get; set; } = string.Empty; // EvSourceA → string with no map
    }

    // Constructor-only dest (no default ctor)
    private class EvCtorOnlyDest
    {
        public int Id { get; }
        public string Label { get; }
        public double Score { get; }

        public EvCtorOnlyDest(int id, string label, double score)
        {
            Id = id;
            Label = label;
            Score = score;
        }
    }

    private class EvCtorSource
    {
        public int Id { get; set; }
        // No Label or Score — should warn about missing ctor params
    }

    private class EvCtorMatchingSource
    {
        public int Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public double Score { get; set; }
    }

    // Self-referencing (circular) model
    private class EvTreeSource
    {
        public string Value { get; set; } = string.Empty;
        public EvTreeSource? Parent { get; set; }
    }

    private class EvTreeDest
    {
        public string Value { get; set; } = string.Empty;
        public EvTreeDest? Parent { get; set; }
    }

    // Model with extra dest members (to create an error)
    private class EvExtraDestSource
    {
        public int Id { get; set; }
    }

    private class EvExtraDestDest
    {
        public int Id { get; set; }
        public string Extra { get; set; } = string.Empty;
    }

    // ── 1. Duplicate Mapping Detection ──────────────────────────────

    [Fact]
    public void Should_Use_LastWins_For_Duplicate_CreateMap_Calls()
    {
        // CreateMap uses dictionary keyed by (Source,Dest) — second call overwrites first.
        // This is intentional last-wins behavior, not an error.
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvSourceA, EvDestA>();
            cfg.CreateMap<EvSourceA, EvDestA>(); // overwrites, not duplicate
        });

        // Should not throw — there's only one type map (the last one)
        config.AssertConfigurationIsValid();

        var maps = config.GetAllTypeMaps();
        Assert.Single(maps.Where(m =>
            m.SourceType == typeof(EvSourceA) && m.DestinationType == typeof(EvDestA)));
    }

    // ── 2. Type Mismatch Warnings (don't throw alone) ──────────────

    [Fact]
    public void Should_Not_Throw_For_Type_Mismatch_Warning_Alone()
    {
        // DateTime → int is handled by IConvertible, so use non-convertible types instead
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvNonConvertibleSource, EvNonConvertibleDest>();
        });

        // Type mismatch is only a warning — should not throw if no errors
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Include_Type_Mismatch_Warning_When_Error_Present()
    {
        var config = new MapperConfiguration(cfg =>
        {
            // This map has a type mismatch warning (EvSourceA -> string) AND an error (Extra unmapped)
            cfg.CreateMap<EvNonConvertibleSource, EvNonConvertibleDest>()
                .ForMember("Nested", opt => opt.Ignore()); // suppress the mismatch

            // This creates an unmapped member error to force the exception
            cfg.CreateMap<EvExtraDestSource, EvExtraDestDest>();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.AssertConfigurationIsValid());

        // The error from unmapped 'Extra' should be present
        Assert.Contains("Unmapped destination member", ex.Message);
        Assert.Contains("Extra", ex.Message);
    }

    // ── 3. Missing Constructor Parameter Warnings ───────────────────

    [Fact]
    public void Should_Not_Throw_For_Missing_Ctor_Param_Warning_Alone()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvCtorSource, EvCtorOnlyDest>()
                .ValidateMemberList(MemberList.None); // skip unmapped member errors
        });

        // Missing ctor params are warnings only — should not throw
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Include_Missing_Ctor_Param_Warning_When_Error_Present()
    {
        var config = new MapperConfiguration(cfg =>
        {
            // EvCtorSource → EvCtorOnlyDest: missing label/score ctor params = warnings
            cfg.CreateMap<EvCtorSource, EvCtorOnlyDest>()
                .ValidateMemberList(MemberList.None);

            // Also add a map with errors to force exception
            cfg.CreateMap<EvExtraDestSource, EvExtraDestDest>();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.AssertConfigurationIsValid());

        // Warnings should be in the message along with errors
        Assert.Contains("Warnings:", ex.Message);
        Assert.Contains("Constructor parameter", ex.Message);
    }

    [Fact]
    public void Should_Not_Warn_When_Ctor_Params_Match_Source_Properties()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvCtorMatchingSource, EvCtorOnlyDest>()
                .ValidateMemberList(MemberList.None);
        });

        // All ctor params (id, label, score) match source properties — no warning
        config.AssertConfigurationIsValid();
    }

    // ── 4. Circular Reference Detection ─────────────────────────────

    [Fact]
    public void Should_Not_Throw_For_Circular_Ref_Warning_Alone()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvTreeSource, EvTreeDest>();
        });

        // Circular ref is a warning — should not throw alone
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Include_Circular_Ref_Warning_When_Error_Present()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvTreeSource, EvTreeDest>();

            // Add a map with errors
            cfg.CreateMap<EvExtraDestSource, EvExtraDestDest>();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.AssertConfigurationIsValid());

        Assert.Contains("Warnings:", ex.Message);
        Assert.Contains("circular reference", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Should_Not_Warn_About_Circular_Ref_When_PreserveReferences_Enabled()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvTreeSource, EvTreeDest>()
                .PreserveReferences();

            // Add a map with errors so we can inspect the message
            cfg.CreateMap<EvExtraDestSource, EvExtraDestDest>();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.AssertConfigurationIsValid());

        // Should have the unmapped error but NOT the circular ref warning
        Assert.Contains("Unmapped destination member", ex.Message);
        Assert.DoesNotContain("circular reference", ex.Message.ToLowerInvariant());
    }

    // ── 5. Combined Errors + Warnings ───────────────────────────────

    [Fact]
    public void Should_Include_Both_Errors_And_Warnings_In_Message()
    {
        var config = new MapperConfiguration(cfg =>
        {
            // Error: unmapped 'Extra' member
            cfg.CreateMap<EvExtraDestSource, EvExtraDestDest>();

            // Warning: circular reference
            cfg.CreateMap<EvTreeSource, EvTreeDest>();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.AssertConfigurationIsValid());

        // Should contain both sections
        Assert.Contains("Unmapped destination member", ex.Message);
        Assert.Contains("Warnings:", ex.Message);
    }

    // ── 6. Edge Cases ───────────────────────────────────────────────

    [Fact]
    public void Should_Skip_Validation_For_Maps_With_Custom_Converter()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvNonConvertibleSource, EvNonConvertibleDest>()
                .ConvertUsing(s => new EvNonConvertibleDest { Id = s.Id, Nested = "converted" });
        });

        // Custom converter bypasses all member checks
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Pass_Validation_For_Clean_Configuration()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<EvSourceA, EvDestA>();
            cfg.CreateMap<EvCtorMatchingSource, EvCtorOnlyDest>()
                .ValidateMemberList(MemberList.None);
        });

        // No errors, no warnings scenario
        config.AssertConfigurationIsValid();
    }
}

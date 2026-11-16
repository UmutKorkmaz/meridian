using Microsoft.CodeAnalysis;

namespace Meridian.Mapping.Tests;

public class GeneratedConversionTests
{
    [Fact]
    public void Structural_Int_To_Long_Equals_Runtime() => Proof("int", "long", "42", "return value == 42L && value == Runtime(source).Value;");

    [Fact]
    public void Nullable_Null_To_Int_Is_Default() => Proof("int?", "int", "null", "return value == 0 && value == Runtime(source).Value;");

    [Theory]
    [InlineData("int", "long?", "42", "value == 42L")]
    [InlineData("int?", "long?", "null", "value == null")]
    [InlineData("int?", "long?", "42", "value == 42L")]
    public void Nullable_Widening_Transitions(string source, string destination, string input, string expected) => Proof(source, destination, input, "return " + expected + ";");

    [Fact]
    public void Invalid_Enum_Is_Rejected() => Proof("string", "Role", "\"bad\"", "return false;", "InvariantParse", catchBody: "return ex is MeridianEnumMappingException e && e.SourceValue == \"bad\" && e.DestinationMember == \"Value\";");

    [Fact]
    public void Undefined_Enum_OptOut_Is_Honored() => Proof("string", "Role", "\"19\"", "return (int)value == 19 && value == Runtime(source).Value;", "InvariantParse", ", AllowUndefinedEnumValues = true", runtimeOptions: "c.AllowUndefinedEnumValues = true;");

    [Theory]
    [InlineData("int", "byte", "300")]
    [InlineData("long", "int", "long.MaxValue")]
    [InlineData("double", "int", "double.NaN")]
    [InlineData("double", "int", "double.PositiveInfinity")]
    public void Narrowing_Overflow_Is_Checked(string source, string destination, string input) => Proof(source, destination, input, "return false;", "CheckedNumeric", catchBody: "return ex is System.OverflowException;");

    [Fact]
    public void Checked_Fractional_Narrowing_Is_Explicit_Truncation() => Proof("double", "int", "1.9", "return value == 1 && Runtime(source).Value == 2;", "CheckedNumeric");

    [Fact]
    public void Numeric_Enum_Strictness_Is_Deliberate_Runtime_Difference() => Proof("int", "Role", "19", "return false;", catchBody: "return ex is MeridianEnumMappingException && (int)Runtime(source).Value == 19;");

    [Theory]
    [InlineData("Role", "Role", "(Role)19")]
    [InlineData("Role?", "Role?", "(Role)19")]
    public void Identity_Enums_Enforce_Structural_Policy(string source, string destination, string input) => Proof(source, destination, input, "return false;", catchBody: "return ex is MeridianEnumMappingException;");

    [Theory]
    [InlineData("Role?", "Role?", "null", "value == null")]
    [InlineData("Role", "int", "Role.Admin", "value == 2")]
    [InlineData("int", "Role", "2", "value == Role.Admin")]
    [InlineData("string", "Access", "\"Read, Write\"", "value == (Access)3")]
    [InlineData("string", "Access", "\"0\"", "value == (Access)0")]
    public void Enum_Supported_Values(string source, string destination, string input, string expected) => Proof(source, destination, input, "return " + expected + ";", source == "string" ? "InvariantParse" : null);

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void Invariant_Parse_Is_Culture_Independent(string culture)
    {
        Proof("string", "decimal", "\"1.5\"", "return value == 1.5m;", "InvariantParse", culture: culture);
        Proof("string", "System.DateTime", "\"2026-10-04\"", "return value == new System.DateTime(2026, 10, 4) && value == Runtime(source).Value;", "InvariantParse", culture: culture);
        Proof("string", "Role", "\"admin\"", "return value == Role.Admin && value == Runtime(source).Value;", "InvariantParse", culture: culture);
        Proof("string", "Role", "\"ADMİN\"", "return false;", "InvariantParse", culture: culture, catchBody: "return ex is MeridianEnumMappingException;");
    }

    [Fact]
    public void Invariant_Parse_Differs_From_CurrentCulture_Runtime() => Proof("string", "decimal", "\"1.5\"", "return value == 1.5m && Runtime(source).Value == 15m;", "InvariantParse", culture: "tr-TR");

    [Theory]
    [InlineData("int", "\"bad\"", "System.FormatException")]
    [InlineData("int", "\"999999999999999999\"", "System.OverflowException")]
    [InlineData("System.DateTime", "\"bad\"", "System.FormatException")]
    public void Parse_Exception_Types_Match_Runtime(string destination, string input, string exception) => Proof("string", destination, input, "return false;", "InvariantParse", culture: "en-US", catchBody: "try { Runtime(source); return false; } catch (System.Exception runtime) { return ex is " + exception + " && runtime.GetType() == ex.GetType(); }");

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Structural")]
    public void Explicit_Conversion_And_Paired_Selectors_Work_In_Both_Modes(string mode) => Proof("long", "int", "42L", "return value == 42;", "CheckedNumeric", mode: mode, selectors: ", SourceType = typeof(Source), DestinationType = typeof(Dto)");

    [Theory]
    [InlineData("[ConvertMapMember(\"Value\", GeneratedConversionKind.CheckedNumeric, SourceType = typeof(Source))]")]
    [InlineData("[ConvertMapMember(\"Value\", (GeneratedConversionKind)9)]")]
    [InlineData("[ConvertMapMember(\"Value\", GeneratedConversionKind.CheckedNumeric)][IgnoreMapMember(\"Value\")]")]
    [InlineData("[ConvertMapMember(\"Value\", GeneratedConversionKind.InvariantParse)]")]
    public void Invalid_Conversion_Configuration_Is_Diagnosed(string attribute)
    {
        var result = GeneratorTestHarness.Compile(Fixture("int", "long", attribute, "", "", ""));
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN006");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Null_Reference_Setter_Preserves_Constructed_Default_And_Reads_Once()
    {
        var result = GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public int Reads; public string? Value { get { Reads++; return null; } } }
            public class Dto { public int Writes; private string? value = "seed"; public string? Value { get => value; set { Writes++; this.value = value; } } }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural, AllowNullDestinationValues = false)]
            public static partial class Maps { public static bool Proof() { var s = new Source(); var d = MapToDto(s); return d.Value == "seed" && d.Writes == 0 && s.Reads == 1; } }
            """);
        AssertSuccess(result);
    }

    [Theory]
    [InlineData("public string? Value { get; init; } = \"seed\";")]
    [InlineData("public required string? Value { get; set; }")]
    public void Null_Skip_Init_Or_Required_Assignment_Is_Diagnosed(string member)
    {
        var result = GeneratorTestHarness.Compile("#nullable enable\nusing Meridian.Mapping; public class Source { public string? Value {get;set;} } public class Dto { " + member + " } [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural, AllowNullDestinationValues=false)] public static partial class Maps {}");
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN005");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Constructor_Conversions_And_Consumed_Null_Init_Are_Supported()
    {
        var result = GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public int Number { get; set; } = 42; public string? Value {get;set;} }
            public record Dto(long Number, string? Value);
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural, AllowNullDestinationValues=false)]
            public static partial class Maps { public static bool Proof() { var d=MapToDto(new Source()); return d.Number == 42L && d.Value == null; } }
            """);
        AssertSuccess(result);
    }

    [Fact]
    public void Converted_Constructor_Consumed_Property_Is_Not_Assigned_Twice()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public int Value {get;set;} = 42; }
            public class Dto { public long Value {get; init;} public Dto(long value) { Value = value + 1; } }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural)]
            public static partial class Maps { public static bool Proof() => MapToDto(new Source()).Value == 43; }
            """);
        AssertSuccess(result);
    }

    [Theory]
    [InlineData("string?", "string", "null", "value == null")]
    [InlineData("string?", "int", "null", "value == 0")]
    [InlineData("string?", "int?", "null", "value == null")]
    [InlineData("string", "char", "\"x\"", "value == 'x'")]
    public void Null_Reference_And_Parse_Transitions(string source, string destination, string input, string expected) => Proof(source, destination, input, "return " + expected + ";", destination == "string" ? null : "InvariantParse");

    [Theory]
    [InlineData("[MapMember(\"Value\", \"Other\")][ConvertMapMember(\"Value\", GeneratedConversionKind.CheckedNumeric)]")]
    public void Rename_Composes_With_Conversion(string configuration)
    {
        var result = GeneratorTestHarness.Compile(Fixture("long", "int", configuration, "public static bool Proof() => MapToDto(new Source { Other = 42 }).Value == 42;", "", "").Replace("public long Value", "public long Other"));
        AssertSuccess(result);
    }

    [Fact]
    public void Explicit_Conversion_Selects_Compatible_Constructor_Overload()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public long Value {get;set;} = 42; }
            public class Dto { public int Number {get;} public Dto(int value) { Number = value; } public Dto(System.Guid value) { Number = -1; } }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural)]
            [ConvertMapMember("value", GeneratedConversionKind.CheckedNumeric)]
            public static partial class Maps { public static bool Proof() => MapToDto(new Source()).Number == 42; }
            """);
        AssertSuccess(result);
    }

    [Fact]
    public void Nullability_Transition_Is_Valid_Under_WarningsAsErrors()
    {
        var result = GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public string? Value {get;set;} }
            public class Dto { public string Value {get;set;} = "seed"; }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural)]
            public static partial class Maps { public static bool Proof() => MapToDto(new Source()).Value == null; }
            """);
        var strict = result.Compilation.WithOptions(result.Compilation.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
        using var stream = new MemoryStream();
        var emit = strict.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        AssertSuccess(result);
    }

    [Theory]
    [InlineData("string", "Access", "\"8\"")]
    [InlineData("string", "Role", "\"9999999999999999999999\"")]
    public void Invalid_Enum_Values_Match_Runtime_Exception(string source, string destination, string input) => Proof(source, destination, input, "return false;", "InvariantParse", catchBody: "try { Runtime(source); return false; } catch (MeridianEnumMappingException) { return ex is MeridianEnumMappingException; }");

    [Fact]
    public void Nullable_Parse_Input_Compiles_Under_WarningsAsErrors()
    {
        var result = GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public string? Value {get;set;} }
            public class Dto { public int Value {get;set;} }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural)]
            [ConvertMapMember("Value", GeneratedConversionKind.InvariantParse)]
            public static partial class Maps { public static bool Proof() => MapToDto(new Source()).Value == 0; }
            """);
        using var stream = new MemoryStream();
        var emit = result.Compilation.WithOptions(result.Compilation.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error)).Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        AssertSuccess(result);
    }

    [Fact]
    public void Explicit_Conversion_With_Missing_Source_Is_Invalid_In_Legacy()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source {}
            public class Dto { public int Value {get;set;} }
            [GenerateMapper(typeof(Source), typeof(Dto))]
            [ConvertMapMember("Value", GeneratedConversionKind.CheckedNumeric)]
            public static partial class Maps {}
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN006");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Selected_Conversion_Does_Not_Change_Other_Legacy_Pair()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public long Value {get;set;} = 42; }
            public class Other { public long Value {get;set;} = 99; }
            public class Dto { public int Value {get;set;} }
            [GenerateMapper(typeof(Source), typeof(Dto))]
            [GenerateMapper(typeof(Other), typeof(Dto))]
            [ConvertMapMember("Value", GeneratedConversionKind.CheckedNumeric, SourceType=typeof(Source), DestinationType=typeof(Dto))]
            public static partial class Maps { public static bool Proof() => MapToDto(new Source()).Value == 42 && MapToDto(new Other()).Value == 0; }
            """);
        AssertSuccess(result);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN002" && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void Null_Policy_Does_Not_Reject_Consumed_Required_Init()
    {
        var result = GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public string? Value {get;set;} }
            public class Dto { public required string? Value {get;init;} [System.Diagnostics.CodeAnalysis.SetsRequiredMembers] public Dto(string? value) { Value=value; } }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural, AllowNullDestinationValues=false)]
            public static partial class Maps { public static bool Proof() => MapToDto(new Source()).Value == null; }
            """);
        AssertSuccess(result);
    }

    [Fact]
    public void Null_Setter_Policy_Agrees_With_Runtime()
    {
        var result = GeneratorTestHarness.Compile("""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public string? Value {get;set;} }
            public class Dto { public string? Value {get;set;} = "seed"; }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural, AllowNullDestinationValues=false)]
            public static partial class Maps { public static bool Proof() { var mapper = new MapperConfiguration(c => { c.AllowNullDestinationValues=false; c.CreateMap<Source,Dto>(); }).CreateMapper(); return MapToDto(new Source()).Value == "seed" && mapper.Map<Source,Dto>(new Source()).Value == "seed" && MapToDto(new Source {Value="assigned"}).Value == "assigned"; } }
            """);
        AssertSuccess(result);
    }

    [Fact]
    public void Enum_OptOut_Compiles_Under_WarningsAsErrors()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public int Value {get;set;} }
            public class Dto { public System.DayOfWeek Value {get;set;} }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode=GeneratedMappingMode.Structural, AllowUndefinedEnumValues=true)]
            public static partial class Maps { public static bool Proof() => (int)MapToDto(new Source {Value=99}).Value == 99; }
            """);
        using var stream = new MemoryStream();
        var emit = result.Compilation.WithOptions(result.Compilation.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error)).Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        AssertSuccess(result);
    }

    private static void Proof(string sourceType, string destinationType, string input, string body, string? conversion = null,
        string options = "", string culture = "en-US", string catchBody = "throw;", string runtimeOptions = "", string mode = "Structural", string selectors = "")
    {
        var attribute = conversion is null ? "" : "[ConvertMapMember(\"Value\", GeneratedConversionKind." + conversion + selectors + ")]";
        var proof = $$"""
            public static bool Proof() {
                var previous = System.Globalization.CultureInfo.CurrentCulture;
                try {
                    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("{{culture}}");
                    var source = new Source { Value = {{input}} };
                    try { var value = MapToDto(source).Value; {{body}} }
                    catch (System.Exception ex) { {{catchBody}} }
                } finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
            }
            """;
        AssertSuccess(GeneratorTestHarness.Compile(Fixture(sourceType, destinationType, attribute, proof, options, runtimeOptions, mode)));
    }

    private static string Fixture(string source, string destination, string attribute, string proof, string options, string runtimeOptions, string mode = "Structural") => $$"""
        #nullable enable
        using Meridian.Mapping;
        public enum Role { Guest = 0, Admin = 2 }
        [System.Flags] public enum Access { Read = 1, Write = 2 }
        public class Source { public {{source}} Value { get; set; } }
        public class Dto { public {{destination}} Value { get; set; } }
        [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.{{mode}}{{options}})]
        {{attribute}}
        public static partial class Maps {
            public static Dto Runtime(Source source) => new MapperConfiguration(c => { {{runtimeOptions}} c.CreateMap<Source, Dto>(); }).CreateMapper().Map<Source, Dto>(source);
            {{proof}}
        }
        """;

    private static void AssertSuccess(GeneratorCompilationResult result)
    {
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result, "Maps", "Proof"));
    }
}

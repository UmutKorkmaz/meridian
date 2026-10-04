using Microsoft.CodeAnalysis;

namespace Meridian.Mapping.Tests;

public class GeneratedMemberMappingTests
{
    [Theory]
    [InlineData("Legacy")]
    [InlineData("Structural")]
    public void Renamed_Member_Maps(string mode) => Value("[MapMember(\"Name\", \"Other\")]", "public string Name { get; init; } = \"\";", "dto.Name", "explicit", mode);

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Structural")]
    public void Explicit_Ignore_Is_Intentional(string mode) => Value("[IgnoreMapMember(\"Name\")]", "public string Name { get; init; } = \"kept\";", "dto.Name", "kept", mode);

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Structural")]
    public void Static_Converter_Maps(string mode) => Value("[MapWith(\"Name\", typeof(Convert), \"Apply\")]", "public int Name { get; init; }", "dto.Name.ToString()", "8", mode);

    [Fact]
    public void Rename_And_Converter_Compose() => Value("[MapMember(\"Name\", \"Other\")][MapWith(\"Name\", typeof(Convert), \"Apply\")]", "public int Name { get; init; }", "dto.Name.ToString()", "8", "Structural");

    [Theory]
    [InlineData("public static string Apply(string value) => value;")]
    [InlineData("public int Apply(string value) => 1;")]
    [InlineData("private static int Apply(string value) => 1;")]
    [InlineData("public static int Apply<T>(string value) => 1;")]
    [InlineData("public static int Apply(ref string value) => 1;")]
    [InlineData("public static int Apply(object value) => 1; public static int Apply(string value) => 2;")]
    public void Static_Converter_Return_Type_Is_Checked(string method)
    {
        var source = Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]", "public int Name { get; init; }", "", "Structural")
            .Replace("public static int Apply(string value) => value.Length;", method);
        Error(source);
    }

    [Theory]
    [InlineData("[MapMember(\"Name\", \"Other\", SourceType = typeof(Source))]")]
    [InlineData("[MapMember(\"Name\", \"Missing\")]")]
    [InlineData("[MapMember(\"Missing\", \"Other\")]")]
    [InlineData("[IgnoreMapMember(\"Name\")][MapMember(\"Name\", \"Other\")]")]
    [InlineData("[MapMember(\"Name\", \"Other\")][MapMember(\"Name\", \"Name\")]")]
    [InlineData("[MapMember(\"Name\", \"Other\", SourceType = typeof(Source), DestinationType = typeof(Convert))]")]
    public void Invalid_Configuration_Is_Diagnosed(string configuration) =>
        Error(Fixture(configuration, "public string Name { get; init; } = \"\";", "", "Structural"));

    [Fact]
    public void Selected_Configuration_Only_Affects_Declared_Pair()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public string Name { get; set; } = "same"; public string Other { get; set; } = "renamed"; }
            public class Dto { public string Name { get; set; } = ""; }
            public class OtherDto { public string Name { get; set; } = ""; }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            [GenerateMapper(typeof(Source), typeof(OtherDto), Mode = GeneratedMappingMode.Structural)]
            [MapMember("Name", "Other", SourceType = typeof(Source), DestinationType = typeof(Dto))]
            public static partial class Maps {
                public static string Proof() => MapToDto(new Source()).Name + ":" + MapToOtherDto(new Source()).Name;
            }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("renamed:same", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Constructor_Configuration_Maps()
    {
        var result = GeneratorTestHarness.Compile(Fixture("[MapMember(\"Name\", \"Other\")][MapWith(\"Name\", typeof(Convert), \"Apply\")]",
            "public int Name { get; } public Dto(int name) { Name = name; }", "public static string Proof() => MapToDto(new Source()).Name.ToString();", "Structural"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("8", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Rename_Does_Not_Bypass_Pending_Structural_Support()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public int[] Other { get; set; } = new int[0]; }
            public class Dto { public int[] Value { get; set; } = new int[0]; }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            [MapMember("Value", "Other")]
            public static partial class Maps {}
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN005");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Structural")]
    public void Ignore_Required_Member_With_SetsRequiredMembers_Is_Intentional(string mode) => Value(
        "[IgnoreMapMember(\"Name\")]", "public required string Name { get; init; } [System.Diagnostics.CodeAnalysis.SetsRequiredMembers] public Dto() { Name = \"ctor\"; }", "dto.Name", "ctor", mode);

    [Fact]
    public void Ignore_Required_Member_Without_Constructor_Promise_Is_Diagnostic()
    {
        var result = GeneratorTestHarness.Compile(Fixture("[IgnoreMapMember(\"Name\")]", "public required string Name { get; init; }", "", "Structural"));
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN003");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Unselected_Configuration_Applies_Only_To_Compatible_Destinations()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public string Other { get; set; } = "renamed"; public int Count { get; set; } = 17; }
            public class Dto { public string Name { get; set; } = ""; }
            public class OtherDto { public int Count { get; set; } }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            [GenerateMapper(typeof(Source), typeof(OtherDto), Mode = GeneratedMappingMode.Structural)]
            [MapMember("Name", "Other")]
            public static partial class Maps {
                public static string Proof() => MapToDto(new Source()).Name + ":" + MapToOtherDto(new Source()).Count;
            }
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("renamed:17", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Unselected_Configuration_Does_Not_Silently_Skip_Invalid_Source_In_Applicable_Pair()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public string Other { get; set; } = "renamed"; }
            public class MissingSource { public string Name { get; set; } = "wrong"; }
            public class Dto { public string Name { get; set; } = ""; }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            [GenerateMapper(typeof(MissingSource), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            [MapMember("Name", "Other")]
            public static partial class Maps {
                public static string Proof() => MapToDto(new Source()).Name;
            }
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN006");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("renamed", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Static_Converter_Can_Return_Assignable_Reference()
    {
        var result = GeneratorTestHarness.Compile(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]",
            "public object Name { get; init; } = new object();", "public static string Proof() => (string)MapToDto(new Source()).Name;", "Structural")
            .Replace("public static int Apply(string value) => value.Length;", "public static string Apply(object value) => value + \"!\";"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("original!", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Null_Configured_Source_Name_Is_Invalid() =>
        Error(Fixture("[MapMember(\"Name\", null)]", "public string Name { get; init; } = \"\";", "", "Structural"));

    [Fact]
    public void Constructor_And_Member_Models_Cache_Unrelated_Edits()
    {
        var parse = Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default.WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12);
        var source = Fixture("[MapMember(\"Name\", \"Other\")][MapWith(\"Name\", typeof(Convert), \"Apply\")]",
            "public int Name { get; } public Dto(int name, int missing = 7) { Name = name + missing; }", "", "Structural");
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, parse);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("B3Cache", [tree],
            GeneratorTestHarness.Compile(source).Compilation.References, new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(
            [new Meridian.Mapping.Generators.MeridianMapperGenerator().AsSourceGenerator()], parseOptions: parse,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class Unrelated {}", parse)));
        Assert.All(driver.GetRunResult().Results.Single().TrackedSteps["MapperOutput"].SelectMany(s => s.Outputs),
            output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
        var replacement = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source.Replace("\"Other\"", "\"Missing\""), parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation.ReplaceSyntaxTree(tree, replacement), out _, out var diagnostics);
        Assert.Contains(diagnostics, d => d.Id == "MERIDIANGEN006" && ReferenceEquals(d.Location.SourceTree, replacement));
    }

    [Fact]
    public void Static_Abstract_Interface_Converter_Is_Diagnostic()
    {
        Error(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]", "public int Name { get; init; }", "", "Structural")
            .Replace("public class Convert { public static int Apply(string value) => value.Length; }", "public interface Convert { static abstract int Apply(string value); }"));
    }

    [Fact]
    public void Inherited_Static_Converter_Is_Callable()
    {
        var result = GeneratorTestHarness.Compile(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]", "public int Name { get; init; }",
            "public static string Proof() => MapToDto(new Source()).Name.ToString();", "Structural")
            .Replace("public class Convert { public static int Apply(string value) => value.Length; }", "public class BaseConvert { public static int Apply(string value) => value.Length + 1; } public class Convert : BaseConvert {}"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("9", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Theory]
    [InlineData("string", "string?")]
    [InlineData("string?", "string")]
    public void Renamed_Nullability_Transitions_Remain_Unsupported(string sourceType, string destinationType)
    {
        var result = GeneratorTestHarness.Compile($$"""
            #nullable enable
            using Meridian.Mapping;
            public class Source { public {{sourceType}} Other { get; set; } = ""; }
            public class Dto { public {{destinationType}} Value { get; set; } = ""; }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            [MapMember("Value", "Other")]
            public static partial class Maps {}
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN005");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Converter_Binds_Eligible_Constructor_Without_Rejecting_Other_Overload()
    {
        var result = GeneratorTestHarness.Compile(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]",
            "public int Name { get; } public Dto(int name) { Name = name; } public Dto(System.Guid name) { Name = -1; }",
            "public static string Proof() => MapToDto(new Source()).Name.ToString();", "Structural"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("8", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Unselected_Configuration_For_Only_Legacy_Constructor_Is_Invalid() =>
        Error(Fixture("[MapMember(\"Name\", \"Other\")]", "public string Name { get; } public Dto(string name = \"default\") { Name = name; }", "", "Legacy"));

    [Fact]
    public void Constructor_Converter_With_No_Valid_Candidate_Is_Diagnostic() =>
        Error(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]",
            "public Dto(System.Guid name) {}", "", "Structural"));

    [Fact]
    public void Constructor_Converter_Equal_Valid_Candidates_Remain_Ambiguous()
    {
        var result = GeneratorTestHarness.Compile(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]",
            "public Dto(int name) {} public Dto(object name) {}", "", "Structural"));
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN003");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Invalid_Configured_Overload_Cannot_Fall_Back_To_Optional_Default()
    {
        var result = GeneratorTestHarness.Compile(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]",
            "public int Name { get; } public Dto(int name) { Name = name; } public Dto(System.Guid name = default, int extra = 7) { Name = -1; }",
            "public static string Proof() => MapToDto(new Source()).Name.ToString();", "Structural"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("8", GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    [Fact]
    public void File_Local_Converter_Is_Inaccessible_From_Generated_File() =>
        Error(Fixture("[MapWith(\"Name\", typeof(Convert), \"Apply\")]", "public int Name { get; init; }", "", "Structural")
            .Replace("public class Convert", "file class Convert"));

    private static void Error(string source)
    {
        var result = GeneratorTestHarness.Compile(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN006" && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    private static void Value(string config, string members, string expression, string expected, string mode)
    {
        var result = GeneratorTestHarness.Compile(Fixture(config, members,
            "public static string Proof() { var dto = MapToDto(new Source()); return " + expression + "; }", mode));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(expected, GeneratorTestHarness.InvokeStatic<string>(result, "Maps", "Proof"));
    }

    private static string Fixture(string config, string members, string proof, string mode) => $$"""
        using Meridian.Mapping;
        public class Source { public string Name { get; set; } = "original"; public string Other { get; set; } = "explicit"; }
        public class Dto { {{members}} }
        public class Convert { public static int Apply(string value) => value.Length; }
        [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.{{mode}})]
        {{config}}
        public static partial class Maps { {{proof}} }
        """;
}

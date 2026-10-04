using Meridian.Mapping.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Tests;

public class GeneratorDiagnosticTests
{
    [Fact]
    public void Legacy_Mismatch_Is_Info()
    {
        var result = GeneratorTestHarness.Compile(Fixture("", "long"));
        AssertDiagnostic(result, "MERIDIANGEN002", DiagnosticSeverity.Info);
        AssertCompiles(result);
        var strict = result.Compilation.WithOptions(result.Compilation.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
        using var stream = new MemoryStream();
        Assert.True(strict.Emit(stream).Success);
    }

    [Fact]
    public void Structural_Mismatch_Is_Error()
    {
        var result = GeneratorTestHarness.Compile(Fixture(", Mode = GeneratedMappingMode.Structural", "System.Guid"));
        AssertDiagnostic(result, "MERIDIANGEN002", DiagnosticSeverity.Error);
        AssertCompiles(result);
        Assert.DoesNotContain("public static global::Dto MapToDto", result.GeneratedSource);
    }

    [Fact]
    public void Legacy_Collection_Alias_Is_Unchanged()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public int[] Values { get; set; } = []; }
            public class Dto { public int[] Values { get; set; } = []; }
            [GenerateMapper(typeof(Source), typeof(Dto))]
            public static partial class Maps {
                public static bool Proof() { var source = new Source(); return object.ReferenceEquals(source.Values, MapToDto(source).Values); }
            }
            """);
        AssertCompiles(result);
        Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result, "Maps", "Proof"));
    }

    [Theory]
    [InlineData("MaxDepth = 0")]
    [InlineData("MaxDepth = -1")]
    [InlineData("MaxCollectionItems = 0")]
    [InlineData("MaxCollectionItems = -1")]
    public void Structural_Invalid_Limits_Are_Diagnosed(string option)
    {
        var result = GeneratorTestHarness.Compile(Fixture(", Mode = GeneratedMappingMode.Structural, " + option));
        AssertDiagnostic(result, "MERIDIANGEN006", DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Fact]
    public void Same_Simple_Destination_Names_Do_Not_Create_Ambiguous_Methods()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { }
            namespace One { public class Dto { } }
            namespace Two { public class Dto { } }
            [GenerateMapper(typeof(Source), typeof(One.Dto))]
            [GenerateMapper(typeof(Source), typeof(Two.Dto))]
            public static partial class Maps { }
            """);
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN004" && d.Severity == DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Fact]
    public void Duplicate_Attribute_Pair_Is_Diagnosed()
    {
        var result = GeneratorTestHarness.Compile(Fixture("").Replace("public static partial", "[GenerateMapper(typeof(Source), typeof(Dto), MethodName = \"Other\")] public static partial"));
        Assert.Contains(result.Diagnostics, d => d.Id == "MERIDIANGEN004");
        AssertCompiles(result);
    }

    [Fact]
    public void Explicit_MethodName_Resolves_Destination_Name_Collision()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public int Value { get; set; } }
            namespace One { public class Dto { public int Value { get; set; } } }
            namespace Two { public class Dto { public int Value { get; set; } } }
            [GenerateMapper(typeof(Source), typeof(One.Dto), MethodName = "First", Mode = GeneratedMappingMode.Structural)]
            [GenerateMapper(typeof(Source), typeof(Two.Dto), MethodName = "Second")]
            public static partial class Maps {
                public static int Proof() => First(new Source { Value = 20 }).Value + Second(new Source { Value = 22 }).Value;
            }
            """);
        AssertCompiles(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id.StartsWith("MERIDIANGEN", StringComparison.Ordinal));
        Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
    }

    [Theory]
    [InlineData("MethodName = \"\"")]
    [InlineData("MethodName = \"not valid\"")]
    [InlineData("MethodName = \"A.B\"")]
    [InlineData("MethodName = \"@Name\"")]
    [InlineData("MethodName = \"class\"")]
    [InlineData("Mode = (GeneratedMappingMode)7")]
    [InlineData("MaxDepth = 0")]
    public void Invalid_Configuration_Produces_Compile_Safe_Output(string option)
    {
        var result = GeneratorTestHarness.Compile(Fixture(", " + option));
        AssertDiagnostic(result, "MERIDIANGEN006", DiagnosticSeverity.Error);
        AssertCompiles(result);
        Assert.DoesNotContain("public static global::Dto MapToDto", result.GeneratedSource);
    }

    [Theory]
    [InlineData("public static Dto MapToDto(Source source) => new();", "")]
    [InlineData("public static int MapToDto = 1;", "")]
    [InlineData("", ", MethodName = \"Maps\"")]
    [InlineData("public static int Custom(int number) => number;", ", MethodName = \"Custom\"")]
    public void Existing_Member_And_Container_Name_Collisions_Are_Diagnosed(string member, string option)
    {
        var result = GeneratorTestHarness.Compile(Fixture(option).Replace("class Maps { }", "class Maps { " + member + " }"));
        AssertDiagnostic(result, "MERIDIANGEN004", DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Fact]
    public void Distinct_Source_Overloads_Preserve_Legacy_Method_Names()
    {
        var result = GeneratorTestHarness.Compile(Fixture("") + """
            public class OtherSource { public int Value { get; set; } }
            [Meridian.Mapping.GenerateMapper(typeof(OtherSource), typeof(Dto))]
            public static partial class Maps { }
            """);
        AssertCompiles(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MERIDIANGEN004");
        Assert.Contains("MapToDto(global::Source source)", result.GeneratedSource);
        Assert.Contains("MapToDto(global::OtherSource source)", result.GeneratedSource);
    }

    [Theory]
    [InlineData("int[,]")]
    [InlineData("System.Collections.Generic.HashSet<int>")]
    [InlineData("System.Collections.Generic.Queue<int>")]
    public void Structural_Unsupported_Collection_Shapes_Are_Diagnosed(string memberType)
    {
        var source = Fixture(", Mode = GeneratedMappingMode.Structural", memberType).Replace("public int Value", "public " + memberType + " Value");
        var result = GeneratorTestHarness.Compile(source);
        AssertDiagnostic(result, "MERIDIANGEN005", DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Theory]
    [InlineData("GenerateResolver = true")]
    public void Structural_Pending_Options_Are_Diagnosed(string option)
    {
        var result = GeneratorTestHarness.Compile(Fixture(", Mode = GeneratedMappingMode.Structural, " + option));
        AssertDiagnostic(result, "MERIDIANGEN005", DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Fact]
    public void Missing_Required_Member_With_Constructor_Is_Not_Silently_Dropped()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { }
            public class Dto {
                public required int Value { get; set; }
                [System.Diagnostics.CodeAnalysis.SetsRequiredMembers] public Dto() { }
            }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps { }
            """);
        AssertDiagnostic(result, "MERIDIANGEN002", DiagnosticSeverity.Error);
        AssertCompiles(result);
        Assert.DoesNotContain("public static global::Dto MapToDto", result.GeneratedSource);
    }

    [Fact]
    public void Missing_And_Inaccessible_Source_Properties_Are_Diagnosed()
    {
        foreach (var declaration in new[] { "", "private int Value { get; set; }" })
        {
            var result = GeneratorTestHarness.Compile(Fixture("").Replace("class Source { public int Value { get; set; } }", "class Source { " + declaration + " }"));
            AssertDiagnostic(result, "MERIDIANGEN002", DiagnosticSeverity.Info);
            AssertCompiles(result);
        }
    }

    [Fact]
    public void Invalid_Pair_Does_Not_Remove_Unrelated_Valid_Legacy_Pair()
    {
        var result = GeneratorTestHarness.Compile(Fixture(", MethodName = \"bad name\"") + """
            public class OtherDto { public int Value { get; set; } }
            [Meridian.Mapping.GenerateMapper(typeof(Source), typeof(OtherDto))]
            public static partial class Maps { public static int Proof() => MapToOtherDto(new Source { Value = 42 }).Value; }
            """);
        AssertDiagnostic(result, "MERIDIANGEN006", DiagnosticSeverity.Error);
        AssertCompiles(result);
        Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Attribute_Defaults_Are_Exact()
    {
        var result = GeneratorTestHarness.Compile(Fixture("") + """
            public static class Proof {
                public static bool Defaults() {
                    var value = new Meridian.Mapping.GenerateMapperAttribute(typeof(Source), typeof(Dto));
                    return (int)Meridian.Mapping.GeneratedMappingMode.Legacy == 0 && (int)Meridian.Mapping.GeneratedMappingMode.Structural == 1 &&
                        value.Mode == Meridian.Mapping.GeneratedMappingMode.Legacy && value.MaxDepth == 64 && value.MaxCollectionItems == 10000 &&
                        !value.PreserveReferences && !value.AllowUndefinedEnumValues && !value.AllowNullCollections && value.AllowNullDestinationValues &&
                        !value.GenerateResolver && value.MethodName is null;
                }
            }
            """);
        AssertCompiles(result);
        Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result, "Proof", "Defaults"));
    }

    [Fact]
    public void Changed_Options_Invalidate_Emission_And_Diagnostics_Use_Current_Tree()
    {
        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12);
        var tree = CSharpSyntaxTree.ParseText(Fixture("", "System.Guid"), parse, "Input.cs");
        var references = GeneratorTestHarness.Compile(Fixture("")).Compilation.References;
        var input = CSharpCompilation.Create("OptionCache", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new MeridianMapperGenerator().AsSourceGenerator()], parseOptions: parse);
        driver = driver.RunGenerators(input);
        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(driver.GetRunResult().Diagnostics).Severity);
        var replacement = CSharpSyntaxTree.ParseText("\n\n" + Fixture(", Mode = GeneratedMappingMode.Structural", "System.Guid"), parse, "Input.cs");
        input = input.ReplaceSyntaxTree(tree, replacement);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var updated, out var diagnostics);
        var error = Assert.Single(diagnostics);
        Assert.Equal("MERIDIANGEN002", error.Id);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Same(replacement, error.Location.SourceTree);
        Assert.DoesNotContain(updated.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var repaired = CSharpSyntaxTree.ParseText(Fixture(", Mode = GeneratedMappingMode.Structural, MethodName = \"Renamed\""), parse, "Input.cs");
        input = input.ReplaceSyntaxTree(replacement, repaired);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out updated, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.Contains(" Renamed(", string.Join("", driver.GetRunResult().GeneratedTrees.Select(t => t.ToString())));
        Assert.DoesNotContain(updated.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("internal", "internal", "internal", false)]
    [InlineData("internal", "private", "internal", true)]
    [InlineData("internal", "private", "private", false)]
    [InlineData("public", "internal", "protected", true)]
    [InlineData("public", "internal", "private protected", false)]
    [InlineData("public", "protected", "internal", true)]
    [InlineData("public", "protected", "protected", false)]
    public void Effective_Method_Visibility_Must_Not_Expose_Less_Visible_Types(
        string outerAccess, string sourceAccess, string mapsAccess, bool invalid)
    {
        var result = GeneratorTestHarness.Compile($$"""
            using Meridian.Mapping;
            {{outerAccess}} partial class Outer {
                {{sourceAccess}} class Source { }
                public class Dto { }
                [GenerateMapper(typeof(Source), typeof(Dto))]
                {{mapsAccess}} partial class Maps { }
            }
            """);
        if (invalid) AssertDiagnostic(result, "MERIDIANGEN006", DiagnosticSeverity.Error);
        else Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MERIDIANGEN006");
        AssertCompiles(result);
    }

    [Fact]
    public void Partial_Declaration_Changes_Update_One_Output_And_Collision_Locations()
    {
        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12);
        var first = CSharpSyntaxTree.ParseText(Fixture(""), parse, "First.cs");
        const string secondSource = """
            using Meridian.Mapping;
            public class OtherDto { public int Value { get; set; } }
            [GenerateMapper(typeof(Source), typeof(OtherDto))]
            public static partial class Maps { }
            """;
        var second = CSharpSyntaxTree.ParseText(secondSource, parse, "Second.cs");
        var references = GeneratorTestHarness.Compile(Fixture("")).Compilation.References;
        var input = CSharpCompilation.Create("PartialCache", [first, second], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new MeridianMapperGenerator().AsSourceGenerator()], parseOptions: parse,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var updated, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(updated.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(driver.GetRunResult().Results.Single().GeneratedSources, s => s.HintName == "Maps.Meridian.g.cs");
        Assert.Contains("MapToDto", generated.SourceText.ToString());
        Assert.Contains("MapToOtherDto", generated.SourceText.ToString());
        input = input.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated {}", parse, "Unrelated.cs"));
        driver = driver.RunGenerators(input);
        Assert.All(driver.GetRunResult().Results.Single().TrackedSteps["MapperOutput"].SelectMany(step => step.Outputs),
            step => Assert.Equal(IncrementalStepRunReason.Cached, step.Reason));
        var renamed = CSharpSyntaxTree.ParseText(secondSource.Replace("typeof(OtherDto))", "typeof(OtherDto), MethodName = \"Renamed\")"), parse, "Second.cs");
        input = input.ReplaceSyntaxTree(second, renamed);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out updated, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(updated.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(" Renamed(", Assert.Single(driver.GetRunResult().Results.Single().GeneratedSources, s => s.HintName == "Maps.Meridian.g.cs").SourceText.ToString());
        var collision = CSharpSyntaxTree.ParseText(secondSource.Replace("typeof(OtherDto)", "typeof(Dto)"), parse, "Second.cs");
        input = input.ReplaceSyntaxTree(renamed, collision);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out updated, out diagnostics);
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("MERIDIANGEN004", d.Id));
        Assert.Contains(diagnostics, d => ReferenceEquals(d.Location.SourceTree, first));
        Assert.Contains(diagnostics, d => ReferenceEquals(d.Location.SourceTree, collision));
        Assert.DoesNotContain(updated.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Structural_Required_Field_Is_Unsupported_And_Declared_Nested_Map_Is_Supported()
    {
        var required = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Source { public int Value { get; set; } }
            public class Dto { public required int Value; [System.Diagnostics.CodeAnalysis.SetsRequiredMembers] public Dto() {} }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps {}
            """);
        AssertDiagnostic(required, "MERIDIANGEN005", DiagnosticSeverity.Error);
        AssertCompiles(required);
        var nested = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Child { }
            public class Source { public Child Value { get; set; } = new(); }
            public class Dto { public Child Value { get; set; } = new(); }
            [GenerateMapper(typeof(Child), typeof(Child), Mode = GeneratedMappingMode.Structural)]
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps {}
            """);
        Assert.DoesNotContain(nested.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        AssertCompiles(nested);
    }

    [Fact]
    public void Structural_Ordinary_Reference_Identity_Remains_Shallow()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Child { }
            public class Source { public Child Value { get; set; } = new(); }
            public class Dto { public Child Value { get; set; } = new(); }
            [GenerateMapper(typeof(Source), typeof(Dto), Mode = GeneratedMappingMode.Structural)]
            public static partial class Maps {
                public static bool Proof() { var source = new Source(); return object.ReferenceEquals(source.Value, MapToDto(source).Value); }
            }
            """);
        AssertCompiles(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(GeneratorTestHarness.InvokeStatic<bool>(result, "Maps", "Proof"));
    }

    [Theory]
    [InlineData("MapTo\u200DDto", "public static Dto MapToDto(Source source) => new();")]
    [InlineData("A\u200C", "public static Dto A(Source source) => new();")]
    [InlineData("Ma\u200Dps", "")]
    public void Ignored_Format_Characters_In_MethodName_Are_Rejected(string name, string existing)
    {
        var result = GeneratorTestHarness.Compile(Fixture(", MethodName = \"" + name + "\"")
            .Replace("class Maps { }", "class Maps { " + existing + " }"));
        AssertDiagnostic(result, "MERIDIANGEN006", DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Fact]
    public void Format_Character_Alias_Does_Not_Invalidate_Another_Mapping()
    {
        var source = Fixture(", MethodName = \"A\u200C\"").Replace("public static partial class Maps { }", """
            [GenerateMapper(typeof(Source), typeof(OtherDto), MethodName = "A")]
            public static partial class Maps {
                public static int Proof() => A(new Source { Value = 42 }).Value;
            }
            public class OtherDto { public int Value { get; set; } }
            """);
        var result = GeneratorTestHarness.Compile(source);
        AssertDiagnostic(result, "MERIDIANGEN006", DiagnosticSeverity.Error);
        AssertCompiles(result);
        Assert.Equal(42, GeneratorTestHarness.InvokeStatic<int>(result, "Maps", "Proof"));
    }

    [Fact]
    public void Escaped_Existing_Identifier_Uses_The_Same_Method_Identity()
    {
        var result = GeneratorTestHarness.Compile(Fixture("").Replace("class Maps { }",
            "class Maps { public static Dto @MapToDto(Source source) => new(); }"));
        AssertDiagnostic(result, "MERIDIANGEN004", DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Theory]
    [InlineData("object", "dynamic")]
    [InlineData("object", "object?")]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic?>")]
    [InlineData("object[]", "dynamic[]")]
    [InlineData("object[,]", "dynamic[,]")]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>")]
    [InlineData("System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<object[]>>", "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<dynamic[]>>")]
    [InlineData("Generic<object>.Nested<int>", "Generic<dynamic>.Nested<int>")]
    public void Dynamic_And_Object_Signatures_Cannot_Distinguish_Overloads(string sourceType, string existingType)
    {
        var result = GeneratorTestHarness.Compile($$"""
            using Meridian.Mapping;
            public class Generic<T> { public class Nested<U> { } }
            public class Dto { }
            [GenerateMapper(typeof({{sourceType}}), typeof(Dto))]
            public static partial class Maps {
                public static Dto MapToDto({{existingType}} source) => new();
            }
            """);
        AssertDiagnostic(result, "MERIDIANGEN004", DiagnosticSeverity.Error);
        AssertCompiles(result);
    }

    [Theory]
    [InlineData("object[]", "dynamic[,]")]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<string>")]
    [InlineData("Generic<object>.Nested<int>", "Generic<string>.Nested<int>")]
    public void Distinct_Array_And_Generic_Signatures_Remain_Valid_Overloads(string sourceType, string existingType)
    {
        var result = GeneratorTestHarness.Compile($$"""
            using Meridian.Mapping;
            public class Generic<T> { public class Nested<U> { } }
            public class Dto { }
            [GenerateMapper(typeof({{sourceType}}), typeof(Dto))]
            public static partial class Maps {
                public static Dto MapToDto({{existingType}} source) => new();
            }
            """);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id.StartsWith("MERIDIANGEN", StringComparison.Ordinal));
        AssertCompiles(result);
        Assert.Contains(" MapToDto(", result.GeneratedSource);
    }

    [Fact]
    public void Dynamic_Object_Equivalent_Attributed_Pairs_Are_Diagnosed()
    {
        var result = GeneratorTestHarness.Compile("""
            using Meridian.Mapping;
            public class Dto { }
            [GenerateMapper(typeof(System.Collections.Generic.List<object>), typeof(Dto))]
            [GenerateMapper(typeof(System.Collections.Generic.List<dynamic>), typeof(Dto))]
            public static partial class Maps { }
            """);
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Id == "MERIDIANGEN004"));
        AssertCompiles(result);
    }

    private static string Fixture(string options, string destinationType = "int") => $$"""
        using Meridian.Mapping;
        public class Source { public int Value { get; set; } }
        public class Dto { public {{destinationType}} Value { get; set; } }
        [GenerateMapper(typeof(Source), typeof(Dto){{options}})]
        public static partial class Maps { }
        """;

    private static void AssertDiagnostic(GeneratorCompilationResult result, string id, DiagnosticSeverity severity)
    {
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == id);
        Assert.Equal(severity, diagnostic.Severity);
        Assert.NotNull(diagnostic.Location.SourceTree);
        Assert.Contains("GenerateMapper", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    private static void AssertCompiles(GeneratorCompilationResult result) =>
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
}

using Meridian.Mapping.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace Meridian.Mapping.Tests;

public sealed class GeneratorIncrementalityTests(ITestOutputHelper output)
{
    private const string Fixture = """
        using Meridian.Mapping;
        namespace Example;
        public class Source { public int Id { get; set; } }
        public class Dto { public int Id { get; init; } }
        [GenerateMapper(typeof(Source), typeof(Dto))]
        public static partial class Maps
        {
            public static int Proof() => MapToDto(new Source { Id = 42 }).Id;
        }
        """;

    [Fact]
    public void Unchanged_Input_Is_Cached()
    {
        var compilation = CreateCompilation(Fixture);
        var driver = Run(CreateDriver(), compilation);
        var before = Sources(driver);
        driver = Run(driver, compilation);
        Assert.Equal(before, Sources(driver));
        AssertCached(driver);
    }

    [Fact]
    public void Changing_Unrelated_File_Does_Not_Change_Output()
    {
        var unrelated = Parse("public class Unrelated { public int Value => 1; }", "Unrelated.cs");
        var compilation = CreateCompilation(Fixture).AddSyntaxTrees(unrelated);
        var driver = Run(CreateDriver(), compilation);
        var before = Sources(driver);
        driver = Run(driver, compilation.ReplaceSyntaxTree(unrelated,
            Parse("public class Unrelated { public string Value => \"changed\"; }", "Unrelated.cs")));
        Assert.Equal(before, Sources(driver));
        AssertCached(driver);
    }

    [Fact]
    public void Two_Containers_Have_Stable_Distinct_Hints()
    {
        var compilation = CreateCompilation(Fixture, Fixture.Replace("namespace Example;", "namespace Other;"));
        var driver = Run(CreateDriver(), compilation);
        var before = Sources(driver);
        Assert.Equal(new[] { "Example.Maps.Meridian.g.cs", "GenerateMapperAttribute.g.cs", "Other.Maps.Meridian.g.cs" }, before.Select(x => x.Hint));
        driver = Run(driver, compilation.AddSyntaxTrees(Parse("class Unrelated { }", "Extra.cs")));
        Assert.Equal(before, Sources(driver));
        AssertCached(driver);
    }

    [Fact]
    public void Changing_Mapped_Member_Invalidates_Only_Its_Container()
    {
        var compilation = CreateCompilation(Fixture, Fixture.Replace("namespace Example;", "namespace Other;"));
        var driver = Run(CreateDriver(), compilation);
        var before = Sources(driver);
        driver = Run(driver, compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(),
            Parse(Fixture.Replace("public int Id {", "public int Extra { get; set; } public int Id {"), "Input0.cs")));
        var after = Sources(driver);
        Assert.NotEqual(before.Single(x => x.Hint.StartsWith("Example.", StringComparison.Ordinal)).Text, after.Single(x => x.Hint.StartsWith("Example.", StringComparison.Ordinal)).Text);
        Assert.Equal(before.Single(x => x.Hint.StartsWith("Other.", StringComparison.Ordinal)), after.Single(x => x.Hint.StartsWith("Other.", StringComparison.Ordinal)));
        var reasons = OutputReasons(driver);
        Assert.Contains(IncrementalStepRunReason.Cached, reasons);
        Assert.Contains(IncrementalStepRunReason.Modified, reasons);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Container_Diagnostic_Preserves_Pragma_Location_And_Warnings_As_Errors(bool suppress, bool warningsAsErrors)
    {
        var source = """
            using Meridian.Mapping;
            public class Source { }
            public class Dto { }
            public class Outer
            {
            #line 200 "Mapped.cs"
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps { }
            }
            """;
        if (suppress) source = "#pragma warning disable MERIDIANGEN001\n" + source;
        var compilation = CreateCompilation(source).WithOptions(new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary, generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default));
        var driver = CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        if (suppress)
            Assert.DoesNotContain(diagnostics, d => d.Id == "MERIDIANGEN001" && !d.IsSuppressed);
        else
        {
            var diagnostic = Assert.Single(diagnostics, d => d.Id == "MERIDIANGEN001");
            Assert.Same(compilation.SyntaxTrees.Single(), diagnostic.Location.SourceTree);
            Assert.True(diagnostic.Location.IsInSource);
            Assert.Equal("Mapped.cs", diagnostic.Location.GetMappedLineSpan().Path);
            Assert.Equal(200, diagnostic.Location.GetMappedLineSpan().StartLinePosition.Line);
            Assert.Equal(warningsAsErrors ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Equal(warningsAsErrors, diagnostic.IsWarningAsError);
        }
        Assert.DoesNotContain(Sources(driver), s => s.Hint.EndsWith(".Meridian.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Changing_Pragma_Updates_Diagnostic_Without_Invalidating_Mapper_Output()
    {
        var source = Fixture.Replace("public static partial class Maps", "public class Outer { public static partial class Maps")
            .Replace("public static int Proof() => MapToDto(new Source { Id = 42 }).Id;", "") + "}";
        // Keep the attribute on the mapper, inside the non-partial outer type.
        source = source.Replace("[GenerateMapper(typeof(Source), typeof(Dto))]\npublic class Outer {", "public class Outer {\n[GenerateMapper(typeof(Source), typeof(Dto))]");
        var compilation = CreateCompilation(source);
        var driver = CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out _, out var initial);
        Assert.Single(initial, d => d.Id == "MERIDIANGEN001");
        foreach (var suppress in new[] { true, false })
        {
            var tree = Parse((suppress ? "#pragma warning disable MERIDIANGEN001\n" : "\n") + source, "Input0.cs");
            compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), tree);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
            if (suppress) Assert.DoesNotContain(diagnostics, d => d.Id == "MERIDIANGEN001" && !d.IsSuppressed);
            else Assert.Same(tree, Assert.Single(diagnostics, d => d.Id == "MERIDIANGEN001").Location.SourceTree);
            Assert.All(driver.GetRunResult().Results.Single().TrackedSteps["MapperOutput"].SelectMany(s => s.Outputs),
                item => Assert.Equal(IncrementalStepRunReason.Cached, item.Reason));
        }
    }

    [Fact]
    public void Construction_Diagnostic_Uses_Current_Tree_And_Disappears_When_Repaired()
    {
        var source = Fixture.Replace("public class Dto {", "public class Dto { private Dto() {} ")
            .Replace("public static int Proof() => MapToDto(new Source { Id = 42 }).Id;", "");
        var compilation = CreateCompilation(source);
        var driver = CreateDriver();
        foreach (var prefix in new[] { "", "\n\n" })
        {
            var tree = Parse(prefix + source, "Input0.cs");
            compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), tree);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
            var diagnostic = Assert.Single(diagnostics, d => d.Id == "MERIDIANGEN003");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Same(tree, diagnostic.Location.SourceTree);
            Assert.Contains("GenerateMapper", tree.GetText().ToString(diagnostic.Location.SourceSpan));
            Assert.DoesNotContain("MapToDto", Sources(driver).Single(s => s.Hint.EndsWith(".Meridian.g.cs", StringComparison.Ordinal)).Text);
        }
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), Parse(Fixture, "Input0.cs"));
        driver = Run(driver, compilation);
        Assert.DoesNotContain(driver.GetRunResult().Diagnostics, d => d.Id == "MERIDIANGEN003");
    }

    [Fact]
    public void Nested_Generic_Constraints_Are_Cached_And_Changes_Update_Output()
    {
        var source = """
            using Meridian.Mapping;
            namespace Example;
            public class Source { public int Id { get; set; } }
            public class Dto { public int Id { get; init; } }
            public partial class Outer<T> where T : class
            {
                [GenerateMapper(typeof(Source), typeof(Dto))]
                public static partial class Maps { }
            }
            """;
        var compilation = CreateCompilation(source);
        var driver = CreateDriver().RunGenerators(compilation);
        var before = Sources(driver);
        compilation = compilation.AddSyntaxTrees(Parse("class Unrelated {}", "Extra.cs"));
        driver = driver.RunGenerators(compilation);
        Assert.Equal(before, Sources(driver));
        AssertCached(driver);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(),
            Parse(source.Replace("where T : class", "where T : class, new()"), "Input0.cs"));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        Assert.DoesNotContain(diagnostics.Concat(updated.GetDiagnostics()), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("where T : class, new()", Sources(driver).Single(s => s.Hint.EndsWith(".Meridian.g.cs", StringComparison.Ordinal)).Text);
        Assert.Contains(IncrementalStepRunReason.Modified, OutputReasons(driver));
    }

    private GeneratorDriver Run(GeneratorDriver driver, CSharpCompilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        Assert.DoesNotContain(diagnostics.Concat(updated.GetDiagnostics()), x => x.Severity == DiagnosticSeverity.Error);
        var result = new GeneratorCompilationResult((CSharpCompilation)updated, diagnostics,
            string.Join(Environment.NewLine, Sources(driver).Select(x => x.Text)));
        Assert.Equal(42, Convert.ToInt32(GeneratorTestHarness.InvokeStatic<object>(result, "Example.Maps", "Proof"), System.Globalization.CultureInfo.InvariantCulture));
        foreach (var step in driver.GetRunResult().Results.Single().TrackedSteps)
            output.WriteLine($"{step.Key}: {string.Join(", ", step.Value.SelectMany(x => x.Outputs).Select(x => x.Reason))}");
        return driver;
    }

    private static void AssertCached(GeneratorDriver driver) => Assert.All(OutputReasons(driver), reason => Assert.Equal(IncrementalStepRunReason.Cached, reason));

    private static IncrementalStepRunReason[] OutputReasons(GeneratorDriver driver) =>
        driver.GetRunResult().Results.Single().TrackedOutputSteps["SourceOutput"]
            .SelectMany(x => x.Outputs).Select(x => x.Reason).ToArray();

    private static (string Hint, string Text)[] Sources(GeneratorDriver driver) => driver.GetRunResult().Results.Single()
        .GeneratedSources.OrderBy(x => x.HintName, StringComparer.Ordinal).Select(x => (x.HintName, x.SourceText.ToString())).ToArray();

    private static SyntaxTree Parse(string source, string path) => CSharpSyntaxTree.ParseText(source,
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12), path);

    private static CSharpCompilation CreateCompilation(params string[] sources) => CSharpCompilation.Create(
        "IncrementalityFixture", sources.Select((s, i) => Parse(s, $"Input{i}.cs")),
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static GeneratorDriver CreateDriver() => CSharpGeneratorDriver.Create(
        generators: [new MeridianMapperGenerator().AsSourceGenerator()],
        parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12),
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
}

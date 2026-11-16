using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Meridian.Mapping.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Meridian.Mapping.Tests;

internal static class GeneratorTestHarness
{
    private static readonly ImmutableArray<MetadataReference> References = GetMetadataReferences();
    private static int _assemblySequence;

    internal static GeneratorCompilationResult Compile(
        string source, LanguageVersion languageVersion = LanguageVersion.CSharp12)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(languageVersion);
        var compilation = CSharpCompilation.Create(
            $"Meridian.GeneratorFixture.{Interlocked.Increment(ref _assemblySequence)}",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new MeridianMapperGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        return new GeneratorCompilationResult(
            (CSharpCompilation)updated,
            diagnostics.AddRange(updated.GetDiagnostics()),
            string.Join(Environment.NewLine, driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources)
                .OrderBy(s => s.HintName, StringComparer.Ordinal).Select(s => s.SourceText.ToString())));
    }

    internal static T InvokeStatic<T>(GeneratorCompilationResult result, string typeName, string methodName)
    {
        using var stream = new MemoryStream();
        var emitted = result.Compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext(result.Compilation.AssemblyName, isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var type = assembly.GetType(typeName, throwOnError: true)!;
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(typeName, methodName);
            return (T)method.Invoke(null, null)!;
        }
        finally
        {
            context.Unload();
        }
    }

    private static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
            ?? throw new InvalidOperationException("Trusted platform assemblies are required by the generator test harness.");
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
    }
}

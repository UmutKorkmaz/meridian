using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Meridian.Mediator.Generators;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Meridian.Mediator.Generators.Tests;

public class RegistryGenerationTests
{
    private const string Prefix = "using System; using System.Threading; using System.Threading.Tasks; using Meridian.Mediator; using Meridian.Mediator.Generated; ";
    private const string Handler = "public sealed class Q : IRequest<int> {} public sealed class H : IRequestHandler<Q,int> { public Task<int> Handle(Q q, CancellationToken c) => Task.FromResult(42); } ";
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Append(typeof(IRequest<>).Assembly.Location).Append(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    private static (GeneratorDriverRunResult Result, Compilation Output) Generate(string body)
    {
        var input = CSharpCompilation.Create("Fixture", new[] { CSharpSyntaxTree.ParseText(Prefix + body, new CSharpParseOptions(LanguageVersion.CSharp12)) }, References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MeridianMediatorGenerator().AsSourceGenerator() }, parseOptions: new CSharpParseOptions(LanguageVersion.CSharp12));
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var output, out _);
        return (driver.GetRunResult(), output);
    }
    [Fact]
    public void Registry_Emits_Closed_Handler_Registrations()
    {
        var result = Generate(Handler + "[GenerateMediatorRegistry(typeof(H))] public partial class Registry {}");
        Assert.Contains(result.Result.GeneratedTrees, t => t.ToString().Contains("new global::H("));
        Assert.Empty(result.Output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
    [Fact]
    public void Missing_Duplicate_Explicit_Handlers_Have_Stable_Diagnostics()
    {
        var result = Generate(Handler + "[GenerateMediatorRegistry(ExpectedRequestTypes = new[] {typeof(Q)})] public partial class Registry {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED001");
    }

    private static string RegistrySource((GeneratorDriverRunResult Result, Compilation Output) result) => result.Result.GeneratedTrees.Single(t => t.FilePath.EndsWith("Registry.g.cs", StringComparison.Ordinal)).ToString();
    private static IGeneratedMediatorRegistry Load((GeneratorDriverRunResult Result, Compilation Output) result)
    {
        Assert.Empty(result.Result.Diagnostics);
        using var stream = new MemoryStream();
        var emit = result.Output.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return (IGeneratedMediatorRegistry)Activator.CreateInstance(Assembly.Load(stream.ToArray()).GetType("Registry")!)!;
    }
    [Fact]
    public void Repeated_Registry_Is_Deduplicated()
    {
        var registry = Load(Generate(Handler + "[GenerateMediatorRegistry(typeof(H), typeof(H))] public partial class Registry {}"));
        var services = new ServiceCollection();
        registry.RegisterServices(services, ServiceLifetime.Scoped);
        registry.RegisterServices(services, ServiceLifetime.Scoped);
        Assert.Single(services, d => d.ServiceType.Name.StartsWith("IRequestHandler", StringComparison.Ordinal));
        var contract = ((IGeneratedMediatorContributions)registry).Contributions[0].ServiceType;
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService(contract), first.ServiceProvider.GetRequiredService(contract));
        Assert.NotSame(first.ServiceProvider.GetRequiredService(contract), second.ServiceProvider.GetRequiredService(contract));
        Assert.Null(registry.FindRequestHandler(registry.RequestTypes.Single()));
    }
    [Fact]
    public void Missing_Explicit_Expected_Request_Has_Stable_Diagnostic()
    {
        var result = Generate(Handler + "public class Other : IRequest<int> {} [GenerateMediatorRegistry(ExpectedRequestTypes = new[] { typeof(Q), typeof(Q) })] public partial class Registry {}");
        var diagnostic = Assert.Single(result.Result.Diagnostics);
        Assert.Equal("MERIDIANMED001", diagnostic.Id);
        Assert.Equal("Registry 'Registry' declares expected request 'Q', but its handlerTypes contain no matching request handler", diagnostic.GetMessage());
        Assert.Equal("typeof(Q)", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }
    [Theory]
    [InlineData("public sealed class Q : IRequest {} public sealed class H : IRequestHandler<Q> { public Task Handle(Q q,CancellationToken c)=>Task.CompletedTask; }")]
    [InlineData("public sealed class Q : IRequest {} public sealed class H : IRequestHandler<Q,Unit> { public Task<Unit> Handle(Q q,CancellationToken c)=>Task.FromResult(Unit.Value); }")]
    [InlineData(Handler)]
    public void Matching_Expected_Request_Handler_Satisfies_Local_Obligation(string handlers)
    {
        var result = Generate(handlers + "[GenerateMediatorRegistry(typeof(H), ExpectedRequestTypes = new[] {typeof(Q)})] public partial class Registry {}");
        Assert.Empty(result.Result.Diagnostics);
        Assert.Empty(result.Output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
    [Theory]
    [InlineData("")]
    [InlineData(", ExpectedRequestTypes = new[] { typeof(Q) }")]
    public void Duplicate_Explicit_Handlers_Have_Stable_Diagnostic(string option)
    {
        var result = Generate(Handler + Handler.Replace("class Q : IRequest<int> {} public sealed ", "").Replace("class H", "class H2") + "[GenerateMediatorRegistry(typeof(H),typeof(H2)" + option + ")] public partial class Registry {}");
        Assert.Contains(result.Result.Diagnostics, d => d.Id == "MERIDIANMED002");
        Assert.DoesNotContain(result.Result.Diagnostics, d => d.Id == "MERIDIANMED001");
    }
    [Fact]
    public void Expected_Request_Types_Default_Empty()
    {
        Assert.Empty(Generate(Handler + "[GenerateMediatorRegistry] public partial class Registry {}").Result.Diagnostics);
        Assert.Empty(Generate(Handler + "[GenerateMediatorRegistry(ExpectedRequestTypes = new Type[0])] public partial class Registry {}").Result.Diagnostics);
    }
    [Fact]
    public void Expected_Requests_Are_Local_Not_Project_Or_Composition_Inference()
    {
        var result = Generate(Handler + "[GenerateMediatorRegistry(ExpectedRequestTypes = new[] {typeof(Q)})] public partial class Registry {} [GenerateMediatorRegistry(typeof(H))] public partial class B {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED001");
        Assert.Contains(result.Result.GeneratedTrees, t => t.ToString().Contains("partial class @B"));
    }
    [Fact]
    public void Expected_Request_Identity_And_Deduplication_Are_Semantic()
    {
        var source = Handler + "namespace Other { public class Q : IRequest<int> {} } [GenerateMediatorRegistry(typeof(H),typeof(H), ExpectedRequestTypes = new[] { typeof(Other.Q), typeof(Other.Q) })] public partial class Registry {}";
        Assert.Single(Generate(source).Result.Diagnostics, d => d.Id == "MERIDIANMED001");
        var normal = Generate(Handler + "[GenerateMediatorRegistry(typeof(H))] public partial class Registry {}");
        var repeated = Generate(Handler + "[GenerateMediatorRegistry(typeof(H),typeof(H), ExpectedRequestTypes=new[]{typeof(Q),typeof(Q)})] public partial class Registry {}");
        Assert.Equal(RegistrySource(normal), RegistrySource(repeated));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("new Type[]{null!}")]
    [InlineData("new[]{typeof(string)}")]
    [InlineData("new[]{typeof(System.Collections.Generic.List<>)}")]
    [InlineData("new[]{typeof(N)}")]
    [InlineData("new[]{typeof(S)}")]
    public void Invalid_Expected_Request_Is_Declared_Input_Error(string value)
    {
        var result = Generate("public class N : INotification {} public class S : Meridian.Mediator.Streaming.IStreamRequest<int> {} [GenerateMediatorRegistry(ExpectedRequestTypes=" + value + ")] public partial class Registry {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED003");
        Assert.DoesNotContain(result.Result.Diagnostics, d => d.Id == "MERIDIANMED001");
    }
    [Fact]
    public void Private_Inaccessible_Handler_Is_Diagnosed()
    {
        var result = Generate(Handler.Replace("public Task<int>", "private H() {} public Task<int>") + "[GenerateMediatorRegistry(typeof(H),ExpectedRequestTypes=new[]{typeof(Q)})] public partial class Registry {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED003");
        Assert.DoesNotContain(result.Result.Diagnostics, d => d.Id == "MERIDIANMED001");
    }
    [Fact]
    public void Ambiguous_Handler_Constructor_Is_Diagnosed()
    {
        var result = Generate(Handler.Replace("public Task<int>", "public H() {} public H(int x) {} public Task<int>") + "[GenerateMediatorRegistry(typeof(H),ExpectedRequestTypes=new[]{typeof(Q)})] public partial class Registry {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED003");
    }
    [Fact]
    public void Open_Handler_Requires_Explicit_Closed_Type()
    {
        var generic = "public class Q<T> : IRequest<T> {} public class H<T> : IRequestHandler<Q<T>,T> { public Task<T> Handle(Q<T> q,CancellationToken c)=>Task.FromResult(default(T)!); } ";
        var open = Generate(generic + "[GenerateMediatorRegistry(typeof(H<>),ExpectedRequestTypes=new[]{typeof(Q<int>)})] public partial class Registry {}");
        Assert.Single(open.Result.Diagnostics, d => d.Id == "MERIDIANMED005");
        var closed = Generate(generic + "[GenerateMediatorRegistry(typeof(H<int>),ExpectedRequestTypes=new[]{typeof(Q<int>)})] public partial class Registry {}");
        Assert.Empty(closed.Result.Diagnostics);
        Assert.Empty(closed.Output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
    [Fact]
    public void Invalid_Handler_Does_Not_Cascade_To_Missing()
    {
        var result = Generate(Handler + "[GenerateMediatorRegistry(typeof(Q),ExpectedRequestTypes=new[]{typeof(Q)})] public partial class Registry {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED003");
    }
    [Fact]
    public void Marked_Constructor_And_Optional_Default_Are_Resolved_Directly()
    {
        var source = Handler.Replace("public Task<int>", "public H() {} [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] public H(int value = 17, string? name = null) { if(value!=17 || name!=null) throw new Exception(); } public Task<int>");
        var registry = Load(Generate(source + "[GenerateMediatorRegistry(typeof(H))] public partial class Registry {}"));
        var services = new ServiceCollection(); registry.RegisterServices(services, ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService(((IGeneratedMediatorContributions)registry).Contributions[0].ServiceType));
    }
    [Fact]
    public void Duplicate_Stream_And_Multiple_Notifications_Are_Classified()
    {
        const string stream = "public class S : Meridian.Mediator.Streaming.IStreamRequest<int> {} public class SH : Meridian.Mediator.Streaming.IStreamRequestHandler<S,int> { public System.Collections.Generic.IAsyncEnumerable<int> Handle(S s,CancellationToken c)=>throw new NotImplementedException(); } ";
        var result = Generate(stream + stream[(stream.IndexOf("public class SH", StringComparison.Ordinal))..].Replace("class SH", "class SH2") + "[GenerateMediatorRegistry(typeof(SH),typeof(SH2))] public partial class Registry {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED002");
        const string notification = "public class N : INotification {} public class NH : INotificationHandler<N> { public Task Handle(N n,CancellationToken c)=>Task.CompletedTask; } ";
        var valid = Generate(notification + notification[(notification.IndexOf("public class NH", StringComparison.Ordinal))..].Replace("class NH", "class NH2") + "[GenerateMediatorRegistry(typeof(NH),typeof(NH2))] public partial class Registry {}");
        var registry = Load(valid);
        Assert.Single(registry.NotificationTypes);
        Assert.Equal(2, ((IGeneratedMediatorContributions)registry).Contributions.Count);
    }
    [Fact]
    public void Expected_Request_Change_Recomputes_Diagnostics_Deterministically()
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp12);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MeridianMediatorGenerator().AsSourceGenerator() }, parseOptions: options);
        foreach (var expected in new[] { "", "ExpectedRequestTypes=new[]{typeof(Q)}", "" })
        {
            var input = CSharpCompilation.Create("Fixture", new[] { CSharpSyntaxTree.ParseText(Prefix + Handler + "[GenerateMediatorRegistry(" + expected + ")] public partial class Registry {}", options) }, References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            driver = driver.RunGenerators(input);
            Assert.Equal(expected.Length == 0 ? 0 : 1, driver.GetRunResult().Diagnostics.Count(d => d.Id == "MERIDIANMED001"));
        }
    }
    [Fact]
    public void Pipeline_Capability_Is_Not_Silently_Ignored()
    {
        Assert.Single(Generate(Handler + "[GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(H)})] public partial class Registry {}").Result.Diagnostics, d => d.Id == "MERIDIANMED005");
        var registry = Load(Generate(Handler + "[GenerateMediatorRegistry(typeof(H),IncludeStandardPipeline=true)] public partial class Registry {}"));
        Assert.True(((IGeneratedMediatorContributions)registry).IncludesStandardPipeline);
    }
    [Fact]
    public void Reordered_Inputs_Emit_Byte_Identical_Registry()
    {
        var extra = "public class R : IRequest<string> {} public class RH : IRequestHandler<R,string> { public Task<string> Handle(R r,CancellationToken c)=>Task.FromResult(\"ok\"); } ";
        var a = Generate(Handler + extra + "[GenerateMediatorRegistry(typeof(H),typeof(RH),ExpectedRequestTypes=new[]{typeof(Q),typeof(R)})] public partial class Registry {}");
        var b = Generate(Handler + extra + "[GenerateMediatorRegistry(typeof(RH),typeof(H),typeof(H),ExpectedRequestTypes=new[]{typeof(R),typeof(Q),typeof(Q)})] public partial class Registry {}");
        Assert.Equal(RegistrySource(a), RegistrySource(b));
        Assert.DoesNotContain("MakeGenericType", RegistrySource(a));
        Assert.DoesNotContain("Activator", RegistrySource(a));
        Assert.Contains("static sp => new", RegistrySource(a));
    }
    [Theory]
    [InlineData("public partial class Registry", "public H(ref int value) {}", "MERIDIANMED003")]
    [InlineData("public partial class Registry", "public H(Span<int> value) {}", "MERIDIANMED003")]
    [InlineData("public partial class Registry", "public required string Name {get;init;}", "MERIDIANMED003")]
    [InlineData("public partial record Registry", "", "MERIDIANMED003")]
    [InlineData("public class Registry", "", "MERIDIANMED003")]
    [InlineData("public partial class Registry<T>", "", "MERIDIANMED003")]
    public void Unsupported_Declaration_And_Constructor_Shapes_Are_Diagnosed(string declaration, string member, string id)
    {
        var result = Generate(Handler.Replace("public Task<int>", member + " public Task<int>") + "[GenerateMediatorRegistry(typeof(H))] " + declaration + " {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == id);
    }
    [Fact]
    public void Alternate_Void_Forms_Compete_For_One_Slot()
    {
        var result = Generate("public class Q : IRequest {} public class H : IRequestHandler<Q> { public Task Handle(Q q,CancellationToken c)=>Task.CompletedTask; } public class H2 : IRequestHandler<Q,Unit> { public Task<Unit> Handle(Q q,CancellationToken c)=>Task.FromResult(Unit.Value); } [GenerateMediatorRegistry(typeof(H),typeof(H2),ExpectedRequestTypes=new[]{typeof(Q)})] public partial class Registry {}");
        Assert.Single(result.Result.Diagnostics, d => d.Id == "MERIDIANMED002");
    }
    [Fact]
    public void Expectations_Do_Not_Filter_Valid_Handlers_And_Collections_Are_Readonly()
    {
        var registry = Load(Generate(Handler + "[GenerateMediatorRegistry(typeof(H),ExpectedRequestTypes=new Type[0])] public partial class Registry {}"));
        Assert.Single(registry.RequestTypes);
        Assert.Throws<NotSupportedException>(() => ((IList<Type>)registry.RequestTypes).Add(typeof(string)));
        Assert.Throws<NotSupportedException>(() => ((IList<GeneratedMediatorContribution>)((IGeneratedMediatorContributions)registry).Contributions).Clear());
    }

}

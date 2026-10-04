using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Meridian.Mediator.Generators;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Meridian.Mediator.Generators.Tests;

public class RegistryGenerationTests
{
    [Theory]
    [InlineData("PreProcessorTypes", "IRequestPreProcessor<Q>", "public Task Process(Q request,CancellationToken cancellationToken)=>Task.CompletedTask;")]
    [InlineData("ExceptionActionTypes", "IRequestExceptionAction<Q,Exception>", "public Task Execute(Q request,Exception exception,CancellationToken cancellationToken)=>Task.CompletedTask;")]
    public void Pipeline_Only_Ambiguous_Ordinary_Response_Is_Diagnosed(string option, string contract, string method)
    {
        var result = Generate("public sealed class Q : IRequest<int>, IRequest<string> {} public sealed class P : Meridian.Mediator.Pipeline." + contract + " { " + method + " } [GenerateMediatorRegistry(" + option + "=new[]{typeof(P)})] public partial class Registry {}");
        Assert.Contains(result.Result.Diagnostics, d => d.Id == "MERIDIANMED005");
    }

    [Theory]
    [InlineData("PreProcessorTypes", "IRequestPreProcessor<Q>", "public Task Process(Q request,CancellationToken cancellationToken)=>Task.CompletedTask;")]
    [InlineData("PostProcessorTypes", "IRequestPostProcessor<Q,int>", "public Task Process(Q request,int response,CancellationToken cancellationToken)=>Task.CompletedTask;")]
    [InlineData("ExceptionActionTypes", "IRequestExceptionAction<Q,Exception>", "public Task Execute(Q request,Exception exception,CancellationToken cancellationToken)=>Task.CompletedTask;")]
    public void Pipeline_Only_Registry_Emits_Driver_Without_Handler_Ownership(string option, string contract, string method)
    {
        var registry = Load(Generate("public sealed class Q : IRequest<int> {} public sealed class P : Meridian.Mediator.Pipeline." + contract + " { " + method + " } [GenerateMediatorRegistry(" + option + "=new[]{typeof(P)})] public partial class Registry {}"));
        Assert.Empty(registry.RequestTypes);
        var driver = Assert.Single(((IGeneratedMediatorContributions)registry).Contributions.Where(c => c.Category == GeneratedMediatorCategory.Behavior && c.Activation == GeneratedMediatorActivation.Declared));
        var services = new ServiceCollection(); registry.RegisterServices(services, ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService(driver.ServiceType));
    }

    [Theory]
    [InlineData("public sealed class Q {}")]
    [InlineData("public sealed class Q : IRequest<string> {}")]
    public void Pipeline_Only_PostProcessor_Unsupported_Response_Is_Diagnosed(string request)
    {
        var result = Generate(request + " public sealed class P : Meridian.Mediator.Pipeline.IRequestPostProcessor<Q,int> { public Task Process(Q request,int response,CancellationToken cancellationToken)=>Task.CompletedTask; } [GenerateMediatorRegistry(PostProcessorTypes=new[]{typeof(P)})] public partial class Registry {}");
        Assert.Contains(result.Result.Diagnostics, d => d.Id == "MERIDIANMED005");
    }

    [Fact]
    public void Ambiguous_Stream_Response_Is_Diagnosed()
    {
        var result=Generate("public sealed class S : Meridian.Mediator.Streaming.IStreamRequest<int>,Meridian.Mediator.Streaming.IStreamRequest<string> {} public sealed class H : Meridian.Mediator.Streaming.IStreamRequestHandler<S,int> { public System.Collections.Generic.IAsyncEnumerable<int> Handle(S s,CancellationToken c)=>throw new Exception(); } [GenerateMediatorRegistry(typeof(H))] public partial class Registry {}");
        Assert.Contains(result.Result.Diagnostics,d=>d.Id=="MERIDIANMED005");
    }

    private const string StreamFixture = "public sealed class S : Meridian.Mediator.Streaming.IStreamRequest<int> {} public sealed class H : Meridian.Mediator.Streaming.IStreamRequestHandler<S,int> { public System.Collections.Generic.IAsyncEnumerable<int> Handle(S s,CancellationToken c) => throw new Exception(); } ";
    [Fact]
    public void Duplicate_Stream_Handler_Is_Diagnostic()
    {
        var result = Generate(StreamFixture + "public sealed class H2 : Meridian.Mediator.Streaming.IStreamRequestHandler<S,int> { public System.Collections.Generic.IAsyncEnumerable<int> Handle(S s,CancellationToken c) => throw new Exception(); } [GenerateMediatorRegistry(typeof(H),typeof(H2))] public partial class Registry {}");
        Assert.Contains(result.Result.Diagnostics, d => d.Id == "MERIDIANMED002");
    }
    [Theory]
    [InlineData("B<S,int>", "ClosedStream")]
    [InlineData("B<,>", "OpenStream")]
    public void Finite_Stream_Behavior_Emits_Closed_Factory_And_Wrapper(string selection, string phase)
    {
        var result = Generate(StreamFixture + "public sealed class B<Q,R> : Meridian.Mediator.Streaming.IStreamPipelineBehavior<Q,R> where Q : Meridian.Mediator.Streaming.IStreamRequest<R> { public System.Collections.Generic.IAsyncEnumerable<R> Handle(Q q, Meridian.Mediator.Streaming.StreamHandlerDelegate<R> next,CancellationToken c)=>next(); } [GenerateMediatorRegistry(typeof(H), StreamBehaviorTypes = new[]{typeof(" + selection + ")})] public partial class Registry {}", true);
        var registry = Load(result);
        Assert.NotNull(registry.FindStreamHandler(registry.StreamTypes.Single()));
        var contribution = Assert.Single(((IGeneratedMediatorContributions)registry).Contributions.Where(c=>c.Category == GeneratedMediatorCategory.StreamBehavior));
        Assert.Equal(phase, contribution.Phase.ToString());
        var services = new ServiceCollection(); registry.RegisterServices(services,ServiceLifetime.Scoped);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService(contribution.ServiceType));
        Assert.DoesNotContain("MakeGenericType", RegistrySource(result));
        Assert.DoesNotContain("Activator", RegistrySource(result));
    }
    [Fact]
    public void Stream_Marker_Constraint_Has_Distinct_Finite_Category()
    {
        var result = Generate(StreamFixture + "public interface Marker {} public sealed class B<Q,R> : Meridian.Mediator.Streaming.IStreamPipelineBehavior<Q,R> where Q : Meridian.Mediator.Streaming.IStreamRequest<R>, Marker { public System.Collections.Generic.IAsyncEnumerable<R> Handle(Q q,Meridian.Mediator.Streaming.StreamHandlerDelegate<R> next,CancellationToken c)=>next(); } [GenerateMediatorRegistry(typeof(H),StreamBehaviorTypes=new[]{typeof(B<,>)})] public partial class Registry {}", true);
        var registry = Load(result);
        var capability = Assert.Single(((IGeneratedMediatorBehaviorCapabilities)registry).BehaviorCapabilities);
        Assert.Equal(GeneratedMediatorCategory.StreamBehavior, capability.Category);
        Assert.False(capability.Applies);
    }

    private const string Prefix = "using System; using System.Threading; using System.Threading.Tasks; using Meridian.Mediator; using Meridian.Mediator.Generated; ";
    private const string Handler = "public sealed class Q : IRequest<int> {} public sealed class H : IRequestHandler<Q,int> { public Task<int> Handle(Q q, CancellationToken c) => Task.FromResult(42); } ";
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Append(typeof(IRequest<>).Assembly.Location).Append(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    private static (GeneratorDriverRunResult Result, Compilation Output) Generate(string body, bool warningsAsErrors = false)
    {
        var input = CSharpCompilation.Create("Fixture", new[] { CSharpSyntaxTree.ParseText(Prefix + body, new CSharpParseOptions(LanguageVersion.CSharp12)) }, References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default));
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
        Assert.NotNull(registry.FindRequestHandler(registry.RequestTypes.Single()));
    }
    [Fact]
    public void Missing_Explicit_Expected_Request_Has_Stable_Diagnostic()
    {
        var result = Generate(Handler + "public class Other : IRequest<int> {} [GenerateMediatorRegistry(ExpectedRequestTypes = new[] { typeof(Q), typeof(Q) })] public partial class Registry {}");
        var diagnostic = Assert.Single(result.Result.Diagnostics);
        Assert.Equal("MERIDIANMED001", diagnostic.Id);
        Assert.Equal("Registry 'Registry' declares expected request 'Q', but its handlerTypes contain no matching request handler", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
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
        Assert.Single(Generate(Handler + "[GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(H)})] public partial class Registry {}").Result.Diagnostics, d => d.Id == "MERIDIANMED003");
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

    [Theory]
    [InlineData("byte", "1")]
    [InlineData("sbyte", "1")]
    [InlineData("short", "1")]
    [InlineData("ushort", "1")]
    [InlineData("int", "1")]
    [InlineData("uint", "1")]
    [InlineData("long", "1")]
    [InlineData("ulong", "1")]
    [InlineData("float", "1")]
    [InlineData("double", "1")]
    [InlineData("decimal", "1")]
    [InlineData("byte?", "1")]
    [InlineData("uint?", "1")]
    [InlineData("E", "E.One")]
    [InlineData("E?", "E.One")]
    public void Optional_Numeric_Defaults_Preserve_Declared_Type_And_Registered_Override(string type, string constant)
    {
        var source = "public enum E : byte { One=1, Two=2 } " + Handler.Replace("public Task<int>", "public H("+type+" value = "+constant+") { Value = value; } public object? Value { get; } public Task<int>");
        var registry = Load(Generate(source + "[GenerateMediatorRegistry(typeof(H))] public partial class Registry {}"));
        var contribution = ((IGeneratedMediatorContributions)registry).Contributions.Single(c => c.Phase == GeneratedMediatorPhase.Handler);
        var parameterType = contribution.ImplementationType.GetConstructors().Single().GetParameters().Single().ParameterType;
        var underlyingType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        foreach (var registered in new[] { false, true })
        {
            var services = new ServiceCollection();
            if (registered)
            {
                var value = underlyingType.IsEnum ? Enum.ToObject(underlyingType, 2) : Convert.ChangeType(2, underlyingType, System.Globalization.CultureInfo.InvariantCulture);
                services.AddSingleton(parameterType, _ => value);
            }
            registry.RegisterServices(services, ServiceLifetime.Transient);
            using var provider = services.BuildServiceProvider();
            var instance = provider.GetRequiredService(contribution.ServiceType);
            var actual = contribution.ImplementationType.GetProperty("Value")!.GetValue(instance);
            Assert.Equal(underlyingType, actual!.GetType());
            Assert.Equal(registered ? 2 : 1, Convert.ToInt32(actual, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
    [Fact] public void Ordinary_And_Stream_Contracts_Have_Independent_Duplicate_Slots()
    {
        var source = Handler.Replace("Q : IRequest<int>","Q : IRequest<int>, Meridian.Mediator.Streaming.IStreamRequest<int>") + "public class SH : Meridian.Mediator.Streaming.IStreamRequestHandler<Q,int> { public System.Collections.Generic.IAsyncEnumerable<int> Handle(Q q,CancellationToken c)=>throw new NotImplementedException(); } [GenerateMediatorRegistry(typeof(H),typeof(SH))] public partial class Registry {}";
        var registry = Load(Generate(source));
        Assert.Equal(registry.RequestTypes.Single(), registry.StreamTypes.Single());
        var contributions = ((IGeneratedMediatorContributions)registry).Contributions;
        Assert.Equal(2, contributions.Count(c => c.Phase == GeneratedMediatorPhase.Handler));
        var services = new ServiceCollection(); registry.RegisterServices(services, ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        foreach (var contribution in contributions.Where(c => c.Phase == GeneratedMediatorPhase.Handler)) Assert.Equal(contribution.ImplementationType, provider.GetRequiredService(contribution.ServiceType).GetType());
    }
    [Theory]
    [InlineData("same", false, false)]
    [InlineData("generated-first", false, false)]
    [InlineData("scanned-first", false, false)]
    [InlineData("same", true, false)]
    [InlineData("generated-first", true, false)]
    [InlineData("scanned-first", true, false)]
    [InlineData("generated-first", false, true)]
    [InlineData("scanned-first", false, true)]
    public async Task Generated_And_Scanned_Notifications_Deduplicate_Known_Handlers_Only(string order, bool opaqueFactory, bool differingLifetime)
    {
        const string source = "public class N : INotification {} public class NH : INotificationHandler<N> { public static int Calls; public Task Handle(N n,CancellationToken c) { Calls++; return Task.CompletedTask; } } public class OtherNH : INotificationHandler<N> { public static int Calls; public Task Handle(N n,CancellationToken c) { Calls++; return Task.CompletedTask; } } [GenerateMediatorRegistry(typeof(NH))] public partial class Registry {}";
        var registry = Load(Generate(source));
        var assembly = registry.GetType().Assembly;
        var contribution = ((IGeneratedMediatorContributions)registry).Contributions.Single(c => c.Phase == GeneratedMediatorPhase.Handler);
        var services = new ServiceCollection();
        if (opaqueFactory) services.AddTransient(contribution.ServiceType, _ => Activator.CreateInstance(contribution.ImplementationType)!);
        void Generated(MeridianMediatorConfiguration c) { if (differingLifetime) c.Lifetime = ServiceLifetime.Singleton; typeof(MeridianMediatorConfiguration).GetMethod("AddGeneratedRegistry")!.MakeGenericMethod(registry.GetType()).Invoke(c, null); }
        if (order == "same") services.AddMeridianMediator(c => { Generated(c); c.RegisterServicesFromAssembly(assembly); });
        else if (order == "generated-first") { services.AddMeridianMediator(Generated); services.AddMeridianMediator(c => c.RegisterServicesFromAssembly(assembly)); }
        else { services.AddMeridianMediator(c => c.RegisterServicesFromAssembly(assembly)); services.AddMeridianMediator(Generated); }
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMediator>().Publish((INotification)Activator.CreateInstance(registry.NotificationTypes.Single())!);
        Assert.Equal(opaqueFactory ? 2 : 1, (int)contribution.ImplementationType.GetField("Calls")!.GetValue(null)!);
        Assert.Equal(1, (int)assembly.GetType("OtherNH")!.GetField("Calls")!.GetValue(null)!);
        Assert.Equal(opaqueFactory ? 3 : 2, services.Count(d => d.ServiceType == contribution.ServiceType));
        if (differingLifetime)
        {
            var handler = provider.GetServices(contribution.ServiceType).Single(h => h!.GetType() == contribution.ImplementationType);
            var again = provider.GetServices(contribution.ServiceType).Single(h => h!.GetType() == contribution.ImplementationType);
            if (order == "generated-first") Assert.Same(handler, again); else Assert.NotSame(handler, again);
        }
    }

    [Theory]
    [InlineData("none", "scan", 1)]
    [InlineData("remove", "scan", 1)]
    [InlineData("replace", "scan", 2)]
    [InlineData("remove", "generated", 1)]
    [InlineData("replace", "generated", 2)]
    public async Task Generated_Markers_Require_Their_Live_Descriptor_After_Removal_Or_Replacement(string mutation, string registration, int expectedCalls)
    {
        const string source = "public class N : INotification {} public class NH : INotificationHandler<N>, IDisposable { public static int Calls; public static int Disposals; public Task Handle(N n,CancellationToken c) { Calls++; return Task.CompletedTask; } public void Dispose() { Disposals++; } } [GenerateMediatorRegistry(typeof(NH))] public partial class Registry {}";
        var registry = Load(Generate(source));
        var contribution = ((IGeneratedMediatorContributions)registry).Contributions.Single(c => c.Phase == GeneratedMediatorPhase.Handler);
        var services = new ServiceCollection();
        registry.RegisterServices(services, ServiceLifetime.Transient);
        if (mutation != "none") services.RemoveAll(contribution.ServiceType);
        ServiceDescriptor? replacement = null;
        if (mutation == "replace")
        {
            replacement = ServiceDescriptor.Transient(contribution.ServiceType, _ => Activator.CreateInstance(contribution.ImplementationType)!);
            services.Add(replacement);
        }
        if (registration == "scan") services.AddMeridianMediator(c => c.RegisterServicesFromAssembly(registry.GetType().Assembly));
        else
        {
            registry.RegisterServices(services, ServiceLifetime.Transient);
            registry.RegisterServices(services, ServiceLifetime.Transient);
            services.AddMeridianMediator(_ => { });
        }
        if (replacement != null) Assert.Contains(replacement, services);
        Assert.Equal(expectedCalls, services.Count(d => d.ServiceType == contribution.ServiceType));
        using (var provider = services.BuildServiceProvider())
            await provider.GetRequiredService<IMediator>().Publish((INotification)Activator.CreateInstance(registry.NotificationTypes.Single())!);
        Assert.Equal(expectedCalls, (int)contribution.ImplementationType.GetField("Calls")!.GetValue(null)!);
        Assert.Equal(expectedCalls, (int)contribution.ImplementationType.GetField("Disposals")!.GetValue(null)!);
    }

    [Fact]
    public async Task Closed_Behavior_And_Processors_Execute_In_Declared_Phases()
    {
        const string extras = "public class B<T,U> : Meridian.Mediator.Pipeline.IPipelineBehavior<T,U> where T:notnull { public async Task<U> Handle(T q,RequestHandlerDelegate<U> next,CancellationToken c) { var r=await next(); return (U)(object)((int)(object)r!+1); } } public class P : Meridian.Mediator.Pipeline.IRequestPreProcessor<Q> { public Task Process(Q q,CancellationToken c) => Task.CompletedTask; } ";
        var registry = Load(Generate(Handler + extras + "[GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(B<,>)},PreProcessorTypes=new[]{typeof(P)})] public partial class Registry {}"));
        var services = new ServiceCollection(); registry.RegisterServices(services,ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        var request = Activator.CreateInstance(registry.RequestTypes.Single())!;
        Assert.Equal(43, await registry.FindRequestHandler(request.GetType())!.Handle(request,provider,default));
    }

    [Theory]
    [InlineData("ExceptionHandlerTypes", "public class E : Meridian.Mediator.Pipeline.IRequestExceptionHandler<Q,int,Exception> { public Task Handle(Q q,Exception e,Meridian.Mediator.Pipeline.RequestExceptionHandlerState<int> state,CancellationToken c) { state.SetHandled(17); return Task.CompletedTask; } }", 17)]
    [InlineData("ExceptionActionTypes", "public class E : Meridian.Mediator.Pipeline.IRequestExceptionAction<Q,Exception> { public Task Execute(Q q,Exception e,CancellationToken c) => Task.FromException(new Exception(\"ignored\")); }", -1)]
    public async Task Declared_Typed_Exception_Inputs_Are_Operational(string option, string extra, int expected)
    {
        var registry = Load(Generate(Handler.Replace("Task.FromResult(42)", "Task.FromException<int>(new InvalidOperationException(\"original\"))") + extra + "[GenerateMediatorRegistry(typeof(H),"+option+"=new[]{typeof(E)})] public partial class Registry {}"));
        var services = new ServiceCollection(); registry.RegisterServices(services,ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        var request = Activator.CreateInstance(registry.RequestTypes.Single())!;
        if(expected == 17) Assert.Equal(17, await registry.FindRequestHandler(request.GetType())!.Handle(request,provider,default));
        else Assert.Equal("original", (await Assert.ThrowsAsync<InvalidOperationException>(() => registry.FindRequestHandler(request.GetType())!.Handle(request,provider,default))).Message);
    }

    [Theory]
    [InlineData("where T:class", "public sealed class Q : IRequest<int>", true)]
    [InlineData("where T:struct", "public sealed class Q : IRequest<int>", false)]
    [InlineData("where T:new()", "public sealed class Q : IRequest<int>", true)]
    [InlineData("where T:IMarker", "public sealed class Q : IRequest<int>", false)]
    [InlineData("where U:unmanaged", "public sealed class Q : IRequest<int>", true)]
    [InlineData("where T:IMarker", "public sealed class Q : IRequest<int>, IMarker", true)]
    public async Task Finite_Constraints_Are_Checked_By_The_Consumer_Compiler(string constraints,string request,bool applies)
    {
        var behavior="public interface IMarker {} public class B<T,U> : Meridian.Mediator.Pipeline.IPipelineBehavior<T,U> "+constraints+" { public async Task<U> Handle(T q,RequestHandlerDelegate<U> next,CancellationToken c) { var r=await next(); return (U)(object)((int)(object)r!+1); } } ";
        var registry=Load(Generate(Handler.Replace("public sealed class Q : IRequest<int>",request)+behavior+"[GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(B<,>)})] public partial class Registry {}"));
        var services=new ServiceCollection();registry.RegisterServices(services,ServiceLifetime.Transient);
        using var provider=services.BuildServiceProvider();
        var query=Activator.CreateInstance(registry.RequestTypes.Single())!;
        Assert.Equal(applies ? 43 : 42,await registry.FindRequestHandler(query.GetType())!.Handle(query,provider,default));
        var capability=Assert.Single(((IGeneratedMediatorBehaviorCapabilities)registry).BehaviorCapabilities,c=>c.DefinitionType.Name.StartsWith("B`",StringComparison.Ordinal));
        Assert.Equal(applies,capability.Applies); Assert.Equal(typeof(int),capability.ResponseType); Assert.Equal(GeneratedMediatorCategory.Behavior,capability.Category);
    }
    [Fact]
    public void Unsupported_Nested_Substitution_Is_Diagnosed_And_Explicit_Audit_Is_Supported()
    {
        var bad=Generate(Handler+"public class B<T,U> : Meridian.Mediator.Pipeline.IPipelineBehavior<System.Collections.Generic.List<T>,U> { public Task<U> Handle(System.Collections.Generic.List<T> q,RequestHandlerDelegate<U> next,CancellationToken c)=>next(); } [GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(B<,>)})] public partial class Registry {}");
        Assert.Contains(bad.Result.Diagnostics,d=>d.Id=="MERIDIANMED005");
        var good=Generate(Handler+"[GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(Meridian.Mediator.Behaviors.AuditBehavior<,>)})] public partial class Registry {}");
        Assert.Empty(good.Result.Diagnostics);Assert.Empty(good.Output.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error));
        Assert.DoesNotContain("MakeGenericType",RegistrySource(good));Assert.DoesNotContain("Activator",RegistrySource(good));
    }

    [Fact]
    public async Task Constraint_Probe_Uses_Registry_Accessibility_And_Avoids_Member_Collisions()
    {
        var source="[GenerateMediatorRegistry(typeof(Registry.H),BehaviorTypes=new[]{typeof(Registry.B<,>)})] public partial class Registry { private int __MeridianConstraintProbe; private class Q:IRequest<int>{} private class H:IRequestHandler<Q,int> { public Task<int> Handle(Q q,CancellationToken c)=>Task.FromResult(9); } private class B<T,U>:Meridian.Mediator.Pipeline.IPipelineBehavior<T,U> where T:class {public Task<U> Handle(T q,RequestHandlerDelegate<U> next,CancellationToken c)=>next();} }";
        var registry=Load(Generate(source));
        var services=new ServiceCollection();registry.RegisterServices(services,ServiceLifetime.Transient);
        using var provider=services.BuildServiceProvider();
        var request=Activator.CreateInstance(registry.RequestTypes.Single(),true)!;
        Assert.Equal(9,await registry.FindRequestHandler(request.GetType())!.Handle(request,provider,default));
        Assert.True(Assert.Single(((IGeneratedMediatorBehaviorCapabilities)registry).BehaviorCapabilities,c=>c.DefinitionType.Name.StartsWith("B`",StringComparison.Ordinal)).Applies);
    }
    [Fact]
    public void Dependent_Constraint_Mismatch_Is_Explicitly_Inapplicable()
    {
        var source=Handler+"public class B<T,U>:Meridian.Mediator.Pipeline.IPipelineBehavior<T,U> where T:U {public Task<U> Handle(T q,RequestHandlerDelegate<U> next,CancellationToken c)=>next();} [GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(B<,>)})] public partial class Registry {}";
        var registry=Load(Generate(source));
        Assert.False(Assert.Single(((IGeneratedMediatorBehaviorCapabilities)registry).BehaviorCapabilities,c=>c.DefinitionType.Name.StartsWith("B`",StringComparison.Ordinal)).Applies);
    }

    [Fact]
    public void Nullable_Response_Does_Not_Satisfy_Notnull_Behavior_Constraint()
    {
        var source="#nullable enable\npublic class Q:IRequest<string?> {} public class H:IRequestHandler<Q,string?> {public Task<string?> Handle(Q q,CancellationToken c)=>Task.FromResult<string?>(null);} public class B<T,U>:Meridian.Mediator.Pipeline.IPipelineBehavior<T,U> where T:notnull where U:notnull {public Task<U> Handle(T q,RequestHandlerDelegate<U> next,CancellationToken c)=>next();} [GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(B<,>)})] public partial class Registry {}";
        var registry=Load(Generate("\n"+source));
        Assert.False(Assert.Single(((IGeneratedMediatorBehaviorCapabilities)registry).BehaviorCapabilities,c=>c.DefinitionType.Name.StartsWith("B`",StringComparison.Ordinal)).Applies);
    }

    [Fact]
    public void Constraint_Probe_Has_No_Incidental_Warnings_In_WarningsAsErrors_Consumer()
    {
        var result=Generate("\n#nullable enable\n"+Handler+"public class B<T,U>:Meridian.Mediator.Pipeline.IPipelineBehavior<T,U> where T:notnull {public Task<U> Handle(T q,RequestHandlerDelegate<U> next,CancellationToken c)=>next();} [GenerateMediatorRegistry(typeof(H),BehaviorTypes=new[]{typeof(B<,>)})] public partial class Registry {}",true);
        Assert.Empty(result.Result.Diagnostics);Assert.Empty(result.Output.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error));
    }

}

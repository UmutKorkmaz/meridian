using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Tests;

public class GeneratedRequestDispatchTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Generated_Typed_Request_Matches_Runtime(bool generated)
    {
        using var token = new CancellationTokenSource();
        var request = new GeneratedQuery();
        using var services = Provider(generated);
        Assert.Equal(42, await services.GetRequiredService<IMediator>().Send(request, token.Token));
        Assert.Same(request, services.GetRequiredService<DispatchCapture>().Request);
        Assert.Equal(token.Token, services.GetRequiredService<DispatchCapture>().Token);
    }

    [Fact]
    public async Task Object_Send_Uses_Generated_Wrapper()
    {
        using var provider = Provider();
        using var token = new CancellationTokenSource();
        var request = new GeneratedQuery();
        Assert.Equal(42, await provider.GetRequiredService<IMediator>().Send((object)request, token.Token));
        Assert.Same(request, provider.GetRequiredService<DispatchCapture>().Request);
        Assert.Equal(token.Token, provider.GetRequiredService<DispatchCapture>().Token);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Generated_Both_Void_Handler_Forms_Match_Runtime(bool generated)
    {
        using var provider = Provider(generated);
        using var token = new CancellationTokenSource();
        var mediator = provider.GetRequiredService<IMediator>();
        await mediator.Send(new GeneratedVoid(), token.Token);
        Assert.Equal(token.Token, provider.GetRequiredService<DispatchCapture>().Token);
        Assert.Equal(Unit.Value, await mediator.Send((object)new GeneratedUnit(), token.Token));
        await mediator.Send(new GeneratedUnit(),token.Token);
        Assert.Equal(Unit.Value,await mediator.Send((object)new GeneratedVoid(),token.Token));
        Assert.Equal(4, provider.GetRequiredService<DispatchCapture>().Calls);
    }

    [Fact]
    public async Task Strict_Unknown_Request_Is_Actionable_Error()
    {
        var legacy = new ServiceCollection().AddSingleton<DispatchCapture>().AddTransient<IRequestHandler<GeneratedQuery,int>,GeneratedQueryHandler>();
        legacy.AddMeridianMediator(_ => {});
        using var warm = legacy.BuildServiceProvider();
        Assert.Equal(42, await warm.GetRequiredService<IMediator>().Send(new GeneratedQuery()));
        var strict = new ServiceCollection();
        strict.AddMeridianMediator(c => c.RequireGeneratedDispatch = true);
        using var provider = strict.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IMediator>().Send(new GeneratedQuery()));
        Assert.Contains(nameof(GeneratedQuery), error.Message);
        Assert.Contains("generated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Same_Request_Type_Two_Providers_Uses_Own_Scoped_Handler()
    {
        using var first = Provider(); using var second = Provider();
        using var a = first.CreateScope(); using var b = second.CreateScope();
        await Task.WhenAll(a.ServiceProvider.GetRequiredService<IMediator>().Send(new GeneratedQuery()), b.ServiceProvider.GetRequiredService<IMediator>().Send(new GeneratedQuery()));
        Assert.NotSame(a.ServiceProvider.GetRequiredService<IRequestHandler<GeneratedQuery,int>>(), b.ServiceProvider.GetRequiredService<IRequestHandler<GeneratedQuery,int>>());
        Assert.Same(a.ServiceProvider.GetRequiredService<IRequestHandler<GeneratedQuery,int>>(), a.ServiceProvider.GetRequiredService<IRequestHandler<GeneratedQuery,int>>());
        var owned=(GeneratedQueryHandler)a.ServiceProvider.GetRequiredService<IRequestHandler<GeneratedQuery,int>>();
        a.Dispose();Assert.True(owned.Disposed);
        using var c=first.CreateScope();
        Assert.NotSame(owned,c.ServiceProvider.GetRequiredService<IRequestHandler<GeneratedQuery,int>>());
    }

    private static ServiceProvider Provider(bool generated=true)
    {
        var services = new ServiceCollection().AddSingleton<DispatchCapture>();
        if(generated) services.AddMeridianMediator(c => { c.AddGeneratedRegistry<DispatchRegistry>(); c.RequireGeneratedDispatch = true; c.Lifetime = ServiceLifetime.Scoped; });
        else {services.AddScoped<IRequestHandler<GeneratedQuery,int>,GeneratedQueryHandler>();services.AddScoped<IRequestHandler<GeneratedVoid>,GeneratedVoidHandler>();services.AddScoped<IRequestHandler<GeneratedUnit,Unit>,GeneratedUnitHandler>();services.AddMeridianMediator(_=>{});}
        return services.BuildServiceProvider();
    }
}
public sealed class DispatchCapture { public object? Request; public CancellationToken Token; public int Calls; }
public sealed class GeneratedQuery : IRequest<int>;
public sealed class GeneratedVoid : IRequest;
public sealed class GeneratedUnit : IRequest;
public sealed class GeneratedQueryHandler(DispatchCapture capture) : IRequestHandler<GeneratedQuery,int>,IDisposable
{
    public bool Disposed {get;private set;}
    public void Dispose()=>Disposed=true;
    public Task<int> Handle(GeneratedQuery request, CancellationToken cancellationToken) { capture.Request = request; capture.Token = cancellationToken; capture.Calls++; return Task.FromResult(42); }
}
public sealed class GeneratedVoidHandler(DispatchCapture capture) : IRequestHandler<GeneratedVoid>
{
    public Task Handle(GeneratedVoid request, CancellationToken cancellationToken) { capture.Request = request; capture.Token = cancellationToken; capture.Calls++; return Task.CompletedTask; }
}
public sealed class GeneratedUnitHandler(DispatchCapture capture) : IRequestHandler<GeneratedUnit,Unit>
{
    public Task<Unit> Handle(GeneratedUnit request, CancellationToken cancellationToken) { capture.Request = request; capture.Token = cancellationToken; capture.Calls++; return Task.FromResult(Unit.Value); }
}
[GenerateMediatorRegistry(typeof(GeneratedQueryHandler), typeof(GeneratedVoidHandler), typeof(GeneratedUnitHandler))]
public partial class DispatchRegistry;

public class GeneratedPipelineDispatchTests
{
    [Fact]
    public async Task Generated_Behavior_Order_Matches_Configuration()
    {
        var services = new ServiceCollection().AddSingleton<PipelineTrace>();
        services.AddMeridianMediator(c => { c.AddGeneratedRegistry<PipelineRegistry>(); c.RequireGeneratedDispatch=true; c.AddOpenBehavior(typeof(TraceBehavior<,>),-10); c.AddBehavior<ClosedTraceBehavior>(-20); c.AddOpenBehavior(typeof(TraceBehavior<,>),10); });
        using var provider=services.BuildServiceProvider();
        Assert.Equal(7,await provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        Assert.Equal(new[]{"closed","open","open","pre","handler","post"},provider.GetRequiredService<PipelineTrace>().Events);
    }
    [Fact]
    public async Task Closed_Marker_Behavior_Applies_Only_When_Constraint_Matches()
    {
        var services = new ServiceCollection().AddSingleton<PipelineTrace>();
        services.AddMeridianMediator(c => { c.AddGeneratedRegistry<MarkerRegistry>(); c.RequireGeneratedDispatch=true; c.AddOpenBehavior(typeof(MarkerTraceBehavior<,>)); });
        using var provider=services.BuildServiceProvider();
        Assert.Equal(7,await provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        Assert.Equal(8,await provider.GetRequiredService<IMediator>().Send(new PlainQuery()));
        Assert.Equal(new[]{"marker","handler","plain"},provider.GetRequiredService<PipelineTrace>().Events);
    }
    [Fact]
    public void Strict_Empty_Registry_Rejects_Unrooted_Pipeline_Before_Registration()
    {
        var services=new ServiceCollection();
        Assert.Throws<InvalidOperationException>(()=>services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddOpenBehavior(typeof(TraceBehavior<,>));}));
    }
    [Fact]
    public void Strict_Unrooted_Behavior_Fails_Before_Resolution()
    {
        var services=new ServiceCollection();
        var error=Assert.Throws<InvalidOperationException>(()=>services.AddMeridianMediator(c=>{ c.AddGeneratedRegistry<DispatchRegistry>();c.RequireGeneratedDispatch=true;c.AddOpenBehavior(typeof(TraceBehavior<,>)); }));
        Assert.Contains("TraceBehavior",error.Message);
    }
    [Theory]
    [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task Generated_Standard_Validator_Executes_Once(bool attribute,bool extension)
    {
        var services=new ServiceCollection().AddSingleton<PipelineTrace>().AddSingleton<IAuditSink,TraceAuditSink>().AddSingleton<IStringLocalizer<PipelineQuery>,TraceLocalizer>();
        if(extension) { if(attribute) services.AddMeridianStandard<StandardRegistry>(c=>c.RequireGeneratedDispatch=true); else services.AddMeridianStandard<DormantStandardRegistry>(c=>c.RequireGeneratedDispatch=true); }
        else services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<StandardRegistry>();c.RequireGeneratedDispatch=true;});
        if(attribute) services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<StandardRegistry>();c.RequireGeneratedDispatch=true;});
        using var provider=services.BuildServiceProvider();
        Assert.Equal(7,await provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        var trace=provider.GetRequiredService<PipelineTrace>();
        Assert.Equal(new[]{"validate","handler","audit"},trace.Events);
        Assert.False(string.IsNullOrEmpty(Assert.Single(trace.Audit).CorrelationId));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Generated_Standard_Audit_Preserves_Optional_Telemetry_And_Custom_Sink(bool details)
    {
        var trace=new PipelineTrace { Reject=true };
        var sink=new TraceAuditSink(trace);
        var services=new ServiceCollection().AddSingleton(trace).AddSingleton<IAuditSink>(sink).AddSingleton<IStringLocalizer<PipelineQuery>,TraceLocalizer>();
        if(details) services.AddSingleton(new MediatorTelemetryOptions {RecordExceptionMessage=true});
        services.AddMeridianStandard<StandardRegistry>(c=>c.RequireGeneratedDispatch=true);
        using var provider=services.BuildServiceProvider();
        await Assert.ThrowsAsync<ValidationException>(()=>provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        Assert.Same(sink,provider.GetRequiredService<IAuditSink>());
        var entry=Assert.Single(trace.Audit);
        Assert.False(entry.Success);
        Assert.False(string.IsNullOrEmpty(entry.CorrelationId));
        if(details) Assert.NotNull(entry.FailureMessage); else Assert.Equal("An error occurred during request processing.",entry.FailureMessage);
        Assert.Equal(new[]{"validate","audit"},trace.Events);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Standard_Missing_Dependencies_Fail_Resolution(bool localizer)
    {
        var services=new ServiceCollection().AddSingleton<PipelineTrace>();
        if(localizer) services.AddSingleton<IStringLocalizer<PipelineQuery>,TraceLocalizer>();
        else services.AddSingleton<IAuditSink,TraceAuditSink>();
        services.AddMeridianStandard<StandardRegistry>(c=>c.RequireGeneratedDispatch=true);
        using var provider=services.BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
    }
    [Fact]
    public async Task Repeated_Registry_Does_Not_Multiply_Behaviors_And_Removed_Factories_Are_Restored()
    {
        var services=new ServiceCollection().AddSingleton<PipelineTrace>();
        void Configure(MeridianMediatorConfiguration c) {c.AddGeneratedRegistry<PipelineRegistry>();c.RequireGeneratedDispatch=true;}
        services.AddMeridianMediator(Configure);
        var service=typeof(IPipelineBehavior<PipelineQuery,int>);
        foreach(var descriptor in services.Where(d=>d.ServiceType==service).ToArray()) services.Remove(descriptor);
        services.AddTransient<IPipelineBehavior<PipelineQuery,int>>(sp=>new ClosedTraceBehavior(sp.GetRequiredService<PipelineTrace>()));
        services.AddMeridianMediator(Configure);
        services.AddMeridianMediator(Configure);
        using var provider=services.BuildServiceProvider();
        Assert.Equal(7,await provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        Assert.Equal(new[]{"closed","open","closed","pre","handler","post"},provider.GetRequiredService<PipelineTrace>().Events);
    }
    [Fact]
    public void Strict_External_Open_Behavior_Cannot_Bypass_Generated_Closure()
    {
        var services=new ServiceCollection();
        services.AddTransient(typeof(IPipelineBehavior<,>),typeof(TraceBehavior<,>));
        Assert.Throws<InvalidOperationException>(()=>services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<DispatchRegistry>();}));
    }
    [Fact]
    public async Task NonStrict_Unknown_Behavior_Retains_Order_And_Repeated_Registration_Identity()
    {
        var services=new ServiceCollection().AddSingleton<PipelineTrace>();
        void Configure(MeridianMediatorConfiguration c) {c.AddGeneratedRegistry<PipelineRegistry>();c.AddBehavior<ExtraTraceBehavior>(-30);}
        services.AddMeridianMediator(Configure);services.AddMeridianMediator(Configure);
        using var provider=services.BuildServiceProvider();
        await provider.GetRequiredService<IMediator>().Send(new PipelineQuery());
        Assert.Equal(new[]{"extra","open","closed","pre","handler","post"},provider.GetRequiredService<PipelineTrace>().Events);
    }
    [Fact]
    public async Task NonStrict_Configured_Open_Behavior_Applies_To_Unknown_Legacy_Request()
    {
        var services=new ServiceCollection().AddSingleton<PipelineTrace>().AddTransient<IRequestHandler<PlainQuery,int>,PlainHandler>();
        services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<PipelineRegistry>();c.AddOpenBehavior(typeof(TraceBehavior<,>));});
        using var provider=services.BuildServiceProvider();
        Assert.Equal(8,await provider.GetRequiredService<IMediator>().Send(new PlainQuery()));
        Assert.Equal(new[]{"open","plain"},provider.GetRequiredService<PipelineTrace>().Events);
    }
    [Fact]
    public async Task NonStrict_Undeclared_Processor_Retains_Legacy_Fallback()
    {
        var services=new ServiceCollection().AddSingleton<PipelineTrace>();
        services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<DormantStandardRegistry>();c.AddRequestPreProcessor(typeof(TracePre));});
        using var provider=services.BuildServiceProvider();
        await provider.GetRequiredService<IMediator>().Send(new PipelineQuery());
        Assert.Equal(new[]{"pre","handler"},provider.GetRequiredService<PipelineTrace>().Events);
    }
    [Fact]
    public async Task Provider_Built_Before_Collection_Edit_Keeps_Its_Registry_Snapshot()
    {
        var services=new ServiceCollection().AddSingleton<PipelineTrace>().AddSingleton<DispatchCapture>();
        services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<DispatchRegistry>();c.RequireGeneratedDispatch=true;});
        using var before=services.BuildServiceProvider();
        services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<DormantStandardRegistry>();c.RequireGeneratedDispatch=true;});
        using var after=services.BuildServiceProvider();
        Assert.Equal(7,await after.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>before.GetRequiredService<IMediator>().Send(new PipelineQuery()));
    }
}
public interface IMarked;
public sealed class PipelineQuery : IRequest<int>,IMarked;
public sealed class PlainQuery : IRequest<int>;
public sealed class PipelineTrace { public bool Reject {get;set;} public List<string> Events {get;}=new(); public List<AuditEntry> Audit {get;}=new(); }
public sealed class PipelineHandler(PipelineTrace trace) : IRequestHandler<PipelineQuery,int> { public Task<int> Handle(PipelineQuery q,CancellationToken cancellationToken) {trace.Events.Add("handler");return Task.FromResult(7);} }
public sealed class PlainHandler(PipelineTrace trace) : IRequestHandler<PlainQuery,int> { public Task<int> Handle(PlainQuery q,CancellationToken cancellationToken) {trace.Events.Add("plain");return Task.FromResult(8);} }
public sealed class TraceBehavior<Q,R>(PipelineTrace trace) : IPipelineBehavior<Q,R> where Q:notnull { public Task<R> Handle(Q q,RequestHandlerDelegate<R> next,CancellationToken cancellationToken) {trace.Events.Add("open");return next();} }
public sealed class ClosedTraceBehavior(PipelineTrace trace) : IPipelineBehavior<PipelineQuery,int> { public Task<int> Handle(PipelineQuery q,RequestHandlerDelegate<int> next,CancellationToken cancellationToken) {trace.Events.Add("closed");return next();} }
public sealed class MarkerTraceBehavior<Q,R>(PipelineTrace trace) : IPipelineBehavior<Q,R> where Q:notnull,IMarked { public Task<R> Handle(Q q,RequestHandlerDelegate<R> next,CancellationToken cancellationToken) {trace.Events.Add("marker");return next();} }
public sealed class TracePre(PipelineTrace trace) : IRequestPreProcessor<PipelineQuery> {public Task Process(PipelineQuery q,CancellationToken cancellationToken) {trace.Events.Add("pre");return Task.CompletedTask;} }
public sealed class TracePost(PipelineTrace trace) : IRequestPostProcessor<PipelineQuery,int> {public Task Process(PipelineQuery q,int r,CancellationToken cancellationToken) {trace.Events.Add("post");return Task.CompletedTask;} }
public sealed class TraceValidator(PipelineTrace trace) : IValidator<PipelineQuery> {public Task<ValidationResult> ValidateAsync(PipelineQuery q,CancellationToken cancellationToken) {trace.Events.Add("validate");return Task.FromResult(new ValidationResult {Errors=trace.Reject ? new(){new("Name","invalid")} : new()});} }
public sealed class TraceAuditSink(PipelineTrace trace) : IAuditSink {public Task RecordAsync(AuditEntry entry,CancellationToken cancellationToken) {trace.Events.Add("audit");trace.Audit.Add(entry);return Task.CompletedTask;} }
public sealed class TraceLocalizer : IStringLocalizer<PipelineQuery> { public LocalizedString this[string name]=>new(name,name,true); public LocalizedString this[string name,params object[] arguments]=>new(name,name,true); public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)=>Array.Empty<LocalizedString>(); }
[GenerateMediatorRegistry(typeof(PipelineHandler),BehaviorTypes=new[]{typeof(TraceBehavior<,>),typeof(ClosedTraceBehavior)},PreProcessorTypes=new[]{typeof(TracePre)},PostProcessorTypes=new[]{typeof(TracePost)})] public partial class PipelineRegistry;
[GenerateMediatorRegistry(typeof(PipelineHandler),typeof(PlainHandler),BehaviorTypes=new[]{typeof(MarkerTraceBehavior<,>)})] public partial class MarkerRegistry;
[GenerateMediatorRegistry(typeof(PipelineHandler),ValidatorTypes=new[]{typeof(TraceValidator)},IncludeStandardPipeline=true)] public partial class StandardRegistry;
[GenerateMediatorRegistry(typeof(PipelineHandler),ValidatorTypes=new[]{typeof(TraceValidator)})] public partial class DormantStandardRegistry;

public class GeneratedExceptionDispatchTests
{
    [Theory]
    [InlineData("handled")]
    [InlineData("unhandled")]
    [InlineData("sync-handler")]
    [InlineData("async-handler")]
    [InlineData("sync-action")]
    [InlineData("async-action")]
    public async Task Generated_Exception_Semantics_Match_Runtime(string mode)
    {
        foreach(var generated in new[]{false,true})
        {
            var trace = new ExceptionTrace {Mode=mode};
            var services = new ServiceCollection().AddSingleton(trace);
            if(generated) services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<ExceptionRegistry>();c.RequireGeneratedDispatch=true;});
            else
            {
                services.AddTransient<IRequestHandler<ExceptionQuery,int>,ExceptionQueryHandler>();
                services.AddTransient<IRequestExceptionHandler<ExceptionQuery,int,InvalidOperationException>,DeclineExceptionHandler>();
                services.AddTransient<IRequestExceptionHandler<ExceptionQuery,int,InvalidOperationException>,SecondExceptionHandler>();
                services.AddTransient<IRequestExceptionHandler<ExceptionQuery,int,Exception>,BaseExceptionHandler>();
                services.AddTransient<IRequestExceptionAction<ExceptionQuery,InvalidOperationException>,ExceptionAction>();
                services.AddMeridianMediator(c=>c.AddOpenBehavior(typeof(RequestExceptionProcessorBehavior<,>)));
            }
            using var provider=services.BuildServiceProvider();
            using var token=new CancellationTokenSource();
            var request=new ExceptionQuery();
            var mediator=provider.GetRequiredService<IMediator>();
            if(mode=="handled") Assert.Equal(81,await mediator.Send(request,token.Token));
            else
            {
                var error=await Record.ExceptionAsync(()=>mediator.Send(request,token.Token));
                if(mode.StartsWith("sync",StringComparison.Ordinal)) Assert.Same(trace.Secondary,Assert.IsType<System.Reflection.TargetInvocationException>(error).InnerException);
                else Assert.Same(mode=="async-handler" ? trace.Secondary : trace.Original,error);
            }
            Assert.Same(request,trace.Request);
            Assert.Equal(token.Token,trace.Token);
            if(mode=="handled") Assert.Equal(new[]{"decline","second"},trace.Events);
            if(mode is "unhandled" or "sync-action" or "async-action") Assert.Equal(new[]{"decline","second","base","action"},trace.Events);
            if(mode is "sync-handler" or "async-handler") Assert.Equal(new[]{"decline","second"},trace.Events);
        }
    }
}
public sealed class ExceptionQuery : IRequest<int>;
public sealed class ExceptionTrace {public string Mode {get;set;}="";public List<string> Events {get;}=new();public Exception Original {get;}=new InvalidOperationException("original");public Exception Secondary {get;}=new ArgumentException("secondary");public object? Request;public CancellationToken Token; public RequestExceptionHandlerState<int>? FirstState;}
public sealed class ExceptionQueryHandler(ExceptionTrace trace) : IRequestHandler<ExceptionQuery,int> {public Task<int> Handle(ExceptionQuery q,CancellationToken cancellationToken)=>Task.FromException<int>(trace.Original);}
public sealed class DeclineExceptionHandler(ExceptionTrace trace) : IRequestExceptionHandler<ExceptionQuery,int,InvalidOperationException> {public Task Handle(ExceptionQuery q,InvalidOperationException e,RequestExceptionHandlerState<int> state,CancellationToken cancellationToken) {trace.Events.Add("decline");trace.FirstState=state;trace.Request=q;trace.Token=cancellationToken;return Task.CompletedTask;}}
public sealed class SecondExceptionHandler(ExceptionTrace trace) : IRequestExceptionHandler<ExceptionQuery,int,InvalidOperationException> {public Task Handle(ExceptionQuery q,InvalidOperationException e,RequestExceptionHandlerState<int> state,CancellationToken cancellationToken) {trace.Events.Add("second");Assert.NotSame(trace.FirstState,state);if(trace.Mode=="handled") state.SetHandled(81);if(trace.Mode=="sync-handler") throw trace.Secondary;return trace.Mode=="async-handler" ? Task.FromException(trace.Secondary) : Task.CompletedTask;}}
public sealed class BaseExceptionHandler : IRequestExceptionHandler<ExceptionQuery,int,Exception>
{
    private readonly ExceptionTrace trace;
#pragma warning disable CA2201 // Deliberate fault: the base-typed handler must not be resolved eagerly when a more specific handler handles the exception.
    public BaseExceptionHandler(ExceptionTrace trace) {if(trace.Mode=="handled") throw new Exception("base resolved eagerly");this.trace=trace;}
#pragma warning restore CA2201
    public Task Handle(ExceptionQuery q,Exception e,RequestExceptionHandlerState<int> state,CancellationToken cancellationToken) {trace.Events.Add("base");return Task.CompletedTask;}
}
public sealed class ExceptionAction(ExceptionTrace trace) : IRequestExceptionAction<ExceptionQuery,InvalidOperationException> {public Task Execute(ExceptionQuery q,InvalidOperationException e,CancellationToken cancellationToken) {trace.Events.Add("action");if(trace.Mode=="sync-action") throw trace.Secondary;return trace.Mode=="async-action" ? Task.FromException(trace.Secondary) : Task.CompletedTask;}}
[GenerateMediatorRegistry(typeof(ExceptionQueryHandler),ExceptionHandlerTypes=new[]{typeof(DeclineExceptionHandler),typeof(SecondExceptionHandler),typeof(BaseExceptionHandler)},ExceptionActionTypes=new[]{typeof(ExceptionAction)})] public partial class ExceptionRegistry;

public sealed class ExtraTraceBehavior(PipelineTrace trace) : IPipelineBehavior<PipelineQuery,int> { public Task<int> Handle(PipelineQuery q,RequestHandlerDelegate<int> next,CancellationToken cancellationToken) {trace.Events.Add("extra");return next();} }

public class GeneratedProcessorCompositionTests
{
    [Theory]
    [InlineData(true, false)] [InlineData(false, false)]
    [InlineData(true, true)] [InlineData(false, true)]
    public async Task Mixed_Processors_Run_Once_And_Retain_Unknown_Request_Fallback(bool pre, bool repeated)
    {
        var services = new ServiceCollection().AddSingleton<PipelineTrace>()
            .AddTransient<IRequestHandler<PlainQuery, int>, PlainHandler>();
        void Configure(MeridianMediatorConfiguration configuration)
        {
            configuration.AddGeneratedRegistry<PipelineRegistry>();
            configuration.AddOpenBehavior(typeof(TraceBehavior<,>), -10);
            configuration.AddOpenBehavior(typeof(TraceBehavior<,>), 10);
            if (pre) configuration.AddRequestPreProcessor(typeof(ExtraCompositionPre<>));
            else configuration.AddRequestPostProcessor(typeof(ExtraCompositionPost<,>));
        }
        services.AddMeridianMediator(Configure);
        if (repeated) services.AddMeridianMediator(Configure);
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var trace = provider.GetRequiredService<PipelineTrace>();
        Assert.Equal(7, await mediator.Send(new PipelineQuery()));
        Assert.Equal(1, trace.Events.Count(e => e == "pre"));
        Assert.Equal(1, trace.Events.Count(e => e == "post"));
        Assert.Equal(1, trace.Events.Count(e => e == "extra-processor"));
        Assert.Equal(1, trace.Events.Count(e => e == "handler"));
        Assert.Equal(2, trace.Events.Count(e => e == "open"));
        Assert.Equal(new[] { "open", "closed", "open" }, trace.Events.Take(3));
        trace.Events.Clear();
        Assert.Equal(8, await mediator.Send(new PlainQuery()));
        Assert.Equal(pre ? new[] { "open", "open", "extra-processor", "plain" } : new[] { "open", "open", "plain", "extra-processor" }, trace.Events);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Split_Processor_Registry_Executes_Once_In_Either_Order(bool strict, bool processorsFirst)
    {
        var services = new ServiceCollection().AddSingleton<PipelineTrace>();
        void Handlers(MeridianMediatorConfiguration c) { c.RequireGeneratedDispatch = strict; c.AddGeneratedRegistry<DormantStandardRegistry>(); }
        void Processors(MeridianMediatorConfiguration c) { c.RequireGeneratedDispatch = strict; c.AddGeneratedRegistry<ProcessorOnlyRegistry>(); c.AddGeneratedRegistry<RepeatedPreOnlyRegistry>(); }
        services.AddMeridianMediator(processorsFirst ? Processors : Handlers);
        services.AddMeridianMediator(processorsFirst ? Handlers : Processors);
        services.AddMeridianMediator(Processors);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(7, await provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        Assert.Equal(new[] { "pre", "handler", "post" }, provider.GetRequiredService<PipelineTrace>().Events);
        Assert.Empty(new ProcessorOnlyRegistry().RequestTypes);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Split_Action_Registry_Executes_Once_In_Either_Order(bool strict, bool actionsFirst)
    {
        var trace = new ExceptionTrace();
        var services = new ServiceCollection().AddSingleton(trace);
        void Handlers(MeridianMediatorConfiguration c) { c.RequireGeneratedDispatch = strict; c.AddGeneratedRegistry<ExceptionHandlerOnlyRegistry>(); }
        void Actions(MeridianMediatorConfiguration c) { c.RequireGeneratedDispatch = strict; c.AddGeneratedRegistry<ActionOnlyRegistry>(); }
        services.AddMeridianMediator(actionsFirst ? Actions : Handlers);
        services.AddMeridianMediator(actionsFirst ? Handlers : Actions);
        services.AddMeridianMediator(Actions);
        using var provider = services.BuildServiceProvider();
        var error = await Record.ExceptionAsync(() => provider.GetRequiredService<IMediator>().Send(new ExceptionQuery()));
        Assert.Same(trace.Original, error);
        Assert.Equal(new[] { "action" }, trace.Events);
        Assert.Empty(new ActionOnlyRegistry().RequestTypes);
    }
}
public sealed class ExtraCompositionPre<TRequest>(PipelineTrace trace) : IRequestPreProcessor<TRequest> where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken) { trace.Events.Add("extra-processor"); return Task.CompletedTask; }
}
public sealed class ExtraCompositionPost<TRequest, TResponse>(PipelineTrace trace) : IRequestPostProcessor<TRequest, TResponse> where TRequest : notnull
{
    public Task Process(TRequest request, TResponse response, CancellationToken cancellationToken) { trace.Events.Add("extra-processor"); return Task.CompletedTask; }
}
[GenerateMediatorRegistry(PreProcessorTypes = new[] { typeof(TracePre) }, PostProcessorTypes = new[] { typeof(TracePost) })]
public partial class ProcessorOnlyRegistry;
[GenerateMediatorRegistry(typeof(ExceptionQueryHandler))] public partial class ExceptionHandlerOnlyRegistry;
[GenerateMediatorRegistry(ExceptionActionTypes = new[] { typeof(ExceptionAction) })] public partial class ActionOnlyRegistry;

[GenerateMediatorRegistry(PreProcessorTypes = new[] { typeof(TracePre) })] public partial class RepeatedPreOnlyRegistry;

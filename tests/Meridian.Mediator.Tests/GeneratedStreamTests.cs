using System.Runtime.CompilerServices;
using System.Diagnostics;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Streaming;
using Meridian.Mediator.Wrappers;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

[Collection("Pipeline")]
public class GeneratedStreamTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Manual_Base_Registry_Complete_Wrappers_Work(bool strict)
    {
        var services=new ServiceCollection().AddScoped<GeneratedStreamLog>().AddScoped<GeneratedNoticeLog>();
        services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=strict;c.AddGeneratedRegistry<ManualD3Registry>();});
        using var provider=services.BuildServiceProvider(); using var scope=provider.CreateScope();
        var mediator=scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Publish(new GeneratedNotice());
        await foreach(var _ in mediator.CreateStream(new GeneratedStreamRequest())) { }
        Assert.Equal(new[]{"A"},scope.ServiceProvider.GetRequiredService<GeneratedNoticeLog>().Events);
        Assert.Equal(new[]{"enter","finally"},scope.ServiceProvider.GetRequiredService<GeneratedStreamLog>().Events);
    }

    [Fact]
    public async Task Manual_Null_Wrappers_Keep_Nonstrict_Fallback_And_Reject_Strict()
    {
        var services=new ServiceCollection().AddScoped<GeneratedStreamLog>().AddScoped<GeneratedNoticeLog>();
        services.AddMeridianMediator(c=>c.AddGeneratedRegistry<NullD3Registry>());
        using var provider=services.BuildServiceProvider(); using var scope=provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(new GeneratedNotice());
        await foreach(var _ in scope.ServiceProvider.GetRequiredService<IMediator>().CreateStream(new GeneratedStreamRequest())) { }
        var strict=new ServiceCollection();
        Assert.Throws<InvalidOperationException>(()=>strict.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<NullD3Registry>();}));
        Assert.DoesNotContain(strict,d=>d.ServiceType==typeof(INotificationHandler<GeneratedNotice>));
        Assert.DoesNotContain(strict,d=>d.ServiceType==typeof(IStreamRequestHandler<GeneratedStreamRequest,int>));
    }

    [Fact]
    public void Duplicate_Composed_Stream_Slots_Are_Rejected()
    {
        var services=new ServiceCollection();
        Assert.Throws<InvalidOperationException>(()=>services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<StreamRegistry>();c.AddGeneratedRegistry<ManualD3Registry>();}));
    }

    [Fact]
    public async Task Marker_Inapplicable_Stream_Is_Skipped_Without_Request_Category_Confusion()
    {
        var services=new ServiceCollection();
        services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<DualRegistry>();c.AddOpenBehavior(typeof(DualOrdinaryBehavior<,>));c.AddOpenStreamBehavior(typeof(DualStreamBehavior<,>));});
        using var provider=services.BuildServiceProvider();
        var mediator=provider.GetRequiredService<IMediator>();
        Assert.Equal(11,await mediator.Send((IRequest<int>)new DualRequest()));
        var values=new List<int>(); await foreach(var value in mediator.CreateStream(new DualRequest())) values.Add(value);
        Assert.Equal(new[]{12},values);
    }
    [Fact]
    public void Strict_External_Open_Stream_Descriptor_Is_Rejected()
    {
        var services=new ServiceCollection(); services.AddTransient(typeof(IStreamPipelineBehavior<,>),typeof(GeneratedOpenStreamBehavior<,>));
        Assert.Throws<InvalidOperationException>(()=>services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<StreamRegistry>();}));
    }
    [Fact]
    public void Strict_CrossRegistry_Unrooted_Stream_Pair_Is_Rejected()
    {
        var services=new ServiceCollection();
        Assert.Throws<InvalidOperationException>(()=>services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<StreamBehaviorRegistry>();c.AddGeneratedRegistry<Q06Registry>();c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>));}));
    }

    public static IEnumerable<object[]> LifetimeCases()
    {
        foreach (var generated in new[]{false,true}) foreach (var boxed in new[]{false,true})
        foreach (var listening in new[]{false,true}) foreach (var mode in new[]{"break","complete","failure","cancel-before","cancel-after","consumer-cancel"})
            yield return new object[]{generated,boxed,listening,mode};
    }

    [Theory]
    [MemberData(nameof(LifetimeCases))]
    public async Task Generated_And_Runtime_Stream_Q06_Parity(bool generated, bool boxed, bool listening, string mode)
    {
        using var listener = new ActivityListener { ShouldListenTo = _ => listening, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
        if (listening) ActivitySource.AddActivityListener(listener);
        var services = new ServiceCollection().AddScoped<StreamLifetimeTests.StreamState>();
        if (generated) services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.Lifetime=ServiceLifetime.Scoped;c.AddGeneratedRegistry<Q06Registry>();});
        else
        {
            services.AddMeridianMediator(_=>{});
            services.AddScoped<IStreamRequestHandler<StreamLifetimeTests.LifetimeRequest,int>,StreamLifetimeTests.ScopedStreamHandler>();
            services.AddScoped<IStreamPipelineBehavior<StreamLifetimeTests.LifetimeRequest,int>,StreamLifetimeTests.OuterStreamBehavior>();
            services.AddScoped<IStreamPipelineBehavior<StreamLifetimeTests.LifetimeRequest,int>,StreamLifetimeTests.InnerStreamBehavior>();
        }
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions {ValidateScopes=true});
        StreamLifetimeTests.StreamState state;
        using (var scope=provider.CreateScope())
        {
            state=scope.ServiceProvider.GetRequiredService<StreamLifetimeTests.StreamState>();
            var sender=scope.ServiceProvider.GetRequiredService<IStreamSender>();
            var failure=new InvalidOperationException("deferred original");
            var request=new StreamLifetimeTests.LifetimeRequest(mode=="failure" ? failure : null);
            using var cancellation=new CancellationTokenSource();
            if(mode=="cancel-before") cancellation.Cancel();
            var values=new List<object?>();
            async Task Enumerate()
            {
                var supplied=mode=="consumer-cancel" ? CancellationToken.None : cancellation.Token;
                if(boxed)
                {
                    await foreach(var value in sender.CreateStream((object)request,supplied).WithCancellation(cancellation.Token))
                    { values.Add(value); if(mode=="break") break; if(mode is "cancel-after" or "consumer-cancel") cancellation.Cancel(); }
                }
                else
                {
                    await foreach(var value in sender.CreateStream(request,supplied).WithCancellation(cancellation.Token))
                    { values.Add(value); if(mode=="break") break; if(mode is "cancel-after" or "consumer-cancel") cancellation.Cancel(); }
                }
            }
            if(mode=="failure") Assert.Same(failure,await Assert.ThrowsAsync<InvalidOperationException>(Enumerate));
            else if(mode.StartsWith("cancel",StringComparison.Ordinal) || mode=="consumer-cancel")
                Assert.Equal(cancellation.Token,(await Assert.ThrowsAnyAsync<OperationCanceledException>(Enumerate)).CancellationToken);
            else await Enumerate();
            Assert.Equal(mode=="cancel-before" ? Array.Empty<object?>() : mode=="complete" ? new object?[]{1,2} : new object?[]{1},values);
            Assert.Equal(new[]{"outer-before","inner-before","handler-before","handler-finally","inner-finally","outer-finally"},state.Events);
            Assert.Equal(1,state.ResourcesReleased);
            Assert.False(state.HandlerDisposed); Assert.False(state.OuterDisposed); Assert.False(state.InnerDisposed);
        }
        Assert.True(state.HandlerDisposed); Assert.True(state.OuterDisposed); Assert.True(state.InnerDisposed);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task Stream_Resolution_Timing_Matches_Legacy(bool boxed,bool listening)
    {
        using var listener = new ActivityListener { ShouldListenTo = _ => listening, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
        if(listening) ActivitySource.AddActivityListener(listener);
        var services=new ServiceCollection(); services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<StreamRegistry>();});
        using var provider=services.BuildServiceProvider();
        var mediator=provider.GetRequiredService<IMediator>();
        // Missing constructor dependency fails eagerly only for listener-free typed dispatch.
        if(!boxed&&!listening) Assert.Throws<InvalidOperationException>(()=>mediator.CreateStream(new GeneratedStreamRequest()));
        else if(boxed) { var stream=mediator.CreateStream((object)new GeneratedStreamRequest()); await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var _ in stream) { }}); }
        else { var stream=mediator.CreateStream(new GeneratedStreamRequest()); await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var _ in stream) { }}); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Generated_Stream_Uses_Current_Scope_And_Disposes_Enumeration(bool boxed)
    {
        var services = new ServiceCollection().AddScoped<GeneratedStreamLog>();
        services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<StreamRegistry>(); });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        GeneratedStreamLog prior;
        using (var scope = provider.CreateScope())
        {
            prior = scope.ServiceProvider.GetRequiredService<GeneratedStreamLog>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            if (boxed) { await foreach (var value in mediator.CreateStream((object)new GeneratedStreamRequest())) { Assert.Equal(7, value); break; } }
            else { await foreach (var value in mediator.CreateStream(new GeneratedStreamRequest())) { Assert.Equal(7, value); break; } }
            Assert.Equal(new[] { "enter", "finally" }, prior.Events);
            Assert.False(prior.Disposed);
            await foreach (var _ in mediator.CreateStream(new GeneratedStreamRequest())) { }
            Assert.Equal(4, prior.Events.Count);
        }
        Assert.True(prior.Disposed);
        using var second = provider.CreateScope();
        Assert.NotSame(prior, second.ServiceProvider.GetRequiredService<GeneratedStreamLog>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stream_Behavior_Closed_Before_Open_And_Finally_Reverse(bool strict)
    {
        var services = new ServiceCollection().AddScoped<GeneratedStreamLog>();
        for (var i=0;i<2;i++) services.AddMeridianMediator(c => { c.RequireGeneratedDispatch=strict; c.AddGeneratedRegistry<StreamBehaviorRegistry>(); c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>)); c.AddStreamBehavior<GeneratedClosedStreamBehavior>(); });
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        await foreach (var _ in scope.ServiceProvider.GetRequiredService<IMediator>().CreateStream(new GeneratedStreamRequest())) { break; }
        Assert.Equal(new[]{"closed:enter","open:enter","enter","finally","open:finally","closed:finally"},scope.ServiceProvider.GetRequiredService<GeneratedStreamLog>().Events);
    }
    [Fact]
    public void Strict_Unrooted_Stream_Behavior_Is_Rejected_Before_Resolution()
    {
        var services = new ServiceCollection();
        var error=Assert.Throws<InvalidOperationException>(()=>services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<StreamRegistry>();c.AddOpenStreamBehavior(typeof(GeneratedOpenStreamBehavior<,>));}));
        Assert.Contains(nameof(GeneratedOpenStreamBehavior<GeneratedStreamRequest,int>).Split('`')[0],error.Message);
    }

    [Fact]
    public async Task Strict_Stream_Miss_Rejects_Before_Warmed_Legacy_Cache()
    {
        var legacy = new ServiceCollection().AddSingleton<GeneratedStreamLog>().AddTransient<IStreamRequestHandler<GeneratedStreamRequest,int>, GeneratedStreamHandler>();
        using var warm = legacy.BuildServiceProvider();
        await foreach (var _ in new Mediator(warm).CreateStream(new GeneratedStreamRequest())) { }
        var strict = new ServiceCollection(); strict.AddMeridianMediator(c => c.RequireGeneratedDispatch = true);
        using var provider = strict.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMediator>().CreateStream(new GeneratedStreamRequest()));
        Assert.Contains("stream", exception.Message); Assert.Contains(nameof(GeneratedStreamRequest), exception.Message);
    }
}
public sealed class GeneratedStreamRequest : IStreamRequest<int> { }
public sealed class GeneratedStreamLog : IDisposable { public List<string> Events { get; } = new(); public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
public sealed class GeneratedStreamHandler(GeneratedStreamLog log) : IStreamRequestHandler<GeneratedStreamRequest,int>
{
    public async IAsyncEnumerable<int> Handle(GeneratedStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    { log.Events.Add("enter"); try { cancellationToken.ThrowIfCancellationRequested(); yield return 7; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return 8; } finally { log.Events.Add("finally"); } }
}
[GenerateMediatorRegistry(typeof(GeneratedStreamHandler))] public partial class StreamRegistry { }

public sealed class GeneratedClosedStreamBehavior(GeneratedStreamLog log) : IStreamPipelineBehavior<GeneratedStreamRequest,int>
{
    public async IAsyncEnumerable<int> Handle(GeneratedStreamRequest request,StreamHandlerDelegate<int> next,[EnumeratorCancellation] CancellationToken cancellationToken)
    { log.Events.Add("closed:enter"); try { await foreach(var value in next().WithCancellation(cancellationToken)) yield return value; } finally { log.Events.Add("closed:finally"); } }
}
public sealed class GeneratedOpenStreamBehavior<TRequest,TResponse>(GeneratedStreamLog log) : IStreamPipelineBehavior<TRequest,TResponse> where TRequest:notnull
{
    public async IAsyncEnumerable<TResponse> Handle(TRequest request,StreamHandlerDelegate<TResponse> next,[EnumeratorCancellation] CancellationToken cancellationToken)
    { log.Events.Add("open:enter"); try { await foreach(var value in next().WithCancellation(cancellationToken)) yield return value; } finally { log.Events.Add("open:finally"); } }
}
[GenerateMediatorRegistry(typeof(GeneratedStreamHandler),StreamBehaviorTypes=new[]{typeof(GeneratedOpenStreamBehavior<,>),typeof(GeneratedClosedStreamBehavior)})]
public partial class StreamBehaviorRegistry { }

[GenerateMediatorRegistry(typeof(StreamLifetimeTests.ScopedStreamHandler), StreamBehaviorTypes=new[]{typeof(StreamLifetimeTests.OuterStreamBehavior),typeof(StreamLifetimeTests.InnerStreamBehavior)})]
public partial class Q06Registry { }

public class ManualD3Registry : IGeneratedMediatorRegistry
{
    public string RegistryId=>GetType().Name;
    public IReadOnlyCollection<Type> RequestTypes=>Array.Empty<Type>();
    public IReadOnlyCollection<Type> NotificationTypes=>new[]{typeof(GeneratedNotice)};
    public IReadOnlyCollection<Type> StreamTypes=>new[]{typeof(GeneratedStreamRequest)};
    public void RegisterServices(IServiceCollection services,ServiceLifetime handlerLifetime)
    {
        services.Add(new ServiceDescriptor(typeof(INotificationHandler<GeneratedNotice>),sp=>new GeneratedNoticeA(sp.GetRequiredService<GeneratedNoticeLog>()),handlerLifetime));
        services.Add(new ServiceDescriptor(typeof(IStreamRequestHandler<GeneratedStreamRequest,int>),sp=>new GeneratedStreamHandler(sp.GetRequiredService<GeneratedStreamLog>()),handlerLifetime));
    }
    public RequestHandlerBase? FindRequestHandler(Type requestType)=>null;
    public virtual NotificationHandlerWrapperBase? FindNotificationHandler(Type notificationType)=>notificationType==typeof(GeneratedNotice)?new NotificationHandlerWrapperImpl<GeneratedNotice>():null;
    public virtual StreamRequestHandlerBase? FindStreamHandler(Type requestType)=>requestType==typeof(GeneratedStreamRequest)?new StreamRequestHandlerWrapperImpl<GeneratedStreamRequest,int>():null;
}
public sealed class NullD3Registry : ManualD3Registry
{
    public override NotificationHandlerWrapperBase? FindNotificationHandler(Type notificationType)=>null;
    public override StreamRequestHandlerBase? FindStreamHandler(Type requestType)=>null;
}

public interface IDualMarker { }
public sealed class DualRequest : IRequest<int>, IStreamRequest<int> { }
public sealed class DualHandler : IRequestHandler<DualRequest,int>, IStreamRequestHandler<DualRequest,int>
{
    public Task<int> Handle(DualRequest request,CancellationToken cancellationToken)=>Task.FromResult(10);
    async IAsyncEnumerable<int> IStreamRequestHandler<DualRequest,int>.Handle(DualRequest request,[EnumeratorCancellation] CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); yield return 12; await Task.Yield(); }
}
public sealed class DualOrdinaryBehavior<TRequest,TResponse> : Meridian.Mediator.Pipeline.IPipelineBehavior<TRequest,TResponse> where TRequest:notnull
{ public async Task<TResponse> Handle(TRequest request,RequestHandlerDelegate<TResponse> next,CancellationToken cancellationToken)=>(TResponse)(object)((int)(object)(await next())!+1); }
public sealed class DualStreamBehavior<TRequest,TResponse> : IStreamPipelineBehavior<TRequest,TResponse> where TRequest:IDualMarker
{ public IAsyncEnumerable<TResponse> Handle(TRequest request,StreamHandlerDelegate<TResponse> next,CancellationToken cancellationToken)=>throw new InvalidOperationException("inapplicable"); }
[GenerateMediatorRegistry(typeof(DualHandler),BehaviorTypes=new[]{typeof(DualOrdinaryBehavior<,>)},StreamBehaviorTypes=new[]{typeof(DualStreamBehavior<,>)})]
public partial class DualRegistry { }

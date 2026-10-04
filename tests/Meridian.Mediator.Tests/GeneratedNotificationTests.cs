using Meridian.Mediator.Extensions;
using Meridian.Mediator.Publishing;
using Meridian.Mediator.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public class GeneratedNotificationTests
{
    [Theory]
    [InlineData(false,0,false)] [InlineData(true,0,false)]
    [InlineData(false,0,true)] [InlineData(true,0,true)]
    [InlineData(false,1,false)] [InlineData(true,1,false)]
    [InlineData(false,1,true)] [InlineData(true,1,true)]
    [InlineData(false,2,false)] [InlineData(true,2,false)]
    [InlineData(false,2,true)] [InlineData(true,2,true)]
    public async Task Publisher_Failures_Preserve_Strategy_And_Original_Exceptions(bool generated,int strategy,bool multiple)
    {
        var state=new PublisherState(); var first=new InvalidOperationException("sync"); var second=new ArgumentException("async");
        state.Callback=(type,token)=> type==typeof(int) ? throw first : multiple ? Task.FromException(second) : Task.CompletedTask;
        using var provider=PublisherProvider(generated,strategy,state);
        var exception=await Record.ExceptionAsync(()=>provider.GetRequiredService<IMediator>().Publish(new PublisherNotice()));
        Assert.NotNull(exception);
        if(strategy==0 || strategy==1&&!multiple) Assert.Same(first,exception);
        else
        {
            var aggregate=Assert.IsType<AggregateException>(exception);
            Assert.Contains(first,aggregate.InnerExceptions);
            Assert.Equal(multiple?2:1,aggregate.InnerExceptions.Count);
            if(multiple) Assert.Contains(second,aggregate.InnerExceptions);
        }
        Assert.Equal(strategy==0?1:2,state.Started);
    }

    [Theory]
    [InlineData(false,0)] [InlineData(true,0)] [InlineData(false,1)] [InlineData(true,1)] [InlineData(false,2)] [InlineData(true,2)]
    public async Task Publisher_Cancellation_Preserves_Token(bool generated,int strategy)
    {
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        var state=new PublisherState { Callback=(_,token)=>Task.FromCanceled(token) };
        using var provider=PublisherProvider(generated,strategy,state);
        var error=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>provider.GetRequiredService<IMediator>().Publish(new PublisherNotice(),cancellation.Token));
        Assert.Equal(cancellation.Token,error.CancellationToken);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Generated_Concurrent_Publish_Default_Cap_Is_16(int strategy)
    {
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active=0; var max=0; var entered=0;
        var state=new PublisherState();
        state.Callback=async (_,token)=>
        {
            var current=Interlocked.Increment(ref active);
            int seen; do { seen=Volatile.Read(ref max); } while(current>seen&&Interlocked.CompareExchange(ref max,current,seen)!=seen);
            if(Interlocked.Increment(ref entered)==16) reached.TrySetResult();
            try { await gate.Task.WaitAsync(token); } finally { Interlocked.Decrement(ref active); }
        };
        var services=new ServiceCollection().AddSingleton(state);
        services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.AddGeneratedRegistry<FanOutRegistry>();c.NotificationPublisher=strategy==1?new TaskWhenAllPublisher():new ResilientTaskWhenAllPublisher();});
        using var provider=services.BuildServiceProvider();
        var publish=provider.GetRequiredService<IMediator>().Publish(new PublisherNotice());
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(16,Volatile.Read(ref entered)); }
        finally { gate.TrySetResult(); }
        await publish;
        Assert.Equal(20,entered); Assert.Equal(16,max); Assert.Equal(0,active);
    }

    [Fact]
    public async Task Notification_Wrapper_Resolves_Scoped_Handlers_On_Each_Call()
    {
        var services=new ServiceCollection().AddScoped<PublisherState>();
        services.AddMeridianMediator(c=>{c.RequireGeneratedDispatch=true;c.Lifetime=ServiceLifetime.Scoped;c.AddGeneratedRegistry<PublisherRegistry>();});
        using var provider=services.BuildServiceProvider(new ServiceProviderOptions {ValidateScopes=true});
        PublisherState state;
        using(var scope=provider.CreateScope())
        {
            state=scope.ServiceProvider.GetRequiredService<PublisherState>();
            var mediator=scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Publish(new PublisherNotice()); await mediator.Publish(new PublisherNotice());
            Assert.Equal(2,state.Constructed); Assert.Equal(4,state.Started); Assert.Equal(0,state.Disposed);
        }
        Assert.Equal(2,state.Disposed);
        using var second=provider.CreateScope(); await second.ServiceProvider.GetRequiredService<IMediator>().Publish(new PublisherNotice());
        Assert.NotSame(state,second.ServiceProvider.GetRequiredService<PublisherState>());
        Assert.Equal(4,state.Started);
    }

    private static ServiceProvider PublisherProvider(bool generated,int strategy,PublisherState state)
    {
        var services=new ServiceCollection().AddSingleton(state);
        if(!generated) { services.AddTransient<INotificationHandler<PublisherNotice>,PublisherHandler<int>>(); services.AddTransient<INotificationHandler<PublisherNotice>,PublisherHandler<string>>(); }
        services.AddMeridianMediator(c=>{if(generated)c.AddGeneratedRegistry<PublisherRegistry>();c.RequireGeneratedDispatch=generated;c.NotificationPublisher=strategy switch {1=>new TaskWhenAllPublisher(),2=>new ResilientTaskWhenAllPublisher(),_=>new ForeachAwaitPublisher()};});
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Multiple_Registries_Allow_Shared_Notification_Type()
    {
        var services = new ServiceCollection().AddScoped<GeneratedNoticeLog>();
        services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<NoticeRegistryA>(); });
        using var before = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<NoticeRegistryB>(); });
        using var after = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = before.CreateScope(); using var second = after.CreateScope();
        await first.ServiceProvider.GetRequiredService<IMediator>().Publish(new GeneratedNotice());
        await second.ServiceProvider.GetRequiredService<IMediator>().Publish((object)new GeneratedNotice());
        Assert.Equal(new[] { "A" }, first.ServiceProvider.GetRequiredService<GeneratedNoticeLog>().Events);
        Assert.Equal(new[] { "A", "B" }, second.ServiceProvider.GetRequiredService<GeneratedNoticeLog>().Events);
    }

    [Fact]
    public async Task Repeated_Registration_Does_Not_Double_Publish()
    {
        var services = new ServiceCollection().AddScoped<GeneratedNoticeLog>();
        for (var i = 0; i < 2; i++) services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<NoticeRegistryA>(); c.AddGeneratedRegistry<NoticeRegistryB>(); });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(new GeneratedNotice());
        Assert.Equal(new[] { "A", "B" }, scope.ServiceProvider.GetRequiredService<GeneratedNoticeLog>().Events);
    }

    [Fact]
    public async Task Strict_Notification_Miss_Rejects_Before_Warmed_Legacy_Cache()
    {
        var services = new ServiceCollection().AddSingleton<GeneratedNoticeLog>().AddTransient<INotificationHandler<GeneratedNotice>, GeneratedNoticeA>();
        using var warm = services.BuildServiceProvider();
        await new Mediator(warm).Publish(new GeneratedNotice());
        var strict = new ServiceCollection(); strict.AddMeridianMediator(c => c.RequireGeneratedDispatch = true);
        using var provider = strict.BuildServiceProvider();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IMediator>().Publish(new GeneratedNotice()));
        Assert.Contains("notification", exception.Message); Assert.Contains(nameof(GeneratedNotice), exception.Message);
    }
}

public sealed class GeneratedNotice : INotification { }
public sealed class GeneratedNoticeLog { public List<string> Events { get; } = new(); }
public sealed class GeneratedNoticeA(GeneratedNoticeLog log) : INotificationHandler<GeneratedNotice>
{ public Task Handle(GeneratedNotice notice, CancellationToken cancellationToken) { log.Events.Add("A"); return Task.CompletedTask; } }
public sealed class GeneratedNoticeB(GeneratedNoticeLog log) : INotificationHandler<GeneratedNotice>
{ public Task Handle(GeneratedNotice notice, CancellationToken cancellationToken) { log.Events.Add("B"); return Task.CompletedTask; } }
[GenerateMediatorRegistry(typeof(GeneratedNoticeA))] public partial class NoticeRegistryA { }
[GenerateMediatorRegistry(typeof(GeneratedNoticeA), typeof(GeneratedNoticeB))] public partial class NoticeRegistryB { }

public sealed class PublisherNotice : INotification { }
public sealed class PublisherState
{
    public Func<Type,CancellationToken,Task> Callback { get; set; }=(_,_)=>Task.CompletedTask;
    internal int Started; internal int Constructed; internal int Disposed;
}
public sealed class PublisherHandler<T> : INotificationHandler<PublisherNotice>, IDisposable
{
    private readonly PublisherState _state;
    public PublisherHandler(PublisherState state) { _state=state; Interlocked.Increment(ref state.Constructed); }
    public Task Handle(PublisherNotice notice,CancellationToken cancellationToken) { Interlocked.Increment(ref _state.Started); return _state.Callback(typeof(T),cancellationToken); }
    public void Dispose()=>Interlocked.Increment(ref _state.Disposed);
}
[GenerateMediatorRegistry(typeof(PublisherHandler<int>),typeof(PublisherHandler<string>))] public partial class PublisherRegistry { }
[GenerateMediatorRegistry(typeof(PublisherHandler<int>), typeof(PublisherHandler<string>), typeof(PublisherHandler<long>), typeof(PublisherHandler<bool>), typeof(PublisherHandler<byte>), typeof(PublisherHandler<sbyte>), typeof(PublisherHandler<short>), typeof(PublisherHandler<ushort>), typeof(PublisherHandler<uint>), typeof(PublisherHandler<ulong>), typeof(PublisherHandler<float>), typeof(PublisherHandler<double>), typeof(PublisherHandler<decimal>), typeof(PublisherHandler<char>), typeof(PublisherHandler<object>), typeof(PublisherHandler<DateTime>), typeof(PublisherHandler<Guid>), typeof(PublisherHandler<TimeSpan>), typeof(PublisherHandler<Uri>), typeof(PublisherHandler<Version>))]
public partial class FanOutRegistry { }

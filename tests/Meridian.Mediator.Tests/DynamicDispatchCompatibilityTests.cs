using System.Runtime.CompilerServices;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

/// <summary>
/// D4 compatibility: the Native AOT feature guards only change runtimes without dynamic code. On an ordinary
/// (JIT) runtime every legacy reflective path keeps its previous behavior.
/// </summary>
[Collection("Pipeline")]
public class DynamicDispatchCompatibilityTests
{
    public sealed record GuardQuery(int Value) : IRequest<int>;
    public sealed class GuardQueryHandler : IRequestHandler<GuardQuery, int>
    {
        public Task<int> Handle(GuardQuery request, CancellationToken cancellationToken) => Task.FromResult(request.Value + 1);
    }

    public sealed class GuardNotice : INotification;
    public sealed class GuardNoticeHandler : INotificationHandler<GuardNotice>
    {
        public static int Calls;
        public Task Handle(GuardNotice notification, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    public sealed record GuardNumbers(int Count) : IStreamRequest<int>;
    public sealed class GuardNumbersHandler : IStreamRequestHandler<GuardNumbers, int>
    {
        public async IAsyncEnumerable<int> Handle(GuardNumbers request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 1; i <= request.Count; i++)
            {
                yield return i;
                await Task.Yield();
            }
        }
    }

    public sealed class GuardOpenBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public static int Calls;
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return next();
        }
    }

    public sealed class GuardOpenStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public static int Calls;
        public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return next();
        }
    }

    [Fact]
    public void Ordinary_Runtimes_Support_Dynamic_Code_So_The_Native_Guards_Are_Inert()
    {
        Assert.True(RuntimeFeature.IsDynamicCodeSupported);
    }

    [Fact]
    public async Task Legacy_NonStrict_Request_Notification_And_Stream_Dispatch_Still_Work_Without_A_Registry()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(c => { });
        services.AddTransient<IRequestHandler<GuardQuery, int>, GuardQueryHandler>();
        services.AddTransient<INotificationHandler<GuardNotice>, GuardNoticeHandler>();
        services.AddTransient<IStreamRequestHandler<GuardNumbers, int>, GuardNumbersHandler>();
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var before = GuardNoticeHandler.Calls;

        Assert.Equal(8, await mediator.Send(new GuardQuery(7)));
        Assert.Equal(8, await mediator.Send((object)new GuardQuery(7)));
        await mediator.Publish(new GuardNotice());
        await mediator.Publish((object)new GuardNotice());
        Assert.Equal(before + 2, GuardNoticeHandler.Calls);
        Assert.Equal(new[] { 1, 2, 3 }, await Collect(mediator.CreateStream(new GuardNumbers(3))));
        Assert.Equal(new object?[] { 1, 2 }, await Collect(mediator.CreateStream((object)new GuardNumbers(2))));
    }

    [Fact]
    public async Task Legacy_Assembly_Scanning_And_Open_Behavior_Registration_Still_Work()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(c =>
        {
            c.RegisterServicesFromAssemblyContaining<DynamicDispatchCompatibilityTests>();
            c.AddOpenBehavior(typeof(GuardOpenBehavior<,>));
            c.AddOpenStreamBehavior(typeof(GuardOpenStreamBehavior<,>));
        });
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var behaviorCalls = GuardOpenBehavior<GuardQuery, int>.Calls;
        var streamCalls = GuardOpenStreamBehavior<GuardNumbers, int>.Calls;

        Assert.Equal(6, await mediator.Send(new GuardQuery(5)));
        Assert.Equal(new[] { 1, 2 }, await Collect(mediator.CreateStream(new GuardNumbers(2))));
        Assert.Equal(behaviorCalls + 1, GuardOpenBehavior<GuardQuery, int>.Calls);
        Assert.Equal(streamCalls + 1, GuardOpenStreamBehavior<GuardNumbers, int>.Calls);
    }

    [Fact]
    public void Notification_Publisher_Type_Registration_Still_Works()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(c => c.NotificationPublisherType = typeof(Publishing.TaskWhenAllPublisher));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<Publishing.TaskWhenAllPublisher>(provider.GetRequiredService<Publishing.INotificationPublisher>());
    }

    private static async Task<T[]> Collect<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);
        return items.ToArray();
    }
}

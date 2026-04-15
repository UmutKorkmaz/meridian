using System.Diagnostics;
using System.Runtime.CompilerServices;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

public sealed record ActivityProbeRequest(int Value) : IRequest<int>;

public sealed class ActivityProbeRequestHandler : IRequestHandler<ActivityProbeRequest, int>
{
    public static string? BeforeAwaitOperation { get; set; }
    public static string? AfterAwaitOperation { get; set; }

    public async Task<int> Handle(ActivityProbeRequest request, CancellationToken cancellationToken)
    {
        BeforeAwaitOperation = Activity.Current?.OperationName;
        await Task.Yield();
        AfterAwaitOperation = Activity.Current?.OperationName;
        return request.Value;
    }
}

public sealed record FailingActivityProbeRequest() : IRequest<int>;

public sealed class FailingActivityProbeRequestHandler : IRequestHandler<FailingActivityProbeRequest, int>
{
    public async Task<int> Handle(FailingActivityProbeRequest request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException("request boom");
    }
}

public sealed record ActivityProbeNotification(string Value) : INotification;

public sealed class ActivityProbeNotificationHandler : INotificationHandler<ActivityProbeNotification>
{
    public static string? BeforeAwaitOperation { get; set; }
    public static string? AfterAwaitOperation { get; set; }

    public async Task Handle(ActivityProbeNotification notification, CancellationToken cancellationToken)
    {
        BeforeAwaitOperation = Activity.Current?.OperationName;
        await Task.Yield();
        AfterAwaitOperation = Activity.Current?.OperationName;
    }
}

public sealed record ActivityProbeStream(int Count) : IStreamRequest<int>;

public sealed class ActivityProbeStreamHandler : IStreamRequestHandler<ActivityProbeStream, int>
{
    public static List<string?> ObservedOperations { get; } = new();

    public async IAsyncEnumerable<int> Handle(
        ActivityProbeStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            ObservedOperations.Add(Activity.Current?.OperationName);
            yield return i;
            await Task.Yield();
        }
    }
}

public sealed record FailingActivityProbeStream() : IStreamRequest<int>;

public sealed class FailingActivityProbeStreamHandler : IStreamRequestHandler<FailingActivityProbeStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        FailingActivityProbeStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("stream boom");
    }
}

public sealed record CancellableActivityProbeStream() : IStreamRequest<int>;

public sealed class CancellableActivityProbeStreamHandler : IStreamRequestHandler<CancellableActivityProbeStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        CancellableActivityProbeStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        yield return 2;
    }
}

[Collection("Pipeline")]
public class TracingTests
{
    [Fact]
    public async Task Send_Activity_Stays_Current_Across_Handler_Await()
    {
        ActivityProbeRequestHandler.BeforeAwaitOperation = null;
        ActivityProbeRequestHandler.AfterAwaitOperation = null;

        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IRequestHandler<ActivityProbeRequest, int>, ActivityProbeRequestHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var result = await mediator.Send(new ActivityProbeRequest(42));

        Assert.Equal(42, result);
        Assert.Equal("Mediator.Send ActivityProbeRequest", ActivityProbeRequestHandler.BeforeAwaitOperation);
        Assert.Equal("Mediator.Send ActivityProbeRequest", ActivityProbeRequestHandler.AfterAwaitOperation);

        var stopped = Assert.Single(collector.Stopped);
        Assert.Equal("Mediator.Send ActivityProbeRequest", stopped.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, stopped.Status);
        Assert.Equal(typeof(ActivityProbeRequest).FullName, stopped.GetTagItem("meridian.request_type"));
        Assert.Equal(typeof(int).FullName, stopped.GetTagItem("meridian.response_type"));
    }

    [Fact]
    public async Task Publish_Activity_Stays_Current_Across_Handler_Await()
    {
        ActivityProbeNotificationHandler.BeforeAwaitOperation = null;
        ActivityProbeNotificationHandler.AfterAwaitOperation = null;

        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddMeridianMediator(_ => { });
        services.AddTransient<INotificationHandler<ActivityProbeNotification>, ActivityProbeNotificationHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        await mediator.Publish(new ActivityProbeNotification("hello"));

        Assert.Equal("Mediator.Publish ActivityProbeNotification", ActivityProbeNotificationHandler.BeforeAwaitOperation);
        Assert.Equal("Mediator.Publish ActivityProbeNotification", ActivityProbeNotificationHandler.AfterAwaitOperation);

        var stopped = Assert.Single(collector.Stopped);
        Assert.Equal("Mediator.Publish ActivityProbeNotification", stopped.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, stopped.Status);
        Assert.Equal(typeof(ActivityProbeNotification).FullName, stopped.GetTagItem("meridian.notification_type"));
    }

    [Fact]
    public async Task CreateStream_Defers_Activity_Until_Enumeration_And_Tracks_Completion()
    {
        ActivityProbeStreamHandler.ObservedOperations.Clear();

        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IStreamRequestHandler<ActivityProbeStream, int>, ActivityProbeStreamHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var stream = mediator.CreateStream(new ActivityProbeStream(2));

        Assert.Empty(collector.Started);
        Assert.Empty(collector.Stopped);

        var items = new List<int>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 1, 2 }, items);
        Assert.Equal(
            new[] { "Mediator.CreateStream ActivityProbeStream", "Mediator.CreateStream ActivityProbeStream" },
            ActivityProbeStreamHandler.ObservedOperations);

        Assert.Single(collector.Started);
        var stopped = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Ok, stopped.Status);
        Assert.Equal(typeof(ActivityProbeStream).FullName, stopped.GetTagItem("meridian.stream_request_type"));
        Assert.Equal(typeof(int).FullName, stopped.GetTagItem("meridian.response_type"));
    }

    [Fact]
    public async Task Send_Activity_Records_Error_Details()
    {
        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IRequestHandler<FailingActivityProbeRequest, int>, FailingActivityProbeRequestHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new FailingActivityProbeRequest()));

        Assert.Equal("request boom", ex.Message);

        var stopped = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Error, stopped.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, stopped.GetTagItem("exception.type"));
        Assert.Equal("request boom", stopped.GetTagItem("exception.message"));
    }

    [Fact]
    public async Task CreateStream_Activity_Records_Error_Details()
    {
        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IStreamRequestHandler<FailingActivityProbeStream, int>, FailingActivityProbeStreamHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        var items = new List<int>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in mediator.CreateStream(new FailingActivityProbeStream()))
            {
                items.Add(item);
            }
        });

        Assert.Equal("stream boom", ex.Message);
        Assert.Equal(new[] { 1 }, items);

        var stopped = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Error, stopped.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, stopped.GetTagItem("exception.type"));
        Assert.Equal("stream boom", stopped.GetTagItem("exception.message"));
    }

    [Fact]
    public async Task CreateStream_Cancellation_Does_Not_Record_Error_Status()
    {
        using var collector = new ActivityCollector();
        var services = new ServiceCollection();
        services.AddMeridianMediator(_ => { });
        services.AddTransient<IStreamRequestHandler<CancellableActivityProbeStream, int>, CancellableActivityProbeStreamHandler>();

        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
        using var cts = new CancellationTokenSource();
        var items = new List<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in mediator.CreateStream(new CancellableActivityProbeStream(), cts.Token))
            {
                items.Add(item);
                cts.Cancel();
            }
        });

        Assert.Equal(new[] { 1 }, items);

        var stopped = Assert.Single(collector.Stopped);
        Assert.NotEqual(ActivityStatusCode.Error, stopped.Status);
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public List<Activity> Started { get; } = [];
        public List<Activity> Stopped { get; } = [];

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "Meridian.Mediator",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity => Started.Add(activity),
                ActivityStopped = activity => Stopped.Add(activity)
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}

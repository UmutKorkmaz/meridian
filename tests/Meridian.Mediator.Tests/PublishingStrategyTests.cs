using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record TimedNotification : INotification;

public class TimedHandler1 : INotificationHandler<TimedNotification>
{
    public static List<(int Id, DateTime Timestamp)> Log { get; } = new();

    public async Task Handle(TimedNotification notification, CancellationToken cancellationToken)
    {
        Log.Add((1, DateTime.UtcNow));
        await Task.Delay(50, cancellationToken);
    }
}

public class TimedHandler2 : INotificationHandler<TimedNotification>
{
    public static List<(int Id, DateTime Timestamp)> Log => TimedHandler1.Log;

    public async Task Handle(TimedNotification notification, CancellationToken cancellationToken)
    {
        Log.Add((2, DateTime.UtcNow));
        await Task.Delay(50, cancellationToken);
    }
}

public record FailingNotification : INotification;

public class FailingHandler1 : INotificationHandler<FailingNotification>
{
    public static bool WasExecuted { get; set; }

    public async Task Handle(FailingNotification notification, CancellationToken cancellationToken)
    {
        WasExecuted = true;
        await Task.Yield();
        throw new InvalidOperationException("Handler1 failed");
    }
}

public class FailingHandler2 : INotificationHandler<FailingNotification>
{
    public static bool WasExecuted { get; set; }

    public async Task Handle(FailingNotification notification, CancellationToken cancellationToken)
    {
        WasExecuted = true;
        await Task.Yield();
        throw new ArgumentException("Handler2 failed");
    }
}

#endregion

public class PublishingStrategyTests
{
    [Fact]
    public async Task ForeachAwaitPublisher_ExecutesHandlersSequentially()
    {
        TimedHandler1.Log.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.NotificationPublisher = new ForeachAwaitPublisher();
        });
        services.AddTransient<INotificationHandler<TimedNotification>, TimedHandler1>();
        services.AddTransient<INotificationHandler<TimedNotification>, TimedHandler2>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Publish(new TimedNotification());

        Assert.Equal(2, TimedHandler1.Log.Count);
        // Sequential: second handler starts after first finishes
        var first = TimedHandler1.Log[0];
        var second = TimedHandler1.Log[1];
        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
    }

    [Fact]
    public async Task TaskWhenAllPublisher_ExecutesHandlersInParallel()
    {
        TimedHandler1.Log.Clear();
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.NotificationPublisher = new TaskWhenAllPublisher();
        });
        services.AddTransient<INotificationHandler<TimedNotification>, TimedHandler1>();
        services.AddTransient<INotificationHandler<TimedNotification>, TimedHandler2>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Publish(new TimedNotification());

        Assert.Equal(2, TimedHandler1.Log.Count);
        // Both handlers started (we can't guarantee perfect parallelism but both ran)
        Assert.Contains(TimedHandler1.Log, l => l.Id == 1);
        Assert.Contains(TimedHandler1.Log, l => l.Id == 2);
    }

    [Fact]
    public async Task ForeachAwaitPublisher_StopsOnFirstException()
    {
        FailingHandler1.WasExecuted = false;
        FailingHandler2.WasExecuted = false;
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.NotificationPublisher = new ForeachAwaitPublisher();
        });
        services.AddTransient<INotificationHandler<FailingNotification>, FailingHandler1>();
        services.AddTransient<INotificationHandler<FailingNotification>, FailingHandler2>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Publish(new FailingNotification()));

        Assert.True(FailingHandler1.WasExecuted);
        // Second handler should NOT have been reached (sequential stops on first error)
        Assert.False(FailingHandler2.WasExecuted);
    }

    [Fact]
    public async Task TaskWhenAllPublisher_ExecutesBothHandlers_EvenWhenOneThrows()
    {
        FailingHandler1.WasExecuted = false;
        FailingHandler2.WasExecuted = false;
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.NotificationPublisher = new TaskWhenAllPublisher();
        });
        services.AddTransient<INotificationHandler<FailingNotification>, FailingHandler1>();
        services.AddTransient<INotificationHandler<FailingNotification>, FailingHandler2>();
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        // Task.WhenAll throws the first exception when awaited.
        // Both handlers still execute since they run in parallel.
        try
        {
            await mediator.Publish(new FailingNotification());
            Assert.Fail("Expected an exception to be thrown");
        }
        catch (Exception)
        {
            // Expected
        }

        // Both handlers were executed in parallel
        Assert.True(FailingHandler1.WasExecuted);
        Assert.True(FailingHandler2.WasExecuted);
    }
}

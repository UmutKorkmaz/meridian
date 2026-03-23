using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

#region Test Fixtures

public record OrderPlaced(int OrderId) : INotification;

public class OrderPlacedHandler1 : INotificationHandler<OrderPlaced>
{
    public static bool WasHandled { get; set; }
    public static int ReceivedOrderId { get; set; }

    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        WasHandled = true;
        ReceivedOrderId = notification.OrderId;
        return Task.CompletedTask;
    }
}

public class OrderPlacedHandler2 : INotificationHandler<OrderPlaced>
{
    public static bool WasHandled { get; set; }
    public static int ReceivedOrderId { get; set; }

    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        WasHandled = true;
        ReceivedOrderId = notification.OrderId;
        return Task.CompletedTask;
    }
}

public record NoHandlerNotification : INotification;

public record SequenceNotification : INotification;

public class SequenceHandler1 : INotificationHandler<SequenceNotification>
{
    public static List<int> ExecutionOrder { get; } = new();

    public async Task Handle(SequenceNotification notification, CancellationToken cancellationToken)
    {
        ExecutionOrder.Add(1);
        await Task.Yield();
    }
}

public class SequenceHandler2 : INotificationHandler<SequenceNotification>
{
    public static List<int> ExecutionOrder => SequenceHandler1.ExecutionOrder;

    public async Task Handle(SequenceNotification notification, CancellationToken cancellationToken)
    {
        ExecutionOrder.Add(2);
        await Task.Yield();
    }
}

#endregion

public class NotificationTests
{
    private IMediator BuildMediator(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => { });
        services.AddTransient<INotificationHandler<OrderPlaced>, OrderPlacedHandler1>();
        services.AddTransient<INotificationHandler<OrderPlaced>, OrderPlacedHandler2>();
        services.AddTransient<INotificationHandler<SequenceNotification>, SequenceHandler1>();
        services.AddTransient<INotificationHandler<SequenceNotification>, SequenceHandler2>();
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Publish_InvokesAllRegisteredHandlers()
    {
        OrderPlacedHandler1.WasHandled = false;
        OrderPlacedHandler2.WasHandled = false;

        var mediator = BuildMediator();
        await mediator.Publish(new OrderPlaced(42));

        Assert.True(OrderPlacedHandler1.WasHandled);
        Assert.True(OrderPlacedHandler2.WasHandled);
    }

    [Fact]
    public async Task Publish_WithNoHandlers_DoesNotThrow()
    {
        var mediator = BuildMediator();
        await mediator.Publish(new NoHandlerNotification());
    }

    [Fact]
    public async Task Publish_MultipleHandlers_AllReceiveCorrectData()
    {
        OrderPlacedHandler1.ReceivedOrderId = 0;
        OrderPlacedHandler2.ReceivedOrderId = 0;

        var mediator = BuildMediator();
        await mediator.Publish(new OrderPlaced(99));

        Assert.Equal(99, OrderPlacedHandler1.ReceivedOrderId);
        Assert.Equal(99, OrderPlacedHandler2.ReceivedOrderId);
    }

    [Fact]
    public async Task Publish_DefaultPublisher_ExecutesSequentially()
    {
        SequenceHandler1.ExecutionOrder.Clear();

        var mediator = BuildMediator();
        await mediator.Publish(new SequenceNotification());

        Assert.Equal(2, SequenceHandler1.ExecutionOrder.Count);
        // Both handlers executed
        Assert.Contains(1, SequenceHandler1.ExecutionOrder);
        Assert.Contains(2, SequenceHandler1.ExecutionOrder);
    }

    [Fact]
    public async Task Publish_NullNotification_ThrowsArgumentNullException()
    {
        var mediator = BuildMediator();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Publish<OrderPlaced>(null!));
    }

    [Fact]
    public async Task Publish_ObjectOverload_WithNonNotification_ThrowsArgumentException()
    {
        var mediator = BuildMediator();
        await Assert.ThrowsAsync<ArgumentException>(
            () => mediator.Publish((object)"not a notification"));
    }
}

using Meridian.Mediator;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Showcase;

public static class NotificationMediatorDemo
{
    public static async Task RunAsync()
    {
        ShowcaseOutput.WriteHeader("Notifications");

        AuditOrderPlacedHandler.Reset();
        FailingOrderPlacedHandler.Reset();

        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ShowcaseAssemblyMarker>();
            cfg.NotificationPublisherType = typeof(ResilientTaskWhenAllPublisher);
        });

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        try
        {
            await mediator.Publish(new OrderPlacedNotification("ORD-1001"));
        }
        catch (AggregateException ex)
        {
            Console.WriteLine($"Publisher => {ex.InnerExceptions.Count} handler failure(s)");
        }

        Console.WriteLine($"Audit handler ran => {AuditOrderPlacedHandler.CallCount}");
        Console.WriteLine($"Failing handler ran => {FailingOrderPlacedHandler.CallCount}");
        Console.WriteLine();
    }
}

public record OrderPlacedNotification(string OrderId) : INotification;

public sealed class AuditOrderPlacedHandler : INotificationHandler<OrderPlacedNotification>
{
    public static int CallCount { get; private set; }

    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        CallCount++;
        Console.WriteLine($"[notification] audited {notification.OrderId}");
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        CallCount = 0;
    }
}

public sealed class FailingOrderPlacedHandler : INotificationHandler<OrderPlacedNotification>
{
    public static int CallCount { get; private set; }

    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("Simulated downstream notification failure.");
    }

    public static void Reset()
    {
        CallCount = 0;
    }
}

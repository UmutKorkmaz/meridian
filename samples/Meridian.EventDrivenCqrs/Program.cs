using Meridian.Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.EventDrivenCqrs;

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddEventDrivenCqrsServices();

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var opened = await mediator.Send(new Commands.OpenOrderCommand("cust-1", "SKU-100", 4));
        Console.WriteLine($"Opened order: {opened.OrderId} status={opened.Status}");

        var packed = await mediator.Send(new Commands.AdvanceOrderCommand(opened.OrderId, "Packed"));
        Console.WriteLine($"Advance: {packed.OrderId} status={packed.Status}");

        var shipped = await mediator.Send(new Commands.AdvanceOrderCommand(opened.OrderId, "Shipped"));
        Console.WriteLine($"Advance: {shipped.OrderId} status={shipped.Status}");

        var projection = await mediator.Send(new Queries.GetOrderProjectionQuery(opened.OrderId));
        Console.WriteLine($"Projection: {projection.OrderId} status={projection.Status} events={projection.EventCount}");

        var list = await mediator.Send(new Queries.ListOrdersSummaryQuery());
        Console.WriteLine($"Summary: {list.TotalOrders} orders, total= {list.TotalQuantity}");

        var timeline = mediator.CreateStream(new Queries.GetOrderTimelineStream(opened.OrderId));
        await foreach (var item in timeline)
        {
            Console.WriteLine($"Timeline: {item.Message}");
        }
    }
}

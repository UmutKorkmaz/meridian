using Meridian.Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.EventDrivenCqrs;

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddEventDrivenCqrsServices();

        // Count actual handler calls: cache hits must bypass this wrapper.
        var summaryExecutions = 0;
        services.AddTransient<IRequestHandler<Queries.ListOrdersSummaryQuery, OrderSummaryListDto>>(sp =>
            new CountingHandler<Queries.ListOrdersSummaryQuery, OrderSummaryListDto>(
                ActivatorUtilities.CreateInstance<Queries.ListOrdersSummaryQueryHandler>(sp), () => summaryExecutions++));
        var projectionExecutions = 0;
        services.AddTransient<IRequestHandler<Queries.GetOrderProjectionQuery, OrderProjectionDto>>(sp =>
            new CountingHandler<Queries.GetOrderProjectionQuery, OrderProjectionDto>(
                ActivatorUtilities.CreateInstance<Queries.GetOrderProjectionQueryHandler>(sp), () => projectionExecutions++));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var emptySummary = await mediator.Send(new Queries.ListOrdersSummaryQuery());
        var cachedSummary = await mediator.Send(new Queries.ListOrdersSummaryQuery());
        Require(summaryExecutions == 1 && ReferenceEquals(emptySummary, cachedSummary) && emptySummary.TotalOrders == 0,
            "summary miss then hit before opening an order");

        var opened = await mediator.Send(new Commands.OpenOrderCommand("cust-1", "SKU-100", 4));
        Console.WriteLine($"Opened order: {opened.OrderId} status={opened.Status}");

        var openedSummary = await mediator.Send(new Queries.ListOrdersSummaryQuery());
        var initialProjection = await mediator.Send(new Queries.GetOrderProjectionQuery(opened.OrderId));
        var cachedProjection = await mediator.Send(new Queries.GetOrderProjectionQuery(opened.OrderId));
        Require(summaryExecutions == 2 && openedSummary.TotalOrders == 1 && openedSummary.TotalQuantity == 4,
            "open invalidates summary and exposes new order");
        Require(projectionExecutions == 1 && ReferenceEquals(initialProjection, cachedProjection) && initialProjection.Status == "New" && initialProjection.EventCount == 1,
            "projection miss then hit");

        var packed = await mediator.Send(new Commands.AdvanceOrderCommand(opened.OrderId, "Packed"));
        Console.WriteLine($"Advance: {packed.OrderId} status={packed.Status}");

        var packedProjection = await mediator.Send(new Queries.GetOrderProjectionQuery(opened.OrderId));
        var packedSummary = await mediator.Send(new Queries.ListOrdersSummaryQuery());
        Require(packed.Status == "Packed" && packedProjection.Status == "Packed" && packedProjection.EventCount == 2
            && packedSummary.StatusBreakdown.SequenceEqual(new[] { "Packed" }) && projectionExecutions == 2 && summaryExecutions == 3,
            "advance invalidates both views and exposes packed state");

        var shipped = await mediator.Send(new Commands.AdvanceOrderCommand(opened.OrderId, "Shipped"));
        Console.WriteLine($"Advance: {shipped.OrderId} status={shipped.Status}");

        var projection = await mediator.Send(new Queries.GetOrderProjectionQuery(opened.OrderId));
        Console.WriteLine($"Projection: {projection.OrderId} status={projection.Status} events={projection.EventCount}");

        var list = await mediator.Send(new Queries.ListOrdersSummaryQuery());
        Console.WriteLine($"Summary: {list.TotalOrders} orders, total= {list.TotalQuantity}");

        Require(shipped.Status == "Shipped" && projection.Status == "Shipped" && projection.EventCount == 3
            && list.StatusBreakdown.SequenceEqual(new[] { "Shipped" }) && projectionExecutions == 3 && summaryExecutions == 4,
            "second advance refreshes both views");
        await mediator.Send(new Queries.GetOrderProjectionQuery(opened.OrderId));
        await mediator.Send(new Queries.ListOrdersSummaryQuery());
        Require(projectionExecutions == 3 && summaryExecutions == 4, "refreshed views are cached");
        Console.WriteLine($"PASS cache assertions: summary handlers={summaryExecutions}; projection handlers={projectionExecutions}; status=New->Packed->Shipped");

        var timeline = mediator.CreateStream(new Queries.GetOrderTimelineStream(opened.OrderId));
        await foreach (var item in timeline)
        {
            Console.WriteLine($"Timeline: {item.Message}");
        }
    }

    private static void Require(bool condition, string assertion)
    {
        if (!condition) throw new InvalidOperationException($"Sample assertion failed: {assertion}");
    }

    private sealed class CountingHandler<TRequest, TResponse>(
        IRequestHandler<TRequest, TResponse> inner, Action onExecution)
        : IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken)
        {
            onExecution();
            return inner.Handle(request, cancellationToken);
        }
    }
}

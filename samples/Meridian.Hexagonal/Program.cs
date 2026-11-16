using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Hexagonal;

public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine("Meridian Hexagonal Sample");

        var services = new ServiceCollection();
        services.AddHexagonalServices();

        // Count actual handler calls: cache hits must bypass this wrapper.
        var catalogExecutions = 0;
        services.AddTransient<IRequestHandler<BrowseCatalogQuery, CatalogDto>>(sp =>
            new CountingHandler<BrowseCatalogQuery, CatalogDto>(
                ActivatorUtilities.CreateInstance<BrowseCatalogQueryHandler>(sp), () => catalogExecutions++));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var catalog = await mediator.Send(new BrowseCatalogQuery());
        Console.WriteLine($"Catalog has {catalog.Items.Count} products");
        foreach (var item in catalog.Items)
        {
            Console.WriteLine($" - {item.Sku} {item.Name} | stock={item.Stock} | {item.PriceLabel}");
        }

        var catalogHit = await mediator.Send(new BrowseCatalogQuery());
        Require(catalogExecutions == 1 && ReferenceEquals(catalog, catalogHit), "catalog miss then hit");
        var initialStock = catalog.Items.Single(item => item.Sku == "HEX-100").Stock;

        var first = await mediator.Send(new PlaceOrderCommand("Ada Lovelace", "HEX-100", 2));
        Console.WriteLine($"Placed: {first.OrderId} total={first.TotalLabel}");

        var refreshed = await mediator.Send(new BrowseCatalogQuery());
        Require(first.OrderId != Guid.Empty && refreshed.Items.Single(item => item.Sku == "HEX-100").Stock == initialStock - 2
            && catalogExecutions == 2, "order invalidates catalog and exposes reduced stock");
        await mediator.Send(new BrowseCatalogQuery());
        Require(catalogExecutions == 2, "refreshed catalog is cached");
        Console.WriteLine($"PASS cache assertions: catalog handlers={catalogExecutions}; stock={initialStock}->{initialStock - 2}");

        var status = await mediator.Send(new GetOrderQuery(first.OrderId));
        Console.WriteLine($"Order status: {status.OrderId} {status.Customer} {status.Status} total={status.TotalLabel}");

        try
        {
            await mediator.Send(new PlaceOrderCommand("Ada Lovelace", "HEX-100", 99));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Business rule: {ex.Message}");
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

using Meridian.Mapping;
using Meridian.Mapping.Extensions;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
namespace Meridian.CleanArchitecture;

public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine("Clean Architecture Sample");

        var services = new ServiceCollection();
        services.AddCleanArchitectureServices();

        // Count actual handler calls: cache hits must bypass this wrapper.
        var catalogExecutions = 0;
        services.AddTransient<IRequestHandler<BrowseCatalogQuery, CatalogDto>>(sp =>
            new CountingHandler<BrowseCatalogQuery, CatalogDto>(
                ActivatorUtilities.CreateInstance<BrowseCatalogQueryHandler>(sp), () => catalogExecutions++));
        var productExecutions = 0;
        services.AddTransient<IRequestHandler<GetProductBySkuQuery, ProductDetailsDto>>(sp =>
            new CountingHandler<GetProductBySkuQuery, ProductDetailsDto>(
                ActivatorUtilities.CreateInstance<GetProductBySkuQueryHandler>(sp), () => productExecutions++));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var catalog = await mediator.Send(new BrowseCatalogQuery());
        Console.WriteLine($"Catalog (cached) item count: {catalog.Items.Count}");

        var cachedCatalog = await mediator.Send(new BrowseCatalogQuery());
        Console.WriteLine($"Cached query invocation: {cachedCatalog.Items.Count} items");

        Require(catalogExecutions == 1 && ReferenceEquals(catalog, cachedCatalog), "catalog miss then hit");

        var selected = await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        Console.WriteLine($"Product details: {selected.Name} -> {selected.FormattedPrice} ({selected.StockRemaining} left)");

        var cachedProduct = await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        Require(productExecutions == 1 && ReferenceEquals(selected, cachedProduct), "product miss then hit");

        var reservation = await mediator.Send(new ReserveInventoryCommand("GADGET-100", 2, Guid.NewGuid()));
        Console.WriteLine($"Reservation: {reservation.Reference} | remaining {reservation.StockRemaining}");

        var depleted = await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        Console.WriteLine($"After reserve: {depleted.StockRemaining} left");

        var refreshedCatalog = await mediator.Send(new BrowseCatalogQuery());
        Require(reservation.Reference != Guid.Empty && depleted.StockRemaining == selected.StockRemaining - 2,
            "reservation succeeds and refreshed product observes reduced stock");
        Require(productExecutions == 2 && catalogExecutions == 2 && !ReferenceEquals(catalog, refreshedCatalog),
            "both invalidated query handlers execute again");
        await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        await mediator.Send(new BrowseCatalogQuery());
        Require(productExecutions == 2 && catalogExecutions == 2, "refreshed queries are cached");
        // Deplete the remainder so the catalog's InStock field also changes visibly.
        await mediator.Send(new ReserveInventoryCommand("GADGET-100", depleted.StockRemaining, Guid.NewGuid()));
        var exhaustedCatalog = await mediator.Send(new BrowseCatalogQuery());
        var exhaustedProduct = await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        Require(catalog.Items.Single(item => item.Sku == "GADGET-100").InStock
            && !exhaustedCatalog.Items.Single(item => item.Sku == "GADGET-100").InStock
            && exhaustedProduct.StockRemaining == 0 && catalogExecutions == 3 && productExecutions == 3,
            "both refreshed query payloads observe depleted inventory");
        await mediator.Send(new BrowseCatalogQuery());
        await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        Require(catalogExecutions == 3 && productExecutions == 3, "depleted query payloads are cached");
        Console.WriteLine($"PASS cache assertions: catalog handlers={catalogExecutions}; product handlers={productExecutions}; stock={selected.StockRemaining}->{depleted.StockRemaining}->0; in-stock=true->false");

        try
        {
            await mediator.Send(new ReserveInventoryCommand("GADGET-100", 999, Guid.NewGuid()));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Domain rule enforced: {ex.Message}");
        }

        var unavailable = await mediator.Send(new GetProductBySkuQuery("PART-404"));
        Console.WriteLine($"Out-of-stock check: {unavailable.StockRemaining} left");
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

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

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var catalog = await mediator.Send(new BrowseCatalogQuery());
        Console.WriteLine($"Catalog (cached) item count: {catalog.Items.Count}");

        var cachedCatalog = await mediator.Send(new BrowseCatalogQuery());
        Console.WriteLine($"Cached query invocation: {cachedCatalog.Items.Count} items");

        var selected = await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        Console.WriteLine($"Product details: {selected.Name} -> {selected.FormattedPrice} ({selected.StockRemaining} left)");

        var reservation = await mediator.Send(new ReserveInventoryCommand("GADGET-100", 2, Guid.NewGuid()));
        Console.WriteLine($"Reservation: {reservation.Reference} | remaining {reservation.StockRemaining}");

        var depleted = await mediator.Send(new GetProductBySkuQuery("GADGET-100"));
        Console.WriteLine($"After reserve: {depleted.StockRemaining} left");

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
}

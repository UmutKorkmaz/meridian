using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Hexagonal;

public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine("Meridian Hexagonal Sample");

        var services = new ServiceCollection();
        services.AddHexagonalServices();

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var catalog = await mediator.Send(new BrowseCatalogQuery());
        Console.WriteLine($"Catalog has {catalog.Items.Count} products");
        foreach (var item in catalog.Items)
        {
            Console.WriteLine($" - {item.Sku} {item.Name} | stock={item.Stock} | {item.PriceLabel}");
        }

        var first = await mediator.Send(new PlaceOrderCommand("Ada Lovelace", "HEX-100", 2));
        Console.WriteLine($"Placed: {first.OrderId} total={first.TotalLabel}");

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
}

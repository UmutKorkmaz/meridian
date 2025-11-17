using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.ModularMonolith;

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        var modules = new IModuleStartup[]
        {
            new Catalog.CatalogModule(),
            new Order.OrderModule(),
            new Billing.BillingModule(),
        };

        foreach (var module in modules)
        {
            module.ConfigureServices(services);
        }

        services.AddModularMonolithServices();

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var catalog = await mediator.Send(new Catalog.Queries.GetCatalogOverviewQuery());
        Console.WriteLine($"Module catalog: {catalog.Items.Count} products");

        var firstOrder = await mediator.Send(new Order.Commands.PlaceOrderCommand("GADGET-100", 2, "cust-001"));
        Console.WriteLine($"Order accepted: {firstOrder.OrderId} total={firstOrder.Total}");

        var secondOrder = await mediator.Send(new Order.Commands.PlaceOrderCommand("GADGET-100", 1, "cust-002"));
        Console.WriteLine($"Second order: {secondOrder.OrderId} total={secondOrder.Total}");

        try
        {
            await mediator.Send(new Order.Commands.PlaceOrderCommand("GADGET-100", 200, "cust-003"));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Module-level rule: {ex.Message}");
        }

        var billingSummary = await mediator.Send(new Billing.Queries.GetBillingSummaryQuery());
        Console.WriteLine($"Billing summary: {billingSummary.InvoiceCount} invoices, total={billingSummary.TotalBilled}");

        var cachedCatalog = await mediator.Send(new Catalog.Queries.GetCatalogOverviewQuery());
        Console.WriteLine($"Cached module query items: {cachedCatalog.Items.Count}");
    }
}

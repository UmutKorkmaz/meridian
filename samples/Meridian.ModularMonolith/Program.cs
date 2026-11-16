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

        // Count actual handler calls: cache hits must bypass this wrapper.
        var catalogExecutions = 0;
        services.AddTransient<IRequestHandler<Catalog.Queries.GetCatalogOverviewQuery, Catalog.CatalogOverviewDto>>(sp =>
            new CountingHandler<Catalog.Queries.GetCatalogOverviewQuery, Catalog.CatalogOverviewDto>(
                ActivatorUtilities.CreateInstance<Catalog.Queries.GetCatalogOverviewQueryHandler>(sp), () => catalogExecutions++));
        var billingExecutions = 0;
        services.AddTransient<IRequestHandler<Billing.Queries.GetBillingSummaryQuery, Billing.BillingSummaryDto>>(sp =>
            new CountingHandler<Billing.Queries.GetBillingSummaryQuery, Billing.BillingSummaryDto>(
                ActivatorUtilities.CreateInstance<Billing.Queries.GetBillingSummaryQueryHandler>(sp), () => billingExecutions++));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var catalog = await mediator.Send(new Catalog.Queries.GetCatalogOverviewQuery());
        Console.WriteLine($"Module catalog: {catalog.Items.Count} products");

        var catalogHit = await mediator.Send(new Catalog.Queries.GetCatalogOverviewQuery());
        var initialBilling = await mediator.Send(new Billing.Queries.GetBillingSummaryQuery());
        var billingHit = await mediator.Send(new Billing.Queries.GetBillingSummaryQuery());
        Require(catalogExecutions == 1 && billingExecutions == 1 && ReferenceEquals(catalog, catalogHit)
            && ReferenceEquals(initialBilling, billingHit) && initialBilling.InvoiceCount == 0, "both module queries miss then hit");

        var firstOrder = await mediator.Send(new Order.Commands.PlaceOrderCommand("GADGET-100", 2, "cust-001"));
        Console.WriteLine($"Order accepted: {firstOrder.OrderId} total={firstOrder.Total}");

        var firstCatalog = await mediator.Send(new Catalog.Queries.GetCatalogOverviewQuery());
        var firstBilling = await mediator.Send(new Billing.Queries.GetBillingSummaryQuery());
        Require(!string.IsNullOrEmpty(firstOrder.OrderId) && firstCatalog.Items.Single(x => x.Sku == "GADGET-100").Stock == 3
            && firstBilling.InvoiceCount == 1 && firstBilling.TotalBilled == firstOrder.Total
            && catalogExecutions == 2 && billingExecutions == 2, "order invalidates catalog and billing and exposes stock and invoice");

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
        Console.WriteLine($"Refreshed module query items: {cachedCatalog.Items.Count}");
        Require(cachedCatalog.Items.Single(x => x.Sku == "GADGET-100").Stock == 2 && billingSummary.InvoiceCount == 2
            && billingSummary.TotalBilled == firstOrder.Total + secondOrder.Total && catalogExecutions == 3 && billingExecutions == 3,
            "second order refreshes both module queries");
        await mediator.Send(new Catalog.Queries.GetCatalogOverviewQuery());
        await mediator.Send(new Billing.Queries.GetBillingSummaryQuery());
        Require(catalogExecutions == 3 && billingExecutions == 3, "refreshed module queries are cached");
        Console.WriteLine($"PASS cache assertions: catalog handlers={catalogExecutions}; billing handlers={billingExecutions}; stock=5->3->2; invoices=0->1->2");
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

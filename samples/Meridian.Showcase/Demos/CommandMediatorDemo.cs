using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Showcase;

public static class CommandMediatorDemo
{
    public static async Task RunAsync()
    {
        ShowcaseOutput.WriteHeader("Mediator Command");

        PlaceOrderHandler.Reset();
        GetCatalogItemHandler.Reset();

        var services = new ServiceCollection();
        services.AddShowcaseMappings();
        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.AddSingleton<ITransactionScopeProvider, DemoTransactionScopeProvider>();
        services.AddSingleton<IMediatorLogger, ConsoleMediatorLogger>();
        services.AddTransient<IValidator<PlaceOrderCommand>, PlaceOrderCommandValidator>();
        services.AddTransient<IAuthorizationHandler<PlaceOrderCommand>, PlaceOrderAuthorizationHandler>();

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<ShowcaseAssemblyMarker>();
            cfg.AddValidationBehavior();
            cfg.AddLoggingBehavior();
            cfg.AddTransactionBehavior();
            cfg.AddRetryBehavior();
            cfg.AddCachingBehavior();
            cfg.AddAuthorizationBehavior();
            cfg.AddIdempotencyBehavior();
        });

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var catalog = await mediator.Send(new GetCatalogItemQuery(" widget-42 "));
        var catalogHit = await mediator.Send(new GetCatalogItemQuery("WIDGET-42"));
        Require(GetCatalogItemHandler.InvocationCount == 2 && ReferenceEquals(catalog, catalogHit),
            "catalog miss (one transient retry) then hit");

        try
        {
            await mediator.Send(new PlaceOrderCommand("ORD-401", "Ada", "widget-42", 99, "ORD-401"));
        }
        catch (UnauthorizedException ex)
        {
            Console.WriteLine($"Authorization => {ex.Message}");
        }

        // Arrange a backing catalog update; the order command invalidates its cached view.
        GetCatalogItemHandler.CatalogPrice = 39.50m;
        var first = await mediator.Send(new PlaceOrderCommand("ORD-1001", "Ada", "widget-42", 2, "ORD-1001"));
        var second = await mediator.Send(new PlaceOrderCommand("ORD-1001", "Ada", "widget-42", 2, "ORD-1001"));

        var refreshed = await mediator.Send(new GetCatalogItemQuery("widget-42"));
        var refreshedHit = await mediator.Send(new GetCatalogItemQuery(" widget-42 "));
        Require(PlaceOrderHandler.ExecutionCount == 1 && first.OrderId == second.OrderId, "command succeeds with idempotency");
        Require(catalog.Price == 42.50m && refreshed.Price == 39.50m && GetCatalogItemHandler.InvocationCount == 3
            && ReferenceEquals(refreshed, refreshedHit), "invalidated catalog reads changed price, then hits cache");
        Console.WriteLine($"PASS cache assertions: catalog attempts={GetCatalogItemHandler.InvocationCount} (includes one retry); command handlers={PlaceOrderHandler.ExecutionCount}; price={catalog.Price}->{refreshed.Price}");

        Console.WriteLine($"Command => {first.OrderId}:{first.Message}");
        Console.WriteLine($"Idempotency => Handler executed {PlaceOrderHandler.ExecutionCount} time(s)");
        Console.WriteLine($"Idempotent response reused => {first.OrderId == second.OrderId}");
        Console.WriteLine();
    }
    private static void Require(bool condition, string assertion)
    {
        if (!condition) throw new InvalidOperationException($"Sample assertion failed: {assertion}");
    }
}

public record PlaceOrderCommand(
    string OrderId,
    string CustomerName,
    string Sku,
    int Quantity,
    string IdempotencyKey)
    : IRequest<OrderReceipt>, ITransactionalRequest, IAuthorizedRequest, IIdempotentRequest, ICacheInvalidatingRequest
{
    public string[] CacheKeysToInvalidate => [CacheKeyComposer.Compose<GetCatalogItemQuery>($"catalog:{Sku.Trim().ToUpperInvariant()}")];
}

public sealed class PlaceOrderCommandValidator : IValidator<PlaceOrderCommand>
{
    public Task<ValidationResult> ValidateAsync(PlaceOrderCommand instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(instance.CustomerName))
        {
            result.Errors.Add(new ValidationError(nameof(instance.CustomerName), "Customer name is required."));
        }

        if (instance.Quantity <= 0)
        {
            result.Errors.Add(new ValidationError(nameof(instance.Quantity), "Quantity must be greater than zero."));
        }

        return Task.FromResult(result);
    }
}

public sealed class PlaceOrderAuthorizationHandler : IAuthorizationHandler<PlaceOrderCommand>
{
    public Task<AuthorizationResult> AuthorizeAsync(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            request.Quantity > 10
                ? AuthorizationResult.Fail("Demo authorization only allows quantities up to 10.")
                : AuthorizationResult.Success());
    }
}

public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrderCommand, OrderReceipt>
{
    public static int ExecutionCount { get; private set; }

    public Task<OrderReceipt> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return Task.FromResult(new OrderReceipt(request.OrderId, $"Created order for {request.CustomerName} x{request.Quantity}."));
    }

    public static void Reset()
    {
        ExecutionCount = 0;
    }
}

using Meridian.Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.ModularMonolith;

public static class Order
{
    public sealed class OrderModule : IModuleStartup
    {
        public string Name => "Orders";

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IOrderStore, InMemoryOrderStore>();
        }
    }

    public interface IOrderStore
    {
        Task AddAsync(PlacedOrder order, CancellationToken cancellationToken);
        Task<int> GetOrderCount(CancellationToken cancellationToken);
    }

    public sealed class InMemoryOrderStore : IOrderStore
    {
        private readonly List<PlacedOrder> _orders = [];

        public Task AddAsync(PlacedOrder order, CancellationToken cancellationToken)
        {
            _orders.Add(order);
            return Task.CompletedTask;
        }

        public Task<int> GetOrderCount(CancellationToken cancellationToken) => Task.FromResult(_orders.Count);
    }

    public static class Commands
    {
        public sealed record PlaceOrderCommand(string Sku, int Quantity, string CustomerId)
            : IRequest<PlaceOrderResult>, ICacheInvalidatingRequest
        {
            public string[] CacheKeysToInvalidate =>
            [
                "modular:catalog:all",
                "modular:billing:summary"
            ];
        }

        public sealed class PlaceOrderCommandHandler(
            Catalog.ICatalogInventory catalog,
            IOrderStore orderStore,
            IPublisher publisher,
            IMapper mapper)
            : IRequestHandler<PlaceOrderCommand, PlaceOrderResult>
        {
            public async Task<PlaceOrderResult> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
            {
                var catalogProduct = await catalog.GetBySku(request.Sku, cancellationToken)
                    ?? throw new InvalidOperationException($"SKU '{request.Sku}' does not exist.");

                if (request.Quantity <= 0)
                {
                    throw new InvalidOperationException("Quantity must be positive.");
                }

                if (catalogProduct.Stock < request.Quantity)
                {
                    throw new InvalidOperationException("Not enough stock.");
                }

                await catalog.Reserve(request.Sku, request.Quantity, cancellationToken);

                var order = new PlacedOrder(
                    Guid.NewGuid().ToString("N"),
                    request.CustomerId,
                    request.Sku,
                    request.Quantity,
                    catalogProduct.UnitPrice * request.Quantity);

                await orderStore.AddAsync(order, cancellationToken);

                await publisher.Publish(
                    new OrderPlacedNotification(
                        order.OrderId,
                        order.CustomerId,
                        order.Sku,
                        order.TotalPrice),
                    cancellationToken);

                return new PlaceOrderResult(
                    order.OrderId,
                    order.CustomerId,
                    mapper.Map<OrderSummaryVm>(order).Total);
            }
        }

        public sealed class PlaceOrderCommandValidator : IValidator<PlaceOrderCommand>
        {
            public Task<ValidationResult> ValidateAsync(PlaceOrderCommand instance, CancellationToken cancellationToken)
            {
                var result = new ValidationResult();

                if (string.IsNullOrWhiteSpace(instance.Sku))
                {
                    result.Errors.Add(new ValidationError(nameof(instance.Sku), "Sku is required."));
                }

                if (instance.Quantity <= 0)
                {
                    result.Errors.Add(new ValidationError(nameof(instance.Quantity), "Quantity must be greater than zero."));
                }

                if (string.IsNullOrWhiteSpace(instance.CustomerId))
                {
                    result.Errors.Add(new ValidationError(nameof(instance.CustomerId), "Customer is required."));
                }

                return Task.FromResult(result);
            }
        }
    }

    public sealed record PlaceOrderResult(string OrderId, string CustomerId, decimal Total);
    public sealed record OrderPlacedNotification(string OrderId, string CustomerId, string Sku, decimal Total) : INotification;
    public sealed record PlacedOrder(string OrderId, string CustomerId, string Sku, int Quantity, decimal TotalPrice);
    public sealed record OrderSummaryVm(string OrderId, string CustomerId, string Sku, int Quantity, decimal Total);
}

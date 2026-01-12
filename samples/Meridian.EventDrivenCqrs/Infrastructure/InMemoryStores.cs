namespace Meridian.EventDrivenCqrs;

public interface IOrderWriteStore
{
    Task<OrderAggregate> CreateAsync(string customerId, string productSku, int quantity, CancellationToken cancellationToken);
    Task<OrderAggregate?> AdvanceAsync(string orderId, string nextStatus, CancellationToken cancellationToken);
}

public interface IOrderReadStore
{
    Task UpsertFromEvent(OrderLifecycleEvent notification);
    Task<OrderProjection?> GetProjectionAsync(string orderId, CancellationToken cancellationToken);
    Task<OrderSummaryDto[]> GetAllSummariesAsync(CancellationToken cancellationToken);
    Task<string[]> GetTimelineAsync(string orderId, CancellationToken cancellationToken);
}

public sealed class InMemoryOrderWriteStore : IOrderWriteStore
{
    private readonly Dictionary<string, OrderAggregate> _orders = new();
    private readonly object _lock = new();

    public Task<OrderAggregate> CreateAsync(string customerId, string productSku, int quantity, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var aggregate = new OrderAggregate(
                Guid.NewGuid().ToString("N"),
                customerId,
                productSku,
                quantity,
                "New",
                "New",
                1);

            _orders[aggregate.OrderId] = aggregate;
            return Task.FromResult(aggregate);
        }
    }

    public Task<OrderAggregate?> AdvanceAsync(string orderId, string nextStatus, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return Task.FromResult<OrderAggregate?>(null);
            }

            if (!CanTransition(order.Status, nextStatus))
            {
                throw new InvalidOperationException($"Transition from {order.Status} to {nextStatus} is not allowed.");
            }

            _orders[orderId] = order with
            {
                PreviousStatus = order.Status,
                Status = nextStatus,
                Sequence = order.Sequence + 1,
            };

            return Task.FromResult<OrderAggregate?>(_orders[orderId]);
        }
    }

    private static bool CanTransition(string from, string to)
    {
        return from switch
        {
            "New" => to is "Packed" or "Cancelled",
            "Packed" => to is "Shipped" or "Cancelled",
            "Shipped" => to is "Delivered" or "Cancelled",
            _ => false,
        };
    }
}

public sealed class InMemoryOrderReadStore : IOrderReadStore
{
    private readonly Dictionary<string, OrderProjection> _projections = new();
    private readonly object _lock = new();

    public Task UpsertFromEvent(OrderLifecycleEvent notification)
    {
        lock (_lock)
        {
            if (!_projections.TryGetValue(notification.OrderId, out var current))
            {
                current = new OrderProjection(
                    notification.OrderId,
                    notification.CustomerId,
                    notification.ProductSku,
                    notification.Quantity,
                    notification.ToStatus,
                    []);

                _projections[notification.OrderId] = current;
            }

            current = current with
            {
                Status = notification.ToStatus,
                Events = current.Events.Append(
                    $"[{notification.Sequence}] {notification.FromStatus}->{notification.ToStatus}").ToArray()
            };

            _projections[notification.OrderId] = current;
            return Task.CompletedTask;
        }
    }

    public Task<OrderProjection?> GetProjectionAsync(string orderId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _projections.TryGetValue(orderId, out var projection);
            return Task.FromResult(projection);
        }
    }

    public Task<OrderSummaryDto[]> GetAllSummariesAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var summary = _projections.Values
                .Select(p => new OrderSummaryDto(p.OrderId, p.Status, p.Quantity))
                .ToArray();
            return Task.FromResult(summary);
        }
    }

    public Task<string[]> GetTimelineAsync(string orderId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_projections.TryGetValue(orderId, out var projection))
            {
                return Task.FromResult(projection.Events);
            }
        }

        return Task.FromResult(Array.Empty<string>());
    }
}

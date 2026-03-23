using System.Collections.Concurrent;

namespace Meridian.Hexagonal;

public sealed class InMemoryOrderRepositoryAdapter : IOrderRepositoryPort
{
    private readonly ConcurrentDictionary<Guid, Order> _store = new();

    public Task SaveAsync(Order order, CancellationToken cancellationToken)
    {
        _store[order.Id] = order;
        return Task.CompletedTask;
    }

    public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        _store.TryGetValue(orderId, out var order);
        return Task.FromResult(order);
    }
}

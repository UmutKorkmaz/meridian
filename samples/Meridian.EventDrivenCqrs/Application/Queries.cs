using System.Runtime.CompilerServices;
using Meridian.Mapping;
using Meridian.Mediator;

namespace Meridian.EventDrivenCqrs;

public static class Queries
{
    public sealed record GetOrderProjectionQuery(string OrderId) : IRequest<OrderProjectionDto>, ICacheableQuery
    {
        public string CacheKey => $"order:view:{OrderId}";
        public TimeSpan? CacheDuration => TimeSpan.FromMinutes(5);
    }

    public sealed class GetOrderProjectionQueryHandler(IMapper mapper, IOrderReadStore readStore)
        : IRequestHandler<GetOrderProjectionQuery, OrderProjectionDto>
    {
        public async Task<OrderProjectionDto> Handle(GetOrderProjectionQuery request, CancellationToken cancellationToken)
        {
            var projection = await readStore.GetProjectionAsync(request.OrderId, cancellationToken)
                ?? throw new InvalidOperationException($"Projection '{request.OrderId}' not found.");

            return mapper.Map<OrderProjectionDto>(projection);
        }
    }

    public sealed record ListOrdersSummaryQuery() : IRequest<OrderSummaryListDto>, ICacheableQuery
    {
        public string CacheKey => "orders:summary";
        public TimeSpan? CacheDuration => TimeSpan.FromMinutes(1);
    }

    public sealed class ListOrdersSummaryQueryHandler(IOrderReadStore readStore)
        : IRequestHandler<ListOrdersSummaryQuery, OrderSummaryListDto>
    {
        public async Task<OrderSummaryListDto> Handle(ListOrdersSummaryQuery request, CancellationToken cancellationToken)
        {
            var summaries = await readStore.GetAllSummariesAsync(cancellationToken);
            return new OrderSummaryListDto(
                summaries.Length,
                summaries.Sum(s => s.Quantity),
                summaries.Select(s => s.Status).ToArray());
        }
    }

    public sealed record GetOrderTimelineStream(string OrderId) : IStreamRequest<TimelineItemDto>;

    public sealed class GetOrderTimelineStreamHandler(IOrderReadStore readStore)
        : IStreamRequestHandler<GetOrderTimelineStream, TimelineItemDto>
    {
        public async IAsyncEnumerable<TimelineItemDto> Handle(
            GetOrderTimelineStream request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var entries = await readStore.GetTimelineAsync(request.OrderId, cancellationToken);
            foreach (var entry in entries)
            {
                yield return new TimelineItemDto(entry);
            }
        }
    }
}

public sealed record OrderStatusDto(string OrderId, string Status, int Sequence);
public sealed record OrderProjectionDto(string OrderId, string CustomerId, string Status, string ProductSku, int Quantity, int EventCount);
public sealed record OrderSummaryListDto(int TotalOrders, int TotalQuantity, string[] StatusBreakdown);
public sealed record TimelineItemDto(string Message);

public sealed record OrderLifecycleEvent(
    string OrderId,
    string CustomerId,
    string ProductSku,
    int Quantity,
    string FromStatus,
    string ToStatus,
    int Sequence) : INotification;

public sealed class ReadModelUpdater(IOrderReadStore readStore) : INotificationHandler<OrderLifecycleEvent>
{
    public async Task Handle(OrderLifecycleEvent notification, CancellationToken cancellationToken)
    {
        await readStore.UpsertFromEvent(notification);
    }
}

public sealed class EventLogger : INotificationHandler<OrderLifecycleEvent>
{
    public Task Handle(OrderLifecycleEvent notification, CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[event] order={notification.OrderId} {notification.FromStatus}->{notification.ToStatus} " +
            $"qty={notification.Quantity}");
        return Task.CompletedTask;
    }
}

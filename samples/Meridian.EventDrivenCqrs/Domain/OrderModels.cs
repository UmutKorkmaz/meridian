namespace Meridian.EventDrivenCqrs;

public sealed record OrderAggregate(
    string OrderId,
    string CustomerId,
    string ProductSku,
    int Quantity,
    string Status,
    string PreviousStatus,
    int Sequence);

public sealed record OrderProjection(
    string OrderId,
    string CustomerId,
    string ProductSku,
    int Quantity,
    string Status,
    string[] Events);

public sealed record OrderSummaryDto(string OrderId, string Status, int Quantity);

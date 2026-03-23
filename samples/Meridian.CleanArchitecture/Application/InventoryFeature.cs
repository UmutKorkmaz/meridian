using Meridian.Mediator;

namespace Meridian.CleanArchitecture;

public sealed record ReserveInventoryCommand(string Sku, int Quantity, Guid CustomerId)
    : IRequest<ReservationDto>, ICacheInvalidatingRequest, ITransactionalRequest
{
    public string[] CacheKeysToInvalidate => [$"catalog:all", $"product:{Sku.ToUpperInvariant()}"];
}

public sealed class ReserveInventoryCommandHandler : IRequestHandler<ReserveInventoryCommand, ReservationDto>
{
    private readonly IProductCatalog _catalog;
    private readonly IPublisher _publisher;

    public ReserveInventoryCommandHandler(IProductCatalog catalog, IPublisher publisher)
    {
        _catalog = catalog;
        _publisher = publisher;
    }

    public async Task<ReservationDto> Handle(ReserveInventoryCommand request, CancellationToken cancellationToken)
    {
        var product = await _catalog.GetBySkuAsync(request.Sku, cancellationToken)
            ?? throw new InvalidOperationException($"Product '{request.Sku}' not found.");

        var reservation = product.Reserve(request.Quantity);

        await _publisher.Publish(
            new InventoryReservedNotification(
                reservation.ReservationId,
                product.Sku,
                reservation.Quantity,
                request.CustomerId),
            cancellationToken);

        return new ReservationDto(reservation.ReservationId, product.Sku, reservation.Quantity, product.Inventory);
    }
}

public sealed class ReserveInventoryCommandValidator : IValidator<ReserveInventoryCommand>
{
    public Task<ValidationResult> ValidateAsync(ReserveInventoryCommand instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(instance.Sku))
        {
            result.Errors.Add(new ValidationError(nameof(instance.Sku), "SKU is required."));
        }

        if (instance.Quantity <= 0)
        {
            result.Errors.Add(new ValidationError(nameof(instance.Quantity), "Quantity must be greater than zero."));
        }

        return Task.FromResult(result);
    }
}

public sealed record InventoryReservedNotification(Guid ReservationId, string Sku, int Quantity, Guid CustomerId) : INotification;

public sealed class InventoryAuditHandler : INotificationHandler<InventoryReservedNotification>
{
    public static int AuditCount { get; private set; }

    public Task Handle(InventoryReservedNotification notification, CancellationToken cancellationToken)
    {
        AuditCount++;
        Console.WriteLine($"[audit] reservation {notification.ReservationId} for {notification.Quantity}x{notification.Sku} customer={notification.CustomerId}");
        return Task.CompletedTask;
    }
}

public sealed class InventoryReservedDomainEventHandler : INotificationHandler<InventoryReservedNotification>
{
    public Task Handle(InventoryReservedNotification notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[event] outbox: inventory reserved => {notification.Sku}");
        return Task.CompletedTask;
    }
}

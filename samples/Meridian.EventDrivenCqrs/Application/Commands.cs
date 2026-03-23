using Meridian.Mediator;

namespace Meridian.EventDrivenCqrs;

public static class Commands
{
    public sealed record OpenOrderCommand(string CustomerId, string ProductSku, int Quantity)
        : IRequest<OrderStatusDto>, ICacheInvalidatingRequest
    {
        public string[] CacheKeysToInvalidate => ["orders:summary"];
    }

    public sealed class OpenOrderCommandHandler(IOrderWriteStore writeStore, IPublisher publisher, IMapper mapper)
        : IRequestHandler<OpenOrderCommand, OrderStatusDto>
    {
        public async Task<OrderStatusDto> Handle(OpenOrderCommand request, CancellationToken cancellationToken)
        {
            var aggregate = await writeStore.CreateAsync(request.CustomerId, request.ProductSku, request.Quantity, cancellationToken);
            await publisher.Publish(
                new OrderLifecycleEvent(
                    aggregate.OrderId,
                    aggregate.CustomerId,
                    aggregate.ProductSku,
                    aggregate.Quantity,
                    "New",
                    aggregate.Status,
                    aggregate.Sequence),
                cancellationToken);

            return mapper.Map<OrderStatusDto>(aggregate);
        }
    }

    public sealed class OpenOrderCommandValidator : IValidator<OpenOrderCommand>
    {
        public Task<ValidationResult> ValidateAsync(OpenOrderCommand instance, CancellationToken cancellationToken)
        {
            var result = new ValidationResult();

            if (string.IsNullOrWhiteSpace(instance.CustomerId))
            {
                result.Errors.Add(new ValidationError(nameof(instance.CustomerId), "Customer is required."));
            }

            if (string.IsNullOrWhiteSpace(instance.ProductSku))
            {
                result.Errors.Add(new ValidationError(nameof(instance.ProductSku), "Product SKU is required."));
            }

            if (instance.Quantity <= 0)
            {
                result.Errors.Add(new ValidationError(nameof(instance.Quantity), "Quantity must be greater than zero."));
            }

            return Task.FromResult(result);
        }
    }

    public sealed record AdvanceOrderCommand(string OrderId, string NextStatus)
        : IRequest<OrderStatusDto>, ICacheInvalidatingRequest
    {
        public string[] CacheKeysToInvalidate => ["orders:summary", $"order:view:{OrderId}"];
    }

    public sealed class AdvanceOrderCommandHandler(
        IOrderWriteStore writeStore,
        IPublisher publisher,
        IMapper mapper)
        : IRequestHandler<AdvanceOrderCommand, OrderStatusDto>
    {
        public async Task<OrderStatusDto> Handle(AdvanceOrderCommand request, CancellationToken cancellationToken)
        {
            var aggregate = await writeStore.AdvanceAsync(request.OrderId, request.NextStatus, cancellationToken)
                ?? throw new InvalidOperationException($"Order '{request.OrderId}' was not found.");

            await publisher.Publish(
                new OrderLifecycleEvent(
                    aggregate.OrderId,
                    aggregate.CustomerId,
                    aggregate.ProductSku,
                    aggregate.Quantity,
                    aggregate.PreviousStatus,
                    aggregate.Status,
                    aggregate.Sequence),
                cancellationToken);

            return mapper.Map<OrderStatusDto>(aggregate);
        }
    }

    public sealed class AdvanceOrderCommandValidator : IValidator<AdvanceOrderCommand>
    {
        private static readonly string[] Allowed = ["Packed", "Shipped", "Delivered", "Cancelled"];

        public Task<ValidationResult> ValidateAsync(AdvanceOrderCommand instance, CancellationToken cancellationToken)
        {
            var result = new ValidationResult();

            if (string.IsNullOrWhiteSpace(instance.OrderId))
            {
                result.Errors.Add(new ValidationError(nameof(instance.OrderId), "Order id is required."));
            }

            if (!Allowed.Contains(instance.NextStatus, StringComparer.OrdinalIgnoreCase))
            {
                result.Errors.Add(new ValidationError(nameof(instance.NextStatus), "Status is not a supported transition."));
            }

            return Task.FromResult(result);
        }
    }
}

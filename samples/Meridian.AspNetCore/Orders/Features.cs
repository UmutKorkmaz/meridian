using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using FluentValidation;
using Meridian.AspNetCore.Infrastructure;
using Meridian.Mapping;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Streaming;
using Microsoft.EntityFrameworkCore;

namespace Meridian.AspNetCore.Orders;

public sealed record OrderDto(Guid Id, string Product, int Quantity, DateTimeOffset CreatedAt);
public sealed record OrderEventDto(long Sequence, Guid OrderId, string Product);
public sealed record CreateOrderBody(string Product, int Quantity);
public sealed record CreateOrder(string Product, int Quantity, string IdempotencyKey) : IRequest<OrderDto>, IAuthorizedRequest, IIdempotentRequest, ITransactionalRequest;
public sealed record ListOrders : IRequest<OrderDto[]>, IAuthorizedRequest;
public sealed record GetOrder(Guid Id) : IRequest<OrderDto?>, IAuthorizedRequest, ICacheableQuery
{
    public string CacheKey => Id.ToString("N");
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(5);
}
public sealed record WatchOrders(long After) : IStreamRequest<OrderEventDto>;
public sealed record OrderCreated(Order Order) : INotification;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderDto))]
[JsonSerializable(typeof(OrderDto[]))]
[JsonSerializable(typeof(OrderEventDto))]
public partial class OrdersJsonContext : JsonSerializerContext;

[GenerateMapper(typeof(Order), typeof(OrderDto), Mode = GeneratedMappingMode.Structural)]
[GenerateMapper(typeof(OrderEvent), typeof(OrderEventDto), Mode = GeneratedMappingMode.Structural)]
public static partial class OrderMaps;

public sealed class OrderProfile : Profile
{
    public OrderProfile() { CreateMap<Order, OrderDto>(); }
}

public sealed class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Product).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Quantity).InclusiveBetween(1, 1000);
        RuleFor(x => x.Product).MustAsync(async (product, token) => { await Task.Delay(1, token); return product != "unavailable"; }).WithMessage("Product is not available.");
    }
}
public sealed class OrderAuthorization<T>(AuthenticatedOrderScope scope) : IAuthorizationHandler<T> where T : IAuthorizedRequest
{
    public Task<AuthorizationResult> AuthorizeAsync(T request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); scope.Authorize(); return Task.FromResult(AuthorizationResult.Success()); }
}
public sealed class CreateOrderHandler(OrdersDbContext db, AuthenticatedOrderScope scope, IPublisher publisher) : IRequestHandler<CreateOrder, OrderDto>
{
    public async Task<OrderDto> Handle(CreateOrder request, CancellationToken cancellationToken)
    {
        var identity = scope.Identity;
        var order = new Order { Id = Guid.NewGuid(), Scope = scope.GetScope(), Issuer = identity.Issuer, Tenant = identity.Tenant, Subject = identity.Subject, Product = request.Product, Quantity = request.Quantity, CreatedAt = DateTimeOffset.UtcNow };
        db.Orders.Add(order);
        await publisher.Publish(new OrderCreated(order), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return OrderMaps.MapToOrderDto(order);
    }
}
public sealed class OrderCreatedHandler(OrdersDbContext db) : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.Events.Add(new OrderEvent { OrderId = notification.Order.Id, Scope = notification.Order.Scope, Product = notification.Order.Product });
        return Task.CompletedTask;
    }
}
public sealed class ListOrdersHandler(OrdersDbContext db, AuthenticatedOrderScope scope, IMapper mapper) : IRequestHandler<ListOrders, OrderDto[]>
{
    public async Task<OrderDto[]> Handle(ListOrders request, CancellationToken cancellationToken)
    {
        var partition = scope.GetScope();
        var orders = await db.Orders.AsNoTracking().Where(x => x.Scope == partition).OrderBy(x => x.Id).Take(100).ToArrayAsync(cancellationToken);
        return orders.Select(mapper.Map<Order, OrderDto>).ToArray();
    }
}
public sealed class GetOrderHandler(OrdersDbContext db, AuthenticatedOrderScope scope) : IRequestHandler<GetOrder, OrderDto?>
{
    public async Task<OrderDto?> Handle(GetOrder request, CancellationToken cancellationToken)
    {
        var partition = scope.GetScope();
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == partition && x.Id == request.Id, cancellationToken);
        return order is null ? null : OrderMaps.MapToOrderDto(order);
    }
}
public sealed class WatchOrdersHandler(IServiceScopeFactory scopes, AuthenticatedOrderScope scope) : IStreamRequestHandler<WatchOrders, OrderEventDto>
{
    public async IAsyncEnumerable<OrderEventDto> Handle(WatchOrders request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        scope.Authorize(); var partition = scope.GetScope(); var cursor = request.After;
        while (true)
        {
            OrderEvent[] events;
            await using (var child = scopes.CreateAsyncScope())
            {
                var db = child.ServiceProvider.GetRequiredService<OrdersDbContext>();
                events = await db.Events.AsNoTracking().Where(x => x.Scope == partition && x.Sequence > cursor).OrderBy(x => x.Sequence).Take(20).ToArrayAsync(cancellationToken);
            }
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); cursor = item.Sequence; yield return OrderMaps.MapToOrderEventDto(item); }
            await Task.Delay(100, cancellationToken);
        }
    }
}

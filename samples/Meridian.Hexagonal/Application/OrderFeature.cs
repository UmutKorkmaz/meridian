namespace Meridian.Hexagonal;

public sealed class HexagonalAssemblyMarker;

public sealed class HexagonalProfile : Profile
{
    public HexagonalProfile()
    {
        CreateMap<Product, CatalogItem>()
            .ForMember(dest => dest.PriceLabel, opt => opt.MapFrom(src => src.UnitPrice.Display))
            .ForMember(dest => dest.Price, opt => opt.MapFrom(src => src.UnitPrice.Amount))
            .ForMember(dest => dest.InStock, opt => opt.MapFrom(src => src.Stock > 0))
            .ForMember(dest => dest.Stock, opt => opt.MapFrom(src => src.Stock));

        CreateMap<Order, OrderConfirmation>()
            .ForMember(dest => dest.OrderId, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.Customer, opt => opt.MapFrom(src => src.CustomerName))
            .ForMember(dest => dest.UnitPriceLabel, opt => opt.MapFrom(src => src.Total / Math.Max(src.Quantity, 1)))
            .ForMember(dest => dest.TotalLabel, opt => opt.MapFrom(src => $"{src.Total:0.00} {src.Currency}"));

        CreateMap<Order, OrderDetails>()
            .ForMember(dest => dest.OrderId, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.Customer, opt => opt.MapFrom(src => src.CustomerName))
            .ForMember(dest => dest.Status, opt => opt.MapFrom(_ => "Accepted"))
            .ForMember(dest => dest.TotalLabel, opt => opt.MapFrom(src => $"{src.Total:0.00} {src.Currency}"))
            .ForMember(dest => dest.StockImpact, opt => opt.MapFrom(src => src.Quantity))
            .ForMember(dest => dest.PlacedAt, opt => opt.MapFrom(src => src.CreatedAt.ToString("s")));
    }
}

public sealed record CatalogItem(string Sku, string Name, decimal Price, string PriceLabel, int Stock, bool InStock);

public sealed record CatalogDto(IReadOnlyList<CatalogItem> Items);

public sealed record OrderConfirmation(Guid OrderId, string Customer, string ProductSku, int Quantity, string UnitPriceLabel, string TotalLabel);

public sealed record OrderDetails(Guid OrderId, string Customer, string ProductSku, int Quantity, string Status, string TotalLabel, string PlacedAt, int StockImpact);

public sealed record BrowseCatalogQuery() : IRequest<CatalogDto>, ICacheableQuery
{
    public string CacheKey => "hexagonal:catalog";
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(1);
}

public sealed class BrowseCatalogQueryHandler : IRequestHandler<BrowseCatalogQuery, CatalogDto>
{
    private readonly IProductCatalogPort _catalog;
    private readonly IMapper _mapper;

    public BrowseCatalogQueryHandler(IProductCatalogPort catalog, IMapper mapper)
    {
        _catalog = catalog;
        _mapper = mapper;
    }

    public async Task<CatalogDto> Handle(BrowseCatalogQuery request, CancellationToken cancellationToken)
    {
        var products = await _catalog.GetCatalogAsync(cancellationToken);
        var items = products.Select(_mapper.Map<CatalogItem>).ToList();
        return new CatalogDto(items);
    }
}

public sealed class BrowseCatalogQueryValidator : IValidator<BrowseCatalogQuery>
{
    public Task<ValidationResult> ValidateAsync(BrowseCatalogQuery instance, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ValidationResult());
    }
}

public sealed record PlaceOrderCommand(string CustomerName, string ProductSku, int Quantity)
    : IRequest<OrderConfirmation>, ICacheInvalidatingRequest, ITransactionalRequest
{
    public string[] CacheKeysToInvalidate => ["hexagonal:catalog"];
}

public sealed class PlaceOrderCommandHandler : IRequestHandler<PlaceOrderCommand, OrderConfirmation>
{
    private readonly IProductCatalogPort _catalog;
    private readonly IOrderRepositoryPort _orders;
    private readonly IMapper _mapper;
    private readonly IClockPort _clock;
    private readonly IPublisher _publisher;

    public PlaceOrderCommandHandler(
        IProductCatalogPort catalog,
        IOrderRepositoryPort orders,
        IMapper mapper,
        IClockPort clock,
        IPublisher publisher)
    {
        _catalog = catalog;
        _orders = orders;
        _mapper = mapper;
        _clock = clock;
        _publisher = publisher;
    }

    public async Task<OrderConfirmation> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        var product = await _catalog.GetProductAsync(request.ProductSku, cancellationToken)
            ?? throw new InvalidOperationException($"Product '{request.ProductSku}' not found.");

        var reserved = await _catalog.TryReserveStockAsync(request.ProductSku, request.Quantity, cancellationToken);
        if (!reserved)
        {
            throw new InvalidOperationException($"Cannot reserve {request.Quantity} unit(s) for SKU '{request.ProductSku}'.");
        }

        var total = request.Quantity * product.UnitPrice.Amount;
        var order = new Order(
            Guid.NewGuid(),
            request.CustomerName,
            request.ProductSku,
            request.Quantity,
            total,
            product.UnitPrice.Currency,
            _clock.UtcNow);

        await _orders.SaveAsync(order, cancellationToken);
        await _publisher.Publish(
            new OrderPlacedNotification(
                order.Id,
                order.CustomerName,
                order.ProductSku,
                order.Quantity,
                order.Total,
                order.Currency),
            cancellationToken);

        return _mapper.Map<OrderConfirmation>(order);
    }
}

public sealed class PlaceOrderValidator : IValidator<PlaceOrderCommand>
{
    public Task<ValidationResult> ValidateAsync(PlaceOrderCommand instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(instance.CustomerName))
        {
            result.Errors.Add(new ValidationError(nameof(instance.CustomerName), "CustomerName is required."));
        }

        if (string.IsNullOrWhiteSpace(instance.ProductSku))
        {
            result.Errors.Add(new ValidationError(nameof(instance.ProductSku), "ProductSku is required."));
        }

        if (instance.Quantity <= 0)
        {
            result.Errors.Add(new ValidationError(nameof(instance.Quantity), "Quantity must be greater than 0."));
        }

        return Task.FromResult(result);
    }
}

public sealed record GetOrderQuery(Guid OrderId) : IRequest<OrderDetails>, ICacheableQuery
{
    public string CacheKey => $"hexagonal:order:{OrderId:N}";
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed class GetOrderQueryHandler : IRequestHandler<GetOrderQuery, OrderDetails>
{
    private readonly IOrderRepositoryPort _orders;
    private readonly IMapper _mapper;

    public GetOrderQueryHandler(IOrderRepositoryPort orders, IMapper mapper)
    {
        _orders = orders;
        _mapper = mapper;
    }

    public async Task<OrderDetails> Handle(GetOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(request.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order '{request.OrderId}' not found.");
        return _mapper.Map<OrderDetails>(order);
    }
}

public sealed class GetOrderQueryValidator : IValidator<GetOrderQuery>
{
    public Task<ValidationResult> ValidateAsync(GetOrderQuery instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();

        if (instance.OrderId == Guid.Empty)
        {
            result.Errors.Add(new ValidationError(nameof(instance.OrderId), "OrderId is required."));
        }

        return Task.FromResult(result);
    }
}

public sealed record OrderPlacedNotification(Guid OrderId, string Customer, string ProductSku, int Quantity, decimal Total, string Currency) : INotification;

public sealed class OrderAuditNotifier : INotificationHandler<OrderPlacedNotification>
{
    private readonly IOrderEventSinkPort _sink;

    public OrderAuditNotifier(IOrderEventSinkPort sink)
    {
        _sink = sink;
    }

    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        _sink.Publish($"order={notification.OrderId:N} customer={notification.Customer} sku={notification.ProductSku} qty={notification.Quantity} total={notification.Total:0.00} {notification.Currency}");
        return Task.CompletedTask;
    }
}

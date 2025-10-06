using Meridian.Mediator;

namespace Meridian.CleanArchitecture;

public sealed class CatalogProfile : Profile
{
    public CatalogProfile()
    {
        CreateMap<Product, CatalogItemDto>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id.ToString("N")))
            .ForMember(dest => dest.PriceLabel, opt => opt.MapFrom(src => src.UnitPrice.Display))
            .ForMember(dest => dest.InStock, opt => opt.MapFrom(src => src.Inventory > 0))
            .ForPath(dest => dest.Supplier.Name, opt => opt.MapFrom(src => src.Supplier.Name))
            .ForPath(dest => dest.Supplier.City, opt => opt.MapFrom(src => src.Supplier.Address.City));

        CreateMap<Product, ProductDetailsDto>()
            .ForMember(dest => dest.FormattedPrice, opt => opt.MapFrom(src => src.UnitPrice.Display))
            .ForMember(dest => dest.PriceLabel, opt => opt.MapFrom(src => src.UnitPrice.Display))
            .ForMember(dest => dest.StockRemaining, opt => opt.MapFrom(src => src.Inventory));

        CreateMap<Supplier, SupplierDto>();
    }
}

public sealed class CleanArchitectureAssemblyMarker;

public sealed class BrowseCatalogQuery : IRequest<CatalogDto>, ICacheableQuery
{
    public string CacheKey => "catalog:all";
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(2);
}

public sealed class BrowseCatalogQueryHandler : IRequestHandler<BrowseCatalogQuery, CatalogDto>
{
    private readonly IProductCatalog _catalog;
    private readonly IMapper _mapper;

    public BrowseCatalogQueryHandler(IProductCatalog catalog, IMapper mapper)
    {
        _catalog = catalog;
        _mapper = mapper;
    }

    public async Task<CatalogDto> Handle(BrowseCatalogQuery request, CancellationToken cancellationToken)
    {
        var all = await _catalog.GetAllAsync(cancellationToken);
        var dtoItems = all.Select(product => _mapper.Map<CatalogItemDto>(product)).ToList();
        return new CatalogDto(dtoItems);
    }
}

public sealed class BrowseCatalogQueryValidator : IValidator<BrowseCatalogQuery>
{
    public Task<ValidationResult> ValidateAsync(BrowseCatalogQuery instance, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ValidationResult());
    }
}

public sealed record GetProductBySkuQuery(string Sku) : IRequest<ProductDetailsDto>, ICacheableQuery
{
    public string CacheKey => $"product:{Sku.ToUpperInvariant()}";
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(1);
}

public sealed class GetProductBySkuQueryHandler : IRequestHandler<GetProductBySkuQuery, ProductDetailsDto>
{
    private readonly IProductCatalog _catalog;
    private readonly IMapper _mapper;

    public GetProductBySkuQueryHandler(IProductCatalog catalog, IMapper mapper)
    {
        _catalog = catalog;
        _mapper = mapper;
    }

    public async Task<ProductDetailsDto> Handle(GetProductBySkuQuery request, CancellationToken cancellationToken)
    {
        var product = await _catalog.GetBySkuAsync(request.Sku, cancellationToken)
            ?? throw new InvalidOperationException($"Product '{request.Sku}' not found.");

        return _mapper.Map<ProductDetailsDto>(product);
    }
}

public sealed class GetProductBySkuQueryValidator : IValidator<GetProductBySkuQuery>
{
    public Task<ValidationResult> ValidateAsync(GetProductBySkuQuery instance, CancellationToken cancellationToken)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(instance.Sku))
        {
            result.Errors.Add(new ValidationError(nameof(instance.Sku), "SKU is required."));
        }

        return Task.FromResult(result);
    }
}


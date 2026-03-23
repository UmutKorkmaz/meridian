using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.ModularMonolith;

public static class Catalog
{
    public sealed class CatalogModule : IModuleStartup
    {
        public string Name => "Catalog";

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ICatalogInventory, InMemoryCatalogInventory>();
        }
    }

    public interface ICatalogInventory
    {
        Task<CatalogProduct[]> GetAllProducts(CancellationToken cancellationToken);
        Task<CatalogProduct?> GetBySku(string sku, CancellationToken cancellationToken);
        Task Reserve(string sku, int quantity, CancellationToken cancellationToken);
    }

    public static class Queries
    {
        public sealed record GetCatalogOverviewQuery() : IRequest<CatalogOverviewDto>, ICacheableQuery
        {
            public string CacheKey => "modular:catalog:all";
            public TimeSpan? CacheDuration => TimeSpan.FromMinutes(2);
        }

        public sealed class GetCatalogOverviewQueryHandler(IMapper mapper, ICatalogInventory catalog)
            : IRequestHandler<GetCatalogOverviewQuery, CatalogOverviewDto>
        {
            public async Task<CatalogOverviewDto> Handle(GetCatalogOverviewQuery request, CancellationToken cancellationToken)
            {
                var products = await catalog.GetAllProducts(cancellationToken);
                return new CatalogOverviewDto(products.Select(mapper.Map<CatalogItemVm>).ToList());
            }
        }

        public sealed class GetCatalogOverviewQueryValidator : IValidator<GetCatalogOverviewQuery>
        {
            public Task<ValidationResult> ValidateAsync(GetCatalogOverviewQuery instance, CancellationToken cancellationToken)
                => Task.FromResult(new ValidationResult());
        }
    }

    public sealed class InMemoryCatalogInventory : ICatalogInventory
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, CatalogProduct> _products = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GADGET-100"] = new CatalogProduct("GADGET-100", "Meridian Gauge", 25.00m, 5),
            ["SENSOR-400"] = new CatalogProduct("SENSOR-400", "Motion Sensor", 80.00m, 1),
        };

        public Task<CatalogProduct[]> GetAllProducts(CancellationToken cancellationToken)
            => Task.FromResult(_products.Values.ToArray());

        public Task<CatalogProduct?> GetBySku(string sku, CancellationToken cancellationToken)
        {
            _products.TryGetValue(sku, out var product);
            return Task.FromResult(product);
        }

        public Task Reserve(string sku, int quantity, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var product = _products[sku];

                if (product.Stock < quantity)
                {
                    throw new InvalidOperationException("Not enough stock.");
                }

                _products[sku] = product with { Stock = product.Stock - quantity };
            }

            return Task.CompletedTask;
        }
    }

    public sealed record CatalogProduct(string Sku, string Name, decimal UnitPrice, int Stock);
    public sealed record CatalogItemVm(string Sku, string Name, decimal UnitPrice, int Stock);
    public sealed record CatalogOverviewDto(List<CatalogItemVm> Items);
}

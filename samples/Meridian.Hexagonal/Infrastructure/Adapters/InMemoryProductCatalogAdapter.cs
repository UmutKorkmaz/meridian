namespace Meridian.Hexagonal;

public sealed class InMemoryProductCatalogAdapter : IProductCatalogPort
{
    private readonly Dictionary<string, Product> _products = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HEX-100"] = new Product("HEX-100", "Meridian Hex Sensor", new Money(199.99m, "USD"), 9),
        ["HEX-200"] = new Product("HEX-200", "Meridian Hex Controller", new Money(349.50m, "USD"), 3),
    };

    public Task<IReadOnlyList<Product>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult((IReadOnlyList<Product>)_products.Values.ToList());
    }

    public Task<Product?> GetProductAsync(string sku, CancellationToken cancellationToken)
    {
        _products.TryGetValue(sku, out var product);
        return Task.FromResult(product);
    }

    public Task<bool> TryReserveStockAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        if (_products.TryGetValue(sku, out var product))
        {
            return Task.FromResult(product.TryReserve(quantity));
        }

        return Task.FromResult(false);
    }
}

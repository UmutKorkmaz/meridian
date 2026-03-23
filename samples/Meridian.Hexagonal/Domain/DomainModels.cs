namespace Meridian.Hexagonal;

public interface IProductCatalogPort
{
    Task<IReadOnlyList<Product>> GetCatalogAsync(CancellationToken cancellationToken);

    Task<Product?> GetProductAsync(string sku, CancellationToken cancellationToken);

    Task<bool> TryReserveStockAsync(string sku, int quantity, CancellationToken cancellationToken);
}

public interface IOrderRepositoryPort
{
    Task SaveAsync(Order order, CancellationToken cancellationToken);

    Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken);
}

public interface IOrderEventSinkPort
{
    void Publish(string message);
}

public interface IClockPort
{
    DateTimeOffset UtcNow { get; }
}

public sealed record Money(decimal Amount, string Currency)
{
    public string Display => $"{Amount:0.00} {Currency}";
}

public sealed class Product
{
    private readonly object _sync = new();
    private int _stock;

    public Product(string sku, string name, Money unitPrice, int stock)
    {
        Sku = sku;
        Name = name;
        UnitPrice = unitPrice;
        _stock = stock;
    }

    public string Sku { get; }

    public string Name { get; }

    public Money UnitPrice { get; }

    public int Stock
    {
        get
        {
            lock (_sync)
            {
                return _stock;
            }
        }
    }

    public bool TryReserve(int quantity)
    {
        if (quantity <= 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (_stock < quantity)
            {
                return false;
            }

            _stock -= quantity;
            return true;
        }
    }
}

public sealed class Order
{
    public Order(Guid id, string customerName, string sku, int quantity, decimal total, string currency, DateTimeOffset createdAt)
    {
        Id = id;
        CustomerName = customerName;
        ProductSku = sku;
        Quantity = quantity;
        Total = total;
        Currency = currency;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string CustomerName { get; }

    public string ProductSku { get; }

    public int Quantity { get; }

    public decimal Total { get; }

    public string Currency { get; }

    public DateTimeOffset CreatedAt { get; }
}

namespace Meridian.CleanArchitecture;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IProductCatalog
{
    Task<Product[]> GetAllAsync(CancellationToken cancellationToken);
    Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken);
}

public sealed class InMemoryProductCatalog : IProductCatalog
{
    private readonly Dictionary<string, Product> _products = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GADGET-100"] = new Product(
            Guid.NewGuid(),
            "GADGET-100",
            "Meridian Smart Gauge",
            new Money(149.99m, "USD"),
            24,
            new Supplier("Meridian Labs", "London")),
        ["PART-404"] = new Product(
            Guid.NewGuid(),
            "PART-404",
            "Beta Adapter",
            new Money(39.95m, "USD"),
            0,
            new Supplier("Meridian Labs", "London")),
    };

    public Task<Product[]> GetAllAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(_products.Values.ToArray());
    }

    public Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken)
    {
        _products.TryGetValue(sku, out var product);
        return Task.FromResult(product);
    }
}

public sealed class Product
{
    private readonly object _gate = new();

    public Product(Guid id, string sku, string name, Money unitPrice, int inventory, Supplier supplier)
    {
        Id = id;
        Sku = sku;
        Name = name;
        UnitPrice = unitPrice;
        Inventory = inventory;
        Supplier = supplier;
    }

    public Guid Id { get; }
    public string Sku { get; }
    public string Name { get; }
    public Money UnitPrice { get; }
    public int Inventory { get; private set; }
    public Supplier Supplier { get; }

    public Reservation Reserve(int quantity)
    {
        if (quantity <= 0)
        {
            throw new InvalidOperationException("Reservation quantity must be positive.");
        }

        lock (_gate)
        {
            if (Inventory < quantity)
            {
                throw new InvalidOperationException("Insufficient inventory.");
            }

            Inventory -= quantity;
            return new Reservation(Guid.NewGuid(), quantity);
        }
    }
}

public sealed record Reservation(Guid ReservationId, int Quantity);

public sealed class Money
{
    public Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }
    public string Currency { get; }
    public string Display => $"{Amount:0.00} {Currency}";
}

public sealed class Supplier
{
    public Supplier(string name, string city)
    {
        Name = name;
        Address = new Address(city);
    }

    public string Name { get; }
    public Address Address { get; }
}

public sealed class Address
{
    public Address(string city)
    {
        City = city;
    }

    public string City { get; }
}

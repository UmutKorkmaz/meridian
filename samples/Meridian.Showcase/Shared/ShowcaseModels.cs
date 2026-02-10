namespace Meridian.Showcase;

public sealed class OrderSource
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public decimal Subtotal { get; init; }
    public ShippingSource Shipping { get; init; } = new();
    public string[]? Tags { get; init; }
}

public sealed class ShippingSource
{
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
}

public sealed class OrderView
{
    public string CustomerName { get; set; } = string.Empty;
    public decimal FinalTotal { get; set; }
    public ShippingView Shipping { get; set; } = new();
    public string[]? Tags { get; set; }
    public string IgnoredByDefault { get; set; } = "left-alone-by-ForAllOtherMembers";
}

public sealed class ShippingView
{
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public sealed class CustomerEnvelope
{
    public CustomerProfile Profile { get; init; } = new();
}

public sealed class CustomerProfile
{
    public string DisplayName { get; init; } = string.Empty;
    public string Tier { get; init; } = string.Empty;
}

public sealed class CustomerCard
{
    public string DisplayName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
}

public sealed class ProductEntity
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class ProductRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class CatalogItemEntity
{
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
}

public sealed class CatalogItemDto
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public sealed class OrderReceipt
{
    public OrderReceipt(string orderId, string message)
    {
        OrderId = orderId;
        Message = message;
    }

    public string OrderId { get; }
    public string Message { get; }
}

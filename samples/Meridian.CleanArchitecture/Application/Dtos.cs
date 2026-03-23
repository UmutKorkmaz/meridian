namespace Meridian.CleanArchitecture;

public sealed record CatalogDto(List<CatalogItemDto> Items);

public sealed class CatalogItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool InStock { get; set; }
    public string PriceLabel { get; set; } = string.Empty;
    public SupplierDto Supplier { get; set; } = new();
}

public sealed class SupplierDto
{
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public sealed record ProductDetailsDto
{
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FormattedPrice { get; init; } = string.Empty;
    public string PriceLabel { get; init; } = string.Empty;
    public int StockRemaining { get; init; }
}

public sealed record ReservationDto(Guid Reference, string Sku, int Quantity, int StockRemaining);

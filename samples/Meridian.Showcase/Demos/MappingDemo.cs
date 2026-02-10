using Meridian.Mapping;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Showcase;

public static class MappingDemo
{
    public static async Task Run()
    {
        ShowcaseOutput.WriteHeader("Mapping");

        var services = new ServiceCollection();
        services.AddShowcaseMappings();

        using var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapper>();

        var order = new OrderSource
        {
            FirstName = "  Ada  ",
            LastName = "  Lovelace  ",
            Subtotal = 125.50m,
            Shipping = new ShippingSource
            {
                Country = "  UK  ",
                City = "  London  "
            },
            Tags = ["  vip  "]
        };

        var mappedOrder = mapper.Map<OrderView>(order);
        Console.WriteLine(
            $"OrderView => {mappedOrder.CustomerName} | FinalTotal: {mappedOrder.FinalTotal} | Ship: {mappedOrder.Shipping.City}, {mappedOrder.Shipping.Country} | Preserved: {mappedOrder.IgnoredByDefault}");

        var customerCard = mapper.Map<CustomerCard>(new CustomerEnvelope
        {
            Profile = new CustomerProfile
            {
                DisplayName = "  Meridian Team  ",
                Tier = "  Gold  "
            }
        });
        Console.WriteLine($"IncludeMembers => {customerCard.DisplayName} ({customerCard.Tier})");

        var reversed = mapper.Map<ProductEntity>(new ProductDto
        {
            Id = 7,
            Name = "  Meridian Widget  "
        });
        Console.WriteLine($"ReverseMap => {reversed.Id}:{reversed.Name}");

        var projected = mapper.ProjectTo<ProductRow>(
                new[]
                {
                    new ProductEntity { Id = 1, Name = "  Meridian Widget  " },
                    new ProductEntity { Id = 2, Name = "  Meridian Gizmo  " }
                }.AsQueryable())
            .ToList();
        Console.WriteLine($"ProjectTo => {string.Join(", ", projected.Select(row => $"{row.Id}:{row.Name}"))}");
        Console.WriteLine();

        await Task.CompletedTask;
    }
}

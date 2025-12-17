namespace Meridian.Mapping.Tests;

public class CollectionMappingTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Map_List()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<OrderItemSource, OrderItemDest>());

        var source = new List<OrderItemSource>
        {
            new() { ProductName = "Widget", Price = 9.99m, Quantity = 2 },
            new() { ProductName = "Gadget", Price = 19.99m, Quantity = 1 }
        };
        var result = mapper.Map<List<OrderItemSource>, List<OrderItemDest>>(source);

        Assert.Equal(2, result.Count);
        Assert.Equal("Widget", result[0].ProductName);
        Assert.Equal(9.99m, result[0].Price);
        Assert.Equal("Gadget", result[1].ProductName);
    }

    [Fact]
    public void Should_Map_IEnumerable()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        IEnumerable<Source> source = new List<Source>
        {
            new() { Id = 10, Name = "First" },
            new() { Id = 20, Name = "Second" }
        };
        var result = mapper.Map<IEnumerable<Source>, List<Destination>>(source);

        Assert.Equal(2, result.Count);
        Assert.Equal(10, result[0].Id);
        Assert.Equal("Second", result[1].Name);
    }

    [Fact]
    public void Should_Map_Array()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var source = new Source[] { new() { Id = 1 }, new() { Id = 2 } };
        var result = mapper.Map<Source[], Destination[]>(source);

        Assert.Equal(2, result.Length);
        Assert.Equal(1, result[0].Id);
        Assert.Equal(2, result[1].Id);
    }

    [Fact]
    public void Should_Map_ICollection()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        ICollection<Source> source = new List<Source>
        {
            new() { Id = 5, Name = "CollItem" }
        };
        var result = mapper.Map<ICollection<Source>, ICollection<Destination>>(source);

        Assert.Single(result);
        Assert.Equal(5, result.First().Id);
    }

    [Fact]
    public void Should_Map_Nested_Collection()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<OrderSource, OrderDest>();
            cfg.CreateMap<OrderItemSource, OrderItemDest>();
        });

        var source = new OrderSource
        {
            OrderId = 100,
            Items = new List<OrderItemSource>
            {
                new() { ProductName = "A", Price = 5m, Quantity = 1 },
                new() { ProductName = "B", Price = 10m, Quantity = 3 }
            }
        };
        var result = mapper.Map<OrderSource, OrderDest>(source);

        Assert.Equal(100, result.OrderId);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("A", result.Items[0].ProductName);
        Assert.Equal(3, result.Items[1].Quantity);
    }

    [Fact]
    public void Should_Map_Empty_Collection_To_Empty()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var source = new List<Source>();
        var result = mapper.Map<List<Source>, List<Destination>>(source);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Should_Map_Null_Collection_To_Null_For_Generic_Overload()
    {
        // Map<TSrc,TDest>(null!) returns default which is null for reference types
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var result = mapper.Map<List<Source>, List<Destination>>(null!);

        Assert.Null(result);
    }
}

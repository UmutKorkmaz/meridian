namespace Meridian.Mapping.Tests;

public class CollectionMappingTests
{
    private static IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
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

    [Fact]
    public void Duplicate_Dictionary_Keys_Use_Last_Write_Wins()
    {
        var mapper = CreateMapper(_ => { });

        var source = new List<KeyValuePair<string, int>>
        {
            new("dup", 1),
            new("dup", 2),
            new("other", 3)
        };

        var result = mapper.Map<List<KeyValuePair<string, int>>, Dictionary<string, int>>(source);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result["dup"]);
        Assert.Equal(3, result["other"]);
    }
    [Fact]
    public void SetInterfaceMappingRetainsDistinctValues()
    {
        var mapper = CreateMapper(_ => { });
        var mapped = mapper.Map<int[], ISet<int>>([1, 1, 2]);
        Assert.Equal(2, mapped.Count);
        Assert.Contains(1, mapped);
        Assert.Contains(2, mapped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullCollectionMemberRetainsConfiguredPolicy(bool allowNull)
    {
        var mapper = CreateMapper(c =>
        {
            c.AllowNullCollections = allowNull;
            c.CreateMap<NullCollectionSource, NullCollectionDto>();
        });
        var mapped = mapper.Map<NullCollectionSource, NullCollectionDto>(new());
        if (allowNull) Assert.Null(mapped.Values);
        else Assert.Empty(Assert.IsType<List<int>>(mapped.Values));
    }
    public sealed class NullCollectionSource { public List<int>? Values { get; set; } }
    public sealed class NullCollectionDto { public List<int>? Values { get; set; } }

    // Top-level collection maps build a new collection, as in 2.1.3, even when the
    // source collection is assignable to the destination type. Without a self-map
    // the elements are copied by reference; with CreateMap<T, T>() they are cloned.

    [Fact]
    public void Top_Level_Same_Type_List_Returns_New_List_Without_Self_Map()
    {
        var mapper = CreateMapper(_ => { });
        var source = new List<Source> { new() { Id = 1, Name = "A" }, new() { Id = 2, Name = "B" } };

        var result = mapper.Map<List<Source>, List<Source>>(source);

        Assert.NotSame(source, result);
        Assert.Equal(2, result.Count);
        Assert.Same(source[0], result[0]);
        Assert.Same(source[1], result[1]);

        result.Add(new Source { Id = 3 });
        Assert.Equal(2, source.Count);
    }

    [Fact]
    public void Top_Level_Same_Type_List_With_Self_Map_Deep_Clones_Elements()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Source>());
        var source = new List<Source> { new() { Id = 1, Name = "A" }, new() { Id = 2, Name = "B" } };

        var result = mapper.Map<List<Source>, List<Source>>(source);

        Assert.NotSame(source, result);
        Assert.Equal(2, result.Count);
        Assert.NotSame(source[0], result[0]);
        Assert.NotSame(source[1], result[1]);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("B", result[1].Name);

        result[0].Name = "changed";
        Assert.Equal("A", source[0].Name);
    }

    [Fact]
    public void Top_Level_Enumerable_To_Assignable_List_Returns_New_List()
    {
        var mapper = CreateMapper(_ => { });
        var list = new List<Source> { new() { Id = 1 } };
        IEnumerable<Source> source = list;

        var result = mapper.Map<IEnumerable<Source>, List<Source>>(source);

        Assert.NotSame(list, result);
        Assert.Same(list[0], Assert.Single(result));
    }

    [Fact]
    public void Top_Level_Same_Type_Collection_Returns_New_Collection_For_Runtime_Typed_Overloads()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Source>());
        var source = new List<Source> { new() { Id = 1 } };

        // The runtime-typed overload is the subject here, so the types are passed as values.
        var listType = source.GetType();
        var viaObject = mapper.Map<List<Source>>(source);
        var viaTypes = (List<Source>)mapper.Map(source, listType, listType);
        var array = new[] { new Source { Id = 2 } };
        var viaArray = mapper.Map<Source[], Source[]>(array);

        Assert.NotSame(source, viaObject);
        Assert.NotSame(source[0], Assert.Single(viaObject));
        Assert.NotSame(source, viaTypes);
        Assert.NotSame(source[0], Assert.Single(viaTypes));
        Assert.NotSame(array, viaArray);
        Assert.NotSame(array[0], Assert.Single(viaArray));
        Assert.Equal(2, viaArray[0].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Top_Level_Same_Type_List_Enforces_Collection_Limit(bool withSelfMap)
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.DefaultMaxCollectionItems = 2;
            if (withSelfMap)
                cfg.CreateMap<Source, Source>();
        });
        var source = Enumerable.Range(0, 5).Select(i => new Source { Id = i }).ToList();

        var ex = Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<List<Source>, List<Source>>(source));

        Assert.Equal(MeridianMappingLimit.MaxCollectionItems, ex.Limit);
        Assert.Equal(2, ex.MaxAllowed);
        Assert.Equal(5, ex.ObservedValue);
    }

    [Fact]
    public void Top_Level_Assignable_Enumerable_Enforces_Collection_Limit_During_Enumeration()
    {
        var mapper = CreateMapper(cfg => cfg.DefaultMaxCollectionItems = 2);

        static IEnumerable<Source> Stream()
        {
            for (var i = 0; i < 5; i++)
                yield return new Source { Id = i };
        }

        var ex = Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<IEnumerable<Source>, IEnumerable<Source>>(Stream()));

        Assert.Equal(MeridianMappingLimit.MaxCollectionItems, ex.Limit);
        Assert.Equal(2, ex.MaxAllowed);
    }

    [Fact]
    public void Top_Level_Same_Type_Collection_That_Cannot_Be_Constructed_Still_Maps()
    {
        // MapCollection can only build arrays, sets, dictionaries, List<T>-assignable
        // types and types with an IEnumerable<T> constructor. Other assignable
        // collections keep the assignable result instead of an InvalidCastException.
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Source>());
        var immutableArray = System.Collections.Immutable.ImmutableArray.Create(1, 2);
        var immutableList = System.Collections.Immutable.ImmutableList.Create(new Source { Id = 1 });
        var readOnly = new System.Collections.ObjectModel.ReadOnlyCollection<Source>([new Source { Id = 2 }]);

        var mappedArray = mapper.Map<System.Collections.Immutable.ImmutableArray<int>,
            System.Collections.Immutable.ImmutableArray<int>>(immutableArray);
        Assert.Collection(mappedArray, v => Assert.Equal(1, v), v => Assert.Equal(2, v));
        Assert.Same(immutableList, mapper.Map<System.Collections.Immutable.ImmutableList<Source>,
            System.Collections.Immutable.ImmutableList<Source>>(immutableList));
        Assert.Same(readOnly, mapper.Map<System.Collections.ObjectModel.ReadOnlyCollection<Source>,
            System.Collections.ObjectModel.ReadOnlyCollection<Source>>(readOnly));
    }

    [Fact]
    public void Explicit_Same_Type_Collection_Map_Still_Takes_Precedence()
    {
        var replacement = new List<Source> { new() { Id = 99 } };
        var mapper = CreateMapper(cfg =>
            cfg.CreateMap<List<Source>, List<Source>>().ConvertUsing(_ => replacement));

        var result = mapper.Map<List<Source>, List<Source>>([new Source { Id = 1 }]);

        Assert.Same(replacement, result);
    }
}

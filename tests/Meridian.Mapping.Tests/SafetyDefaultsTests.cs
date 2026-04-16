using Meridian.Mapping;

namespace Meridian.Mapping.Tests;

/// <summary>
/// Regression tests for the safe-by-default limits added in Phase 1 of the
/// Meridian upgrade plan: DefaultMaxDepth and DefaultMaxCollectionItems.
///
/// These tests guarantee that Meridian does NOT inherit the AutoMapper
/// CVE-2026-32933 class (DoS via uncontrolled recursion / unbounded allocation)
/// regardless of whether the caller has configured per-map caps.
/// </summary>
public class SafetyDefaultsTests
{
    // ── Depth defaults ────────────────────────────────────────────────────

    public class DepthNode
    {
        public int Value { get; set; }
        public DepthNode? Child { get; set; }
    }

    public class DepthNodeDto
    {
        public int Value { get; set; }
        public DepthNodeDto? Child { get; set; }
    }

    private static DepthNode BuildDepthChain(int depth)
    {
        var head = new DepthNode { Value = 0 };
        var current = head;
        for (var i = 1; i < depth; i++)
        {
            current.Child = new DepthNode { Value = i };
            current = current.Child;
        }
        return head;
    }

    private static int CountDepth(DepthNodeDto? node)
    {
        var depth = 0;
        while (node is not null)
        {
            depth++;
            node = node.Child;
        }
        return depth;
    }

    [Fact]
    public void DefaultMaxDepth_Is_64()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<DepthNode, DepthNodeDto>());
        Assert.Equal(64, ((IConfigurationProvider)cfg).DefaultMaxDepth);
    }

    [Fact]
    public void Deep_Graph_Without_Explicit_MaxDepth_Does_Not_StackOverflow()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<DepthNode, DepthNodeDto>());
        var mapper = cfg.CreateMapper();

        // 10k-deep chain would StackOverflow on an unprotected mapper.
        // With MaxDepth=64 default, mapping truncates at 64 levels.
        var source = BuildDepthChain(10_000);

        var dto = mapper.Map<DepthNode, DepthNodeDto>(source);

        Assert.NotNull(dto);
        Assert.True(CountDepth(dto) <= 64, "Mapped depth must not exceed DefaultMaxDepth.");
    }

    [Fact]
    public void DefaultMaxDepth_Is_Overridable_Globally()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.DefaultMaxDepth = 8;
            c.CreateMap<DepthNode, DepthNodeDto>();
        });
        var mapper = cfg.CreateMapper();

        var source = BuildDepthChain(100);
        var dto = mapper.Map<DepthNode, DepthNodeDto>(source);

        Assert.NotNull(dto);
        Assert.True(CountDepth(dto) <= 8);
    }

    [Fact]
    public void Explicit_PerMap_MaxDepth_Wins_Over_Global_Default()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.DefaultMaxDepth = 64;
            c.CreateMap<DepthNode, DepthNodeDto>().MaxDepth(3);
        });
        var mapper = cfg.CreateMapper();

        var source = BuildDepthChain(100);
        var dto = mapper.Map<DepthNode, DepthNodeDto>(source);

        Assert.NotNull(dto);
        Assert.True(CountDepth(dto) <= 3);
    }

    [Fact]
    public void DefaultMaxDepth_Must_Be_Positive()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MapperConfiguration(c => c.DefaultMaxDepth = 0));
        Assert.Contains("DefaultMaxDepth", ex.Message);
    }

    // ── Collection width defaults ────────────────────────────────────────

    public class Item { public int Value { get; set; } }
    public class ItemDto { public int Value { get; set; } }

    [Fact]
    public void DefaultMaxCollectionItems_Is_10_000()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        Assert.Equal(10_000, ((IConfigurationProvider)cfg).DefaultMaxCollectionItems);
    }

    [Fact]
    public void Oversized_List_Throws_MeridianMappingLimitException_Via_FastPath()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        // ICollection-backed — hits fast-path check before any enumeration.
        var source = Enumerable.Range(0, 10_001).Select(i => new Item { Value = i }).ToList();

        var ex = Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<List<Item>, List<ItemDto>>(source));

        Assert.Equal(MeridianMappingLimit.MaxCollectionItems, ex.Limit);
        Assert.Equal(10_000, ex.MaxAllowed);
        Assert.Equal(10_001, ex.ObservedValue);
    }

    [Fact]
    public void Oversized_Enumerable_Throws_During_Enumeration()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        // Pure IEnumerable — no Count, must be caught mid-enumeration.
        IEnumerable<Item> Stream()
        {
            for (var i = 0; i < 20_000; i++)
                yield return new Item { Value = i };
        }

        var ex = Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<IEnumerable<Item>, List<ItemDto>>(Stream()));

        Assert.Equal(MeridianMappingLimit.MaxCollectionItems, ex.Limit);
        Assert.True(ex.ObservedValue > 10_000);
    }

    [Fact]
    public void DefaultMaxCollectionItems_Is_Overridable_Globally()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.DefaultMaxCollectionItems = 5;
            c.CreateMap<Item, ItemDto>();
        });
        var mapper = cfg.CreateMapper();

        var source = Enumerable.Range(0, 6).Select(i => new Item { Value = i }).ToList();

        Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<List<Item>, List<ItemDto>>(source));
    }

    [Fact]
    public void Collection_Below_Limit_Maps_Normally()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        var source = Enumerable.Range(0, 9_999).Select(i => new Item { Value = i }).ToList();

        var result = mapper.Map<List<Item>, List<ItemDto>>(source);

        Assert.NotNull(result);
        Assert.Equal(9_999, result.Count);
    }

    [Fact]
    public void Collection_Exactly_At_Limit_Succeeds()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        var source = Enumerable.Range(0, 10_000).Select(i => new Item { Value = i }).ToList();

        var result = mapper.Map<List<Item>, List<ItemDto>>(source);

        Assert.Equal(10_000, result.Count);
    }

    [Fact]
    public void DefaultMaxCollectionItems_Must_Be_Positive()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MapperConfiguration(c => c.DefaultMaxCollectionItems = 0));
        Assert.Contains("DefaultMaxCollectionItems", ex.Message);
    }

    [Fact]
    public void Exception_Carries_Source_And_Destination_Types()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        var source = Enumerable.Range(0, 10_001).Select(i => new Item { Value = i }).ToList();

        var ex = Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<List<Item>, List<ItemDto>>(source));

        Assert.NotNull(ex.SourceType);
        Assert.NotNull(ex.DestinationType);
        Assert.Contains("List", ex.SourceType!.Name);
    }
}

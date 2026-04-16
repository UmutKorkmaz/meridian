using System.Diagnostics;
using Meridian.Mapping;

namespace Meridian.Mapping.Tests;

/// <summary>
/// PoC tests for the <c>mapper.Map(source, destination)</c> overload — the
/// path that <see cref="DosRegressionTests"/> does not exercise. These tests
/// assert that the advertised <c>DefaultMaxCollectionItems</c> DoS mitigation
/// is enforced on <see cref="Execution.MappingEngine.MapToExisting"/> and its
/// helper <c>TryMapCollectionOntoExisting</c>.
///
/// Expected safe behaviour mirrors <see cref="DosRegressionTests"/>:
///   * An <see cref="ICollection{T}"/> with <c>Count</c> above
///     <c>DefaultMaxCollectionItems</c> must be rejected via the fast-path
///     check BEFORE any enumeration begins.
///   * A pure <see cref="IEnumerable{T}"/> must trip the mid-enumeration
///     counter within 1 item past the cap.
/// </summary>
public class MapToExistingDosTests
{
    public class Item { public int Value { get; set; } }
    public class ItemDto { public int Value { get; set; } }

    [Fact]
    public void MapToExisting_Oversized_ICollection_Is_Rejected_Before_Enumeration()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        // FakeLargeCollection reports Count=1_000_000 > 10_000 cap and
        // throws on enumeration — any mapping that touches the enumerator
        // is a mitigation bypass.
        var source = new FakeLargeCollection(1_000_000);
        var destination = new List<ItemDto>();

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<FakeLargeCollection, List<ItemDto>>(source, destination));
        sw.Stop();

        Assert.Equal(MeridianMappingLimit.MaxCollectionItems, ex.Limit);
        Assert.Equal(10_000, ex.MaxAllowed);
        Assert.Equal(1_000_000, ex.ObservedValue);
        Assert.True(sw.ElapsedMilliseconds < 250,
            $"Fast-path cap check took {sw.ElapsedMilliseconds}ms — " +
            $"expected near-instant rejection via ICollection fast-path on MapToExisting.");
    }

    [Fact]
    public void MapToExisting_Oversized_IEnumerable_Stops_Enumeration_Past_The_Cap()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Item, ItemDto>());
        var mapper = cfg.CreateMapper();

        var pulledCount = 0;
        IEnumerable<Item> Stream()
        {
            for (var i = 0; i < 10_000_000; i++)
            {
                pulledCount++;
                yield return new Item { Value = i };
            }
        }

        var destination = new List<ItemDto>();

        Assert.Throws<MeridianMappingLimitException>(() =>
            mapper.Map<IEnumerable<Item>, List<ItemDto>>(Stream(), destination));

        Assert.True(pulledCount <= 10_001,
            $"Pulled {pulledCount} items from the source stream on MapToExisting — " +
            $"expected to stop at 10_001 (DefaultMaxCollectionItems + 1). " +
            $"A higher count indicates MapToExisting is bypassing the advertised cap.");
    }

    [Fact]
    public void UseDestinationValue_SelfReferential_Collections_Honor_MaxDepth()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.DefaultMaxDepth = 2;
            c.CreateMap<RecursiveNode, RecursiveNodeDto>()
                .ForMember(d => d.Children, opt =>
                {
                    opt.MapFrom(s => s.Children);
                    opt.UseDestinationValue();
                });
        });
        var mapper = cfg.CreateMapper();

        var source = new RecursiveNode();
        source.Children.Add(source);

        var destination = new RecursiveNodeDto();
        destination.Children.Add(new RecursiveNodeDto());

        var result = mapper.Map(source, destination);

        Assert.Single(result.Children);
        Assert.Null(result.Children[0]);
    }

    private sealed class FakeLargeCollection : ICollection<Item>
    {
        public FakeLargeCollection(int size) { Count = size; }
        public int Count { get; }
        public bool IsReadOnly => true;
        public IEnumerator<Item> GetEnumerator() =>
            throw new InvalidOperationException(
                "FakeLargeCollection should not be enumerated — the cap check " +
                "must fire on ICollection.Count before the enumerator is touched.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
        public void Add(Item item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(Item item) => throw new NotSupportedException();
        public void CopyTo(Item[] array, int arrayIndex) => throw new NotSupportedException();
        public bool Remove(Item item) => throw new NotSupportedException();
    }

    private sealed class RecursiveNode
    {
        public List<RecursiveNode> Children { get; set; } = new();
    }

    private sealed class RecursiveNodeDto
    {
        public List<RecursiveNodeDto?> Children { get; set; } = new();
    }
}

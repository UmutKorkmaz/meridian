using System.Globalization;
using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Tests;

#pragma warning disable CA2263 // Runtime-type overloads are explicit compatibility test targets.
public class MappingPathParityTests
{
    private static readonly bool[] Modes = [false, true];
    private static readonly (int Source, int Mapped)[] DepthCases = [(63, 63), (64, 64), (65, 64)];
    private static readonly int[] CollectionCounts = [9999, 10000, 10001];
    public static IEnumerable<object[]> PathsAndDestinations =>
        from interpreter in Modes
        from existing in Modes
        select new object[] { interpreter, existing };

    [Theory]
    [MemberData(nameof(PathsAndDestinations))]
    public void ChildPreserveReferencesSharesDiamondCacheWithoutAncestorOptIn(bool interpreter, bool existing)
    {
        var configuration = Configure(interpreter, c =>
        {
            c.CreateMap<Graph, GraphDto>();
            c.CreateMap<Node, NodeDto>().PreserveReferences();
        });
        var mapper = configuration.CreateMapper();
        var shared = new Node { Value = 7 };
        var source = new Graph { Left = shared, Right = shared };
        var destination = new GraphDto();

        var mapped = existing ? mapper.Map(source, destination) : mapper.Map<Graph, GraphDto>(source);

        if (existing) Assert.Same(destination, mapped);
        Assert.NotNull(mapped.Left);
        Assert.Equal(7, mapped.Left.Value);
        Assert.Same(mapped.Left, mapped.Right);
    }

    [Theory]
    [MemberData(nameof(PathsAndDestinations))]
    public void EqualValueDistinctNodesRemainDistinctAndNestedCycleCloses(bool interpreter, bool existing)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.CreateMap<Graph, GraphDto>();
            c.CreateMap<Node, NodeDto>().PreserveReferences();
        }).CreateMapper();
        var left = new Node { Value = 7 };
        left.Child = left;
        var right = new Node { Value = 7 };
        right.Child = right;
        Assert.Equal(left, right); // Equal by value; two independent source identities.
        var source = new Graph { Left = left, Right = right };
        var mapped = existing ? mapper.Map(source, new GraphDto()) : mapper.Map<Graph, GraphDto>(source);

        Assert.NotSame(mapped.Left, mapped.Right);
        Assert.Equal(7, mapped.Left!.Value);
        Assert.Equal(7, mapped.Right!.Value);
        Assert.Same(mapped.Left, mapped.Left.Child);
        Assert.Same(mapped.Right, mapped.Right.Child);
    }

    [Theory]
    [MemberData(nameof(PathsAndDestinations))]
    public void WithoutPreserveReferencesSharedSourceMapsToIndependentDestinations(bool interpreter, bool existing)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.CreateMap<Graph, GraphDto>();
            c.CreateMap<Node, NodeDto>();
        }).CreateMapper();
        var shared = new Node { Value = 7 };
        var source = new Graph { Left = shared, Right = shared };
        var mapped = existing ? mapper.Map(source, new GraphDto()) : mapper.Map<Graph, GraphDto>(source);

        Assert.NotSame(mapped.Left, mapped.Right);
        Assert.Equal(7, mapped.Left!.Value);
        Assert.Equal(7, mapped.Right!.Value);
    }

    [Theory]
    [MemberData(nameof(PathsAndDestinations))]
    public void PreserveReferencesRootCycleReusesReturnedDestination(bool interpreter, bool existing)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<Node, NodeDto>().PreserveReferences(), requireFastPath: false).CreateMapper();
        var source = new Node { Value = 7 };
        source.Child = source;
        var destination = new NodeDto();

        var mapped = existing ? mapper.Map(source, destination) : mapper.Map<Node, NodeDto>(source);

        if (existing) Assert.Same(destination, mapped);
        Assert.Equal(7, mapped.Value);
        Assert.Same(mapped, mapped.Child);
    }

    [Theory]
    [MemberData(nameof(PathsAndDestinations))]
    public void NullableDefaultsAndIgnoredExistingStateAgree(bool interpreter, bool existing)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<ScalarSource, ScalarDto>()
            .ForMember(d => d.Kept, o => o.Ignore())).CreateMapper();
        var source = new ScalarSource { Number = null, Name = null, Kept = "source" };
        var destination = new ScalarDto { Number = 99, Name = "old", Kept = "keep" };

        var mapped = existing ? mapper.Map(source, destination) : mapper.Map<ScalarSource, ScalarDto>(source);

        if (existing) Assert.Same(destination, mapped);
        Assert.Equal(0, mapped.Number);
        Assert.Null(mapped.Name);
        Assert.Equal("keep", mapped.Kept);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void UndefinedEnumRejectionAndOptOutAgree(bool interpreter, bool existing, bool allowUndefined)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.AllowUndefinedEnumValues = allowUndefined;
            c.CreateMap<EnumSource, EnumDto>();
        }).CreateMapper();
        EnumDto Map() => existing
            ? mapper.Map(new EnumSource { Role = "999" }, new EnumDto())
            : mapper.Map<EnumSource, EnumDto>(new() { Role = "999" });

        if (allowUndefined)
        {
            Assert.Equal((Role)999, Map().Role);
        }
        else
        {
            var failure = Assert.Throws<MeridianEnumMappingException>(Map);
            Assert.Equal("999", failure.SourceValue);
            Assert.Equal(typeof(Role), failure.DestinationEnumType);
            Assert.Equal("Role", failure.DestinationMember);
        }
    }

    [Theory]
    [MemberData(nameof(PathsAndDestinations))]
    public void CurrentCultureAndOverflowAgree(bool interpreter, bool existing)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<NumericSource, NumericDto>()).CreateMapper();
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var source = new NumericSource { Amount = "1,5", Number = 42 };
            var mapped = existing ? mapper.Map(source, new NumericDto()) : mapper.Map<NumericSource, NumericDto>(source);
            Assert.Equal(1.5m, mapped.Amount);
            Assert.Equal((byte)42, mapped.Number);
            source.Number = 300;
            Assert.Throws<OverflowException>(() => existing
                ? mapper.Map(source, new NumericDto()) : mapper.Map<NumericSource, NumericDto>(source));
        }
        finally { CultureInfo.CurrentCulture = priorCulture; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericObjectAndRuntimeTypeOverloadsAgree(bool interpreter)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<ScalarSource, ScalarDto>()
            .ForMember(d => d.Kept, o => o.Ignore())).CreateMapper();
        var source = new ScalarSource { Number = 42, Name = "Meridian" };
        var outputs = new[]
        {
            mapper.Map<ScalarSource, ScalarDto>(source),
            mapper.Map<ScalarDto>((object)source),
            (ScalarDto)mapper.Map(source, typeof(ScalarSource), typeof(ScalarDto))
        };
        foreach (var output in outputs)
        {
            Assert.Equal(42, output.Number);
            Assert.Equal("Meridian", output.Name);
        }
        Assert.Null(mapper.Map<ScalarSource, ScalarDto>(null!));
        Assert.Throws<ArgumentNullException>(() => mapper.Map<ScalarDto>(null!));
        Assert.Throws<ArgumentNullException>(() => mapper.Map(null!, typeof(ScalarSource), typeof(ScalarDto)));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void UserMapFromNullChainFailureAgrees(bool interpreter, int nullAt)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<ChainSource, ChainDto>()
            .ForMember(d => d.Value, o => o.MapFrom(s => s.Inner!.Inner!.Value))).CreateMapper();
        var source = new ChainSource();
        if (nullAt > 0) source.Inner = new ChainSource();
        if (nullAt > 1) source.Inner!.Inner = new ChainSource { Value = "leaf" };
        // A user-supplied lambda retains its exception behavior; it is not null rewritten.
        if (nullAt < 2)
            Assert.Throws<NullReferenceException>(() => mapper.Map<ChainSource, ChainDto>(source));
        else
            Assert.Equal("leaf", mapper.Map<ChainSource, ChainDto>(source).Value);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void DeepFlattenedNullChainAgrees(bool interpreter, int nullAt)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<ChainSource, FlattenedChainDto>(),
            requireFastPath: false).CreateMapper();
        var source = new ChainSource();
        if (nullAt > 0) source.Inner = new ChainSource();
        if (nullAt > 1) source.Inner!.Inner = new ChainSource { Value = "leaf" };
        Assert.Equal(nullAt == 2 ? "leaf" : null,
            mapper.Map<ChainSource, FlattenedChainDto>(source).InnerInnerValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorSelectionAndConstructionFallbackAgree(bool interpreter)
    {
        var configuration = Configure(interpreter, c =>
        {
            c.CreateMap<ScalarSource, ConstructorDto>();
            c.CreateMap<ScalarSource, FactoryDto>().ConstructUsing(s => new FactoryDto("factory:" + s.Name));
        }, requireFastPath: false);
        var mapper = configuration.CreateMapper();
        var source = new ScalarSource { Name = "Meridian", Number = 7 };
        var mapped = mapper.Map<ScalarSource, ConstructorDto>(source);
        Assert.Equal("name", mapped.ConstructorUsed);
        Assert.Equal("Meridian", mapped.Name);
        Assert.Equal("factory:Meridian", mapper.Map<ScalarSource, FactoryDto>(source).Label);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MostSpecificDerivedDispatchAgrees(bool interpreter)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.CreateMap<Animal, AnimalDto>().Include<Dog, DogDto>();
            c.CreateMap<Dog, DogDto>();
        }, requireFastPath: false).CreateMapper();
        Animal source = new Dog { Name = "Rex", Bark = 9 };
        var mapped = Assert.IsType<DogDto>(mapper.Map<Animal, AnimalDto>(source));
        Assert.Equal("Rex", mapped.Name);
        Assert.Equal(9, mapped.Bark);
        var runtime = Assert.IsType<DogDto>(mapper.Map(source, typeof(Animal), typeof(AnimalDto)));
        Assert.Equal(9, runtime.Bark);
    }

    public static IEnumerable<object[]> DepthBoundaries =>
        from interpreter in Modes
        from existing in Modes
        from boundary in DepthCases
        select new object[] { interpreter, existing, boundary.Item1, boundary.Item2 };

    [Theory]
    [MemberData(nameof(DepthBoundaries))]
    public void DefaultDepthBoundaryTruncatesExactly(bool interpreter, bool existing, int sourceDepth, int expectedDepth)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<Node, NodeDto>()).CreateMapper();
        var source = new Node { Value = 0 };
        var tail = source;
        for (var i = 1; i < sourceDepth; i++) tail = tail.Child = new Node { Value = i };
        var destination = new NodeDto { Value = -1 };
        var mapped = existing ? mapper.Map(source, destination) : mapper.Map<Node, NodeDto>(source);
        if (existing) Assert.Same(destination, mapped);
        var seen = 0;
        for (var current = mapped; current != null; current = current.Child)
        {
            Assert.Equal(seen, current.Value);
            seen++;
            Assert.True(seen <= 64);
        }
        Assert.Equal(expectedDepth, seen);
    }

    [Theory]
    [MemberData(nameof(PathsAndDestinations))]
    public void CycleWithoutPreservationIsTruncatedAtDefaultDepth(bool interpreter, bool existing)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<Node, NodeDto>()).CreateMapper();
        var source = new Node { Value = 7 };
        source.Child = source;
        var mapped = existing ? mapper.Map(source, new NodeDto()) : mapper.Map<Node, NodeDto>(source);
        var seen = 0;
        for (var current = mapped; current != null; current = current.Child)
        {
            Assert.Equal(7, current.Value);
            seen++;
            Assert.True(seen <= 64);
        }
        Assert.Equal(64, seen);
    }

    public static IEnumerable<object[]> CollectionBoundaries =>
        from interpreter in Modes
        from existing in Modes
        from count in CollectionCounts
        select new object[] { interpreter, existing, count };

    [Theory]
    [MemberData(nameof(CollectionBoundaries))]
    public void StreamingCollectionBoundaryAndConsumptionAgree(bool interpreter, bool existing, int count)
    {
        var mapper = Configure(interpreter, c => c.CreateMap<Item, ItemDto>()).CreateMapper();
        var pulled = 0;
        IEnumerable<Item> Stream()
        {
            for (var i = 0; i < count; i++)
            {
                pulled++;
                yield return new Item { Value = i };
            }
        }
        var destination = new List<ItemDto> { new() { Value = -1 } };
        List<ItemDto> Map() => existing
            ? mapper.Map<IEnumerable<Item>, List<ItemDto>>(Stream(), destination)
            : mapper.Map<IEnumerable<Item>, List<ItemDto>>(Stream());
        if (count > 10000)
        {
            var failure = Assert.Throws<MeridianMappingLimitException>(Map);
            Assert.Equal(MeridianMappingLimit.MaxCollectionItems, failure.Limit);
            Assert.Equal(10000, failure.MaxAllowed);
            Assert.Equal(10001, failure.ObservedValue);
        }
        else
        {
            var mapped = Map();
            if (existing) Assert.Same(destination, mapped);
            Assert.Equal(count, mapped.Count);
            Assert.Equal(0, mapped[0].Value);
            Assert.Equal(count - 1, mapped[^1].Value);
        }
        Assert.Equal(count, pulled);
        Assert.True(pulled <= 10001);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceCacheKeepsDestinationTypesSeparate(bool existing)
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<MultiTargetSource, MultiTargetDto>();
            c.CreateMap<Node, NodeDto>().PreserveReferences();
            c.CreateMap<Node, AlternateNodeDto>().PreserveReferences();
        }).CreateMapper();
        var shared = new Node { Value = 7 };
        var source = new MultiTargetSource { First = shared, Second = shared };
        var mapped = existing ? mapper.Map(source, new MultiTargetDto())
            : mapper.Map<MultiTargetSource, MultiTargetDto>(source);
        Assert.Equal(7, Assert.IsType<NodeDto>(mapped.First).Value);
        Assert.Equal(7, Assert.IsType<AlternateNodeDto>(mapped.Second).Value);
        Assert.NotSame(mapped.First, mapped.Second);
    }

    [Fact]
    public void MappingSourceOntoItselfPreservesCycleAndIdentity()
    {
        var mapper = new MapperConfiguration(c => c.CreateMap<Node, Node>().PreserveReferences()).CreateMapper();
        var source = new Node { Value = 7 };
        source.Child = source;
        var result = mapper.Map(source, source);
        Assert.Same(source, result);
        Assert.Same(source, result.Child);
        Assert.Equal(7, result.Value);
    }
    public sealed class MultiTargetSource
    {
        public Node? First { get; set; }
        public Node? Second { get; set; }
    }
    public sealed class MultiTargetDto
    {
        public NodeDto? First { get; set; }
        public AlternateNodeDto? Second { get; set; }
    }
    public sealed class AlternateNodeDto { public int Value { get; set; } }

    private static MapperConfiguration Configure(bool interpreter, Action<IMapperConfigurationExpression> configure,
        bool requireFastPath = true)
    {
        // Separate configurations avoid mutating a shared application's immutable configuration.
        var configuration = new MapperConfiguration(configure);
        var maps = configuration.GetAllTypeMaps();
        if (requireFastPath) Assert.Contains(maps, map => map.CompiledFastPath != null);
        if (interpreter)
            foreach (var map in maps) map.CompiledFastPath = null;
        return configuration;
    }

    public sealed class ScalarSource
    {
        public int? Number { get; set; }
        public string? Name { get; set; }
        public string? Kept { get; set; }
    }
    public sealed class ScalarDto
    {
        public int Number { get; set; }
        public string? Name { get; set; }
        public string Kept { get; set; } = "keep";
    }
    public enum Role { User = 1 }
    public sealed class EnumSource { public string Role { get; set; } = "1"; }
    public sealed class EnumDto { public Role Role { get; set; } }
    public sealed class NumericSource
    {
        public string Amount { get; set; } = "0";
        public long Number { get; set; }
    }
    public sealed class NumericDto
    {
        public decimal Amount { get; set; }
        public byte Number { get; set; }
    }
    public sealed class ChainSource
    {
        public ChainSource? Inner { get; set; }
        public string? Value { get; set; }
    }
    public sealed class ChainDto { public string? Value { get; set; } }
    public sealed class FlattenedChainDto { public string? InnerInnerValue { get; set; } }
    public sealed class ConstructorDto
    {
        public ConstructorDto(string name) { Name = name; ConstructorUsed = "name"; }
        public ConstructorDto(string name, Guid missing) { Name = name; ConstructorUsed = "widest"; }
        public string Name { get; }
        public string ConstructorUsed { get; }
    }
    public sealed class FactoryDto
    {
        public FactoryDto(string label) => Label = label;
        public string Label { get; }
    }
    public class Animal { public string Name { get; set; } = ""; }
    public sealed class Dog : Animal { public int Bark { get; set; } }
    public class AnimalDto { public string Name { get; set; } = ""; }
    public sealed class DogDto : AnimalDto { public int Bark { get; set; } }
    public sealed class Item { public int Value { get; set; } }
    public sealed class ItemDto { public int Value { get; set; } }

    public sealed class Graph
    {
        public Node? Left { get; set; }
        public Node? Right { get; set; }
    }
    public sealed class GraphDto
    {
        public NodeDto? Left { get; set; }
        public NodeDto? Right { get; set; }
    }
    public sealed class Node
    {
        public int Value { get; set; }
        public Node? Child { get; set; }
        public override bool Equals(object? obj) => obj is Node other && Value == other.Value;
        public override int GetHashCode() => Value;
    }
    public sealed class NodeDto
    {
        public int Value { get; set; }
        public NodeDto? Child { get; set; }
    }
}

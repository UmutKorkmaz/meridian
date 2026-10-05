using System.Linq.Expressions;
using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Tests;

/// <summary>
/// P3.7 safety net. Every behavior is asserted for both the compiled fast path and the
/// interpreter (all <see cref="TypeMap.CompiledFastPath"/> delegates cleared on an independent
/// configuration). Expression-tree and counting-provider assertions prove which nested maps were
/// inlined and which kept the engine's generic conversion dispatch.
/// </summary>
public class NestedFastPathParityTests
{
    private static readonly bool[] Modes = [false, true];

    public static IEnumerable<object[]> InterpreterModes =>
        Modes.Select(interpreter => new object[] { interpreter });

    public static IEnumerable<object[]> DepthCapsByMode =>
        from interpreter in Modes
        from cap in new[] { 1, 2, 3, 4, 5, 6, 7, 8 }
        select new object[] { interpreter, cap };

    public static IEnumerable<object[]> CycleCapsByMode =>
        from interpreter in Modes
        from cap in new[] { 1, 2, 3, 4, 5, 6, 7, 9, 63, 64 }
        select new object[] { interpreter, cap };

    public static IEnumerable<object[]> UnsupportedFeatures =>
        from interpreter in Modes
        from feature in new[] { "PreserveReferences", "BeforeMap", "AfterMap", "ConvertUsing", "ConstructUsing", "Condition", "DiResolver" }
        select new object[] { interpreter, feature };

    // ------------------------------------------------------------------ models

    public enum NfpKind { Alpha = 1, Beta = 2 }

    public sealed record NfpGeo { public double Lat { get; set; } public double Lon { get; set; } }
    public sealed record NfpGeoDto { public double Lat { get; set; } public double Lon { get; set; } }
    public sealed record NfpAddr { public string Line { get; set; } = ""; public string City { get; set; } = ""; public NfpGeo? Geo { get; set; } }
    public sealed record NfpAddrDto { public string Line { get; set; } = ""; public string City { get; set; } = ""; public NfpGeoDto? Geo { get; set; } }
    public sealed record NfpTag { public string Kind { get; set; } = ""; }
    public sealed record NfpTagDto { public NfpKind Kind { get; set; } }
    public sealed record NfpOrder
    {
        public int Id { get; set; }
        public NfpAddr? Address { get; set; }
        public NfpAddr? Billing { get; set; }
        public NfpTag? Tag { get; set; }
    }
    public sealed record NfpOrderDto
    {
        public int Id { get; set; }
        public NfpAddrDto? Address { get; set; }
        public NfpAddrDto? Billing { get; set; }
        public NfpTagDto? Tag { get; set; }
    }

    // Seven distinct chained pairs so the inline limit and every depth boundary are reachable.
    public sealed record NfpC1 { public int Level { get; set; } public NfpC2? Next { get; set; } }
    public sealed record NfpC2 { public int Level { get; set; } public NfpC3? Next { get; set; } }
    public sealed record NfpC3 { public int Level { get; set; } public NfpC4? Next { get; set; } }
    public sealed record NfpC4 { public int Level { get; set; } public NfpC5? Next { get; set; } }
    public sealed record NfpC5 { public int Level { get; set; } public NfpC6? Next { get; set; } }
    public sealed record NfpC6 { public int Level { get; set; } public NfpC7? Next { get; set; } }
    public sealed record NfpC7 { public int Level { get; set; } }
    public sealed record NfpD1 { public int Level { get; set; } public NfpD2? Next { get; set; } }
    public sealed record NfpD2 { public int Level { get; set; } public NfpD3? Next { get; set; } }
    public sealed record NfpD3 { public int Level { get; set; } public NfpD4? Next { get; set; } }
    public sealed record NfpD4 { public int Level { get; set; } public NfpD5? Next { get; set; } }
    public sealed record NfpD5 { public int Level { get; set; } public NfpD6? Next { get; set; } }
    public sealed record NfpD6 { public int Level { get; set; } public NfpD7? Next { get; set; } }
    public sealed record NfpD7 { public int Level { get; set; } }

    // Mutually recursive pair: A inlines B but B -> A is a cycle and must keep engine dispatch.
    public sealed class NfpA { public int Level { get; set; } public NfpB? Next { get; set; } }
    public sealed class NfpB { public int Level { get; set; } public NfpA? Next { get; set; } }
    public sealed class NfpADto { public int Level { get; set; } public NfpBDto? Next { get; set; } }
    public sealed class NfpBDto { public int Level { get; set; } public NfpADto? Next { get; set; } }

    // Non-sealed source type: a runtime-derived instance must keep ConvertValue's runtime-type lookup.
    public class NfpShape { public string Label { get; set; } = ""; }
    public sealed class NfpRoundShape : NfpShape { public double Radius { get; set; } }
    public class NfpShapeDto { public string Label { get; set; } = ""; }
    public sealed class NfpShapeHolder { public NfpShape? Shape { get; set; } }
    public sealed class NfpShapeHolderDto { public NfpShapeDto? Shape { get; set; } }

    public sealed class NfpReadSource
    {
        private readonly NfpAddr _address = new() { Line = "Main", City = "Ankara" };
        public int Reads { get; private set; }
        public NfpAddr Address { get { Reads++; return _address; } }
    }
    public sealed class NfpReadDto { public NfpAddrDto? Address { get; set; } }

    public sealed record NfpLine { public int Number { get; set; } public NfpAddr? Where { get; set; } }
    public sealed record NfpLineDto { public int Number { get; set; } public NfpAddrDto? Where { get; set; } }
    public sealed class NfpBasket { public List<NfpLine> Lines { get; set; } = []; }
    public sealed class NfpBasketDto { public List<NfpLineDto> Lines { get; set; } = []; }

    // Same-type assignable member registered with a generated descriptor, below a nested runtime map.
    public sealed record NfpStamp { public int Value { get; set; } }
    public sealed record NfpStampHolder { public NfpStamp Stamp { get; set; } = new(); }
    public sealed record NfpStampHolderDto { public NfpStamp Stamp { get; set; } = new(); }
    public sealed record NfpStampParent { public NfpStampHolder Holder { get; set; } = new(); }
    public sealed record NfpStampParentDto { public NfpStampHolderDto Holder { get; set; } = new(); }

    public sealed class NfpCityResolver : IValueResolver<NfpAddr, NfpAddrDto, string>
    {
        public int Calls;
        public string Resolve(NfpAddr source, NfpAddrDto destination, string destMember, ResolutionContext context)
        {
            Calls++;
            return "resolved:" + source.City;
        }
    }

    // ----------------------------------------------------------------- helpers

    private static MapperConfiguration Configure(bool interpreter, Action<IMapperConfigurationExpression> configure)
    {
        var configuration = new MapperConfiguration(configure);
        var maps = configuration.GetAllTypeMaps();
        if (interpreter)
        {
            foreach (var map in maps) map.CompiledFastPath = null;
            Assert.All(maps, map => Assert.Null(map.CompiledFastPath));
        }
        else
        {
            Assert.Contains(maps, map => map.CompiledFastPath is not null);
        }

        return configuration;
    }

    private static readonly Action<IMapperConfigurationExpression> OrderMaps = c =>
    {
        c.CreateMap<NfpGeo, NfpGeoDto>();
        c.CreateMap<NfpAddr, NfpAddrDto>();
        c.CreateMap<NfpTag, NfpTagDto>();
        c.CreateMap<NfpOrder, NfpOrderDto>();
    };

    private static NfpOrder SampleOrder() => new()
    {
        Id = 7,
        Address = new NfpAddr { Line = "Main 1", City = "Ankara", Geo = new NfpGeo { Lat = 39.9, Lon = 32.8 } },
        Billing = new NfpAddr { Line = "Bill 2", City = "Izmir" },
        Tag = new NfpTag { Kind = "beta" },
    };

    private static LambdaExpression BuildExpression(MapperConfiguration configuration, Type source, Type destination)
    {
        var provider = (IConfigurationProvider)configuration;
        var map = provider.FindTypeMap(source, destination)!;
        var expression = FastPathCompiler.TryBuildExpression(
            map, provider.AllowNullDestinationValues, hasValueTransformers: false, hasGeneratedMappings: false, provider.FindTypeMap);
        Assert.NotNull(expression);
        return expression!;
    }

    private static List<Type> ConvertValueTargets(Expression expression)
    {
        var collector = new ConvertValueCollector();
        collector.Visit(expression);
        return collector.Targets;
    }

    private sealed class ConvertValueCollector : ExpressionVisitor
    {
        public List<Type> Targets { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(MappingEngine) && node.Method.Name == nameof(MappingEngine.ConvertValue))
                Targets.Add((Type)((ConstantExpression)node.Arguments[1]).Value!);
            return base.VisitMethodCall(node);
        }
    }

    private static int ChainLength(NfpD1? chain)
    {
        var length = 0;
        object? current = chain;
        while (current is not null)
        {
            length++;
            current = current switch
            {
                NfpD1 d => d.Next,
                NfpD2 d => d.Next,
                NfpD3 d => d.Next,
                NfpD4 d => d.Next,
                NfpD5 d => d.Next,
                NfpD6 d => d.Next,
                _ => null,
            };
        }

        return length;
    }

    private static NfpC1 BuildChain()
    {
        var c7 = new NfpC7 { Level = 7 };
        var c6 = new NfpC6 { Level = 6, Next = c7 };
        var c5 = new NfpC5 { Level = 5, Next = c6 };
        var c4 = new NfpC4 { Level = 4, Next = c5 };
        var c3 = new NfpC3 { Level = 3, Next = c4 };
        var c2 = new NfpC2 { Level = 2, Next = c3 };
        return new NfpC1 { Level = 1, Next = c2 };
    }

    private static readonly Action<IMapperConfigurationExpression> ChainMaps = c =>
    {
        c.CreateMap<NfpC7, NfpD7>();
        c.CreateMap<NfpC6, NfpD6>();
        c.CreateMap<NfpC5, NfpD5>();
        c.CreateMap<NfpC4, NfpD4>();
        c.CreateMap<NfpC3, NfpD3>();
        c.CreateMap<NfpC2, NfpD2>();
        c.CreateMap<NfpC1, NfpD1>();
    };

    private sealed class CountingProvider(IConfigurationProvider inner) : IConfigurationProvider
    {
        public int FindTypeMapCalls { get; private set; }
        public TypeMap? FindTypeMap(Type sourceType, Type destinationType) { FindTypeMapCalls++; return inner.FindTypeMap(sourceType, destinationType); }
        public IMapper CreateMapper() => inner.CreateMapper();
        public IMapper CreateMapper(IServiceProvider serviceProvider) => inner.CreateMapper(serviceProvider);
        public void AssertConfigurationIsValid() => inner.AssertConfigurationIsValid();
        public IReadOnlyCollection<TypeMap> GetAllTypeMaps() => inner.GetAllTypeMaps();
        public bool AllowNullCollections => inner.AllowNullCollections;
        public bool AllowNullDestinationValues => inner.AllowNullDestinationValues;
        public int DefaultMaxDepth => inner.DefaultMaxDepth;
        public int DefaultMaxCollectionItems => inner.DefaultMaxCollectionItems;
        public string GetMappingPlan<TSource, TDestination>() => inner.GetMappingPlan<TSource, TDestination>();
        public string GetMappingPlan(Type sourceType, Type destinationType) => inner.GetMappingPlan(sourceType, destinationType);
    }

    // -------------------------------------------------- extra dispatch (diagnostics)

    [Fact]
    public void Eligible_Nested_Maps_Avoid_Extra_Dispatch()
    {
        var configuration = Configure(interpreter: false, OrderMaps);
        var expression = BuildExpression(configuration, typeof(NfpOrder), typeof(NfpOrderDto));

        // Address, Billing, Geo and Tag are inlined: the only remaining engine conversion is the
        // string -> enum member conversion inside Tag. No nested DTO dispatch is left.
        Assert.Equal([typeof(NfpKind)], ConvertValueTargets(expression));

        // Runtime evidence: executing the compiled delegate never consults the provider's type maps.
        var provider = new CountingProvider(configuration);
        var engine = new MappingEngine(provider);
        var context = new ResolutionContext(configuration.CreateMapper());
        var map = configuration.FindTypeMap(typeof(NfpOrder), typeof(NfpOrderDto))!;
        var dto = (NfpOrderDto)map.CompiledFastPath!(SampleOrder(), engine, context);

        Assert.Equal(0, provider.FindTypeMapCalls);
        var interpreted = Configure(interpreter: true, OrderMaps).CreateMapper().Map<NfpOrder, NfpOrderDto>(SampleOrder());
        Assert.Equal(interpreted, dto);
        Assert.Equal("Ankara", dto.Address!.City);
        Assert.Equal(32.8, dto.Address.Geo!.Lon);
        Assert.Equal(NfpKind.Beta, dto.Tag!.Kind);
        Assert.NotSame(dto.Address, dto.Billing);
    }

    [Fact]
    public void Ineligible_Nested_Map_Keeps_Engine_Dispatch_Control()
    {
        var configuration = Configure(interpreter: false, c =>
        {
            c.CreateMap<NfpGeo, NfpGeoDto>();
            c.CreateMap<NfpAddr, NfpAddrDto>().AfterMap((s, d) => d.Line += "!");
            c.CreateMap<NfpTag, NfpTagDto>();
            c.CreateMap<NfpOrder, NfpOrderDto>();
        });
        var targets = ConvertValueTargets(BuildExpression(configuration, typeof(NfpOrder), typeof(NfpOrderDto)));
        Assert.Equal(2, targets.Count(t => t == typeof(NfpAddrDto)));

        var provider = new CountingProvider(configuration);
        var engine = new MappingEngine(provider);
        var map = configuration.FindTypeMap(typeof(NfpOrder), typeof(NfpOrderDto))!;
        var dto = (NfpOrderDto)map.CompiledFastPath!(SampleOrder(), engine, new ResolutionContext(configuration.CreateMapper()));
        Assert.True(provider.FindTypeMapCalls >= 2);
        Assert.Equal("Main 1!", dto.Address!.Line);
    }

    [Fact]
    public void Inlining_Stops_At_The_Documented_Limit_And_Falls_Back_Below_It()
    {
        var configuration = Configure(interpreter: false, ChainMaps);
        var targets = ConvertValueTargets(BuildExpression(configuration, typeof(NfpC1), typeof(NfpD1)));

        // The root plus MaxInlineDepth nested levels (C2..C(1+limit)) are inline. The next level keeps engine
        // dispatch; that map's own delegate inlines its subtree, so at most one deeper conversion remains.
        var firstFallbackLevel = FastPathCompiler.MaxInlineDepth + 2;
        Type[] expected = firstFallbackLevel <= 7 ? [ChainDestination(firstFallbackLevel)] : [];
        Assert.Equal(expected, targets);
        Assert.Equal(7, ChainLength(configuration.CreateMapper().Map<NfpC1, NfpD1>(BuildChain())));
    }

    private static Type ChainDestination(int level) => level switch
    {
        2 => typeof(NfpD2),
        3 => typeof(NfpD3),
        4 => typeof(NfpD4),
        5 => typeof(NfpD5),
        6 => typeof(NfpD6),
        _ => typeof(NfpD7),
    };

    [Fact]
    public void Recursive_Pairs_Are_Inlined_Once_And_Cycle_Back_Through_The_Engine()
    {
        var configuration = Configure(interpreter: false, c =>
        {
            c.CreateMap<NfpA, NfpADto>();
            c.CreateMap<NfpB, NfpBDto>();
        });
        var targets = ConvertValueTargets(BuildExpression(configuration, typeof(NfpA), typeof(NfpADto)));
        Assert.Equal([typeof(NfpADto)], targets);
    }

    // ------------------------------------------------------------ source reads

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Eligible_Nested_Source_Member_Is_Read_Once(bool interpreter)
    {
        var configuration = Configure(interpreter, c =>
        {
            c.CreateMap<NfpGeo, NfpGeoDto>();
            c.CreateMap<NfpAddr, NfpAddrDto>();
            c.CreateMap<NfpReadSource, NfpReadDto>();
        });
        var source = new NfpReadSource();
        var mapped = configuration.CreateMapper().Map<NfpReadSource, NfpReadDto>(source);
        Assert.Equal(1, source.Reads);
        Assert.Equal("Ankara", mapped.Address!.City);
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Fallback_Nested_Source_Member_Is_Read_Once(bool interpreter)
    {
        var configuration = Configure(interpreter, c =>
        {
            c.CreateMap<NfpGeo, NfpGeoDto>();
            c.CreateMap<NfpAddr, NfpAddrDto>().AfterMap((s, d) => d.Line += "!");
            c.CreateMap<NfpReadSource, NfpReadDto>();
        });
        var source = new NfpReadSource();
        var mapped = configuration.CreateMapper().Map<NfpReadSource, NfpReadDto>(source);
        Assert.Equal(1, source.Reads);
        Assert.Equal("Main!", mapped.Address!.Line);
    }

    // ---------------------------------------------------- value / null / enum parity

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Nested_Values_And_Independent_Destinations_Match(bool interpreter)
    {
        var mapper = Configure(interpreter, OrderMaps).CreateMapper();
        var source = SampleOrder();
        var mapped = mapper.Map<NfpOrder, NfpOrderDto>(source);

        Assert.Equal(7, mapped.Id);
        Assert.Equal("Main 1", mapped.Address!.Line);
        Assert.Equal(39.9, mapped.Address.Geo!.Lat);
        Assert.Equal("Izmir", mapped.Billing!.City);
        Assert.Null(mapped.Billing.Geo);
        Assert.Equal(NfpKind.Beta, mapped.Tag!.Kind);
        Assert.NotSame(mapped.Address, mapped.Billing);

        // A shared source instance maps to independent destinations without PreserveReferences.
        source.Billing = source.Address;
        var shared = mapper.Map<NfpOrder, NfpOrderDto>(source);
        Assert.NotSame(shared.Address, shared.Billing);
        Assert.Equal(shared.Address, shared.Billing);
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Null_Nested_Sources_Map_To_Null_Destinations(bool interpreter)
    {
        var mapper = Configure(interpreter, OrderMaps).CreateMapper();
        var mapped = mapper.Map<NfpOrder, NfpOrderDto>(new NfpOrder { Id = 3 });
        Assert.Equal(3, mapped.Id);
        Assert.Null(mapped.Address);
        Assert.Null(mapped.Billing);
        Assert.Null(mapped.Tag);

        var partial = mapper.Map<NfpOrder, NfpOrderDto>(new NfpOrder { Address = new NfpAddr { Line = "x" } });
        Assert.Equal("x", partial.Address!.Line);
        Assert.Null(partial.Address.Geo);
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Enum_Failures_Inside_Inlined_Nested_Maps_Keep_Member_And_Message(bool interpreter)
    {
        var mapper = Configure(interpreter, OrderMaps).CreateMapper();
        Assert.Equal(NfpKind.Alpha, mapper.Map<NfpOrder, NfpOrderDto>(new NfpOrder { Tag = new NfpTag { Kind = "ALPHA" } }).Tag!.Kind);

        foreach (var invalid in new[] { "gamma", "9", "" })
        {
            var ex = Assert.Throws<MeridianEnumMappingException>(() =>
                mapper.Map<NfpOrder, NfpOrderDto>(new NfpOrder { Tag = new NfpTag { Kind = invalid } }));
            Assert.Equal("Kind", ex.DestinationMember);
            Assert.Equal(typeof(NfpKind), ex.DestinationEnumType);
            Assert.Equal(invalid, ex.SourceValue);
        }
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Explicit_Nested_TypeMap_Configuration_Is_Applied(bool interpreter)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.CreateMap<NfpGeo, NfpGeoDto>().ForMember(d => d.Lat, o => o.MapFrom(s => s.Lat + 1));
            c.CreateMap<NfpAddr, NfpAddrDto>()
                .ForMember(d => d.City, o => o.MapFrom(s => s.City.ToUpperInvariant()))
                .ForMember(d => d.Line, o => o.Ignore());
            c.CreateMap<NfpTag, NfpTagDto>();
            c.CreateMap<NfpOrder, NfpOrderDto>()
                .ForMember(d => d.Billing, o => o.MapFrom(s => s.Address));
        }).CreateMapper();

        var mapped = mapper.Map<NfpOrder, NfpOrderDto>(SampleOrder());
        Assert.Equal("ANKARA", mapped.Address!.City);
        Assert.Equal("", mapped.Address.Line);
        Assert.Equal(40.9, mapped.Address.Geo!.Lat);
        Assert.Equal("ANKARA", mapped.Billing!.City);
        Assert.Equal(mapped.Address, mapped.Billing);
        Assert.NotSame(mapped.Address, mapped.Billing);
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Runtime_Derived_Value_Keeps_Exact_Runtime_Type_Map_Selection(bool interpreter)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.CreateMap<NfpShape, NfpShapeDto>();
            c.CreateMap<NfpRoundShape, NfpShapeDto>().ForMember(d => d.Label, o => o.MapFrom(s => "round:" + s.Label));
            c.CreateMap<NfpShapeHolder, NfpShapeHolderDto>();
        }).CreateMapper();

        Assert.Equal("plain", mapper.Map<NfpShapeHolder, NfpShapeHolderDto>(new() { Shape = new NfpShape { Label = "plain" } }).Shape!.Label);
        Assert.Equal("round:disk", mapper.Map<NfpShapeHolder, NfpShapeHolderDto>(new() { Shape = new NfpRoundShape { Label = "disk" } }).Shape!.Label);
    }

    // ------------------------------------------------------------- depth / caps

    [Theory]
    [MemberData(nameof(DepthCapsByMode))]
    public void Global_Depth_Cap_Truncates_At_The_Same_Level(bool interpreter, int cap)
    {
        var mapper = Configure(interpreter, c => { c.DefaultMaxDepth = cap; ChainMaps(c); }).CreateMapper();
        var mapped = mapper.Map<NfpC1, NfpD1>(BuildChain());
        Assert.Equal(Math.Min(7, cap), ChainLength(mapped));
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Per_Map_Max_Depth_Is_Preserved_For_Inlined_And_Fallback_Levels(bool interpreter)
    {
        // C4 sits at depth 3 (inlined from C1); C6 sits at depth 5 (beyond the inline limit).
        var inlinedLevel = Configure(interpreter, c =>
        {
            c.CreateMap<NfpC7, NfpD7>(); c.CreateMap<NfpC6, NfpD6>(); c.CreateMap<NfpC5, NfpD5>();
            c.CreateMap<NfpC4, NfpD4>().MaxDepth(2);
            c.CreateMap<NfpC3, NfpD3>(); c.CreateMap<NfpC2, NfpD2>(); c.CreateMap<NfpC1, NfpD1>();
        }).CreateMapper();
        Assert.Equal(3, ChainLength(inlinedLevel.Map<NfpC1, NfpD1>(BuildChain())));

        var fallbackLevel = Configure(interpreter, c =>
        {
            c.CreateMap<NfpC7, NfpD7>();
            c.CreateMap<NfpC6, NfpD6>().MaxDepth(1);
            c.CreateMap<NfpC5, NfpD5>(); c.CreateMap<NfpC4, NfpD4>(); c.CreateMap<NfpC3, NfpD3>();
            c.CreateMap<NfpC2, NfpD2>(); c.CreateMap<NfpC1, NfpD1>();
        }).CreateMapper();
        Assert.Equal(5, ChainLength(fallbackLevel.Map<NfpC1, NfpD1>(BuildChain())));
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Inherited_Context_Depth_Cap_Is_Honored_By_Nested_Levels(bool interpreter)
    {
        var configuration = Configure(interpreter, ChainMaps);
        var mapper = configuration.CreateMapper();
        foreach (var cap in new[] { 1, 2, 3, 5, 6 })
        {
            var context = new ResolutionContext(mapper);
            Assert.True(context.TryMapConfigured(BuildChain(), typeof(NfpC1), typeof(NfpD1), relativeDepth: 0, out var dto, maxDepth: cap));
            Assert.Equal(cap, ChainLength((NfpD1?)dto));
        }

        // A bridge that starts two levels down keeps counting from that depth.
        var offset = new ResolutionContext(mapper);
        Assert.True(offset.TryMapConfigured(BuildChain(), typeof(NfpC1), typeof(NfpD1), relativeDepth: 2, out var shifted, maxDepth: 5));
        Assert.Equal(3, ChainLength((NfpD1?)shifted));
    }

    [Theory]
    [MemberData(nameof(CycleCapsByMode))]
    public void Recursive_Pair_Depth_Matches_Cap(bool interpreter, int cap)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.DefaultMaxDepth = cap;
            c.CreateMap<NfpA, NfpADto>();
            c.CreateMap<NfpB, NfpBDto>();
        }).CreateMapper();

        NfpA root = new() { Level = 1 };
        object tail = root;
        for (var level = 2; level <= 70; level++)
        {
            if (tail is NfpA a) { var b = new NfpB { Level = level }; a.Next = b; tail = b; }
            else { var next = new NfpA { Level = level }; ((NfpB)tail).Next = next; tail = next; }
        }

        var mapped = mapper.Map<NfpA, NfpADto>(root);
        var length = 0;
        object? current = mapped;
        while (current is not null)
        {
            length++;
            current = current is NfpADto adto ? adto.Next : ((NfpBDto)current).Next;
        }

        Assert.Equal(cap, length);
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Collection_Cap_Is_Preserved_For_Elements_With_Inlined_Nested_Maps(bool interpreter)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.DefaultMaxCollectionItems = 5;
            c.CreateMap<NfpGeo, NfpGeoDto>();
            c.CreateMap<NfpAddr, NfpAddrDto>();
            c.CreateMap<NfpLine, NfpLineDto>();
            c.CreateMap<NfpBasket, NfpBasketDto>();
        }).CreateMapper();

        NfpBasket Basket(int count) => new()
        {
            Lines = Enumerable.Range(1, count).Select(i => new NfpLine { Number = i, Where = new NfpAddr { Line = "L" + i, Geo = new NfpGeo { Lat = i } } }).ToList(),
        };

        var ok = mapper.Map<NfpBasket, NfpBasketDto>(Basket(5));
        Assert.Equal(5, ok.Lines.Count);
        Assert.Equal("L4", ok.Lines[3].Where!.Line);
        Assert.Equal(5.0, ok.Lines[4].Where!.Geo!.Lat);
        var ex = Assert.Throws<MeridianMappingLimitException>(() => mapper.Map<NfpBasket, NfpBasketDto>(Basket(6)));
        Assert.Equal(MeridianMappingLimit.MaxCollectionItems, ex.Limit);
        Assert.Equal(5, ex.MaxAllowed);
    }

    // ----------------------------------------------- unsupported nested features

    [Theory]
    [MemberData(nameof(UnsupportedFeatures))]
    public void Unsupported_Nested_Features_Keep_Interpreter_Fallback(bool interpreter, string feature)
    {
        var actionCalls = 0;
        var resolver = new NfpCityResolver();
        var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var configuration = Configure(interpreter, c =>
        {
            c.CreateMap<NfpGeo, NfpGeoDto>();
            var address = c.CreateMap<NfpAddr, NfpAddrDto>();
            switch (feature)
            {
                case "PreserveReferences": address.PreserveReferences(); break;
                case "BeforeMap": address.BeforeMap((s, d) => actionCalls++); break;
                case "AfterMap": address.AfterMap((s, d) => { actionCalls++; d.Line += "!"; }); break;
                case "ConvertUsing": address.ConvertUsing(s => { actionCalls++; return new NfpAddrDto { Line = "converted:" + s.Line, City = s.City }; }); break;
                case "ConstructUsing": address.ConstructUsing(s => { actionCalls++; return new NfpAddrDto { Line = "ctor" }; }); break;
                case "Condition": address.ForMember(d => d.Line, o => o.Condition(s => { actionCalls++; return false; })); break;
                case "DiResolver": address.ForMember(d => d.City, o => o.MapFrom<NfpCityResolver>()); break;
            }

            c.CreateMap<NfpTag, NfpTagDto>();
            c.CreateMap<NfpOrder, NfpOrderDto>();
        });

        if (!interpreter)
        {
            // The nested pair keeps the engine's generic dispatch (two sites: Address and Billing).
            var targets = ConvertValueTargets(BuildExpression(configuration, typeof(NfpOrder), typeof(NfpOrderDto)));
            Assert.Equal(2, targets.Count(t => t == typeof(NfpAddrDto)));
        }

        var shared = new NfpAddr { Line = "Main 1", City = "Ankara" };
        var source = new NfpOrder { Address = shared, Billing = shared };
        var mapped = configuration.CreateMapper(services).Map<NfpOrder, NfpOrderDto>(source);

        switch (feature)
        {
            case "PreserveReferences":
                Assert.Same(mapped.Address, mapped.Billing);
                Assert.Equal("Ankara", mapped.Address!.City);
                break;
            case "BeforeMap":
                Assert.Equal(2, actionCalls);
                Assert.Equal("Main 1", mapped.Address!.Line);
                Assert.NotSame(mapped.Address, mapped.Billing);
                break;
            case "AfterMap":
                Assert.Equal(2, actionCalls);
                Assert.Equal("Main 1!", mapped.Address!.Line);
                Assert.Equal("Main 1!", mapped.Billing!.Line);
                break;
            case "ConvertUsing":
                Assert.Equal(2, actionCalls);
                Assert.Equal("converted:Main 1", mapped.Address!.Line);
                break;
            case "ConstructUsing":
                Assert.Equal(2, actionCalls);
                Assert.Equal("Ankara", mapped.Address!.City);
                Assert.Equal("Main 1", mapped.Address.Line);
                break;
            case "Condition":
                Assert.Equal("", mapped.Address!.Line);
                Assert.Equal("Ankara", mapped.Address.City);
                break;
            case "DiResolver":
                Assert.Equal(2, resolver.Calls);
                Assert.Equal("resolved:Ankara", mapped.Address!.City);
                Assert.Equal("resolved:Ankara", mapped.Billing!.City);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Polymorphic_Include_Base_Map_Keeps_Engine_Dispatch(bool interpreter)
    {
        var configuration = Configure(interpreter, c =>
        {
            c.CreateMap<NfpRoundShape, NfpShapeDto>().ForMember(d => d.Label, o => o.MapFrom(s => "round:" + s.Label));
            c.CreateMap<NfpShape, NfpShapeDto>().Include<NfpRoundShape, NfpShapeDto>();
            c.CreateMap<NfpShapeHolder, NfpShapeHolderDto>();
        });
        if (!interpreter)
            Assert.Equal([typeof(NfpShapeDto)], ConvertValueTargets(BuildExpression(configuration, typeof(NfpShapeHolder), typeof(NfpShapeHolderDto))));

        var mapper = configuration.CreateMapper();
        Assert.Equal("plain", mapper.Map<NfpShapeHolder, NfpShapeHolderDto>(new() { Shape = new NfpShape { Label = "plain" } }).Shape!.Label);
        Assert.Equal("round:disk", mapper.Map<NfpShapeHolder, NfpShapeHolderDto>(new() { Shape = new NfpRoundShape { Label = "disk" } }).Shape!.Label);
    }

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void PreserveReferences_Cycles_Below_An_Eligible_Parent_Still_Close(bool interpreter)
    {
        var mapper = Configure(interpreter, c =>
        {
            c.CreateMap<NfpA, NfpADto>().PreserveReferences();
            c.CreateMap<NfpB, NfpBDto>();
        }).CreateMapper();

        var a = new NfpA { Level = 1 };
        a.Next = new NfpB { Level = 2, Next = a };
        var mapped = mapper.Map<NfpA, NfpADto>(a);
        Assert.Same(mapped, mapped.Next!.Next);
    }

    // ------------------------------------------- context / services / concurrency

    [Theory]
    [MemberData(nameof(InterpreterModes))]
    public void Generated_Registrations_Inside_Inlined_Nested_Members_See_The_Same_Context(bool interpreter)
    {
        var observed = new List<(int Depth, object? Item, object? State)>();
        var resolver = new StampResolver(observed);
        var configuration = Configure(interpreter, c =>
        {
            c.UseGeneratedMappings(resolver);
            c.CreateMap<NfpStampHolder, NfpStampHolderDto>();
            c.CreateMap<NfpStampParent, NfpStampParentDto>();
        });

        var mapped = configuration.CreateMapper().Map<NfpStampParent, NfpStampParentDto>(
            new NfpStampParent { Holder = new NfpStampHolder { Stamp = new NfpStamp { Value = 41 } } },
            o => { o.Items["answer"] = 7; o.State = "state"; });

        Assert.Equal(42, mapped.Holder.Stamp.Value);
        var call = Assert.Single(observed);
        // Parent depth 0, holder map depth 1, its member conversion context depth 2: identical in both modes.
        Assert.Equal(2, call.Depth);
        Assert.Equal(7, call.Item);
        Assert.Equal("state", call.State);
    }

    private sealed class StampResolver(List<(int Depth, object? Item, object? State)> observed) : IGeneratedMappingResolver
    {
        public bool TryGetMapping(Type source, Type destination, out GeneratedMappingDescriptor? mapping)
        {
            mapping = source == typeof(NfpStamp) && destination == typeof(NfpStamp)
                ? new GeneratedMappingDescriptor(source, destination, (value, context) =>
                {
                    observed.Add((context.Depth, context.Items["answer"], context.State));
                    return new NfpStamp { Value = ((NfpStamp)value!).Value + 1 };
                })
                : null;
            return mapping is not null;
        }
    }

    [Fact]
    public void Compiled_Delegates_Are_Safe_To_Share_Across_Threads()
    {
        var configuration = Configure(interpreter: false, OrderMaps);
        var mapper = configuration.CreateMapper();
        var expected = Configure(interpreter: true, OrderMaps).CreateMapper().Map<NfpOrder, NfpOrderDto>(SampleOrder());
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            for (var i = 0; i < 500; i++)
            {
                var mapped = mapper.Map<NfpOrder, NfpOrderDto>(SampleOrder());
                if (!mapped.Equals(expected)) failures.Add("value mismatch");
            }
        });

        Assert.Empty(failures);
    }
}

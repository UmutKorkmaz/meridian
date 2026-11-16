using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Mapster;
using MeridianAlias = Meridian.Mapping;

namespace Meridian.Benchmarks;

// ============================================================================
// Mapping benchmarks.
//
// * The E3 families (E3MappingFlat, E3MappingNested, E3MappingCollectionPayload, E3MappingDeep, E3MappingReferences) compare
//   equal work: ONE complete new-result map of the same payload by a hand-written floor, Meridian runtime (interpreted and
//   compiled, selected through the public TypeMap.CompiledFastPath property on independently created identical
//   configurations), Meridian generated mapping (direct call and, separately, the generated runtime integration),
//   Mapster and Mapperly. The generated and Mapperly methods live in GeneratedMappingBenchmarks.cs (partial classes).
// * Every contender is validated against independently specified expected values in GlobalSetup (and by the preflight
//   before any timing): all members, collection element order, non-aliasing of nested objects, reference identity and
//   source immutability. A contender that cannot do the same work is omitted and recorded, never timed.
// * The older H1 classes further below (Flat/OneLevelNested/DeepBounded/CollectionPayload MappingBenchmarks) are the
//   B8 harness (job B8-H1) and keep their bodies and job unchanged. The B8 P0/P1 evidence was produced by the frozen
//   snapshot copies of this harness kept with the task artifacts, and the pair is rerun from those snapshots; these live
//   copies are not part of the E3 evidence set.
// * The April 2026 numbers in the benchmark README were produced by an earlier class of this file and are historical.
// ============================================================================


public class SourceOrder
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string Status { get; set; } = "";
    public decimal TotalGross { get; set; }
    public decimal TotalNet { get; set; }
    public decimal VatRate { get; set; }
    public SourceAddress ShippingAddress { get; set; } = default!;
}

public class SourceAddress
{
    public string Line1 { get; set; } = "";
    public string Line2 { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
}

public class DestOrder
{
    public int OrderId { get; set; }
    public string Number { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public string Status { get; set; } = "";
    public decimal Gross { get; set; }
    public decimal Net { get; set; }
    public decimal Vat { get; set; }
    public DestAddress ShipTo { get; set; } = default!;
}

public class DestAddress
{
    public string Line1 { get; set; } = "";
    public string Line2 { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
}

// ============================================================================
// B8 nested fast-path harness (H1).
//
// Scenarios: flat, one-level nested, deep bounded chain (4 and 16 levels) and
// collection payloads. Every scenario times ONE complete new-result map and
// compares three contenders over identical work:
//   * Manual            - hand-written floor
//   * MeridianInterpreted - the same MapperConfiguration specification with every
//                         TypeMap.CompiledFastPath cleared at setup (existing
//                         public seam, no new switch); path selection is asserted
//   * MeridianCompiled  - the default configuration; compiled delegates asserted
// GlobalSetup validates every contender against the hand-written result with a
// deep value comparison (and non-aliasing of nested objects) outside timing.
// Null, cap and enum-failure behavior is covered by correctness tests, not here.
// The fixed job below is intentionally the only job: do not change it between
// the P0 (pre-optimization) and P1 (post-optimization) runs.
// ============================================================================

/// <summary>The single documented job shared by every B8 scenario.</summary>
public sealed class NestedFastPathJobConfig : BenchmarkDotNet.Configs.ManualConfig
{
    public NestedFastPathJobConfig()
    {
        AddJob(new BenchmarkDotNet.Jobs.Job("B8-H1")
            .WithLaunchCount(3)
            .WithWarmupCount(8)
            .WithIterationCount(20));
        AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
        AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Instance);
        AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
        AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.Full);
        AddExporter(BenchmarkDotNet.Exporters.Csv.CsvExporter.Default);
        AddExporter(BenchmarkDotNet.Exporters.MarkdownExporter.GitHub);
    }
}

/// <summary>Setup-time helpers: path selection proof and semantic equivalence.</summary>
internal static class NestedPathSupport
{
    /// <summary>Creates compiled and interpreted mappers from the same specification.</summary>
    public static (MeridianAlias.IMapper Compiled, MeridianAlias.IMapper Interpreted) Create(
        Action<MeridianAlias.IMapperConfigurationExpression> configure, int expectedCompiledMaps)
    {
        var compiledConfig = new MeridianAlias.MapperConfiguration(configure);
        var compiledMaps = compiledConfig.GetAllTypeMaps();
        var compiledCount = compiledMaps.Count(m => m.CompiledFastPath is not null);
        if (compiledCount != expectedCompiledMaps)
            throw new InvalidOperationException(
                $"Expected {expectedCompiledMaps} compiled fast paths but found {compiledCount} of {compiledMaps.Count}.");

        // An independent configuration so the compiled contender is never mutated.
        var interpretedConfig = new MeridianAlias.MapperConfiguration(configure);
        foreach (var map in interpretedConfig.GetAllTypeMaps())
            map.CompiledFastPath = null;
        if (interpretedConfig.GetAllTypeMaps().Any(m => m.CompiledFastPath is not null))
            throw new InvalidOperationException("Interpreter configuration still has compiled fast paths.");

        return (compiledConfig.CreateMapper(), interpretedConfig.CreateMapper());
    }

    /// <summary>
    /// Deep value comparison of two objects of the same type. Returns the number of
    /// non-default scalar leaves compared and throws on any difference, on default-valued
    /// leaves (which would let an all-default result pass), and on shared nested instances.
    /// </summary>
    public static int DeepCompare(object expected, object actual, string path = "$")
    {
        if (expected.GetType() != actual.GetType())
            throw new InvalidOperationException($"{path}: type {expected.GetType().Name} != {actual.GetType().Name}.");
        var leaves = 0;
        foreach (var property in expected.GetType().GetProperties())
        {
            var left = property.GetValue(expected);
            var right = property.GetValue(actual);
            var childPath = $"{path}.{property.Name}";
            if (left is null || right is null)
            {
                if (left is not null || right is not null)
                    throw new InvalidOperationException($"{childPath}: null mismatch.");
                continue;
            }

            if (left is string || left.GetType().IsValueType)
            {
                if (!left.Equals(right))
                    throw new InvalidOperationException($"{childPath}: '{left}' != '{right}'.");
                var isDefault = left is string text ? text.Length == 0 : left.Equals(Activator.CreateInstance(left.GetType()));
                if (isDefault)
                    throw new InvalidOperationException($"{childPath}: benchmark data must be non-default.");
                leaves++;
            }
            else if (left is System.Collections.IList leftList)
            {
                var rightList = (System.Collections.IList)right;
                if (leftList.Count != rightList.Count)
                    throw new InvalidOperationException($"{childPath}: count {leftList.Count} != {rightList.Count}.");
                for (var i = 0; i < leftList.Count; i++)
                {
                    if (ReferenceEquals(leftList[i], rightList[i]))
                        throw new InvalidOperationException($"{childPath}[{i}]: shared instance.");
                    leaves += DeepCompare(leftList[i]!, rightList[i]!, $"{childPath}[{i}]");
                }
            }
            else
            {
                if (ReferenceEquals(left, right))
                    throw new InvalidOperationException($"{childPath}: shared instance.");
                leaves += DeepCompare(left, right, childPath);
            }
        }

        return leaves;
    }

    public static void Require(string scenario, int leaves, int expectedLeaves)
    {
        if (leaves != expectedLeaves)
            throw new InvalidOperationException($"{scenario}: compared {leaves} leaves, expected {expectedLeaves}.");
    }
}

/// <summary>Flat payload: ten scalars, no nesting.</summary>
[Config(typeof(NestedFastPathJobConfig))]
public class FlatMappingBenchmarks
{
    private FlatSource _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;

    [GlobalSetup]
    public void Setup()
    {
        _source = new FlatSource
        {
            Id = 42, Name = "Acme Corp", Email = "ops@acme.example", Status = "Approved", Notes = "bulk",
            Created = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc), Version = 7L,
            Total = 12_345.67m, Net = 10_288.06m, Vat = 0.20m,
        };
        (_compiled, _interpreted) = NestedPathSupport.Create(
            cfg => cfg.CreateMap<FlatSource, FlatDest>(), expectedCompiledMaps: 1);

        var expected = ManualMap(_source);
        NestedPathSupport.Require("flat/compiled", NestedPathSupport.DeepCompare(expected, _compiled.Map<FlatSource, FlatDest>(_source)), 10);
        NestedPathSupport.Require("flat/interpreted", NestedPathSupport.DeepCompare(expected, _interpreted.Map<FlatSource, FlatDest>(_source)), 10);
    }

    private static FlatDest ManualMap(FlatSource s) => new()
    {
        Id = s.Id, Name = s.Name, Email = s.Email, Status = s.Status, Notes = s.Notes,
        Created = s.Created, Version = s.Version, Total = s.Total, Net = s.Net, Vat = s.Vat,
    };

    [Benchmark(Baseline = true, Description = "Manual")]
    public FlatDest Manual() => ManualMap(_source);

    [Benchmark(Description = "Meridian interpreted")]
    public FlatDest Interpreted() => _interpreted.Map<FlatSource, FlatDest>(_source);

    [Benchmark(Description = "Meridian compiled")]
    public FlatDest Compiled() => _compiled.Map<FlatSource, FlatDest>(_source);
}

/// <summary>One nested address inside an order (the original benchmark shape).</summary>
[Config(typeof(NestedFastPathJobConfig))]
public class OneLevelNestedMappingBenchmarks
{
    private SourceOrder _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;

    [GlobalSetup]
    public void Setup()
    {
        _source = OrderPayload.Create();
        (_compiled, _interpreted) = NestedPathSupport.Create(OrderPayload.ConfigureMeridian, expectedCompiledMaps: 2);

        var expected = OrderPayload.ManualMap(_source);
        NestedPathSupport.Require("nested1/compiled", NestedPathSupport.DeepCompare(expected, _compiled.Map<SourceOrder, DestOrder>(_source)), 15);
        NestedPathSupport.Require("nested1/interpreted", NestedPathSupport.DeepCompare(expected, _interpreted.Map<SourceOrder, DestOrder>(_source)), 15);
    }

    [Benchmark(Baseline = true, Description = "Manual")]
    public DestOrder Manual() => OrderPayload.ManualMap(_source);

    [Benchmark(Description = "Meridian interpreted")]
    public DestOrder Interpreted() => _interpreted.Map<SourceOrder, DestOrder>(_source);

    [Benchmark(Description = "Meridian compiled")]
    public DestOrder Compiled() => _compiled.Map<SourceOrder, DestOrder>(_source);
}

/// <summary>A chain of 4 or 16 nested objects, each its own explicit TypeMap (bounded far below the depth cap).</summary>
[Config(typeof(NestedFastPathJobConfig))]
public class DeepBoundedMappingBenchmarks
{
    private S01 _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;

    [Params(4, 16)]
    public int Depth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _source = DeepChain.BuildDeepSource(Depth);
        (_compiled, _interpreted) = NestedPathSupport.Create(DeepChain.Configure, expectedCompiledMaps: 16);

        var expected = DeepChain.ManualD01(_source);
        var expectedLeaves = Depth * 3;
        NestedPathSupport.Require($"deep{Depth}/compiled", NestedPathSupport.DeepCompare(expected, _compiled.Map<S01, D01>(_source)), expectedLeaves);
        NestedPathSupport.Require($"deep{Depth}/interpreted", NestedPathSupport.DeepCompare(expected, _interpreted.Map<S01, D01>(_source)), expectedLeaves);
    }

    [Benchmark(Baseline = true, Description = "Manual")]
    public D01 Manual() => DeepChain.ManualD01(_source);

    [Benchmark(Description = "Meridian interpreted")]
    public D01 Interpreted() => _interpreted.Map<S01, D01>(_source);

    [Benchmark(Description = "Meridian compiled")]
    public D01 Compiled() => _compiled.Map<S01, D01>(_source);
}

/// <summary>
/// A basket whose collection payload items each contain one nested product. The root
/// map has a collection destination, so it stays on the interpreter in every mode;
/// the per-item maps (with nested product) are the eligible compiled maps.
/// </summary>
[Config(typeof(NestedFastPathJobConfig))]
public class CollectionPayloadMappingBenchmarks
{
    private Basket _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;

    [Params(1, 32)]
    public int Items { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _source = new Basket { Id = 9, Owner = "ops@acme.example" };
        for (var i = 1; i <= Items; i++)
        {
            _source.Lines.Add(new BasketLine
            {
                Number = i, Sku = $"SKU-{i:0000}", Quantity = i * 1.5m, Price = 9.99m + i,
                Product = new BasketProduct { Name = $"Product {i}", Category = "Hardware", Origin = "TR" },
            });
        }

        (_compiled, _interpreted) = NestedPathSupport.Create(BasketPayload.Configure, expectedCompiledMaps: 2);
        var expected = BasketPayload.ManualMap(_source);
        var expectedLeaves = 2 + Items * 7;
        NestedPathSupport.Require($"collection{Items}/compiled", NestedPathSupport.DeepCompare(expected, _compiled.Map<Basket, BasketDto>(_source)), expectedLeaves);
        NestedPathSupport.Require($"collection{Items}/interpreted", NestedPathSupport.DeepCompare(expected, _interpreted.Map<Basket, BasketDto>(_source)), expectedLeaves);
    }

    [Benchmark(Baseline = true, Description = "Manual")]
    public BasketDto Manual() => BasketPayload.ManualMap(_source);

    [Benchmark(Description = "Meridian interpreted")]
    public BasketDto Interpreted() => _interpreted.Map<Basket, BasketDto>(_source);

    [Benchmark(Description = "Meridian compiled")]
    public BasketDto Compiled() => _compiled.Map<Basket, BasketDto>(_source);
}

internal static partial class OrderPayload
{
    public static SourceOrder Create() => new()
    {
        Id = 42,
        OrderNumber = "ORD-2026-00042",
        CustomerName = "Acme Corp",
        CustomerEmail = "ops@acme.example",
        CreatedUtc = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc),
        UpdatedUtc = new DateTime(2026, 4, 14, 13, 30, 0, DateTimeKind.Utc),
        Status = "Approved",
        TotalGross = 12_345.67m,
        TotalNet = 10_288.06m,
        VatRate = 0.20m,
        ShippingAddress = new SourceAddress
        {
            Line1 = "Maslak Mah.", Line2 = "1453 Sk. No:2", City = "Istanbul", PostalCode = "34485", Country = "TR",
        },
    };

    public static void ConfigureMeridian(MeridianAlias.IMapperConfigurationExpression cfg)
    {
        cfg.CreateMap<SourceAddress, DestAddress>()
            .ForMember(d => d.Line1, o => o.MapFrom(s => s.Line1))
            .ForMember(d => d.Line2, o => o.MapFrom(s => s.Line2))
            .ForMember(d => d.City, o => o.MapFrom(s => s.City))
            .ForMember(d => d.PostalCode, o => o.MapFrom(s => s.PostalCode))
            .ForMember(d => d.Country, o => o.MapFrom(s => s.Country));

        cfg.CreateMap<SourceOrder, DestOrder>()
            .ForMember(d => d.OrderId, o => o.MapFrom(s => s.Id))
            .ForMember(d => d.Number, o => o.MapFrom(s => s.OrderNumber))
            .ForMember(d => d.Customer, o => o.MapFrom(s => s.CustomerName))
            .ForMember(d => d.Email, o => o.MapFrom(s => s.CustomerEmail))
            .ForMember(d => d.Created, o => o.MapFrom(s => s.CreatedUtc))
            .ForMember(d => d.Updated, o => o.MapFrom(s => s.UpdatedUtc))
            .ForMember(d => d.Status, o => o.MapFrom(s => s.Status))
            .ForMember(d => d.Gross, o => o.MapFrom(s => s.TotalGross))
            .ForMember(d => d.Net, o => o.MapFrom(s => s.TotalNet))
            .ForMember(d => d.Vat, o => o.MapFrom(s => s.VatRate))
            .ForMember(d => d.ShipTo, o => o.MapFrom(s => s.ShippingAddress));
    }

    public static DestOrder ManualMap(SourceOrder s) => new()
    {
        OrderId = s.Id,
        Number = s.OrderNumber,
        Customer = s.CustomerName,
        Email = s.CustomerEmail,
        Created = s.CreatedUtc,
        Updated = s.UpdatedUtc,
        Status = s.Status,
        Gross = s.TotalGross,
        Net = s.TotalNet,
        Vat = s.VatRate,
        ShipTo = new DestAddress
        {
            Line1 = s.ShippingAddress.Line1,
            Line2 = s.ShippingAddress.Line2,
            City = s.ShippingAddress.City,
            PostalCode = s.ShippingAddress.PostalCode,
            Country = s.ShippingAddress.Country,
        },
    };
}

internal static partial class BasketPayload
{
    public static void Configure(MeridianAlias.IMapperConfigurationExpression cfg)
    {
        cfg.CreateMap<BasketProduct, BasketProductDto>();
        cfg.CreateMap<BasketLine, BasketLineDto>();
        cfg.CreateMap<Basket, BasketDto>();
    }

    public static BasketDto ManualMap(Basket s)
    {
        var lines = new List<BasketLineDto>(s.Lines.Count);
        foreach (var line in s.Lines)
        {
            lines.Add(new BasketLineDto
            {
                Number = line.Number, Sku = line.Sku, Quantity = line.Quantity, Price = line.Price,
                Product = new BasketProductDto { Name = line.Product.Name, Category = line.Product.Category, Origin = line.Product.Origin },
            });
        }

        return new BasketDto { Id = s.Id, Owner = s.Owner, Lines = lines };
    }
}

public class FlatSource
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Created { get; set; }
    public long Version { get; set; }
    public decimal Total { get; set; }
    public decimal Net { get; set; }
    public decimal Vat { get; set; }
}

public class FlatDest
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Created { get; set; }
    public long Version { get; set; }
    public decimal Total { get; set; }
    public decimal Net { get; set; }
    public decimal Vat { get; set; }
}

public class Basket
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    public List<BasketLine> Lines { get; set; } = [];
}

public class BasketLine
{
    public int Number { get; set; }
    public string Sku { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public BasketProduct Product { get; set; } = default!;
}

public class BasketProduct
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Origin { get; set; } = "";
}

public class BasketDto
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    public List<BasketLineDto> Lines { get; set; } = [];
}

public class BasketLineDto
{
    public int Number { get; set; }
    public string Sku { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public BasketProductDto Product { get; set; } = default!;
}

public class BasketProductDto
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Origin { get; set; } = "";
}


internal static partial class DeepChain
{
    public static void Configure(MeridianAlias.IMapperConfigurationExpression cfg)
    {
        cfg.CreateMap<S01, D01>();
        cfg.CreateMap<S02, D02>();
        cfg.CreateMap<S03, D03>();
        cfg.CreateMap<S04, D04>();
        cfg.CreateMap<S05, D05>();
        cfg.CreateMap<S06, D06>();
        cfg.CreateMap<S07, D07>();
        cfg.CreateMap<S08, D08>();
        cfg.CreateMap<S09, D09>();
        cfg.CreateMap<S10, D10>();
        cfg.CreateMap<S11, D11>();
        cfg.CreateMap<S12, D12>();
        cfg.CreateMap<S13, D13>();
        cfg.CreateMap<S14, D14>();
        cfg.CreateMap<S15, D15>();
        cfg.CreateMap<S16, D16>();
    }

    internal static S01 BuildDeepSource(int depth)
    {
        var n16 = new S16 { Level = 16, Label = "level-16", Amount = 16.25m };
        var n15 = new S15 { Level = 15, Label = "level-15", Amount = 15.25m, Child = depth > 15 ? n16 : null };
        var n14 = new S14 { Level = 14, Label = "level-14", Amount = 14.25m, Child = depth > 14 ? n15 : null };
        var n13 = new S13 { Level = 13, Label = "level-13", Amount = 13.25m, Child = depth > 13 ? n14 : null };
        var n12 = new S12 { Level = 12, Label = "level-12", Amount = 12.25m, Child = depth > 12 ? n13 : null };
        var n11 = new S11 { Level = 11, Label = "level-11", Amount = 11.25m, Child = depth > 11 ? n12 : null };
        var n10 = new S10 { Level = 10, Label = "level-10", Amount = 10.25m, Child = depth > 10 ? n11 : null };
        var n9 = new S09 { Level = 9, Label = "level-9", Amount = 9.25m, Child = depth > 9 ? n10 : null };
        var n8 = new S08 { Level = 8, Label = "level-8", Amount = 8.25m, Child = depth > 8 ? n9 : null };
        var n7 = new S07 { Level = 7, Label = "level-7", Amount = 7.25m, Child = depth > 7 ? n8 : null };
        var n6 = new S06 { Level = 6, Label = "level-6", Amount = 6.25m, Child = depth > 6 ? n7 : null };
        var n5 = new S05 { Level = 5, Label = "level-5", Amount = 5.25m, Child = depth > 5 ? n6 : null };
        var n4 = new S04 { Level = 4, Label = "level-4", Amount = 4.25m, Child = depth > 4 ? n5 : null };
        var n3 = new S03 { Level = 3, Label = "level-3", Amount = 3.25m, Child = depth > 3 ? n4 : null };
        var n2 = new S02 { Level = 2, Label = "level-2", Amount = 2.25m, Child = depth > 2 ? n3 : null };
        var n1 = new S01 { Level = 1, Label = "level-1", Amount = 1.25m, Child = depth > 1 ? n2 : null };
        return n1;
    }

    internal static D01 ManualD01(S01 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD02(s.Child) };
    internal static D02 ManualD02(S02 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD03(s.Child) };
    internal static D03 ManualD03(S03 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD04(s.Child) };
    internal static D04 ManualD04(S04 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD05(s.Child) };
    internal static D05 ManualD05(S05 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD06(s.Child) };
    internal static D06 ManualD06(S06 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD07(s.Child) };
    internal static D07 ManualD07(S07 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD08(s.Child) };
    internal static D08 ManualD08(S08 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD09(s.Child) };
    internal static D09 ManualD09(S09 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD10(s.Child) };
    internal static D10 ManualD10(S10 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD11(s.Child) };
    internal static D11 ManualD11(S11 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD12(s.Child) };
    internal static D12 ManualD12(S12 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD13(s.Child) };
    internal static D13 ManualD13(S13 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD14(s.Child) };
    internal static D14 ManualD14(S14 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD15(s.Child) };
    internal static D15 ManualD15(S15 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount, Child = s.Child is null ? null : ManualD16(s.Child) };
    internal static D16 ManualD16(S16 s) => new() { Level = s.Level, Label = s.Label, Amount = s.Amount };
}

// ---- Deep bounded chain: 16 distinct nested pairs (generated once; do not hand-edit) ----
public sealed class S01 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S02? Child { get; set; } }
public sealed class D01 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D02? Child { get; set; } }
public sealed class S02 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S03? Child { get; set; } }
public sealed class D02 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D03? Child { get; set; } }
public sealed class S03 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S04? Child { get; set; } }
public sealed class D03 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D04? Child { get; set; } }
public sealed class S04 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S05? Child { get; set; } }
public sealed class D04 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D05? Child { get; set; } }
public sealed class S05 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S06? Child { get; set; } }
public sealed class D05 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D06? Child { get; set; } }
public sealed class S06 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S07? Child { get; set; } }
public sealed class D06 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D07? Child { get; set; } }
public sealed class S07 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S08? Child { get; set; } }
public sealed class D07 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D08? Child { get; set; } }
public sealed class S08 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S09? Child { get; set; } }
public sealed class D08 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D09? Child { get; set; } }
public sealed class S09 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S10? Child { get; set; } }
public sealed class D09 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D10? Child { get; set; } }
public sealed class S10 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S11? Child { get; set; } }
public sealed class D10 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D11? Child { get; set; } }
public sealed class S11 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S12? Child { get; set; } }
public sealed class D11 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D12? Child { get; set; } }
public sealed class S12 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S13? Child { get; set; } }
public sealed class D12 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D13? Child { get; set; } }
public sealed class S13 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S14? Child { get; set; } }
public sealed class D13 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D14? Child { get; set; } }
public sealed class S14 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S15? Child { get; set; } }
public sealed class D14 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D15? Child { get; set; } }
public sealed class S15 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public S16? Child { get; set; } }
public sealed class D15 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } public D16? Child { get; set; } }
public sealed class S16 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } }
public sealed class D16 { public int Level { get; set; } public string Label { get; set; } = ""; public decimal Amount { get; set; } }


// ============================================================================
// E3 payload helpers: deterministic payloads, independently specified expectations and the equivalence verifiers that
// run in GlobalSetup and in the preflight (never inside a timed method).
// ============================================================================

internal static class FlatPayload
{
    public static FlatSource Create() => new()
    {
        Id = 42, Name = "Acme Corp", Email = "ops@acme.example", Status = "Approved", Notes = "bulk",
        Created = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc), Version = 7L,
        Total = 12_345.67m, Net = 10_288.06m, Vat = 0.20m,
    };

    public static FlatDest ManualMap(FlatSource s) => new()
    {
        Id = s.Id, Name = s.Name, Email = s.Email, Status = s.Status, Notes = s.Notes,
        Created = s.Created, Version = s.Version, Total = s.Total, Net = s.Net, Vat = s.Vat,
    };

    /// <summary>Expected values written out literally, independent of every mapper and of <see cref="ManualMap"/>.</summary>
    private static FlatDest Expected() => new()
    {
        Id = 42, Name = "Acme Corp", Email = "ops@acme.example", Status = "Approved", Notes = "bulk",
        Created = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc), Version = 7L,
        Total = 12_345.67m, Net = 10_288.06m, Vat = 0.20m,
    };

    public static void Verify(string contender, FlatSource source, FlatDest result)
    {
        NestedPathSupport.Require("flat/" + contender, NestedPathSupport.DeepCompare(Expected(), result), 10);
        NestedPathSupport.DeepCompare(Create(), source);
    }
}

internal static partial class OrderPayload
{
    /// <summary>A fully compiled Mapster configuration for the order pair (explicit renames, nested address).</summary>
    public static TypeAdapterConfig CreateMapsterConfig()
    {
        var mapster = new TypeAdapterConfig();
        mapster.NewConfig<SourceAddress, DestAddress>();
        mapster.NewConfig<SourceOrder, DestOrder>()
            .Map(d => d.OrderId, s => s.Id).Map(d => d.Number, s => s.OrderNumber).Map(d => d.Customer, s => s.CustomerName)
            .Map(d => d.Email, s => s.CustomerEmail).Map(d => d.Created, s => s.CreatedUtc).Map(d => d.Updated, s => s.UpdatedUtc)
            .Map(d => d.Gross, s => s.TotalGross).Map(d => d.Net, s => s.TotalNet).Map(d => d.Vat, s => s.VatRate)
            .Map(d => d.ShipTo, s => s.ShippingAddress);
        mapster.Compile();
        return mapster;
    }

    private static DestOrder Expected() => new()
    {
        OrderId = 42, Number = "ORD-2026-00042", Customer = "Acme Corp", Email = "ops@acme.example",
        Created = new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc),
        Updated = new DateTime(2026, 4, 14, 13, 30, 0, DateTimeKind.Utc),
        Status = "Approved", Gross = 12_345.67m, Net = 10_288.06m, Vat = 0.20m,
        ShipTo = new DestAddress { Line1 = "Maslak Mah.", Line2 = "1453 Sk. No:2", City = "Istanbul", PostalCode = "34485", Country = "TR" },
    };

    public static void Verify(string contender, SourceOrder source, DestOrder result)
    {
        NestedPathSupport.Require("nested/" + contender, NestedPathSupport.DeepCompare(Expected(), result), 15);
        NestedPathSupport.DeepCompare(Create(), source);
    }
}

internal static partial class BasketPayload
{
    public static Basket Create(int items)
    {
        var basket = new Basket { Id = 9, Owner = "ops@acme.example" };
        for (var i = 1; i <= items; i++)
        {
            basket.Lines.Add(new BasketLine
            {
                Number = i, Sku = $"SKU-{i:0000}", Quantity = i * 1.5m, Price = 9.99m + i,
                Product = new BasketProduct { Name = $"Product {i}", Category = "Hardware", Origin = "TR" },
            });
        }

        return basket;
    }

    private static BasketDto Expected(int items)
    {
        var dto = new BasketDto { Id = 9, Owner = "ops@acme.example" };
        for (var i = 1; i <= items; i++)
        {
            dto.Lines.Add(new BasketLineDto
            {
                Number = i, Sku = "SKU-" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
                Quantity = i * 1.5m, Price = 9.99m + i,
                Product = new BasketProductDto { Name = "Product " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Category = "Hardware", Origin = "TR" },
            });
        }

        return dto;
    }

    public static void Verify(string contender, int items, Basket source, BasketDto result)
    {
        NestedPathSupport.Require($"collection{items}/{contender}", NestedPathSupport.DeepCompare(Expected(items), result), 2 + items * 7);
        if (result.Lines is null || result.Lines.Count != items)
            throw new InvalidOperationException($"collection{items}/{contender}: wrong collection.");
        NestedPathSupport.DeepCompare(Create(items), source);
    }
}

internal static partial class DeepChain
{
    public static readonly (Type Source, Type Destination)[] Pairs =
    [
        (typeof(S01), typeof(D01)), (typeof(S02), typeof(D02)), (typeof(S03), typeof(D03)), (typeof(S04), typeof(D04)),
        (typeof(S05), typeof(D05)), (typeof(S06), typeof(D06)), (typeof(S07), typeof(D07)), (typeof(S08), typeof(D08)),
        (typeof(S09), typeof(D09)), (typeof(S10), typeof(D10)), (typeof(S11), typeof(D11)), (typeof(S12), typeof(D12)),
        (typeof(S13), typeof(D13)), (typeof(S14), typeof(D14)), (typeof(S15), typeof(D15)), (typeof(S16), typeof(D16)),
    ];

    /// <summary>Walks the destination chain with independently computed expectations (level i: i, "level-i", i + 0.25).</summary>
    public static void Verify(string contender, int depth, S01 source, object? result)
    {
        object? node = result;
        for (var level = 1; level <= depth; level++)
        {
            if (node is null)
                throw new InvalidOperationException($"deep{depth}/{contender}: chain ends before level {level}.");
            var type = node.GetType();
            if (type.Name != "D" + level.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException($"deep{depth}/{contender}: level {level} has type {type.Name}.");
            var actualLevel = (int)type.GetProperty("Level")!.GetValue(node)!;
            var label = (string)type.GetProperty("Label")!.GetValue(node)!;
            var amount = (decimal)type.GetProperty("Amount")!.GetValue(node)!;
            if (actualLevel != level || label != "level-" + level.ToString(System.Globalization.CultureInfo.InvariantCulture) || amount != level + 0.25m)
                throw new InvalidOperationException($"deep{depth}/{contender}: level {level} holds {actualLevel}/{label}/{amount}.");
            node = type.GetProperty("Child")?.GetValue(node);
        }

        if (node is not null)
            throw new InvalidOperationException($"deep{depth}/{contender}: chain is longer than {depth} levels.");
        NestedPathSupport.DeepCompare(BuildDeepSource(depth), source);
    }
}

/// <summary>A small repeated-reference DAG: Left and Right are the SAME source node, Distinct is a different node with equal values.</summary>
public class RefNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class RefNodeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class RefGraph
{
    public RefNode Left { get; set; } = default!;
    public RefNode Right { get; set; } = default!;
    public RefNode Distinct { get; set; } = default!;
}

public class RefGraphDto
{
    public RefNodeDto Left { get; set; } = default!;
    public RefNodeDto Right { get; set; } = default!;
    public RefNodeDto Distinct { get; set; } = default!;
}

internal static class ReferencePayload
{
    public static RefGraph Create()
    {
        var shared = new RefNode { Id = 7, Name = "shared" };
        return new RefGraph { Left = shared, Right = shared, Distinct = new RefNode { Id = 7, Name = "shared" } };
    }

    /// <summary>The hand-written floor implements the same identity tracking by source reference.</summary>
    public static RefGraphDto ManualMap(RefGraph s)
    {
        var seen = new Dictionary<object, RefNodeDto>(ReferenceEqualityComparer.Instance);
        return new RefGraphDto { Left = Node(s.Left, seen), Right = Node(s.Right, seen), Distinct = Node(s.Distinct, seen) };

        static RefNodeDto Node(RefNode source, Dictionary<object, RefNodeDto> seen)
        {
            if (!seen.TryGetValue(source, out var node))
            {
                node = new RefNodeDto { Id = source.Id, Name = source.Name };
                seen.Add(source, node);
            }

            return node;
        }
    }

    public static void Verify(string contender, RefGraph source, RefGraphDto result)
    {
        if (result is null || result.Left is null || result.Right is null || result.Distinct is null)
            throw new InvalidOperationException($"references/{contender}: incomplete result.");
        if (!ReferenceEquals(result.Left, result.Right))
            throw new InvalidOperationException($"references/{contender}: a repeated source reference was not mapped to one destination instance.");
        if (ReferenceEquals(result.Distinct, result.Left))
            throw new InvalidOperationException($"references/{contender}: two distinct equal-valued sources collapsed to one destination instance.");
        foreach (var node in new[] { result.Left, result.Distinct })
        {
            if (node.Id != 7 || node.Name != "shared")
                throw new InvalidOperationException($"references/{contender}: wrong node values {node.Id}/{node.Name}.");
        }

        if (!ReferenceEquals(source.Left, source.Right) || ReferenceEquals(source.Left, source.Distinct)
            || source.Left.Id != 7 || source.Left.Name != "shared")
            throw new InvalidOperationException($"references/{contender}: the source graph was modified.");
    }
}

// ============================================================================
// E3 mapping families (runtime, manual and competitor methods). Generated and Mapperly methods: GeneratedMappingBenchmarks.cs.
// ============================================================================

/// <summary>Flat payload: ten scalars, no nesting.</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mapping")]
public partial class E3MappingFlat
{
    private FlatSource _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;
    private MeridianAlias.IMapper _generatedRuntime = default!;
    private global::MapsterMapper.Mapper _mapster = default!;

    [GlobalSetup]
    public void Setup()
    {
        _source = FlatPayload.Create();
        (_compiled, _interpreted) = NestedPathSupport.Create(cfg => cfg.CreateMap<FlatSource, FlatDest>(), expectedCompiledMaps: 1);
        _generatedRuntime = Generated.GeneratedMappingSupport.CreateRuntimeMapper(new Generated.E3GeneratedFlatMaps.GeneratedResolver());

        var mapster = new Mapster.TypeAdapterConfig();
        mapster.NewConfig<FlatSource, FlatDest>();
        mapster.Compile();
        _mapster = new global::MapsterMapper.Mapper(mapster);
        Preflight();
    }

    /// <summary>Runs every contender once and verifies it against the independently specified expectation.</summary>
    public void Preflight()
    {
        FlatPayload.Verify(nameof(Manual), _source, Manual());
        FlatPayload.Verify(nameof(MeridianInterpreted), _source, MeridianInterpreted());
        FlatPayload.Verify(nameof(MeridianCompiled), _source, MeridianCompiled());
        FlatPayload.Verify(nameof(MeridianGeneratedDirect), _source, MeridianGeneratedDirect());
        FlatPayload.Verify(nameof(MeridianGeneratedRuntime), _source, MeridianGeneratedRuntime());
        FlatPayload.Verify(nameof(Mapster), _source, Mapster());
        FlatPayload.Verify(nameof(Mapperly), _source, Mapperly());
    }

    [Benchmark(Baseline = true)]
    public FlatDest Manual() => FlatPayload.ManualMap(_source);

    [Benchmark]
    public FlatDest MeridianInterpreted() => _interpreted.Map<FlatSource, FlatDest>(_source);

    [Benchmark]
    public FlatDest MeridianCompiled() => _compiled.Map<FlatSource, FlatDest>(_source);

    [Benchmark]
    public FlatDest MeridianGeneratedRuntime() => _generatedRuntime.Map<FlatSource, FlatDest>(_source);

    [Benchmark]
    public FlatDest Mapster() => _mapster.Map<FlatSource, FlatDest>(_source);
}

/// <summary>An order with ten renamed scalar members and one nested address (the original April benchmark shape).</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mapping")]
public partial class E3MappingNested
{
    private SourceOrder _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;
    private MeridianAlias.IMapper _generatedRuntime = default!;
    private global::MapsterMapper.Mapper _mapster = default!;

    [GlobalSetup]
    public void Setup()
    {
        _source = OrderPayload.Create();
        (_compiled, _interpreted) = NestedPathSupport.Create(OrderPayload.ConfigureMeridian, expectedCompiledMaps: 2);
        _generatedRuntime = Generated.GeneratedMappingSupport.CreateRuntimeMapper(new Generated.E3GeneratedOrderMaps.GeneratedResolver());

        _mapster = new global::MapsterMapper.Mapper(OrderPayload.CreateMapsterConfig());
        Preflight();
    }

    public void Preflight()
    {
        OrderPayload.Verify(nameof(Manual), _source, Manual());
        OrderPayload.Verify(nameof(MeridianInterpreted), _source, MeridianInterpreted());
        OrderPayload.Verify(nameof(MeridianCompiled), _source, MeridianCompiled());
        OrderPayload.Verify(nameof(MeridianGeneratedDirect), _source, MeridianGeneratedDirect());
        OrderPayload.Verify(nameof(MeridianGeneratedRuntime), _source, MeridianGeneratedRuntime());
        OrderPayload.Verify(nameof(Mapster), _source, Mapster());
        OrderPayload.Verify(nameof(Mapperly), _source, Mapperly());
    }

    [Benchmark(Baseline = true)]
    public DestOrder Manual() => OrderPayload.ManualMap(_source);

    [Benchmark]
    public DestOrder MeridianInterpreted() => _interpreted.Map<SourceOrder, DestOrder>(_source);

    [Benchmark]
    public DestOrder MeridianCompiled() => _compiled.Map<SourceOrder, DestOrder>(_source);

    [Benchmark]
    public DestOrder MeridianGeneratedRuntime() => _generatedRuntime.Map<SourceOrder, DestOrder>(_source);

    [Benchmark]
    public DestOrder Mapster() => _mapster.Map<SourceOrder, DestOrder>(_source);
}

/// <summary>
/// A basket of 0, 1, 32 or 256 lines, each with a nested product. The root has a collection destination, so Meridian keeps it
/// on the interpreter in every runtime mode; the per-line maps are the eligible compiled maps.
/// </summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mapping")]
public partial class E3MappingCollectionPayload
{
    private Basket _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;
    private MeridianAlias.IMapper _generatedRuntime = default!;
    private global::MapsterMapper.Mapper _mapster = default!;

    [Params(0, 1, 32, 256)]
    public int Items { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _source = BasketPayload.Create(Items);
        (_compiled, _interpreted) = NestedPathSupport.Create(BasketPayload.Configure, expectedCompiledMaps: 2);
        _generatedRuntime = Generated.GeneratedMappingSupport.CreateRuntimeMapper(new Generated.E3GeneratedBasketMaps.GeneratedResolver());

        var mapster = new Mapster.TypeAdapterConfig();
        mapster.NewConfig<BasketProduct, BasketProductDto>();
        mapster.NewConfig<BasketLine, BasketLineDto>();
        mapster.NewConfig<Basket, BasketDto>();
        mapster.Compile();
        _mapster = new global::MapsterMapper.Mapper(mapster);
        Preflight();
    }

    public void Preflight()
    {
        BasketPayload.Verify(nameof(Manual), Items, _source, Manual());
        BasketPayload.Verify(nameof(MeridianInterpreted), Items, _source, MeridianInterpreted());
        BasketPayload.Verify(nameof(MeridianCompiled), Items, _source, MeridianCompiled());
        BasketPayload.Verify(nameof(MeridianGeneratedDirect), Items, _source, MeridianGeneratedDirect());
        BasketPayload.Verify(nameof(MeridianGeneratedRuntime), Items, _source, MeridianGeneratedRuntime());
        BasketPayload.Verify(nameof(Mapster), Items, _source, Mapster());
        BasketPayload.Verify(nameof(Mapperly), Items, _source, Mapperly());
    }

    [Benchmark(Baseline = true)]
    public BasketDto Manual() => BasketPayload.ManualMap(_source);

    [Benchmark]
    public BasketDto MeridianInterpreted() => _interpreted.Map<Basket, BasketDto>(_source);

    [Benchmark]
    public BasketDto MeridianCompiled() => _compiled.Map<Basket, BasketDto>(_source);

    [Benchmark]
    public BasketDto MeridianGeneratedRuntime() => _generatedRuntime.Map<Basket, BasketDto>(_source);

    [Benchmark]
    public BasketDto Mapster() => _mapster.Map<Basket, BasketDto>(_source);
}

/// <summary>A chain of 4 or 16 nested objects, each its own explicit pair (bounded far below the depth cap of 64).</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mapping")]
public partial class E3MappingDeep
{
    private S01 _source = default!;
    private MeridianAlias.IMapper _compiled = default!;
    private MeridianAlias.IMapper _interpreted = default!;
    private MeridianAlias.IMapper _generatedRuntime = default!;
    private global::MapsterMapper.Mapper _mapster = default!;

    [Params(4, 16)]
    public int Depth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _source = DeepChain.BuildDeepSource(Depth);
        (_compiled, _interpreted) = NestedPathSupport.Create(DeepChain.Configure, expectedCompiledMaps: 16);
        _generatedRuntime = Generated.GeneratedMappingSupport.CreateRuntimeMapper(new Generated.E3GeneratedDeepMaps.GeneratedResolver());

        var mapster = new Mapster.TypeAdapterConfig();
        foreach (var (source, destination) in DeepChain.Pairs)
            mapster.NewConfig(source, destination);
        mapster.Compile();
        _mapster = new global::MapsterMapper.Mapper(mapster);
        Preflight();
    }

    public void Preflight()
    {
        DeepChain.Verify(nameof(Manual), Depth, _source, Manual());
        DeepChain.Verify(nameof(MeridianInterpreted), Depth, _source, MeridianInterpreted());
        DeepChain.Verify(nameof(MeridianCompiled), Depth, _source, MeridianCompiled());
        DeepChain.Verify(nameof(MeridianGeneratedDirect), Depth, _source, MeridianGeneratedDirect());
        DeepChain.Verify(nameof(MeridianGeneratedRuntime), Depth, _source, MeridianGeneratedRuntime());
        DeepChain.Verify(nameof(Mapster), Depth, _source, Mapster());
        DeepChain.Verify(nameof(Mapperly), Depth, _source, Mapperly());
    }

    [Benchmark(Baseline = true)]
    public D01 Manual() => DeepChain.ManualD01(_source);

    [Benchmark]
    public D01 MeridianInterpreted() => _interpreted.Map<S01, D01>(_source);

    [Benchmark]
    public D01 MeridianCompiled() => _compiled.Map<S01, D01>(_source);

    [Benchmark]
    public D01 MeridianGeneratedRuntime() => _generatedRuntime.Map<S01, D01>(_source);

    [Benchmark]
    public D01 Mapster() => _mapster.Map<S01, D01>(_source);
}

/// <summary>
/// Repeated-reference graph: every contender preserves source reference identity inside one map operation (the same source
/// node maps to one destination node; an equal-valued but distinct source node maps to a distinct destination node). The
/// hand-written floor implements the same identity tracking. Reference preservation keeps Meridian on the interpreter, which
/// is asserted, so there is a single runtime row. Generated runtime integration is omitted for this shape: only the direct
/// generated call is timed (see the results README).
/// </summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mapping")]
public partial class E3MappingReferences
{
    private RefGraph _source = default!;
    private MeridianAlias.IMapper _runtime = default!;
    private global::MapsterMapper.Mapper _mapster = default!;

    [GlobalSetup]
    public void Setup()
    {
        _source = ReferencePayload.Create();
        (_runtime, _) = NestedPathSupport.Create(cfg =>
        {
            cfg.CreateMap<RefNode, RefNodeDto>().PreserveReferences();
            cfg.CreateMap<RefGraph, RefGraphDto>().PreserveReferences();
        }, expectedCompiledMaps: 0);

        var mapster = new Mapster.TypeAdapterConfig();
        mapster.NewConfig<RefNode, RefNodeDto>().PreserveReference(true);
        mapster.NewConfig<RefGraph, RefGraphDto>().PreserveReference(true);
        mapster.Compile();
        _mapster = new global::MapsterMapper.Mapper(mapster);
        Preflight();
    }

    public void Preflight()
    {
        ReferencePayload.Verify(nameof(Manual), _source, Manual());
        ReferencePayload.Verify(nameof(MeridianRuntime), _source, MeridianRuntime());
        ReferencePayload.Verify(nameof(MeridianGeneratedDirect), _source, MeridianGeneratedDirect());
        ReferencePayload.Verify(nameof(Mapster), _source, Mapster());
        ReferencePayload.Verify(nameof(Mapperly), _source, Mapperly());
    }

    [Benchmark(Baseline = true)]
    public RefGraphDto Manual() => ReferencePayload.ManualMap(_source);

    [Benchmark]
    public RefGraphDto MeridianRuntime() => _runtime.Map<RefGraph, RefGraphDto>(_source);

    [Benchmark]
    public RefGraphDto Mapster() => _mapster.Map<RefGraph, RefGraphDto>(_source);
}

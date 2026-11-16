using BenchmarkDotNet.Attributes;
using Meridian.Benchmarks.Generated;
using MeridianAlias = Meridian.Mapping;
using Riok.Mapperly.Abstractions;

// ============================================================================
// Generated mapping contenders for the E3 mapping families.
//
//  * Meridian generated, DIRECT: a call of the Structural-mode generated static method (no IMapper). Structural mode is
//    the only generated mode that does the same work as the other contenders (deep-copies the declared nested pairs and
//    collections); the default Legacy mode aliases nested references and is deliberately not benchmarked as equal work.
//  * Meridian generated, RUNTIME INTEGRATION: the same pairs reached through IMapper with UseGeneratedMappings
//    (a separate method; it measures the resolver bridge, not the direct call).
//  * Mapperly 4.3.1 (Apache-2.0): static partial mappers generated at compile time.
// Generator output for both is produced during compilation of this project only; neither generator is a runtime dependency.
// ============================================================================

namespace Meridian.Benchmarks.Generated
{
    internal static class GeneratedMappingSupport
    {
        /// <summary>A runtime mapper whose only mappings are the generated descriptors (no explicit TypeMaps).</summary>
        public static MeridianAlias.IMapper CreateRuntimeMapper(MeridianAlias.IGeneratedMappingResolver resolver)
        {
            var configuration = new MeridianAlias.MapperConfiguration(cfg => MeridianAlias.GeneratedMappingExtensions.UseGeneratedMappings(cfg, resolver));
            configuration.AssertConfigurationIsValid();
            if (configuration.GetAllTypeMaps().Count != 0)
                throw new InvalidOperationException("The generated runtime integration row must not contain explicit runtime TypeMaps.");
            return configuration.CreateMapper();
        }
    }

    [MeridianAlias.GenerateMapper(typeof(FlatSource), typeof(FlatDest), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    public static partial class E3GeneratedFlatMaps
    {
    }

    [MeridianAlias.GenerateMapper(typeof(SourceAddress), typeof(DestAddress), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(SourceOrder), typeof(DestOrder), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.MapMember("OrderId", "Id")]
    [MeridianAlias.MapMember("Number", "OrderNumber")]
    [MeridianAlias.MapMember("Customer", "CustomerName")]
    [MeridianAlias.MapMember("Email", "CustomerEmail")]
    [MeridianAlias.MapMember("Created", "CreatedUtc")]
    [MeridianAlias.MapMember("Updated", "UpdatedUtc")]
    [MeridianAlias.MapMember("Gross", "TotalGross")]
    [MeridianAlias.MapMember("Net", "TotalNet")]
    [MeridianAlias.MapMember("Vat", "VatRate")]
    [MeridianAlias.MapMember("ShipTo", "ShippingAddress")]
    public static partial class E3GeneratedOrderMaps
    {
    }

    [MeridianAlias.GenerateMapper(typeof(BasketProduct), typeof(BasketProductDto), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(BasketLine), typeof(BasketLineDto), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(Basket), typeof(BasketDto), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    public static partial class E3GeneratedBasketMaps
    {
    }

    [MeridianAlias.GenerateMapper(typeof(S01), typeof(D01), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S02), typeof(D02), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S03), typeof(D03), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S04), typeof(D04), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S05), typeof(D05), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S06), typeof(D06), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S07), typeof(D07), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S08), typeof(D08), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S09), typeof(D09), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S10), typeof(D10), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S11), typeof(D11), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S12), typeof(D12), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S13), typeof(D13), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S14), typeof(D14), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S15), typeof(D15), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    [MeridianAlias.GenerateMapper(typeof(S16), typeof(D16), Mode = MeridianAlias.GeneratedMappingMode.Structural, GenerateResolver = true)]
    public static partial class E3GeneratedDeepMaps
    {
    }

    [MeridianAlias.GenerateMapper(typeof(RefNode), typeof(RefNodeDto), Mode = MeridianAlias.GeneratedMappingMode.Structural, PreserveReferences = true)]
    [MeridianAlias.GenerateMapper(typeof(RefGraph), typeof(RefGraphDto), Mode = MeridianAlias.GeneratedMappingMode.Structural, PreserveReferences = true)]
    public static partial class E3GeneratedReferenceMaps
    {
    }

    [Mapper]
    public static partial class E3MapperlyFlat
    {
        public static partial FlatDest Map(FlatSource source);
    }

    [Mapper]
    public static partial class E3MapperlyOrder
    {
        [MapProperty(nameof(SourceOrder.Id), nameof(DestOrder.OrderId))]
        [MapProperty(nameof(SourceOrder.OrderNumber), nameof(DestOrder.Number))]
        [MapProperty(nameof(SourceOrder.CustomerName), nameof(DestOrder.Customer))]
        [MapProperty(nameof(SourceOrder.CustomerEmail), nameof(DestOrder.Email))]
        [MapProperty(nameof(SourceOrder.CreatedUtc), nameof(DestOrder.Created))]
        [MapProperty(nameof(SourceOrder.UpdatedUtc), nameof(DestOrder.Updated))]
        [MapProperty(nameof(SourceOrder.TotalGross), nameof(DestOrder.Gross))]
        [MapProperty(nameof(SourceOrder.TotalNet), nameof(DestOrder.Net))]
        [MapProperty(nameof(SourceOrder.VatRate), nameof(DestOrder.Vat))]
        [MapProperty(nameof(SourceOrder.ShippingAddress), nameof(DestOrder.ShipTo))]
        public static partial DestOrder Map(SourceOrder source);
    }

    [Mapper]
    public static partial class E3MapperlyBasket
    {
        public static partial BasketDto Map(Basket source);
    }

    [Mapper]
    public static partial class E3MapperlyDeep
    {
        public static partial D01 Map(S01 source);
    }

    [Mapper(UseReferenceHandling = true)]
    public static partial class E3MapperlyReferences
    {
        public static partial RefGraphDto Map(RefGraph source);
    }
}

namespace Meridian.Benchmarks
{
    public partial class E3MappingFlat
    {
        [Benchmark]
        public FlatDest MeridianGeneratedDirect() => E3GeneratedFlatMaps.MapToFlatDest(_source);

        [Benchmark]
        public FlatDest Mapperly() => E3MapperlyFlat.Map(_source);
    }

    public partial class E3MappingNested
    {
        [Benchmark]
        public DestOrder MeridianGeneratedDirect() => E3GeneratedOrderMaps.MapToDestOrder(_source);

        [Benchmark]
        public DestOrder Mapperly() => E3MapperlyOrder.Map(_source);
    }

    public partial class E3MappingCollectionPayload
    {
        [Benchmark]
        public BasketDto MeridianGeneratedDirect() => E3GeneratedBasketMaps.MapToBasketDto(_source);

        [Benchmark]
        public BasketDto Mapperly() => E3MapperlyBasket.Map(_source);
    }

    public partial class E3MappingDeep
    {
        [Benchmark]
        public D01 MeridianGeneratedDirect() => E3GeneratedDeepMaps.MapToD01(_source);

        [Benchmark]
        public D01 Mapperly() => E3MapperlyDeep.Map(_source);
    }

    public partial class E3MappingReferences
    {
        [Benchmark]
        public RefGraphDto MeridianGeneratedDirect() => E3GeneratedReferenceMaps.MapToRefGraphDto(_source);

        [Benchmark]
        public RefGraphDto Mapperly() => E3MapperlyReferences.Map(_source);
    }
}

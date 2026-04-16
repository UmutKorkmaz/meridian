using AutoMapper;
using BenchmarkDotNet.Attributes;
using MeridianAlias = Meridian.Mapping;

namespace Meridian.Benchmarks;

/// <summary>
/// Compares Meridian.Mapping against AutoMapper v14 (last MIT) and a
/// hand-written baseline on a representative line-of-business payload:
/// flat-to-flat DTO with ~10 scalar properties plus one nested object,
/// mapped via <c>ForMember + MapFrom</c> only — the dominant pattern
/// in real-world AutoMapper usage.
/// </summary>
/// <remarks>
/// Expectations (from PLAN.md §4 Phase 3 exit criteria):
/// Meridian within 2× of hand-written and Mapperly; faster than AutoMapper v14.
/// </remarks>
[MemoryDiagnoser]
public class MappingBenchmarks
{
    private SourceOrder _source = default!;
    private MeridianAlias.IMapper _meridian = default!;
    private AutoMapper.IMapper _autoMapper = default!;

    [GlobalSetup]
    public void Setup()
    {
        _source = new SourceOrder
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
                Line1 = "Maslak Mah.",
                Line2 = "1453 Sk. No:2",
                City = "Istanbul",
                PostalCode = "34485",
                Country = "TR",
            },
        };

        var meridianConfig = new MeridianAlias.MapperConfiguration(cfg =>
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
        });
        _meridian = meridianConfig.CreateMapper();

        var autoMapperConfig = new AutoMapper.MapperConfiguration(cfg =>
        {
            cfg.CreateMap<SourceAddress, DestAddress>();
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
        });
        _autoMapper = autoMapperConfig.CreateMapper();
    }

    [Benchmark(Baseline = true, Description = "Hand-written mapping (floor)")]
    public DestOrder Manual()
    {
        return new DestOrder
        {
            OrderId = _source.Id,
            Number = _source.OrderNumber,
            Customer = _source.CustomerName,
            Email = _source.CustomerEmail,
            Created = _source.CreatedUtc,
            Updated = _source.UpdatedUtc,
            Status = _source.Status,
            Gross = _source.TotalGross,
            Net = _source.TotalNet,
            Vat = _source.VatRate,
            ShipTo = new DestAddress
            {
                Line1 = _source.ShippingAddress.Line1,
                Line2 = _source.ShippingAddress.Line2,
                City = _source.ShippingAddress.City,
                PostalCode = _source.ShippingAddress.PostalCode,
                Country = _source.ShippingAddress.Country,
            },
        };
    }

    [Benchmark(Description = "Meridian.Mapping")]
    public DestOrder Meridian() =>
        _meridian.Map<SourceOrder, DestOrder>(_source);

    [Benchmark(Description = "AutoMapper v14 (last MIT)")]
    public DestOrder AutoMapper() =>
        _autoMapper.Map<SourceOrder, DestOrder>(_source);
}

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
    public SourceAddress ShippingAddress { get; set; } = new();
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
    public DestAddress ShipTo { get; set; } = new();
}

public class DestAddress
{
    public string Line1 { get; set; } = "";
    public string Line2 { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
}

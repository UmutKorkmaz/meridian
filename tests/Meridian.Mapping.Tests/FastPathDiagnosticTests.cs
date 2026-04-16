using Meridian.Mapping;
using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Tests;

/// <summary>
/// Temporary diagnostic tests to verify <see cref="FastPathCompiler"/>
/// actually produces a delegate for benchmark-shaped TypeMaps.
/// </summary>
public class FastPathDiagnosticTests
{
    public class SrcFlat
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class DstFlat
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class SrcWithNested
    {
        public int Id { get; set; }
        public SrcFlat Inner { get; set; } = new();
    }

    public class DstWithNested
    {
        public int Id { get; set; }
        public DstFlat Inner { get; set; } = new();
    }

    [Fact]
    public void Flat_Map_Without_MapFrom_Should_Have_FastPath()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcFlat, DstFlat>());
        var provider = (IConfigurationProvider)cfg;
        var tm = provider.FindTypeMap(typeof(SrcFlat), typeof(DstFlat));
        Assert.NotNull(tm);
        var rejection = FastPathCompiler.DescribeRejection(tm!, provider.AllowNullDestinationValues, hasValueTransformers: false);
        Assert.True(tm!.CompiledFastPath != null, $"Fast path not compiled. Rejection reason: {rejection ?? "none (but delegate is null — Expression.Lambda threw)"}");
    }

    [Fact]
    public void Flat_Map_With_MapFrom_Should_Have_FastPath()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcFlat, DstFlat>()
                .ForMember(d => d.Id, o => o.MapFrom(s => s.Id))
                .ForMember(d => d.Name, o => o.MapFrom(s => s.Name)));
        var provider = (IConfigurationProvider)cfg;
        var tm = provider.FindTypeMap(typeof(SrcFlat), typeof(DstFlat));
        Assert.NotNull(tm);
        Assert.NotNull(tm!.CompiledFastPath);
    }

    [Fact]
    public void Nested_Map_Should_Have_FastPath_On_Both_TypeMaps()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<SrcFlat, DstFlat>();
            c.CreateMap<SrcWithNested, DstWithNested>()
                .ForMember(d => d.Id, o => o.MapFrom(s => s.Id))
                .ForMember(d => d.Inner, o => o.MapFrom(s => s.Inner));
        });
        var provider = (IConfigurationProvider)cfg;

        var inner = provider.FindTypeMap(typeof(SrcFlat), typeof(DstFlat));
        var outer = provider.FindTypeMap(typeof(SrcWithNested), typeof(DstWithNested));

        Assert.NotNull(inner);
        Assert.NotNull(outer);
        Assert.NotNull(inner!.CompiledFastPath);
        Assert.NotNull(outer!.CompiledFastPath);
    }

    [Fact]
    public void Flat_FastPath_Produces_Same_Result_As_Interpreter()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcFlat, DstFlat>()
                .ForMember(d => d.Id, o => o.MapFrom(s => s.Id))
                .ForMember(d => d.Name, o => o.MapFrom(s => s.Name)));
        var mapper = cfg.CreateMapper();

        var source = new SrcFlat { Id = 42, Name = "hello" };
        var result = mapper.Map<SrcFlat, DstFlat>(source);

        Assert.Equal(42, result.Id);
        Assert.Equal("hello", result.Name);
    }

}

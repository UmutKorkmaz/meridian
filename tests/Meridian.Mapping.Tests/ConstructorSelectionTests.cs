namespace Meridian.Mapping.Tests;

public class ConstructorSelectionTests
{
    private static IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Chooses_Narrower_Constructor_When_Widest_Has_Unmatched_Parameters()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<NarrowSource, MultiCtorTarget>());

        var result = mapper.Map<NarrowSource, MultiCtorTarget>(new NarrowSource
        {
            Name = "Meridian",
            Age = 7
        });

        Assert.Equal("two", result.ConstructorUsed);
        Assert.Equal("Meridian", result.Name);
        Assert.Equal(7, result.Age);
    }

    [Fact]
    public void Chooses_Widest_Constructor_When_All_Parameters_Are_Resolved()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<WideSource, MultiCtorTarget>());

        var tenantId = Guid.NewGuid();
        var result = mapper.Map<WideSource, MultiCtorTarget>(new WideSource
        {
            Name = "Meridian",
            Age = 7,
            TenantId = tenantId
        });

        Assert.Equal("three", result.ConstructorUsed);
        Assert.Equal(tenantId, result.TenantId);
    }

    [Fact]
    public void Optional_Parameters_Count_As_Resolved_Bindings()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<OptionalSource, OptionalCtorTarget>());

        var result = mapper.Map<OptionalSource, OptionalCtorTarget>(new OptionalSource
        {
            Name = "Meridian"
        });

        Assert.Equal("Meridian", result.Name);
        Assert.Equal(42, result.Age);
        Assert.Equal("optional", result.ConstructorUsed);
    }

    private sealed class NarrowSource
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private sealed class WideSource
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public Guid TenantId { get; set; }
    }

    private sealed class OptionalSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class MultiCtorTarget
    {
        public string Name { get; }
        public int Age { get; }
        public Guid TenantId { get; }
        public string ConstructorUsed { get; }

        public MultiCtorTarget(string name, int age)
        {
            Name = name;
            Age = age;
            ConstructorUsed = "two";
        }

        public MultiCtorTarget(string name, int age, Guid tenantId)
        {
            Name = name;
            Age = age;
            TenantId = tenantId;
            ConstructorUsed = "three";
        }
    }

    private sealed class OptionalCtorTarget
    {
        public string Name { get; }
        public int Age { get; }
        public string ConstructorUsed { get; }

        public OptionalCtorTarget(string name, int age = 42)
        {
            Name = name;
            Age = age;
            ConstructorUsed = "optional";
        }
    }
}

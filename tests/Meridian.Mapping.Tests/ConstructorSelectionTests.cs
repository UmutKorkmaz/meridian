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
    [Fact]
    public void MissingRequiredConstructorArgumentsFailInsteadOfSilentlyDefaulting()
    {
        var mapper = CreateMapper(c => c.CreateMap<OptionalSource, RequiredCtorTarget>());
        var failure = Assert.Throws<InvalidOperationException>(() =>
            mapper.Map<OptionalSource, RequiredCtorTarget>(new() { Name = "Meridian" }));
        Assert.Contains(nameof(RequiredCtorTarget), failure.Message);
        Assert.Contains("ForCtorParam", failure.Message);
        Assert.Contains("ConstructUsing", failure.Message);
    }
    public sealed class RequiredCtorTarget
    {
        public RequiredCtorTarget(string name, Guid tenantId) { Name = name; TenantId = tenantId; }
        public string Name { get; }
        public Guid TenantId { get; }
    }
    [Fact]
    public void MissingRequiredReferenceConstructorArgumentFails()
    {
        var mapper = CreateMapper(c => c.CreateMap<OptionalSource, RequiredReferenceTarget>());
        Assert.Throws<InvalidOperationException>(() =>
            mapper.Map<OptionalSource, RequiredReferenceTarget>(new() { Name = "Meridian" }));
    }

    [Fact]
    public void ExplicitConstructorArgumentAndConstructionRemainAvailable()
    {
        var tenant = Guid.Parse("b72d245d-e40c-4f84-b5dd-b8e6d16fafbf");
        var mapper = CreateMapper(c =>
        {
            c.CreateMap<OptionalSource, RequiredCtorTarget>()
                .ForCtorParam("tenantId", o => o.MapFrom(_ => tenant));
            c.CreateMap<OptionalSource, RequiredReferenceTarget>()
                .ConstructUsing(s => new RequiredReferenceTarget(s.Name, "explicit"));
        });
        var source = new OptionalSource { Name = "Meridian" };
        var byArgument = mapper.Map<OptionalSource, RequiredCtorTarget>(source);
        Assert.Equal("Meridian", byArgument.Name);
        Assert.Equal(tenant, byArgument.TenantId);
        var byFactory = mapper.Map<OptionalSource, RequiredReferenceTarget>(source);
        Assert.Equal("Meridian", byFactory.Name);
        Assert.Equal("explicit", byFactory.Required);
    }

    [Fact]
    public void DirectObjectCreationRetainsItsExplicitFallbackContract()
    {
        var created = Assert.IsType<RequiredCtorTarget>(
            Meridian.Mapping.Execution.ObjectCreator.CreateInstance(typeof(RequiredCtorTarget)));
        Assert.Null(created.Name);
        Assert.Equal(Guid.Empty, created.TenantId);
    }
    public sealed class RequiredReferenceTarget
    {
        public RequiredReferenceTarget(string name, string required) { Name = name; Required = required; }
        public string Name { get; }
        public string Required { get; }
    }
}

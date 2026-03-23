namespace Meridian.Mapping.Tests;

public class MappingPlanTests
{
    [Fact]
    public void GetMappingPlan_ShowsPropertyMappings()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>();
        });

        var plan = config.GetMappingPlan<Source, Destination>();

        Assert.Contains("Source → Destination", plan);
        Assert.Contains("Name ← Name", plan);
        Assert.Contains("Age ← Age", plan);
    }

    [Fact]
    public void GetMappingPlan_ShowsIgnoredMembers()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
               .ForMember(d => d.Age, opt => opt.Ignore());
        });

        var plan = config.GetMappingPlan<Source, Destination>();

        Assert.Contains("Age ← [Ignored]", plan);
    }

    [Fact]
    public void GetMappingPlan_ShowsCustomExpressions()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
               .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.ToUpper()));
        });

        var plan = config.GetMappingPlan<Source, Destination>();

        Assert.Contains("Name ← Expression(", plan);
    }

    [Fact]
    public void GetMappingPlan_ShowsConditions()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
               .ForMember(d => d.Age, opt =>
               {
                   opt.PreCondition(s => s.Age > 0);
                   opt.Condition(s => s.Age < 200);
               });
        });

        var plan = config.GetMappingPlan<Source, Destination>();

        Assert.Contains("PreCondition: [set]", plan);
        Assert.Contains("Condition: [set]", plan);
    }

    [Fact]
    public void GetMappingPlan_ShowsConstantValues()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
               .ForMember(d => d.Name, opt => opt.MapFrom(_ => "Hello"));
        });

        var plan = config.GetMappingPlan<Source, Destination>();

        // Should show as an expression since MapFrom with lambda creates a custom expression
        Assert.Contains("Name ←", plan);
    }

    [Fact]
    public void GetMappingPlan_ShowsFlattenedProperties()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<FlattenSource, FlattenDest>();
        });

        var plan = config.GetMappingPlan<FlattenSource, FlattenDest>();

        Assert.Contains("AddressStreet ← Address.Street (flattened)", plan);
    }

    [Fact]
    public void GetMappingPlan_ReturnsMessage_ForUnmappedPair()
    {
        var config = new MapperConfiguration(cfg => { });

        var plan = config.GetMappingPlan<Source, Destination>();

        Assert.Contains("No mapping configured", plan);
    }

    [Fact]
    public void GetMappingPlan_ShowsBeforeAfterMapActions()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
               .BeforeMap((s, d) => { })
               .AfterMap((s, d) => { });
        });

        var plan = config.GetMappingPlan<Source, Destination>();

        Assert.Contains("BeforeMap: 1 action(s)", plan);
        Assert.Contains("AfterMap: 1 action(s)", plan);
    }

    #region Test models for flattening
    public class FlattenSource
    {
        public FlattenAddress Address { get; set; } = new();
    }

    public class FlattenAddress
    {
        public string Street { get; set; } = "";
    }

    public class FlattenDest
    {
        public string AddressStreet { get; set; } = "";
    }
    #endregion
}

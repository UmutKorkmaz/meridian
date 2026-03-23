namespace Meridian.Mapping.Tests;

public class ConstructorMappingTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_ConstructUsing_Custom_Factory()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, ImmutableDest>()
                .ConstructUsing(s => new ImmutableDest(s.Name, s.Age));
        });

        var source = new Source { Name = "Immutable", Age = 42 };
        var result = mapper.Map<Source, ImmutableDest>(source);

        Assert.Equal("Immutable", result.Name);
        Assert.Equal(42, result.Age);
    }

    [Fact]
    public void Should_ForCtorParam_Map_To_Constructor_Parameter()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<EmployeeSource, ImmutableDest>()
                .ForCtorParam("name", opt => opt.MapFrom(s => s.FullName))
                .ForCtorParam("age", opt => opt.MapFrom(s => s.YearsExperience));
        });

        var source = new EmployeeSource { FullName = "Jane", YearsExperience = 10 };
        var result = mapper.Map<EmployeeSource, ImmutableDest>(source);

        Assert.Equal("Jane", result.Name);
        Assert.Equal(10, result.Age);
    }

    [Fact]
    public void Should_Auto_Detect_Best_Constructor_By_Matching_Names()
    {
        // Source has Name and Age properties, ImmutableDest has (string name, int age) ctor
        // The engine should automatically match parameter names to source properties
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, ImmutableDest>();
        });

        var source = new Source { Name = "AutoCtor", Age = 30 };
        var result = mapper.Map<Source, ImmutableDest>(source);

        Assert.Equal("AutoCtor", result.Name);
        Assert.Equal(30, result.Age);
    }

    [Fact]
    public void Should_Use_Constructor_With_Most_Matching_Params()
    {
        // MultiCtorDest has (string name) and (string name, int value) constructors
        // Source has Name and no Value property, so the (string name) ctor gets used via fallback
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, MultiCtorDest>();
        });

        var source = new Source { Name = "Multi" };
        var result = mapper.Map<Source, MultiCtorDest>(source);

        Assert.Equal("Multi", result.Name);
    }
}

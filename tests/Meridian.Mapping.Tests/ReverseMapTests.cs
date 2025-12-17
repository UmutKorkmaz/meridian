namespace Meridian.Mapping.Tests;

public class ReverseMapTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Create_Inverse_Mapping()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>().ReverseMap();
        });

        var dest = new Destination { Id = 1, Name = "Reverse", Email = "r@e.com", Age = 40 };
        var result = mapper.Map<Destination, Source>(dest);

        Assert.Equal(1, result.Id);
        Assert.Equal("Reverse", result.Name);
        Assert.Equal("r@e.com", result.Email);
        Assert.Equal(40, result.Age);
    }

    [Fact]
    public void Should_Support_Bidirectional_Mapping()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>().ReverseMap();
        });

        // Forward
        var source = new Source { Id = 1, Name = "Forward" };
        var dest = mapper.Map<Source, Destination>(source);
        Assert.Equal("Forward", dest.Name);

        // Reverse
        var reversed = mapper.Map<Destination, Source>(dest);
        Assert.Equal(1, reversed.Id);
        Assert.Equal("Forward", reversed.Name);
    }

    [Fact]
    public void Should_ReverseMap_With_Simple_ForMember()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<EmployeeSource, EmployeeDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.FullName))
                .ForMember(d => d.JobTitle, opt => opt.MapFrom(s => s.Title))
                .ForMember(d => d.Experience, opt => opt.MapFrom(s => s.YearsExperience))
                .ReverseMap();
        });

        // Forward
        var source = new EmployeeSource { FullName = "John", Title = "Eng", YearsExperience = 5 };
        var dest = mapper.Map<EmployeeSource, EmployeeDest>(source);
        Assert.Equal("John", dest.Name);

        // Reverse: ReverseMap should wire Name -> FullName, JobTitle -> Title, Experience -> YearsExperience
        var reversed = mapper.Map<EmployeeDest, EmployeeSource>(dest);
        Assert.Equal("John", reversed.FullName);
        Assert.Equal("Eng", reversed.Title);
        Assert.Equal(5, reversed.YearsExperience);
    }
}

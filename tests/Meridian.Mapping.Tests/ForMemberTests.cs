namespace Meridian.Mapping.Tests;

public class ForMemberTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_MapFrom_With_Lambda_Expression()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<EmployeeSource, EmployeeDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.FullName))
                .ForMember(d => d.JobTitle, opt => opt.MapFrom(s => s.Title))
                .ForMember(d => d.Experience, opt => opt.MapFrom(s => s.YearsExperience));
        });

        var source = new EmployeeSource { FullName = "Alice", Title = "Dev", YearsExperience = 3 };
        var result = mapper.Map<EmployeeSource, EmployeeDest>(source);

        Assert.Equal("Alice", result.Name);
        Assert.Equal("Dev", result.JobTitle);
        Assert.Equal(3, result.Experience);
    }

    [Fact]
    public void Should_Ignore_Member()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Email, opt => opt.Ignore());
        });

        var source = new Source { Id = 1, Name = "Test", Email = "should@ignore.com", Age = 25 };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(1, result.Id);
        Assert.Equal("Test", result.Name);
        Assert.Equal(string.Empty, result.Email); // Ignored, keeps default initializer
        Assert.Equal(25, result.Age);
    }

    [Fact]
    public void Should_Apply_Condition_When_True()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Age, opt =>
                {
                    opt.MapFrom(s => s.Age);
                    opt.Condition(s => s.Age > 0);
                });
        });

        var source = new Source { Age = 25 };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(25, result.Age);
    }

    [Fact]
    public void Should_Skip_Member_When_Condition_Is_False()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Age, opt =>
                {
                    opt.MapFrom(s => s.Age);
                    opt.Condition(s => s.Age > 0);
                });
        });

        var source = new Source { Age = 0 };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(0, result.Age); // Default int value
    }

    [Fact]
    public void Should_Skip_Entirely_When_PreCondition_Is_False()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.PreCondition(s => !string.IsNullOrEmpty(s.Name));
                });
        });

        var source = new Source { Name = "" };
        var result = mapper.Map<Source, Destination>(source);

        // PreCondition false means mapping is skipped entirely, dest keeps its initializer
        Assert.Equal(string.Empty, result.Name);
    }

    [Fact]
    public void Should_Apply_NullSubstitute_When_Source_Is_Null()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Unknown");
                });
        });

        var source = new Source { Name = null! };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal("Unknown", result.Name);
    }

    [Fact]
    public void Should_Not_Apply_NullSubstitute_When_Source_Is_Not_Null()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Fallback");
                });
        });

        var source = new Source { Name = "Actual" };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal("Actual", result.Name);
    }

    [Fact]
    public void Should_UseValue_Set_Fixed_Value()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt => opt.UseValue("Constant"));
        });

        var source = new Source { Name = "Anything" };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal("Constant", result.Name);
    }

    [Fact]
    public void Should_MapFrom_With_Two_Args_Receive_Source_And_Dest()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt => opt.MapFrom<string>((s, d) => $"{s.Name} (ID: {s.Id})"));
        });

        var source = new Source { Id = 42, Name = "Test" };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal("Test (ID: 42)", result.Name);
    }

    [Fact]
    public void Should_Apply_ForAllMembers_With_Condition()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForAllMembers(opt => opt.Condition(s => s.Id > 0));
        });

        // Id > 0: all members should map
        var source1 = new Source { Id = 1, Name = "Yes", Age = 30 };
        var result1 = mapper.Map<Source, Destination>(source1);
        Assert.Equal(1, result1.Id);
        Assert.Equal("Yes", result1.Name);

        // Id == 0: condition fails, nothing maps
        var source2 = new Source { Id = 0, Name = "No", Age = 99 };
        var result2 = mapper.Map<Source, Destination>(source2);
        Assert.Equal(0, result2.Id); // default
        Assert.Equal(string.Empty, result2.Name); // default initializer
    }
}

namespace Meridian.Mapping.Tests;

public class BasicMappingTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Map_Simple_Properties()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());
        var source = new Source { Id = 1, Name = "Test", Email = "test@example.com", Age = 30 };

        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(1, result.Id);
        Assert.Equal("Test", result.Name);
        Assert.Equal("test@example.com", result.Email);
        Assert.Equal(30, result.Age);
    }

    [Fact]
    public void Should_Map_With_ForMember_MapFrom()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<EmployeeSource, EmployeeDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.FullName))
                .ForMember(d => d.JobTitle, opt => opt.MapFrom(s => s.Title))
                .ForMember(d => d.Experience, opt => opt.MapFrom(s => s.YearsExperience));
        });

        var source = new EmployeeSource { FullName = "John Doe", Title = "Engineer", YearsExperience = 5 };
        var result = mapper.Map<EmployeeSource, EmployeeDest>(source);

        Assert.Equal("John Doe", result.Name);
        Assert.Equal("Engineer", result.JobTitle);
        Assert.Equal(5, result.Experience);
    }

    [Fact]
    public void Should_Throw_For_Null_Source_With_Object_Overload()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        Assert.Throws<ArgumentNullException>(() => mapper.Map<Destination>((object)null!));
    }

    [Fact]
    public void Should_Return_Default_For_Null_Source_With_Generic_Overload()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var result = mapper.Map<Source, Destination>(null!);

        Assert.Null(result);
    }

    [Fact]
    public void Should_Map_To_Existing_Destination()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var source = new Source { Id = 1, Name = "Updated", Email = "new@test.com", Age = 25 };
        var existing = new Destination { Id = 99, Name = "Old", Email = "old@test.com", Age = 50 };

        var result = mapper.Map(source, existing);

        Assert.Same(existing, result);
        Assert.Equal(1, result.Id);
        Assert.Equal("Updated", result.Name);
        Assert.Equal("new@test.com", result.Email);
        Assert.Equal(25, result.Age);
    }

    [Fact]
    public void Should_Map_Nested_Objects()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<PersonSource, PersonDest>();
            cfg.CreateMap<Address, Address>();
        });

        var source = new PersonSource
        {
            Name = "Jane",
            Address = new Address { Street = "123 Main St", City = "Springfield", ZipCode = "62701" }
        };
        var result = mapper.Map<PersonSource, PersonDest>(source);

        Assert.Equal("Jane", result.Name);
        Assert.Equal("123 Main St", result.Address.Street);
        Assert.Equal("Springfield", result.Address.City);
        Assert.Equal("62701", result.Address.ZipCode);
    }

    [Fact]
    public void Should_Map_Using_Runtime_Types()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var source = new Source { Id = 42, Name = "Runtime" };
        var result = mapper.Map(source, typeof(Source), typeof(Destination));

        Assert.IsType<Destination>(result);
        Assert.Equal(42, ((Destination)result).Id);
        Assert.Equal("Runtime", ((Destination)result).Name);
    }

    [Fact]
    public void Should_Map_Using_Single_Generic_Overload()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Source, Destination>());

        var source = new Source { Id = 7, Name = "Generic" };
        var result = mapper.Map<Destination>((object)source);

        Assert.Equal(7, result.Id);
        Assert.Equal("Generic", result.Name);
    }

    [Fact]
    public void Should_Throw_For_Missing_Configuration()
    {
        var mapper = CreateMapper(cfg => { });
        var source = new Source { Id = 1 };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            mapper.Map<Source, Destination>(source));

        Assert.Contains("Missing mapping configuration", ex.Message);
        Assert.Contains("Source", ex.Message);
        Assert.Contains("Destination", ex.Message);
    }

    [Fact]
    public void Should_Expose_ConfigurationProvider()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Source, Destination>());
        var mapper = config.CreateMapper();

        Assert.NotNull(mapper.ConfigurationProvider);
        Assert.Same(config, mapper.ConfigurationProvider);
    }
}

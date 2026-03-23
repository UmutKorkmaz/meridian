namespace Meridian.Mapping.Tests;

public class FlatteningTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Flatten_PascalCase_Nested_Properties()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<PersonSource, FlatPersonDest>());

        var source = new PersonSource
        {
            Name = "John",
            Address = new Address { Street = "456 Oak Ave", City = "Portland" }
        };
        var result = mapper.Map<PersonSource, FlatPersonDest>(source);

        Assert.Equal("John", result.Name);
        Assert.Equal("456 Oak Ave", result.AddressStreet);
        Assert.Equal("Portland", result.AddressCity);
    }

    [Fact]
    public void Should_Flatten_Multi_Level_Properties()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<Company, FlatCompanyDest>());

        var source = new Company
        {
            Region = new Region
            {
                Country = new Country { Name = "Turkey" }
            }
        };
        var result = mapper.Map<Company, FlatCompanyDest>(source);

        Assert.Equal("Turkey", result.RegionCountryName);
    }

    [Fact]
    public void Should_Handle_Null_Intermediate_In_Flattening()
    {
        var mapper = CreateMapper(cfg => cfg.CreateMap<PersonSource, FlatPersonDest>());

        var source = new PersonSource
        {
            Name = "NullAddr",
            Address = null!
        };
        var result = mapper.Map<PersonSource, FlatPersonDest>(source);

        Assert.Equal("NullAddr", result.Name);
        Assert.Null(result.AddressStreet);
        Assert.Null(result.AddressCity);
    }
}

using Meridian.Mapping.Converters;

namespace Meridian.Mapping.Tests;

public class HighPriorityFeaturesTests
{
    private static IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_Map_With_String_Based_ForMember_And_MapFrom()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember("Email", opt => opt.MapFrom("Name"));
        });

        var source = new Source { Name = "alice", Email = "old@x.com" };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal("alice", result.Email);
    }

    [Fact]
    public void Should_Apply_Three_Arg_Condition_With_SourceMember()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.Condition((src, dest, srcMember) => srcMember != null && srcMember.ToString()!.Length > 0);
                });
        });

        var result1 = mapper.Map<Source, Destination>(new Source { Name = "ok" });
        var result2 = mapper.Map<Source, Destination>(new Source { Name = "" });

        Assert.Equal("ok", result1.Name);
        Assert.Equal(string.Empty, result2.Name);
    }

    [Fact]
    public void Should_Map_ForPath_Nested_Destination_And_Create_Intermediate_Object()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<UserSource, UserDestNested>()
                .ForMember(d => d.Address, opt => opt.Ignore())
                .ForPath(d => d.Address.Street, opt => opt.MapFrom(s => s.Address!.Street))
                .ForPath(d => d.Address.City, opt => opt.MapFrom(s => s.Address!.City));
        });

        var source = new UserSource
        {
            Name = "Alice",
            Address = new AddressSource { Street = "Main St", City = "Istanbul" }
        };

        var result = mapper.Map<UserSource, UserDestNested>(source);

        Assert.NotNull(result.Address);
        Assert.Equal("Main St", result.Address.Street);
        Assert.Equal("Istanbul", result.Address.City);
    }

    [Fact]
    public void Should_Map_IncludeMembers_From_Nested_Source()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<PersonWithDetailsSource, PersonDetailsDest>()
                .IncludeMembers(s => s.Details!);
        });

        var source = new PersonWithDetailsSource
        {
            Name = "Alice",
            Details = new PersonDetails { Email = "alice@example.com", Age = 30 }
        };

        var result = mapper.Map<PersonWithDetailsSource, PersonDetailsDest>(source);

        Assert.Equal("Alice", result.Name);
        Assert.Equal("alice@example.com", result.Email);
        Assert.Equal(30, result.Age);
    }

    [Fact]
    public void Should_Inherit_Base_Configuration_With_Include_And_Dispatch_Polymorphically()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<AnimalSource, AnimalDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name + "-base"))
                .Include<DogSource, DogDest>();

            cfg.CreateMap<DogSource, DogDest>()
                .ForMember(d => d.Breed, opt => opt.MapFrom(s => s.Breed));
        });

        AnimalSource source = new DogSource { Name = "Rex", Legs = 4, Breed = "Golden" };
        var result = mapper.Map<AnimalSource, AnimalDest>(source);

        Assert.IsType<DogDest>(result);
        Assert.Equal("Rex-base", result.Name);
        Assert.Equal(4, result.Legs);
        Assert.Equal("Golden", ((DogDest)result).Breed);
    }

    [Fact]
    public void Should_Include_All_Derived_Maps_And_Dispatch_Most_Specific()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<AnimalSource, AnimalDest>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name + "-base"))
                .IncludeAllDerived();

            cfg.CreateMap<DogSource, DogDest>()
                .ForMember(d => d.Breed, opt => opt.MapFrom(s => s.Breed));

            cfg.CreateMap<CatSource, CatDest>()
                .ForMember(d => d.Declawed, opt => opt.MapFrom(s => s.Declawed));
        });

        AnimalSource dog = new DogSource { Name = "Doggo", Legs = 4, Breed = "Husky" };
        AnimalSource cat = new CatSource { Name = "Kitty", Legs = 4, Declawed = true };

        var dogResult = mapper.Map<AnimalSource, AnimalDest>(dog);
        var catResult = mapper.Map<AnimalSource, AnimalDest>(cat);

        Assert.IsType<DogDest>(dogResult);
        Assert.Equal("Husky", ((DogDest)dogResult).Breed);
        Assert.Equal("Doggo-base", dogResult.Name);

        Assert.IsType<CatDest>(catResult);
        Assert.True(((CatDest)catResult).Declawed);
        Assert.Equal("Kitty-base", catResult.Name);
    }

    [Fact]
    public void Should_Apply_Member_Level_ConvertUsing_With_Converter_Instance()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BirthDateSource, BirthDateDest>()
                .ForMember(d => d.Age,
                    opt => opt.ConvertUsing(new DateToAgeConverter(), s => s.BirthDate));
        });

        var source = new BirthDateSource { BirthDate = DateTime.Today.AddYears(-25).AddDays(-1) };
        var result = mapper.Map<BirthDateSource, BirthDateDest>(source);

        Assert.InRange(result.Age, 25, 26);
    }

    [Fact]
    public void Should_Apply_Member_Level_ConvertUsing_With_Func()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<BirthDateSource, BirthDateDest>()
                .ForMember(d => d.Age,
                    opt => opt.ConvertUsing<DateTime, int>(dt => 99, s => s.BirthDate));
        });

        var result = mapper.Map<BirthDateSource, BirthDateDest>(new BirthDateSource { BirthDate = DateTime.Today });
        Assert.Equal(99, result.Age);
    }

    [Fact]
    public void Should_Select_Most_Derived_Map_When_Multiple_Derived_Exist()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<AnimalSource, AnimalDest>()
                .IncludeAllDerived();

            cfg.CreateMap<DogSource, DogDest>()
                .ForMember(d => d.Breed, opt => opt.MapFrom(s => s.Breed));
        });

        AnimalSource source = new DogSource { Name = "Bolt", Legs = 4, Breed = "Collie" };
        var result = mapper.Map<AnimalSource, AnimalDest>(source);

        Assert.IsType<DogDest>(result);
        Assert.Equal("Collie", ((DogDest)result).Breed);
    }
}

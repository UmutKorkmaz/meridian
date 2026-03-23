using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Tests;

public class DependencyInjectionTests
{
    // ── Profiles for assembly scanning ──────────────────────────

    public class TestProfile : Profile
    {
        public TestProfile()
        {
            CreateMap<Source, Destination>();
        }
    }

    public class TestFullNameResolver : IValueResolver<ResolverSource, ResolverDest, string>
    {
        private readonly ITestDependency _dep;

        public TestFullNameResolver(ITestDependency dep)
        {
            _dep = dep;
        }

        public string Resolve(ResolverSource source, ResolverDest destination, string destMember, ResolutionContext context)
        {
            return $"{_dep.GetPrefix()} {source.FirstName} {source.LastName}";
        }
    }

    public interface ITestDependency
    {
        string GetPrefix();
    }

    public class TestDependency : ITestDependency
    {
        public string GetPrefix() => "Mr.";
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public void Should_Register_With_Assembly_Scanning()
    {
        var services = new ServiceCollection();
        services.AddMeridianMapping(typeof(DependencyInjectionTests).Assembly);
        var sp = services.BuildServiceProvider();

        var mapper = sp.GetService<IMapper>();
        Assert.NotNull(mapper);
    }

    [Fact]
    public void Should_Register_With_Action_Config()
    {
        var services = new ServiceCollection();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<Source, Destination>();
        });
        var sp = services.BuildServiceProvider();

        var mapper = sp.GetService<IMapper>();
        Assert.NotNull(mapper);

        var source = new Source { Id = 1, Name = "DI" };
        var result = mapper!.Map<Source, Destination>(source);
        Assert.Equal(1, result.Id);
        Assert.Equal("DI", result.Name);
    }

    [Fact]
    public void Should_Resolve_IMapper_From_DI()
    {
        var services = new ServiceCollection();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<Source, Destination>();
        });
        var sp = services.BuildServiceProvider();

        var mapper = sp.GetRequiredService<IMapper>();
        Assert.NotNull(mapper);
    }

    [Fact]
    public void Should_Resolve_IConfigurationProvider_From_DI()
    {
        var services = new ServiceCollection();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<Source, Destination>();
        });
        var sp = services.BuildServiceProvider();

        var configProvider = sp.GetRequiredService<IConfigurationProvider>();
        Assert.NotNull(configProvider);

        var typeMap = configProvider.FindTypeMap(typeof(Source), typeof(Destination));
        Assert.NotNull(typeMap);
    }

    [Fact]
    public void Should_Inject_Value_Resolver_Dependencies()
    {
        var services = new ServiceCollection();
        services.AddTransient<ITestDependency, TestDependency>();
        services.AddTransient<TestFullNameResolver>();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<ResolverSource, ResolverDest>()
                .ForMember(d => d.FullName, opt => opt.MapFrom<TestFullNameResolver>());
        });
        var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();

        var source = new ResolverSource { FirstName = "John", LastName = "Doe" };
        var result = mapper.Map<ResolverSource, ResolverDest>(source);

        Assert.Equal("Mr. John Doe", result.FullName);
    }
}

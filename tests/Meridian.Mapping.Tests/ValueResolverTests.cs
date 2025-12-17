using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Tests;

public class ValueResolverTests
{
    // ── Resolver implementations ────────────────────────────────

    public class FullNameResolver : IValueResolver<ResolverSource, ResolverDest, string>
    {
        public string Resolve(ResolverSource source, ResolverDest destination, string destMember, ResolutionContext context)
        {
            return $"{source.FirstName} {source.LastName}";
        }
    }

    public interface IGreetingService
    {
        string GetGreeting();
    }

    public class GreetingService : IGreetingService
    {
        public string GetGreeting() => "Hello";
    }

    public class GreetingResolver : IValueResolver<ResolverSource, ResolverDest, string>
    {
        private readonly IGreetingService _greetingService;

        public GreetingResolver(IGreetingService greetingService)
        {
            _greetingService = greetingService;
        }

        public string Resolve(ResolverSource source, ResolverDest destination, string destMember, ResolutionContext context)
        {
            return $"{_greetingService.GetGreeting()}, {source.FirstName} {source.LastName}";
        }
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public void Should_Resolve_Value_With_Simple_Resolver()
    {
        var services = new ServiceCollection();
        services.AddTransient<FullNameResolver>();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<ResolverSource, ResolverDest>()
                .ForMember(d => d.FullName, opt => opt.MapFrom<FullNameResolver>());
        });
        var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();

        var source = new ResolverSource { FirstName = "John", LastName = "Doe" };
        var result = mapper.Map<ResolverSource, ResolverDest>(source);

        Assert.Equal("John Doe", result.FullName);
    }

    [Fact]
    public void Should_Resolve_Value_With_Constructor_Dependencies()
    {
        var services = new ServiceCollection();
        services.AddTransient<IGreetingService, GreetingService>();
        services.AddTransient<GreetingResolver>();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<ResolverSource, ResolverDest>()
                .ForMember(d => d.FullName, opt => opt.MapFrom<GreetingResolver>());
        });
        var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();

        var source = new ResolverSource { FirstName = "Jane", LastName = "Smith" };
        var result = mapper.Map<ResolverSource, ResolverDest>(source);

        Assert.Equal("Hello, Jane Smith", result.FullName);
    }

    [Fact]
    public void Should_Resolve_Value_Receiving_Source_Destination_And_Context()
    {
        // The FullNameResolver receives source, destination, destMember, and context
        var services = new ServiceCollection();
        services.AddTransient<FullNameResolver>();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<ResolverSource, ResolverDest>()
                .ForMember(d => d.FullName, opt => opt.MapFrom<FullNameResolver>());
        });
        var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();

        var source = new ResolverSource { FirstName = "A", LastName = "B" };
        var result = mapper.Map<ResolverSource, ResolverDest>(source);

        // Resolver has access to source, dest, and context, and produces the combined name
        Assert.Equal("A B", result.FullName);
    }
}

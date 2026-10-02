using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Tests;

public class TypeConverterTests
{
    private IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    // ── Converter implementations ───────────────────────────────

    private class SourceToDestConverter : ITypeConverter<Source, Destination>
    {
        public Destination Convert(Source source, Destination destination, ResolutionContext context)
        {
            return new Destination
            {
                Id = source.Id,
                Name = source.Name + "_converted",
                Email = source.Email,
                Age = source.Age
            };
        }
    }

    private class DoubleAgeConverter : ITypeConverter<Source, Destination>
    {
        public Destination Convert(Source source, Destination destination, ResolutionContext context)
        {
            return new Destination
            {
                Id = source.Id,
                Name = source.Name,
                Email = source.Email,
                Age = source.Age * 2
            };
        }
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public void Should_ConvertUsing_TypeConverter_Type_Resolved_From_DI()
    {
        var services = new ServiceCollection();
        services.AddTransient<SourceToDestConverter>();
        services.AddMeridianMapping(cfg =>
        {
            cfg.CreateMap<Source, Destination>().ConvertUsing<SourceToDestConverter>();
        });
        var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();

        var source = new Source { Id = 5, Name = "hello" };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(5, result.Id);
        Assert.Equal("hello_converted", result.Name);
    }

    [Fact]
    public void Should_ConvertUsing_Inline_Func()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ConvertUsing(s => new Destination
                {
                    Id = s.Id * 10,
                    Name = s.Name.ToUpper(),
                    Email = s.Email,
                    Age = s.Age
                });
        });

        var source = new Source { Id = 5, Name = "hello", Email = "e@e.com", Age = 20 };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(50, result.Id);
        Assert.Equal("HELLO", result.Name);
    }

    [Fact]
    public void Should_ConvertUsing_Converter_Instance()
    {
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ConvertUsing(new SourceToDestConverter());
        });

        var source = new Source { Id = 3, Name = "converter" };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(3, result.Id);
        Assert.Equal("converter_converted", result.Name);
    }

    [Fact]
    public void Should_Override_Property_Mapping_With_ConvertUsing()
    {
        // ConvertUsing completely replaces property-level mapping
        var mapper = CreateMapper(cfg =>
        {
            cfg.CreateMap<Source, Destination>()
                .ConvertUsing(new DoubleAgeConverter());
        });

        var source = new Source { Id = 1, Name = "Test", Age = 15 };
        var result = mapper.Map<Source, Destination>(source);

        Assert.Equal(30, result.Age); // Doubled by converter
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConverterFailuresRetainOriginalExceptionForInstanceAndDiPaths(bool useDi)
    {
        var failure = new InvalidOperationException("converter failed");
        var converter = new FailingConverter(failure);
        using var provider = new ServiceCollection().AddSingleton(converter).BuildServiceProvider();
        var configuration = new MapperConfiguration(c =>
        {
            var map = c.CreateMap<Source, Destination>();
            if (useDi) map.ConvertUsing<FailingConverter>();
            else map.ConvertUsing(converter);
        });
        var mapper = configuration.CreateMapper(provider);
        var observed = Record.Exception(() => mapper.Map<Source, Destination>(new() { Id = 42 }));
        Assert.NotNull(observed);
        Assert.Same(failure, observed.GetBaseException());
    }
    public sealed class FailingConverter : ITypeConverter<Source, Destination>
    {
        private readonly Exception _failure;
        public FailingConverter(Exception failure) => _failure = failure;
        public Destination Convert(Source source, Destination destination, ResolutionContext context) => throw _failure;
    }
}

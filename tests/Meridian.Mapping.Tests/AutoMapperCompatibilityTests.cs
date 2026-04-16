using Meridian.Mapping.Configuration;
using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Tests;

public class AutoMapperCompatibilityTests
{
    [Fact]
    public void Should_Flow_Operation_Items_And_State_To_Resolvers()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<OptionsSource, OptionsDest>()
                .ForMember(d => d.Label, opt => opt.MapFrom<OptionsLabelResolver>());
        });
        var mapper = config.CreateMapper();

        var result = mapper.Map<OptionsSource, OptionsDest>(
            new OptionsSource { Name = "Alice" },
            opts =>
            {
                opts.Items["prefix"] = "Dr.";
                opts.State = "state";
            });

        Assert.Equal("Dr. Alice (state)", result.Label);
    }

    [Fact]
    public void Should_Run_Mapping_Actions_From_DI()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISuffixProvider, TestSuffixProvider>();
        services.AddTransient<NameSuffixAction>();
        services.AddAutoMapper(cfg =>
        {
            cfg.CreateMap<ActionSource, ActionDest>()
                .AfterMap<NameSuffixAction>();
        });

        var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapper>();

        var result = mapper.Map<ActionSource, ActionDest>(new ActionSource { Name = "Alice" });

        Assert.Equal("Alice!", result.Name);
    }

    [Fact]
    public void Should_Respect_ForSourceMember_DoNotValidate()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<SourceValidationSource, SourceValidationDest>()
                .ValidateMemberList(MemberList.Source)
                .ForSourceMember(s => s.Ignored, opt => opt.DoNotValidate());
        });

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_Reuse_Existing_Destination_Value_When_Configured()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<NestedSource, NestedDest>();
            cfg.CreateMap<ContainerSource, ContainerDest>()
                .ForMember(d => d.Inner, opt =>
                {
                    opt.MapFrom(s => s.Inner);
                    opt.UseDestinationValue();
                });
        });
        var mapper = config.CreateMapper();

        var existingInner = new NestedDest { Name = "old" };
        var destination = new ContainerDest { Inner = existingInner };

        var result = mapper.Map(
            new ContainerSource { Inner = new NestedSource { Name = "new" } },
            destination);

        Assert.Same(existingInner, result.Inner);
        Assert.Equal("new", result.Inner!.Name);
    }

    [Fact]
    public void Should_Allow_Null_Per_Member_When_Global_Null_Assignment_Is_Disabled()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AllowNullDestinationValues = false;
            cfg.CreateMap<NullSource, NullDest>()
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.AllowNull();
                });
        });
        var mapper = config.CreateMapper();

        var destination = new NullDest { Name = "keep" };
        var result = mapper.Map(new NullSource { Name = null }, destination);

        Assert.Null(result.Name);
    }

    [Fact]
    public void Should_Project_Explicit_Expansion_Only_When_Requested()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ProjectionInnerSource2, ProjectionInnerDest2>();
            cfg.CreateMap<ProjectionSource2, ProjectionDest2>()
                .ForMember(d => d.Inner, opt =>
                {
                    opt.MapFrom(s => s.Inner);
                    opt.ExplicitExpansion();
                });
        });
        var mapper = config.CreateMapper();
        var queryable = new[]
        {
            new ProjectionSource2 { Inner = new ProjectionInnerSource2 { Name = "expanded" } }
        }.AsQueryable();

        var withoutExpansion = mapper.ProjectTo<ProjectionDest2>(queryable).Single();
        var withExpansion = mapper.ProjectTo<ProjectionDest2>(queryable, d => d.Inner!).Single();

        Assert.Null(withoutExpansion.Inner);
        Assert.Equal("expanded", withExpansion.Inner!.Name);
    }

    [Fact]
    public void Should_Throw_Clear_Error_For_Unsupported_ProjectTo_Runtime_Resolver()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ProjectionSource3, ProjectionDest3>()
                .ForMember(d => d.Name, opt => opt.MapFrom<ProjectionNameResolver>());
        });
        var mapper = config.CreateMapper();
        var source = new[] { new ProjectionSource3 { Name = "Alice" } }.AsQueryable();

        var ex = Assert.ThrowsAny<Exception>(() => mapper.ProjectTo<ProjectionDest3>(source).ToList());
        var actual = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException! : ex;

        Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("ProjectionDest3.Name", actual.Message);
    }

    [Fact]
    public void Should_Scan_Maps_By_Marker_Type()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddMaps(typeof(CompatibilityScanProfile));
        });
        var mapper = config.CreateMapper();

        var result = mapper.Map<ScanSource, ScanDest>(new ScanSource { Name = "scanned" });

        Assert.Equal("scanned", result.Name);
    }

    [Fact]
    public void Should_Register_AutoMapper_Alias_By_Marker_Type()
    {
        var services = new ServiceCollection();
        services.AddAutoMapper(typeof(CompatibilityScanProfile));

        var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapper>();
        var result = mapper.Map<ScanSource, ScanDest>(new ScanSource { Name = "alias" });

        Assert.Equal("alias", result.Name);
    }

    private sealed class OptionsLabelResolver : IValueResolver<OptionsSource, OptionsDest, string>
    {
        public string Resolve(OptionsSource source, OptionsDest destination, string destMember, ResolutionContext context)
        {
            var prefix = (string)context.Items["prefix"]!;
            return $"{prefix} {source.Name} ({context.State})";
        }
    }

    private sealed class NameSuffixAction : IMappingAction<ActionSource, ActionDest>
    {
        private readonly ISuffixProvider _suffixProvider;

        public NameSuffixAction(ISuffixProvider suffixProvider)
        {
            _suffixProvider = suffixProvider;
        }

        public void Process(ActionSource source, ActionDest destination, ResolutionContext context)
        {
            destination.Name += _suffixProvider.GetSuffix();
        }
    }

    private interface ISuffixProvider
    {
        string GetSuffix();
    }

    private sealed class TestSuffixProvider : ISuffixProvider
    {
        public string GetSuffix() => "!";
    }

    private sealed class ProjectionNameResolver : IValueResolver<ProjectionSource3, ProjectionDest3, string>
    {
        public string Resolve(ProjectionSource3 source, ProjectionDest3 destination, string destMember, ResolutionContext context)
        {
            return source.Name.ToUpperInvariant();
        }
    }

    private sealed class CompatibilityScanProfile : Profile
    {
        public CompatibilityScanProfile()
        {
            CreateMap<ScanSource, ScanDest>();
        }
    }

    private sealed class OptionsSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class OptionsDest
    {
        public string Label { get; set; } = string.Empty;
    }

    private sealed class ActionSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ActionDest
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class SourceValidationSource
    {
        public string Name { get; set; } = string.Empty;
        public string Ignored { get; set; } = string.Empty;
    }

    private sealed class SourceValidationDest
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class NestedSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class NestedDest
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ContainerSource
    {
        public NestedSource? Inner { get; set; }
    }

    private sealed class ContainerDest
    {
        public NestedDest? Inner { get; set; }
    }

    private sealed class NullSource
    {
        public string? Name { get; set; }
    }

    private sealed class NullDest
    {
        public string? Name { get; set; }
    }

    private sealed class ProjectionInnerSource2
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProjectionInnerDest2
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProjectionSource2
    {
        public ProjectionInnerSource2? Inner { get; set; }
    }

    private sealed class ProjectionDest2
    {
        public ProjectionInnerDest2? Inner { get; set; }
    }

    private sealed class ProjectionSource3
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProjectionDest3
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ScanSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ScanDest
    {
        public string Name { get; set; } = string.Empty;
    }
}

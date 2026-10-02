using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mapping.Tests;

public class ConcurrentMappingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParallelMappingsKeepScopedResolversAndNestedOptionsIsolated(bool interpreter, bool existing)
    {
        using var barrier = new Barrier(4);
        var configuration = new MapperConfiguration(c =>
        {
            c.CreateMap<Envelope, EnvelopeDto>();
            c.CreateMap<Leaf, LeafDto>()
                .ForMember(d => d.Proof, o => o.MapFrom<ScopedProofResolver>());
        });
        var outerMap = Assert.IsType<TypeMap>(configuration.FindTypeMap(typeof(Envelope), typeof(EnvelopeDto)));
        var childMap = Assert.IsType<TypeMap>(configuration.FindTypeMap(typeof(Leaf), typeof(LeafDto)));
        Assert.NotNull(outerMap.CompiledFastPath);
        Assert.Null(childMap.CompiledFastPath); // Resolver maps intentionally use the interpreter.
        if (interpreter) outerMap.CompiledFastPath = null;
        var originalMaps = configuration.GetAllTypeMaps().ToArray();
        var originalFastPaths = originalMaps.Select(map => map.CompiledFastPath).ToArray();
        using var providerA = CreateProvider(configuration, barrier, "provider-A");
        using var providerB = CreateProvider(configuration, barrier, "provider-B");
        using var scopeA = providerA.CreateScope();
        using var scopeB = providerB.CreateScope();
        scopeA.ServiceProvider.GetRequiredService<ScopeStamp>().Name = "scope-A";
        scopeB.ServiceProvider.GetRequiredService<ScopeStamp>().Name = "scope-B";
        var mapperA = scopeA.ServiceProvider.GetRequiredService<IMapper>();
        var mapperB = scopeB.ServiceProvider.GetRequiredService<IMapper>();
        Assert.Same(configuration, mapperA.ConfigurationProvider);
        Assert.Same(configuration, mapperB.ConfigurationProvider);

        // Two overlapping operations reuse each mapper; the providers and their scopes differ.
        Task<EnvelopeDto> Run(IMapper mapper, string operation) => Task.Factory.StartNew(() =>
        {
            var source = new Envelope { Inner = new Leaf { Value = 42 } };
            var destination = new EnvelopeDto();
            void SetOptions(IMappingOperationOptions options)
            {
                options.Items["operation"] = operation;
                options.Items["synchronize"] = true;
                options.State = new OperationState(operation);
            }
            var mapped = existing
                ? mapper.Map(source, destination, options => SetOptions(options))
                : mapper.Map<Envelope, EnvelopeDto>(source, options => SetOptions(options));
            if (existing) Assert.Same(destination, mapped);
            return mapped;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var outputs = await Task.WhenAll(Run(mapperA, "A1"), Run(mapperA, "A2"),
            Run(mapperB, "B1"), Run(mapperB, "B2"));

        Assert.Collection(outputs,
            output => Assert.Equal("provider-A/scope-A/A1/A1/42", output.Inner!.Proof),
            output => Assert.Equal("provider-A/scope-A/A2/A2/42", output.Inner!.Proof),
            output => Assert.Equal("provider-B/scope-B/B1/B1/42", output.Inner!.Proof),
            output => Assert.Equal("provider-B/scope-B/B2/B2/42", output.Inner!.Proof));
        // A later call has fresh Items/State even on a previously used mapper.
        Assert.Equal("provider-A/scope-A/none/none/5", mapperA.Map<Envelope, EnvelopeDto>(
            new() { Inner = new Leaf { Value = 5 } }).Inner!.Proof);
        Assert.Equal("provider-B/scope-B/none/none/6", mapperB.Map<Envelope, EnvelopeDto>(
            new() { Inner = new Leaf { Value = 6 } }).Inner!.Proof);
        var currentMaps = configuration.GetAllTypeMaps().ToArray();
        Assert.Equal(originalMaps.Length, currentMaps.Length);
        for (var i = 0; i < originalMaps.Length; i++)
        {
            Assert.Same(originalMaps[i], currentMaps[i]);
            Assert.Same(originalFastPaths[i], currentMaps[i].CompiledFastPath);
        }
    }

    [Fact]
    public async Task ConcurrentOperationsPreserveTheirOwnDiamondAndNeverReuseAnotherDestination()
    {
        using var barrier = new Barrier(2);
        var configuration = new MapperConfiguration(c =>
        {
            c.CreateMap<MappingPathParityTests.Graph, MappingPathParityTests.GraphDto>()
                .BeforeMap((_, _) =>
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15)))
                        throw new TimeoutException("Mapping operations did not overlap.");
                });
            c.CreateMap<MappingPathParityTests.Node, MappingPathParityTests.NodeDto>().PreserveReferences();
        });
        var mapper = configuration.CreateMapper();
        var shared = new MappingPathParityTests.Node { Value = 7 };
        var source = new MappingPathParityTests.Graph { Left = shared, Right = shared };
        Task<MappingPathParityTests.GraphDto> Run() => Task.Factory.StartNew(
            () => mapper.Map<MappingPathParityTests.Graph, MappingPathParityTests.GraphDto>(source),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var results = await Task.WhenAll(Run(), Run());

        Assert.Same(results[0].Left, results[0].Right);
        Assert.Same(results[1].Left, results[1].Right);
        Assert.NotSame(results[0].Left, results[1].Left);
        Assert.Equal(7, results[0].Left!.Value);
        Assert.Equal(7, results[1].Left!.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedActionsRetainOrderOptionsAndExistingDestination(bool interpreter, bool existing)
    {
        var configuration = new MapperConfiguration(c =>
        {
            c.CreateMap<Envelope, EnvelopeDto>();
            c.CreateMap<Leaf, LeafDto>()
                .BeforeMap<BeforeProofAction>()
                .ForMember(d => d.Proof, o => o.MapFrom<OrderedProofResolver>())
                .AfterMap<AfterProofAction>();
        });
        var outer = Assert.IsType<TypeMap>(configuration.FindTypeMap(typeof(Envelope), typeof(EnvelopeDto)));
        Assert.NotNull(outer.CompiledFastPath);
        if (interpreter) outer.CompiledFastPath = null;
        var services = new ServiceCollection();
        services.AddTransient<BeforeProofAction>();
        services.AddTransient<AfterProofAction>();
        services.AddTransient<OrderedProofResolver>();
        using var provider = services.BuildServiceProvider();
        var mapper = configuration.CreateMapper(provider);
        var source = new Envelope { Inner = new Leaf { Value = 42 } };
        var destination = new EnvelopeDto();
        var steps = new List<string>();
        void SetOptions(IMappingOperationOptions options)
        {
            options.Items["steps"] = steps;
            options.State = "operation";
        }
        var mapped = existing
            ? mapper.Map(source, destination, options => SetOptions(options))
            : mapper.Map<Envelope, EnvelopeDto>(source, options => SetOptions(options));
        if (existing) Assert.Same(destination, mapped);
        Assert.Equal("operation:42", mapped.Inner!.Proof);
        Assert.Collection(steps,
            step => Assert.Equal("before:operation", step),
            step => Assert.Equal("resolve:operation", step),
            step => Assert.Equal("after:operation:42", step));
    }
    public sealed class BeforeProofAction : IMappingAction<Leaf, LeafDto>
    {
        public void Process(Leaf source, LeafDto destination, ResolutionContext context) =>
            ((List<string>)context.Items["steps"]!).Add("before:" + context.State);
    }
    public sealed class AfterProofAction : IMappingAction<Leaf, LeafDto>
    {
        public void Process(Leaf source, LeafDto destination, ResolutionContext context) =>
            ((List<string>)context.Items["steps"]!).Add("after:" + destination.Proof);
    }
    public sealed class OrderedProofResolver : IValueResolver<Leaf, LeafDto, string>
    {
        public string Resolve(Leaf source, LeafDto destination, string destMember, ResolutionContext context)
        {
            ((List<string>)context.Items["steps"]!).Add("resolve:" + context.State);
            return context.State + ":" + source.Value;
        }
    }

    private static ServiceProvider CreateProvider(MapperConfiguration configuration, Barrier barrier, string name)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationProvider>(configuration);
        services.AddSingleton(barrier);
        services.AddSingleton(new ProviderStamp(name));
        services.AddScoped<ScopeStamp>();
        services.AddScoped<ScopedProofResolver>();
        services.AddScoped<IMapper>(provider => configuration.CreateMapper(provider));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed class ScopedProofResolver : IValueResolver<Leaf, LeafDto, string>
    {
        private readonly ScopeStamp _scope;
        private readonly ProviderStamp _provider;
        private readonly Barrier _barrier;
        public ScopedProofResolver(ScopeStamp scope, ProviderStamp provider, Barrier barrier)
        {
            _scope = scope;
            _provider = provider;
            _barrier = barrier;
        }
        public string Resolve(Leaf source, LeafDto destination, string destMember, ResolutionContext context)
        {
            context.Items.TryGetValue("operation", out var operation);
            var state = (context.State as OperationState)?.Name ?? "none";
            if (context.Items.ContainsKey("synchronize"))
            {
                // Both phases enforce overlap without any scheduling sleeps.
                if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Scoped resolver calls did not overlap.");
                context.Items["resolverVisited"] = operation;
                if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Scoped resolver calls did not finish together.");
                Assert.Equal(operation, context.Items["resolverVisited"]);
                Assert.Same(_scope, context.ServiceProvider!.GetRequiredService<ScopeStamp>());
            }
            return $"{_provider.Name}/{_scope.Name}/{operation ?? "none"}/{state}/{source.Value}";
        }
    }
    public sealed class ProviderStamp
    {
        public ProviderStamp(string name) => Name = name;
        public string Name { get; }
    }
    public sealed class ScopeStamp { public string Name { get; set; } = "unset"; }
    public sealed record OperationState(string Name);
    public sealed class Envelope { public Leaf? Inner { get; set; } }
    public sealed class EnvelopeDto { public LeafDto? Inner { get; set; } }
    public sealed class Leaf { public int Value { get; set; } }
    public sealed class LeafDto { public string Proof { get; set; } = ""; }
}

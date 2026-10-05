using BenchmarkDotNet.Attributes;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeridianAlias = Meridian.Mapping;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

// ============================================================================
// Configuration / startup benchmarks (E3). Three DIFFERENT questions, never merged into one number:
//
//  1. "Configure" rows (E3StartupMapping/E3StartupMediator, hot): construct the library configuration only, in an already
//     warmed process, many times. Fresh configuration in a warmed process is NOT cold start.
//  2. "Ready" rows (same classes, hot): configuration plus everything needed to produce the first result (mapper creation
//     and one Map, or provider build, mediator resolution and one Send, with provider disposal inside the timed operation
//     for every contender alike). Still a warmed process.
//  3. Cold rows (E3ColdMapping/E3ColdMediator): the first use in a FRESH process: ten fresh launches, no warmup, one
//     measured invocation each. The measured region starts after the benchmark harness is running, so process launch and
//     runtime initialization are excluded; JIT, type loading and static initialization of the library code are included.
//     The semantic checks for these classes run in the separate preflight process so they cannot warm the measured one.
//
// Omitted on purpose: Mapperly has no runtime configuration (its cost is compile time and is not a runtime startup
// number); source-generator build time is a build cost, not measured here; assembly-scanning registration is omitted
// because the two mediators would scan different handler inventories in this assembly (not equal work). MediatR's API
// requires one assembly to be named, so its rows include a scan of the small MediatR assembly itself (no handler is
// discovered there; handlers are added explicitly for every library).
// ============================================================================

internal static class E3StartupWork
{
    internal static MeridianAlias.MapperConfiguration MeridianConfiguration() =>
        new(OrderPayload.ConfigureMeridian);

    internal static MeridianAlias.MapperConfiguration MeridianGeneratedConfiguration() =>
        new(cfg => MeridianAlias.GeneratedMappingExtensions.UseGeneratedMappings(cfg, new Generated.E3GeneratedOrderMaps.GeneratedResolver()));

    internal static AutoMapper.MapperConfiguration AutoMapperConfiguration() => new(OrderPayload.ConfigureAutoMapper);

    internal static DestOrder ReadyMeridian(SourceOrder source) =>
        MeridianConfiguration().CreateMapper().Map<SourceOrder, DestOrder>(source);

    internal static DestOrder ReadyMeridianGenerated(SourceOrder source) =>
        MeridianGeneratedConfiguration().CreateMapper().Map<SourceOrder, DestOrder>(source);

    internal static DestOrder ReadyAutoMapper(SourceOrder source) =>
        AutoMapperConfiguration().CreateMapper().Map<SourceOrder, DestOrder>(source);

    internal static DestOrder ReadyMapster(SourceOrder source) =>
        new global::MapsterMapper.Mapper(OrderPayload.CreateMapsterConfig()).Map<SourceOrder, DestOrder>(source);

    // ---- mediator ----

    internal static IServiceCollection MeridianServices()
    {
        var services = new ServiceCollection();
        services.AddTransient<MeridianM.IRequestHandler<Ping, Pong>, PingMeridianHandler>();
        services.AddMeridianMediator(_ => { });
        return services;
    }

    internal static IServiceCollection StrictGeneratedServices()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RequireGeneratedDispatch = true;
            cfg.AddGeneratedRegistry<E3DispatchRegistry>();
        });
        return services;
    }

    internal static IServiceCollection MediatRServices()
    {
        var services = new ServiceCollection();
        services.AddTransient<MediatR.IRequestHandler<Ping, Pong>, PingMediatRHandler>();
        E3MediatR.Add(services);
        return services;
    }

    internal static Pong SendMeridian(IServiceCollection services, Ping request)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<MeridianM.IMediator>().Send(request).GetAwaiter().GetResult();
    }

    internal static Pong SendMediatR(IServiceCollection services, Ping request)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<MediatR.IMediator>().Send(request).GetAwaiter().GetResult();
    }

    internal static void RequireMapperConfiguration(string name, MeridianAlias.MapperConfiguration configuration, int maps) =>
        E3Work.Require(configuration.GetAllTypeMaps().Count == maps, $"{name}: expected {maps} type maps but found {configuration.GetAllTypeMaps().Count}.");

    internal static void RequireHandler(string name, IServiceCollection services, Type handlerService) =>
        E3Work.Require(services.Any(d => d.ServiceType == handlerService), $"{name}: the configuration contains no registration for {handlerService.Name}.");
}

/// <summary>Hot mapping startup: configuration construction only, and ready-for-first-map (warmed process).</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "startup")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet requires instance benchmark methods.")]
public class E3StartupMapping
{
    private SourceOrder _source = default!;

    [GlobalSetup]
    public void Setup()
    {
        _source = OrderPayload.Create();
        Preflight();
    }

    public void Preflight()
    {
        E3StartupWork.RequireMapperConfiguration(nameof(MeridianConfigure), MeridianConfigure(), 2);
        E3StartupWork.RequireMapperConfiguration(nameof(MeridianGeneratedRuntimeConfigure), MeridianGeneratedRuntimeConfigure(), 0);
        E3Work.Require(AutoMapperConfigure() is not null, "AutoMapper configuration was not created.");
        E3Work.Require(MapsterConfigure().RuleMap.Count >= 2, "Mapster configuration must contain the registered rules.");
        OrderPayload.Verify(nameof(MeridianReady), _source, MeridianReady());
        OrderPayload.Verify(nameof(MeridianGeneratedRuntimeReady), _source, MeridianGeneratedRuntimeReady());
        OrderPayload.Verify(nameof(AutoMapperReady), _source, AutoMapperReady());
        OrderPayload.Verify(nameof(MapsterReady), _source, MapsterReady());
    }

    [Benchmark(Description = "Meridian configuration")]
    public MeridianAlias.MapperConfiguration MeridianConfigure() => E3StartupWork.MeridianConfiguration();

    [Benchmark(Description = "Meridian generated-runtime configuration")]
    public MeridianAlias.MapperConfiguration MeridianGeneratedRuntimeConfigure() => E3StartupWork.MeridianGeneratedConfiguration();

    [Benchmark(Description = "AutoMapper 14.0.0 configuration (historical pin)")]
    public AutoMapper.MapperConfiguration AutoMapperConfigure() => E3StartupWork.AutoMapperConfiguration();

    [Benchmark(Description = "Mapster configuration (compiled)")]
    public Mapster.TypeAdapterConfig MapsterConfigure() => OrderPayload.CreateMapsterConfig();

    [Benchmark(Description = "Meridian ready + first map")]
    public DestOrder MeridianReady() => E3StartupWork.ReadyMeridian(_source);

    [Benchmark(Description = "Meridian generated-runtime ready + first map")]
    public DestOrder MeridianGeneratedRuntimeReady() => E3StartupWork.ReadyMeridianGenerated(_source);

    [Benchmark(Description = "AutoMapper 14.0.0 ready + first map (historical pin)")]
    public DestOrder AutoMapperReady() => E3StartupWork.ReadyAutoMapper(_source);

    [Benchmark(Description = "Mapster ready + first map")]
    public DestOrder MapsterReady() => E3StartupWork.ReadyMapster(_source);
}

/// <summary>Hot mediator startup: registration only, and provider build + first Send + provider disposal (warmed process).</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "startup")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet requires instance benchmark methods.")]
public class E3StartupMediator
{
    private static readonly Ping Request = new(42);

    [GlobalSetup]
    public void Setup() => Preflight();

    public void Preflight()
    {
        E3StartupWork.RequireHandler(nameof(MeridianRuntimeConfigure), MeridianRuntimeConfigure(), typeof(MeridianM.IRequestHandler<Ping, Pong>));
        E3StartupWork.RequireHandler(nameof(MeridianStrictGeneratedConfigure), MeridianStrictGeneratedConfigure(), typeof(MeridianM.IRequestHandler<Ping, Pong>));
        E3StartupWork.RequireHandler(nameof(MediatRV12Configure), MediatRV12Configure(), typeof(MediatR.IRequestHandler<Ping, Pong>));
        foreach (var (name, ready) in new (string, Func<Pong>)[]
                 {
                     (nameof(MeridianRuntimeReady), MeridianRuntimeReady), (nameof(MeridianStrictGeneratedReady), MeridianStrictGeneratedReady), (nameof(MediatRV12Ready), MediatRV12Ready),
                 })
        {
            E3Work.Reset();
            E3Work.RequirePong(name, ready(), 43);
            E3Work.Require(E3Work.HandlerCalls == 1, $"{name}: the handler ran {E3Work.HandlerCalls} times.");
        }
    }

    [Benchmark(Description = "Meridian runtime registration")]
    public IServiceCollection MeridianRuntimeConfigure() => E3StartupWork.MeridianServices();

    [Benchmark(Description = "Meridian strict generated registration")]
    public IServiceCollection MeridianStrictGeneratedConfigure() => E3StartupWork.StrictGeneratedServices();

    [Benchmark(Description = "MediatR 12.5.0 registration (historical pin)")]
    public IServiceCollection MediatRV12Configure() => E3StartupWork.MediatRServices();

    [Benchmark(Description = "Meridian runtime build + first Send")]
    public Pong MeridianRuntimeReady() => E3StartupWork.SendMeridian(E3StartupWork.MeridianServices(), Request);

    [Benchmark(Description = "Meridian strict generated build + first Send")]
    public Pong MeridianStrictGeneratedReady() => E3StartupWork.SendMeridian(E3StartupWork.StrictGeneratedServices(), Request);

    [Benchmark(Description = "MediatR 12.5.0 build + first Send (historical pin)")]
    public Pong MediatRV12Ready() => E3StartupWork.SendMediatR(E3StartupWork.MediatRServices(), Request);
}

/// <summary>Fresh-process first mapping. Setup only builds the plain source object; semantic checks run in the preflight process.</summary>
[Config(typeof(E3ColdConfig))]
[BenchmarkCategory("E3", "cold")]
public class E3ColdMapping
{
    private SourceOrder _source = default!;

    [GlobalSetup]
    public void Setup() => _source = OrderPayload.Create();

    public void Preflight()
    {
        OrderPayload.Verify(nameof(ManualFirstMap), _source, ManualFirstMap());
        OrderPayload.Verify(nameof(MeridianGeneratedDirectFirstMap), _source, MeridianGeneratedDirectFirstMap());
        OrderPayload.Verify(nameof(MapperlyFirstMap), _source, MapperlyFirstMap());
        OrderPayload.Verify(nameof(MeridianReadyFirstMap), _source, MeridianReadyFirstMap());
        OrderPayload.Verify(nameof(MeridianGeneratedRuntimeReadyFirstMap), _source, MeridianGeneratedRuntimeReadyFirstMap());
        OrderPayload.Verify(nameof(MapsterReadyFirstMap), _source, MapsterReadyFirstMap());
        OrderPayload.Verify(nameof(AutoMapperV14ReadyFirstMap), _source, AutoMapperV14ReadyFirstMap());
    }

    [Benchmark(Baseline = true, Description = "Hand-written first map")]
    public DestOrder ManualFirstMap() => OrderPayload.ManualMap(_source);

    [Benchmark(Description = "Meridian generated direct first map")]
    public DestOrder MeridianGeneratedDirectFirstMap() => Generated.E3GeneratedOrderMaps.MapToDestOrder(_source);

    [Benchmark(Description = "Mapperly first map")]
    public DestOrder MapperlyFirstMap() => Generated.E3MapperlyOrder.Map(_source);

    [Benchmark(Description = "Meridian configuration + first map")]
    public DestOrder MeridianReadyFirstMap() => E3StartupWork.ReadyMeridian(_source);

    [Benchmark(Description = "Meridian generated-runtime configuration + first map")]
    public DestOrder MeridianGeneratedRuntimeReadyFirstMap() => E3StartupWork.ReadyMeridianGenerated(_source);

    [Benchmark(Description = "Mapster configuration + first map")]
    public DestOrder MapsterReadyFirstMap() => E3StartupWork.ReadyMapster(_source);

    [Benchmark(Description = "AutoMapper 14.0.0 configuration + first map (historical pin)")]
    public DestOrder AutoMapperV14ReadyFirstMap() => E3StartupWork.ReadyAutoMapper(_source);
}

/// <summary>Fresh-process first Send. Setup is empty; semantic checks run in the preflight process.</summary>
[Config(typeof(E3ColdConfig))]
[BenchmarkCategory("E3", "cold")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet requires instance benchmark methods.")]
public class E3ColdMediator
{
    private static readonly Ping Request = new(42);

    public void Preflight()
    {
        foreach (var (name, send) in new (string, Func<Pong>)[]
                 {
                     (nameof(DirectHandlerFirstCall), DirectHandlerFirstCall), (nameof(MeridianRuntimeReadyFirstSend), MeridianRuntimeReadyFirstSend),
                     (nameof(MeridianStrictGeneratedReadyFirstSend), MeridianStrictGeneratedReadyFirstSend), (nameof(MediatRV12ReadyFirstSend), MediatRV12ReadyFirstSend),
                 })
        {
            E3Work.Reset();
            E3Work.RequirePong(name, send(), 43);
            E3Work.Require(E3Work.HandlerCalls == 1, $"{name}: the handler ran {E3Work.HandlerCalls} times.");
        }
    }

    [Benchmark(Baseline = true, Description = "Direct handler first call")]
    public Pong DirectHandlerFirstCall() => new PingMeridianHandler().Handle(Request, CancellationToken.None).GetAwaiter().GetResult();

    [Benchmark(Description = "Meridian runtime build + first Send")]
    public Pong MeridianRuntimeReadyFirstSend() => E3StartupWork.SendMeridian(E3StartupWork.MeridianServices(), Request);

    [Benchmark(Description = "Meridian strict generated build + first Send")]
    public Pong MeridianStrictGeneratedReadyFirstSend() => E3StartupWork.SendMeridian(E3StartupWork.StrictGeneratedServices(), Request);

    [Benchmark(Description = "MediatR 12.5.0 build + first Send (historical pin)")]
    public Pong MediatRV12ReadyFirstSend() => E3StartupWork.SendMediatR(E3StartupWork.MediatRServices(), Request);
}

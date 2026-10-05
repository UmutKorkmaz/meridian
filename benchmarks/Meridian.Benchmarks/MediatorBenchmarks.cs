using BenchmarkDotNet.Attributes;
using MediatR;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

// ============================================================================
// Mediator dispatch benchmarks (E3).
//
// Equal work: one request, one handler invocation, one observable Pong response. Every handler (the direct floor, the
// Meridian handler and the MediatR handler) performs the identical advertised work: it counts its invocation in a plain
// static counter and returns the same Pong. GlobalSetup/Preflight verify the response value and the handler call count of
// every contender before timing, so no contender can win by skipping the handler.
//
//  * MediatR is benchmarked at the explicitly pinned historical 12.5.0 (Apache-2.0; the last permissively licensed
//    release). It is not the current MediatR and says nothing about newer, commercially licensed releases.
//  * "Strict generated" uses the optional generated registry with RequireGeneratedDispatch, so no handler wrapper is created
//    by reflection. It is a distinct row, never mixed with the runtime row.
//  * Timings are synchronous-completion Task paths (no I/O, no thread hops); they say nothing about asynchronous latency.
// ============================================================================

/// <summary>Observable work shared by every handler, behavior and counter in the E3 mediator scenarios (single-threaded).</summary>
internal static class E3Work
{
    internal static long HandlerCalls;
    internal static long BehaviorCalls;
    internal static long NotificationCalls;
    internal static int LastScopeId;
    internal static bool Recording;
    internal static readonly List<int> BehaviorLog = [];

    internal static void Reset()
    {
        HandlerCalls = 0;
        BehaviorCalls = 0;
        NotificationCalls = 0;
        LastScopeId = 0;
        BehaviorLog.Clear();
    }

    /// <summary>Records that behavior <paramref name="id"/> ran. The optional log is only filled during preflight.</summary>
    internal static void Hit(int id)
    {
        BehaviorCalls++;
        if (Recording)
        {
            BehaviorLog.Add(id);
        }
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void RequirePong(string contender, Pong pong, int expected)
    {
        Require(pong is not null && pong.Value == expected, $"{contender}: expected Pong({expected}) but got {pong?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}.");
    }
}

/// <summary>MediatR registration limited to explicitly added handlers (its API requires one assembly to be named; none of its types is a handler).</summary>
internal static class E3MediatR
{
    internal static void Add(IServiceCollection services, Action<MediatRServiceConfiguration>? configure = null) =>
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Unit).Assembly);
            cfg.TypeEvaluator = static _ => false;
            configure?.Invoke(cfg);
        });
}

/// <summary>The optional generated registry (strict dispatch) for the plain Ping/Pong request.</summary>
[Meridian.Mediator.Generated.GenerateMediatorRegistry(typeof(PingMeridianHandler), ExpectedRequestTypes = new[] { typeof(Ping) })]
public partial class E3DispatchRegistry
{
}

/// <summary>Request/response dispatch with no behaviors: direct handler floor, Meridian runtime, Meridian strict generated, MediatR 12.5.0.</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3MediatorDispatch
{
    private static readonly Ping Request = new(42);
    private PingMeridianHandler _handler = default!;
    private ServiceProvider _runtimeProvider = default!;
    private ServiceProvider _generatedProvider = default!;
    private ServiceProvider _mediatRProvider = default!;
    private MeridianM.IMediator _runtime = default!;
    private MeridianM.IMediator _generated = default!;
    private MediatR.IMediator _mediatR = default!;

    [GlobalSetup]
    public void Setup()
    {
        _handler = new PingMeridianHandler();

        var runtime = new ServiceCollection();
        runtime.AddTransient<MeridianM.IRequestHandler<Ping, Pong>, PingMeridianHandler>();
        runtime.AddMeridianMediator(_ => { });
        _runtimeProvider = runtime.BuildServiceProvider();
        _runtime = _runtimeProvider.GetRequiredService<MeridianM.IMediator>();

        var generated = new ServiceCollection();
        generated.AddMeridianMediator(cfg =>
        {
            cfg.RequireGeneratedDispatch = true;
            cfg.AddGeneratedRegistry<E3DispatchRegistry>();
        });
        _generatedProvider = generated.BuildServiceProvider();
        _generated = _generatedProvider.GetRequiredService<MeridianM.IMediator>();

        var mediatR = new ServiceCollection();
        mediatR.AddTransient<MediatR.IRequestHandler<Ping, Pong>, PingMediatRHandler>();
        E3MediatR.Add(mediatR);
        _mediatRProvider = mediatR.BuildServiceProvider();
        _mediatR = _mediatRProvider.GetRequiredService<MediatR.IMediator>();

        Preflight();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _runtimeProvider.Dispose();
        _generatedProvider.Dispose();
        _mediatRProvider.Dispose();
    }

    /// <summary>Every contender must return Pong(43) and run the handler exactly once per send.</summary>
    public void Preflight()
    {
        Check(nameof(DirectHandler), DirectHandler);
        Check(nameof(MeridianRuntime), MeridianRuntime);
        Check(nameof(MeridianStrictGenerated), MeridianStrictGenerated);
        Check(nameof(MediatRV12), MediatRV12);

        static void Check(string name, Func<Task<Pong>> send)
        {
            E3Work.Reset();
            E3Work.RequirePong(name, send().GetAwaiter().GetResult(), 43);
            E3Work.Require(E3Work.HandlerCalls == 1, $"{name}: the handler ran {E3Work.HandlerCalls} times for one send.");
        }
    }

    [Benchmark(Baseline = true, Description = "Direct handler call (floor)")]
    public Task<Pong> DirectHandler() => _handler.Handle(Request, CancellationToken.None);

    [Benchmark(Description = "Meridian runtime Send")]
    public Task<Pong> MeridianRuntime() => _runtime.Send(Request);

    [Benchmark(Description = "Meridian strict generated Send")]
    public Task<Pong> MeridianStrictGenerated() => _generated.Send(Request);

    [Benchmark(Description = "MediatR 12.5.0 Send (historical pin)")]
    public Task<Pong> MediatRV12() => _mediatR.Send(Request);
}

/// <summary>Scoped handlers inside ONE existing scope: the scope is created and disposed outside the timed methods.</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3MediatorScopeExisting
{
    private static readonly ScopedPing Request = new(42);
    private ServiceProvider _provider = default!;
    private IServiceScope _scope = default!;
    private MeridianM.IMediator _meridian = default!;
    private MediatR.IMediator _mediatR = default!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = E3Scoped.BuildProvider();
        _scope = _provider.CreateScope();
        _meridian = _scope.ServiceProvider.GetRequiredService<MeridianM.IMediator>();
        _mediatR = _scope.ServiceProvider.GetRequiredService<MediatR.IMediator>();
        Preflight();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    /// <summary>Same scoped dependency for every send inside the scope, a different one in another scope, disposed with its scope.</summary>
    public void Preflight()
    {
        var disposedBefore = E3ScopedDependency.Disposed;
        E3Work.Reset();
        E3Work.RequirePong(nameof(DiResolvedHandler), DiResolvedHandler().GetAwaiter().GetResult(), 43);
        var scopeId = E3Work.LastScopeId;
        E3Work.RequirePong(nameof(MeridianRuntime), MeridianRuntime().GetAwaiter().GetResult(), 43);
        E3Work.Require(E3Work.LastScopeId == scopeId, "Meridian handler did not share the scoped dependency of the scope.");
        E3Work.RequirePong(nameof(MediatRV12), MediatRV12().GetAwaiter().GetResult(), 43);
        E3Work.Require(E3Work.LastScopeId == scopeId, "MediatR handler did not share the scoped dependency of the scope.");
        E3Work.Require(E3Work.HandlerCalls == 3, $"expected 3 handler calls but saw {E3Work.HandlerCalls}.");

        using (var other = _provider.CreateScope())
        {
            E3Work.RequirePong("other scope", other.ServiceProvider.GetRequiredService<MeridianM.IMediator>().Send(Request).GetAwaiter().GetResult(), 43);
            E3Work.Require(E3Work.LastScopeId != scopeId, "A different scope reused the scoped dependency.");
        }

        E3Work.Require(E3ScopedDependency.Disposed == disposedBefore + 1, "The other scope's dependency was not disposed with its scope.");
    }

    [Benchmark(Baseline = true, Description = "DI-resolved handler call (floor)")]
    public Task<Pong> DiResolvedHandler() =>
        _scope.ServiceProvider.GetRequiredService<E3ScopedPingMeridianHandler>().Handle(Request, CancellationToken.None);

    [Benchmark(Description = "Meridian runtime Send")]
    public Task<Pong> MeridianRuntime() => _meridian.Send(Request);

    [Benchmark(Description = "MediatR 12.5.0 Send (historical pin)")]
    public Task<Pong> MediatRV12() => _mediatR.Send(Request);
}

/// <summary>Scoped handlers with one scope created, used and disposed PER operation, for every contender alike.</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3MediatorScopePerOperation
{
    private static readonly ScopedPing Request = new(42);
    private ServiceProvider _provider = default!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = E3Scoped.BuildProvider();
        Preflight();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    /// <summary>Each operation gets its own scope: a fresh scoped dependency, disposed when the operation ends.</summary>
    public void Preflight()
    {
        var ids = new HashSet<int>();
        foreach (var (name, send) in new (string, Func<Task<Pong>>)[]
                 {
                     (nameof(DiResolvedHandler), DiResolvedHandler), (nameof(MeridianRuntime), MeridianRuntime), (nameof(MediatRV12), MediatRV12),
                 })
        {
            var disposedBefore = E3ScopedDependency.Disposed;
            E3Work.Reset();
            E3Work.RequirePong(name, send().GetAwaiter().GetResult(), 43);
            E3Work.Require(E3Work.HandlerCalls == 1, $"{name}: the handler ran {E3Work.HandlerCalls} times.");
            E3Work.Require(ids.Add(E3Work.LastScopeId), $"{name}: the scoped dependency was reused across operations.");
            E3Work.Require(E3ScopedDependency.Disposed == disposedBefore + 1, $"{name}: the per-operation scope was not disposed exactly once.");
        }
    }

    [Benchmark(Baseline = true, Description = "Scope + DI-resolved handler call (floor)")]
    public async Task<Pong> DiResolvedHandler()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<E3ScopedPingMeridianHandler>().Handle(Request, CancellationToken.None);
    }

    [Benchmark(Description = "Scope + Meridian runtime Send")]
    public async Task<Pong> MeridianRuntime()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MeridianM.IMediator>().Send(Request);
    }

    [Benchmark(Description = "Scope + MediatR 12.5.0 Send (historical pin)")]
    public async Task<Pong> MediatRV12()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(Request);
    }
}

internal static class E3Scoped
{
    /// <summary>Scope validation is on so a captured scoped service would fail fast instead of silently benchmarking a bug.</summary>
    internal static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<IE3ScopedDependency, E3ScopedDependency>();
        services.AddTransient<E3ScopedPingMeridianHandler>();
        services.AddTransient<MeridianM.IRequestHandler<ScopedPing, Pong>>(sp => sp.GetRequiredService<E3ScopedPingMeridianHandler>());
        services.AddMeridianMediator(_ => { });
        services.AddTransient<MediatR.IRequestHandler<ScopedPing, Pong>, E3ScopedPingMediatRHandler>();
        E3MediatR.Add(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}

// Request/handler shapes visible to BOTH mediators; each library reads its own interfaces.

public sealed record Ping(int Value) : MeridianM.IRequest<Pong>, MediatR.IRequest<Pong>;

public sealed record Pong(int Value);

public sealed class PingMeridianHandler : MeridianM.IRequestHandler<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, CancellationToken cancellationToken)
    {
        E3Work.HandlerCalls++;
        return Task.FromResult(new Pong(request.Value + 1));
    }
}

public sealed class PingMediatRHandler : MediatR.IRequestHandler<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, CancellationToken cancellationToken)
    {
        E3Work.HandlerCalls++;
        return Task.FromResult(new Pong(request.Value + 1));
    }
}

public sealed record ScopedPing(int Value) : MeridianM.IRequest<Pong>, MediatR.IRequest<Pong>;

public interface IE3ScopedDependency
{
    int Id { get; }

    void Touch();
}

public sealed class E3ScopedDependency : IE3ScopedDependency, IDisposable
{
    private static int s_next;
    private static int s_disposed;
    private int _touches;

    public int Id { get; } = Interlocked.Increment(ref s_next);

    internal static int Disposed => Volatile.Read(ref s_disposed);

    public void Touch() => _touches++;

    public void Dispose()
    {
        Interlocked.Increment(ref s_disposed);
        _touches = 0;
    }
}

public sealed class E3ScopedPingMeridianHandler(IE3ScopedDependency dependency) : MeridianM.IRequestHandler<ScopedPing, Pong>
{
    public Task<Pong> Handle(ScopedPing request, CancellationToken cancellationToken)
    {
        dependency.Touch();
        E3Work.LastScopeId = dependency.Id;
        E3Work.HandlerCalls++;
        return Task.FromResult(new Pong(request.Value + 1));
    }
}

public sealed class E3ScopedPingMediatRHandler(IE3ScopedDependency dependency) : MediatR.IRequestHandler<ScopedPing, Pong>
{
    public Task<Pong> Handle(ScopedPing request, CancellationToken cancellationToken)
    {
        dependency.Touch();
        E3Work.LastScopeId = dependency.Id;
        E3Work.HandlerCalls++;
        return Task.FromResult(new Pong(request.Value + 1));
    }
}

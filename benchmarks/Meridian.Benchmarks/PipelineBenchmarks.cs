using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

// ============================================================================
// Pipeline benchmarks (E3): closed versus open behavior registration, cache and idempotency behavior overhead, metrics.
//
//  * Pipelines: 0, 1 or 4 pass-through behaviors, registered either as closed IPipelineBehavior<Ping, Pong> services or as
//    open generics, for Meridian runtime and MediatR 12.5.0 alike. Each behavior counts itself and calls next(); no logging,
//    sleeps or I/O. Preflight verifies the response, the handler call count and the exact behavior order.
//  * Cache and idempotency rows measure BEHAVIOR OVERHEAD with in-process providers. "synthetic" providers are bounded
//    deterministic doubles (always-miss, always-claim) so the measured state never drifts from a miss into a hit; the
//    "primed"/"replay" rows use real in-memory storage that was filled once during setup. None of this says anything
//    about Redis, EF Core or any production latency.
//  * Metrics rows use the accepted opt-in Meridian.Mediator metrics: disabled, enabled without a listener, and enabled with
//    an in-process MeterListener whose callbacks neither capture nor allocate. No exporter, no network.
// ============================================================================

/// <summary>Closed versus open pipeline registration with 0, 1 and 4 pass-through behaviors: Meridian runtime and MediatR 12.5.0.</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3MediatorPipelines
{
    private static readonly Ping Request = new(42);
    private ServiceProvider _meridianProvider = default!;
    private ServiceProvider _mediatRProvider = default!;
    private MeridianM.IMediator _meridian = default!;
    private MediatR.IMediator _mediatR = default!;

    /// <summary>none, then 1 or 4 behaviors with closed or open registration.</summary>
    [Params("none", "1-closed", "1-open", "4-closed", "4-open")]
    public string Shape { get; set; } = "none";

    private int Count => Shape == "none" ? 0 : int.Parse(Shape[..1], System.Globalization.CultureInfo.InvariantCulture);

    private bool Open => Shape.EndsWith("open", StringComparison.Ordinal);

    [GlobalSetup]
    public void Setup()
    {
        var meridian = new ServiceCollection();
        meridian.AddTransient<MeridianM.IRequestHandler<Ping, Pong>, PingMeridianHandler>();
        meridian.AddMeridianMediator(cfg =>
        {
            for (var i = 1; i <= Count; i++)
            {
                if (Open)
                {
                    cfg.AddOpenBehavior(MeridianOpen[i - 1]);
                }
                else
                {
                    cfg.AddClosedBehavior(typeof(MeridianM.Pipeline.IPipelineBehavior<Ping, Pong>), MeridianClosed[i - 1]);
                }
            }
        });
        _meridianProvider = meridian.BuildServiceProvider();
        _meridian = _meridianProvider.GetRequiredService<MeridianM.IMediator>();

        var mediatR = new ServiceCollection();
        mediatR.AddTransient<MediatR.IRequestHandler<Ping, Pong>, PingMediatRHandler>();
        E3MediatR.Add(mediatR, cfg =>
        {
            for (var i = 1; i <= Count; i++)
            {
                if (Open)
                {
                    cfg.AddOpenBehavior(MediatROpen[i - 1]);
                }
                else
                {
                    cfg.AddBehavior(typeof(MediatR.IPipelineBehavior<Ping, Pong>), MediatRClosed[i - 1]);
                }
            }
        });
        _mediatRProvider = mediatR.BuildServiceProvider();
        _mediatR = _mediatRProvider.GetRequiredService<MediatR.IMediator>();

        Preflight();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _meridianProvider.Dispose();
        _mediatRProvider.Dispose();
    }

    private static readonly Type[] MeridianClosed = [typeof(E3MeridianClosedBehavior1), typeof(E3MeridianClosedBehavior2), typeof(E3MeridianClosedBehavior3), typeof(E3MeridianClosedBehavior4)];
    private static readonly Type[] MeridianOpen = [typeof(E3MeridianOpenBehavior1<,>), typeof(E3MeridianOpenBehavior2<,>), typeof(E3MeridianOpenBehavior3<,>), typeof(E3MeridianOpenBehavior4<,>)];
    private static readonly Type[] MediatRClosed = [typeof(E3MediatRClosedBehavior1), typeof(E3MediatRClosedBehavior2), typeof(E3MediatRClosedBehavior3), typeof(E3MediatRClosedBehavior4)];
    private static readonly Type[] MediatROpen = [typeof(E3MediatROpenBehavior1<,>), typeof(E3MediatROpenBehavior2<,>), typeof(E3MediatROpenBehavior3<,>), typeof(E3MediatROpenBehavior4<,>)];

    /// <summary>Both libraries must run the same behaviors in the same order and the handler exactly once.</summary>
    public void Preflight()
    {
        var expected = Enumerable.Range(1, Count).ToArray();
        foreach (var (name, send) in new (string, Func<Task<Pong>>)[] { (nameof(MeridianRuntime), MeridianRuntime), (nameof(MediatRV12), MediatRV12) })
        {
            E3Work.Reset();
            E3Work.Recording = true;
            try
            {
                E3Work.RequirePong(name, send().GetAwaiter().GetResult(), 43);
            }
            finally
            {
                E3Work.Recording = false;
            }

            E3Work.Require(E3Work.HandlerCalls == 1, $"{name}/{Shape}: the handler ran {E3Work.HandlerCalls} times.");
            E3Work.Require(E3Work.BehaviorCalls == Count, $"{name}/{Shape}: {E3Work.BehaviorCalls} behaviors ran, expected {Count}.");
            E3Work.Require(E3Work.BehaviorLog.SequenceEqual(expected),
                $"{name}/{Shape}: behavior order was [{string.Join(",", E3Work.BehaviorLog)}], expected [{string.Join(",", expected)}].");
        }
    }

    [Benchmark(Baseline = true, Description = "Meridian runtime Send")]
    public Task<Pong> MeridianRuntime() => _meridian.Send(Request);

    [Benchmark(Description = "MediatR 12.5.0 Send (historical pin)")]
    public Task<Pong> MediatRV12() => _mediatR.Send(Request);
}

/// <summary>Meridian caching behavior overhead: disabled, primed in-memory hit, deterministic synthetic miss.</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3MediatorCacheBehavior
{
    private static readonly E3CachedQuery Request = new();
    private ServiceProvider _provider = default!;
    private MeridianM.IMediator _mediator = default!;
    private E3InMemoryCacheProvider? _memory;
    private E3AlwaysMissCacheProvider? _miss;

    /// <summary>disabled (no behavior; handler runs), primed-hit (in-memory store, handler skipped), synthetic-miss (handler runs, one write).</summary>
    [Params("disabled", "primed-hit", "synthetic-miss")]
    public string Mode { get; set; } = "disabled";

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddTransient<MeridianM.IRequestHandler<E3CachedQuery, int>, E3CachedQueryHandler>();
        if (Mode == "primed-hit")
        {
            _memory = new E3InMemoryCacheProvider();
            _memory.SetAsync(CacheKeyComposer.Compose<E3CachedQuery>(Request.CacheKey), 7, null, CancellationToken.None).GetAwaiter().GetResult();
            services.AddSingleton<ICacheProvider>(_memory);
        }
        else if (Mode == "synthetic-miss")
        {
            _miss = new E3AlwaysMissCacheProvider();
            services.AddSingleton<ICacheProvider>(_miss);
        }

        services.AddMeridianMediator(cfg =>
        {
            if (Mode != "disabled")
            {
                cfg.AddOpenBehavior(typeof(CachingBehavior<,>));
            }
        });
        _provider = services.BuildServiceProvider();
        _mediator = _provider.GetRequiredService<MeridianM.IMediator>();
        Preflight();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    /// <summary>A hit skips the handler; a miss runs it once and writes the expected value under the composed key.</summary>
    public void Preflight()
    {
        E3Work.Reset();
        var writesBefore = _miss?.Writes ?? 0;
        var result = _mediator.Send(Request).GetAwaiter().GetResult();
        E3Work.Require(result == 7, $"{Mode}: expected 7 but got {result}.");
        switch (Mode)
        {
            case "disabled":
                E3Work.Require(E3Work.HandlerCalls == 1, "disabled: the handler must run.");
                break;
            case "primed-hit":
                E3Work.Require(E3Work.HandlerCalls == 0, "primed-hit: a hit must skip the handler.");
                E3Work.Require(_memory!.Hits >= 1, "primed-hit: the in-memory provider was not consulted.");
                break;
            default:
                E3Work.Require(E3Work.HandlerCalls == 1, "synthetic-miss: a miss must run the handler exactly once.");
                E3Work.Require(_miss!.Writes == writesBefore + 1, "synthetic-miss: a miss must write exactly once.");
                E3Work.Require(_miss.LastKey == CacheKeyComposer.Compose<E3CachedQuery>(Request.CacheKey) && Equals(_miss.LastValue, 7),
                    "synthetic-miss: the written key or value is wrong.");
                break;
        }
    }

    [Benchmark(Description = "Send through the pipeline")]
    public Task<int> Send() => _mediator.Send(Request);
}

/// <summary>Meridian idempotency behavior overhead: disabled, completed replay (legacy and atomic stores), synthetic new claim (atomic).</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3MediatorIdempotencyBehavior
{
    private static readonly E3IdempotentCommand Request = new();
    private ServiceProvider _provider = default!;
    private MeridianM.IMediator _mediator = default!;
    private E3AlwaysClaimIdempotencyStore? _claim;

    /// <summary>disabled, legacy-replay (non-atomic store), atomic-replay (atomic store), atomic-synthetic-claim (always-claim atomic store).</summary>
    [Params("disabled", "legacy-replay", "atomic-replay", "atomic-synthetic-claim")]
    public string Mode { get; set; } = "disabled";

    [GlobalSetup]
    public void Setup()
    {
        var key = IdempotencyKeyComposer.Compose<E3IdempotentCommand>(Request.IdempotencyKey);
        var services = new ServiceCollection();
        services.AddTransient<MeridianM.IRequestHandler<E3IdempotentCommand, int>, E3IdempotentCommandHandler>();
        switch (Mode)
        {
            case "legacy-replay":
                services.AddSingleton<IIdempotencyStore>(new E3LegacyIdempotencyStore(key, 5));
                break;
            case "atomic-replay":
                services.AddSingleton<IIdempotencyStore>(new E3CompletedAtomicIdempotencyStore(key, 5));
                break;
            case "atomic-synthetic-claim":
                _claim = new E3AlwaysClaimIdempotencyStore();
                services.AddSingleton<IIdempotencyStore>(_claim);
                break;
        }

        services.AddMeridianMediator(cfg =>
        {
            if (Mode != "disabled")
            {
                cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
            }
        });
        _provider = services.BuildServiceProvider();
        _mediator = _provider.GetRequiredService<MeridianM.IMediator>();
        Preflight();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    /// <summary>A replay skips the handler; a new claim runs it once and records completion with the response.</summary>
    public void Preflight()
    {
        E3Work.Reset();
        var completedBefore = _claim?.Completions ?? 0;
        var result = _mediator.Send(Request).GetAwaiter().GetResult();
        E3Work.Require(result == 5, $"{Mode}: expected 5 but got {result}.");
        if (Mode is "legacy-replay" or "atomic-replay")
        {
            E3Work.Require(E3Work.HandlerCalls == 0, $"{Mode}: a completed replay must skip the handler.");
        }
        else
        {
            E3Work.Require(E3Work.HandlerCalls == 1, $"{Mode}: the handler must run exactly once.");
        }

        if (Mode == "atomic-synthetic-claim")
        {
            E3Work.Require(_claim!.Completions == completedBefore + 1 && Equals(_claim.LastResponse, 5), "atomic-synthetic-claim: completion was not recorded once with the response.");
        }
    }

    [Benchmark(Description = "Send through the pipeline")]
    public Task<int> Send() => _mediator.Send(Request);
}

/// <summary>Meridian metrics: disabled, enabled with no listener, enabled with an allocation-free in-process listener (typed Send).</summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3MediatorMetrics : IDisposable
{
    private static readonly Ping Request = new(42);
    private static long s_longEvents;
    private static long s_doubleEvents;
    private ServiceProvider _provider = default!;
    private MeridianM.IMediator _mediator = default!;
    private MeterListener? _listener;

    [Params("disabled", "enabled-no-listener", "enabled-listener")]
    public string Mode { get; set; } = "disabled";

    [GlobalSetup]
    public void Setup()
    {
        // A recording probe is attached BEFORE the provider exists so it sees the instruments being created. It verifies the
        // exact events of three sends and is disposed before any timing, so it never counts as the benchmark's listener.
        var recorded = new List<(string Name, double Value, string? Type, string? Outcome)>();
        var published = new List<string>();
        using (var probe = new MeterListener())
        {
            probe.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Meridian.Mediator")
                {
                    published.Add(instrument.Name);
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            probe.SetMeasurementEventCallback<long>((i, m, tags, _) => recorded.Add((i.Name, m, Tag(tags, "request.type"), Tag(tags, "outcome"))));
            probe.SetMeasurementEventCallback<double>((i, m, tags, _) => recorded.Add((i.Name, m, Tag(tags, "request.type"), Tag(tags, "outcome"))));
            probe.Start();

            var services = new ServiceCollection();
            services.AddTransient<MeridianM.IRequestHandler<Ping, Pong>, PingMeridianHandler>();
            services.AddMeridianMediator(cfg =>
            {
                if (Mode != "disabled")
                {
                    cfg.Metrics = new MeridianM.MediatorMetricsOptions { Enabled = true };
                }
            });
            _provider = services.BuildServiceProvider();
            _mediator = _provider.GetRequiredService<MeridianM.IMediator>();

            for (var i = 0; i < 3; i++)
            {
                E3Work.RequirePong(Mode, _mediator.Send(Request).GetAwaiter().GetResult(), 43);
            }

            if (Mode == "disabled")
            {
                E3Work.Require(published.Count == 0 && recorded.Count == 0, "disabled: a disabled provider must create no instrument and emit no event.");
            }
            else
            {
                E3Work.Require(published.Count == 10, $"{Mode}: expected the ten documented instruments but {published.Count} were published.");
                var counts = recorded.Where(r => r.Name == "meridian.mediator.request.count").ToList();
                var durations = recorded.Where(r => r.Name == "meridian.mediator.request.duration").ToList();
                E3Work.Require(counts.Count == 3 && counts.All(c => c.Value == 1 && c.Outcome == "success" && c.Type is not null && c.Type.Contains("Ping", StringComparison.Ordinal)),
                    $"{Mode}: expected three success request.count events of 1 but got [{string.Join("; ", counts)}].");
                E3Work.Require(durations.Count == 3 && durations.All(d => d.Outcome == "success"), $"{Mode}: expected three success request.duration events.");
                E3Work.Require(recorded.Count == 6, $"{Mode}: expected exactly 6 events for three sends but saw {recorded.Count}.");
            }
        }

        if (Mode == "enabled-listener")
        {
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "Meridian.Mediator")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => s_longEvents++);
            _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => s_doubleEvents++);
            _listener.Start();
        }

        Preflight();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _provider.Dispose();
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    /// <summary>The business result is identical in every mode; only the listener mode observes exactly one count and one duration per send.</summary>
    public void Preflight()
    {
        var longBefore = s_longEvents;
        var doubleBefore = s_doubleEvents;
        E3Work.Reset();
        E3Work.RequirePong(Mode, _mediator.Send(Request).GetAwaiter().GetResult(), 43);
        E3Work.Require(E3Work.HandlerCalls == 1, $"{Mode}: the handler ran {E3Work.HandlerCalls} times.");
        var expected = Mode == "enabled-listener" ? 1 : 0;
        E3Work.Require(s_longEvents - longBefore == expected && s_doubleEvents - doubleBefore == expected,
            $"{Mode}: the benchmark listener saw {s_longEvents - longBefore} counter and {s_doubleEvents - doubleBefore} duration events, expected {expected} each.");
    }

    [Benchmark(Description = "typed Send")]
    public Task<Pong> Send() => _mediator.Send(Request);
}

// ---- Cache and idempotency doubles (bounded, deterministic, in-process) ----

public sealed class E3CachedQuery : MeridianM.IRequest<int>, ICacheableQuery
{
    public string CacheKey => "e3";

    public TimeSpan? CacheDuration => null;
}

public sealed class E3CachedQueryHandler : MeridianM.IRequestHandler<E3CachedQuery, int>
{
    public Task<int> Handle(E3CachedQuery request, CancellationToken cancellationToken)
    {
        E3Work.HandlerCalls++;
        return Task.FromResult(7);
    }
}

/// <summary>A real dictionary-backed in-memory provider (one bounded key, no expiry, no eviction).</summary>
public sealed class E3InMemoryCacheProvider : ICacheProvider
{
    private readonly ConcurrentDictionary<string, object> _store = new(StringComparer.Ordinal);

    public long Hits { get; private set; }

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_store.TryGetValue(key, out var value))
        {
            Hits++;
            return Task.FromResult<(bool, object?)>((true, value));
        }

        return Task.FromResult<(bool, object?)>((false, null));
    }

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Synthetic: always reports a miss and only counts (and remembers the last) write, so state never drifts into a hit.</summary>
public sealed class E3AlwaysMissCacheProvider : ICacheProvider
{
    public long Writes { get; private set; }

    public string? LastKey { get; private set; }

    public object? LastValue { get; private set; }

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<(bool, object?)>((false, null));

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        Writes++;
        LastKey = key;
        LastValue = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class E3IdempotentCommand : MeridianM.IRequest<int>, IIdempotentRequest
{
    public string IdempotencyKey => "e3";
}

public sealed class E3IdempotentCommandHandler : MeridianM.IRequestHandler<E3IdempotentCommand, int>
{
    public Task<int> Handle(E3IdempotentCommand request, CancellationToken cancellationToken)
    {
        E3Work.HandlerCalls++;
        return Task.FromResult(5);
    }
}

/// <summary>A real in-memory NON-atomic store holding one completed response.</summary>
public sealed class E3LegacyIdempotencyStore(string key, int response) : IIdempotencyStore
{
    private readonly Dictionary<string, object> _completed = new(StringComparer.Ordinal) { [key] = response };

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
        Task.FromResult<(bool, object?)>(_completed.TryGetValue(key, out var value) ? (true, value) : (false, null));

    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _completed[key] = response;
        return Task.CompletedTask;
    }
}

/// <summary>A real in-memory ATOMIC store holding one completed response (a different contract than the legacy store).</summary>
public sealed class E3CompletedAtomicIdempotencyStore(string key, int response) : IAtomicIdempotencyStore
{
    private readonly Dictionary<string, object?> _completed = new(StringComparer.Ordinal) { [key] = response };

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
        Task.FromResult<(bool, object?)>(_completed.TryGetValue(key, out var value) ? (true, value) : (false, null));

    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _completed[key] = response;
        return Task.CompletedTask;
    }

    public Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct) =>
        Task.FromResult(_completed.TryGetValue(key, out var value)
            ? new IdempotencyClaimResult(IdempotencyClaimStatus.Completed, value)
            : new IdempotencyClaimResult(IdempotencyClaimStatus.Claimed));

    public Task CompleteAsync(string key, object? response, CancellationToken ct)
    {
        _completed[key] = response;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Synthetic: every claim succeeds and completion is only counted, so the measured state is always a new claim.</summary>
public sealed class E3AlwaysClaimIdempotencyStore : IAtomicIdempotencyStore
{
    public long Completions { get; private set; }

    public object? LastResponse { get; private set; }

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
        Task.FromResult<(bool, object?)>((false, null));

    public Task StoreAsync(string key, object response, CancellationToken ct) => Task.CompletedTask;

    public Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct) =>
        Task.FromResult(new IdempotencyClaimResult(IdempotencyClaimStatus.Claimed));

    public Task CompleteAsync(string key, object? response, CancellationToken ct)
    {
        Completions++;
        LastResponse = response;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken ct) => Task.CompletedTask;
}

// ---- Pass-through behaviors: identical advertised work (count, then next()) for both libraries ----

public sealed class E3MeridianClosedBehavior1 : MeridianM.Pipeline.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MeridianM.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(1);
        return next();
    }
}

public sealed class E3MeridianOpenBehavior1<TRequest, TResponse> : MeridianM.Pipeline.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MeridianM.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(1);
        return next();
    }
}

public sealed class E3MeridianClosedBehavior2 : MeridianM.Pipeline.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MeridianM.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(2);
        return next();
    }
}

public sealed class E3MeridianOpenBehavior2<TRequest, TResponse> : MeridianM.Pipeline.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MeridianM.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(2);
        return next();
    }
}

public sealed class E3MeridianClosedBehavior3 : MeridianM.Pipeline.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MeridianM.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(3);
        return next();
    }
}

public sealed class E3MeridianOpenBehavior3<TRequest, TResponse> : MeridianM.Pipeline.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MeridianM.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(3);
        return next();
    }
}

public sealed class E3MeridianClosedBehavior4 : MeridianM.Pipeline.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MeridianM.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(4);
        return next();
    }
}

public sealed class E3MeridianOpenBehavior4<TRequest, TResponse> : MeridianM.Pipeline.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MeridianM.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(4);
        return next();
    }
}

public sealed class E3MediatRClosedBehavior1 : MediatR.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MediatR.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(1);
        return next(cancellationToken);
    }
}

public sealed class E3MediatROpenBehavior1<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(1);
        return next(cancellationToken);
    }
}

public sealed class E3MediatRClosedBehavior2 : MediatR.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MediatR.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(2);
        return next(cancellationToken);
    }
}

public sealed class E3MediatROpenBehavior2<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(2);
        return next(cancellationToken);
    }
}

public sealed class E3MediatRClosedBehavior3 : MediatR.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MediatR.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(3);
        return next(cancellationToken);
    }
}

public sealed class E3MediatROpenBehavior3<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(3);
        return next(cancellationToken);
    }
}

public sealed class E3MediatRClosedBehavior4 : MediatR.IPipelineBehavior<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, MediatR.RequestHandlerDelegate<Pong> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(4);
        return next(cancellationToken);
    }
}

public sealed class E3MediatROpenBehavior4<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        E3Work.Hit(4);
        return next(cancellationToken);
    }
}

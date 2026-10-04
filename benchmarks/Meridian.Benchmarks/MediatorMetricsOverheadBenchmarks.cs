using System.Diagnostics;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Benchmarks;

/// <summary>
/// Fixed D5 measurement harness. It uses only the public mediator API that exists both before and after
/// the bounded metrics change, so the identical source (and settings) can be compiled against the baseline
/// product (P0) and the instrumented product (P1). The metrics options type is located by name; on the
/// baseline it does not exist and every <see cref="Mode"/> row measures the same unchanged code, which is
/// retained as the same-binary noise floor.
/// </summary>
internal static class MetricsBenchmarkHost
{
    internal const string Disabled = "disabled";
    internal const string EnabledNoListener = "enabled-no-listener";
    internal const string EnabledListener = "enabled-listener";

    internal static long Measurements;
    internal static long DoubleMeasurements;

    internal static bool MetricsApiPresent =>
        typeof(IMediator).Assembly.GetType("Meridian.Mediator.MediatorMetricsOptions") is not null;

    /// <summary>Registers an enabled metrics options instance when the product has the opt-in API.</summary>
    internal static void ConfigureMetrics(IServiceCollection services, string mode)
    {
        if (mode == Disabled) return;
        var type = typeof(IMediator).Assembly.GetType("Meridian.Mediator.MediatorMetricsOptions");
        if (type is null) return;
        var options = Activator.CreateInstance(type)!;
        type.GetProperty("Enabled")!.SetValue(options, true);
        services.AddSingleton(type, options);
    }

    /// <summary>Starts a constant-callback listener (no capture, no allocation per measurement).</summary>
    internal static MeterListener? StartListener(string mode)
    {
        if (mode != EnabledListener) return null;
        var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, l) =>
            {
                if (instrument.Meter.Name == "Meridian.Mediator") l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => Measurements++);
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => DoubleMeasurements++);
        listener.Start();
        return listener;
    }

    internal static void Verify(string mode, long before, string what)
    {
        var delta = Measurements + DoubleMeasurements - before;
        if (MetricsApiPresent && mode == EnabledListener)
        {
            if (delta <= 0) throw new InvalidOperationException(what + ": the enabled listener observed no measurements.");
        }
        else if (delta != 0)
        {
            throw new InvalidOperationException(what + ": unexpected measurements in mode " + mode + ".");
        }
    }
}

/// <summary>
/// D5 No_Listener_Hot_Path_Has_No_New_Allocation: five dispatch shapes in three metrics modes. Compare the
/// <c>Allocated</c> column of baseline P0 against P1 rows; the criterion is a zero allocation delta for the
/// disabled and enabled/no-listener modes, not zero absolute allocation.
/// </summary>
[MemoryDiagnoser]
public class No_Listener_Hot_Path_Has_No_New_Allocation
{
    private IMediator _mediator = default!;
    private ServiceProvider _provider = default!;
    private MeterListener? _listener;
    private readonly HotQuery _query = new(41);
    private readonly HotCommand _command = new();
    private readonly HotNotification _notification = new();
    private readonly HotStream _stream = new();

    [Params(MetricsBenchmarkHost.Disabled, MetricsBenchmarkHost.EnabledNoListener, MetricsBenchmarkHost.EnabledListener)]
    public string Mode { get; set; } = MetricsBenchmarkHost.Disabled;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<HotQuery, int>, HotQueryHandler>();
        services.AddTransient<IRequestHandler<HotCommand>, HotCommandHandler>();
        services.AddTransient<IStreamRequestHandler<HotStream, int>, HotStreamHandler>();
        services.AddTransient<INotificationHandler<HotNotification>, HotNotificationHandler1>();
        services.AddTransient<INotificationHandler<HotNotification>, HotNotificationHandler2>();
        services.AddTransient<INotificationHandler<HotNotification>, HotNotificationHandler3>();
        services.AddTransient<INotificationHandler<HotNotification>, HotNotificationHandler4>();
        MetricsBenchmarkHost.ConfigureMetrics(services, Mode);
        services.AddMeridianMediator(_ => { });
        _listener = MetricsBenchmarkHost.StartListener(Mode);
        _provider = services.BuildServiceProvider();
        _mediator = _provider.GetRequiredService<IMediator>();

        // Exact semantic preflight, then identical warm-up for every row.
        var before = MetricsBenchmarkHost.Measurements + MetricsBenchmarkHost.DoubleMeasurements;
        for (var i = 0; i < 64; i++)
        {
            if (await Typed() != 42 || (int)(await Object())! != 42) throw new InvalidOperationException("send preflight");
            await Void();
            await Notification();
            if (await Stream() != 3) throw new InvalidOperationException("stream preflight");
        }
        MetricsBenchmarkHost.Verify(Mode, before, nameof(No_Listener_Hot_Path_Has_No_New_Allocation));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _provider.Dispose();
    }

    [Benchmark(Description = "typed Send")]
    public Task<int> Typed() => _mediator.Send(_query);

    [Benchmark(Description = "object Send")]
    public Task<object?> Object() => _mediator.Send((object)_query);

    [Benchmark(Description = "void Send")]
    public Task Void() => _mediator.Send(_command);

    [Benchmark(Description = "Publish x4")]
    public Task Notification() => _mediator.Publish(_notification);

    [Benchmark(Description = "stream x2 consumed")]
    public async Task<int> Stream()
    {
        var total = 0;
        await foreach (var item in _mediator.CreateStream(_stream)) total += item;
        return total;
    }
}

/// <summary>Paired Activity-only versus Activity plus enabled metrics (no metrics listener) tracing-branch case.</summary>
[MemoryDiagnoser]
public class Activity_Listener_Hot_Path
{
    private IMediator _mediator = default!;
    private ServiceProvider _provider = default!;
    private ActivityListener _activityListener = default!;
    private MeterListener? _listener;
    private readonly HotQuery _query = new(41);
    private readonly HotStream _stream = new();

    [Params(MetricsBenchmarkHost.Disabled, MetricsBenchmarkHost.EnabledNoListener, MetricsBenchmarkHost.EnabledListener)]
    public string Mode { get; set; } = MetricsBenchmarkHost.Disabled;

    [GlobalSetup]
    public async Task Setup()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Meridian.Mediator",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(_activityListener);
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<HotQuery, int>, HotQueryHandler>();
        services.AddTransient<IStreamRequestHandler<HotStream, int>, HotStreamHandler>();
        MetricsBenchmarkHost.ConfigureMetrics(services, Mode);
        services.AddMeridianMediator(_ => { });
        _listener = MetricsBenchmarkHost.StartListener(Mode);
        _provider = services.BuildServiceProvider();
        _mediator = _provider.GetRequiredService<IMediator>();
        var before = MetricsBenchmarkHost.Measurements + MetricsBenchmarkHost.DoubleMeasurements;
        for (var i = 0; i < 64; i++)
        {
            if (await Typed() != 42) throw new InvalidOperationException("send preflight");
            if (await Stream() != 3) throw new InvalidOperationException("stream preflight");
        }
        MetricsBenchmarkHost.Verify(Mode, before, nameof(Activity_Listener_Hot_Path));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _activityListener.Dispose();
        _provider.Dispose();
    }

    // Send starts its Activity in the caller's execution context. Running each operation inside an async
    // method keeps that ambient state out of the benchmark loop's context (identical for every row).
    [Benchmark(Description = "typed Send, Activity listener")]
    public async Task<int> Typed() => await _mediator.Send(_query);

    [Benchmark(Description = "stream x2, Activity listener")]
    public async Task<int> Stream()
    {
        var total = 0;
        await foreach (var item in _mediator.CreateStream(_stream)) total += item;
        return total;
    }
}

/// <summary>Three focused pipeline rows: primed cache hit, one zero-delay retry, completed idempotency replay.</summary>
[MemoryDiagnoser]
public class Pipeline_Event_Overhead
{
    private IMediator _mediator = default!;
    private ServiceProvider _provider = default!;
    private MeterListener? _listener;
    private readonly HotCachedQuery _cached = new();
    private readonly HotIdempotentCommand _idempotent = new();

    [Params(MetricsBenchmarkHost.Disabled, MetricsBenchmarkHost.EnabledNoListener, MetricsBenchmarkHost.EnabledListener)]
    public string Mode { get; set; } = MetricsBenchmarkHost.Disabled;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<HotCachedQuery, int>, HotCachedQueryHandler>();
        services.AddTransient<IRequestHandler<HotRetryRequest, int>, HotRetryRequestHandler>();
        services.AddTransient<IRequestHandler<HotIdempotentCommand, int>, HotIdempotentCommandHandler>();
        services.AddSingleton<ICacheProvider, HotCacheProvider>();
        services.AddSingleton<IIdempotencyStore, HotIdempotencyStore>();
        MetricsBenchmarkHost.ConfigureMetrics(services, Mode);
        services.AddMeridianMediator(cfg =>
        {
            cfg.AddOpenBehavior(typeof(CachingBehavior<,>));
            cfg.AddOpenBehavior(typeof(RetryBehavior<,>));
            cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
        });
        _listener = MetricsBenchmarkHost.StartListener(Mode);
        _provider = services.BuildServiceProvider();
        _mediator = _provider.GetRequiredService<IMediator>();
        var before = MetricsBenchmarkHost.Measurements + MetricsBenchmarkHost.DoubleMeasurements;
        for (var i = 0; i < 64; i++)
        {
            if (await CacheHit() != 7 || await RetryOnce() != 9 || await IdempotentReplay() != 5)
                throw new InvalidOperationException("pipeline preflight");
        }
        MetricsBenchmarkHost.Verify(Mode, before, nameof(Pipeline_Event_Overhead));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _provider.Dispose();
    }

    [Benchmark(Description = "primed cache hit")]
    public Task<int> CacheHit() => _mediator.Send(_cached);

    [Benchmark(Description = "one zero-delay retry")]
    public Task<int> RetryOnce() => _mediator.Send(new HotRetryRequest());

    [Benchmark(Description = "idempotency replay")]
    public Task<int> IdempotentReplay() => _mediator.Send(_idempotent);
}

public sealed record HotQuery(int Value) : IRequest<int>;

public sealed class HotQueryHandler : IRequestHandler<HotQuery, int>
{
    public Task<int> Handle(HotQuery request, CancellationToken cancellationToken) => Task.FromResult(request.Value + 1);
}

public sealed class HotCommand : IRequest;

public sealed class HotCommandHandler : IRequestHandler<HotCommand>
{
    public Task Handle(HotCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HotNotification : INotification;

public sealed class HotNotificationHandler1 : INotificationHandler<HotNotification>
{
    public Task Handle(HotNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HotNotificationHandler2 : INotificationHandler<HotNotification>
{
    public Task Handle(HotNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HotNotificationHandler3 : INotificationHandler<HotNotification>
{
    public Task Handle(HotNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HotNotificationHandler4 : INotificationHandler<HotNotification>
{
    public Task Handle(HotNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HotStream : IStreamRequest<int>;

public sealed class HotStreamHandler : IStreamRequestHandler<HotStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        HotStream request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.CompletedTask;
        yield return 2;
    }
}

public sealed class HotCachedQuery : IRequest<int>, ICacheableQuery
{
    public string CacheKey => "hot";
    public TimeSpan? CacheDuration => null;
}

public sealed class HotCachedQueryHandler : IRequestHandler<HotCachedQuery, int>
{
    public Task<int> Handle(HotCachedQuery request, CancellationToken cancellationToken) => Task.FromResult(-1);
}

public sealed class HotCacheProvider : ICacheProvider
{
    private static readonly object Boxed = 7;
    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<(bool, object?)>((true, Boxed));
    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HotRetryRequest : IRequest<int>, IRetryableRequest
{
    public int Attempts;
    public int MaxRetries => 1;
    public TimeSpan RetryDelay => TimeSpan.Zero;
}

public sealed class HotRetryRequestHandler : IRequestHandler<HotRetryRequest, int>
{
    public Task<int> Handle(HotRetryRequest request, CancellationToken cancellationToken)
    {
        if (request.Attempts++ == 0) throw new InvalidOperationException("transient");
        return Task.FromResult(9);
    }
}

public sealed class HotIdempotentCommand : IRequest<int>, IIdempotentRequest
{
    public string IdempotencyKey => "hot";
}

public sealed class HotIdempotentCommandHandler : IRequestHandler<HotIdempotentCommand, int>
{
    public Task<int> Handle(HotIdempotentCommand request, CancellationToken cancellationToken) => Task.FromResult(-1);
}

public sealed class HotIdempotencyStore : IIdempotencyStore
{
    private static readonly object Boxed = 5;
    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
        Task.FromResult<(bool, object?)>((true, Boxed));
    public Task StoreAsync(string key, object response, CancellationToken ct) => Task.CompletedTask;
}

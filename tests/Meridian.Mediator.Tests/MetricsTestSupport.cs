#pragma warning disable CA1707 // Contract test names are deliberately descriptive.
using System.Diagnostics.Metrics;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

/// <summary>Metrics tests attach process-wide listeners, so they never overlap with other test classes.</summary>
[CollectionDefinition("Metrics", DisableParallelization = true)]
public sealed class MetricsCollectionDefinition
{
}

internal static class Mx
{
    internal const string RequestCount = "meridian.mediator.request.count";
    internal const string RequestDuration = "meridian.mediator.request.duration";
    internal const string NotificationCount = "meridian.mediator.notification.count";
    internal const string StreamCount = "meridian.mediator.stream.count";
    internal const string StreamDuration = "meridian.mediator.stream.duration";
    internal const string CacheLookup = "meridian.mediator.cache.lookup.count";
    internal const string RetryAttempt = "meridian.mediator.retry.attempt.count";
    internal const string ValidationRejection = "meridian.mediator.validation.rejection.count";
    internal const string AuthorizationRejection = "meridian.mediator.authorization.rejection.count";
    internal const string IdempotencyState = "meridian.mediator.idempotency.state.count";

    internal static MediatorMetricsOptions On(int maximum = 100) => new() { Enabled = true, MaximumRequestTypes = maximum };

    internal static string Label(Type type) => MediatorMetrics.DescribeType(type)!;

    /// <summary>Asserts that a Task-returning call throws synchronously (not through the returned task).</summary>
    internal static T ThrowsSync<T>(Func<object> call) where T : Exception => Assert.Throws<T>(() => { _ = call(); });

    /// <summary>Registers handlers before the mediator (so their types are known), then the options and the mediator.</summary>
    internal static ServiceProvider Build(
        Action<IServiceCollection>? register = null,
        Action<MeridianMediatorConfiguration>? configure = null,
        MediatorMetricsOptions? options = null,
        Action<IServiceCollection>? afterMediator = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<MxScript>();
        register?.Invoke(services);
        if (options is not null)
        {
            services.AddSingleton(options);
        }

        services.AddMeridianMediator(c => configure?.Invoke(c));
        afterMediator?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    internal static ServiceProvider BuildStandard(
        MediatorMetricsOptions? options = null, Action<MeridianMediatorConfiguration>? configure = null)
    {
        return Build(
            s =>
            {
                s.AddTransient<IRequestHandler<MxQuery, int>, MxQueryHandler>();
                s.AddTransient<IRequestHandler<MxCommand>, MxCommandHandler>();
                s.AddTransient<INotificationHandler<MxNote>, MxNoteHandler>();
                s.AddTransient<IStreamRequestHandler<MxStream, int>, MxStreamHandler>();
            },
            configure,
            options ?? On());
    }

    internal static MediatorMetrics MetricsOf(ServiceProvider provider) => provider.GetRequiredService<MediatorMetrics>();
}

/// <summary>Copies every measurement of one meter instance (or every meter with a name) into an owned list.</summary>
internal sealed class MetricCapture : IDisposable
{
    internal sealed record Measurement(string Instrument, string? Unit, string Kind, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        internal string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value as string : null;
    }

    internal sealed record Published(string Name, string? Unit, Type InstrumentType);

    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private readonly List<Measurement> _measurements = new();
    private readonly List<Published> _published = new();

    internal MetricCapture(Meter? meter, string? meterName = null, bool throwFromCallbacks = false, Func<Instrument, bool>? include = null)
    {
        ThrowFromCallbacks = throwFromCallbacks;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (((meter is not null && ReferenceEquals(instrument.Meter, meter)) || (meterName is not null && instrument.Meter.Name == meterName)) && (include is null || include(instrument)))
            {
                lock (_gate)
                {
                    _published.Add(new Published(instrument.Name, instrument.Unit, instrument.GetType()));
                }

                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, m, t, _) => Add(i, "long", m, t));
        _listener.SetMeasurementEventCallback<double>((i, m, t, _) => Add(i, "double", m, t));
        _listener.Start();
    }

    internal bool ThrowFromCallbacks { get; set; }

    internal IReadOnlyList<Measurement> All
    {
        get
        {
            lock (_gate)
            {
                return _measurements.ToArray();
            }
        }
    }

    internal IReadOnlyList<Published> PublishedInstruments
    {
        get
        {
            lock (_gate)
            {
                return _published.ToArray();
            }
        }
    }

    internal IReadOnlyList<Measurement> Of(string instrument) => All.Where(m => m.Instrument == instrument).ToArray();

    internal double Sum(string instrument, params (string Key, string Value)[] tags) =>
        Of(instrument).Where(m => tags.All(t => m.Tag(t.Key) == t.Value)).Sum(m => m.Value);

    internal int CountOf(string instrument, params (string Key, string Value)[] tags) =>
        Of(instrument).Count(m => tags.All(t => m.Tag(t.Key) == t.Value));

    private void Add(Instrument instrument, string kind, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument.Name, instrument.Unit, kind, value, copy));
        }

        if (ThrowFromCallbacks)
        {
            throw new InvalidOperationException("listener fault");
        }
    }

    public void Dispose() => _listener.Dispose();
}

public sealed record MxQuery(int Value) : IRequest<int>;

public sealed class MxCommand : IRequest;

public sealed class MxNote : INotification;

public sealed class MxStream : IStreamRequest<int>;

public sealed class MxGeneric<T> : IRequest<int>;

/// <summary>Scriptable behavior shared by the metrics test handlers.</summary>
public sealed class MxScript
{
    public Func<MxQuery, CancellationToken, Task<int>> Query { get; set; } = static (q, _) => Task.FromResult(q.Value + 1);

    public Func<MxCommand, CancellationToken, Task> Command { get; set; } = static (_, _) => Task.CompletedTask;

    public Func<MxNote, CancellationToken, Task> Note { get; set; } = static (_, _) => Task.CompletedTask;

    public Func<MxStream, CancellationToken, IAsyncEnumerable<int>> Stream { get; set; } = static (_, ct) => Items(ct, 1, 2);

    public static async IAsyncEnumerable<int> Items(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, params int[] values)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return value;
        }
    }
}

public sealed class MxQueryHandler(MxScript script) : IRequestHandler<MxQuery, int>
{
    public Task<int> Handle(MxQuery request, CancellationToken cancellationToken) => script.Query(request, cancellationToken);
}

public sealed class MxCommandHandler(MxScript script) : IRequestHandler<MxCommand>
{
    public Task Handle(MxCommand request, CancellationToken cancellationToken) => script.Command(request, cancellationToken);
}

public sealed class MxNoteHandler(MxScript script) : INotificationHandler<MxNote>
{
    public Task Handle(MxNote notification, CancellationToken cancellationToken) => script.Note(notification, cancellationToken);
}

public sealed class MxNoteSecondHandler(MxScript script) : INotificationHandler<MxNote>
{
    public Task Handle(MxNote notification, CancellationToken cancellationToken) => script.Note(notification, cancellationToken);
}

public sealed class MxStreamHandler(MxScript script) : IStreamRequestHandler<MxStream, int>
{
    public IAsyncEnumerable<int> Handle(MxStream request, CancellationToken cancellationToken) => script.Stream(request, cancellationToken);
}

public sealed class MxGenericHandler<T> : IRequestHandler<MxGeneric<T>, int>
{
    public Task<int> Handle(MxGeneric<T> request, CancellationToken cancellationToken) => Task.FromResult(0);
}

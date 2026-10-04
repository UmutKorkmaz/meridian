#pragma warning disable CA1707 // Contract test names are deliberately descriptive.
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Tests;

[Collection("Metrics")]
public class MetricsOperationTests
{
    private static readonly string QueryLabel = Mx.Label(typeof(MxQuery));
    private static readonly string CommandLabel = Mx.Label(typeof(MxCommand));

    private static (string, string) Type(string label) => ("request.type", label);

    private static (string, string) Outcome(string outcome) => ("outcome", outcome);

    [Fact]
    public async Task Send_Records_One_Terminal_Count_And_One_Duration_For_Each_Shape()
    {
        using var provider = Mx.BuildStandard();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Equal(2, await mediator.Send(new MxQuery(1)));
        Assert.Equal(3, await mediator.Send((object)new MxQuery(2)) is int value ? value : -1);
        await mediator.Send(new MxCommand());

        Assert.Equal(2, capture.Sum(Mx.RequestCount, Type(QueryLabel), Outcome("success")));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(CommandLabel), Outcome("success")));
        Assert.Equal(3, capture.Of(Mx.RequestCount).Count);
        Assert.Equal(3, capture.Of(Mx.RequestDuration).Count);
        Assert.All(capture.Of(Mx.RequestDuration), m =>
        {
            Assert.Equal("ms", m.Unit);
            Assert.Equal("double", m.Kind);
            Assert.True(double.IsFinite(m.Value) && m.Value >= 0);
        });
        Assert.All(capture.Of(Mx.RequestCount), m =>
        {
            Assert.Equal("{operation}", m.Unit);
            Assert.Equal(1, m.Value);
        });
    }

    [Fact]
    public void Duration_Is_Converted_To_Milliseconds()
    {
        Assert.Equal(1500d, MediatorMetrics.ToMilliseconds(1_500_000_000, 1_000_000_000));
        Assert.Equal(1500d, MediatorMetrics.ToMilliseconds((long)(1.5 * Stopwatch.Frequency), Stopwatch.Frequency), 6);
        Assert.Equal(0d, MediatorMetrics.ToMilliseconds(-5, 1_000_000_000));
    }

    [Fact]
    public async Task Blocked_Handler_Records_Nothing_Until_Completion_And_Duration_Brackets_Real_Time()
    {
        using var provider = Mx.BuildStandard();
        var script = provider.GetRequiredService<MxScript>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        script.Query = (_, _) => gate.Task;
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        var started = Stopwatch.GetTimestamp();
        var task = mediator.Send(new MxQuery(1));
        await Task.Delay(150);
        Assert.False(task.IsCompleted);
        Assert.Empty(capture.All);

        gate.SetResult(7);
        Assert.Equal(7, await task);
        var elapsedMs = MediatorMetrics.ToMilliseconds(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency);
        var measurement = Assert.Single(capture.Of(Mx.RequestDuration));
        Assert.InRange(measurement.Value, 100, elapsedMs + 1);
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Outcome("success")));
    }

    [Fact]
    public async Task Failures_Are_Counted_Once_With_Original_Exception_And_Timing()
    {
        var original = new InvalidOperationException("boom");
        using var provider = Mx.BuildStandard();
        var script = provider.GetRequiredService<MxScript>();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        // Synchronous handler throw: still thrown synchronously from Send.
        script.Query = (_, _) => throw original;
        var thrown = Mx.ThrowsSync<InvalidOperationException>(() => mediator.Send(new MxQuery(1)));
        Assert.Same(original, thrown);

        // Asynchronously faulted task: the same exception object is observed through the returned task.
        script.Query = (_, _) => Task.FromException<int>(original);
        var faulted = mediator.Send(new MxQuery(1));
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => faulted));

        // An unrelated cancellation (no canceled token) is a failure.
        script.Query = (_, _) => Task.FromException<int>(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => mediator.Send(new MxQuery(1)));

        Assert.Equal(3, capture.Sum(Mx.RequestCount, Type(QueryLabel), Outcome("failure")));
        Assert.Equal(0, capture.Sum(Mx.RequestCount, Outcome("success")));
        Assert.Equal(0, capture.Sum(Mx.RequestCount, Outcome("cancellation")));
        Assert.Equal(3, capture.Of(Mx.RequestDuration).Count);
    }

    [Fact]
    public async Task Cancellation_Requires_A_Canceled_Supplied_Token()
    {
        using var provider = Mx.BuildStandard();
        var script = provider.GetRequiredService<MxScript>();
        script.Query = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        };
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();
        using var cts = new CancellationTokenSource();
        var task = mediator.Send(new MxQuery(1), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(QueryLabel), Outcome("cancellation")));
        Assert.Equal(0, capture.Sum(Mx.RequestCount, Outcome("failure")));
        Assert.Single(capture.Of(Mx.RequestDuration));
    }

    [Fact]
    public async Task Synchronous_Dispatch_Failures_Count_Once_In_The_Overflow_Bucket()
    {
        using var provider = Mx.BuildStandard();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Mx.ThrowsSync<ArgumentNullException>(() => mediator.Send((IRequest<int>)null!));
        Mx.ThrowsSync<ArgumentNullException>(() => mediator.Send((MxCommand)null!));
        Mx.ThrowsSync<ArgumentNullException>(() => mediator.Send((object)null!));
        Mx.ThrowsSync<InvalidOperationException>(() => mediator.Send(new object()));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => mediator.Send(new MxUnregistered()));

        Assert.Equal(5, capture.Sum(Mx.RequestCount, Outcome("failure")));
        Assert.Equal(5, capture.Sum(Mx.RequestCount, Type(MediatorMetrics.OverflowLabel), Outcome("failure")));
        Assert.Equal(0, capture.Sum(Mx.RequestCount, Outcome("success")));
        Assert.Equal(5, capture.Of(Mx.RequestDuration).Count);
    }

    [Fact]
    public async Task Strict_Unknown_Request_Is_One_Overflow_Failure()
    {
        using var provider = Mx.Build(configure: c => c.RequireGeneratedDispatch = true, options: Mx.On());
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IMediator>().Send(new MxQuery(1)));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(MediatorMetrics.OverflowLabel), Outcome("failure")));
        Assert.Single(capture.Of(Mx.RequestCount));
    }

    [Fact]
    public async Task Nested_Send_Is_A_Second_Operation()
    {
        using var provider = Mx.BuildStandard();
        var mediator = provider.GetRequiredService<IMediator>();
        provider.GetRequiredService<MxScript>().Query = async (q, ct) => q.Value > 0 ? await mediator.Send(new MxQuery(q.Value - 1), ct) : 5;
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        Assert.Equal(5, await mediator.Send(new MxQuery(1)));
        Assert.Equal(2, capture.Sum(Mx.RequestCount, Type(QueryLabel), Outcome("success")));
        Assert.Equal(2, capture.Of(Mx.RequestDuration).Count);
    }

    [Fact]
    public async Task Retry_Counts_Actual_Extra_Invocations_Without_Recounting_Send()
    {
        using var provider = RetryProvider();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();
        var label = Mx.Label(typeof(MxRetryQuery));

        var succeeds = new MxRetryQuery { Failures = 2, Max = 3 };
        Assert.Equal(5, await mediator.Send(succeeds));
        Assert.Equal(3, succeeds.Calls);
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(label), Outcome("success")));
        Assert.Single(capture.Of(Mx.RequestDuration));
        Assert.Equal(2, capture.Sum(Mx.RetryAttempt, Type(label)));

        var exhausted = new MxRetryQuery { Failures = 99, Max = 2 };
        await Assert.ThrowsAsync<IOException>(() => mediator.Send(exhausted));
        Assert.Equal(3, exhausted.Calls);
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(label), Outcome("failure")));
        Assert.Equal(4, capture.Sum(Mx.RetryAttempt, Type(label)));
        Assert.Equal(2, capture.Of(Mx.RequestCount).Count);
        Assert.All(capture.Of(Mx.RetryAttempt), m => Assert.Equal("{attempt}", m.Unit));
    }

    [Fact]
    public async Task Retry_Delay_Cancellation_Does_Not_Count_An_Attempt_That_Never_Ran()
    {
        using var provider = RetryProvider();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();
        var label = Mx.Label(typeof(MxRetryQuery));
        var request = new MxRetryQuery { Failures = 1, Max = 3, Delay = TimeSpan.FromMinutes(5) };
        using var cts = new CancellationTokenSource();
        var task = mediator.Send(request, cts.Token);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(1, request.Calls);
        Assert.Equal(0, capture.Sum(Mx.RetryAttempt));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(label), Outcome("cancellation")));
    }

    [Fact]
    public async Task Publish_Counts_One_Terminal_Outcome_For_Zero_One_And_Many_Handlers()
    {
        foreach (var handlers in new[] { 0, 1, 2 })
        {
            using var provider = Mx.Build(
                s =>
                {
                    if (handlers >= 1) s.AddTransient<INotificationHandler<MxNote>, MxNoteHandler>();
                    if (handlers >= 2) s.AddTransient<INotificationHandler<MxNote>, MxNoteSecondHandler>();
                },
                options: Mx.On());
            var calls = 0;
            provider.GetRequiredService<MxScript>().Note = (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            };
            using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
            await provider.GetRequiredService<IMediator>().Publish(new MxNote());
            await provider.GetRequiredService<IPublisher>().Publish((object)new MxNote());

            Assert.Equal(handlers * 2, calls);
            Assert.Equal(2, capture.Sum(Mx.NotificationCount, Outcome("success")));
            Assert.Equal(2, capture.Of(Mx.NotificationCount).Count);
            Assert.All(capture.Of(Mx.NotificationCount), m => Assert.Equal("{operation}", m.Unit));
            Assert.Empty(capture.Of(Mx.RequestCount));
        }
    }

    [Fact]
    public async Task Publish_Failure_And_Cancellation_Are_Counted_Once_Each()
    {
        var original = new IOException("handler");
        using var provider = Mx.Build(
            s =>
            {
                s.AddTransient<INotificationHandler<MxNote>, MxNoteHandler>();
                s.AddTransient<INotificationHandler<MxNote>, MxNoteSecondHandler>();
            },
            options: Mx.On());
        var script = provider.GetRequiredService<MxScript>();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        script.Note = (_, _) => Task.FromException(original);
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => mediator.Publish(new MxNote())));
        script.Note = async (_, ct) => await Task.Delay(Timeout.Infinite, ct);
        using var cts = new CancellationTokenSource();
        var task = mediator.Publish(new MxNote(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Mx.ThrowsSync<ArgumentNullException>(() => mediator.Publish((object)null!));
        Mx.ThrowsSync<ArgumentException>(() => mediator.Publish(new object()));

        Assert.Equal(3, capture.Sum(Mx.NotificationCount, Outcome("failure")));
        Assert.Equal(1, capture.Sum(Mx.NotificationCount, Outcome("cancellation")));
        Assert.Equal(0, capture.Sum(Mx.NotificationCount, Outcome("success")));
    }

    [Fact]
    public async Task Concurrent_Publisher_Aggregate_Failure_Is_One_Operation()
    {
        using var provider = Mx.Build(
            s =>
            {
                s.AddTransient<INotificationHandler<MxNote>, MxNoteHandler>();
                s.AddTransient<INotificationHandler<MxNote>, MxNoteSecondHandler>();
            },
            c => c.NotificationPublisher = new TaskWhenAllPublisher(),
            Mx.On());
        provider.GetRequiredService<MxScript>().Note = (_, _) => Task.FromException(new IOException("x"));
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var error = await Record.ExceptionAsync(() => provider.GetRequiredService<IMediator>().Publish(new MxNote()));
        Assert.NotNull(error);
        Assert.Equal(1, capture.Sum(Mx.NotificationCount, Outcome("failure")));
        Assert.Single(capture.Of(Mx.NotificationCount));
    }

    [Fact]
    public async Task Throwing_Listener_Does_Not_Change_Business_Outcomes()
    {
        var original = new InvalidOperationException("business");
        using var provider = Mx.BuildStandard();
        var script = provider.GetRequiredService<MxScript>();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter, throwFromCallbacks: true);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Equal(2, await mediator.Send(new MxQuery(1)));
        await mediator.Publish(new MxNote());
        var total = 0;
        await foreach (var item in mediator.CreateStream(new MxStream()))
        {
            total += item;
        }

        Assert.Equal(3, total);
        script.Query = (_, _) => Task.FromException<int>(original);
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new MxQuery(1))));
        Assert.NotEmpty(capture.All);
    }

    [Fact]
    public async Task Listener_Attached_Mid_Operation_Has_No_Retroactive_Partial_Measurement()
    {
        using var provider = Mx.BuildStandard();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetRequiredService<MxScript>().Query = (_, _) => gate.Task;
        var mediator = provider.GetRequiredService<IMediator>();
        var meter = Mx.MetricsOf(provider).Meter;

        var inFlight = mediator.Send(new MxQuery(1));
        using var capture = new MetricCapture(meter);
        gate.SetResult(3);
        await inFlight;
        Assert.Empty(capture.All);

        provider.GetRequiredService<MxScript>().Query = (q, _) => Task.FromResult(q.Value);
        await mediator.Send(new MxQuery(1));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Outcome("success")));
        Assert.Single(capture.Of(Mx.RequestDuration));
    }

    [Fact]
    public async Task Selective_Subscription_Receives_Only_Subscribed_Instruments()
    {
        using var provider = Mx.BuildStandard();
        var meter = Mx.MetricsOf(provider).Meter;
        using var counterOnly = new MetricCapture(meter, include: i => i.Name == Mx.RequestCount);
        using var durationOnly = new MetricCapture(meter, include: i => i.Name == Mx.RequestDuration);
        using var cacheOnly = new MetricCapture(meter, include: i => i.Name == Mx.CacheLookup);
        await provider.GetRequiredService<IMediator>().Send(new MxQuery(1));

        Assert.Equal(1, counterOnly.All.Count);
        Assert.Equal(Mx.RequestCount, counterOnly.All[0].Instrument);
        Assert.Equal(1, durationOnly.All.Count);
        Assert.Equal(Mx.RequestDuration, durationOnly.All[0].Instrument);
        Assert.Empty(cacheOnly.All);
    }

    [Fact]
    public async Task Providers_Have_Independent_Options_Admission_And_Disposal()
    {
        var provider1 = Mx.BuildStandard(Mx.On(maximum: 5));
        using var provider2 = Mx.BuildStandard(Mx.On(maximum: 0));
        using var disabled = Mx.BuildStandard(new MediatorMetricsOptions());
        using var capture1 = new MetricCapture(Mx.MetricsOf(provider1).Meter);
        using var capture2 = new MetricCapture(Mx.MetricsOf(provider2).Meter);
        using var everything = new MetricCapture(null, MediatorMetrics.MeterName);

        await provider1.GetRequiredService<IMediator>().Send(new MxQuery(1));
        await provider2.GetRequiredService<IMediator>().Send(new MxQuery(1));
        await disabled.GetRequiredService<IMediator>().Send(new MxQuery(1));

        Assert.Equal(1, capture1.Sum(Mx.RequestCount, Type(QueryLabel)));
        Assert.Equal(0, capture1.Sum(Mx.RequestCount, Type(MediatorMetrics.OverflowLabel)));
        Assert.Equal(1, capture2.Sum(Mx.RequestCount, Type(MediatorMetrics.OverflowLabel)));
        Assert.Equal(0, capture2.Sum(Mx.RequestCount, Type(QueryLabel)));
        Assert.False(Mx.MetricsOf(disabled).IsEnabled);
        Assert.Equal(2, everything.Of(Mx.RequestCount).Count);

        // Scopes share the root provider's single component.
        using (var scope = provider1.CreateScope())
        {
            Assert.Same(Mx.MetricsOf(provider1), scope.ServiceProvider.GetRequiredService<MediatorMetrics>());
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new MxQuery(1));
        }

        Assert.Equal(2, capture1.Sum(Mx.RequestCount, Type(QueryLabel)));
        provider1.Dispose();
        await provider2.GetRequiredService<IMediator>().Send(new MxQuery(1));
        Assert.Equal(2, capture1.Sum(Mx.RequestCount, Type(QueryLabel)));
        Assert.Equal(2, capture2.Sum(Mx.RequestCount, Type(MediatorMetrics.OverflowLabel)));
    }

    [Fact]
    public async Task Manual_Composition_Is_Opt_In_And_Legacy_Constructors_Stay_Disabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MxScript>();
        services.AddTransient<IRequestHandler<MxQuery, int>, MxQueryHandler>();
        using var provider = services.BuildServiceProvider();
        using var metrics = new MediatorMetrics(Mx.On(), new[] { typeof(MxQuery) });
        using var capture = new MetricCapture(metrics.Meter);
        using var everything = new MetricCapture(null, MediatorMetrics.MeterName);

        var legacy = new Mediator(provider);
        var withTelemetry = new Mediator(provider, new ForeachAwaitPublisher(), MediatorTelemetryOptions.Default);
        var withComposition = new Mediator(provider, new ForeachAwaitPublisher(), MediatorTelemetryOptions.Default, GeneratedMediatorComposition.Empty);
        Assert.Equal(2, await legacy.Send(new MxQuery(1)));
        Assert.Equal(2, await withTelemetry.Send(new MxQuery(1)));
        Assert.Equal(2, await withComposition.Send(new MxQuery(1)));
        Assert.Empty(capture.All);

        var instrumented = new Mediator(provider, new ForeachAwaitPublisher(), MediatorTelemetryOptions.Default, GeneratedMediatorComposition.Empty, metrics);
        Assert.Equal(2, await instrumented.Send(new MxQuery(1)));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(QueryLabel), Outcome("success")));
        Assert.Equal(1, everything.Of(Mx.RequestCount).Count);
        Assert.Throws<ArgumentNullException>(() => new MediatorMetrics(null!, Array.Empty<Type>()));
        Assert.Throws<ArgumentNullException>(() => new MediatorMetrics(Mx.On(), null!));
    }

    [Fact]
    public async Task Generated_Registry_Manifest_Types_Are_Admitted_And_Config_Metrics_Is_Honored()
    {
        var services = new ServiceCollection().AddSingleton<DispatchCapture>();
        services.AddMeridianMediator(c =>
        {
            c.AddGeneratedRegistry<DispatchRegistry>();
            c.RequireGeneratedDispatch = true;
            c.Metrics = Mx.On();
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        Assert.Equal(42, await provider.GetRequiredService<IMediator>().Send(new GeneratedQuery()));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(Mx.Label(typeof(GeneratedQuery))), Outcome("success")));
        Assert.True(Mx.MetricsOf(provider).AdmittedTypeCount >= 3);
    }

    [Fact]
    public async Task Meter_Factory_Is_Used_When_Registered()
    {
        var factory = new CountingMeterFactory();
        using var provider = Mx.Build(
            s =>
            {
                s.AddSingleton<IMeterFactory>(factory);
                s.AddTransient<IRequestHandler<MxQuery, int>, MxQueryHandler>();
            },
            options: Mx.On());
        var metrics = Mx.MetricsOf(provider);
        Assert.Same(Assert.Single(factory.Created), metrics.Meter);
        Assert.Equal(MediatorMetrics.MeterName, metrics.Meter!.Name);
        using var capture = new MetricCapture(metrics.Meter);
        await provider.GetRequiredService<IMediator>().Send(new MxQuery(1));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Outcome("success")));
        provider.Dispose();
        Assert.Equal(0, factory.DisposedMeters);
    }

    [Fact]
    public async Task Generated_Standard_Localized_Validation_Receives_Metrics_Through_Its_Constructor()
    {
        var trace = new PipelineTrace { Reject = true };
        var services = new ServiceCollection().AddSingleton(trace).AddSingleton<IAuditSink, TraceAuditSink>().AddSingleton<IStringLocalizer<PipelineQuery>, TraceLocalizer>();
        services.AddMeridianStandard<StandardRegistry>(c =>
        {
            c.RequireGeneratedDispatch = true;
            c.Metrics = Mx.On();
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        await Assert.ThrowsAsync<ValidationException>(() => provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        var label = Mx.Label(typeof(PipelineQuery));
        Assert.Equal(1, capture.Sum(Mx.ValidationRejection, Type(label)));
        Assert.Equal(1, capture.Sum(Mx.RequestCount, Type(label), Outcome("failure")));
    }

    [Fact]
    public async Task Generated_Selected_Built_In_Behavior_Uses_Its_Marked_Constructor_With_Metrics()
    {
        var trace = new PipelineTrace { Reject = true };
        var services = new ServiceCollection().AddSingleton(trace);
        services.AddMeridianMediator(c =>
        {
            c.AddGeneratedRegistry<MetricsBuiltInRegistry>();
            c.RequireGeneratedDispatch = true;
            c.AddOpenBehavior(typeof(ValidationBehavior<,>));
            c.Metrics = Mx.On();
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        await Assert.ThrowsAsync<ValidationException>(() => provider.GetRequiredService<IMediator>().Send(new PipelineQuery()));
        Assert.Equal(1, capture.Sum(Mx.ValidationRejection, Type(Mx.Label(typeof(PipelineQuery)))));
        Assert.Equal(new[] { "validate" }, trace.Events);
    }

    private static ServiceProvider RetryProvider() => Mx.Build(
        s => s.AddTransient<IRequestHandler<MxRetryQuery, int>, MxRetryHandler>(),
        c => c.AddOpenBehavior(typeof(RetryBehavior<,>)),
        Mx.On());

    private sealed class CountingMeterFactory : IMeterFactory
    {
        internal List<Meter> Created { get; } = new();

        internal int DisposedMeters { get; private set; }

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            Created.Add(meter);
            return meter;
        }

        public void Dispose() => DisposedMeters++;
    }
}

public sealed class MxRetryQuery : IRequest<int>, IRetryableRequest
{
    public int Failures { get; set; }

    public int Calls { get; set; }

    public int Max { get; set; } = 3;

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    int IRetryableRequest.MaxRetries => Max;

    TimeSpan IRetryableRequest.RetryDelay => Delay;
}

public sealed class MxRetryHandler : IRequestHandler<MxRetryQuery, int>
{
    public Task<int> Handle(MxRetryQuery request, CancellationToken cancellationToken)
    {
        request.Calls++;
        if (request.Calls <= request.Failures)
        {
            throw new IOException("transient");
        }

        return Task.FromResult(5);
    }
}

[GenerateMediatorRegistry(typeof(PipelineHandler), ValidatorTypes = new[] { typeof(TraceValidator) }, BehaviorTypes = new[] { typeof(ValidationBehavior<,>) })]
public partial class MetricsBuiltInRegistry;

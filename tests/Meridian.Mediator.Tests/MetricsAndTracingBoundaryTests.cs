using System.Diagnostics;
using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

// Boundary tests for the telemetry hot paths (MediatorMetrics, Mediator activity twins) that were measured below the
// 90%/80% coverage targets. They attach process-wide listeners, so they share the serialized "Metrics" collection.
[Collection("Metrics")]
public class MetricsBoundaryTests
{
    private static MediatorMetrics Enabled(params Type[] known) => new(Mx.On(), known);

    [Fact]
    public void Instruments_Without_A_Listener_Create_No_Operations_And_Observe_Nothing()
    {
        using var metrics = Enabled(typeof(MxQuery));
        Assert.False(metrics.RequestEnabled);
        Assert.False(metrics.NotificationEnabled);
        Assert.False(metrics.StreamEnabled);
        Assert.False(metrics.AuthorizationEnabled);
        Assert.False(metrics.IdempotencyEnabled);

        Assert.Null(metrics.BeginRequest(typeof(MxQuery)).Label);
        Assert.Null(metrics.BeginNotification(typeof(MxNote)).Label);
        Assert.Null(metrics.BeginStream(typeof(MxStream)).Label);

        metrics.EndRequest(default, MetricOutcome.Success);
        metrics.EndNotification(default, MetricOutcome.Success);
        metrics.EndStream(default, MetricOutcome.Success, StreamPhase.Dispatch);

        var plain = Task.FromResult(1);
        Assert.Same(plain, metrics.ObserveRequest(plain, default, CancellationToken.None));
        Task untyped = Task.CompletedTask;
        Assert.Same(untyped, metrics.ObserveRequest(untyped, default, CancellationToken.None));
        Assert.Same(untyped, metrics.ObserveNotification(untyped, default, CancellationToken.None));
    }

    [Fact]
    public void Behavior_Events_Are_Dropped_Without_A_Listener_And_On_The_Inert_Component()
    {
        using var metrics = Enabled(typeof(MxQuery));
        foreach (var target in new[] { metrics, MediatorMetrics.Disabled })
        {
            target.RecordCacheLookup(typeof(MxQuery), CacheLookupState.Hit);
            target.RecordRetryAttempt(typeof(MxQuery));
            target.RecordValidationRejection(typeof(MxQuery));
            target.RecordAuthorizationRejection(typeof(MxQuery), AuthorizationRejection.Denied);
            target.RecordIdempotency(typeof(MxQuery), IdempotencyMode.Leased, IdempotencyState.Claimed);
        }
    }

    [Fact]
    public void A_Throwing_Listener_Never_Escapes_Any_Metrics_Recording_Call()
    {
        using var metrics = Enabled(typeof(MxQuery));
        using var capture = new MetricCapture(metrics.Meter, throwFromCallbacks: true);
        var request = metrics.BeginRequest(typeof(MxQuery));
        var notification = metrics.BeginNotification(typeof(MxNote));
        var stream = metrics.BeginStream(typeof(MxStream));
        Assert.NotNull(request.Label);

        metrics.EndRequest(request, MetricOutcome.Failure);
        metrics.EndNotification(notification, MetricOutcome.Success);
        metrics.EndStream(stream, MetricOutcome.Cancellation, StreamPhase.Enumeration);
        metrics.RecordCacheLookup(typeof(MxQuery), CacheLookupState.Miss);
        metrics.RecordRetryAttempt(typeof(MxQuery));
        metrics.RecordValidationRejection(typeof(MxQuery));
        metrics.RecordAuthorizationRejection(typeof(MxQuery), AuthorizationRejection.MissingHandler);
        metrics.RecordIdempotency(typeof(MxQuery), IdempotencyMode.Atomic, IdempotencyState.Replayed);

        // Every call reached its instrument (the listener recorded it before throwing); none surfaced the fault.
        Assert.True(capture.CountOf(Mx.RequestCount) == 1 && capture.CountOf(Mx.NotificationCount) == 1 && capture.CountOf(Mx.StreamCount) == 1);
        Assert.Equal(1, capture.CountOf(Mx.CacheLookup));
        Assert.Equal(1, capture.CountOf(Mx.RetryAttempt));
        Assert.Equal(1, capture.CountOf(Mx.ValidationRejection));
        Assert.Equal(1, capture.CountOf(Mx.AuthorizationRejection));
        Assert.Equal(1, capture.CountOf(Mx.IdempotencyState));
    }

    [Fact]
    public async Task Pending_Tasks_Are_Observed_Once_With_Their_Terminal_Outcome()
    {
        using var metrics = Enabled(typeof(MxQuery), typeof(MxNote));
        using var capture = new MetricCapture(metrics.Meter);
        var start = metrics.BeginRequest(typeof(MxQuery));

        var typed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedTyped = metrics.ObserveRequest(typed.Task, start, CancellationToken.None);
        Assert.NotSame(typed.Task, observedTyped);
        typed.SetResult(5);
        Assert.Equal(5, await observedTyped);

        var failing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedFailing = metrics.ObserveRequest(failing.Task, start, CancellationToken.None);
        failing.SetException(new InvalidOperationException("typed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => observedFailing);

        var untyped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedUntyped = metrics.ObserveRequest((Task)untyped.Task, start, CancellationToken.None);
        untyped.SetResult();
        await observedUntyped;

        var untypedFailing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedUntypedFailing = metrics.ObserveRequest((Task)untypedFailing.Task, start, CancellationToken.None);
        untypedFailing.SetException(new TimeoutException());
        await Assert.ThrowsAsync<TimeoutException>(() => observedUntypedFailing);

        using var cancelled = new CancellationTokenSource();
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancelling = metrics.ObserveRequest((Task)cancelling.Task, start, cancelled.Token);
        cancelled.Cancel();
        cancelling.SetCanceled(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observedCancelling);

        var note = metrics.BeginNotification(typeof(MxNote));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedNote = metrics.ObserveNotification(gate.Task, note, CancellationToken.None);
        gate.SetResult();
        await observedNote;
        var noteFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedNoteFailure = metrics.ObserveNotification(noteFailure.Task, note, CancellationToken.None);
        noteFailure.SetException(new InvalidOperationException("note"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => observedNoteFailure);

        Assert.Equal(2, capture.CountOf(Mx.RequestCount, ("outcome", "success")));
        Assert.Equal(2, capture.CountOf(Mx.RequestCount, ("outcome", "failure")));
        Assert.Equal(1, capture.CountOf(Mx.RequestCount, ("outcome", "cancellation")));
        Assert.Equal(1, capture.CountOf(Mx.NotificationCount, ("outcome", "success")));
        Assert.Equal(1, capture.CountOf(Mx.NotificationCount, ("outcome", "failure")));
    }

    [Fact]
    public void Already_Completed_Tasks_Are_Recorded_Synchronously_And_Returned_Unchanged()
    {
        using var metrics = Enabled(typeof(MxQuery));
        using var capture = new MetricCapture(metrics.Meter);
        var operation = metrics.BeginRequest(typeof(MxQuery));

        var faulted = Task.FromException<int>(new InvalidOperationException("done"));
        Assert.Same(faulted, metrics.ObserveRequest(faulted, operation, CancellationToken.None));
        var empty = Task.FromException(new AggregateException());
        Assert.Same(empty, metrics.ObserveRequest(empty, operation, CancellationToken.None));
        var cancelledTask = Task.FromCanceled(new CancellationToken(true));
        Assert.Same(cancelledTask, metrics.ObserveRequest(cancelledTask, operation, new CancellationToken(true)));
        Assert.Same(cancelledTask, metrics.ObserveRequest(cancelledTask, operation, CancellationToken.None));

        Assert.Equal(3, capture.CountOf(Mx.RequestCount, ("outcome", "failure")));
        Assert.Equal(1, capture.CountOf(Mx.RequestCount, ("outcome", "cancellation")));
    }

    public sealed class Outer<T>
    {
        public sealed class Inner<TNext>
        {
        }
    }

    [Fact]
    public void Canonical_Labels_Cover_Nested_Generic_Array_And_Unrenderable_Types()
    {
        Assert.Equal("System.Int32[,]", MediatorMetrics.DescribeType(typeof(int[,])));
        Assert.Equal("System.String[]", MediatorMetrics.DescribeType(typeof(string[])));
        Assert.Equal(
            "Meridian.Mediator.Tests.MetricsBoundaryTests+Outer<System.Int32>+Inner<System.String>",
            MediatorMetrics.DescribeType(typeof(Outer<int>.Inner<string>)));
        Assert.Equal("Meridian.Mediator.Tests.MxGeneric<System.Int32[]>", MediatorMetrics.DescribeType(typeof(MxGeneric<int[]>)));

        Assert.Null(MediatorMetrics.DescribeType(typeof(List<>)));
        Assert.Null(MediatorMetrics.DescribeType(typeof(List<>).GetGenericArguments()[0]));
        Assert.Null(MediatorMetrics.DescribeType(typeof(int).MakeByRefType()));
        Assert.Null(MediatorMetrics.DescribeType(typeof(int).MakePointerType()));
        Assert.Null(MediatorMetrics.DescribeType(typeof(Outer<>.Inner<>)));
    }

    [Fact]
    public void Admission_Skips_Unrenderable_Overlong_And_Duplicate_Types_And_Honors_The_Cap()
    {
        var overlong = typeof(int);
        for (var i = 0; i < 8; i++)
        {
            overlong = typeof(Dictionary<,>).MakeGenericType(typeof(string), overlong);
        }

        Assert.True(MediatorMetrics.DescribeType(overlong)!.Length > MediatorMetrics.MaximumLabelLength);

        using var metrics = new MediatorMetrics(
            Mx.On(2), new[] { typeof(MxQuery), typeof(MxQuery), overlong, typeof(List<>), typeof(MxNote), typeof(MxStream), null! });
        Assert.Equal(2, metrics.AdmittedTypeCount);
        Assert.Equal(Mx.Label(typeof(MxNote)), metrics.LabelFor(typeof(MxNote)));
        Assert.Equal(Mx.Label(typeof(MxQuery)), metrics.LabelFor(typeof(MxQuery)));
        Assert.NotEqual(Mx.Label(typeof(MxStream)), metrics.LabelFor(typeof(MxStream)));
        Assert.Equal(metrics.LabelFor(overlong), metrics.LabelFor(typeof(List<>)));

        using var none = new MediatorMetrics(Mx.On(0), new[] { typeof(MxQuery) });
        Assert.Equal(0, none.AdmittedTypeCount);
    }
}

[Collection("Metrics")]
public class MediatorTelemetryBoundaryTests
{
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Stopped { get; } = new();

        public ActivityCapture(ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Meridian.Mediator",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
                ActivityStopped = activity =>
                {
                    lock (Stopped)
                    {
                        Stopped.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity[] Named(string prefix)
        {
            lock (Stopped)
            {
                return Stopped.Where(a => a.OperationName.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Metered_Dispatch_Under_An_Activity_Listener_Reports_Both_Spans_And_Measurements()
    {
        using var provider = Mx.BuildStandard();
        using var metrics = new MetricCapture(Mx.MetricsOf(provider).Meter);
        using var activities = new ActivityCapture();
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Equal(4, await mediator.Send(new MxQuery(3)));
        Assert.Equal(6, await mediator.Send((object)new MxQuery(5)) is int boxed ? boxed : -1);
        await mediator.Send(new MxCommand());
        await mediator.Publish(new MxNote());
        await mediator.Publish((object)new MxNote());

        Assert.Equal(3, metrics.CountOf(Mx.RequestCount, ("outcome", "success")));
        Assert.Equal(2, metrics.CountOf(Mx.NotificationCount, ("outcome", "success")));
        Assert.Equal(3, activities.Named("Mediator.Send").Length);
        Assert.Equal(2, activities.Named("Mediator.Publish").Length);
    }

    [Theory]
    [InlineData(ActivitySamplingResult.None, 0)]
    [InlineData(ActivitySamplingResult.AllDataAndRecorded, 2)]
    public async Task Unmetered_Send_Dispatches_Typed_And_Object_Requests_Regardless_Of_Sampling(
        ActivitySamplingResult sampling, int expectedSpans)
    {
        using var provider = Mx.BuildStandard();
        using var activities = new ActivityCapture(sampling);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Equal(4, await mediator.Send(new MxQuery(3)));
        Assert.Equal(6, Assert.IsType<int>(await mediator.Send((object)new MxQuery(5))));

        var spans = activities.Named("Mediator.Send");
        Assert.Equal(expectedSpans, spans.Length);
        Assert.All(spans, span =>
        {
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
            Assert.Equal(typeof(MxQuery).FullName, span.GetTagItem("meridian.request_type"));
        });
    }

    [Fact]
    public async Task Unmetered_Publish_Under_An_Activity_Listener_Emits_A_Span_For_Both_Overloads()
    {
        using var provider = Mx.BuildStandard();
        using var activities = new ActivityCapture();
        var mediator = provider.GetRequiredService<IMediator>();

        // No metric listener is attached, so the unmetered branch (with a span) runs.
        await mediator.Publish(new MxNote());
        await mediator.Publish((object)new MxNote());

        Assert.Equal(2, activities.Named("Mediator.Publish").Length);
    }

    [Fact]
    public async Task Metered_Publish_Rejects_Null_Notifications_Synchronously_And_Counts_The_Failure()
    {
        using var provider = Mx.BuildStandard();
        using var metrics = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Throws<ArgumentNullException>(() => { _ = mediator.Publish<MxNote>(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = mediator.Publish((object)null!); });
        Assert.Throws<ArgumentException>(() => { _ = mediator.Publish(new object()); });
        await mediator.Publish(new MxNote());

        Assert.Equal(3, metrics.CountOf(Mx.NotificationCount, ("outcome", "failure")));
        Assert.Equal(1, metrics.CountOf(Mx.NotificationCount, ("outcome", "success")));
    }

    [Fact]
    public async Task Failed_And_Cancelled_Void_Operations_Mark_Their_Span_Without_Changing_The_Outcome()
    {
        using var provider = Mx.BuildStandard();
        var script = provider.GetRequiredService<MxScript>();
        using var activities = new ActivityCapture();
        var mediator = provider.GetRequiredService<IMediator>();

        script.Command = (_, _) => Task.FromException(new InvalidOperationException("void failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new MxCommand()));
        script.Note = (_, _) => Task.FromException(new InvalidOperationException("note failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Publish(new MxNote()));

        using var cancelled = new CancellationTokenSource();
        script.Command = (_, token) => Task.FromCanceled(token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mediator.Send(new MxCommand(), cancelled.Token));

        var send = activities.Named("Mediator.Send");
        var publish = activities.Named("Mediator.Publish");
        Assert.Equal(ActivityStatusCode.Error, send[0].Status);
        Assert.Equal("System.InvalidOperationException", send[0].GetTagItem("exception.type"));
        Assert.Equal(ActivityStatusCode.Error, publish[0].Status);
        // A caller-requested cancellation is not recorded as a failure of the operation.
        Assert.NotEqual(ActivityStatusCode.Error, send[1].Status);
    }

    [Fact]
    public void Constructors_Reject_Null_Collaborators_And_Default_The_Publisher_And_Telemetry()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var publisher = new ForeachAwaitPublisher();
        var options = MediatorTelemetryOptions.Default;

        Assert.Throws<ArgumentNullException>(() => new Mediator(null!, publisher, options, Generated.GeneratedMediatorComposition.Empty, null));
        Assert.Throws<ArgumentNullException>(() => new Mediator(services, null!, options, Generated.GeneratedMediatorComposition.Empty, null));
        Assert.Throws<ArgumentNullException>(() => new Mediator(services, publisher, null!, Generated.GeneratedMediatorComposition.Empty, null));
        Assert.Throws<ArgumentNullException>(() => new Mediator(services, publisher, options, null!, null));

        Assert.NotNull(new Mediator(services, publisher));
        Assert.NotNull(new Mediator(services));
    }
}

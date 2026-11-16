#pragma warning disable CA1707 // Contract test names are deliberately descriptive.
using System.Diagnostics;
using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

[Collection("Metrics")]
public class MetricsStreamTests
{
    private static readonly string StreamLabel = Mx.Label(typeof(MxStream));

    private static (string, string) Outcome(string outcome) => ("outcome", outcome);

    private static (string, string) Phase(string phase) => ("phase", phase);

    private static Task<int> Drain(IAsyncEnumerable<int> stream) => Drain(stream, default);

    private static async Task<int> Drain(IAsyncEnumerable<int> stream, CancellationToken token)
    {
        var total = 0;
        await foreach (var item in stream.WithCancellation(token))
        {
            total += item;
        }

        return total;
    }

    // Each scenario runs with metrics only (eager acquisition, ambient activity untouched) and with an Activity
    // listener attached (deferred acquisition under the stream activity).
    public static TheoryData<bool> Modes => new() { false, true };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Stream_Never_Enumerated_Emits_Nothing(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var stream = provider.GetRequiredService<IMediator>().CreateStream(new MxStream());
        Assert.NotNull(stream);
        _ = provider.GetRequiredService<IMediator>().CreateStream((object)new MxStream());
        Assert.Empty(capture.All);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Disposed_Before_First_Move_Emits_Disposed_Without_Running_The_Handler(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var bodyRan = false;
        provider.GetRequiredService<MxScript>().Stream = (_, ct) => Body(() => bodyRan = true, ct);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var enumerator = provider.GetRequiredService<IMediator>().CreateStream(new MxStream()).GetAsyncEnumerator();
        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();

        Assert.False(bodyRan);
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("disposed"), Phase("enumeration")));
        Assert.Single(capture.Of(Mx.StreamCount));
        Assert.Single(capture.Of(Mx.StreamDuration));
        Assert.Empty(activities?.Stopped ?? new List<Activity>());

        static async IAsyncEnumerable<int> Body(Action ran, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ran();
            await Task.Yield();
            yield return 1;
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Full_Enumeration_Is_One_Success_With_Duration_Including_Cleanup(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var inner = new ScriptedEnumerator(new[] { 1, 2 });
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Dispose = () => new ValueTask(disposeGate.Task);
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => inner);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var enumerator = provider.GetRequiredService<IMediator>().CreateStream(new MxStream()).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        var last = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);
        Assert.False(last.IsCompleted);
        Assert.Empty(capture.All);
        Assert.Empty(activities?.Stopped ?? new List<Activity>());

        disposeGate.SetResult();
        Assert.False(await last);
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("success"), Phase("enumeration")));
        var duration = Assert.Single(capture.Of(Mx.StreamDuration));
        Assert.InRange(duration.Value, 80, 10_000);
        Assert.Equal("ms", duration.Unit);
        Assert.Equal(1, inner.DisposeCalls);
        await enumerator.DisposeAsync();
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Single(capture.Of(Mx.StreamCount));
        if (activities is not null)
        {
            var stopped = Assert.Single(activities.Stopped);
            Assert.Equal(ActivityStatusCode.Ok, stopped.Status);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Typed_And_Object_Streams_Complete_And_Repeat_Independently(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();
        var typed = mediator.CreateStream(new MxStream());

        Assert.Equal(3, await Drain(typed));
        Assert.Equal(3, await Drain(typed));
        var objects = 0;
        await foreach (var item in mediator.CreateStream((object)new MxStream()))
        {
            objects += (int)item!;
        }

        Assert.Equal(3, objects);
        Assert.Equal(3, capture.Sum(Mx.StreamCount, Outcome("success"), Phase("enumeration")));
        Assert.Equal(3, capture.Of(Mx.StreamDuration).Count);
        Assert.Equal(3, capture.Sum(Mx.StreamCount, ("request.type", StreamLabel)));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Early_Break_Is_Disposed_Not_Success(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        await foreach (var _ in provider.GetRequiredService<IMediator>().CreateStream(new MxStream()))
        {
            break;
        }

        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("disposed"), Phase("enumeration")));
        Assert.Equal(0, capture.Sum(Mx.StreamCount, Outcome("success")));
        Assert.Single(capture.Of(Mx.StreamCount));
        if (activities is not null)
        {
            Assert.Equal(ActivityStatusCode.Unset, Assert.Single(activities.Stopped).Status);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Acquisition_Failure_Is_One_Failure_With_The_Original_Exception(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var original = new InvalidOperationException("acquire");
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => throw original);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(provider.GetRequiredService<IMediator>().CreateStream(new MxStream())));
        Assert.Same(original, error);
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("failure"), Phase("enumeration")));
        Assert.Single(capture.Of(Mx.StreamCount));
        Assert.Single(capture.Of(Mx.StreamDuration));
        if (activities is not null)
        {
            Assert.Equal(ActivityStatusCode.Error, Assert.Single(activities.Stopped).Status);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoveNext_Failure_Cleans_Up_Once_And_Preserves_Identity(bool withActivity, bool asynchronous)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var original = new IOException("move");
        var inner = new ScriptedEnumerator(new[] { 1, 2 })
        {
            Move = () => asynchronous ? new ValueTask<bool>(Task.FromException<bool>(original)) : throw original,
        };
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => inner);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var error = await Assert.ThrowsAsync<IOException>(() => Drain(provider.GetRequiredService<IMediator>().CreateStream(new MxStream())));
        Assert.Same(original, error);
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("failure"), Phase("enumeration")));
        Assert.Single(capture.Of(Mx.StreamCount));
        Assert.Equal(0, capture.Sum(Mx.StreamCount, Outcome("success")));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Cleanup_Failure_After_Exhaustion_Is_A_Failure_Not_A_Success(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var cleanup = new IOException("cleanup");
        var inner = new ScriptedEnumerator(new[] { 1 }) { Dispose = () => throw cleanup };
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => inner);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var error = await Assert.ThrowsAsync<IOException>(() => Drain(provider.GetRequiredService<IMediator>().CreateStream(new MxStream())));
        Assert.Same(cleanup, error);
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("failure")));
        Assert.Equal(0, capture.Sum(Mx.StreamCount, Outcome("success")));
        Assert.Single(capture.Of(Mx.StreamCount));
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Double_Failure_Retains_Both_Original_Objects_Primary_First(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var primary = new IOException("primary");
        var cleanup = new InvalidOperationException("cleanup");
        var inner = new ScriptedEnumerator(new[] { 1 }) { Move = () => throw primary, Dispose = () => throw cleanup };
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => inner);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var error = await Assert.ThrowsAsync<AggregateException>(() => Drain(provider.GetRequiredService<IMediator>().CreateStream(new MxStream())));
        Assert.Collection(error.InnerExceptions, e => Assert.Same(primary, e), e => Assert.Same(cleanup, e));
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("failure")));
        Assert.Single(capture.Of(Mx.StreamCount));
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Current_Failure_Is_A_Failure_And_Cleanup_Still_Happens_On_Dispose(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var original = new IOException("current");
        var inner = new ScriptedEnumerator(new[] { 1, 2 }) { CurrentFailure = original };
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => inner);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var enumerator = provider.GetRequiredService<IMediator>().CreateStream(new MxStream()).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var thrown = Assert.Throws<IOException>(() => enumerator.Current);
        Assert.Same(original, thrown);
        Assert.Empty(capture.All);
        await enumerator.DisposeAsync();

        Assert.Equal(1, inner.DisposeCalls);
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("failure")));
        Assert.Equal(0, capture.Sum(Mx.StreamCount, Outcome("success")));
        Assert.Single(capture.Of(Mx.StreamCount));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_Uses_Either_Effective_Token_And_Is_Not_A_Failure(bool withActivity, bool creationToken)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        provider.GetRequiredService<MxScript>().Stream = (_, ct) => Block(ct);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        using var cts = new CancellationTokenSource();
        var mediator = provider.GetRequiredService<IMediator>();
        var stream = creationToken ? mediator.CreateStream(new MxStream(), cts.Token) : mediator.CreateStream(new MxStream());
        var task = Drain(stream, creationToken ? default : cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("cancellation"), Phase("enumeration")));
        Assert.Equal(0, capture.Sum(Mx.StreamCount, Outcome("failure")));
        Assert.Single(capture.Of(Mx.StreamCount));

        static async IAsyncEnumerable<int> Block([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield return 1;
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Unrelated_Cancellation_Without_A_Canceled_Token_Is_A_Failure(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var provider = Mx.BuildStandard();
        var inner = new ScriptedEnumerator(new[] { 1 }) { Move = () => throw new OperationCanceledException() };
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => inner);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Drain(provider.GetRequiredService<IMediator>().CreateStream(new MxStream())));
        Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("failure")));
        Assert.Equal(0, capture.Sum(Mx.StreamCount, Outcome("cancellation")));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Failed_Synchronous_Dispatch_Is_Its_Own_Phase_And_Never_An_Enumeration(bool withActivity)
    {
        using var activities = Activities(withActivity);
        using var strict = Mx.Build(configure: c => c.RequireGeneratedDispatch = true, options: Mx.On());
        using var capture = new MetricCapture(Mx.MetricsOf(strict).Meter);
        var mediator = strict.GetRequiredService<IMediator>();

        Assert.Throws<ArgumentNullException>(() => mediator.CreateStream((IStreamRequest<int>)null!));
        Assert.Throws<ArgumentNullException>(() => mediator.CreateStream((object)null!));
        Assert.Throws<InvalidOperationException>(() => mediator.CreateStream(new MxStream()));

        Assert.Equal(3, capture.Sum(Mx.StreamCount, Outcome("failure"), Phase("dispatch")));
        Assert.Equal(0, capture.Sum(Mx.StreamCount, Phase("enumeration")));
        Assert.Equal(3, capture.Of(Mx.StreamDuration).Count);
        Assert.All(capture.Of(Mx.StreamCount), m => Assert.Equal(MediatorMetrics.OverflowLabel, m.Tag("request.type")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Metrics_Only_Streams_Leave_Activity_Current_Untouched()
    {
        using var provider = Mx.BuildStandard();
        var seen = new List<string?>();
        provider.GetRequiredService<MxScript>().Stream = (_, ct) => Observe(seen, ct);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var sentinel = new Activity("sentinel").Start();
        try
        {
            await foreach (var _ in provider.GetRequiredService<IMediator>().CreateStream(new MxStream()))
            {
                Assert.Same(sentinel, Activity.Current);
            }

            Assert.Same(sentinel, Activity.Current);
            Assert.All(seen, name => Assert.Equal("sentinel", name));
            Assert.NotEmpty(seen);
            Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("success")));
        }
        finally
        {
            sentinel.Stop();
        }
    }

    [Fact]
    public async Task Stream_Activity_Is_Ambient_Only_During_Steps_And_Caller_Activity_Is_Restored_Every_Time()
    {
        using var activities = Activities(true);
        using var provider = Mx.BuildStandard();
        var seen = new List<string?>();
        provider.GetRequiredService<MxScript>().Stream = (_, ct) => Observe(seen, ct);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var sentinel = new Activity("sentinel").Start();
        try
        {
            var enumerator = provider.GetRequiredService<IMediator>().CreateStream(new MxStream()).GetAsyncEnumerator();
            Assert.Same(sentinel, Activity.Current);
            while (await enumerator.MoveNextAsync())
            {
                Assert.Same(sentinel, Activity.Current);
                _ = enumerator.Current;
                Assert.Same(sentinel, Activity.Current);
            }

            Assert.Same(sentinel, Activity.Current);
            await enumerator.DisposeAsync();
            Assert.Same(sentinel, Activity.Current);

            Assert.NotEmpty(seen);
            Assert.All(seen, name => Assert.Equal("Mediator.CreateStream MxStream", name));
            Assert.Equal(sentinel.Id, Assert.Single(activities!.Stopped).ParentId);

            // A faulted stream restores the caller activity as well.
            var original = new IOException("boom");
            provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => new ScriptedEnumerator(new[] { 1 }) { Move = () => throw original });
            await Assert.ThrowsAsync<IOException>(() => Drain(provider.GetRequiredService<IMediator>().CreateStream(new MxStream())));
            Assert.Same(sentinel, Activity.Current);
            Assert.Equal(1, capture.Sum(Mx.StreamCount, Outcome("failure")));
        }
        finally
        {
            sentinel.Stop();
        }
    }

    [Fact]
    public async Task Activity_Status_Is_Not_Ok_Until_Cleanup_Succeeds()
    {
        using var activities = Activities(true);
        using var provider = Mx.BuildStandard();
        var cleanup = new IOException("cleanup");
        var inner = new ScriptedEnumerator(new[] { 1 }) { Dispose = () => throw cleanup };
        provider.GetRequiredService<MxScript>().Stream = (_, _) => new ScriptedStream(_ => inner);

        await Assert.ThrowsAsync<IOException>(() => Drain(provider.GetRequiredService<IMediator>().CreateStream(new MxStream())));
        var stopped = Assert.Single(activities!.Stopped);
        Assert.Equal(ActivityStatusCode.Error, stopped.Status);
        Assert.Null(stopped.StatusDescription);
        Assert.Null(stopped.GetTagItem("exception.message"));
        Assert.Equal(typeof(IOException).FullName, stopped.GetTagItem("exception.type"));
    }

    private static async IAsyncEnumerable<int> Observe(List<string?> seen, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        seen.Add(Activity.Current?.OperationName);
        await Task.Yield();
        seen.Add(Activity.Current?.OperationName);
        yield return 1;
        await Task.Yield();
        seen.Add(Activity.Current?.OperationName);
        yield return 2;
        seen.Add(Activity.Current?.OperationName);
    }

    private static StreamActivities? Activities(bool enabled) => enabled ? new StreamActivities() : null;

    private sealed class StreamActivities : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly object _gate = new();
        private readonly List<Activity> _stopped = new();

        internal StreamActivities()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "Meridian.Mediator",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _stopped.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal List<Activity> Stopped
        {
            get
            {
                lock (_gate)
                {
                    return _stopped.ToList();
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class ScriptedStream(Func<CancellationToken, IAsyncEnumerator<int>> acquire) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => acquire(cancellationToken);
    }

    private sealed class ScriptedEnumerator(int[] values) : IAsyncEnumerator<int>
    {
        private int _index = -1;

        internal Func<ValueTask<bool>>? Move { get; set; }

        internal Func<ValueTask>? Dispose { get; set; }

        internal Exception? CurrentFailure { get; set; }

        internal int DisposeCalls { get; private set; }

        public int Current => CurrentFailure is not null ? throw CurrentFailure : values[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            if (Move is not null)
            {
                return Move();
            }

            _index++;
            return new ValueTask<bool>(_index < values.Length);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return Dispose?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}

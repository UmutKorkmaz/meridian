using System.Collections;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Publishing;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Mediator.Tests;

// Named boundary tests for the changed core hot paths whose line/branch coverage was measured below the 90%/80%
// targets by eng/coverage_gate.py against eng/coverage-baseline.json. Each test pins a real contract at an input
// boundary (argument validation, collection shape, hostile exception metadata); none duplicates an existing case.
public class HotPathBoundaryTests
{
    #region AuthorizationBehavior: collection shapes handed in by custom containers

    public sealed record BoundaryAuthorized(string User) : IRequest<string>, IAuthorizedRequest;

    private sealed class RecordingAuthorizationHandler(List<string> calls, string name, bool allowed = true) : IAuthorizationHandler<BoundaryAuthorized>
    {
        public Task<AuthorizationResult> AuthorizeAsync(BoundaryAuthorized request, CancellationToken cancellationToken)
        {
            calls.Add(name);
            return Task.FromResult(new AuthorizationResult(allowed));
        }
    }

    private sealed class ReadOnlyOnly<T>(params T[] items) : IReadOnlyCollection<T>
    {
        public int Count => items.Length;
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static IEnumerable<T> EnumerableOnly<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static Task<string> Next() => Task.FromResult("ran");

    [Fact]
    public async Task Authorization_Read_Only_Collection_Without_Handlers_Is_Rejected_By_Default()
    {
        var behavior = new AuthorizationBehavior<BoundaryAuthorized, string>(new ReadOnlyOnly<IAuthorizationHandler<BoundaryAuthorized>>());
        await Assert.ThrowsAsync<MissingAuthorizationHandlerException>(() => behavior.Handle(new("u"), Next, CancellationToken.None));
    }

    [Fact]
    public async Task Authorization_Read_Only_Collection_Without_Handlers_Runs_Only_When_Compatibility_Is_Explicit()
    {
        var options = new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = true };
        var behavior = new AuthorizationBehavior<BoundaryAuthorized, string>(new ReadOnlyOnly<IAuthorizationHandler<BoundaryAuthorized>>(), options);
        Assert.Equal("ran", await behavior.Handle(new("u"), Next, CancellationToken.None));
    }

    [Fact]
    public async Task Authorization_Read_Only_Collection_Runs_Every_Handler_And_Stops_On_Denial()
    {
        var calls = new List<string>();
        Task<string> RecordedNext()
        {
            calls.Add("next");
            return Next();
        }
        var allowed = new AuthorizationBehavior<BoundaryAuthorized, string>(new ReadOnlyOnly<IAuthorizationHandler<BoundaryAuthorized>>(
            new RecordingAuthorizationHandler(calls, "first"), new RecordingAuthorizationHandler(calls, "second")));
        Assert.Equal("ran", await allowed.Handle(new("u"), RecordedNext, CancellationToken.None));
        Assert.Collection(calls, item => Assert.Equal("first", item), item => Assert.Equal("second", item), item => Assert.Equal("next", item));

        foreach (var compatibility in new[] { false, true })
        {
            calls.Clear();
            var options = new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = compatibility };
            var denied = new AuthorizationBehavior<BoundaryAuthorized, string>(new ReadOnlyOnly<IAuthorizationHandler<BoundaryAuthorized>>(
                new RecordingAuthorizationHandler(calls, "first"), new RecordingAuthorizationHandler(calls, "deny", false),
                new RecordingAuthorizationHandler(calls, "trailing")), options);
            var failure = await Assert.ThrowsAsync<UnauthorizedException>(() => denied.Handle(new("u"), RecordedNext, CancellationToken.None));
            Assert.Equal("Unauthorized", failure.Message);
            Assert.Collection(calls, item => Assert.Equal("first", item), item => Assert.Equal("deny", item));

            // Arrays use the separate ICollection path, including the sequence shape returned by Microsoft DI.
            calls.Clear();
            var arrayDenied = new AuthorizationBehavior<BoundaryAuthorized, string>(new IAuthorizationHandler<BoundaryAuthorized>[]
            {
                new RecordingAuthorizationHandler(calls, "first"), new RecordingAuthorizationHandler(calls, "deny", false),
                new RecordingAuthorizationHandler(calls, "trailing")
            }, options);
            var arrayFailure = await Assert.ThrowsAsync<UnauthorizedException>(() => arrayDenied.Handle(new("u"), RecordedNext, CancellationToken.None));
            Assert.Equal("Unauthorized", arrayFailure.Message);
            Assert.Collection(calls, item => Assert.Equal("first", item), item => Assert.Equal("deny", item));
        }
    }

    [Fact]
    public async Task Authorization_Plain_Enumerable_Without_Handlers_Is_Rejected_By_Default()
    {
        var behavior = new AuthorizationBehavior<BoundaryAuthorized, string>(EnumerableOnly<IAuthorizationHandler<BoundaryAuthorized>>());
        await Assert.ThrowsAsync<MissingAuthorizationHandlerException>(() => behavior.Handle(new("u"), Next, CancellationToken.None));
    }

    [Fact]
    public async Task Authorization_Plain_Enumerable_Without_Handlers_Runs_Only_When_Compatibility_Is_Explicit()
    {
        var options = new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = true };
        var behavior = new AuthorizationBehavior<BoundaryAuthorized, string>(EnumerableOnly<IAuthorizationHandler<BoundaryAuthorized>>(), options);
        Assert.Equal("ran", await behavior.Handle(new("u"), Next, CancellationToken.None));
    }

    [Fact]
    public async Task Authorization_Plain_Enumerable_Evaluates_Every_Handler_In_Order()
    {
        var calls = new List<string>();
        Task<string> RecordedNext()
        {
            calls.Add("next");
            return Next();
        }
        var allowed = new AuthorizationBehavior<BoundaryAuthorized, string>(EnumerableOnly<IAuthorizationHandler<BoundaryAuthorized>>(
            new RecordingAuthorizationHandler(calls, "first"), new RecordingAuthorizationHandler(calls, "second")));
        Assert.Equal("ran", await allowed.Handle(new("u"), RecordedNext, CancellationToken.None));
        Assert.Collection(calls, item => Assert.Equal("first", item), item => Assert.Equal("second", item), item => Assert.Equal("next", item));

        foreach (var compatibility in new[] { false, true })
        {
            calls.Clear();
            var options = new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = compatibility };
            var denied = new AuthorizationBehavior<BoundaryAuthorized, string>(EnumerableOnly<IAuthorizationHandler<BoundaryAuthorized>>(
                new RecordingAuthorizationHandler(calls, "first"), new RecordingAuthorizationHandler(calls, "deny", false),
                new RecordingAuthorizationHandler(calls, "trailing")), options);
            await Assert.ThrowsAsync<UnauthorizedException>(() => denied.Handle(new("u"), RecordedNext, CancellationToken.None));
            Assert.Collection(calls, item => Assert.Equal("first", item), item => Assert.Equal("deny", item));
        }
    }

    [Fact]
    public void Authorization_Behavior_Rejects_A_Null_Handler_Sequence()
    {
        Assert.Throws<ArgumentNullException>(() => new AuthorizationBehavior<BoundaryAuthorized, string>(null!));
    }

    #endregion

    #region Audit: constructor and sink contracts

    [Fact]
    public void Audit_Sink_Rejects_A_Null_Logger()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerAuditSink(null!));
    }

    [Fact]
    public void Audit_Behavior_Rejects_Null_Dependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new AuditBehavior<BoundaryAuthorized, string>(null!));
        Assert.Throws<ArgumentNullException>(() => new AuditBehavior<BoundaryAuthorized, string>(new LoggerAuditSink(NullLogger<LoggerAuditSink>.Instance), null!));
    }

    #endregion

    #region Generated contributions: closed-type contract

    [Fact]
    public void Generated_Contribution_Rejects_Open_Generic_Service_And_Implementation_Types()
    {
        static object Factory(IServiceProvider provider) => new object();
        var closed = typeof(List<int>);
        var open = typeof(List<>);

        Assert.Throws<ArgumentException>(() => new GeneratedMediatorContribution(open, closed, null, GeneratedMediatorCategory.Behavior,
            GeneratedMediatorPhase.Request, 0, 0, GeneratedMediatorLifetime.Transient, GeneratedMediatorActivation.Declared, Factory));
        Assert.Throws<ArgumentException>(() => new GeneratedMediatorContribution(closed, open, null, GeneratedMediatorCategory.Behavior,
            GeneratedMediatorPhase.Request, 0, 0, GeneratedMediatorLifetime.Transient, GeneratedMediatorActivation.Declared, Factory));
        var accepted = new GeneratedMediatorContribution(closed, closed, open, GeneratedMediatorCategory.Behavior,
            GeneratedMediatorPhase.Request, 3, 4, GeneratedMediatorLifetime.Singleton, GeneratedMediatorActivation.StandardOnly, Factory);
        Assert.Equal(3, accepted.Order);
        Assert.Equal(open, accepted.DefinitionType);
    }

    #endregion

    #region RetryPolicy: exception classification and hostile exception metadata

    public enum Fault { None, ReadOnly, ThrowsOnWrite, ForgetsValue, IgnoresRemove, ThrowsOnContains }

    private sealed class HostileData(Fault fault) : IDictionary
    {
        private readonly Hashtable _items = new();

        public object? this[object key]
        {
            get => fault == Fault.ForgetsValue ? null : _items[key];
            set
            {
                if (fault == Fault.ThrowsOnWrite)
                {
                    throw new NotSupportedException("rejects writes");
                }

                if (fault != Fault.ForgetsValue)
                {
                    _items[key] = value;
                }
            }
        }

        public ICollection Keys => _items.Keys;
        public ICollection Values => _items.Values;
        public bool IsReadOnly => fault == Fault.ReadOnly;
        public bool IsFixedSize => false;
        public int Count => _items.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => _items.SyncRoot;
        public void Add(object key, object? value) => this[key] = value;
        public void Clear() => _items.Clear();

        public bool Contains(object key) =>
            fault == Fault.ThrowsOnContains ? throw new NotSupportedException("cannot probe") : _items.Contains(key);

        public void CopyTo(Array array, int index) => _items.CopyTo(array, index);
        public IDictionaryEnumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Remove(object key)
        {
            if (fault != Fault.IgnoresRemove)
            {
                _items.Remove(key);
            }
        }
    }

    private sealed class ExceptionWithData(IDictionary data, Exception? inner = null) : Exception("hostile", inner)
    {
        public override IDictionary Data => data;
    }

    [Theory]
    [InlineData(Fault.ReadOnly)]
    [InlineData(Fault.ThrowsOnWrite)]
    [InlineData(Fault.ForgetsValue)]
    [InlineData(Fault.IgnoresRemove)]
    [InlineData(Fault.ThrowsOnContains)]
    public void Retry_Suppression_Is_Conservative_When_Exception_Metadata_Cannot_Hold_The_Marker(Fault fault)
    {
        Assert.True(BehaviorFailure.IsRetrySuppressed(new ExceptionWithData(new HostileData(fault))));
        Assert.False(RetryPolicy.TransientOnly(new ExceptionWithData(new HostileData(fault))));
    }

    [Fact]
    public void Retry_Suppression_Marking_Never_Masks_The_Original_Failure_When_Metadata_Is_Hostile()
    {
        BehaviorFailure.SuppressRetry(new ExceptionWithData(new HostileData(Fault.ThrowsOnContains)));
        BehaviorFailure.SuppressRetry(new ExceptionWithData(new HostileData(Fault.ThrowsOnWrite)));
    }

    [Fact]
    public void Retry_Suppression_Marking_Is_Idempotent_And_Does_Not_Leak_Into_Probing()
    {
        var failure = new InvalidOperationException("transient");
        Assert.False(BehaviorFailure.IsRetrySuppressed(failure));
        Assert.True(RetryPolicy.TransientOnly(failure));

        BehaviorFailure.SuppressRetry(failure);
        BehaviorFailure.SuppressRetry(failure);
        Assert.True(BehaviorFailure.IsRetrySuppressed(failure));
        Assert.False(RetryPolicy.TransientOnly(failure));
    }

    [Fact]
    public void Retry_Suppression_Is_Inherited_From_Inner_And_Aggregate_Members()
    {
        var suppressed = new InvalidOperationException("do not retry");
        BehaviorFailure.SuppressRetry(suppressed);

        Assert.True(BehaviorFailure.IsRetrySuppressed(new InvalidOperationException("outer", suppressed)));
        Assert.True(BehaviorFailure.IsRetrySuppressed(new AggregateException(new TimeoutException(), suppressed)));
        Assert.False(BehaviorFailure.IsRetrySuppressed(new AggregateException(new TimeoutException(), new IOException())));
        Assert.False(BehaviorFailure.IsRetrySuppressed(new InvalidOperationException("outer", new TimeoutException())));
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(ValidationException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Retry_Transient_Filter_Rejects_Non_Transient_Exception_Families(Type type)
    {
        var exception = type == typeof(ValidationException)
            ? new ValidationException(new[] { new ValidationError("p", "m") })
            : (Exception)Activator.CreateInstance(type)!;
        Assert.False(RetryPolicy.TransientOnly(exception));
    }

    [Fact]
    public void Retry_Transient_Filter_Walks_Inner_Chains_And_Aggregates()
    {
        Assert.True(RetryPolicy.TransientOnly(new InvalidOperationException("outer", new TimeoutException())));
        Assert.False(RetryPolicy.TransientOnly(new InvalidOperationException("outer", new ArgumentException("bad"))));
        Assert.True(RetryPolicy.TransientOnly(new AggregateException(new TimeoutException(), new IOException())));
        Assert.False(RetryPolicy.TransientOnly(new AggregateException(new TimeoutException(), new ArgumentException("bad"))));
    }

    #endregion

    #region Notification publishers: container shapes and argument limits

    public sealed record BoundaryNotification : INotification;

    private static NotificationHandlerExecutor Executor(Func<Task> work) =>
        new(new object(), (_, _) => work());

    private static NotificationHandlerExecutor Succeeds(List<int> log, int id) =>
        Executor(() =>
        {
            lock (log)
            {
                log.Add(id);
            }

            return Task.CompletedTask;
        });

    private static NotificationHandlerExecutor Fails(string message) =>
        Executor(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException(message);
        });

    private static readonly int[] AllNineRuns = { 1, 1, 1, 2, 2, 2, 3, 3, 3 };
    private static readonly string[] TwoFailureMessages = { "a", "b" };
    private static readonly int[] SingleSuccess = { 7 };

    public static TheoryData<int> InvalidLimits => new() { 0, -2, int.MinValue };

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    public void Publishers_Reject_Non_Positive_Limits_Other_Than_Unbounded(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskWhenAllPublisher(limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilientTaskWhenAllPublisher(limit));
        _ = new TaskWhenAllPublisher(-1);
        _ = new ResilientTaskWhenAllPublisher(1);
    }

    public static TheoryData<string, int> PublisherShapes()
    {
        var data = new TheoryData<string, int>();
        foreach (var publisher in new[] { "whenall", "resilient" })
        {
            foreach (var limit in new[] { 1, 16, -1 })
            {
                data.Add(publisher, limit);
            }
        }

        return data;
    }

    private static INotificationPublisher Create(string kind, int limit) =>
        kind == "whenall" ? new TaskWhenAllPublisher(limit) : new ResilientTaskWhenAllPublisher(limit);

    [Theory]
    [MemberData(nameof(PublisherShapes))]
    public async Task Publishers_Treat_Every_Empty_Container_Shape_As_Nothing_To_Do(string kind, int limit)
    {
        var publisher = Create(kind, limit);
        await publisher.Publish(new List<NotificationHandlerExecutor>(), new BoundaryNotification(), CancellationToken.None);
        await publisher.Publish(new ReadOnlyOnly<NotificationHandlerExecutor>(), new BoundaryNotification(), CancellationToken.None);
        await publisher.Publish(EnumerableOnly<NotificationHandlerExecutor>(), new BoundaryNotification(), CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(PublisherShapes))]
    public async Task Publishers_Run_Every_Handler_For_Each_Container_Shape(string kind, int limit)
    {
        var publisher = Create(kind, limit);
        var log = new List<int>();
        var handlers = new[] { Succeeds(log, 1), Succeeds(log, 2), Succeeds(log, 3) };

        await publisher.Publish(handlers.ToList(), new BoundaryNotification(), CancellationToken.None);
        await publisher.Publish(new ReadOnlyOnly<NotificationHandlerExecutor>(handlers), new BoundaryNotification(), CancellationToken.None);
        await publisher.Publish(EnumerableOnly(handlers), new BoundaryNotification(), CancellationToken.None);

        Assert.Equal(AllNineRuns, log.OrderBy(x => x).ToArray());
    }

    [Theory]
    [MemberData(nameof(PublisherShapes))]
    public async Task Publishers_Surface_Every_Failure_With_Their_Documented_Lone_Failure_Shape(string kind, int limit)
    {
        var publisher = Create(kind, limit);
        if (kind == "whenall")
        {
            // The fail-fast publisher rethrows a lone handler failure unwrapped.
            var lone = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                publisher.Publish(EnumerableOnly(Fails("only")), new BoundaryNotification(), CancellationToken.None));
            Assert.Equal("only", lone.Message);
        }
        else
        {
            // The resilient publisher always reports the collected set, even when it holds one failure.
            var lone = await Assert.ThrowsAsync<AggregateException>(() =>
                publisher.Publish(EnumerableOnly(Fails("only")), new BoundaryNotification(), CancellationToken.None));
            Assert.Equal("only", Assert.Single(lone.InnerExceptions).Message);
        }

        var many = await Assert.ThrowsAsync<AggregateException>(() =>
            publisher.Publish(EnumerableOnly(Fails("a"), Fails("b")), new BoundaryNotification(), CancellationToken.None));
        Assert.Equal(TwoFailureMessages, many.InnerExceptions.Select(e => e.Message).OrderBy(m => m).ToArray());
    }

    [Fact]
    public async Task Resilient_Publisher_Completes_Succeeding_Handlers_When_Others_Fail_Without_A_Limit()
    {
        var log = new List<int>();
        var publisher = new ResilientTaskWhenAllPublisher(-1);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => publisher.Publish(
            new ReadOnlyOnly<NotificationHandlerExecutor>(Fails("x"), Succeeds(log, 7), Fails("y")),
            new BoundaryNotification(), CancellationToken.None));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(SingleSuccess, log);
    }

    [Fact]
    public async Task Publishers_Observe_A_Cancelled_Token_Before_Starting_Bounded_Handlers()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var log = new List<int>();
        var publisher = new TaskWhenAllPublisher(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.Publish(
            EnumerableOnly(Succeeds(log, 1), Succeeds(log, 2)), new BoundaryNotification(), cancelled.Token));
        Assert.Empty(log);
    }

    #endregion

    #region MeridianMediatorConfiguration: registration contract violations

    public interface IOtherService { }

    public sealed class NotABehavior { }

    public sealed class BoundaryBehavior : IPipelineBehavior<BoundaryAuthorized, string>
    {
        public Task<string> Handle(BoundaryAuthorized request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => next();
    }

    public sealed class BoundaryStreamBehavior : IStreamPipelineBehavior<BoundaryStreamRequest, int>
    {
        public IAsyncEnumerable<int> Handle(BoundaryStreamRequest request, StreamHandlerDelegate<int> next, CancellationToken cancellationToken) => next();
    }

    public sealed record BoundaryStreamRequest : IStreamRequest<int>;

    public abstract class AbstractBehavior : IPipelineBehavior<BoundaryAuthorized, string>
    {
        // Explicit implementation: the type is abstract (not a concrete behavior) without exposing a virtual Handle.
        Task<string> IPipelineBehavior<BoundaryAuthorized, string>.Handle(BoundaryAuthorized request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => next();
    }

    public sealed class OpenBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
    }

    public sealed class OpenStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
    }

    private static readonly Type ClosedPipeline = typeof(IPipelineBehavior<BoundaryAuthorized, string>);
    private static readonly Type ClosedStream = typeof(IStreamPipelineBehavior<BoundaryStreamRequest, int>);

    [Fact]
    public void Configuration_Rejects_Closed_Pipeline_Registrations_That_Break_The_Contract()
    {
        var configuration = new MeridianMediatorConfiguration();
        Assert.Throws<ArgumentException>(() => configuration.AddBehavior<NotABehavior>());
        Assert.Throws<ArgumentException>(() => configuration.AddClosedBehavior(typeof(IOtherService), typeof(BoundaryBehavior)));
        Assert.Throws<ArgumentException>(() => configuration.AddClosedBehavior(typeof(IPipelineBehavior<,>), typeof(BoundaryBehavior)));
        Assert.Throws<ArgumentException>(() => configuration.AddClosedBehavior(ClosedPipeline, typeof(OpenBehavior<,>)));
        Assert.Throws<ArgumentException>(() => configuration.AddClosedBehavior(ClosedPipeline, typeof(AbstractBehavior)));
        Assert.Throws<ArgumentException>(() => configuration.AddClosedBehavior(ClosedPipeline, typeof(IPipelineBehavior<BoundaryAuthorized, string>)));
        Assert.Throws<ArgumentException>(() => configuration.AddClosedBehavior(ClosedPipeline, typeof(NotABehavior)));
        Assert.Empty(configuration.ClosedBehaviors);

        configuration.AddClosedBehavior(ClosedPipeline, typeof(BoundaryBehavior), 5);
        configuration.AddBehavior<BoundaryBehavior>(6);
        Assert.Equal(2, configuration.ClosedBehaviors.Count);
    }

    [Fact]
    public void Configuration_Rejects_Open_Pipeline_Registrations_That_Break_The_Contract()
    {
        var configuration = new MeridianMediatorConfiguration();
        Assert.Throws<ArgumentNullException>(() => configuration.AddOpenBehavior(null!));
        Assert.Throws<ArgumentException>(() => configuration.AddOpenBehavior(typeof(BoundaryBehavior)));
        Assert.Throws<ArgumentException>(() => configuration.AddOpenBehavior(typeof(List<>)));
        Assert.Empty(configuration.OpenBehaviors);

        configuration.AddOpenBehavior(typeof(OpenBehavior<,>), 3);
        Assert.Single(configuration.OpenBehaviors);
    }

    [Fact]
    public void Configuration_Rejects_Stream_Registrations_That_Break_The_Contract()
    {
        var configuration = new MeridianMediatorConfiguration();
        Assert.Throws<ArgumentException>(() => configuration.AddStreamBehavior<NotABehavior>());
        Assert.Throws<ArgumentException>(() => configuration.AddClosedStreamBehavior(typeof(IOtherService), typeof(BoundaryStreamBehavior)));
        Assert.Throws<ArgumentException>(() => configuration.AddClosedStreamBehavior(ClosedStream, typeof(OpenStreamBehavior<,>)));
        Assert.Throws<ArgumentException>(() => configuration.AddClosedStreamBehavior(ClosedStream, typeof(BoundaryBehavior)));
        Assert.Throws<ArgumentNullException>(() => configuration.AddOpenStreamBehavior(null!));
        Assert.Throws<ArgumentException>(() => configuration.AddOpenStreamBehavior(typeof(BoundaryStreamBehavior)));
        Assert.Throws<ArgumentException>(() => configuration.AddOpenStreamBehavior(typeof(List<>)));
        Assert.Empty(configuration.ClosedStreamBehaviors);
        Assert.Empty(configuration.OpenStreamBehaviors);

        configuration.AddStreamBehavior<BoundaryStreamBehavior>();
        configuration.AddOpenStreamBehavior(typeof(OpenStreamBehavior<,>));
        Assert.Single(configuration.ClosedStreamBehaviors);
        Assert.Single(configuration.OpenStreamBehaviors);
    }

    #endregion

    #region DynamicRegistrationGuard: the shared Native AOT failure for reflective registration

    // Callers reach the guard only when RuntimeFeature.IsDynamicCodeSupported is false, which the Native AOT lanes execute
    // (case native-scanning-unsupported). Its failure contract is pinned here directly instead of being waived from the
    // coverage gate: the exception names the rejected operation and the supported generated-registry replacement.
    [Fact]
    public void Dynamic_Registration_Guard_Names_The_Operation_And_The_Generated_Registry_Replacement()
    {
        var failure = DynamicRegistrationGuard.Unsupported("RegisterServicesFromAssembly");

        Assert.IsType<NotSupportedException>(failure);
        Assert.StartsWith("RegisterServicesFromAssembly needs run-time reflection or code generation", failure.Message, StringComparison.Ordinal);
        Assert.Contains("(Native AOT)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[GenerateMediatorRegistry]", failure.Message, StringComparison.Ordinal);
        Assert.Contains("AddGeneratedRegistry<TRegistry>()", failure.Message, StringComparison.Ordinal);
        Assert.Contains("RequireGeneratedDispatch = true", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    #endregion
}

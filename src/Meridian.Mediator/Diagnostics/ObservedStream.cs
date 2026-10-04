using System.Diagnostics;
using Meridian.Mediator.Streaming;
using Meridian.Mediator.Wrappers;

namespace Meridian.Mediator.Diagnostics;

/// <summary>
/// Wraps one stream so each acquired enumerator finishes exactly once, including its owned cleanup, for both the
/// stream <see cref="Activity"/> and the stream metrics. Only created when an activity listener or an enabled
/// stream instrument is present; otherwise the handler's enumerable is returned untouched.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <remarks>
/// Two acquisition modes preserve the pre-existing timing of each configuration. A deferred stream (activity
/// tracing) resolves the handler on the first <c>MoveNextAsync</c>, exactly as the former iterator did, and combines
/// the creation and enumeration tokens itself. An eager stream (metrics only) wraps the enumerable the handler
/// already produced and asks it for its enumerator immediately, exactly as an unobserved stream would.
/// </remarks>
internal abstract class ObservedStream<T> : IAsyncEnumerable<T>
{
    private readonly MediatorMetrics _metrics;
    private readonly MediatorTelemetryOptions _telemetry;
    private readonly Type _requestType;
    private readonly Type? _responseType;
    private readonly bool _useActivity;
    private readonly CancellationToken _creationToken;

    protected ObservedStream(
        MediatorMetrics metrics,
        MediatorTelemetryOptions telemetry,
        Type requestType,
        Type? responseType,
        bool useActivity,
        CancellationToken creationToken)
    {
        _metrics = metrics;
        _telemetry = telemetry;
        _requestType = requestType;
        _responseType = responseType;
        _useActivity = useActivity;
        _creationToken = creationToken;
    }

    /// <summary>Whether the handler enumerator is acquired on the first move (activity tracing) or immediately.</summary>
    private bool IsDeferred => _useActivity;

    /// <summary>Resolves the handler and acquires its enumerator under the combined token (deferred mode only).</summary>
    protected abstract IAsyncEnumerator<T> AcquireDeferred(CancellationToken effectiveToken);

    /// <summary>Acquires the enumerator of an already created handler enumerable (eager mode only).</summary>
    protected abstract IAsyncEnumerator<T> AcquireEager(CancellationToken consumerToken);

    /// <summary>Wraps an already created handler enumerable (metrics only; no ambient activity is touched).</summary>
    internal static ObservedStream<T> Eager(
        MediatorMetrics metrics,
        MediatorTelemetryOptions telemetry,
        Type requestType,
        IAsyncEnumerable<T> source,
        CancellationToken creationToken) =>
        new EagerStream(metrics, telemetry, requestType, source, creationToken);

    private sealed class EagerStream : ObservedStream<T>
    {
        private readonly IAsyncEnumerable<T> _source;

        internal EagerStream(
            MediatorMetrics metrics, MediatorTelemetryOptions telemetry, Type requestType,
            IAsyncEnumerable<T> source, CancellationToken creationToken)
            : base(metrics, telemetry, requestType, null, false, creationToken)
        {
            _source = source;
        }

        protected override IAsyncEnumerator<T> AcquireDeferred(CancellationToken effectiveToken) =>
            throw new InvalidOperationException("An eager stream acquires its enumerator immediately.");

        protected override IAsyncEnumerator<T> AcquireEager(CancellationToken consumerToken) =>
            _source.GetAsyncEnumerator(consumerToken);
    }

    /// <summary>Handler resolution and enumerator acquisition are deferred to the first move (activity tracing).</summary>
    internal static ObservedStream<TResponse> Deferred<TResponse>(
        MediatorMetrics metrics,
        MediatorTelemetryOptions telemetry,
        IStreamRequest<TResponse> request,
        Type requestType,
        StreamRequestHandlerWrapper<TResponse> handler,
        IServiceProvider serviceProvider,
        CancellationToken creationToken) =>
        new DeferredTypedStream<TResponse>(metrics, telemetry, request, requestType, handler, serviceProvider, creationToken);

    internal sealed class DeferredTypedStream<TResponse> : ObservedStream<TResponse>
    {
        private readonly IStreamRequest<TResponse> _request;
        private readonly StreamRequestHandlerWrapper<TResponse> _handler;
        private readonly IServiceProvider _serviceProvider;

        internal DeferredTypedStream(
            MediatorMetrics metrics, MediatorTelemetryOptions telemetry, IStreamRequest<TResponse> request,
            Type requestType, StreamRequestHandlerWrapper<TResponse> handler, IServiceProvider serviceProvider,
            CancellationToken creationToken)
            : base(metrics, telemetry, requestType, typeof(TResponse), true, creationToken)
        {
            _request = request;
            _handler = handler;
            _serviceProvider = serviceProvider;
        }

        protected override IAsyncEnumerator<TResponse> AcquireDeferred(CancellationToken effectiveToken) =>
            _handler.Handle(_request, _serviceProvider, effectiveToken).GetAsyncEnumerator(effectiveToken);

        protected override IAsyncEnumerator<TResponse> AcquireEager(CancellationToken consumerToken) =>
            throw new InvalidOperationException("A deferred stream acquires its enumerator on the first move.");
    }

    /// <inheritdoc/>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var operation = _metrics.StreamEnabled ? _metrics.BeginStream(_requestType) : default;
        var enumerator = new Enumerator(this, operation, cancellationToken);
        if (!IsDeferred)
        {
            enumerator.AcquireEager();
        }

        return enumerator;
    }

    private sealed class Enumerator : IAsyncEnumerator<T>
    {
        private const int Created = 0;
        private const int Active = 1;
        private const int Terminal = 2;

        private readonly ObservedStream<T> _owner;
        private readonly CancellationToken _consumerToken;
        private readonly MetricOperation _operation;
        private CancellationTokenSource? _linked;
        private IAsyncEnumerator<T>? _inner;
        private Activity? _activity;
        private Exception? _pending;
        private int _state;
        private bool _finished;

        internal Enumerator(ObservedStream<T> owner, MetricOperation operation, CancellationToken consumerToken)
        {
            _owner = owner;
            _operation = operation;
            _consumerToken = consumerToken;
        }

        public T Current
        {
            get
            {
                var inner = _inner;
                if (inner is null || _state == Terminal)
                {
                    return default!;
                }

                if (!_owner._useActivity)
                {
                    return ReadCurrent(inner);
                }

                var previous = Activity.Current;
                Activity.Current = _activity;
                try
                {
                    return ReadCurrent(inner);
                }
                finally
                {
                    Activity.Current = previous;
                }
            }
        }

        internal void AcquireEager()
        {
            try
            {
                _inner = _owner.AcquireEager(_consumerToken);
            }
            catch (Exception ex)
            {
                Finish(Classify(ex), ex);
                throw;
            }
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_state == Terminal)
            {
                return false;
            }

            var useActivity = _owner._useActivity;
            var previous = useActivity ? Activity.Current : null;
            try
            {
                if (_state == Created)
                {
                    _state = Active;
                    if (useActivity)
                    {
                        _activity = Mediator.StartStreamActivity(_owner._requestType, _owner._responseType);
                    }

                    if (_inner is null)
                    {
                        if (useActivity)
                        {
                            Activity.Current = _activity;
                        }

                        try
                        {
                            var token = EffectiveToken();
                            _inner = _owner.AcquireDeferred(token);
                        }
                        catch (Exception ex)
                        {
                            Finish(Classify(ex), ex);
                            throw;
                        }
                    }
                }

                bool moved;
                try
                {
                    if (useActivity)
                    {
                        Activity.Current = _activity;
                    }

                    moved = await _inner!.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception primary)
                {
                    // The enumerator faulted; it is cleaned up now, exactly once, before the failure reaches the consumer.
                    Exception? cleanup = null;
                    try
                    {
                        if (useActivity)
                        {
                            Activity.Current = _activity;
                        }

                        await _inner!.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        cleanup = cleanupFailure;
                    }

                    if (cleanup is not null)
                    {
                        Finish(MetricOutcome.Failure, primary);
                        throw new AggregateException("Stream enumeration and cleanup failed.", primary, cleanup);
                    }

                    Finish(Classify(primary), primary);
                    throw;
                }

                if (moved)
                {
                    return true;
                }

                try
                {
                    if (useActivity)
                    {
                        Activity.Current = _activity;
                    }

                    await _inner!.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    Finish(MetricOutcome.Failure, cleanupFailure);
                    throw;
                }

                Finish(MetricOutcome.Success, null);
                return false;
            }
            finally
            {
                if (useActivity)
                {
                    Activity.Current = previous;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_state == Terminal)
            {
                return;
            }

            _state = Terminal;
            var useActivity = _owner._useActivity;
            var previous = useActivity ? Activity.Current : null;
            try
            {
                if (_inner is not null)
                {
                    try
                    {
                        if (useActivity)
                        {
                            Activity.Current = _activity;
                        }

                        await _inner.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        var pending = _pending;
                        Finish(MetricOutcome.Failure, pending ?? cleanupFailure);
                        if (pending is not null)
                        {
                            throw new AggregateException("Stream enumeration and cleanup failed.", pending, cleanupFailure);
                        }

                        throw;
                    }
                }

                if (_pending is not null)
                {
                    Finish(MetricOutcome.Failure, _pending);
                }
                else
                {
                    Finish(MetricOutcome.Disposed, null);
                }
            }
            finally
            {
                if (useActivity)
                {
                    Activity.Current = previous;
                }
            }
        }

        private T ReadCurrent(IAsyncEnumerator<T> inner)
        {
            try
            {
                return inner.Current;
            }
            catch (Exception ex)
            {
                // The consumer sees this failure through Current; cleanup and the terminal event follow in DisposeAsync.
                _pending ??= ex;
                throw;
            }
        }

        private MetricOutcome Classify(Exception exception) =>
            exception is OperationCanceledException &&
            (_consumerToken.IsCancellationRequested || _owner._creationToken.IsCancellationRequested)
                ? MetricOutcome.Cancellation
                : MetricOutcome.Failure;

        // Mirrors [EnumeratorCancellation] semantics of the compiler-generated iterator this wrapper replaces.
        private CancellationToken EffectiveToken()
        {
            var creation = _owner._creationToken;
            if (_consumerToken == default || _consumerToken == creation)
            {
                return creation;
            }

            if (creation == default)
            {
                return _consumerToken;
            }

            _linked = CancellationTokenSource.CreateLinkedTokenSource(creation, _consumerToken);
            return _linked.Token;
        }

        private void Finish(MetricOutcome outcome, Exception? failure)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _state = Terminal;
            var activity = _activity;
            if (activity is not null)
            {
                if (outcome == MetricOutcome.Success)
                {
                    activity.SetStatus(ActivityStatusCode.Ok);
                }
                else if (outcome == MetricOutcome.Failure && failure is not null)
                {
                    Mediator.MarkActivityFailure(_owner._telemetry, activity, failure);
                }

                activity.Dispose();
            }

            _linked?.Dispose();
            _owner._metrics.EndStream(_operation, outcome, StreamPhase.Enumeration);
        }
    }
}

/// <summary>Deferred object stream (activity tracing) over a non-generic stream handler wrapper.</summary>
internal sealed class DeferredObjectStream : ObservedStream<object?>
{
    private readonly object _request;
    private readonly StreamRequestHandlerBase _handler;
    private readonly IServiceProvider _serviceProvider;

    internal DeferredObjectStream(
        MediatorMetrics metrics, MediatorTelemetryOptions telemetry, object request, Type requestType,
        StreamRequestHandlerBase handler, IServiceProvider serviceProvider, CancellationToken creationToken)
        : base(metrics, telemetry, requestType, null, true, creationToken)
    {
        _request = request;
        _handler = handler;
        _serviceProvider = serviceProvider;
    }

    protected override IAsyncEnumerator<object?> AcquireDeferred(CancellationToken effectiveToken) =>
        _handler.Handle(_request, _serviceProvider, effectiveToken).GetAsyncEnumerator(effectiveToken);

    protected override IAsyncEnumerator<object?> AcquireEager(CancellationToken consumerToken) =>
        throw new InvalidOperationException("A deferred stream acquires its enumerator on the first move.");
}

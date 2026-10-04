using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Meridian.Mediator.Diagnostics;

/// <summary>
/// Provider-owned, optional instrumentation for Meridian.Mediator. Owns the <c>Meridian.Mediator</c>
/// <see cref="System.Diagnostics.Metrics.Meter"/>, its fixed instruments and a bounded, immutable table of admitted
/// message-type tag values.
/// </summary>
/// <remarks>
/// <para>
/// Every instrument has a finite name, unit and tag whitelist. Tags never contain cache/idempotency keys,
/// request payload or member values, user or tenant identifiers, authorization reasons or exception details.
/// The only open-ended dimension is <c>request.type</c>, which is bounded by
/// <see cref="MediatorMetricsOptions.MaximumRequestTypes"/> plus one constant <c>overflow</c> value.
/// </para>
/// <para>
/// A disabled instance (the default) creates no meter. Instances are intended to be created once per root service
/// provider by <c>AddMeridianMediator</c>; manual composition can pass one to the public behavior and
/// <see cref="Mediator"/> constructors that accept it. Measurement is best effort: an exception thrown by a listener
/// callback is swallowed and never replaces the business outcome. Telemetry is never retried or replayed.
/// </para>
/// </remarks>
public sealed class MediatorMetrics : IDisposable
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "Meridian.Mediator";

    /// <summary>The single tag value shared by every message type outside the admitted set.</summary>
    public const string OverflowLabel = "overflow";

    /// <summary>The maximum length of an admitted type label; longer canonical names use the overflow value.</summary>
    internal const int MaximumLabelLength = 128;

    internal const string TypeTagKey = "request.type";

    private static readonly string[] OutcomeNames = { "success", "failure", "cancellation", "disposed" };
    private static readonly string[] PhaseNames = { "dispatch", "enumeration" };
    private static readonly string[] CacheStateNames = { "hit", "miss", "error", "cancellation" };
    private static readonly string[] AuthorizationReasonNames = { "denied", "missing_handler" };
    private static readonly string[] IdempotencyModeNames = { "legacy", "atomic", "leased" };
    private static readonly string[] IdempotencyStateNames =
    {
        "claimed", "miss", "replayed", "in_flight", "completed", "released", "lease_lost", "uncertain", "error", "cancellation",
    };

    private static readonly KeyValuePair<string, object?>[] OutcomeTags = BuildTags("outcome", OutcomeNames);
    private static readonly KeyValuePair<string, object?>[] PhaseTags = BuildTags("phase", PhaseNames);
    private static readonly KeyValuePair<string, object?>[] CacheStateTags = BuildTags("state", CacheStateNames);
    private static readonly KeyValuePair<string, object?>[] AuthorizationReasonTags = BuildTags("reason", AuthorizationReasonNames);
    private static readonly KeyValuePair<string, object?>[] IdempotencyModeTags = BuildTags("mode", IdempotencyModeNames);
    private static readonly KeyValuePair<string, object?>[] IdempotencyStateTags = BuildTags("state", IdempotencyStateNames);

    private readonly Meter? _meter;
    private readonly bool _ownsMeter;
    private readonly FrozenDictionary<Type, string> _labels = FrozenDictionary<Type, string>.Empty;
    private readonly Counter<long>? _requestCount;
    private readonly Histogram<double>? _requestDuration;
    private readonly Counter<long>? _notificationCount;
    private readonly Counter<long>? _streamCount;
    private readonly Histogram<double>? _streamDuration;
    private readonly Counter<long>? _cacheLookupCount;
    private readonly Counter<long>? _retryAttemptCount;
    private readonly Counter<long>? _validationRejectionCount;
    private readonly Counter<long>? _authorizationRejectionCount;
    private readonly Counter<long>? _idempotencyStateCount;

    private MediatorMetrics()
    {
    }

    /// <summary>
    /// Creates a metrics component. When <see cref="MediatorMetricsOptions.Enabled"/> is <c>false</c> the component is
    /// inert and creates no meter.
    /// </summary>
    /// <param name="options">The metrics options.</param>
    /// <param name="knownTypes">
    /// The closed request, notification and stream types known at startup. At most
    /// <see cref="MediatorMetricsOptions.MaximumRequestTypes"/> of them are admitted, deterministically by canonical
    /// name. The supplied sequence is copied; no rejected type is retained.
    /// </param>
    public MediatorMetrics(MediatorMetricsOptions options, IEnumerable<Type> knownTypes)
        : this(options, knownTypes, null)
    {
    }

    /// <summary>
    /// Creates a metrics component that obtains its meter from <paramref name="meterFactory"/> when one is supplied.
    /// </summary>
    /// <param name="options">The metrics options.</param>
    /// <param name="knownTypes">The closed message types known at startup (see the two-argument overload).</param>
    /// <param name="meterFactory">An optional factory; the factory then owns the meter lifetime.</param>
    public MediatorMetrics(MediatorMetricsOptions options, IEnumerable<Type> knownTypes, IMeterFactory? meterFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(knownTypes);
        if (options.MaximumRequestTypes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaximumRequestTypes, "MediatorMetricsOptions.MaximumRequestTypes must not be negative.");
        }

        if (!options.Enabled)
        {
            return;
        }

        _labels = BuildAdmission(knownTypes, options.MaximumRequestTypes);
        var version = typeof(MediatorMetrics).Assembly.GetName().Version?.ToString();
        if (meterFactory is null)
        {
            _meter = new Meter(MeterName, version);
            _ownsMeter = true;
        }
        else
        {
            _meter = meterFactory.Create(new MeterOptions(MeterName) { Version = version });
        }

        _requestCount = _meter.CreateCounter<long>(
            "meridian.mediator.request.count", "{operation}", "Terminal outcomes of Send invocations.");
        _requestDuration = _meter.CreateHistogram<double>(
            "meridian.mediator.request.duration", "ms", "Whole Send duration including pipeline and retry delays.");
        _notificationCount = _meter.CreateCounter<long>(
            "meridian.mediator.notification.count", "{operation}", "Terminal outcomes of Publish invocations.");
        _streamCount = _meter.CreateCounter<long>(
            "meridian.mediator.stream.count", "{operation}", "Failed stream dispatches and terminal stream enumerations.");
        _streamDuration = _meter.CreateHistogram<double>(
            "meridian.mediator.stream.duration", "ms", "Failed dispatch duration or enumeration lifetime including cleanup.");
        _cacheLookupCount = _meter.CreateCounter<long>(
            "meridian.mediator.cache.lookup.count", "{lookup}", "Initial typed cache lookup decisions.");
        _retryAttemptCount = _meter.CreateCounter<long>(
            "meridian.mediator.retry.attempt.count", "{attempt}", "Additional retry attempts that were actually invoked.");
        _validationRejectionCount = _meter.CreateCounter<long>(
            "meridian.mediator.validation.rejection.count", "{rejection}", "Requests rejected by a validation behavior.");
        _authorizationRejectionCount = _meter.CreateCounter<long>(
            "meridian.mediator.authorization.rejection.count", "{rejection}", "Requests rejected by an authorization behavior.");
        _idempotencyStateCount = _meter.CreateCounter<long>(
            "meridian.mediator.idempotency.state.count", "{transition}", "Confirmed idempotency decisions and transitions.");
    }

    /// <summary>The shared inert component used by every constructor that does not receive one.</summary>
    internal static MediatorMetrics Disabled { get; } = new();

    /// <summary>Gets a value indicating whether this component creates instruments.</summary>
    public bool IsEnabled => _meter is not null;

    /// <summary>The meter owned by this component, or <c>null</c> when disabled. Test and diagnostics seam.</summary>
    internal Meter? Meter => _meter;

    /// <summary>The number of admitted (non-overflow) type labels.</summary>
    internal int AdmittedTypeCount => _labels.Count;

    // The gates sit on every operation, so the disabled case (a null instrument) must be a single compare.
    internal bool RequestEnabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _requestCount is not null && AnyEnabled(_requestCount, _requestDuration!);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AnyEnabled(Instrument first, Instrument second) => first.Enabled || second.Enabled;

    internal bool NotificationEnabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _notificationCount is not null && _notificationCount.Enabled;
    }

    internal bool CacheLookupEnabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _cacheLookupCount is not null && _cacheLookupCount.Enabled;
    }

    internal bool RetryEnabled => _retryAttemptCount is { Enabled: true };

    internal bool ValidationEnabled => _validationRejectionCount is { Enabled: true };

    internal bool AuthorizationEnabled => _authorizationRejectionCount is { Enabled: true };

    internal bool StreamEnabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _streamCount is not null && AnyEnabled(_streamCount, _streamDuration!);
    }

    /// <summary>Returns the admitted label for a type, or the shared overflow value.</summary>
    internal string LabelFor(Type? type) =>
        type is not null && _labels.TryGetValue(type, out var label) ? label : OverflowLabel;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter?.Dispose();
        }
    }

    // Operation boundaries ------------------------------------------------------------------------------------------

    internal MetricOperation BeginRequest(Type? type)
    {
        var count = _requestCount!.Enabled;
        var time = _requestDuration!.Enabled;
        if (!count && !time)
        {
            return default;
        }

        return new MetricOperation(LabelFor(type), time ? Stopwatch.GetTimestamp() : 0, count, time);
    }

    internal void EndRequest(in MetricOperation operation, MetricOutcome outcome)
    {
        if (operation.Label is null)
        {
            return;
        }

        var type = new KeyValuePair<string, object?>(TypeTagKey, operation.Label);
        var tag = OutcomeTags[(int)outcome];
#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        if (operation.Count)
        {
            try { _requestCount!.Add(1, type, tag); } catch (Exception) { }
        }

        if (operation.Time)
        {
            try { _requestDuration!.Record(ElapsedMilliseconds(operation.Start), type, tag); } catch (Exception) { }
        }
#pragma warning restore CA1031
    }

    internal MetricOperation BeginNotification(Type? type)
    {
        if (!_notificationCount!.Enabled)
        {
            return default;
        }

        return new MetricOperation(LabelFor(type), 0, true, false);
    }

    internal void EndNotification(in MetricOperation operation, MetricOutcome outcome)
    {
        if (operation.Label is null)
        {
            return;
        }

#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        try
        {
            _notificationCount!.Add(1, new KeyValuePair<string, object?>(TypeTagKey, operation.Label), OutcomeTags[(int)outcome]);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    internal MetricOperation BeginStream(Type? type)
    {
        var count = _streamCount!.Enabled;
        var time = _streamDuration!.Enabled;
        if (!count && !time)
        {
            return default;
        }

        return new MetricOperation(LabelFor(type), time ? Stopwatch.GetTimestamp() : 0, count, time);
    }

    internal void EndStream(in MetricOperation operation, MetricOutcome outcome, StreamPhase phase)
    {
        if (operation.Label is null)
        {
            return;
        }

        var type = new KeyValuePair<string, object?>(TypeTagKey, operation.Label);
        var tag = OutcomeTags[(int)outcome];
        var phaseTag = PhaseTags[(int)phase];
#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        if (operation.Count)
        {
            try { _streamCount!.Add(1, type, tag, phaseTag); } catch (Exception) { }
        }

        if (operation.Time)
        {
            try { _streamDuration!.Record(ElapsedMilliseconds(operation.Start), type, tag, phaseTag); } catch (Exception) { }
        }
#pragma warning restore CA1031
    }

    // Task observation ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Records the terminal outcome of an already created task exactly once. A completed task is recorded
    /// synchronously and returned unchanged (identity and exception shape preserved); an incomplete task is
    /// observed by a continuation that rethrows the original exception object.
    /// </summary>
    internal Task ObserveRequest(Task task, MetricOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Label is null)
        {
            return task;
        }

        if (task.IsCompleted)
        {
            EndRequest(operation, ClassifyCompleted(task, cancellationToken));
            return task;
        }

        return ObserveRequestAsync(task, operation, cancellationToken);
    }

    internal Task<T> ObserveRequest<T>(Task<T> task, MetricOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Label is null)
        {
            return task;
        }

        if (task.IsCompleted)
        {
            EndRequest(operation, ClassifyCompleted(task, cancellationToken));
            return task;
        }

        return ObserveRequestAsync(task, operation, cancellationToken);
    }

    internal Task ObserveNotification(Task task, MetricOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Label is null)
        {
            return task;
        }

        if (task.IsCompleted)
        {
            EndNotification(operation, ClassifyCompleted(task, cancellationToken));
            return task;
        }

        return ObserveNotificationAsync(task, operation, cancellationToken);
    }

    private async Task ObserveRequestAsync(Task task, MetricOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EndRequest(operation, Classify(ex, cancellationToken));
            throw;
        }

        EndRequest(operation, MetricOutcome.Success);
    }

    private async Task<T> ObserveRequestAsync<T>(Task<T> task, MetricOperation operation, CancellationToken cancellationToken)
    {
        T result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EndRequest(operation, Classify(ex, cancellationToken));
            throw;
        }

        EndRequest(operation, MetricOutcome.Success);
        return result;
    }

    private async Task ObserveNotificationAsync(Task task, MetricOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EndNotification(operation, Classify(ex, cancellationToken));
            throw;
        }

        EndNotification(operation, MetricOutcome.Success);
    }

    /// <summary>
    /// Cancellation means an <see cref="OperationCanceledException"/> accompanied by cancellation of the token the
    /// caller supplied; any other exception, including an unrelated cancellation, is a failure.
    /// </summary>
    internal static MetricOutcome Classify(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? MetricOutcome.Cancellation
            : MetricOutcome.Failure;

    private static MetricOutcome ClassifyCompleted(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompletedSuccessfully)
        {
            return MetricOutcome.Success;
        }

        if (task.IsCanceled)
        {
            return cancellationToken.IsCancellationRequested ? MetricOutcome.Cancellation : MetricOutcome.Failure;
        }

        var inner = task.Exception?.InnerExceptions;
        return inner is { Count: > 0 } ? Classify(inner[0], cancellationToken) : MetricOutcome.Failure;
    }

    // Behavior-owned events -----------------------------------------------------------------------------------------

    internal void RecordCacheLookup(Type requestType, CacheLookupState state)
    {
        var counter = _cacheLookupCount;
        if (counter is null || !counter.Enabled)
        {
            return;
        }

#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        try
        {
            counter.Add(1, new KeyValuePair<string, object?>(TypeTagKey, LabelFor(requestType)), CacheStateTags[(int)state]);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    internal void RecordRetryAttempt(Type requestType)
    {
        var counter = _retryAttemptCount;
        if (counter is null || !counter.Enabled)
        {
            return;
        }

#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        try
        {
            counter.Add(1, new KeyValuePair<string, object?>(TypeTagKey, LabelFor(requestType)));
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    internal void RecordValidationRejection(Type requestType)
    {
        var counter = _validationRejectionCount;
        if (counter is null || !counter.Enabled)
        {
            return;
        }

#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        try
        {
            counter.Add(1, new KeyValuePair<string, object?>(TypeTagKey, LabelFor(requestType)));
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    internal void RecordAuthorizationRejection(Type requestType, AuthorizationRejection reason)
    {
        var counter = _authorizationRejectionCount;
        if (counter is null || !counter.Enabled)
        {
            return;
        }

#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        try
        {
            counter.Add(1, new KeyValuePair<string, object?>(TypeTagKey, LabelFor(requestType)), AuthorizationReasonTags[(int)reason]);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    internal bool IdempotencyEnabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _idempotencyStateCount is not null && _idempotencyStateCount.Enabled;
    }

    internal void RecordIdempotency(Type requestType, IdempotencyMode mode, IdempotencyState state)
    {
        var counter = _idempotencyStateCount;
        if (counter is null || !counter.Enabled)
        {
            return;
        }

#pragma warning disable CA1031 // A faulty listener must never replace the business outcome.
        try
        {
            counter.Add(
                1,
                new KeyValuePair<string, object?>(TypeTagKey, LabelFor(requestType)),
                IdempotencyModeTags[(int)mode],
                IdempotencyStateTags[(int)state]);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    // Time and admission --------------------------------------------------------------------------------------------

    internal static double ElapsedMilliseconds(long startTimestamp) =>
        ToMilliseconds(Stopwatch.GetTimestamp() - startTimestamp, Stopwatch.Frequency);

    /// <summary>Converts a monotonic timestamp delta to milliseconds, clamped to be non-negative.</summary>
    internal static double ToMilliseconds(long timestampDelta, long frequency) =>
        timestampDelta <= 0 ? 0d : timestampDelta * 1000d / frequency;

    private static KeyValuePair<string, object?>[] BuildTags(string key, string[] values)
    {
        var tags = new KeyValuePair<string, object?>[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            tags[i] = new KeyValuePair<string, object?>(key, values[i]);
        }

        return tags;
    }

    /// <summary>
    /// Admits at most <paramref name="maximum"/> distinct closed types by canonical name. Names that collide,
    /// exceed <see cref="MaximumLabelLength"/> or cannot be rendered are never admitted. Only admitted types are
    /// retained.
    /// </summary>
    private static FrozenDictionary<Type, string> BuildAdmission(IEnumerable<Type> knownTypes, int maximum)
    {
        if (maximum == 0)
        {
            return FrozenDictionary<Type, string>.Empty;
        }

        var byLabel = new Dictionary<string, Type?>(StringComparer.Ordinal);
        var seen = new HashSet<Type>();
        foreach (var type in knownTypes)
        {
            if (type is null || !seen.Add(type))
            {
                continue;
            }

            var label = DescribeType(type);
            if (label is null || label.Length > MaximumLabelLength || label == OverflowLabel)
            {
                continue;
            }

            // A canonical-name collision is ambiguous, so neither type is admitted.
            byLabel[label] = byLabel.ContainsKey(label) ? null : type;
        }

        var admitted = new List<KeyValuePair<string, Type>>();
        foreach (var pair in byLabel)
        {
            if (pair.Value is not null)
            {
                admitted.Add(new KeyValuePair<string, Type>(pair.Key, pair.Value));
            }
        }

        admitted.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        if (admitted.Count > maximum)
        {
            admitted.RemoveRange(maximum, admitted.Count - maximum);
        }

        var result = new Dictionary<Type, string>(admitted.Count);
        foreach (var pair in admitted)
        {
            result.Add(pair.Value, pair.Key);
        }

        return result.ToFrozenDictionary();
    }

    /// <summary>
    /// Renders the canonical, assembly-neutral name of a closed type (for example
    /// <c>Ns.Outer+Inner&lt;System.Int32&gt;</c>), or <c>null</c> for open, by-ref or pointer types.
    /// </summary>
    internal static string? DescribeType(Type type)
    {
        if (type.IsGenericParameter || type.ContainsGenericParameters || type.IsByRef || type.IsPointer || type.IsFunctionPointer)
        {
            return null;
        }

        var builder = new StringBuilder();
        return Append(builder, type) ? builder.ToString() : null;

        static bool Append(StringBuilder builder, Type type)
        {
            if (type.IsArray)
            {
                if (!Append(builder, type.GetElementType()!))
                {
                    return false;
                }

                builder.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
                return true;
            }

            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            var baseName = tick < 0 ? name : name.Substring(0, tick);
            var declaring = type.DeclaringType;
            if (declaring is not null)
            {
                if (!Append(builder, declaring))
                {
                    return false;
                }

                builder.Append('+');
            }
            else if (!string.IsNullOrEmpty(type.Namespace))
            {
                builder.Append(type.Namespace).Append('.');
            }

            builder.Append(baseName);
            if (type.IsGenericType)
            {
                var arguments = type.GetGenericArguments();
                var own = tick < 0 ? 0 : int.Parse(name.AsSpan(tick + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
                if (own > arguments.Length)
                {
                    return false;
                }

                if (own > 0)
                {
                    builder.Append('<');
                    for (var i = arguments.Length - own; i < arguments.Length; i++)
                    {
                        if (i > arguments.Length - own)
                        {
                            builder.Append(',');
                        }

                        if (!Append(builder, arguments[i]))
                        {
                            return false;
                        }
                    }

                    builder.Append('>');
                }
            }

            return true;
        }
    }
}

/// <summary>Value-type operation token: admitted label, start timestamp and the instruments enabled at start.</summary>
internal readonly struct MetricOperation
{
    internal MetricOperation(string label, long start, bool count, bool time)
    {
        Label = label;
        Start = start;
        Count = count;
        Time = time;
    }

    internal string? Label { get; }

    internal long Start { get; }

    internal bool Count { get; }

    internal bool Time { get; }
}

internal enum MetricOutcome : byte
{
    Success,
    Failure,
    Cancellation,
    Disposed,
}

internal enum StreamPhase : byte
{
    Dispatch,
    Enumeration,
}

internal enum CacheLookupState : byte
{
    Hit,
    Miss,
    Error,
    Cancellation,
}

internal enum AuthorizationRejection : byte
{
    Denied,
    MissingHandler,
}

internal enum IdempotencyMode : byte
{
    Legacy,
    Atomic,
    Leased,
}

internal enum IdempotencyState : byte
{
    Claimed,
    Miss,
    Replayed,
    InFlight,
    Completed,
    Released,
    LeaseLost,
    Uncertain,
    Error,
    Cancellation,
}

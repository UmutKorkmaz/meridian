# Mediator metrics

`Meridian.Mediator` can publish bounded, privacy-preserving metrics through a standard
`System.Diagnostics.Metrics.Meter`. Metrics are **opt-in**: with the default options a provider creates no meter
and no instruments, and every send and publish, and every stream that is not observed, takes the code path it had
before. A stream is observed when stream metrics are enabled with a listener **or** an `Activity` listener is
attached: such a stream is wrapped in an observing enumerator even when metrics are disabled (a small extra
allocation per stream), and if both its enumeration and its cleanup fail it throws an `AggregateException` (see
[Cost and failure behavior](#cost-and-failure-behavior)). The known-type snapshot taken by `AddMeridianMediator` is
retained even while metrics are disabled.
Meridian never installs an exporter, a hosting dependency or a periodic monitor; the application attaches its own
listener (OpenTelemetry, `dotnet-counters`, `MeterListener`).

## Enabling

<!-- snippet: mediator-metrics -->
```csharp
services.AddMeridianMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.Metrics = new MediatorMetricsOptions { Enabled = true, MaximumRequestTypes = 100 };
});
```

An application can instead register a `MediatorMetricsOptions` singleton, exactly like `MediatorTelemetryOptions`.

| Option | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Creates the `Meridian.Mediator` meter and its instruments for this provider. |
| `MaximumRequestTypes` | `100` | Distinct message types that receive their own `request.type` value per provider. `0` means overflow only. A negative value throws when the provider creates its metrics component (on the first `IMediator` resolution, even with `Enabled = false`), not when `AddMeridianMediator` is called. |

The options are an immutable snapshot read once when the provider-owned `MediatorMetrics` component is created.
The component is created once per root provider, shared by its scopes, and disposed with the provider. Two providers
built from one service collection receive independent components. If an `IMeterFactory` is registered it creates the
meter (and owns its lifetime); otherwise the component owns a plain `Meter`.

Manual composition is possible: construct `new MediatorMetrics(options, knownTypes)` and pass it to the
`Mediator` constructor or the behavior constructors that accept one. Every constructor that does not receive a
component keeps metrics disabled.

## Instruments

The meter name is `Meridian.Mediator`. Counters are `Counter<long>` that add exactly 1 per event; durations are
`Histogram<double>` in milliseconds (`ms`). Every instrument has the tag `request.type` (the bounded message type,
see below) plus the tags listed here. No other tag key or value is ever emitted.

| Instrument | Unit | Extra tags | One event means |
| --- | --- | --- | --- |
| `meridian.mediator.request.count` | `{operation}` | `outcome` = `success`, `failure`, `cancellation` | One terminal outcome per `Send` invocation. |
| `meridian.mediator.request.duration` | `ms` | `outcome` | The whole `Send` duration, including the pipeline and retry delays. |
| `meridian.mediator.notification.count` | `{operation}` | `outcome` | One terminal outcome per `Publish` invocation (zero, one or many handlers). |
| `meridian.mediator.stream.count` | `{operation}` | `outcome` = `success`, `failure`, `cancellation`, `disposed`; `phase` = `dispatch`, `enumeration` | A failed synchronous `CreateStream`, or one finished enumeration. |
| `meridian.mediator.stream.duration` | `ms` | `outcome`, `phase` | Failed dispatch duration, or the enumeration lifetime including cleanup. |
| `meridian.mediator.cache.lookup.count` | `{lookup}` | `state` = `hit`, `miss`, `error`, `cancellation` | The first typed cache lookup of a `CachingBehavior` invocation. |
| `meridian.mediator.retry.attempt.count` | `{attempt}` | none | An additional `next` invocation that actually ran (the first attempt is not counted). |
| `meridian.mediator.validation.rejection.count` | `{rejection}` | none | A validation behavior rejected the request (once, regardless of the number of errors). |
| `meridian.mediator.authorization.rejection.count` | `{rejection}` | `reason` = `denied`, `missing_handler` | An authorization behavior rejected the request. |
| `meridian.mediator.idempotency.state.count` | `{transition}` | `mode` = `legacy`, `atomic`, `leased`; `state` (below) | One confirmed idempotency decision or transition. |

### Operation semantics

* **Requests and notifications.** The operation starts before argument and dispatch validation, so a null request,
  an unknown type or a strict-dispatch miss is one `failure` in the overflow bucket. Success is recorded when the
  returned task completes successfully, never merely because a task was returned. Cancellation means an
  `OperationCanceledException` together with cancellation of the token the caller supplied; any other exception,
  including an unrelated cancellation, is a failure. A handler that calls `Send` starts a second operation.
  Retrying is a pipeline behavior and never re-counts the outer `Send`.
* **Streams.** `CreateStream` itself emits nothing on success; only a failed synchronous dispatch is counted (with
  `phase=dispatch`). Each enumerator that is acquired finishes **exactly once** (`phase=enumeration`) after its owned
  cleanup: normal exhaustion plus successful disposal is `success`, disposal before exhaustion is `disposed`, a failure
  (including a cleanup failure after apparent success) is `failure`, and a cancellation is `cancellation`. The
  duration runs from `GetAsyncEnumerator` to the end of cleanup, so it includes time the consumer spends between
  items. An enumerable that is never enumerated emits nothing, and an enumerator that is abandoned without being
  disposed cannot finish (Meridian adds no finalizer or timer and never reports a fictitious success).
* **Cache.** One lookup event per `CachingBehavior` invocation: a compatible stored value is a `hit`; a missing,
  null or incompatible value is a `miss`. A coalescing provider's `GetOrCreateAsync` result is deliberately not
  reinterpreted as a hit, a leader or a follower.
* **Retry.** Counted immediately before an additional attempt runs, after its delay and cancellation check.
* **Validation / authorization.** Counted once at the behavior's own rejection. Validator or authorization-provider
  infrastructure failures and a `ValidationException` thrown downstream are failures of the request, not rejections.

### Idempotency states

Idempotency emits transitions, not one final state per request. Do not sum the transition counter as a request
count: a successful owner emits `claimed` and `completed`; a replay emits only `replayed`.

| State | Meaning |
| --- | --- |
| `claimed` | The provider granted ownership (atomic or leased). |
| `miss` | The legacy check confirmed no stored response. |
| `replayed` | A type-compatible completed response was returned without running the handler. |
| `in_flight` | The provider reported another owner. |
| `completed` | The provider confirmed the completion or store (a legacy `null` response is never stored, so it is never `completed`). |
| `released` | The provider confirmed release after a handler failure (atomic mode only; the leased contract never releases automatically). |
| `lease_lost` | A renewal returned `false` (confirmed ownership loss); emitted at most once per claim. A provider that merely throws is not a confirmed loss. |
| `uncertain` | Business work may have run but completion, release or ownership cannot be established. Retry suppression and the original exception are unchanged. |
| `error` | A pre-handler provider failure, an invalid provider result or an incompatible replay. |
| `cancellation` | A definitive pre-handler cancellation. |

## Cardinality and privacy

The only open-ended dimension is `request.type`. At startup the provider takes the closed request, notification and
stream types it knows (generated registry manifests and the closed handler registrations present when
`AddMeridianMediator` ran, including those added by assembly scanning), orders them by canonical name and admits at
most `MaximumRequestTypes` of them. Everything else (a type beyond the budget, a type registered after
`AddMeridianMediator`, a runtime-created or invalid type, `null`) shares the single constant value `overflow`.
Admission is never decided by first use. A canonical name that is ambiguous (two types render identically), longer
than 128 characters or not renderable is never admitted. Labels are assembly-neutral names such as
`Ns.Outer+Inner<System.Int32>`. The bound is per provider; Meridian does not promise a process-wide bound across
arbitrarily many differently configured providers.

Tags never contain cache or idempotency keys, request payloads or members, user or tenant identifiers, validation
messages, authorization reasons, correlation or trace identifiers, lease tokens, handler names or any exception
type, message or stack. `MediatorTelemetryOptions` (`RecordExceptionMessage`, `RecordExceptionStackTrace`) affects
only `Activity` data, defaults to `false`, and cannot add metric tags.

## Cost and failure behavior

* Disabled metrics, and enabled metrics with no listener, check one flag per operation before any timestamp, tag,
  closure or wrapper is created. A counter-only subscription never takes a timestamp.
* Measurement is best effort. Listener attachment is not transactional: an operation that started before a listener
  attached emits nothing for itself, and a counter and its histogram are separate deliveries. An exception thrown by a
  listener callback is swallowed and never replaces the business outcome or exception; telemetry is never retried.
* When an `Activity` listener is attached, streams are observed under the stream activity: `Activity.Current` is the
  stream activity only while the producer runs and is restored to the caller's activity after every step
  (acquisition, each `MoveNextAsync`, `Current`, `DisposeAsync`), including on faults.
* A stream that is being observed (stream metrics enabled with a listener, or an `Activity` listener attached)
  whose enumeration and cleanup both fail throws an `AggregateException` that retains both original exception objects,
  primary first. A single failure keeps its identity and stack. A stream that is not observed is returned exactly as
  the handler produced it.

## Performance status

The overhead comparison in `benchmarks/Meridian.Benchmarks/README.md` was measured on a busy, non-dedicated developer
host. Its allocation figures are exact; its timings are **not** accepted performance evidence. Metrics performance
acceptance is PENDING/INCONCLUSIVE until the benchmark task (E3) re-measures it on a dedicated host. The
documentation consumer asserts the enabled example above records exactly one `meridian.mediator.request.count`
measurement with `outcome=success` and the bounded `request.type` tag.

# D — Mediator AOT, registration diagnostics and metrics design

Status: proposed. Inherits portfolio Global Constraints and requires A4/A7/A8 compatibility acceptance.

## Outcome and generated registry

Provide a real generated-dispatch alternative while retaining reflection-based registration for current applications. Generating DI registrations alone does not make Mediator.Native AOT-safe: current dispatch constructs closed wrapper types at runtime. Generate both known closed registrations and typed wrappers for requests, void commands, notifications, streams, processors and declared behaviors.

Add an optional core IGeneratedMediatorRegistry with:
- string RegistryId { get; }
- IReadOnlyCollection<Type> RequestTypes, NotificationTypes and StreamTypes { get; }
- void RegisterServices(IServiceCollection services, ServiceLifetime handlerLifetime);
- RequestHandlerBase? FindRequestHandler(Type requestType);
- NotificationHandlerWrapperBase? FindNotificationHandler(Type notificationType);
- StreamRequestHandlerBase? FindStreamHandler(Type requestType).

These use existing public wrapper bases. Generated registries hold type metadata and wrapper factories, never handler/scoped-service instances. AddGeneratedRegistry<TRegistry>() where TRegistry:class,IGeneratedMediatorRegistry,new() on MeridianMediatorConfiguration constructs a parameterless registry without runtime type activation; a new constructor overload injects composed registries without changing existing constructors. RequireGeneratedDispatch defaults false. In strict generated mode, unknown types and unrooted open behaviors fail with actionable configuration errors rather than invoking reflection. Existing explicit handler lifetime is passed to RegisterServices. Generated DI factories invoke constructors directly and resolve declared dependencies; exactly one accessible constructor or one explicit ActivatorUtilitiesConstructor is required, otherwise MERIDIANMED003 diagnoses ambiguity.

Meridian.Mediator.Generators is a separate opt-in analyzer package targeting netstandard2.0, with its compiler floor proven by packed consumers. GenerateMediatorRegistryAttribute(params Type[] handlerTypes) is emitted into the consumer compilation. An optional `Type[] ExpectedRequestTypes { get; set; } = new Type[0]` asserts completeness only for closed ordinary requests against the same attribute's explicit handler types. Expectations are semantic, deduplicated, and neither registrations nor a whitelist. No expectation is inferred from call sites, validators, processors, other registries or plugins. Invalid matching constructors produce 003 without cascading 001; unsupported handler inputs suppress incomplete missing-handler inference. Missing diagnostics use the first expected typeof location.

Attributes also accept Type[] BehaviorTypes, StreamBehaviorTypes, ValidatorTypes, PreProcessorTypes, PostProcessorTypes, ExceptionHandlerTypes and ExceptionActionTypes (all default empty), plus bool IncludeStandardPipeline=false. All selected types are explicit and closed, or open behavior types that the generator can safely close over the known requests. IncludeStandardPipeline generates correlation/audit/localized-validation registrations and declared validators; a generic AddMeridianStandard<TRegistry>() alternative avoids assembly scanning. The generic extension is IServiceCollection AddMeridianStandard<TRegistry>(this IServiceCollection services,Action<MeridianMediatorConfiguration>? configure=null) where TRegistry:class,IGeneratedMediatorRegistry,new(); equivalent standard registration is deduplicated if both mechanisms are selected.

Registry composition rejects duplicate request/stream handlers, deduplicates repeated registry registration, and permits multiple notification handlers for the same notification. Open generic handler discovery is not silently guessed: explicitly supplied closed types work, unsupported constructions receive a diagnostic. Closed behavior generic constraints are checked; marker-constrained behaviors apply only to matching requests. No Type.MakeGenericType/Activator dispatch fallback is reachable in the strict supported path.

## Diagnostics

Reserve MERIDIANMED001 missing request handler, 002 duplicate request/stream handler, 003 invalid declared registration, 004 scoped lifetime mismatch visible at compilation, and 005 unsupported generated/AOT construction. Diagnostics apply only to explicit generated-registry input where evidence is complete. Runtime plugin handlers are not declared missing merely because they are absent from one compilation.

Current AssertHandlerRegistration remains supported. Generated manifests provide documentation and runtime strict-mode validation, and coexist with legacy registration when strict mode is off. Two providers using the same request type retain separate service scopes and configuration; static caches cannot bind one application's registry or services into another.

## Observability

Add optional MediatorMetricsOptions.Enabled=false and MaximumRequestTypes=100. Meter name Meridian.Mediator emits request duration milliseconds, request success/failure/cancellation counts, notification/stream counts, cache hit/miss, retry attempts, validation/authorization rejections and idempotency state counters. Type tags use known configured request types with a bounded overflow bucket; cache keys, tenant/user IDs, request bodies, raw exception detail and arbitrary type strings are absent.

Metrics use System.Diagnostics.Metrics and existing ActivitySource, with no exporter dependency. Top-level request outcomes count once per Send call; retry attempts count inside RetryBehavior separately, cache hits/misses inside CachingBehavior, and idempotency states inside IdempotencyBehavior. Stream duration/outcome spans actual enumeration including early disposal; notification counts are per Publish call rather than handler count. Nested Send calls are independent operations. Existing privacy flags remain false and govern log/Activity behavior. Disabled/no-listener overhead is measured and must not introduce unexplained hot-path allocations.

## Native AOT acceptance

A consumer restored from actual packed packages publishes and runs on linux-x64 and osx-arm64 CI runners, executing a typed query, both void-command forms, notifications, streams, validation/audit/correlation, scoped handlers and generated DTO mapping. Add supported generated behavior closures only after the binary proves them. Trimming/AOT warnings are either fixed or narrowly documented with a demonstrated safe reason; blanket suppressions and publish-only success are not acceptance.

Runtime reflection paths remain a supported non-AOT choice. The generated support table names exactly which configurations passed. Unknown/plugin requests in strict mode fail deterministically instead of appearing supported.

## Task map

D1 registry contract and package; D2 request/void/behavior dispatch; D3 notification/stream composition; D4 packed AOT execution; D5 diagnostics/metrics and overhead proof. Distribution E documents the supported subset and migration paths.

## Approved additive contribution seam and staged delivery

`IGeneratedMediatorContributions` optionally exposes immutable `IReadOnlyList<GeneratedMediatorContribution> Contributions` and `bool IncludesStandardPipeline`. Each immutable contribution contains exact closed service/implementation types, optional open definition, category, phase, order, declaration ordinal, lifetime policy (handler/transient/singleton), activation (declared/standard-only), and a closed constructor factory. Public `GeneratedMediatorRegistration.RegisterServices` applies declared handler factories with explicit metadata identity; it never captures a provider or resolves services during registration. Base manually authored registries remain valid. Generic standard support requires this companion and fails actionably when absent.

D1 supplies metadata and direct closed handler DI registrations only. Its wrapper lookups return null; strict dispatch and selected pipeline options fail explicitly while reserved attribute options and standard-selection metadata remain available. D2 owns request/void dispatch, finite standard/behavior factories and normalization; D3 owns notification/stream wrappers and composition. Dormant standard factories, ordering phases, open constraint applicability and generic standard activation become operational in D2. No D1 result establishes Native AOT support.

D2 preserves request order/index and automatic processor phase, stream closed-before-open phase, distinct-order open occurrences, and metadata-based factory deduplication. Its fixed built-in audit factory resolves required IAuditSink and optional MediatorTelemetryOptions (Default fallback), preserving legacy constructors. Declared exception inputs activate one order-zero declaration-phase typed driver per request/response pair, explicitly overridable; adapters deduplicate per closed exception contract. A small typed exception helper preserves derived-before-base, fresh state, original rethrow and synchronous TargetInvocationException artifacts without reflective invocation. Generated repeated calls reject conflicting strictness/lifetime and registry ID collisions, while preserving first legacy publisher/configuration precedence and provider snapshots.

D1 declaration syntax is a non-generic, top-level partial class with a public parameterless constructor generated by the analyzer. Handler constructors must be accessible from that declaration, unique or uniquely marked ActivatorUtilitiesConstructor, with no unsupported keyed/ref/pointer parameters or unsatisfied required members. Optional dependency defaults are resolved through typed casts and their compile-time constants.


## D2 finite applicability and manual registry compatibility

The optional `IGeneratedMediatorBehaviorCapabilities` companion exposes immutable `IReadOnlyList<GeneratedMediatorBehaviorCapability> BehaviorCapabilities`. Each record identifies `RequestType`, `ResponseType`, `DefinitionType`, contribution `Category`, and `Applies`. Ordinary and stream contracts remain distinct even for the same CLR request type. Only compiler-proven substituted constraint failures yield `Applies=false`; missing evidence means unrooted/unsupported, and contradictory metadata is rejected. Constraint probes run in the actual registry declaration's accessibility context. The supported open ordinary behavior subset maps two distinct parameters directly to request and response, including reversed parameter positions and constraint checks; nested argument mappings and open processors are diagnosed. Runtime normalization never constructs a generic type.

Manually authored base registries remain valid without either optional companion. A null ordinary lookup permits legacy fallback when strict mode is off; strict registration rejects an advertised ordinary request with a null lookup. Complete manually authored wrappers work in strict mode. Generated request dispatch and finite standard/processor/typed exception helpers are D2 semantics; notification and stream dispatch remain the D3 capability and strict attempts report that boundary. D2 does not establish trimming or Native AOT execution.

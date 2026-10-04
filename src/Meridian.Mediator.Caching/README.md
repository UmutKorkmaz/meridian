# Meridian.Mediator.Caching

Optional memory caching for Meridian Mediator on .NET 8, 9 and 10. The package uses
Microsoft.Extensions.Caching.Memory 10.0.9 (MIT). Core Meridian packages do not
acquire a memory backend dependency.

```csharp
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Meridian.Mediator.Extensions;

services.AddMeridianMemoryCaching(options =>
    options.DefaultDuration = TimeSpan.FromMinutes(5));
services.AddMeridianMediator(config => config.AddCachingBehavior());
// Optional: ordinary caching keeps its existing miss path by default.
services.AddSingleton(new CachingBehaviorOptions { EnableRequestCoalescing = true });
```

Queries implement `ICacheableQuery`. Compatible hits return the same object; values
are not serialized or copied. Entries use absolute expiration from `CacheDuration`
or the adapter's positive `DefaultDuration` (five minutes by default). A zero or
negative per-call duration removes any existing entry and does not store the new
value. Null results are never stored. Cancellation, factory errors and backend
failures propagate. Expired reads miss immediately; physical reclamation follows
the memory backend's access/scan behavior.

`AddMeridianMemoryCaching(IServiceCollection, Action<CacheProviderOptions>? = null)`
returns the supplied collection. Repeated calls register one `ICacheProvider` and
preserve a deliberate custom provider, including scoped registrations. The first
options registration wins; every configuration call is validated. Configuration
and constructor durations are copied. The default provider and DI cache are
singletons; the provider borrows `IMemoryCache` and never disposes it. A cache
registered as an instance remains owned by its caller. Provider construction does
not resolve or retain scoped handler services.

`IConcurrentCacheProvider.GetOrCreateAsync(string key,
Func<CancellationToken, Task<object?>> factory, TimeSpan? duration,
CancellationToken cancellationToken)` shares one **live** owner per provider
instance and effective key. Its state contains only active operations and is
removed on success, null, failure or cancellation after the factory settles.
The owner's original token reaches its factory. Each waiter cancels only its own
wait; its unused factory is not retained by the provider. The owner directly awaits
the actual factory: if the factory ignores cancellation, the owner stays pending
until it completes, then cancels without storing the response. A later caller can
retry after retirement. Separate provider instances may run independent factories
even when they share the same memory cache. This is local coalescing, with no
cross-process lock or hard concurrency/memory capacity guarantee.

A factory or its logical descendant must not join a live ancestor owner operation.
This includes direct same-key nesting and an A-key → B-key → A-key owner chain.
Such a join fails immediately with a redacted `InvalidOperationException`; the
original owner and legitimate waiters receive that failure, state retires, and a
later request may retry. Context tracks inert operation identity tokens only and
is restored when the owner settles. Retired inherited context does not block a
new operation, and separate providers remain independent. For nested reads whose
logical keys overlap, use distinct logical keys or disable request coalescing.
This narrow policy does not detect arbitrary user-level deadlocks across separate
execution flows. Ordinary cache hits require no join and remain valid.

The behavior checks both ordinary and richer-provider results with `is TResponse`.
Incompatible raw-key results execute that caller's own handler once and store its
non-null response at the same key. An owner can return its own successful null;
a waiter receiving untyped shared null runs its own handler once because that
result has no response-type provenance. Twenty compatible non-null misses share
one factory; shared null and heterogeneous response types may execute additional
handlers. Coalescing is opt-in (`EnableRequestCoalescing = false` by default).

Request-type namespaced keys remain enabled by default. Commands should invalidate
`CacheKeyComposer.Compose<TQuery>(userKey)`. Legacy consumers can explicitly set
`UseRequestTypeNamespacedKeys = false` for both cache and invalidation behaviors.
Removal does not fence a live factory: it may repopulate an entry after removal.
Use suitable user-specific keys and perform authorization before caching private
responses; this memory adapter does not add authenticated key scopes.

The borrowed cache has the application's configured capacity. This adapter does
not impose a size limit on a shared application cache or keep a second cache of
expired keys. Distributed serialization, authenticated provider key scopes and
Redis are separate integrations.

See [Microsoft memory cache guidance](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory?view=aspnetcore-10.0)
and the [Memory 10.0.9 package metadata](https://www.nuget.org/packages/Microsoft.Extensions.Caching.Memory/10.0.9).

Distributed caching uses a borrowed `IDistributedCache`; configure its transport
separately. This package has no Redis client dependency. The Redis backend used by
our provider tests is a test-only dependency. Register every local response type
explicitly with stable contract IDs, positive schemas, and metadata that supports
source-generated deserialization:

```csharp
using System.Text.Json.Serialization;

var contracts = new CacheContractRegistry()
    .RegisterContract("product.v1", 1, CacheJson.Default.Product);
services.AddMeridianDistributedCaching(contracts, options =>
{
    options.ApplicationKeyPrefix = "catalog";
    options.MaximumEntryBytes = 1024 * 1024; // Total framed entry; positive.
});

public sealed record Product(string Name);
[JsonSerializable(typeof(Product))]
internal partial class CacheJson : JsonSerializerContext { }
```

`ICacheValueSerializer` has exactly `GetContractId(Type)`,
`Serialize(object?, string) -> byte[]`, and
`Deserialize(ReadOnlyMemory<byte>, string) -> object?`. The built-in JSON serializer
uses registered `JsonTypeInfo` exclusively; no assembly-qualified type name,
`Type.GetType`, assembly scan or reflection fallback activates remote data.
An exact runtime type selects the contract for an ordinary non-null cache write.
One ID maps to one exact local type/schema/metadata, and each type has one ID.
Exact repeated metadata registrations are idempotent before freeze; conflicts
fail. Registration, serializer or codec construction freezes the registry and
makes its metadata read-only. Use independently configured registries with the
same stable IDs and schema versions across application instances.

`CacheValueCodec` owns outer framing: ASCII `MRCV`, version byte 1, big-endian
uint16 ID byte count (1–128), ASCII `[A-Za-z0-9._-]` ID, big-endian positive int32
schema, then opaque serializer body. Default total size is 1 MiB. Header, lengths,
version, ID, schema and local expected contract are validated before invoking a
body serializer. Custom non-JSON serializers share this framing and explicit
registry; they replace only the body format. Encode/local mapping/type/size
failures propagate. `Decode(envelope, expectedContractId)` additionally verifies
a known local expected contract, and can replay null for a reference or nullable
contract. Ordinary cache always removes decoded null and misses; it never turns
ambiguous C2 null into a typed response.

Only `CacheValueDataException` means remote input rejection. Built-in JSON converts
`JsonException` around deserialization into a redacted data exception; application
converters must reserve `JsonException` for input rejection and use ordinary
exceptions for programming faults. Custom serializers explicitly opt into this
data boundary. Wrong returned CLR types and unrelated converter/configuration
errors propagate. Corrupt, unknown, schema-incompatible or null ordinary entries
increment `meridian.cache.entry_rejections` on the `Meridian.Mediator.Caching`
meter with a finite `reason` label, await removal of the same key, and miss only
if removal succeeds. No key, contract ID, payload, identity, raw exception message
or per-key suppression state appears in these default diagnostics. Get/set/remove
transport failures and cancellation propagate, including removal failures.
`IDistributedCache` cannot compare-and-delete: corrupt-read removal racing a good
writer can remove its fresh value and lose a cache hit. It never returns corrupted
data and provides no fencing guarantee.

Opt into authenticated partitioning explicitly:

```csharp
services.AddMeridianCacheScope<AuthenticatedPartition>();
// Either memory or distributed registration may precede/follow this call.
// AuthenticatedPartition : ICacheKeyScope is application supplied and scoped.
```

Authentication/authorization must complete before cache lookup. The application
must return a stable nonempty partition throughout that DI scope. Missing,
whitespace-only, invalid Unicode or changing identity fails before backend I/O;
observed change during an owned factory fails before storage, and an observed change during a read fails before returning its response. Authentication identity itself must remain immutable; a change and revert between observations cannot be universally detected. Personalized responses
require a tenant **and user** (and relevant authorization/version) partition,
or a complete user-specific logical input key. Tenant-only scope is insufficient.
Use `CacheKeyDecorator.Compose(userId, tenantId, authorizationVersion)` to build
an unambiguous partition; the library invents no authenticated identity.

`CacheKeyDecorator.Compose(key, applicationPrefix, scope)` preserves an
undecorated logical key verbatim when both dimensions are absent. Decorated keys
use `MRCKey1:` plus base64 of presence flags and big-endian lengths over strict
UTF-8 segments (application/scope/key). Decorated inputs require well-formed
Unicode: unpaired surrogates are rejected, never replacement-normalized.
Delimiters, Unicode, absent dimensions, and marker-looking raw keys cannot
collide. All get/set/remove and forwarded concurrent operations decorate once;
backend providers accept effective keys. `ScopedCacheProvider.Create` supports
explicit manual composition over a custom raw provider and preserves its actual
concurrent capability. Wrapping an already decorated provider fails clearly.

Repeated registrations expose one provider. First options win and all calls are
validated; conflicting memory/distributed backend or scope choices fail clearly.
Custom pre-registered providers remain preserved; automatic scope decoration of
unowned providers fails and requires explicit manual composition. Default memory
without decoration retains its C2 singleton identity. Prefix-only memory is a
singleton concurrent wrapper. Authenticated memory has per-DI-scope raw providers
and wrappers over the shared backend: warm values can be shared within the same
partition, but active misses cannot join across scopes. Within one scope the C2
owner/waiter cancellation, direct-await lifetime, reentry guard, cleanup and
per-caller ambiguous-null fallback remain intact. Distributed raw providers,
serializers and codecs are scoped by default, support scoped custom serializers,
and advertise no distributed coalescing/lock. Providers and wrappers borrow and
never dispose DI backends.

Changing prefix, authenticated partition, decoration version, contract schema or
request-type key names creates a cold cache namespace cutover. Let old values
expire or remove only explicitly authorized old keys. Never dual-read unscoped
private data into scoped entries or automatically clear a database. C4 owned
idempotency records require coordinated retention/reconciliation migration:
changing namespace can bypass replay protection. C4 reuses `CacheValueCodec`,
`CacheContractRegistry.GetSchemaVersion` and `CacheKeyDecorator`; it must fail
closed on data rejection, retain replay protection and never use C3 eviction as
an idempotency recovery policy. A lease already carries an effective key and
completion/release/renewal must use it verbatim; reconciliation starts from a
logical key and decorates once.

Ordinary unprovisioned tests use `--filter "Provider!=Redis"`; selecting
`Provider=Redis` requires `MERIDIAN_TEST_REDIS` and fails if absent. CI provisions
an isolated pinned Redis service and runs that required lane on all supported
frameworks. The package consumer disables JSON reflection and uses separately
configured registries and source-generated metadata; the fresh-package consumer also uses Redis when `MERIDIAN_TEST_REDIS` is explicitly supplied (set `MERIDIAN_TEST_REDIS_REQUIRED=1` to require this lane and fail on a missing endpoint) and otherwise uses `MemoryDistributedCache`. Its transport receipt is separate from the common assertion line. The Redis dependency belongs only to test projects/consumers, never the Caching/core packages.

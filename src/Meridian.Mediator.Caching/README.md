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

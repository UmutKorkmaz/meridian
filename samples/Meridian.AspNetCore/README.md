# Orders HTTP sample

A .NET 10 Minimal API using ISender/IPublisher/IStreamSender, generated order/event DTO maps, a runtime OrderProfile, asynchronous FluentValidation, positive immutable-query caching, EF8 SQLite owned transactions and leased Redis idempotency. Notifications insert a local event in the same transaction; no external messages are sent.

Supply a new disposable SQLite path, a dedicated local Redis endpoint, a unique namespace and at least 32 random bytes encoded as base64 for LocalAuth__Key. There are no default provider endpoints, signing keys or business databases. The sample fails startup when prerequisites are absent.

```sh
export Orders__DatabasePath="/absolute/disposable/orders.db"
export Orders__Redis="127.0.0.1:16379"
export Orders__Namespace="my-isolated-orders-demo"
export LocalAuth__Key="<base64 of 32 or more random bytes>"
dotnet run --project samples/Meridian.AspNetCore -c Release -- --urls http://127.0.0.1:5000
```

Local bearer tokens are a deliberately small local demo format: base64url JSON LocalIdentity, a dot, then base64url HMAC-SHA256 of those exact JSON bytes. The issuer is meridian-local and audience meridian-orders; signature, expiry, issuer and audience are checked. The owner can create a token with `LocalToken.Create(key, tenant, subject, "orders")` in local tooling; there is no HTTP token-issuance endpoint. A valid token without complete tenant/subject or permission is denied before provider access. Replace this local demonstration with your identity provider for a real application. Raw tenant/user headers never establish identity.

| Route | Behavior |
| --- | --- |
| POST /orders | Product and quantity 1–1000 plus one bounded ASCII Idempotency-Key; 201 and Location |
| GET /orders | Current scoped list, at most 100 immutable orders; uncached |
| GET /orders?orderId=guid | Positive immutable DTO lookup; scoped application cache, 404 otherwise |
| GET /orders/events?after=sequence | Scoped committed events, bounded short DB queries, one JSON object per newline |
| GET /openapi/v1.json | Generated route statuses, correlation header and NDJSON framing contract |

Authentication and authorization run before every cache lookup and idempotency replay; async validation precedes claims. Scope is an immutable injective namespace/issuer/tenant/subject/permission tuple. Database filters use the same scope. Query negatives are not cached; lists remain current without list invalidation races. Cache removal uses CacheKeyComposer<GetOrder> through the same scoped provider. Prefixes are namespacing, not backend access control; dedicated/trusted raw clients are required.

Order/event writes are saved and the owned SQLite transaction commits before Redis completion. Replays do not run handlers/notifications again. No write Retry or EF execution strategy is enabled. Redis and SQLite are not an atomic distributed transaction: post-commit Redis failure can leave an order and a retained uncertain claim. A failed POST does not establish rollback and must not trigger automatic replay. The key denotes the original immutable submission; changed valid input under the same key replays the original response, and this sample does not claim payload fingerprint validation. Pending records require explicit manual application reconciliation after quiescence and database verification; no reconciliation endpoint or external exactly-once guarantee is provided.

Every response has a server-generated X-Correlation-ID. Inbound correlation headers are ignored; preceding ambient scope is restored. Public errors use fixed codes from the optional package; raw validation/internal details never enter problems, including Development and unsupported Accept. Authentication challenges/model binding are explicitly integrated as safe status problems.

Streams authorize before enumeration, prefetch the first event before writing headers, pass RequestAborted to every DB/delay/write/MoveNext call and await disposal while the request scope lives. Readers/owned contexts close before yielding. Initial errors can be safe 500; a post-start error aborts the transport and clients must detect truncation. A disconnect gets no synthetic error body. Cleanup is cooperative and awaited, not subject to a promised hard deadline.

Integration tests require MERIDIAN_TEST_REDIS pointing at a dedicated local instance and own unique SQLite files/namespaces/keys. WebApplicationFactory tests prove composition; SocketTests launch the built ordinary sample and a separately compiled test-only launcher for fault gates/counters. Ordinary sample configuration cannot enable those probes. `eng/run-samples.ps1 -Framework net10.0 -IncludeHttp` runs the seven console samples and the bounded socket lane; HTTP is never run as an endless console sample.

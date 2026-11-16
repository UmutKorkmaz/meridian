# Optional integrations

The two core packages, `Meridian.Mapping` and `Meridian.Mediator`, depend only on `Microsoft.Extensions.*`
abstractions. FluentValidation, caching providers, Redis, Entity Framework Core and ASP.NET Core live in separate
optional packages so that an application only takes the dependencies it uses. Every runtime package below targets
net8.0, net9.0 and net10.0; `Meridian.Mediator.Generators` is a netstandard2.0 analyzer. All are **unpublished candidates**
(no stable version yet), exercised by their own packaged consumers on their own dependency closures.

| Package | Adds | Direct dependencies (minimum versions) | Required at runtime | Runnable proof |
| --- | --- | --- | --- | --- |
| `Meridian.Mediator.FluentValidation` | Async FluentValidation rules inside Meridian's validation behaviors | Meridian.Mediator, FluentValidation 12.1.1, FluentValidation.DependencyInjectionExtensions 12.1.1, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.9 | Nothing external | `tests/consumers/Meridian.FluentValidationConsumer` |
| `Meridian.Mediator.Caching` | Memory and distributed cache providers, authenticated cache scopes, opt-in miss coalescing, allowlisted value contracts | Meridian.Mediator, Microsoft.Extensions.Caching.Memory 10.0.9 | An `IDistributedCache` for the distributed provider | `tests/consumers/Meridian.CachingConsumer`, `Meridian.DistributedCachingConsumer` |
| `Meridian.Mediator.Redis` | Owned leased idempotency with reconciliation | Meridian.Mediator, Meridian.Mediator.Caching, StackExchange.Redis 2.7.27 | A Redis endpoint and an application-lifetime `IConnectionMultiplexer` | `tests/consumers/Meridian.RedisIdempotencyConsumer` |
| `Meridian.Mediator.EntityFrameworkCore` | Owned database transactions, savepoints and execution-strategy composition | Meridian.Mediator, Microsoft.EntityFrameworkCore.Relational 8.0.20 | A relational provider for your `DbContext` | `tests/consumers/Meridian.EfTransactionsConsumer` |
| `Meridian.Mediator.AspNetCore` | Redacted `ProblemDetails` mapping for Meridian exceptions | Meridian.Mediator and the ASP.NET Core shared framework | ASP.NET Core | `tests/consumers/Meridian.HttpConsumer`, `samples/Meridian.AspNetCore` |
| `Meridian.Mediator.Generators` | Analyzer-only generated mediator registries and strict dispatch | none at run time (netstandard2.0 analyzer, Roslyn 4.8 API floor) | Reference alongside Meridian.Mediator | `tests/consumers/Meridian.MediatorRegistryConsumer`, `Meridian.NativeAotConsumer` |

Each package's README in the repository (`src/<package>/README.md`, also shipped inside the package) is the detailed
contract. The C# fences in those READMEs are illustrative fragments; the consumers listed above are the compiled and
executed equivalents.

## FluentValidation

Register the mediator with one validation behavior (`AddValidationBehavior()` or `AddLocalizedValidationBehavior()`)
and call `services.AddMeridianFluentValidation(assemblies)`. The adapter awaits `ValidateAsync`, forwards the original
cancellation token and maps every failure. The consumer asserts asynchronous rule execution, token identity,
aggregated and localized errors, scope isolation, exactly-once validation with duplicate registration, and the
zero-validator case. Evidence: regression rows N04 and Q07, task C1.

## Caching

The core `CachingBehavior` composes cache keys from the query type; invalidation must name the cached query type with
`CacheKeyComposer.Compose<TQuery>(key)` (a raw key is rejected unless `UseRequestTypeNamespacedKeys=false` is set
explicitly). The caching package adds memory and distributed providers with tenant/user scope separation, a
serializer allowlist with schema versions, explicit null policy and opt-in miss coalescing with limits. The
distributed consumer can run against `MemoryDistributedCache` without Redis; that run is **not** Redis evidence.
Setting `MERIDIAN_TEST_REDIS_REQUIRED=1` makes a missing Redis endpoint fail. Evidence: N04 and Q09, tasks C2 and C3.

## Redis

`AddMeridianRedisIdempotency(registry, configure)` takes a contract registry and an optional configuration callback
(see the package README). It registers leased idempotency: a claim, renewal loop, completion and
explicit reconciliation, using Redis server time. A handler failure or an uncertain completion retains the claim; it
is never automatically re-executed. There is no exactly-once guarantee for external effects and no guarantee against
data loss on Redis failover. The provider suite requires `MERIDIAN_TEST_REDIS` and fails, never skips, when it is
missing; its crash/restart proof covers one standalone Redis 7.4 instance with `appendfsync always`. Evidence: N05,
task C4.

## Entity Framework Core

`AddMeridianEfCoreTransactions<TContext>()` makes the transaction behavior commit, roll back and dispose an
adapter-owned transaction, or join an existing transaction through a savepoint. Rollback does not reset tracked
entities or external effects. The handler is never replayed by an EF execution strategy once entered. The adapter's
mandatory floor is EF Core Relational 8.0.20; an isolated net10.0 consumer suite additionally runs EF 10.0.8 with
Npgsql 10.0.0. PostgreSQL proof requires `MERIDIAN_TEST_POSTGRES` (PostgreSQL 16.4 in the tests). Evidence: N06 and
Q12, task C5.

## ASP.NET Core

`AddMeridianProblemDetails()` plus `UseExceptionHandler()` maps Meridian exceptions to fixed, redacted problem
responses (400 `validation_failed`, 401/403, 409 idempotency, 500 otherwise) without exception messages, keys or
payloads. A missing authorization handler is a configuration failure mapped to 500. Authentication, correlation
middleware and status-code pages remain application responsibilities; the
orders sample (`samples/Meridian.AspNetCore`, net10.0) shows them together with EF transactions, FluentValidation,
caching and Redis idempotency. In the sample's OpenAPI document `GET /orders` returns 200 with a `oneOf` schema: an
`OrderDto` array without `orderId`, a single `OrderDto` with `orderId` (404 when not visible). The `POST /orders` 201
`Location` keeps the `/orders?orderId=` query form. `tests/Meridian.AspNetCore.Tests` asserts the wire shapes against
the generated schema and needs `MERIDIAN_TEST_REDIS`. Evidence: N04 and Q08, task C6 (OpenAPI correction E1).

<a id="providers"></a>
## Running provider-backed proofs locally

Provider tests and consumers read `MERIDIAN_TEST_REDIS` (`host:port`) and `MERIDIAN_TEST_POSTGRES` (a Npgsql
connection string). Point them only at disposable, test-only instances; the tests create and remove their own keys,
schemas and databases and never flush shared data. A missing variable fails the selected tests instead of skipping
them. Hosted CI provider lanes are configured but their runs on GitHub are PENDING.

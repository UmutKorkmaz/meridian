# Queryable projection providers

`ProjectTo<TDestination>` constructs deferred LINQ expressions. Provider tests execute the resulting query, inspect `ToQueryString()`, and intercept the actual database command. EF Core dependencies belong only to the test project; Meridian.Mapping has no EF Core or database-driver dependency.

## Verified version lanes

| Target runtime | EF Core / SQLite provider | Npgsql EF provider | Database |
| --- | --- | --- | --- |
| .NET 8 | 8.0.20 | 8.0.11 | SQLite and PostgreSQL 16.4 |
| .NET 9 | 8.0.20 | 8.0.11 | SQLite and PostgreSQL 16.4 |
| .NET 10 | 10.0.8 | 10.0.0 | SQLite and PostgreSQL 16.4 |

The test project explicitly pins SQLitePCLRaw.bundle_e_sqlite3 2.1.13 in all lanes, replacing the older transitive native library flagged by [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q). Native SQLite/server versions and loaded provider assembly informational versions and SHA-256 hashes are recorded in each successful query's TRX output. The compatibility claim is limited to these tested combinations and shapes, not every expression or provider version.

## Capability contract

| Shape | SQLite | PostgreSQL | Contract |
| --- | --- | --- | --- |
| Flat DTO selection | Supported | Supported | One command, only requested columns, no entity tracking or secret payload |
| Optional reference flattening | Supported | Supported | LEFT JOIN and null result for absent navigation |
| Explicit scalar expansion | Supported | Supported | Column omitted unless requested; convention or explicit MapFrom expression |
| Expression mapping and downstream predicate | Supported | Supported | Concatenation and predicate appear in SQL |
| Null substitution | Supported | Supported | SQL COALESCE |
| Immutable/record constructor DTO | Supported | Supported | Automatic parameter names and ForCtorParam expressions; no compiled delegate |
| Optional constructor defaults | Supported | Supported | Constants, including null and nonnull nullable defaults, are retained |
| Nested and collection constructor DTO | Supported | Supported | Null reference preserved; collection materialized with one relational command |
| Runtime resolvers/conditions | Rejected | Rejected | Actionable member error during projection construction, before execution |
| Runtime ConstructUsing/type converters | Rejected | Rejected | Use expression-based member/constructor configuration |

DTO constructors materialize objects on the client using SQL-selected arguments. This does not claim that arbitrary constructor code runs on the SQL server. EF's final-projection behavior can permit other client expressions; only the enumerated shapes have translation evidence. Unsupported provider expressions remain provider errors.

Without explicit constructor arguments, an available parameterless constructor retains precedence. Otherwise, the public fully resolved constructor with greatest arity is selected. Explicit ForCtorParam expressions precede same-name readable source properties; optional defaults fill remaining arguments. Equal-arity viable constructors fail as ambiguous. Missing required arguments fail with ForCtorParam guidance. Automatic constructor matching supports assignable types and nonnullable-to-nullable assignment; use explicit expressions for other conversions. A constructor-supplied record/init member is not overwritten by convention. Explicit writable member configuration retains final precedence, including members whose constructor argument used an optional default. Ignored and unrequested expansion members are also omitted from constructor inputs: optional arguments retain their defaults; required omitted arguments fail before execution. An explicit ForCtorParam does not override that omission. Requested expansion retains its explicit constructor expression. Runtime ConstructUsing delegates are never invoked for a projection.

## Reproduce

```sh
dotnet test tests/Meridian.Mapping.EntityFramework.Tests -c Release -f net8.0 --filter 'Provider=SQLite'
dotnet test tests/Meridian.Mapping.EntityFramework.Tests -c Release -f net9.0 --filter 'Provider=SQLite'
dotnet test tests/Meridian.Mapping.EntityFramework.Tests -c Release -f net10.0 --filter 'Provider=SQLite'
```

For PostgreSQL, provision an isolated test-only PostgreSQL 16.4 instance using image `postgres:16.4-alpine@sha256:5660c2cbfea50c7a9127d17dc4e48543eedd3d7a41a595a2dfa572471e37e64c`, bind an ephemeral localhost port, and set `MERIDIAN_TEST_POSTGRES` to that test database. Run the same three commands with `Provider=PostgreSQL`. Each test creates and removes only its unique `meridian_b7_<guid>` schema. Never point this variable at a business database. A missing service or variable fails the selected tests; it is never a skip. Running the project without a filter requires both providers.

The regular multi-OS PR runtime jobs include SQLite and explicitly exclude PostgreSQL. A separate non-optional PostgreSQL job provisions the pinned service for every runtime. The workflow is checked-in automation; local passes do not prove a hosted job has run or branch protection has required it.

The EF Core and Microsoft.Data.Sqlite packages use MIT; Npgsql and its EF provider use PostgreSQL; SQLitePCLRaw uses Apache-2.0 and the bundled SQLite engine is public domain. Exact restored dependency/license records belong to the candidate's B7 evidence.

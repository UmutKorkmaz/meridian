# Provider testing (local Redis and PostgreSQL)

Some suites, consumers and lanes need a real Redis or PostgreSQL server. This page explains how to run them against
**disposable backends that you own**, bound to loopback only, started from the exact image digests that CI pins, with
credentials kept in your environment and never committed.

Run the complete recipe from a clean disposable copy and retain its commit, environment, logs and cleanup receipt.
Running it on your machine gives `local-executed` evidence for that machine only. It is
not CI, release or publication evidence, and a CI provider lane stays `CI-defined-not-yet-run` until it has run on
GitHub ([proof levels](governance/required-checks.md)).

## Ground rules

- **Own the backend.** Start fresh containers for your run, label them and remove exactly those afterwards. Never
  point `MERIDIAN_TEST_REDIS` or `MERIDIAN_TEST_POSTGRES` at a shared, staging, managed or production service. The
  PostgreSQL tests create uniquely named schemas and drop them afterwards, and the Redis tests use unique key
  prefixes. The tests assume nothing else is running against the instance.
- **Loopback only.** Publish ports as `127.0.0.1::<port>` (a random host port on loopback). Never publish on `0.0.0.0`.
- **Pinned images.** Use the digests in `providers` in [`eng/required-checks.json`](../eng/required-checks.json). The
  lane collector resolves the container that serves the endpoint and requires the pinned digest in the image's
  `RepoDigests`, so a container started from a bare tag such as `redis:7` fails a lane.
- **No committed secrets.** Generate a throwaway password per run and keep it in your environment. Put no credentials
  in files inside the repository and none in your shell history. Unset the variables when you are done.
- **Failures are evidence.** A missing backend fails the selected tests and never skips them. Keep the logs and TRX
  files of a failing run, and do not rerun until green and then throw the failure away.
- **Clean up and verify.** Remove only containers carrying your owner label, then confirm none remain.

## What needs which backend

| What | Command or lane | Needs | Filter or selection |
| --- | --- | --- | --- |
| PostgreSQL projections | `tests/Meridian.Mapping.EntityFramework.Tests`, lane `provider-postgres/<tfm>` | PostgreSQL | `Provider=PostgreSQL` |
| Distributed cache and leased idempotency | `tests/Meridian.Mediator.Caching.Tests` and `tests/Meridian.Mediator.Redis.Tests`, lane `provider-redis-cache/<tfm>` | Redis, plus a **local Docker CLI** for the hard-restart test | `Provider=Redis` |
| EF transactions | `tests/Meridian.Mediator.EntityFrameworkCore.Tests`, lane `ef-transactions/<tfm>` | PostgreSQL and Redis | unfiltered |
| EF10 compatibility | `eng/test-ef10-compatibility.ps1`, lane `ef10-compatibility/net10.0` | PostgreSQL, Redis, a packed feed and exactly SDK 10.0.300 | isolated copy of the EF tests |
| Orders HTTP API | `tests/Meridian.AspNetCore.Tests` (`net10.0` only) plus `eng/run-samples.ps1 -IncludeHttp`, lane `http-integration/net10.0` | Redis | unfiltered |
| Package consumers | `eng/verify-packages.ps1` | `Meridian.RedisIdempotencyConsumer`: Redis. `Meridian.EfTransactionsConsumer`: PostgreSQL. `Meridian.DistributedCachingConsumer`: Redis when `MERIDIAN_TEST_REDIS_REQUIRED=1` | A partial lane requires the policy's exact selection and collected candidate/compiler evidence; all-consumer lanes require both backends |
| Coverage and current servicing | lanes `coverage/net10.0`, `current-servicing/<tfm>` | PostgreSQL and Redis | every runtime suite unfiltered |

The test minimums for each lane are in `countProfiles` in `eng/required-checks.json`. The backend-free selection
`Provider!=PostgreSQL&Provider!=Redis` is described in [CONTRIBUTING](../CONTRIBUTING.md).

| Variable | Value | Read by |
| --- | --- | --- |
| `MERIDIAN_TEST_REDIS` | A StackExchange.Redis configuration string, for example `127.0.0.1:<port>,abortConnect=true,connectTimeout=5000` | Redis tests, HTTP tests, Redis consumers, run-samples `-IncludeHttp`, EF10 script |
| `MERIDIAN_TEST_POSTGRES` | An Npgsql connection string; it contains the password | PostgreSQL tests, `Meridian.EfTransactionsConsumer`, EF10 script |
| `MERIDIAN_TEST_REDIS_REQUIRED` | `1` makes `Meridian.DistributedCachingConsumer` fail without Redis, as the Redis consumer lanes set it | That consumer |
| `MERIDIAN_TEST_DOCKER_HOST` | Optional. The host name the Redis restart test uses to reach the port it publishes (default `127.0.0.1`) | `RedisRestartTests` |

## Start disposable backends (bash, macOS or Linux)

Paste this into an interactive shell. It sets no `set -e`, so a failing step does not close your terminal. Check that
each step succeeded before you go on.

```bash
owner="meridian-local-$(date -u +%Y%m%dt%H%M%Sz)-$$"
redis_image='redis:7.4-alpine@sha256:6ab0b6e7381779332f97b8ca76193e45b0756f38d4c0dcda72dbb3c32061ab99'
postgres_image='postgres:16.4-alpine@sha256:5660c2cbfea50c7a9127d17dc4e48543eedd3d7a41a595a2dfa572471e37e64c'

docker pull "$redis_image"
docker pull "$postgres_image"
docker image inspect --format '{{json .RepoDigests}}' "$redis_image"      # must list the pinned digest
docker image inspect --format '{{json .RepoDigests}}' "$postgres_image"

# Redis: no persistence, data on tmpfs, loopback-only random port.
docker run -d --name "$owner-redis" --label "meridian.owner=$owner" \
  -p 127.0.0.1::6379 --tmpfs /data \
  "$redis_image" redis-server --save '' --appendonly no

# PostgreSQL: throwaway password from the environment (-e NAME without a value copies it), data on tmpfs.
export POSTGRES_PASSWORD="$(openssl rand -hex 24)"
docker run -d --name "$owner-postgres" --label "meridian.owner=$owner" \
  -e POSTGRES_USER=meridian_test -e POSTGRES_DB=meridian_test -e POSTGRES_PASSWORD \
  -p 127.0.0.1::5432 --tmpfs /var/lib/postgresql/data \
  "$postgres_image"

# Readiness. pg_isready over TCP becomes ready only after initialisation has finished.
for _ in $(seq 1 60); do
  docker exec "$owner-redis" redis-cli ping 2>/dev/null | grep -qx PONG && break
  sleep 1
done
for _ in $(seq 1 60); do
  docker exec "$owner-postgres" pg_isready -h 127.0.0.1 -p 5432 -U meridian_test -d meridian_test >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$owner-redis" redis-cli ping
docker exec "$owner-postgres" pg_isready -h 127.0.0.1 -p 5432 -U meridian_test -d meridian_test

redis_port="$(docker port "$owner-redis" 6379/tcp | head -n 1 | awk -F: '{print $NF}')"
postgres_port="$(docker port "$owner-postgres" 5432/tcp | head -n 1 | awk -F: '{print $NF}')"
export MERIDIAN_TEST_REDIS="127.0.0.1:${redis_port},abortConnect=true,connectTimeout=5000"
export MERIDIAN_TEST_POSTGRES="Host=127.0.0.1;Port=${postgres_port};Database=meridian_test;Username=meridian_test;Password=${POSTGRES_PASSWORD}"
echo "owner=$owner redis=127.0.0.1:${redis_port} postgres=127.0.0.1:${postgres_port}"   # no secret printed
```

The password still shows up in `docker inspect` output for that container. That is acceptable only because the
container is disposable, bound to loopback and removed at the end. Keep `owner` in the same shell, because the cleanup
step needs it.

## Start disposable backends (PowerShell 7, any OS)

```powershell
$owner = "meridian-local-$([DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'").ToLowerInvariant())-$PID"
$redisImage = 'redis:7.4-alpine@sha256:6ab0b6e7381779332f97b8ca76193e45b0756f38d4c0dcda72dbb3c32061ab99'
$postgresImage = 'postgres:16.4-alpine@sha256:5660c2cbfea50c7a9127d17dc4e48543eedd3d7a41a595a2dfa572471e37e64c'

docker pull $redisImage; docker pull $postgresImage
docker image inspect --format '{{json .RepoDigests}}' $redisImage
docker image inspect --format '{{json .RepoDigests}}' $postgresImage

docker run -d --name "$owner-redis" --label "meridian.owner=$owner" -p 127.0.0.1::6379 --tmpfs /data `
    $redisImage redis-server --save '' --appendonly no
$env:POSTGRES_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
docker run -d --name "$owner-postgres" --label "meridian.owner=$owner" `
    -e POSTGRES_USER=meridian_test -e POSTGRES_DB=meridian_test -e POSTGRES_PASSWORD `
    -p 127.0.0.1::5432 --tmpfs /var/lib/postgresql/data $postgresImage

foreach ($attempt in 1..60) { if ((docker exec "$owner-redis" redis-cli ping 2>$null) -eq 'PONG') { break }; Start-Sleep -Seconds 1 }
foreach ($attempt in 1..60) {
    docker exec "$owner-postgres" pg_isready -h 127.0.0.1 -p 5432 -U meridian_test -d meridian_test *> $null
    if ($LASTEXITCODE -eq 0) { break }; Start-Sleep -Seconds 1
}
$redisPort = ((docker port "$owner-redis" 6379/tcp) | Select-Object -First 1).Split(':')[-1]
$postgresPort = ((docker port "$owner-postgres" 5432/tcp) | Select-Object -First 1).Split(':')[-1]
$env:MERIDIAN_TEST_REDIS = "127.0.0.1:$redisPort,abortConnect=true,connectTimeout=5000"
$env:MERIDIAN_TEST_POSTGRES = "Host=127.0.0.1;Port=$postgresPort;Database=meridian_test;Username=meridian_test;Password=$($env:POSTGRES_PASSWORD)"
"owner=$owner redis=127.0.0.1:$redisPort postgres=127.0.0.1:$postgresPort"
```

## Run the provider suites

After a Release build (`dotnet build Meridian.sln -c Release`), use the same filters as the lanes. The example uses
`net10.0`; repeat for `net8.0` and `net9.0`, except `Meridian.AspNetCore.Tests`, which targets `net10.0` only.

```bash
dotnet test tests/Meridian.Mapping.EntityFramework.Tests/Meridian.Mapping.EntityFramework.Tests.csproj -c Release -f net10.0 --no-build --filter 'Provider=PostgreSQL'
dotnet test tests/Meridian.Mediator.Caching.Tests/Meridian.Mediator.Caching.Tests.csproj -c Release -f net10.0 --no-build --filter 'Provider=Redis'
dotnet test tests/Meridian.Mediator.Redis.Tests/Meridian.Mediator.Redis.Tests.csproj -c Release -f net10.0 --no-build --filter 'Provider=Redis'
dotnet test tests/Meridian.Mediator.EntityFrameworkCore.Tests/Meridian.Mediator.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Meridian.AspNetCore.Tests/Meridian.AspNetCore.Tests.csproj -c Release --no-build
pwsh eng/run-samples.ps1 -Framework net10.0 -IncludeHttp -NoBuild
```

- `Meridian.Mediator.Redis.Tests` contains a hard-restart test. It runs `docker` itself, creates its own container and
  volume from the pinned Redis image (labelled `meridian.task=C4-aof`, port published on `127.0.0.1`), kills and
  restarts that container, and then removes both and checks they are gone. It needs a Docker CLI that talks to a
  **local** daemon. `MERIDIAN_TEST_REDIS` is used by the other tests in that suite.
- The crash tests start `tests/Meridian.Mediator.Redis.CrashFixture` from its build output, so build the solution first.
- With a packed feed (see [CONTRIBUTING](../CONTRIBUTING.md#package-documentation-and-candidate-checks)), the EF10 check
  and the all-consumer package run are:

  ```bash
  pwsh eng/test-ef10-compatibility.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1
  MERIDIAN_TEST_REDIS_REQUIRED=1 pwsh eng/verify-packages.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1
  ```

- As lane evidence, run the gate for one lane from a clean, committed tree and a feed packed from that same commit:

  ```bash
  pwsh eng/verify-candidate.ps1 -Mode Run -Feed artifacts/candidate -EvidenceDirectory artifacts/evidence/provider-local \
    -CandidateVersion 2.1.4-local.1 -Lane provider-postgres/net10.0
  ```

  This ends in `SCOPED-PASS` at best, which is a local, single-lane verdict and not a candidate verdict.

## Clean up and verify

```bash
for id in $(docker ps -aq --filter "label=meridian.owner=$owner"); do docker rm -f -v "$id"; done
docker ps -a --filter "label=meridian.owner=$owner" --format '{{.ID}} {{.Names}}'     # must print nothing
unset MERIDIAN_TEST_REDIS MERIDIAN_TEST_POSTGRES POSTGRES_PASSWORD
```

```powershell
docker ps -aq --filter "label=meridian.owner=$owner" | ForEach-Object { docker rm -f -v $_ }
docker ps -a --filter "label=meridian.owner=$owner" --format '{{.ID}} {{.Names}}'     # must print nothing
Remove-Item Env:MERIDIAN_TEST_REDIS, Env:MERIDIAN_TEST_POSTGRES, Env:POSTGRES_PASSWORD -ErrorAction SilentlyContinue
```

If a Redis restart test run was interrupted, it may have left its own container or volume behind. List them with
`docker ps -a --filter label=meridian.task=C4-aof` and `docker volume ls --filter label=meridian.task=C4-aof`, and
remove only the ones you can tie to your interrupted run. Somebody else's run on the same daemon uses the same label.

## Known limits

- The pinned images were pulled and run on an arm64 (Apple Silicon) Docker daemon in earlier local runs. On x64 they
  are CI-defined service containers that have not run yet.
- Only Redis 7.4 and PostgreSQL 16.4 at these digests are covered. No other version, managed service or provider is
  claimed ([provider support table](governance/required-checks.md#providers)).
- A provider suite proves that the service answered. The digest check rests on the local Docker daemon's
  `docker inspect` output.

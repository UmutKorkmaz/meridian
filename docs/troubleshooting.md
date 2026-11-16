# Troubleshooting

This page lists the failures contributors run into most often, the exact message where one exists, and how to
reproduce or fix each one. The messages are copied from the current scripts and tests. Setup is described in
[CONTRIBUTING](../CONTRIBUTING.md) and backends in [provider testing](provider-testing.md).

## Record the environment first

Most "works on my machine" differences come from SDK or runtime selection. Put this in every bug report:

```bash
git rev-parse HEAD
git status --porcelain          # empty when the tree is clean
dotnet --version                # the SDK that global.json selects here
dotnet --list-sdks
dotnet --list-runtimes
pwsh --version
python3 --version
```

Leave out connection strings, passwords and tokens. `MERIDIAN_TEST_POSTGRES` contains a password.

## SDK and runtime selection

| Symptom | Cause | Fix |
| --- | --- | --- |
| `dotnet --version` prints 10.0.4xx while CI uses 10.0.300 | `global.json` names `10.0.100` with `rollForward: latestFeature`, which selects the highest installed 10.0 feature band | Fine for development. To reproduce a pinned lane exactly, use a private dotnet root that holds only the pinned SDKs ([CONTRIBUTING](../CONTRIBUTING.md#installing-sdks-and-runtimes)). The gate checks the selected SDK from the lane's `lane-toolchain.json` and fails a pinned lane built with anything other than 10.0.300. |
| A `net8.0` or `net9.0` test run fails with a .NET host message that the framework `Microsoft.NETCore.App` was not found | The runtime family is not installed in the dotnet root you are using | Install that runtime family. `DOTNET_ROLL_FORWARD=LatestPatch` only rolls forward within a family. |
| A runtime lane fails with `the lane toolchain offers 8.x runtimes [...]; exactly 8.0.27 must be the only one` | The dotnet root has several patches of the family | Use a private root that has exactly the pinned patch (pinned lanes) or exactly the current patch (servicing lanes). |
| `verify-packages.ps1`: `Compiler SDK/framework pair is not approved by pinnedCompatibility` | `-Framework`/`-SdkVersion` is not one of the `compilerHosts` pairs in `eng/packages.json` | Pass a manifest pair (`net8.0`/`8.0.421`, `net9.0`/`9.0.318`, `net10.0`/`10.0.300`), or leave both out to run all of them. |
| `verify-packages.ps1`: `Wrong actual SDK: <version>` | The exact SDK is not installed in the dotnet root, so the generated `global.json` (`rollForward: disable`) could not select it | Install that exact SDK. |
| `run-aot-smoke.ps1`: `Framework and SDK must be one pinned manifest compiler host pair.` / `Framework and SDK major version must match.` | Same as above, for Native AOT | Use a manifest pair. See [Native AOT](native-aot.md#reproduce). |
| `verify-candidate.ps1`: `Lane current-servicing/... needs the isolated toolchain root named by $env:MERIDIAN_SERVICING_DOTNET_ROOT.` or `Isolated toolchain root ... has no dotnet host.` | The six `current servicing` lanes run with a separate dotnet root | Install SDKs 8.0.428, 9.0.321 and 10.0.404 into their own root, point `MERIDIAN_SERVICING_DOTNET_ROOT` at it and check that `--list-runtimes` shows exactly one patch per family. Otherwise leave those lanes out with `-Lane`; the result is then scoped. |
| The preview job fails | `preview samples (net11.0, optional)` is optional and `continue-on-error` | Nothing blocks on it. Locally, net11 samples are built only with `MeridianIncludeNet11Samples=true` and a preview SDK. |

## Tests fail because a backend is missing

Provider tests never skip. Each one fails with a message that names the missing variable:

| Message (exact text) | Where |
| --- | --- |
| `PostgreSQL is required. Set MERIDIAN_TEST_POSTGRES to an isolated test service; no skip or business database fallback is allowed.` | `Meridian.Mapping.EntityFramework.Tests` (`Provider=PostgreSQL`) |
| `MERIDIAN_TEST_POSTGRES is required for selected PostgreSQL tests.` | `Meridian.Mediator.EntityFrameworkCore.Tests` (`Provider=PostgreSQL`) |
| `Required isolated Redis endpoint MERIDIAN_TEST_REDIS is missing; provider support gate cannot skip.` | `Meridian.Mediator.Redis.Tests` (`Provider=Redis`) |
| `Required Redis provider lane is selected: MERIDIAN_TEST_REDIS must point to its provisioned isolated backend.` | `Meridian.Mediator.Caching.Tests` (`Provider=Redis`) |
| `MERIDIAN_TEST_REDIS required.` | `Meridian.Mediator.EntityFrameworkCore.Tests` (`Provider=Redis`) |
| `A dedicated C6 Redis endpoint is required.` / `Dedicated Redis required.` | `Meridian.AspNetCore.Tests` (`net10.0`, the HTTP lane) |
| `Required MERIDIAN_TEST_POSTGRES is missing.` | `Meridian.EfTransactionsConsumer`, run by `verify-packages.ps1` |
| `Required Redis idempotency consumer endpoint MERIDIAN_TEST_REDIS is missing.` | `Meridian.RedisIdempotencyConsumer`, run by `verify-packages.ps1` |
| `Selected Redis package consumer requires its provisioned endpoint.` | `Meridian.DistributedCachingConsumer` when `MERIDIAN_TEST_REDIS_REQUIRED=1` |
| `HTTP smoke requires an explicitly isolated MERIDIAN_TEST_REDIS.` | `eng/run-samples.ps1 -IncludeHttp` |
| `Both owned MERIDIAN_TEST_POSTGRES and MERIDIAN_TEST_REDIS providers are required.` | `eng/test-ef10-compatibility.ps1` |

Either run the backend-free selection (`--filter 'Provider!=PostgreSQL&Provider!=Redis'`, see
[CONTRIBUTING](../CONTRIBUTING.md#build-and-run-the-tests-that-need-no-backend)) or start disposable backends as
described in [provider testing](provider-testing.md). Never point these variables at a shared, staging or production
service.

`Meridian.Mediator.Redis.Tests` also contains a hard-restart test that runs `docker` itself. If no Docker daemon is
reachable it fails in its first `docker volume create`.

## A sample fails

`eng/run-samples.ps1` runs the samples in order and **stops at the first failure**, printing
`FAIL <sample>; passed=<n>/<total>; log=<path>`. The exit code is the sample's own exit code; `124` means it hit the
timeout (`-TimeoutSeconds`, 300 by default). Logs go to `-LogDirectory`, which defaults to
`artifacts/sample-results/<framework>/<Sample>.log`. `samples-summary.json` lists every expected sample, and any that
never ran keep `"exit": null`.

Reproduce one sample directly after a Release build:

```bash
dotnet build Meridian.sln -c Release
dotnet run --project samples/Meridian.Showcase/Meridian.Showcase.csproj -c Release -f net8.0 --no-build
```

- QuickStart targets `net10.0` only. On `net8.0`/`net9.0` the runner prints `SKIP QuickStart: targets net10.0 only`,
  which is expected.
- `-NoBuild` expects an existing Release build. Without it, `dotnet run` builds first, which takes longer and can hit
  the timeout.
- `-IncludeHttp` (`net10.0` only) also runs the `SocketTests` of `tests/Meridian.AspNetCore.Tests` through an owned
  supervisor and needs `MERIDIAN_TEST_REDIS`. Its row in `samples-summary.json` records `exit`, `timeout`,
  `exitObserved`, `drainsObserved` and `ownershipValidated`, and the full output is in `http.log`. Keep that log when
  you report a failure; do not rerun until it passes and then discard the failure.

## A generator diagnostic appears

The two Meridian source generators define the diagnostics below (`src/Meridian.Mapping.Generators/Diagnostics/MapperDiagnostics.cs`
and `src/Meridian.Mediator.Generators/Diagnostics/MediatorDiagnostics.cs`). This is the complete list of IDs those two
generators define today. It is not a list of every compiler, analyzer or runtime message a Meridian application can
produce. Packaged consumers compile with **C# 12 and warnings as errors**, so a Warning fails such a build as well.

### Mapping generator (`[GenerateMapper]`, shipped inside `Meridian.Mapping`)

| ID | Severity | Meaning | What to do |
| --- | --- | --- | --- |
| `MERIDIANGEN001` | Warning | A mapper container is nested inside a containing type that is not `partial`. No code is generated for that container, and its other diagnostics are held back until this is fixed. | Mark every containing type `partial` or move the container to namespace scope. |
| `MERIDIANGEN002` | Info in Legacy mode, **Error** in Structural mode | A destination member has no eligible source member, or the types differ. Legacy skips the member. | Add or rename the source member, map it explicitly (`[MapMember]`, `[MapWith]`, `[ConvertMapMember]`) or ignore it (`[IgnoreMapMember]`). Legacy behavior does not change unless you opt in to Structural. |
| `MERIDIANGEN003` | Error | The destination cannot be built with the supported constructor contract, for example an unsatisfied required member. Only that method is omitted; other pairs in the container are kept. | Use a concrete destination with a supported constructor and satisfy its required members. Structural mode maps public constructor parameters; runtime-only construction stays with the runtime `IMapper`. |
| `MERIDIANGEN004` | Error | Method collision: a duplicate source/destination pair, a `MethodName` used by another mapping or by an existing member, or a reserved generated-helper or `GeneratedResolver` name. | Choose a unique `MethodName`, remove the duplicate pair or rename the conflicting member. |
| `MERIDIANGEN005` | Error | An unsupported structural shape or capability: constructor parameters or members that need unsupported nullable, collection, enum or nested mapping support, a required member that is not a public writable property, a null-skip on an init-only or required reference property, a reference-preserved cycle through a constructor/init-only/required member, or a member that depends on an unsupported declared nested pair. | Simplify the shape, declare the nested pair it needs, fix the nested pair's own diagnostic first, or use the runtime `IMapper` for that pair. |
| `MERIDIANGEN006` | Error | Invalid configuration: mode not Legacy or Structural, limits not positive, `MethodName` not a valid identifier, source/destination types not accessible, closed and nameable, no constructor satisfying a configured constructor-only member, conflicting member configuration or an invalid static converter. | Fix the attribute arguments named in the message. |

All pair diagnostics point at the original `[GenerateMapper]` attribute. The full contract is in
[generated-mapping-contract.md](generated-mapping-contract.md) and [generated-mapping.md](generated-mapping.md).

### Mediator generator (`Meridian.Mediator.Generators`, analyzer only)

| ID | Severity | Meaning | What to do |
| --- | --- | --- | --- |
| `MERIDIANMED001` | Error | A request listed in `ExpectedRequestTypes` has no matching request handler in the same declaration's handler types. Requests that are not listed are not checked. | Add the closed handler to the registry or remove the expectation. |
| `MERIDIANMED002` | Error | Several handlers in one registry handle the same request or stream request. Several notification handlers are allowed. | Keep exactly one handler per request or stream request. |
| `MERIDIANMED003` | Error | Invalid declared registration. The registry must be a top-level, non-generic, non-abstract `partial` class without declared constructors. Handler and option arrays must be non-null and contain resolved types. Expected requests must be closed concrete request types. A handler must implement a supported request, notification or stream contract and have one accessible constructor (or exactly one `ActivatorUtilitiesConstructor`). A contribution must implement its declared contract. | Fix the declaration as the message says. |
| `MERIDIANMED004` | (reserved) | Not emitted. The ID is reserved for visible declared lifetime evidence; startup lifetimes are never inferred. | None. |
| `MERIDIANMED005` | Error | Unsupported generated construction: an open handler (give a closed type), a request whose response contracts leave no unique dispatch slot, an open contribution other than a pipeline behavior, an open behavior whose two type parameters do not map directly to request and response, an unsatisfied constraint, an exception driver with no declared exception handlers or actions, or an unsupported contribution option. | Declare explicit closed types, or drop the unsupported option. |

These are compile-time diagnostics. At run time, a Native AOT binary has no reflective fallback. An undeclared request,
notification or stream fails with `No generated request dispatch for <type>. ...`, and assembly scanning throws
`NotSupportedException` that points at the generated registry. See
[Native AOT](native-aot.md#unsupported-on-native-and-their-failures) and `src/Meridian.Mediator.Generators/README.md`.

### Reproducing a generator problem

1. See the generated source: add `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` to the consuming
   project, as `tests/consumers/Meridian.GeneratedConsumer` does, and rebuild. The files appear under `obj/`.
2. Write a failing unit test with the existing harnesses. Mapping: `tests/Meridian.Mapping.Tests`
   (`GeneratorTestHarness.cs`, `GeneratorDiagnosticTests.cs`). Mediator: `tests/Meridian.Mediator.Generators.Tests`
   (`RegistryGenerationTests.cs`). Run a subset, for example:

   ```bash
   dotnet test tests/Meridian.Mapping.Tests/Meridian.Mapping.Tests.csproj -c Release -f net10.0 \
     --filter 'FullyQualifiedName~GeneratorDiagnosticTests'
   dotnet test tests/Meridian.Mediator.Generators.Tests/Meridian.Mediator.Generators.Tests.csproj -c Release -f net10.0
   ```

3. Reproduce under one exact compiler host from a packed candidate (C# 12, warnings as errors, Roslyn of that SDK):

   ```powershell
   ./eng/pack-packages.ps1 -CandidateVersion 2.1.4-local.1 -OutputDirectory artifacts/candidate
   ./eng/verify-packages.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1 -Framework net8.0 -SdkVersion 8.0.421
   ./eng/verify-packages.ps1 -Feed artifacts/candidate -CandidateVersion 2.1.4-local.1 -PlanOnly   # plan only, no build
   ```

   The Roslyn 4.8 dependency of the generators is a compile-time API floor. It does not prove that SDK 8.0.100 runs
   them; the oldest native compiler host that has actually been tested is SDK 8.0.421.

## Packing and candidate scripts

| Message | Fix |
| --- | --- |
| `CandidateVersion must be canonical three-part SemVer without aliases or build metadata.` | Use `MAJOR.MINOR.PATCH[-prerelease]` with no `+metadata`, no fourth component and no leading zeros. |
| `Numeric prerelease identifiers must not have leading zeroes.` | For example `local.1`, not `local.01`. |
| `Stable CandidateVersion requires explicit -ReleasePreparation; this does not authorize publication.` | Local probes use prerelease versions. `-ReleasePreparation` is for a deliberate stable preparation only. |
| `ReleasePreparation requires a stable identity; ordinary probes must remain prereleases.` | Drop `-ReleasePreparation` for prerelease versions. |
| `Candidate version already exists in output feed; choose a fresh version` | Pack a new prerelease version. Never overwrite or rename an existing candidate. |
| `FAIL: the working tree has uncommitted changes; evidence must name the exact source it came from ...` | Commit on your branch or verify from a separate clone. Do not stash or reset somebody else's work. `-AllowDirtySource` is for tooling self-tests only, and evidence collected with it fails the gate. Untracked `__pycache__` directories count, so use `python3 -B`. |
| `Feed must hold exactly one package-manifest.<version>.json (found N); pass -CandidateVersion.` | Pass `-CandidateVersion`, or use one feed directory per candidate. |
| `Python 3.9 or newer is required (python3 or python on PATH).` | Install Python 3.9 or newer. |

`verify-candidate.ps1` exit codes: `0` PASS or SCOPED-PASS (read `releaseEligible` in `summary.json`), `1` FAIL, `2`
invalid policy or arguments, `3` PENDING (only with `-AllowPending`). A lane without evidence is never reported as
passed. See [required checks](governance/required-checks.md#how-the-gate-decides).

## Tooling tests (`eng/tests`)

| Symptom | Fix |
| --- | --- |
| `ModuleNotFoundError: No module named 'yaml'` in `test_ci_workflows.py` | Install PyYAML 6.0.3, preferably in a virtual environment outside the repository. |
| `NameError: name 'ExceptionGroup' is not defined` in `test_http_runner_timeout.py` | That POSIX fixture needs Python 3.11 or newer; the suite has been run with 3.12. The production helpers still target 3.9. |
| The run takes a long time | The full discovery builds and packs fixture projects and takes about 25 minutes. Run one module with `-p '<file>'`. |

## Documentation build

`pwsh eng/verify-docs.ps1 -Configuration Release -Feed <feed>` restores a pinned private DocFX tool from nuget.org,
builds the site under `artifacts/docs/` and checks links, snippets and compiler warnings. Nothing is published. It runs
`Meridian.DocumentationConsumer` on every manifest compiler host, so it also needs the three exact SDKs. Only the
pages listed in `docs/docfx.json` go into the site. Repository Markdown links in `README.md`, `docs/README.md`, the
package READMEs and the sample READMEs are checked by `eng/check-docs.py mdlinks`.

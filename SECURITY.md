# Security Policy

## Supported Versions

Security patches are provided for the most recent minor release of each
major version. `2.0.x` was published one day before `2.1.0` and lacks the
Phase 1 safety-defaults infrastructure (`DefaultMaxDepth`,
`DefaultMaxCollectionItems`, `MeridianMappingLimitException`) required to
carry the HIGH-severity fixes from `GHSA-f5v8-v6q3-q4h6`. Effective
2026-04-16, `2.0.x` is deprecated in favor of `2.1.x`.

| Version   | Supported |
| --------- | --------- |
| 2.1.x (prefer latest, currently 2.1.3) | ✅ |
| 2.0.x     | ❌ (deprecated 2026-04-16 — upgrade to 2.1.1+) |
| < 2.0     | ❌        |

The optional `Meridian.Mediator.*` packages (FluentValidation, Caching, Redis,
EntityFrameworkCore, AspNetCore, Generators) and the newer features of the
core packages exist only as an unpublished candidate. Reports against that
code are welcome, but it has no supported release yet
([capability ledger](./docs/capabilities.md)).

### Target frameworks and .NET runtime support

Supported 2.1.x releases target `net8.0`, `net9.0` and `net10.0`. A target
framework is never removed in a minor release; retiring one is a separately
reviewed major-version decision
([compatibility policy](./docs/compatibility-policy.md)).

Microsoft support for .NET 8 and .NET 9 ends on 2026-11-10. After that date
Microsoft no longer ships security fixes for those runtimes. Meridian keeps
targeting them in 2.x, but a vulnerability in the .NET runtime itself can only
be fixed by running on a Microsoft-supported .NET version, so please plan to do
that. Microsoft supports only the latest patch of each family
([support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)).

## Reporting a Vulnerability

**Do not open a public GitHub issue, pull request or discussion for security
vulnerabilities.**

Use GitHub's private vulnerability reporting:

1. Go to <https://github.com/UmutKorkmaz/meridian/security/advisories/new>
2. Fill out the advisory template with reproduction steps, affected versions,
   and impact assessment
3. Submit — we aim to acknowledge within 48 hours (see the response targets below)

If you cannot use GitHub's reporting flow, email **security@meridian-dotnet.dev**.
No encryption key is published yet, so do not put exploit details in plain
email: send a short note asking for a private channel and we will set one up.

Please do not include real credentials, connection strings or customer data in
a report. The repository's own tests take backend endpoints from environment
variables and need only disposable test instances
([provider testing](./docs/provider-testing.md)).

## Response SLA

| Phase                | Target        |
| -------------------- | ------------- |
| Initial ack          | ≤ 48 hours    |
| Triage + severity    | ≤ 7 days      |
| Fix or mitigation    | ≤ 30 days     |
| Coordinated release  | ≤ 90 days     |

The clock starts when we acknowledge your report. Embargoes may be extended
by mutual agreement for complex issues.

Meridian currently has **one maintainer** and no backup responder. The targets
above are commitments we work toward, not guarantees. If one is going to be
missed, we will tell you rather than go quiet. The maintainer's handling steps
are in the [maintainer checklist](./docs/maintainer-checklist.md#security-response).

## Scope

### In scope

- Denial-of-service vulnerabilities reachable through public APIs
  (recursion, collection width, unbounded allocation)
- Type-confusion or reflection-injection exploits through configured
  converters, resolvers, or type maps
- Supply-chain integrity issues (e.g., package tampering, provenance
  attestation failures)
- Information disclosure through exception messages or telemetry

### Out of scope

- Vulnerabilities in applications consuming Meridian that arise from user
  misconfiguration (e.g., accepting unbounded JSON before mapping)
- Performance regressions without a security impact
- Issues in dependencies — report those upstream; we will track them

## Safe Harbor

Good-faith security research conducted under this policy is authorized. We
will not pursue legal action against researchers who:

- Follow the reporting process above
- Do not exfiltrate or retain data beyond what is necessary to demonstrate
  the issue
- Give us reasonable time to remediate before public disclosure

## Hall of Fame

Reporters of accepted vulnerabilities are credited in `CHANGELOG.md` and the
published GHSA unless they request anonymity.

## Related docs

- [docs/security-model.md](./docs/security-model.md) — threat model and mitigations
- [docs/compatibility-policy.md](./docs/compatibility-policy.md) — SemVer and default-behavior guarantees
- [docs/maintainer-checklist.md](./docs/maintainer-checklist.md) — security response and release duties
- [NOTICE](./NOTICE) — third-party dependency licenses and build-time tooling

## Historical Advisories

- [`GHSA-f5v8-v6q3-q4h6`](https://github.com/UmutKorkmaz/meridian/security/advisories/GHSA-f5v8-v6q3-q4h6)
  (CVE pending): coordinated security release for `Meridian.Mapping` and
  `Meridian.Mediator` covering two HIGH-severity DoS findings plus four
  MEDIUM and three LOW defense-in-depth gaps. Patched in `v2.1.1`
  (published 2026-04-16). No `2.0.x` backport — see the supported-versions
  table above for rationale; `2.0.x` consumers must upgrade to `2.1.1+`.

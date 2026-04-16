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
| 2.1.x     | ✅        |
| 2.0.x     | ❌ (deprecated 2026-04-16 — upgrade to 2.1.1+) |
| < 2.0     | ❌        |

## Reporting a Vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Use GitHub's private vulnerability reporting:

1. Go to <https://github.com/UmutKorkmaz/meridian/security/advisories/new>
2. Fill out the advisory template with reproduction steps, affected versions,
   and impact assessment
3. Submit — you will receive an acknowledgement within 48 hours

If you cannot use GitHub's reporting flow, email **security@meridian-dotnet.dev**
with a GPG-encrypted message (key fingerprint published in this file once
available).

## Response SLA

| Phase                | Target        |
| -------------------- | ------------- |
| Initial ack          | ≤ 48 hours    |
| Triage + severity    | ≤ 7 days      |
| Fix or mitigation    | ≤ 30 days     |
| Coordinated release  | ≤ 90 days     |

The clock starts when we acknowledge your report. Embargoes may be extended
by mutual agreement for complex issues.

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

## Historical Advisories

- [`GHSA-f5v8-v6q3-q4h6`](https://github.com/UmutKorkmaz/meridian/security/advisories/GHSA-f5v8-v6q3-q4h6)
  (CVE pending): coordinated security release for `Meridian.Mapping` and
  `Meridian.Mediator` covering two HIGH-severity DoS findings plus four
  MEDIUM and three LOW defense-in-depth gaps. Patched in `v2.1.1`
  (published 2026-04-16). No `2.0.x` backport — see the supported-versions
  table above for rationale; `2.0.x` consumers must upgrade to `2.1.1+`.

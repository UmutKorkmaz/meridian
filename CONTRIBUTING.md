# Contributing to Meridian

Thank you for considering a contribution. This document covers the workflow,
quality gates, and conventions we expect on pull requests.

## Quick Start

```bash
git clone https://github.com/UmutKorkmaz/meridian.git
cd meridian
dotnet restore Meridian.sln
dotnet build Meridian.sln -c Release
dotnet test tests/Meridian.Mapping.Tests  -c Release
dotnet test tests/Meridian.Mediator.Tests -c Release
```

Required tools:
- .NET SDK matching `global.json` (or a compatible install the SDK can roll forward to)
- Git 2.40+

Samples live under `samples/` (including Hexagonal, QuickStart, Showcase, and architecture demos).

## How to Propose a Change

1. **Open an issue first** for any non-trivial change. Discuss scope before
   writing code. For typos, small docs fixes, or obvious bug fixes, a direct
   PR is fine.
2. **Fork** and create a topic branch off `main`:
   `git checkout -b feat/short-description`
3. **Commit** using [Conventional Commits](https://www.conventionalcommits.org/):
   - `feat:` new capability
   - `fix:` bug fix
   - `perf:` performance improvement without behavior change
   - `refactor:` internal change, no behavior change
   - `docs:` documentation only
   - `test:` tests only
   - `chore:` build, tooling, dependencies
   - Breaking changes: append `!` after the type, e.g. `feat!:` and include a
     `BREAKING CHANGE:` footer.
4. **Open a PR** against `main`. Fill in the PR template. Link the issue.

## Compatibility

Do **not** change public APIs or default runtime behavior without a major
version and explicit CHANGELOG notes. See
[docs/compatibility-policy.md](./docs/compatibility-policy.md).

## Quality Gates (enforced by CI)

A PR is mergeable when **all** of these pass:

- `dotnet build` + `dotnet test` succeed on the CI matrix
  (`build + test (ubuntu-latest|windows-latest|macos-latest)` — see `.github/workflows/ci.yml`)
- Packable projects keep `EnablePackageValidation` green (no unintended public
  API surface regressions on pack)
- CodeQL finds no new critical issues
- At least one maintainer approval (CODEOWNERS applies; security-sensitive
  paths under `Execution/`, `Pipeline/`, and `Wrappers/` require explicit attention)

## Code Style

- Follow `.editorconfig` and the repository `Directory.Build.props` defaults
  (nullable reference types, latest language version, analysis level).
- Prefer `record` / `record struct` for immutable value-like types.
- Avoid `dynamic` and `Delegate.DynamicInvoke` in application code (source of
  past perf bugs — use cached compiled expression trees instead).
- Every public type needs an XML doc comment. `GenerateDocumentationFile` is
  enabled on packable projects.
- Keep files focused. If a file exceeds 500 lines, consider splitting.

## Testing

- Unit tests use **xUnit** + **FluentAssertions**.
- Every behaviour change must land with a test. Fuzz-reachable code paths
  (see `tests/Meridian.Mapping.Fuzz`) have extra expectations documented in
  that project's `README.md`.
- Flaky tests are quarantined in `[Trait("flaky", "true")]` and excluded from
  default runs. Fix or delete within one sprint — never let quarantine
  become permanent.

## Security Issues

**Do not open a public issue or PR.** Follow [`SECURITY.md`](./SECURITY.md).

## Code of Conduct

Be respectful. Discuss ideas, not people. English and Turkish are both
welcome in issues and PRs.

## License

By contributing, you agree that your contributions will be licensed under the
repository's [MIT License](./LICENSE).

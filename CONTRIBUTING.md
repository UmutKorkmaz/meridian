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
- .NET SDK 10.0.103 or later (see `global.json`)
- Git 2.40+

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

## Quality Gates (enforced by CI)

A PR is mergeable when **all** of these pass:

- `dotnet build -warnaserror` succeeds on `net8.0`, `net9.0`, `net10.0`
- All unit tests pass (`dotnet test`)
- Public API changes are reflected in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`
- `PackageValidation` passes (no unintended breaking API changes)
- CodeQL finds no new critical issues
- At least one maintainer approval (two for changes in `Execution/` or
  `Pipeline/` directories — these are the security-sensitive hot paths)

## Code Style

- Follow the existing `.editorconfig` and nullable-reference-types settings.
- Prefer `record` / `record struct` for immutable value-like types.
- Avoid `dynamic` and `Delegate.DynamicInvoke` in application code (source of
  past perf bugs — use cached compiled expression trees instead).
- Every public type needs an XML doc comment. `GenerateDocumentationFile` is
  enabled and warnings about missing docs will fail the build eventually.
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

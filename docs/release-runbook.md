# Release runbook: exact candidate preparation and promotion

This is the operator procedure for releasing Meridian from one exact, already verified set of package bytes. It is
implemented by `eng/prepare-release.ps1`, `eng/promote-release.ps1` and their shared helper `eng/release_candidate.py`
(the PowerShell scripts are thin wrappers that propagate the helper's exit codes). The tools never build, pack, rename,
re-sign or rewrite a package, and preparation never changes remote state.

> **Status.** The tools exist and are covered by fixture tests (`eng/tests/test_release_candidate.py`: tiny real ZIP/nupkg
> archives, an in-memory transport and verifier that exist only in the test file). That is orchestration evidence, not a
> release, publication, provider, CI or clean-copy proof. No stable candidate has been prepared and nothing has been
> published with these tools.

## Not yet available

These are missing on purpose or pending elsewhere; each one keeps a candidate `blocked` (never `prepared`) or keeps
execution unavailable:

| Missing piece | Effect today |
| --- | --- |
| A **trusted authorization** verifier (human approval bound to the candidate digest, repository, version, targets and source commit) | `promote-release.ps1 -Execute` and the `execute` subcommand fail closed with exit 3. No write transport ships. A self-authored `approved=true` document, or knowing the digest, is never authority. |
| Effective repository **governance** (required status checks and approving reviews on `main`) | The read-only snapshots show required checks and reviews absent and no rulesets, so the `governance` row is `blocked`. Proposed settings are in `docs/governance/required-checks.md`; a second maintainer must not be simulated. |
| Final cumulative E4 evidence (**E4d**) for the final bytes | The `e4-aggregate` row is `blocked` (lanes missing) or `fail` until the final campaign passes every required lane for the exact candidate. |
| E6 maintenance and final-review receipts for the final source commit | The `e6-maintenance` and `final-review` rows are `blocked` until receipts exist. |
| A fresh read-only remote availability check | `3.0.0` is a proposal. The `remote-availability` row records which snapshot was used and marks it stale-able. |
| A stable pack produced by the root-owned engine with `-ReleasePreparation` | No stable bytes exist; the frozen probe feed (`3.0.0-toolprobe.*`, `sourceDirty: true`) is never release evidence. |
| NuGet signature trust, symbol processing and fresh signed-remote consumer verification | External steps (pinned `dotnet nuget verify --all`, the symbol server, the root-owned package engine). The tools record them as pending, never as passed. |

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | prepared / verified / plan ready with no stop condition / archives equal under the signing rule |
| 1 | failed validation: integrity, identity, collision, a stop condition, corrupted receipts or a lost bundle |
| 2 | invalid arguments, including a stable version without `-ReleasePreparation` or a prerelease with it |
| 3 | BLOCKED: fail-closed missing evidence, governance, E7 validator, authorization, lock, or unobservable/uncertain remote state |

## Source commit S and receipt commit R

* Commit every final product, version, changelog, tooling, E6, E7 and final-review fix **before** the final pack. That
  commit is the **source commit S**. The packages, the candidate bundle and every evidence manifest are produced from S
  and stored outside it; S never contains hashes of its own artifacts.
* The tag points to S. A later authorized **receipt commit R** may record S, the approval digest and the promotion
  receipts. Never tag R while claiming the packages were built from R.
* If a review changes any source-tree content after packing, the candidate is invalid: integrate a new S and prepare
  again. A reproducibility rebuild is a separate comparison artifact and never replaces approved packages.

## 1. When to freeze

Implement and test tools early, but freeze only after E6, E7 and the final independent review and their fixes are
complete and committed as S. Then produce the final bytes once, run the cumulative evidence against those bytes, re-read
remote version and governance state, and only then present the candidate for authorization. Do not stop independent
local work to ask an early publication question; the authorization request is the final external step.

## 2. Produce the final bytes once (root-owned engine)

```powershell
pwsh eng/pack-packages.ps1 -CandidateVersion 3.0.0 -ReleasePreparation -OutputDirectory <feed>
```

`-ReleasePreparation` is the stable-intent guard shared by every package entry point (`pack-packages.ps1`,
`verify-packages.ps1`, `verify-candidate.ps1`, `verify-docs.ps1`, `run-aot-smoke.ps1`, `test-ef10-compatibility.ps1`,
`prepare-release.ps1`, and `promote-release.ps1` for a stable candidate). It is intent, never approval. Ordinary probes
stay unique prereleases without it. Never rename or rewrite prerelease bytes or a nuspec into a stable package. A failed
pack attempt is discarded; a new attempt uses a new output directory and is never resumed by repacking missing files.

## 3. Cumulative evidence against those bytes

Run the final E4 campaign (E4d) so every required lane consumes the same feed:

```powershell
pwsh eng/verify-candidate.ps1 -Feed <feed> -EvidenceDirectory <e4-evidence> -Mode Aggregate -CandidateVersion 3.0.0 -ReleasePreparation
```

Keep the E4 evidence directory (`lanes/<lane>/lane-result.json` plus retained raw files); preparation re-derives the
verdict from it.

## 4. Prepare the candidate

```powershell
pwsh eng/prepare-release.ps1 -CandidateVersion 3.0.0 -ReleasePreparation `
  -EvidenceDirectory <new-candidate-dir> -Feed <feed> -GateEvidence <e4-evidence> `
  -AvailabilitySnapshot <availability.json> -E6Receipt <e6.json> -FinalReviewReceipt <final-review.json> `
  -GovernanceProtection <protection.json> -GovernanceRulesets <rulesets.json> -ReleaseNotes <notes.md>
```

`-EvidenceDirectory` is the **new** candidate directory. An existing (occupied) directory is refused with exit 1 and left
untouched. Preparation works in a private staging directory next to it and checks, as evidence rows:

| Row | Passes only when |
| --- | --- |
| `release-identity` | the version is canonical NuGet SemVer2 (three parts, no build metadata, no leading zeros, int32 bounds), stable intent is explicit, it exceeds the published baseline `2.1.3` and it is the next major line (accepted major rationale: changed authorization, cache/idempotency, enum, telemetry, pipeline and constructor defaults) |
| `source-identity` | HEAD is the package manifest's `sourceCommit` S, the tree is clean (`git status` empty, untracked files included), `git archive` of S matches every git blob, and S is unchanged at the end of preparation |
| `package-adoption` | `package-manifest.<version>.json` has `sourceDirty: false`, its inventory hash equals `eng/packages.json`, the feed holds exactly the inventory package set (no missing, duplicate, unlisted or symlinked file), every archive hash matches, every archive is unambiguous (no duplicate, case-colliding, traversal, absolute or symlink entries, no prepended data), each root nuspec's id/version equals the manifest, internal dependencies use exactly the candidate version, the dependency graph is acyclic and the inventory order is dependency-first, and no approved package carries a `.signature.p7s` |
| `remote-availability` | a read-only snapshot covers every package, the version is not present and exceeds the highest published stable version (stale-able, never a reservation) |
| `e4-aggregate` | `eng/candidate_gate.py` re-run in aggregate mode over the adopted copies is `PASS`, release eligible, a whole-candidate verdict, for the same version, package manifest digest, packed source commit and package hashes; lanes without evidence are `blocked`, failed or skipped lanes `fail` |
| `e7-lifecycle` | The actual wrapper receives `-Candidate <immutable package-manifest>` and `-ConsumerResults <one E4-validated complete lane>`. Its report must bind both byte hashes, all four ledger entries, retained source invariants and all nine matching old/new fixtures. Preparation and bundle verification independently re-derive these facts; a missing input blocks, a mismatch fails. |
| `e6-maintenance`, `final-review` | a `meridian-release-receipt/1` with verdict `PASS` bound to S and the version (the final review also to the package manifest digest) |
| `governance` | the branch protection snapshot of this repository requires every CI-defined policy check and at least one approving review |
| `release-notes` | notes exist and name the exact version |

If every row passes, the staging directory is renamed to the candidate directory, files are made read-only, and the
helper prints the **approval anchor**: the SHA-256 of the exact `candidate.json` bytes. It is also written outside the
bundle as `<candidate-dir>.approval-anchor.json`. `candidate.json` never contains its own hash.

If any row fails (exit 1) or is blocked (exit 3), nothing becomes the candidate directory: the attempt moves to
`<candidate-dir>.quarantine-<candidate-id>` with a `preparation-report.json` listing every failure and blocker. A
quarantined attempt is kept for diagnosis and never resumed; a new attempt gets a new candidate id.

### Candidate directory layout

```text
<candidate-dir>/
  candidate.json                      schema meridian-release-candidate/1 (not listed in its own allowlist)
  source/source.tar                   git archive of S
  source/source-files.json            sorted path / mode / size / sha256 / git blob of every file of S
  source/packages.json                inventory snapshot
  source/tooling.json                 hashes of the release, gate, pack and E7 tooling plus the Python version
  packages/<id>.<version>.nupkg       adopted byte-for-byte
  packages/<id>.<version>.snupkg      symbols, tracked separately
  packages/package-manifest.<version>.json
  evidence/e4/...                     copy of the E4 evidence the gate was recomputed from
  evidence/e4-candidate-summary.json  recomputed meridian-candidate-summary/2
  evidence/e7-deprecation-validation.json (+ .log)
  evidence/availability-snapshot.json
  evidence/receipts/*.json            E6, final review and governance snapshots
  metadata/release-notes.md, CHANGELOG.md, LICENSE, NOTICE (when present in S)
```

`candidate.json` records the schema, candidate id, canonical version and release class, repository, S and its tree, the
inventory hash, the file allowlist (relative path, SHA-256, size, role), the package closure (every archive entry's
path/length/SHA-256 and the nuspec id/version/dependencies), the dependency-first order, the evidence policy rows, the
tooling identities, the ordered promotion targets and the limitations. There is no `site/` content: site publication is
`not-prepared` and not authorized. The SBOM is the package manifest's private analyzer build provenance (a custom format,
not CycloneDX or SPDX).

The E7 lifecycle report binds the exact immutable `package-manifest.<version>.json` and one complete raw old/new
consumer lane. Every required consumer lane still has to pass E4, including its source, compiler, runtime and actual
restored archives. The report's `provenanceVerified: false` is intentional; semantic equality alone does not prove an
execution. The enclosing `candidate.json` approval digest then binds the report and all retained raw files. These two
manifest identities avoid a self-hash cycle. Verification re-derives lifecycle/source/semantic facts from the archived
source and raw evidence with trusted controller code; archived code is never imported or executed.

## 5. Verify

```powershell
python3 eng/release_candidate.py verify --manifest <candidate-dir>/candidate.json --expected-sha256 <digest>
```

Verification checks the exact-bytes digest, that every listed file is present with its hash and size, that there are no
unlisted, symlinked or special files, that every path is relative, unique, contained, portable and free of case
collisions, that every archive still has exactly the approved entries and nuspec identity, that the internal dependency
graph is acyclic with same-version internal dependencies, and that every required evidence row passed.

## 6. Present the candidate for trusted authorization

Present the digest, the version, S, the repository and the exact ordered target list from `candidate.json` (tag, GitHub
release and assets, NuGet packages dependency-first, symbols, and explicitly whether source push or a site is included).
Authorization must name exactly those. Until a trusted verifier exists, record "local preparation complete; promotion
pending trusted authorization" and stop.

## 7. Plan the promotion (read-only)

```powershell
pwsh eng/promote-release.ps1 -Manifest <candidate-dir>/candidate.json -ApprovedManifestSha256 <digest> -ReleasePreparation `
  -Receipts <outside>/promotion-receipts.jsonl -Output <outside>/promotion-plan.json
```

The plan verifies the bundle, copies it into a private snapshot, reads remote truth with GET requests only (GitHub API
and the NuGet flat container; `GITHUB_TOKEN`, when set, is sent only to `api.github.com`) and prints each target's
observed state, recorded receipt state and next action. `-Offline` reads nothing and is therefore `blocked`. A plan never
writes remote state or receipts. The `Release promotion plan` workflow (`.github/workflows/release.yml`) runs the same
plan on demand (`workflow_dispatch` with the bundle artifact, its run id and the approved digest); it has no tag trigger
and no publisher. When a bundle is uploaded as an artifact, upload the whole candidate directory unchanged (set
`include-hidden-files: true` if any evidence file name starts with a dot); any missing or added file fails verification.
Use one receipt stream per candidate; its lock file serializes promotions that share it.

## 8. Promotion order (only after trusted authorization)

1. Confirm S is on the remote (push exactly S only if the authorization includes it; the tool never pushes source).
2. Create the tag `v<version>` at S.
3. Create the GitHub release with `metadata/release-notes.md` and attach the exact assets (packages, symbols, package
   manifest, `candidate.json`); download each asset and require whole-file equality.
4. Push NuGet packages in dependency order (a dependent waits until its dependencies are verified). Then upload symbols.
5. Retrieve every package and compare it with the approved bytes under the signing rule below; verify the repository
   signature with the pinned SDK; run fresh isolated consumers against the signed remote packages.
6. Only then publish an authorized site artifact (none is prepared today).

GitHub and NuGet status are recorded separately: a tag or a GitHub release alone is not package publication. Every write
is preceded by an intent receipt and the snapshot bytes are re-hashed before each upload. `--skip-duplicate` is never
used: an existing version is never treated as success.

## 9. Recovery table

| Observed condition | Required recovery |
| --- | --- |
| Tag absent | Create it only at the approved S after authorization. |
| Tag present at S | Resume only the same recorded approved promotion; record the verified identity. Without a recorded promotion: stop for disposition. |
| Tag present elsewhere | Stop. Never force-move or delete it. |
| Release asset present with matching bytes | Download, hash, mark verified; upload only genuinely absent assets. |
| Release or asset collision, unexpected assets or different bytes | Stop for explicit disposition; never overwrite or clobber. |
| NuGet version present on a first or new attempt | Reject the reused identity; matching metadata does not make it reusable. |
| NuGet version present during a recorded resume | Retrieve it, compare under the signing rule, verify the repository signature; accept only the same approved candidate. |
| Upload timeout, lost response or indexing delay | Mark `uncertain`; requery with bounded backoff and keep the evidence. A transient 404 is never permission to republish different bytes. A later resume requeries first and only then retries the same approved bytes under the original intent. |
| Some packages published, others absent | Resume the absent identities from the frozen set only, dependency-first; no rebuild, renamed version or mixed candidate. |
| Approved local bundle lost or corrupted | Stop. Restore byte-identical bytes from a verified retained copy; rebuilding cannot recover the approved identity. |
| Consumer or publication verification fails | Mark the partial or failed verification and stop advancing site claims. Diagnose without deleting tags or packages or creating replacement content under the same version. |
| Receipt stream corrupted or holding another candidate | Stop. Never erase, truncate or rewrite receipts; never mix candidates. |
| Lock file present | Another promotion holds it, or a crashed one left it. Confirm nothing is running before removing it by hand. |

## 10. NuGet repository-signing comparison rule

NuGet.org repository-signs every upload, so the downloaded whole-file hash legitimately differs from the uploaded one.
Keep both `uploadSha256` and `downloadSha256`. The downloaded package must contain every non-signature entry of the
approved package with identical path, length and SHA-256, including the nuspec, README, LICENSE, libraries and build
assets. The only permitted added entry is the exact root `.signature.p7s`; a nested or differently cased signature name,
a changed nuspec or any other difference is a mismatch. GitHub release assets require whole-file equality. To compare a
downloaded package by hand:

```powershell
python3 eng/release_candidate.py compare-nuget --approved <bundle>/packages/<id>.<version>.nupkg --downloaded <downloaded.nupkg>
```

Payload equality is not signature trust: run `dotnet nuget verify --all` with the pinned SDK and retain its output.

## 11. Symbols

Symbol packages are uploaded after the packages and tracked as separate targets. NuGet exposes no retrievable symbol
archive, so a symbol upload is at most `present-unverified` in the receipts; use the supported symbol-server
verification route and record its result separately. Package success never implies symbol success.

## 12. Promotion receipts

Receipts are an append-only, hash-chained JSON-lines stream (`meridian-promotion-receipt/1`) outside the bundle. Each
line carries its sequence, the previous line's SHA-256, its own content digest, the candidate digest and id, the attempt
id, the target and one state of `absent`, `requested`, `present-unverified`, `verified`, `failed` or `uncertain`, plus a
redacted request/result and the upload/download hashes. An intent (`requested`) is written before every request; a retry
names the original intent (`retryOfSequence`). A restart always reads remote truth first. Corrupted streams are never
repaired automatically.

## 13. Rollback

Rollback means applications pin a previously verified compatible package set (for the core packages the published
`2.1.3`) and, if authorized, the prior verified site artifact is restored. The new adapter packages have no older
version to roll back to. Provider schema or key changes (cache namespaces, idempotency and lease records) can make binary
rollback unsafe: keep cache namespace transitions, never erase uncertain idempotency or lease records, reconcile business
effects separately and never replay side effects automatically. Unlisting, advisories or any provider mutation need
their own authorized disposition. A bad published version is fixed with a new version, never with replacement bytes.

## 14. Input formats

* `meridian-release-receipt/1` (E6 and final review): `{"schema", "kind": "e6-maintenance" | "final-review",
  "verdict": "PASS", "sourceCommit": S, "candidateVersion", "packageManifestSha256" (final review), "reviewedAtUtc",
  "reviewer"}`. A receipt is evidence, not authorization.
* Governance: the raw read-only envelopes `{"atUtc", "command": ["gh", "api", "repos/<owner>/<repo>/branches/main/protection"],
  "exit", "data"}` and the matching `.../rulesets` read.
* Remote availability: `{"schema": "meridian-remote-availability/1", "retrievedAtUtc", "packages": [{"id", "status":
  "present" | "absent", "versions": [...]}]}` or the flat-container status list (`id`, `http`, `stableVersions`). A
  stable-only listing cannot prove that a prerelease identity is free.

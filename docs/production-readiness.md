# Production and certification readiness

Updated: 2026-08-25.

“Production-ready” means every repository, packaging and live-model gate is
present and fail-closed. It does **not** mean that Autodesk endorses the product
or that Windows can authenticate its publisher: public releases are permanently
unsigned by owner policy.

## What this repository can prove without buying a certificate

- One contract and one version for the server and all Revit payloads.
- Revit 2023–2027 compile gates against each installed Revit API.
- Exact payload inventory, SHA-256 manifest, CycloneDX SBOM and GitHub artifact
  attestation.
- Deterministic static triage of the staged IronPython standard library, including
  the pinned 614-file inventory, cross-year byte equality, unexpected-file and
  high-confidence source-risk checks, with JSON/SARIF evidence. This is not a
  comprehensive semantic or vulnerability audit.
- Release gates that require every public Horizun-owned PE and Setup to be
  `NotSigned`, reject misleading invalid/self-signed states, and stamp the absence
  of publisher identity into the package record.
- A bootstrap that requires explicit `-AllowUnsigned` acknowledgement for every
  release and warns that matching release checksums do not authenticate a Windows
  publisher.
- Separation between untrusted pull-request jobs, licensed Revit runners and
  disposable hosted integrity/attestation jobs, with immutable action references.
- Safe capability defaults: typed in-model writes available, document/external side
  effects gated, arbitrary Python off; a visible Revit ON/OFF grant remains active
  only after explicit owner consent and until that owner revokes it.
- Bounded queues, requests, responses, images, task records and retention stores.
- MCP protocol negotiation through 2025-11-25 with Tools, Resources, Prompts,
  Completions, progress, opt-in Logging and durable task-augmented tool calls.
- Live harnesses that refuse a green release report when a required probe is failed,
  unverified or not covered.

## Evidence state

| Gate | Current state | Authority |
| --- | --- | --- |
| Core tests | 1446/1446 Release on 2026-08-25, including deterministic packing rules and source guards for provisional paper-size measurement after authorisation | repository/CI |
| Server tests | 377/377 Release on 2026-08-25, including production tool schemas, direct-model visual-review prompt requirements and schema-4 live evidence | repository/CI |
| Windows deployment suites | 16/16 on 2026-08-25 (the workflow's 14, plus sbom and version-consistency which need a stage), each child exit code enforced | repository/CI |
| Revit API compile | 2023–2027, locked restore + `-warnaserror`, 0 warnings/errors on 2026-08-25 | local licensed runner |
| Live Revit matrix | **GREEN on Revit 2023–2027 at candidate `32baa87`: 165/165 per year, 825/825 total, 0 failed / 0 unverified / 0 not covered; 81 committing probes per year, 405 total.** Each year includes 17 dimension, 11 detail-2D, 22 planimetry query/audit, 23 planimetry-correction and 5 autonomous-production cases. Production proves obstacle-aware packing, auto-tag planning through the verified explicit-type writer, semantic intent dimensioning, atomic revision/sheet/cloud creation and real sheet PNG capture without PDF. The matrix exposed and closed three pre-green defects: auto-tag search bounded too tightly; the harness truncated deeply nested cloud loops; and Revit 2023 returned an empty `View.Outline` for a valid unplaced drafting view, so packing now measures a real provisional viewport+label and preserves its insertion-point offset after a confirmed rollback. The earlier intermittent Revit 2023 `section-view-template` stale observation did not recur, but remains open because one clean candidate run does not establish its cause. Revit 2023 startup was twice blocked before health by Autodesk Insights' own external-tool failure dialog; the journal named Insights, only that notice was closed, and no model/probe had started. Durable sanitized evidence is `docs/evidence/live-matrix.json` schema 4; it pins candidate, harness, server/add-in and each full artifact SHA-256 | Revit runners + fixtures |
| Unsigned public-artifact state | permanent policy; all eight owned release targets must be `NotSigned`, disclosed in package metadata | repository/CI |
| Hosted integrity and provenance | exact package hashes, manifest, SBOM and GitHub attestations; no publisher identity claimed | disposable hosted runner |
| GitHub repository controls | secret scanning and push protection enabled; two administrators; protected `release-signing` environment and immutable `v*` tag ruleset created on 2026-08-20 | repository administrators |
| Licensed release runner group | `REVIT_RUNNER_GROUP` required for build, install and live matrix | organization administrators |
| Autodesk support/endorsement | not claimed and not obtainable from source changes | Autodesk |

The repository and live rows are complementary: neither a static build nor a
matrix alone represents the installable release chain.

The five-year matrix in this table is the current complete live result for
candidate `32baa87`. The schema-4 durable manifest pins the committed harness,
each local full report and the installed server/add-ins; later documentation or
evidence-only commits do not pretend to be a different tested binary candidate.

## Release decision

- **Stable, including 1.0+:** allowed unsigned with explicit disclosure,
  `-AllowUnsigned`, exact hashes/SBOM/attestations and a complete attached
  five-year live matrix. It is official but has no Windows publisher identity.
- **Preview with binaries:** follows the same unsigned disclosure rule and never
  replaces `latest`.
- **Source/validation commit:** allowed when repository gates pass; it carries no
  installation or certification claim.

## Operator actions outside this tree

1. Configure and audit `REVIT_RUNNER_GROUP`. Immutable release-tag rules, two
   administrators, secret scanning and push protection remain external controls.
2. Run `scripts/run-release-live-gate.ps1` for every supported year with the published
   synthetic fixture set; retain the JSON/JUnit evidence.
3. Build the release, install it on the release runner, and run
   `scripts/verify-release.ps1 -AllowUnsigned -Installed` against the exact
   per-run install result.

The correct trust verdict is **official unsigned release; source, package and
live-model validated; Windows publisher identity intentionally unavailable**.

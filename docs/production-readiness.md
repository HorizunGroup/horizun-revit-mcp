# Production and certification readiness

**Historical checkpoints last updated: 2026-08-29.** These rows describe named
development candidates. For current release evidence, use the assets attached
to [the applicable release](https://github.com/HorizunGroup/horizun-revit-mcp/releases).
A source change does not rerun or refresh these measurements.

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

## Historical evidence checkpoints

| Gate | Current state | Authority |
| --- | --- | --- |
| Core tests | 2555/2555 on 2026-08-29, including the production, DWG-to-BIM, structural/rebar and native-model diagnostics rules | repository/CI |
| Server tests | 404/404 on 2026-08-27, including the tool-pack golden lists and layering (a pack is visibility, never elevation), the schema-5 live-evidence gate and the extended production schemas | repository/CI |
| Windows PowerShell gates | 20/20 on 2026-08-29 before release preparation; the 18 Revit-free workflow gates now also include live-path and structural-ledger consistency, while SBOM and staged version consistency run after packaging | repository/CI |
| Revit API compile | 2023–2027, locked restore + `-warnaserror`, 0 warnings/errors on 2026-08-27 | local licensed runner |
| Live Revit matrix | **GREEN on Revit 2023–2027 at candidate `32baa87`: 165/165 per year, 825/825 total, 0 failed / 0 unverified / 0 not covered; 81 committing probes per year, 405 total.** Each year includes 17 dimension, 11 detail-2D, 22 planimetry query/audit, 23 planimetry-correction and 5 autonomous-production cases. Production proves obstacle-aware packing, auto-tag planning through the verified explicit-type writer, semantic intent dimensioning, atomic revision/sheet/cloud creation and real sheet PNG capture without PDF. The matrix exposed and closed three pre-green defects: auto-tag search bounded too tightly; the harness truncated deeply nested cloud loops; and Revit 2023 returned an empty `View.Outline` for a valid unplaced drafting view, so packing now measures a real provisional viewport+label and preserves its insertion-point offset after a confirmed rollback. The earlier intermittent Revit 2023 `section-view-template` stale observation did not recur, but remains open because one clean candidate run does not establish its cause. Revit 2023 startup was twice blocked before health by Autodesk Insights' own external-tool failure dialog; the journal named Insights, only that notice was closed, and no model/probe had started. Durable sanitized evidence is `docs/evidence/live-matrix.json` schema 4 (private evidence, kept out of the public repository); it pins candidate, harness, server/add-in and each full artifact SHA-256 | Revit runners + fixtures |
| Live Revit 2026 dev checkpoint (dimension production) | **GREEN at candidate `f39c84a` on 2026-08-26: 169/169 probes, 0 failed / 0 unverified**, 3 named not-covered gaps (the closed-workset fixture the machine has not provided, and the two release-manifest SHA comparisons that belong to the release gate). The run adds the W10 linked-dimension and production-at-scale section: 18 cases covering link discovery with three-id separation and transform fingerprints, a committed host+linked mixed chain, the stale refusal NAMING `link_transform_moved` when the link moves between rehearsal and apply, unload/reload coded coverage, auto-dimension planning with chain dedup against the drawing (replayed plans answer `already_dimensioned`), the measured linked-datum rejection enforced at discovery/annotate/planner, room view production, placeholder/convert/duplicate sheets, view range/crop/viewport alignment, schedule definition editing with a fingerprint-bound token, revision sheet withdrawal, and the tool-pack round trip. Multiversion (2023–2027 live) is deliberately NOT run: it is the deferred final phase awaiting express authorization. Report: `artifacts/live/verify-2026-dev-w10-run5.json` (local, not committed) | Revit 2026 runner + fixtures |
| Live Revit 2026 dev checkpoint (Maximum Program W11) | **GREEN at candidate `3f669b5` on 2026-08-26: 182/182 probes, 0 failed / 0 unverified** (the W10 dimension-production 18 plus thirteen W11 probes covering phases 5-14: connector facts and verified fittings, the measured coincidence refusal, penetration planning with the structural opt-in and the verified wall opening, durable coordination findings with export re-read from disk, grid-crossing columns with the already_present replay, the CSV round trip with its declared no-op and duplicate-key refusal, typed link unload/reload/pin/unpin, the pre-delivery gate, the type catalog verified from disk, and health's job/timing facts). Run 6 measured that a committed opening RESOLVES its clash - the finding re-run now precedes the cut. The three named not-covered gaps are unchanged (closed-workset fixture, two release-manifest SHA comparisons). Report: `artifacts/live/verify-2026-dev-w11-run8.json` (local). Multiversion stays deferred. | Revit 2026 runner + fixtures |
| Live Revit 2026 checkpoint (W12 checklist increment) | **GREEN at candidate `6915417` on 2026-08-26: 196/196 probes, 0 failed / 0 unverified** - the 182 previous plus fourteen W12 probes: the routed L committing whole with its deferred batch corner, named route refusals, the takeoff's plan-time distance AND routing-preference refusals (both measured: NewTakeoffFitting needs junction preference Tap), verified sizing on a sloped run, the open-connector census failing the zero gate (61 measured), the circular slab opening from a vertical crossing, 300 mm crossings clustered into one opening, oriented sleeves, the finding's comment/evidence/structural-BCF story, beam systems with authored framing and the wall footing, CSV placement with the edited file refusing its stale token, shared-coordinate reads plus the replayed CSV writer, link add/repoint both re-read, and the family flexed AND MEASURED with its thumbnail verified from disk. Four runs of measurement (9-13) closed three product defects found live. Phases 6, 7 and 10 complete their checklists; the ledger carries every remaining pending item. Report: `artifacts/live/verify-2026-dev-w12-run13.json` (local). Multiversion stays deferred. | Revit 2026 runner + fixtures |
| Live Revit 2026 checkpoint (Maximum Program, development surface closed) | **GREEN at candidate `da6271b` on 2026-08-27: 237/237 probes, 0 failed / 0 unverified**, 157 of them committing writes. The increment over the 196/196 of run 13: the mandated positives (a family whose labeled parameter DRIVES its solid, measured 300 and 600 mm; visibility, material and reload; the workbook round trip over the wire; units and locale DECLARED, one height read four ways; shared-coordinate placement; network membership and connectivity; cancellation, backpressure and the lost reply replayed by key; export presets proved from the file; five reads under pre-declared caps) plus sixteen W14 probes: inline accessories that BREAK a live run and rejoin both halves, the rebar cover safe subset, an interrupted job from a DEAD Revit answering off the disk record, S/M/L performance with UI hold and working set, named MEP systems whose members are re-read from the model, four membership refusals each measured from Revit first and moved to plan time, and THE POSITIVE TAKEOFF on a duct type whose junction preference re-reads Tap. Three product defects were found by this run series and fixed: quantities answering off whatever document was in front, an empty category erroring instead of reporting incomplete coverage, and MEPSystem.Add associating nothing without throwing. The two named not-covered gaps compare installed bytes against a RELEASE MANIFEST, which a development tree does not have. Report: `artifacts/live/verify-2026-dev-w15-run25.json` (local). Multiversion stays deferred. | Revit 2026 runner + fixtures |
| Live Revit checkpoint (native-model diagnostics P0) | **GREEN on Revit 2026 at candidate `09fc20b`: 16/16 probes, 0 failed / 0 unverified / 0 not covered / 0 fixture-missing.** It covers coordinates, datums, model-scan diagnostic sections, readiness evaluation, durable snapshots and the health-index rules implemented in the P0 slice. The broader 33-story diagnostics programme is not claimed complete; its generated ledger (`docs/MODEL-DIAGNOSTICS-PROGRAM-STATE.json`, private, kept out of the public repository) records 2 done, 0 partial and 31 implemented_not_live_verified, and names the remaining work. | Revit 2026 runner + diagnostic fixture |
| Live Revit 2026 checkpoint (1.2.0: wall decomposition + Model Doctor) | **Measured 2026-09-01 on Revit 2026 build 26.4.0.32, on binaries stamped `1.1.6` whose source equals the release (`git diff b08b7a2 1b3575c -- src` empty).** Walls at `b08b7a2`: 45 passed, 0 failed, 5 blocked_fixture, 1 blocked_environment, 4 unsupported_api of 55. Model Doctor at `a792663`, contract `56cc3b66a8d226035884ad4a`: 71 passed, 0 failed, 5 fixture_missing (ACC cloud, ownership, borrowing, closed workset, workset placement). No `1.2.0`-stamped binary has been measured; 2023/2024/2025/2027 not opened. Record: `docs/evidence/release-1.2.0-live-evidence.md` + `.json` (private evidence, kept out of the public repository). | Revit 2026 runner + disposable fixtures |
| Unsigned public-artifact state | permanent policy; all eight owned release targets must be `NotSigned`, disclosed in package metadata | repository/CI |
| Hosted integrity and provenance | exact package hashes, manifest, SBOM and GitHub attestations; no publisher identity claimed | disposable hosted runner |
| GitHub repository controls | secret scanning and push protection enabled; two administrators; protected `release-signing` environment and immutable `v*` tag ruleset created on 2026-08-20 | repository administrators |
| Licensed release runner group | `REVIT_RUNNER_GROUP` required for build, install and live matrix | organization administrators |
| Autodesk support/endorsement | not claimed and not obtainable from source changes | Autodesk |

The repository and live rows are complementary: neither a static build nor a
matrix alone represents the installable release chain.

The five-year matrix in this historical table is the complete live result recorded for
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

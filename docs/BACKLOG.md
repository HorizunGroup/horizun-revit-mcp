# Horizun Revit MCP — Backlog

Working backlog for the next phases. Sizes are rough: `S ≈ days · M ≈ 1–2 weeks
· L ≈ weeks`. ⭐ marks the recommended entry point of each epic.

**How to work this:** one branch per story (`epicN/short-name`), a PR into `main`,
never a direct commit to `main`. Build and run the tests before opening the PR.
Read [AGENTS.md](../AGENTS.md) first — it loads the project rules every session.

---

## EPIC 0 — Signing & distribution *(unblocks worldwide install)*

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 0.1 ⭐ | Buy an OV cert (SSL.com cloud signing) and sign server + add-ins in CI | M | — |
| 0.2 | Opt-in installer step for Trusted Publishers (drives the dialog to zero) | S | 0.1 |
| 0.3 | Verify on a clean machine that "Unsigned Add-In" is gone | S | 0.1 |

## EPIC 1 — Verified commands from field knowledge *(widens the moat)*

Turn validated `execute_python` recipes (from the CORE memory `mep.md` / `api.md`)
into typed, verified commands. Each new command re-reads the model after the
commit and gets an entry in the `execute_python` typed-overlap guard.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 1.0 ⭐ | Prioritised candidate list from `mep.md`/`api.md` — each with a proposed contract + estimate | S | — |
| 1.1 ⭐ | `horizun_place_sprinklers` (validated: 786 placed; verify connector + 0mm slack) | M | 1.0 |
| 1.2 | `horizun_connect_mep` (tees/reducers; the "opposite direction" fix) | M | 1.0 |
| 1.3 | `horizun_terminate_riser` (RCI riser to roof, seismic joint) | M | 1.1 |
| 1.4 | Family: verified parametric-void mirror/duplicate command | M | 1.0 |
| 1.5 | Each new command → entry in the `execute_python` overlap guard | S | 1.1–1.4 |

## EPIC 2 — Unified bridge contract *(the platform jump)*

Not one binary — one shared contract that Civil3D/Navisworks/PBI adopt: named
pipe + token (no TCP ports), verified writes, capability profiles, health,
discovery. Revit is the reference implementation.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 2.1 | Write "Horizun Bridge Contract v1" (transport, verified write, health, discovery, profiles) | M | — |
| 2.2 | Retrofit `civil3d-bridge` to the contract (token+pipe, `*_health`) | L | 2.1 |
| 2.3 | Unified installer + naming (`horizun-revit` / `-civil3d` / `-navis`) | M | 2.2 |
| 2.4 | Federated `horizun_health` (one call, status of every bridge) | S | 2.2 |

## EPIC 3 — Model → data → budget pipeline (+ real time) *(deep-moat vertical)*

The pieces exist (`horizun_quantities`, `horizun_power_bi_push`,
`horizun_excel_write_rows`, durable idempotency, async job queue). What is
missing is the verified connective tissue.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 3.1 ⭐ | Provenance: every quantity row carries element IDs + `HRZ_COD_PRES` | M | — |
| 3.2 | Drift detection: diff takeoff vs. last budget, flag changed lines (the 4D value) | M | 3.1 |
| 3.3 | `horizun_publish_delivery`: model→quantities→Excel→PBI in one idempotent op | M | 3.1 |
| 3.4 | Event-driven push (`DocumentChanged`→delta→`power_bi_push`, debounced via async queue) — real-time PBI without re-export | L | 3.1, 3.3 |
| 3.5 | *(enterprise, optional)* Fresh mirror + DirectQuery (Postgres/Fabric) | L | 3.3 |

### Real-time Power BI — the honest architecture
Power BI cannot DirectQuery a live `.rvt` (Revit exposes no query endpoint).
"Real time without re-export" means **push-on-change**: Revit's `DocumentChanged`
event → compute the delta → push to a Power BI streaming/push dataset (story 3.4),
debounced and run through the async job queue so Revit's UI thread is never
blocked. For rich DAX models, keep a fresh mirror that PBI DirectQuery's (3.5).

## EPIC 4 — Standards as data *(makes the diagnosis portable)*

The diagnosis already exists. `horizun_model_scan` and `horizun_audit_model`
measure, and the pre-delivery and model-diagnosis skills score and propose. What
does not exist is a way to say **which standard** they are measuring against: the
rules are welded in — Horizun's naming conventions, one client's catalogue, one
client's classification system — so a new client means rewriting a skill.

So this epic does not add ISO 19650 to the bridge. It turns a standard into an
**argument**, which is what AGENTS.md already says every command needing one must
do. Three layers, and the line between them is the whole design:

- **The bridge MEASURES and never judges.** Facts per element, with the
  distinction it already draws between "does not conform" and "could not be read".
- **The standard ARRIVES as a versioned artifact** — a *requirement set*:
  classification system and table, required properties per category and stage,
  a naming grammar, an export mapping. Passed, never compiled. A standard inside
  the C# ships as a new binary whenever a clause changes and cannot be diffed per
  project; as an artifact, one command serves a Colombian NSR job, a UK ISO 19650
  job with its national annex, and a client EIR that overrides both.
- **The judgement and the prose live above**, in the agent and in Horizun Hub. The
  bridge hands back measured evidence **plus the typed command that would fix each
  finding** (`set_keynote`, `write_params_verified`, `bind_shared_param`,
  `regroup_by_param`), so a proposal is composed out of measurements rather than
  written from a guess about the model.

Almost all of it is READ-ONLY, so it does not wait on Epic 1: the write half
(auto-correcting codes and properties) waits on the rollback rule and the write
probes, which landed with #9.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 4.0 ⭐ | `docs/requirement-set.md` — the artifact schema, general enough that ISO 19650, IFC/buildingSMART and COBie are three **documents** rather than three code paths | M | — |
| 4.1 ⭐ | `horizun_check_requirements`: takes a requirement set, returns per-element measured conformance, never a verdict it did not measure, each finding naming the typed command that would fix it | L | 4.0 |
| 4.2 | Classification coverage against a supplied table: missing codes, and codes that are not a last-level leaf. Generalises the existing client-classification audit to OmniClass / Uniclass / IFC class with no code change | M | 4.1 |
| 4.3 | LOIN / property-set conformance per category and stage: which required properties are absent, per element, with unreadable kept separate from missing | M | 4.1 |
| 4.4 | IFC mapping completeness BEFORE export: which categories will land as `IfcBuildingElementProxy` and which have no mapping at all — whether the IFC will be usable, which is what buildingSMART conformance actually turns on | M | — |
| 4.5 | Naming grammar as a supplied pattern (ISO 19650-2 field structure), replacing the naming rules hard-coded in the pre-delivery skill | M | 4.1 |
| 4.6 | COBie-shaped extraction (Spaces / Types / Components / Systems) that reports what is missing instead of emitting blank cells | L | 4.3 |
| 4.7 | Guided correction: apply a requirement set's fixes through the existing typed writes, one confirmation per batch | L | 4.1, 1.x fixed |

### The reference standard: all three, as documents, from the start

Decided 2026-08-03. The alternative was to implement one standard first and
generalise later; this takes the slower first result deliberately, because the
whole claim of this epic is that a standard is data. Three standards loaded as
three requirement sets on day one is the only thing that actually proves it — one
standard first would let a single set of assumptions harden into the schema
unchallenged, and "generalise it later" is how a compiled-in standard happens by
accident.

They ask different questions of a model, and that is the point: the schema has to
carry all three shapes without a branch in the C#.

- **ISO 19650** — naming grammar, information containers, stage-gated LOIN.
  Strongest for delivery discipline, says nothing about geometry.
- **IFC / buildingSMART** — class mapping and export conformance. The only one an
  outside party can verify without opening Revit.
- **COBie** — handover data completeness. Narrowest, and the easiest to be
  unambiguously right or wrong about.

So 4.0 is the entry point, not a decision to be made, and the acceptance test for
it is blunt: **three requirement-set documents, one command, no standard-specific
code.** If 4.1 needs an `if (standard == …)` anywhere, 4.0 is not finished.

4.4 keeps its independence and can still run first or in parallel — it needs no
schema — but it is no longer the recommended entry point.

## EPIC 5 — Trust, not surface *(from an external review, verified against the code)*

The surface is wide enough. The next jump is turning technical guarantees into
**verifiable** trust. An external review proposed ten items; each was checked
against this tree before being written down, because a review accepted on faith is
just a longer opinion. Status per item, and the three places the review is wrong
or in conflict with a rule we already adopted, are recorded below the table.

**Tool freeze:** no new typed commands until 5.1–5.4 land. Epic 1's three
commands still cannot do their job; adding surface on top of that is how the
review's whole diagnosis came about.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 5.1 ⭐ | **Bind the confirmation token to the RESOLVED ELEMENT SET, not the request.** Materialised plan: UniqueIds, before-values, types/levels/hosts, geometry or bbox fingerprint, expected create/modify/delete counts, cascade effects, document fingerprint + Revit version. Re-fingerprint at apply; mismatch returns `stale_plan`. Ordered hash / Merkle for large sets | L | — |
| 5.2 | Ledger → **operation receipts**: `operation_id`, tool, model + plan + request hashes, user, timings, affected UniqueIds, warnings, verification and transaction outcome. DPAPI at rest, configurable redaction, retention (7/30/90/manual) with a size cap and a purge tool, JSONL export, correlation ids across server/pipe/Revit | M | — |
| 5.3 | **Derive `destructiveHint`/`openWorldHint` from the contract**, not from hardcoded tool-name lists in `Tools.cs`. A new tool currently gets wrong annotations unless someone remembers to edit two `if` chains — the same welded-in shape Epic 4 exists to remove | S | — |
| 5.4 | **Human approval inside Revit** (`approval_mode: revit_ui`) for delete/save/export/family-replace/`execute_python`: non-modal panel naming document, tool, change summary, element count, external destinations, irreversible effects. Plus a ribbon-driven temporary unlock for `execute_python` (10–15 min, active document only, revoked on close/switch, optionally script-hash scoped) | L | 5.1 |
| 5.5 | **Live certification matrix 2023–2027** published per release: JUnit/HTML report, exact Revit build, fixture hashes, tools covered and not, time and peak memory, sanitised warning log. Dimensions: year × language × units × model kind (non-shared/local/central/detached) × links × size × discipline × outcome (commit/rollback/refusal/crash recovery). Needs public synthetic fixtures | L | 5.1 |
| 5.6 | **Chunked long operations** with cooperative cancellation, per-turn UI budget (100–250 ms), phase + percentage progress, safe checkpoints between batches, `submit_job` mapped to native MCP tasks. Benchmarks published as *longest continuous UI block*, not just total duration | L | 5.1 |
| 5.7 | **Sign the installer and binaries**: OV/EV Authenticode over exe/dll/installer/packaged scripts, timestamping, GitHub build attestations, signed SBOM and manifest, verification during install. Certificate trust stays an IT decision (Intune/GPO) — never installed silently | M | 0.1 |
| 5.8 | **Isolate the protocol behind a `ProtocolAdapter`** independent of the Revit domain: conformance tests against the official SDK and MCP Inspector, golden JSON-RPC request/response tests, explicit per-version negotiation, client compatibility matrix, schema deprecation policy. Add `execution.taskSupport`. Do this BEFORE adopting 2026-07-28, which is still RC | M | — |
| 5.9 | **Public governance**: issue templates (bug / proposal / compatibility) that demand Revit version+build+language, Horizun version, MCP client, tool, document kind, expected vs observed, sanitised logs. Discussions, public roadmap and milestones, labels, review SLA, a second maintainer with release rights, ADRs | S | — |
| 5.10 | **Release channels and the road to 1.0**: `stable` (signed + matrix approved), `preview`, `validation-only` (no new binaries). Publish SemVer policy, schema compatibility, deprecation window, config migration, back-version support, and an explicit definition of "production ready" | M | 5.5, 5.7 |

### What the review got right, verified in this tree

- **5.1 is genuinely the number-one item.** Confirmed: `ConfirmationStore.PlanHash`
  hashes only the named *request fields* — the arguments — so a token binds the
  question, never the answer. A filter that resolves to different elements after
  the dry run is accepted today.
- **5.2**: no retention, purge, encryption or redaction exists anywhere in the
  idempotency store. Confirmed by search.
- **5.8**: `execution.taskSupport` appears nowhere in the tree. Confirmed.
- **5.5**: the numbers quoted (48 passed / 11 not covered / 1 unverified on 2026)
  are this repository's own last live run, and they are exactly why a matrix is
  needed rather than a single-machine result.

### Where the review is wrong, or conflicts with a rule already adopted

1. **Annotations are already published for every tool** — `Tools.cs` emits all
   four hints. The review asks for something that exists. The real defect is
   underneath and it missed it: `destructiveHint` and `openWorldHint` come from
   hardcoded tool-name lists, so they rot silently. That is 5.3, and it is new.
2. **Public issues are NOT restricted.** `has_issues=true`, zero open. What is
   actually missing is templates and triage, which is why 5.9 is scoped to those
   and dropped to S.
3. **Chunking (5.6) collides head-on with the rollback rule from 0.6.0.** A Revit
   transaction cannot span `ExternalEvent` invocations, so "process in chunks"
   means separate transactions — and a failure then leaves earlier chunks
   committed, which CONTRIBUTING now forbids. Chunking therefore cannot be a
   global default: it has to be a **declared per-tool property** ("this batch is
   safe to apply partially, and here is what a partial application means"), with
   the receipt from 5.2 recording exactly which chunks committed. Any story that
   chunks a command without declaring that is a regression, not an improvement.
4. **`approval_mode: revit_ui` (5.4) would deadlock the unattended paths we just
   built.** The `-WriteProbes` tier and the release gate run with nobody at the
   keyboard by design; a panel waiting for a human hangs them. So it must be a
   permission-profile-level opt-in with a documented exemption for the harness —
   otherwise turning trust on turns verification off.
5. **The release-channel confusion (5.10) is already handled in practice**: on
   2026-08-04 `latest` was deliberately kept on v0.6.0 because v0.6.1 ships no
   installer, precisely so a script following `latest` keeps working. What is
   missing is the *written policy*, not the behaviour.

### Evidence the matrix already has, for free

Today's session produced two of 5.5's dimensions unprompted: family-template
names are localized (a hardcoded English name finds nothing), and in a Spanish
model `Level` is **ambiguous** on some elements — "2 parameters share it; use a
BuiltInParameter token or GUID". Language is not a cosmetic axis.

---

## Suggested order
`5.1 → 5.2/5.3 → 1.1–1.3 fixed → 0.1/5.7 → 5.5 → 4.0/4.1 → 5.4/5.6 → 3.1 → 5.8/5.9/5.10 → 2.x`

Read the previous order as superseded from here down. 5.1 comes first because
every other guarantee is written on top of "the thing you approved is the thing
that ran", and that is the one link currently missing. Receipts and honest
annotations are cheap and unblock auditing. Epic 1's commands still have to work.
Signing opens the market; the matrix is what makes "verified against real Revit"
a claim someone else can reproduce.

A standards layer over write paths that do not verify would be judgement resting
on measurement nobody checked, so Epic 1's commands are fixed before Epic 4 is
built on them. Epic 4 is almost entirely read-only and can run in parallel; only
4.7 has to wait. The platform jump (Epic 2) waits until Epics 1 and 3 have
validated the approach.

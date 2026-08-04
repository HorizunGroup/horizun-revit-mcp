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
rules are welded in — Horizun's naming, Prodesa's catalogue, PRODESA CLASS — so a
new client means rewriting a skill.

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
| 4.2 | Classification coverage against a supplied table: missing codes, and codes that are not a last-level leaf. Generalises the Prodesa Class audit to OmniClass / Uniclass / IFC class with no code change | M | 4.1 |
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

---

## Suggested order
`0.1 → 1.1–1.3 fixed → 3.1 → 4.0/4.1 → 3.2/3.3 → 3.4 → 2.x`

Signing opens the market. Epic 1's three commands are written and reviewed but
none can currently do its job, so fixing them comes before anything is built on
top — a standards layer over write paths that do not verify would be judgement
resting on measurement nobody checked. Provenance unlocks real time. Then the
requirement-set schema, with all three standards loaded as documents. The platform
jump waits until Epics 1 and 3 have validated the approach.

Epic 4 is almost entirely read-only, so it can run in parallel with the Epic 1
fixes; only 4.7 has to wait for them.

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

---

## Suggested order
`0.1 → 1.0 → 1.1 → 3.1 → 3.2/3.3 → 3.4 → 2.x`

Signing opens the market; the sprinkler command proves the typed-command model;
provenance + drift also unlock real time; the platform jump waits until Epics 1
and 3 have validated the approach.

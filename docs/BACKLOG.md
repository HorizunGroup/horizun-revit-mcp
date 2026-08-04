# Horizun Revit MCP — Backlog

Working backlog for the next phases. Sizes are rough: `S ≈ days · M ≈ 1–2 weeks
· L ≈ weeks`. ⭐ marks the recommended entry point of each epic.

**How to work this:** one branch per story (`epicN/short-name`), a PR into `main`,
never a direct commit to `main`. Build and run the tests before opening the PR.
Read [AGENTS.md](../AGENTS.md) first — it loads the project rules every session.

---

## EPIC 0 — The unsigned-add-in dialog *(purchase dropped 2026-08-04)*

**0.1 is dropped: no certificate is being bought.** That decision stands, and this
epic is rewritten around it rather than left looking alive.

What was MEASURED on 2026-08-04, because it corrects an earlier wrong conclusion
in this very file's history: Revit's "Always Load" trust is keyed to the **binary**,
not to the AddInId. With all 18 add-ins on a machine marked trusted in
`HKCU\...\Autodesk Revit <year>\CodeSigning`, Revit still prompted for exactly the
two whose DLL had changed that week and stayed silent for the one untouched since
June. Perfect correlation with the file date. So `scripts/trust-addin.ps1` is useful
(it writes the record, `-Report` lists who will prompt, `-Revoke` undoes it) but it
does **not** end the dialog after a rebuild. Autodesk exposes no switch to disable
the warning - the Revit key holds nothing but `CodeSigning`.

That leaves exactly three honest options, and no fourth:

| ID | Story | Size | Dep |
|----|-------|------|-----|
| ~~0.1~~ | ~~Buy an OV cert and sign in CI~~ — **dropped by the owner** | — | — |
| 0.2 ⭐ | **Self-sign, free**: `New-SelfSignedCertificate` + `Set-AuthenticodeSignature` (both already on Windows), certificate into Trusted Publishers, DLLs signed at install. Ends the dialog **permanently on machines that trust that certificate** — which is the team's own machines. Trust moves to the certificate, so a rebuild no longer re-prompts | M | — |
| 0.3 | **Stop changing the DLL on the machine you work on**: install a release and do not rebuild there. The dialog only returns when the binary changes — six reinstalls in one morning is a development loop, not daily use | S | — |
| 0.4 | *(only if 0.1 ever revives)* Verify on a CLEAN machine that the dialog is gone — a machine that already trusts the certificate proves nothing | S | 0.1 |

0.2 is the recommendation and it is not the same thing as 0.1: a purchased
certificate exists so that **other people's** machines trust the build without
installing anything. Self-signing costs nothing and solves it for ours. Installing a
certificate into Trusted Publishers means anything signed with it is trusted, so it
is the operator's decision to make deliberately, not a script's to make quietly.

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

### Story 1.1 — five live iterations, what is now KNOWN (2026-08-04)

Not seated yet. Every line below is measured on a real RCI model, and each one
falsified a theory rather than supporting one — the placement report the command
now carries is what made that possible.

| Read | Behaviour | Use it for |
|---|---|---|
| `Connector.Origin` (raw or via `GetTransform().OfPoint`) | A family-definition **constant** (-25.4 mm) whatever the aim, before and after a 22.5 m move | `ConnectTo` only — **never** position |
| `LocationPoint` | Reads the level (-0.0) and does **not** move; `MoveElement` Z is discarded | Nothing, on this family |
| Bounding box | Tracks reality (the designer's seated heads report z 54,848 mm) | **Measurement** |
| `Offset from Host` | `Set()` accepted with no exception, **read back 0.0** — reverted on regeneration | Nothing yet — see below |

Two traps this cost, worth remembering:

1. **A broken ruler falsifies a good lever.** Iteration 2 (writing the offset
   parameter) was reverted because the run "showed" it changed nothing — measured
   with the connector's own Origin, which *could never* show a change. Re-instating
   it with a working ruler proved the lever is also broken, but the first rejection
   was not evidence.
2. **`Set()` returning without throwing is not a write.** Only the read-back is.

**Next measurement, not next guess:** the designer's own heads carry
`Host = "Level : Cubierta"` *with* a 4.16 m `Offset from Host`, so that value is
legal on a level-associated instance. Something about how this command creates the
instance refuses it. Compare the two instances — placement type, host association,
`Level`/`Schedule Level`, whether the offset only takes after `ConnectTo` — and
measure the difference before changing anything.

Connection is unaffected: **37/37** `connected_to_intended_target`. The rollback
rule held on all five failed runs; the model is untouched after every one.

**RESUME HERE (saved 2026-08-04):** the comparison experiment is a script, not a
memory — `scripts/probe-sprinkler-compare.ps1` on `fix/1.1-seat-heads` (tip
`aeb13c9`). It creates ONE instance that survives (via `create_elements`, which
verifies presence but not seating), compares it parameter-by-parameter against
designer-seated head `2802224`, and writes the offset OUTSIDE the placement
transaction through `write_params_verified`, whose read-back settles whether the
parameter reverts in general or only inside `place_sprinklers`' transaction.
Before running: **pre-migrate the fixture once** — open the source model, let
Revit upgrade it, save that copy, reuse it. Re-upgrading the same model every
cycle cost a morning of 3–10 minute opens. `scripts/live-cycle.ps1` +
`scripts/trust-addin.ps1` make the rest of the loop unattended.


### The gap batch work keeps hitting: no way to open a document

Noticed 2026-08-04 from an agent working families: *"there is no command to open
documents in this build, so it is one instance per family. I close the previous one
so as not to fill the machine with Revits."* The fact is right — this build
publishes 42 tools and none of them opens or closes a document — and the workaround
is the expensive one: killing and relaunching Revit costs minutes per file, which is
how an afternoon disappears.

The cheap pattern already exists and is proven 5/5 in `scripts/live-cycle.ps1`: one
live instance, files handed to it by shell, which detaches a workshared central
without prompting. But that is a script working around the bridge, not the bridge
doing its job.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 1.6 | `horizun_document_session`-style open/close, so batch work is ONE instance and many files instead of one instance per file. Turns folder audits, family sweeps and the verification harness from minutes-per-file into seconds | M | tool freeze lifted |

**Held by the Epic 5 tool freeze on purpose.** Epic 5 froze new typed commands
until 5.1–5.4 land, and adding one the same day that rule was written would make the
rule worth nothing. It is a real gap, it is written down, and it waits.

### The second gap real use found: no 2D drafting geometry

Reported 2026-08-04 by an agent doing electrical documentation: *"the available tool
does not expose creating `DetailCurve`/`DetailLine` in Revit, so I cannot redraw
those lines through the current bridge without an external tool or manual
intervention."*

Verified against the contract: `DetailCurve`, `DetailLine` and `detail_line` appear
**nowhere**. `horizun_create_elements` publishes 14 kinds — level, grid, wall, floor,
ceiling, roof, room, family_instance, structural_framing, structural_column, duct,
pipe, conduit, cable_tray — and none is drafting geometry. `horizun_annotate` covers
text notes, tags and dimensions, which is annotation, not linework.

So the bridge can model a building and annotate it, and cannot draw a line on a
sheet. Anything that finishes a drawing — legends, schematic risers, detail
enhancement, symbols made of lines — stops at the bridge and goes back to a human.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 1.7 | Detail lines and curves in a view: `detail_line` / `detail_curve` kinds on `create_elements`, view-scoped, with line style passed as an argument (never a compiled-in style list). Verified by re-reading the curve's geometry and its owner view after commit | M | tool freeze lifted |

**Worth noting about the report itself:** the agent said what it could not do instead
of drawing something else or claiming success. That is the product working — a
refusal is the honest answer to a missing capability. The gap is real; the behaviour
around it was right.

**A pattern, after three of these in one day.** Real use found three gaps the backlog
had not predicted: no way to open a document (1.6), no drafting geometry (1.7), and a
rename step that identifies types by name (5.11, from the same session). Gaps found
by use are worth more than gaps predicted by planning — and the freeze should be
lifted deliberately once 5.1–5.4 land, with these three first in the queue, rather
than punctured one command at a time whenever somebody hits one.

## ~~EPIC 2 — Unified bridge contract~~ *(DROPPED 2026-08-04)*

**Dropped by the owner.** It was the only epic whose work lives in a DIFFERENT
repository (`civil3d-bridge`), so it could never be finished from here — and a
shared contract is worth writing only once the reference implementation has
settled. Epic 1's commands are still being proven against real models.

What was worth keeping from it, moved rather than deleted:

- The transport and verified-write rules it would have specified are already
  written down and enforced here: `docs/security-model.md`, the confirmation and
  stale-plan machinery (5.1), and the rollback rule in CONTRIBUTING.
- Federated health (was 2.4) is a small read-only addition that never needed the
  contract; if it is wanted it comes back as its own story.

Re-open it deliberately, not by drift: a bridge contract written before this one is
verified would freeze today's mistakes into three products instead of one.

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
| 4.0 ✅ | **DONE 2026-08-04** — `docs/requirement-set.md`. The artifact schema, general enough that ISO 19650, IFC/buildingSMART and COBie are three **documents** rather than three code paths | M | — |
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
| 5.1 ◐ | **MECHANISM + 6 COMMANDS DONE 2026-08-04** — `manage_system_types`: a duplicate INHERITS everything not overridden, so the plan carries what each named parameter reads on the SOURCE now (AsValueString, the value the person saw) — a source renamed or edited under the rehearsal refuses as stale; inherited-but-unnamed parameters are explicitly NOT frozen and the note says so. 8 left. Previously: **MECHANISM + 5 COMMANDS DONE** — `bind_shared_param` (the command whose rehearsal once fell through into the write) now refuses when insert has silently become reinsert: if somebody binds the parameter first, the same request REPLACES the category list instead of inserting, and the plan catches it. Dropped-category count rides in `ExpectedCascadeCount`. 9 left. Previously: **MECHANISM + 4 COMMANDS DONE** — `family_apply` closes the gap its own rehearsal named: each resolved row now carries the value the parameter reads NOW, so overwriting somebody else's change refuses instead. Added `ResolvedPlan.ContextFingerprint` for state a plan depends on WITHOUT being one of its elements — family_apply measures only the ACTIVE type's shape, so a rehearsal taken with a different type active approved a check of a different shape. 10 left. Previously: **MECHANISM + 3 COMMANDS DONE** — `set_keynote` closes the gap its own rehearsal used to only WARN about: the plan carries the keynote each resolved TYPE reads right now, so a colleague re-coding it makes the apply a `stale_plan` instead of a silent overwrite, and the collateral count rides in `ExpectedCascadeCount`. `PlanWiringTests` now guards the mixed state itself — a plan recorded but never compared fails the build, and the gate cannot stop disclosing the limit while any command is unwired. 11 left. Previously: **MECHANISM + 2 COMMANDS DONE** — `transform_elements` too, the shape CONTRIBUTING points new commands at, with a rounded bounding-box fingerprint so an element somebody else moved reads as a different plan. 12 commands still stamp a token without a materialised plan, and each SAYS so in its own reply. Originally: **MECHANISM DONE + FIRST WIRE LIVE-PROVEN**: write_params binds its token to the resolved values; the stale_plan refusal and the survives-refusal property both verified against a real model. Remaining: wire the other typed writes (delete's multi-pass purge needs care). Bind the confirmation token to the RESOLVED ELEMENT SET, not the request.** Materialised plan: UniqueIds, before-values, types/levels/hosts, geometry or bbox fingerprint, expected create/modify/delete counts, cascade effects, document fingerprint + Revit version. Re-fingerprint at apply; mismatch returns `stale_plan`. Ordered hash / Merkle for large sets | L | — |
| 5.2 ◐ | **RETENTION/REDACTION DONE 2026-08-04** (15 tests; malformed settings never delete, caps drop oldest). Remaining: receipt payload + ledger writer wiring + DPAPI. Ledger → operation receipts: `operation_id`, tool, model + plan + request hashes, user, timings, affected UniqueIds, warnings, verification and transaction outcome. DPAPI at rest, configurable redaction, retention (7/30/90/manual) with a size cap and a purge tool, JSONL export, correlation ids across server/pipe/Revit | M | — |
| 5.3 ✅ | **DONE 2026-08-04.** Derive `destructiveHint`/`openWorldHint` from the contract**, not from hardcoded tool-name lists in `Tools.cs`. A new tool currently gets wrong annotations unless someone remembers to edit two `if` chains — the same welded-in shape Epic 4 exists to remove | S | — |
| 5.4 | **Human approval inside Revit** (`approval_mode: revit_ui`) for delete/save/export/family-replace/`execute_python`: non-modal panel naming document, tool, change summary, element count, external destinations, irreversible effects. Plus a ribbon-driven temporary unlock for `execute_python` (10–15 min, active document only, revoked on close/switch, optionally script-hash scoped) | L | 5.1 |
| 5.5 | **Live certification matrix 2023–2027** published per release: JUnit/HTML report, exact Revit build, fixture hashes, tools covered and not, time and peak memory, sanitised warning log. Dimensions: year × language × units × model kind (non-shared/local/central/detached) × links × size × discipline × outcome (commit/rollback/refusal/crash recovery). Needs public synthetic fixtures | L | 5.1 |
| 5.6 | **Chunked long operations** with cooperative cancellation, per-turn UI budget (100–250 ms), phase + percentage progress, safe checkpoints between batches, `submit_job` mapped to native MCP tasks. Benchmarks published as *longest continuous UI block*, not just total duration | L | 5.1 |
| 5.7 | **Sign the installer and binaries**: OV/EV Authenticode over exe/dll/installer/packaged scripts, timestamping, GitHub build attestations, signed SBOM and manifest, verification during install. Certificate trust stays an IT decision (Intune/GPO) — never installed silently | M | 0.1 |
| 5.8 ✅ | **NEGOTIATION SLICE DONE 2026-08-04** (golden-tested; 2026-07-28 RC guarded by a failing test). Remaining: full adapter, SDK conformance, client matrix, execution.taskSupport. Isolate the protocol behind a `ProtocolAdapter` independent of the Revit domain: conformance tests against the official SDK and MCP Inspector, golden JSON-RPC request/response tests, explicit per-version negotiation, client compatibility matrix, schema deprecation policy. Add `execution.taskSupport`. Do this BEFORE adopting 2026-07-28, which is still RC | M | — |
| 5.9 ✅ | **DONE 2026-08-04.** Public governance: issue templates (bug / proposal / compatibility) that demand Revit version+build+language, Horizun version, MCP client, tool, document kind, expected vs observed, sanitised logs. Discussions, public roadmap and milestones, labels, review SLA, a second maintainer with release rights, ADRs | S | — |
| 5.10 ✅ | **DONE 2026-08-04** — `docs/RELEASE-POLICY.md`. Release channels and the road to 1.0: `stable` (signed + matrix approved), `preview`, `validation-only` (no new binaries). Publish SemVer policy, schema compatibility, deprecation window, config migration, back-version support, and an explicit definition of "production ready" | M | 5.5, 5.7 |

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

### 5.8 was already mostly built — the review assumed otherwise (checked 2026-08-04)

The review asked to "isolate the protocol layer behind an adapter". Measured: it is
already isolated. `ProtocolNegotiation.cs`, `Errors.cs` and `Wire.cs` are separate
files that compile with no Revit reference and carry **32 tests of their own** (5 + 9
+ 18) covering negotiation across all four supported protocol revisions, the JSON-RPC
error codes, an unknown method, a request whose id cannot be echoed, a wrong
`jsonrpc` version, and that listed tools publish modern schemas and annotations. The
schema-deprecation policy the review also asked for was written the same day into
`docs/RELEASE-POLICY.md` (two-MINOR window, no field ever repurposed).

What was genuinely missing was one field, now emitted: `execution.taskSupport`,
derived from the contract rather than from a second list — `optional` for anything
that forwards to Revit (exactly the set `horizun_submit_job` accepts, and a model scan
does outlive a request), `forbidden` for host-resident tools and for the two
`submit_job` refuses by name. Advertising a task a caller cannot create is worse than
advertising nothing.

Still deliberately NOT done: adopting MCP revision 2026-07-28, which is still RC
upstream. The isolation that makes adopting it safe is what already exists.

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
`1.1 seated → 1.2/1.3 → 5.1 wiring → 5.5 → 4.0/4.1 → 5.4 → 3.1 → rest of 5.2/5.8`

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

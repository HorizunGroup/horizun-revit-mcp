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
| 0.3 ◐ | **AUTOMATED HALF DONE 2026-08-04** — `install.ps1` now re-signs the fresh binaries automatically when this user's certificate already exists and is trusted (no new trust is ever minted as a side effect; without a cert it prints the one command and WHY it will not run it for you). The human failure this removes: every install re-armed the dialog because re-signing was a separate step people forgot. The practice half stands: on a daily-use machine, install releases, do not rebuild | S | 0.2 |
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

### The second field report (2026-08-04, evening) — a full session over a 2,616-wall tower

The most valuable review yet, because every number in it was measured against real
use. What worked is recorded in its own words — the `federated_coverage` block turned
a would-be wrong answer (2,616 walls reported as the whole tower) into a right one by
naming `NOT ESTR.rvt` as **NotFound, not Unloaded**, a 2,350-element error that never
happened. The gaps, as stories:

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 1.8 ◐ | **DELIVERED 2026-08-04 (needs live verification)** — `group_by` (category/level/type/family/source_model/source_kind) + `sum_parameters` on `query_model`: groups computed over the WHOLE matched set in one call, no rows, no cursor (the combination is refused, not ignored). Each sum shows its own arithmetic — summed/absent/unreadable/non_numeric and a `complete` flag — because a sum over half a group must never read like a sum over the group, and that substitution is easiest to commit inside an aggregate, where the rows that would show the gap are exactly what the caller asked not to see. Bounded at 500 groups. The reporter's three PowerShell scripts become one call. Was: **Server-side aggregation** — `group_by: ["type","level"]` with `count` and `sum(param)` on `query_model`/`list_elements`. Measured need: every query in the session ended in group-and-count, none possible server-side; the workaround was query → token overflow → dump to disk → `ConvertFrom-Json` → `Group-Object`, **three times**. A histogram request should be one call, not five paginated calls plus a script. The single biggest missing piece for an agent caller. Parameter on existing tools — not a new tool, no freeze conflict | L | none |
| 1.9 ✅ | **DELIVERED 2026-08-04 (needs live verification)** — `parameter_format:"compact"` (name:raw, ~5x smaller per parameter; absent/unreadable move to a per-row `parameter_issues` — compact is a diet, not an amnesty) and `return_fields` (identity/federation fields only when named; `element_id` always). Columnar output DECIDED AGAINST, not deferred: compact+return_fields already beats the ~60% the columnar reshape promised, and a second answer shape is a second way to misread one. Was: **Row payload diet** — measured ~741 chars/row to read three numbers per wall (500 walls = 370,299 chars). Three fixes, smallest first: `parameter_format:"compact"` (`"CURVE_ELEM_LENGTH": 19.357` instead of the 5-field object); a projection parameter to omit identity/federation fields when not wanted; columnar output (header + arrays), which alone cuts ~60% | M | none |
| 1.10 ✅ | **DELIVERED 2026-08-04 (needs live verification)** — `LevelName` now reads the level wherever the category keeps it: `WALL_BASE_CONSTRAINT`, `FAMILY_BASE_LEVEL_PARAM`, `RBS_START_LEVEL_PARAM`, then the plain params. Same read feeds the `level:` filter, so both the false summary and the useless filter close together; the filter's contract description now says what it matches. Was: **`by_level` states a falsehood for walls** — reports `"(no level)": 2616` with the shape of a fact, when walls carry their level in `WALL_BASE_CONSTRAINT`. For a tool whose contract is *never report what you did not verify*, this is the worst clash in the report: "these elements have no level" is FALSE. Either resolve the base constraint for walls or answer `not_applicable_for_category` — never a false zero. Same fix documents the `level:` filter's wall behaviour in the parameter description (today it takes insider knowledge: a `parameters` predicate on `WALL_BASE_CONSTRAINT` with the exact level name) | M | none |
| 1.11 ✅ | **DELIVERED 2026-08-04 (needs live verification)** — `bridge_queue` now carries `waited_on`: with nothing ahead in the FIFO, none of the wait was queue — it was Revit not yet idle (warm-up or a modal dialog), and the field says so. Was: **`waited_ms` mislabels warm-up as queueing** — first `horizun_health` measured `waited_ms: 25014` with `queued: false`, `ahead_at_admission: 0`: 25 s waiting in a queue it says it never joined. Subsequent calls 60–900 ms. Name add-in warm-up as its own field instead of counting it as queue wait — a measurement labelled with the wrong cause is the house failure mode | S | none |
| 1.12 ✅ | **DELIVERED 2026-08-04 (needs live verification)** — `is_revision_schedule` per row, `include_revision_schedules` filter (default true: nobody's count changes under them), and `revision_schedules_in_document` reported even when excluded — ESPECIALLY when excluded. Was: **`list_schedules` noise** — 28 of 49 rows were `<Revision Schedule>`. Add `is_revision` per row and an `include_revision_schedules` filter (default true, so behaviour does not change under anyone) | S | none |

Fixed same day, from the same report: the v0.6.1 tag / `<Version>` 0.6.0 drift (now a
CI rule: the two projects version together, and a tagged build agrees with its tag),
and the overstated .NET 4.8 targeting-pack prerequisite in `AGENTS.md` (measured: NuGet
restore suffices; 2024 compiled clean on a machine without the pack).

**What the report asked us not to touch:** the tool descriptions that explain WHY —
"the expensive failure is not a dead bridge, it is a healthy one connected to the
wrong instance" made the reporter call `horizun_target` before making that exact
mistake with two Revits open. The style is load-bearing; keep writing them that way.

**A pattern, after three of these in one day.** Real use found three gaps the backlog
had not predicted: no way to open a document (1.6), no drafting geometry (1.7), and a
rename step that identifies types by name (5.11, from the same session). Gaps found
by use are worth more than gaps predicted by planning — and the freeze should be
lifted deliberately once 5.1–5.4 land, with these three first in the queue, rather
than punctured one command at a time whenever somebody hits one.

### 5.11 — `family_apply`'s shape check keys types by NAME, so a rename reads as a moved shape

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 5.11 ✅ | **DONE 2026-08-05** (`eab6156`, merged to main; not yet deployed). Landed cleaner than the ElementId idea: the comparison receives a map of the renames the command itself PERFORMED (declared only when requested, not created, and the call did not throw), and pairs the before-shape under the after-name. Nothing is guessed — a type that vanishes without a declared rename still reports as removed, and a declared rename does not by itself make the verdict `changed`. Five regression tests, including the delete-hiding-behind-a-rename fear | S | — |

### From a full day of real homologation use — 2026-08-05 (9 Prodesa families, add-in 0.5.0→0.6.1)

Measured in the field, not predicted. Ordered by what cost time or nearly caused a wrong claim.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 5.12 ⭐ | **`family_apply save:true` must prove its save the way `save_document` does.** Today it reports `file_changed: null, sha256_before/after: null` ("file is being used by another process") on EVERY family — measured 9/9 — while `save_document` hashes the same situation fine (`saved_verified`, `bytes_changed_on_disk`). Port `save_document`'s hashing approach (its file-share mode) into `family_apply`'s save evidence, so the flagship command's proof is not weaker than the plain save's | S | — |
| 5.13 | **Closing the last document requires a decoy.** `close` correctly refuses the active document (Revit API cannot close it), but the only workaround is opening a document you do not want, three times in one session. Either an `activate` operation on `document_session`, or `close` accepting `activate_other: true` that picks another open document and REPORTS which one it activated. **Re-confirmed 2026-08-07 at batch scale**: twice more in a 123-model audit, and with a consequence the first report did not have — the last model of a 54-model batch stays open, so RELAUNCHING the batch skips it. The decoy dance is now the difference between a rerunnable batch and one with a hole in it | S | — |
| 5.14 | **`would_change` per row in `family_apply`'s plan.** A parameter already holding the requested value still shows in `params_would_set` and forces every caller to diff `before.value` vs `requested` themselves to avoid presenting a plan that appears to touch things it does not. One boolean per row, computed once, in the one place that already has both values | S | — |
| 5.15 | **An ACC upload-status command.** Copying into the Desktop Connector folder and hashing proves the LOCAL CACHE, not the cloud: the upload is a later async step that fails under throttling ("Too many people or processes…", ~11-minute circuit breaker) — measured 3 of 8 families silently unuploaded, caught only by a human screenshot. The WAL already answers "does this path have a folderUrn yet?" (external `extract_wal_links.py` proves it); make that a bridge command so publishing to ACC can be verified instead of asserted | M | — |
| 5.16 | **`other_clients_connected` in `horizun_health`.** Two agents on one machine: Revit 2025 died three times (journal ends mid-activity, no exception — killed from outside) while another agent recompiled and redeployed the add-in underneath the first. The bridge cannot see that another client is attached to the same instance; even an approximate count of concurrent pipe clients in health would have turned three journal autopsies into one line | S | — |
| 5.17 | **Declare units per field in `geometry_baseline`.** `solid_volume` and `surface_area` read back in ft³/ft² (verified exact against a 15×15×10 cm box), but `bbox_x/y/z` returned 656.17 — neither mm nor feet for that piece. The comparison is unaffected (same-unit before vs after), but the baseline misleads a human reader; name the unit on every dimension, and check whether bbox is even measuring the right extent | S | — |
| 5.18 | **A guard that measured nothing must not say `unchanged`.** Residual edge of the 5.11 lesson: `GeometryVerdict.Status` returns `unchanged` when both captures are EMPTY — zero types compared reads identically to a clean pass. An empty table is not agreement (the same rule verify-live's `All-Rows` already enforces). Decide the honest word (`unproven`?) and check every consumer before changing the string | S | — |

**Validated by the same session — do not regress:** the refusal to pick between two live
Revits (`horizun_target`), `execute_python`'s transaction policy (`IsModifiable` enforced,
4 hand-opened transactions came out clean), `save_as`'s `saved_evidence` prose, idempotency
keys ("retried without fear all day"), `bridge_queue.waited_on` distinguishing queue from
busy-UI, the three version guards (`expected_version` / `expected_revit_version` /
`rfa_path`), and the confirmation-token message naming that `save` binds too.

**Re-measured on v0.6.1, 2026-08-05, with a cleaner reproduction than the first.** The
request carried NOTHING but `family_name` + `keep_type` — no values, no shared
parameters, no junk sweep — and still rolled back:

```
geometry_check: "changed"   dimensions_compared: 0
  types_added:   ["PRD-CAJA_PASO-15x15x10cm_COM"]
  types_removed: ["Caja paso RITEL 15x15x10 cm"]
```

Nothing moved. The before-census measures the type **by name**; the rename the command
was asked to perform makes that name vanish, so the after-pass matches zero types,
compares **zero dimensions**, and calls it changed. `dimensions_compared: 0` next to a
verdict of `changed` is the tell: the conclusion was reached without comparing anything.

**The control, same session, same host:** seven INDIRED families where
`type_rename_would` came back `null` (their types were already canonical) all committed
clean — `geometry_check: unchanged`, **7 of 7** dimensions compared, every value
confirmed against the caller's. The rename is the sole trigger, and it is not an
optional flourish: renaming the surviving type to the canonical family name is one of
homologation's four defined outcomes. Today it has no path through the MCP, and the
family has to be finished by hand in Revit.

### From a day of unattended batch auditing — 2026-08-07 (123 models, Revit 2025 + 2026, v0.8.0)

Raw report: [feedback-from-use-2026-08-07.md](feedback-from-use-2026-08-07.md). Every claim
below was verified against this tree before being written down; the seats named are where the
mechanism actually lives, which is not always where the report guessed.

| ID | Story | Size | Dep |
|----|-------|------|-----|
| 5.19 ⭐ | **A request the bridge knows never started must not cost 600 s to say so.** Measured: a "New Project" dialog left open made three `horizun_health` calls wait the full `CommandTimeoutMs` (`Program.cs`, const 600000) each — 30 minutes to learn what the log knew in the first second (`(it never started)`, `Dispatcher.cs`). Three cuts, smallest first: (a) a short own timeout for `horizun_health` — it is the diagnostic command, and a slow answer IS the answer; (b) `timeout_ms` per call, clamped to the ceiling; (c) the good one: detect the pre-existing modal and return it as a RESULT, not a timeout — "Revit has `<title>` open; nothing was queued". Note the trap verification found: `Interference` only sees dialogs raised WHILE a command runs on the UI thread — a modal already open before the call means the command never reaches the UI thread, so today the one case that costs 10 minutes is exactly the one the dialog watcher cannot see. The detection has to happen off the UI thread (the transport side can see the process's windows; the UI thread is the thing that is stuck) | M | — |
| 5.20 | **`horizun_file_info(paths[])`: read `BasicFileInfo` from disk with NO document open.** Format/saved-version, `IsWorkshared`, `IsCentral` for a whole folder — the first thing every batch does, hand-written in `execute_python` every time today. The report's second finding folds in: format triage needed no document at all, yet an anchor project had to be created by hand because the bridge requires an active document even for work that never touches one. `BasicFileInfo.Extract` runs off a path; the command should not demand a document it will not use | S | tool freeze lifted |
| 5.21 | **`revit_said` is dropped on the async path — the sync path keeps it.** Verified seat: `Dispatcher.RunOneAsync` writes only `result.Data` into the job record (`work.Record.Result(SerializeObject(result.Data))`), while the sync path serialises `RevitSaid` in `PipeEnvelope`. So the dialog/warning telemetry that solved the day's hardest case ("Opening was canceled" → a `DocWarnDialog` the bridge cancelled, plus the errors Revit raised before it) exists for a synchronous call and vanishes for the same work submitted through `submit_job` — which is precisely how batches run. Four failed models went undiagnosed for exactly this. Carry `revit_said` into the job record and out through `job_status` | S | — |
| 5.22 | **`on_open_dialog: cancel \| dismiss` on the open paths.** Cancelling by default is right and stays the default. But 6 of 123 models cannot be audited unattended because their open raises a dialog whose only safe unattended answer is "acknowledge and continue" — and today there is no way to say so per call. A model that will not open unattended is still a FINDING; the point is that today its quality cannot even be measured. Scope: `open_document` and `document_session` open, read-only intent, every dismissed dialog recorded in `revit_said` as always | M | — |
| 5.23 | **`ScriptEvidence` rejects the status name the bridge itself teaches.** Verified: the classifier's switch accepts `verified\|completed_unverified\|partial\|failed` and its `default:` branch warns on anything else — so a script that declares `status: "self_reported_verified"`, the state name every doc and disclaimer popularises, is read as an UNKNOWN claim and downgraded with a warning naming a list that omits it. Accept `self_reported_verified` as a declared status with the SAME evidence requirements as `verified` (checked=true + non-empty evidence, else downgrade) — never a raised claim, just the vocabulary agreeing with itself | S | — |
| 5.24 | **The server never sweeps orphaned discovery files.** The add-in already does (`Discovery.SweepStale`, with its two deliberate refusals: legacy two-segment names never touched, any live pid keeps the file) — but it runs only when an INSTANCE PUBLISHES, i.e. when a Revit starts. Kill Revit and call again without starting a new one and the orphan (`revit-<year>-<pid>.json`) sits there for the server to trip over; the error message was praised as excellent, and it still describes a state the server could have cleaned. Run the same sweep, same refusals, from the server side at startup | S | — |

**Validated by the same session — do not regress:** `run_async` + `horizun_job_status` moved 54
models in 7 minutes and 69 in ~12 without losing one; the version guard refused to open another
year without `allow_upgrade`; resubmitting a batch returned the SAME `job_id` (idempotency doing
its job under a real retry); and the dialog telemetry on `open_document` solved the day's
hardest diagnosis — the complaint in 5.21 is that it exists in one place, not that it is wrong.

**A correction the reporter made against their own suspicion, worth keeping:**
`open_all_worksets` was NOT the cause of the opening failures — tested with `false`, they fail
the same. The docs' advice ("first thing to drop when a model dies on open") is right in
general and misled this particular day; evidence beats the heuristic, and the reporter measured
instead of believing it.

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
| 3.1 ◐ | **DELIVERED 2026-08-04 (needs live verification)** — `code_parameter` on `horizun_quantities`, org-neutral as AGENTS.md demands (the parameter NAME is the argument). Every row gains `code` with three non-values kept distinct — `(no such parameter)`, `(empty)`, `(unreadable)` — and a `by_code` rollup whose every sum states how many elements it covers, because a code whose volume summed 3 of its 40 elements is a fragment wearing the code's name, and Excel cannot tell once the number lands. Was: Provenance: every quantity row carries element IDs + `HRZ_COD_PRES` | M | — |
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
| 4.0 ✅ | **DONE 2026-08-04, acceptance test now EXECUTABLE** — `docs/requirement-set.md` + the three reference documents in `docs/requirement-sets/` (iso-19650, ifc-buildingsmart, cobie). `ReferenceRequirementSetTests` loads all three through ONE loop with no branch on which standard a file is — that absence IS the test — and every remediation is checked against the contract, because a set naming a tool the bridge does not ship is a standard smuggling in behaviour | M | — |
| 4.1 ◐ | **PURE HALF DONE 2026-08-04** — `RequirementSet.cs` + 16 tests: the loader with every refusal rule from `docs/requirement-set.md` (no-selector, unknown-operator-naming-the-known, comparing-without-value, unresolvable-table-at-load, duplicate-id, unknown-top-level-key), `is_leaf_of` over inline and CSV tables, and `Passes()` where unreadable CANNOT enter — the caller must report it as `unreadable`, because collapsing it into pass/fail would happen exactly there. Table resolver injected so the loader stays pure. JSON today; YAML is a parser dependency, which is a supply-chain decision the loader must not make by itself. REMAINING: the measuring half + the `horizun_check_requirements` tool itself — blocked on the tool freeze | L | 4.0, freeze |
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
| 5.1 ◐ | **ALL 14 COMMANDS WIRED 2026-08-04 — pending live verification** — the final three were the orchestrators. `create_family`: the TEMPLATE binds by content (SHA-256) — path, size and mtime all survive an edit that changes what the family becomes. `execute_plan`: inside the confirmed group the children's checks degrade to document identity, so the apply RE-REHEARSES every independent action read-only and refuses if any resolves differently — the same mechanism, composed, at the cost of running child dry-runs twice per apply. `recipe`: coarse on purpose — recipe SHA + the intended counts the person approved (the same numbers Verifications re-reads after commit); the full planned JSON is NOT hashed because prose that varies between honest runs would refuse every apply. What remains for ✅ is LIVE proof: a stale_plan refusal provoked against a real model for at least one command of each shape. Previously: **MECHANISM + 11 COMMANDS DONE** — `export`: sources by identity (a re-cropped view exports different content under the same id) and the destination's file-existence fact in `ContextFingerprint` — a file that appears under a no-overwrite approval refuses instead of destroying data the rehearsal promised not to touch. Existence only, not size/time: an export rewritten by its own previous run must not read as drift. 3 left (create_family, execute_plan, recipe). Previously: **MECHANISM + 10 COMMANDS DONE** — `create_schedule`: two ambient facts decide what one creation produces — which category the (possibly localized) text resolved to, and the name-collision fact, which is about the model NOW. Both in the plan; the apply re-runs both and the polite refusal wins over any race. 4 left (create_family, execute_plan, export, recipe). Previously: **MECHANISM + 9 COMMANDS DONE** — `annotate`: a tag approved against "Bomba 5" applied after somebody swapped that element is a label telling a reader the wrong thing IN PRINT — the quietest wrong answer a model can produce. Each row records view, target and type by identity AND name as resolved now. 5 left (create_family, create_schedule, execute_plan, export, recipe). Previously: **MECHANISM + 8 COMMANDS DONE** — `manage_views`: an id is a pointer, not a meaning. The plan records each referenced element's UniqueId AND name as resolved now (template, source view, titleblock, level), so an edited template or renamed source view refuses as stale even though the id still resolves; `RefIdFields` is one list so the plan builder and the schema cannot quietly disagree about what is a reference. 6 left. Previously: **MECHANISM + 7 COMMANDS DONE** — `create_elements`: a creation batch is NAMES, and none of their meanings is frozen by the request. The plan records what each name resolved to — type UniqueId, system type, level UniqueId AND its measured elevation to 0.1mm, because "create on N.E 10" approved a HEIGHT, not a word: a level moved 50mm is a different creation wearing the same words. 7 left. Previously: **MECHANISM + 6 COMMANDS DONE** — `manage_system_types`: a duplicate INHERITS everything not overridden, so the plan carries what each named parameter reads on the SOURCE now (AsValueString, the value the person saw) — a source renamed or edited under the rehearsal refuses as stale; inherited-but-unnamed parameters are explicitly NOT frozen and the note says so. 8 left. Previously: **MECHANISM + 5 COMMANDS DONE** — `bind_shared_param` (the command whose rehearsal once fell through into the write) now refuses when insert has silently become reinsert: if somebody binds the parameter first, the same request REPLACES the category list instead of inserting, and the plan catches it. Dropped-category count rides in `ExpectedCascadeCount`. 9 left. Previously: **MECHANISM + 4 COMMANDS DONE** — `family_apply` closes the gap its own rehearsal named: each resolved row now carries the value the parameter reads NOW, so overwriting somebody else's change refuses instead. Added `ResolvedPlan.ContextFingerprint` for state a plan depends on WITHOUT being one of its elements — family_apply measures only the ACTIVE type's shape, so a rehearsal taken with a different type active approved a check of a different shape. 10 left. Previously: **MECHANISM + 3 COMMANDS DONE** — `set_keynote` closes the gap its own rehearsal used to only WARN about: the plan carries the keynote each resolved TYPE reads right now, so a colleague re-coding it makes the apply a `stale_plan` instead of a silent overwrite, and the collateral count rides in `ExpectedCascadeCount`. `PlanWiringTests` now guards the mixed state itself — a plan recorded but never compared fails the build, and the gate cannot stop disclosing the limit while any command is unwired. 11 left. Previously: **MECHANISM + 2 COMMANDS DONE** — `transform_elements` too, the shape CONTRIBUTING points new commands at, with a rounded bounding-box fingerprint so an element somebody else moved reads as a different plan. 12 commands still stamp a token without a materialised plan, and each SAYS so in its own reply. Originally: **MECHANISM DONE + FIRST WIRE LIVE-PROVEN**: write_params binds its token to the resolved values; the stale_plan refusal and the survives-refusal property both verified against a real model. Remaining: wire the other typed writes (delete's multi-pass purge needs care). Bind the confirmation token to the RESOLVED ELEMENT SET, not the request.** Materialised plan: UniqueIds, before-values, types/levels/hosts, geometry or bbox fingerprint, expected create/modify/delete counts, cascade effects, document fingerprint + Revit version. Re-fingerprint at apply; mismatch returns `stale_plan`. Ordered hash / Merkle for large sets | L | — |
| 5.2 ◐ | **LEDGER WRITER + WIRING DONE 2026-08-04** — `ReceiptLedger` writes one JSONL per UTC day from the Dispatcher, copying only what each reply carried (absence stays absence — found and fixed: a JObject null-assign CREATES the property). A broken redact pattern WITHHOLDS the receipt rather than leaking it; retention never deletes today's file; failed appends are counted, never silent. Also found: `FromSettings` never read `receipt_redact_patterns` — RedactPatterns was unpopulatable dead code; now a JSON array or a single pattern. Remaining: DPAPI at rest (a dependency decision), purge tool, JSONL export surface. Ledger → operation receipts: `operation_id`, tool, model + plan + request hashes, user, timings, affected UniqueIds, warnings, verification and transaction outcome. DPAPI at rest, configurable redaction, retention (7/30/90/manual) with a size cap and a purge tool, JSONL export, correlation ids across server/pipe/Revit | M | — |
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

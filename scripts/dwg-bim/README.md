# DWG → BIM: reproducible route and evaluation harness

Everything here runs against a Revit started in an **isolated session** (the add-in built from this
tree, staged by `scripts/live/dev-addin-session.ps1`; the permanent installation is never touched) and
works on **new disposable models**. Nothing here reads or writes a client file.

## What belongs where

| Part | Where it lives | Why |
|---|---|---|
| Reading the drawing, planning, building, auditing, updating, decisions on held changes and split dependents, catalogue check | **The product**: `horizun_plan_from_cad` (incl. `catalog_check_only`), `horizun_apply_cad_plan`, `horizun_plan_cad_update` (incl. `dependent_decisions`), `horizun_apply_cad_update`, `horizun_audit_cad_model`, and the procedures `dwg-to-bim-unit` / `dwg-to-bim-update` run by `horizun_run_procedure` | A user needs these whatever the project; they verify their own writes and refuse what they cannot decide |
| Starting and stopping an isolated Revit, one input spec driving the procedure, results by dimension against a declared truth | **This folder** (`session.ps1`, `run_spec.py`) | Evaluation harness: it drives only public tools and edits no record by hand |
| An INDEPENDENT reference of a real drawing (its own reading of the raw dump), campaign batches, metrics | The private campaign harness (outside this repository) | It carries project data and must not travel with the code |

## Reproduce the synthetic unit

1. Generate the drawing (AutoCAD's console, `acad.dwt`; nothing is copied from a project):

   ```
   python scripts/dwg-bim/synthetic/make_synthetic_dwg.py <folder>
   ```

2. Write the MACHINE config (not versioned), `%USERPROFILE%\.horizun\dwg-bim-run.json`:

   ```json
   {
     "model_dir": "<where disposable models go>",
     "revit_template": "C:/ProgramData/Autodesk/RVT 2026/Templates/English-Imperial/Electrical-Default.rte",
     "drawings": { "synthetic-unit": "<folder>/SYNTH-UNIT.dwg" },
     "server_exe_file": "%TEMP%/hz_srv.txt",
     "records": "<where run records go>"
   }
   ```

3. Start the isolated session and run the spec:

   ```
   pwsh -File scripts/dwg-bim/session.ps1 start [-DeclineAddIn <unrelated unsigned add-in>]
   python scripts/dwg-bim/run_spec.py scripts/dwg-bim/synthetic/spec.synthetic-unit.json HZ_SYNTH_1
   pwsh -File scripts/dwg-bim/session.ps1 stop
   ```

`run_spec.py` prints the path of `result.json`: the build identity, the drawing and truth hashes,
the preflight, every procedure step, results by dimension per drawn symbol, the bridge's audit, the
pending items with their candidate id, the comparison after a real close and reopen, a replan of the
same drawing, and the acceptance verdict. Each call is recorded beside it.

## The spec

`spec.synthetic-unit.json` is the whole input: the drawing by NAME (the machine config says where it
is), link units, level, the walls and devices requirement sets (zone, catalogue, wall policy,
mounting heights, host faces), an optional versioned decisions file, and the acceptance criteria.
Every height, type and host mode in the synthetic spec is a **test hypothesis**, not a project
decision. A project spec is the same shape with the project's data.

## A wall split with doors and windows (split_openings)

A second synthetic case, for the update route: `synthetic/make_split_dwgs.py <folder>` draws SPLIT-A
(one 8 in wall) and SPLIT-B (the same wall with a 60 in gap) and their truth file. Add
`"split-A"` and `"split-B"` to `drawings` in the machine config (and, for several Revit years,
`"revit_templates": {"<year>": "<.rte>"}`), then per year:

```
pwsh -File scripts/dwg-bim/session.ps1 start -Year <year>
python scripts/dwg-bim/split_openings.py build HZ_SO_CLEAN
python scripts/dwg-bim/split_openings.py run HZ_SO_CLEAN
python scripts/dwg-bim/split_openings.py build HZ_SO_FAULT
pwsh -File scripts/dwg-bim/session.ps1 stop -Year <year>
pwsh -File scripts/dwg-bim/session.ps1 start -Year <year> -FailAction cad-update-move-0
$env:HORIZUN_TEST_FAIL_ACTION = 'cad-update-move-0'; python scripts/dwg-bim/split_openings.py run HZ_SO_FAULT; Remove-Item Env:HORIZUN_TEST_FAIL_ACTION
pwsh -File scripts/dwg-bim/session.ps1 stop -Year <year>
pwsh -File scripts/dwg-bim/session.ps1 start -Year <year>
python scripts/dwg-bim/split_openings.py recover HZ_SO_FAULT
pwsh -File scripts/dwg-bim/session.ps1 stop -Year <year>
```

`start` refuses while a session of that year is recorded: every `start` needs its `stop`.

`run` expects 12 verdicts true (rollback refused with Revit's reason and nothing written; an
impossible "stay" for a door refused in the plan; decided deletes before the re-shape; the whole
apply; the kept wall keeps its id on its piece; the new piece built; the door and window that stay
keep id, host and point; the removed ones gone; replan clean; reopen identical). With the fault,
`run` expects the partial state (deletes landed, wall untouched, kept openings intact) and
`recover` the same final state as a clean run.

## What the session script will and will not touch

`session.ps1` (`start | register | stop | status`) is a thin wrapper over
`scripts/live/owned-session.ps1`, which reuses the rules of `year-matrix.session.ps1`:

- **Process ownership** is pid + executable + start time, recorded before anything else. A pid that
  now belongs to another process is not ours. Any Revit of the same year that this run did not
  start - or a Revit whose year cannot be read - makes `start` refuse before the add-in is staged.
- **Document ownership** is a record of THIS run: `run_spec.py` (through `session_hooks.py`)
  registers each document it opens or saves-as by the path the bridge publishes. A title or an
  `HZ_` prefix proves nothing.
- **`stop` checks before it closes**: health must answer completely; every open document must be
  registered; each close is re-checked. A document the run did not register (the user opened one
  in this Revit) leaves the process running, sends no close, and writes a recovery-pending record.
- **Restore is verified** against the snapshot taken at `start`. A restore that fails is kept as
  `restore_pending` (and a recovery-pending record); running `stop` again finishes it. It is never
  reported as a clean stop. `status` only reads.
- One run per year: a file lock refuses a second concurrent `start`/`stop`.
- Dialogs: it declines only the unsigned add-ins named in `-DeclineAddIn` (in this process only) and
  closes only Autodesk's own "External Tool Failure" notice for its Insights add-in (Revit 2023,
  English or Spanish). It never kills a process and never answers a Save dialog.

The same recorded-Revit rules now back `scripts/live-cycle.ps1`, `scripts/live/verify-structure-matrix.ps1`
and `scripts/live/deploy-and-verify.ps1` (which also refuses, instead of killing, when an MCP
server runs the installed executable). `pwsh -File scripts/live/owned-session.tests.ps1` exercises
all of it without a Revit and without anybody's session.

Revit's interface language matters for NAMES, never for this route's inputs: a requirement set may
name a system wall type as `Basic Wall: <type>` in any language, and parameters are safest by
BuiltInParameter (`ALL_MODEL_MARK`), because their display names are translated.

## Checklist for another machine

1. Revit 2023–2027 (any subset), AutoCAD's `accoreconsole.exe` (the synthetic drawings), Python 3,
   PowerShell 7 and the .NET SDK of `global.json`; this repository at the candidate commit.
2. Generate the synthetic drawings (both scripts above) into a folder of your own.
3. Write `%USERPROFILE%\.horizun\dwg-bim-run.json` for that machine (paths only; nothing is copied
   from another machine).
4. Run the synthetic unit and split_openings per year as above; keep every `result.json`.
5. Compare by property, not by id: `candidate_id` derives from the drawing's bytes, so regenerated
   drawings give other ids and the same results.

# DWG → BIM: reproducible route and evaluation harness

Everything here runs against a Revit started in an **isolated session** (the add-in built from this
tree, staged by `scripts/dev-addin-session.ps1`; the permanent installation is never touched) and
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

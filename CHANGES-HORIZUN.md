# Horizun Hardening Layer — Changes in this distribution

**Horizun Revit MCP** is a modified distribution of
[`bimwright/rvt-mcp`](https://github.com/bimwright/rvt-mcp) by **Khoa Le**,
under the **Apache License 2.0** (see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE)).
The whole work stays Apache-2.0. This file records Horizun's modifications, as
Apache §4(b) requires ("carry prominent notices stating that You changed the
files").

> Independent, unofficial. Not affiliated with or endorsed by the upstream
> author or by Autodesk. "Revit" is a trademark of Autodesk, Inc.

## Why this layer exists

The chosen base is an excellent, mature C# Revit MCP — one UI-thread dispatch
pump, centralized schema validation, redaction and a broad handler set. What it
lacked (verified by grep and by the upstream's own "a modal dialog may be
blocking" timeout message) were the exact failure modes that froze or corrupted
long automation runs. Horizun adds a thin, centralized hardening layer for
them. Every technique is standard/public (The Building Coder `DialogBoxShowing`,
`Revit.Async`/ExternalEvent job patterns, `IFailuresPreprocessor`); no
third-party GPL code is used.

## New files (original Horizun contributions, Apache-2.0)
- `src/shared/Infrastructure/McpDialogGuard.cs` — global `DialogBoxShowing` +
  `FailuresProcessing` suppression, gated by `IsMcpExecuting` so only MCP
  commands are affected, never the interactive user's dialogs.
- `src/shared/Infrastructure/McpJobRegistry.cs` — process-wide async job store
  (submit→job_id→poll backbone).
- `src/shared/Infrastructure/McpAsyncRouter.cs` — (B) transport-thread router:
  `job_status` polls are answered straight from the registry (never queued
  behind the job they poll), and `"async": true` submits return a `job_id`
  immediately while the real work runs on the UI pump.
- `src/shared/Infrastructure/SchemaCoercion.cs` — (D) tolerant-reader pass:
  repairs numeric/boolean strings and camelCase↔snake_case key drift BEFORE
  validation, conservatively (only where the schema asks for a non-string type,
  and only when a key match is unambiguous), so a call that was obviously meant
  to succeed isn't rejected on a round-trip.
- `src/shared/Infrastructure/HorizunGuard.cs` — the contract that keeps a handler
  from reporting work it did not do. `Commit()` throws when Revit answers
  anything other than `Committed` (Revit rolls a transaction back and returns a
  status rather than throwing, so `t.Commit();` with the result discarded is a
  handler that reports success on an empty write — we measured one that claimed
  758 purged types while nothing was written). `Verify()` compares intended vs.
  actual counts, `Reconcile()` reports two sources of a quantity side by side
  instead of picking one, and the unit constants end the ft³-reported-as-m³ class
  of error. Every rewritten handler is built on this.
- `src/shared/Handlers/Horizun/HorizunExecutePythonHandler.cs` —
  `horizun_execute_python`: Python against the live Revit API on the UI thread,
  with `doc`/`uidoc`/`uiapp`/`app` injected. Two things the IronPython bridges we
  have used do not do: the **standard library ships** (`import json`, `re`, `csv`,
  `datetime` all resolve — bridges that omit it force you to hand-roll JSON with
  string joins), and **it cannot leave a transaction open** (a script that throws
  inside one is rolled back in a `finally`, instead of poisoning every later
  command with "Modification of the document is forbidden"). 228 typed tools will
  never cover the whole API; this does.

## Modified files
- `src/shared/Infrastructure/McpEventHandler.cs` — wrap `command.Execute` so
  (A) modal suppression is active only during on-UI-thread execution, and
  (C) a transaction left open by a handler is detected and logged the instant
  the command returns (the dialog guard also auto-dismisses the resulting
  "transaction discarded" modal instead of freezing the pump). Also (B) marks a
  job `Running` when the pump picks it up, and (D) runs `SchemaCoercion` before
  validating/executing (the original params are still what gets logged).
- `src/plugin-r22..r27/App.cs` — subscribe/unsubscribe `McpDialogGuard` next to
  the existing `Idling` hookup, and route async/job_status via `McpAsyncRouter`
  before the normal enqueue path (one line each).
- `src/plugin-r22..r27/RibbonSetup.cs` — ribbon panel labelled "Horizun MCP".
- `src/plugin-r22..r27/RvtMcp.*.addin` — add-in manifest Name/VendorId/
  VendorDescription rebranded to Horizun (AddInId GUID and class refs unchanged).
- `src/server/Program.cs` — (B) `SendToRevit` carries an optional `async` flag
  (wire-identical to the base when false); two new `meta` tools
  `revit_submit_async` and `revit_job_status`; server identity rebranded to
  "Horizun Revit MCP" (`horizun-revit-mcp`, 0.6.0-horizun.1).

## Removed from this distribution
These documented the **upstream's own project**, not the shipped product, so they
are not carried here. None of it is code, and none of it affects attribution —
the upstream's copyright, LICENSE and NOTICE are retained in full, as Apache-2.0
requires.
- `benchmarks/` — the upstream's v0.1.0 baseline run and its prompt template.
  Baselines are per-build and per-language; ours would have to be established fresh.
- `docs/analysis/` — the upstream author's internal session logs, backlogs and
  product-decision closeouts.
- `docs/roadmap.md` — the upstream's roadmap and distribution channels (NuGet
  global tool, upstream release ZIPs), none of which apply here.
- `CHANGELOG.md` — the upstream's release notes, written against its own version
  line and `BIMWRIGHT_*` environment variables.
- `CLAUDE.md` — the upstream's internal agent instructions.
- Vietnamese sample data in `docs/kei-equipment-import.md` translated to English.
  The `nameVN`/`specsVN` fields stay: they are part of the KEI schema's bilingual
  contract, not prose.

## Distribution / editorial changes
- `README.md` rewritten for the Horizun distribution (hardening-layer feature
  table, build-from-source install, credits); upstream's translated READMEs
  (`README.ja.md`, `README.vi.md`, `README.zh-CN.md`) removed because they
  describe the upstream product and would go stale here.
- `server.json` re-identified as `io.github.HorizunGroup/horizun-revit-mcp`
  (the upstream NuGet package claim was removed — it belongs to the upstream author).
- `SECURITY.md` / `CONTRIBUTING.md` repointed at this repository (upstream is
  also credited for shared-code vulnerabilities).
- New handler `open_document` (open/activate any `.rvt`/`.rfa`, detach/audit) and
  an upstream bugfix: `export_ifc` now runs inside a committed `Transaction`
  (modern Revit's IFC exporter writes element GUIDs and throws without one).
- `src/shared/Security/AuthToken.cs` — the discovery file (`revit-YYYY.json`) was
  written once at startup and never looked at again, so if anything removed it
  mid-session — antivirus, a temp sweep, a careless delete — the plugin stayed
  healthy and listening while every client silently failed to find it, and the
  only cure was restarting Revit. We hit this ourselves. The last payload is now
  kept and the Idling loop puts the file back; a deliberate shutdown still clears
  it so a dead plugin is not resurrected.
- `src/plugin-r22..r27/*.csproj` — reference `IronPython` + `IronPython.StdLib`
  3.4.2, and `scripts/stage-plugin-zip.ps1` ships both the runtime DLLs and the
  640-file stdlib. The staging script warns loudly if the stdlib is ever missing:
  without it `horizun_execute_python` loads fine and then fails on `import json`.

## Validation
- All six plugins (r22–r27) and the server build clean (0 errors).
- `SchemaCoercion` — 13/13 assertions on the shipping source, including the
  no-corruption invariants (numeric-looking strings on string fields stay
  strings; non-parseable values and already-present canonical keys are left
  untouched).
- `McpJobRegistry` — 8/8 lifecycle assertions (pending→running→done, error path,
  no state regression on a completed job, null-safety, bounded prune).

## Attribution
Base: **Khoa Le — `bimwright/rvt-mcp`** (Apache-2.0). Hardening layer:
**Horizun**. Whole work remains Apache-2.0.

## v0.6.1-horizun.1 — the honesty is now tested, not just claimed
- `LIES.md` — the five measured lies (758 purge, keynote that re-coded 51, 42.7%
  volume, clash that ignored links, chapter accepted as a leaf), each linked to
  the guard that catches it and the test that pins it.
- `HorizunReconcile.cs` — the Revit-free arithmetic behind `horizun_quantities`,
  split out of `HorizunGuard` so it is unit-testable without Autodesk assemblies.
  `HorizunGuard` delegates to it; the response JSON is unchanged.
- `HorizunReconcileTests` (10) + `HorizunCatalogLookupTests` (11) prove the
  quantities reconciliation (the measured 42.7% gap is flagged, matching sources
  agree at 0%, `Verified(758,0)`/`Verified(1,8)` are false) and the catalog leaf
  rule (a 3-segment code that is a parent is refused; a non-existent code is
  `is_leaf: null`, not `false`; provenance on every response; a missing/empty
  catalog is a hard error). They run in the existing `tests-xunit` CI job.
- Fixed two confirmed lies in inherited handlers: `create_schedule` now commits
  through `HorizunGuard.Commit` (a rolled-back schedule can no longer report an
  id and a name), and `ai_element_filter` converts to the document's actual
  display units instead of a hardcoded millimetre (an imperial-model filter no
  longer compares against the wrong unit in silence).
- Safety fix caught by a test: the `horizun` toolset carries write tools, so it
  is now in `WriteCapable` and `--read-only` strips it — previously read-only
  mode leaked every Horizun write tool.

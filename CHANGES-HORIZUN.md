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

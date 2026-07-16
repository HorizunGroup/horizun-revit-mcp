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
  (foundation for submit→job_id→poll).

## Modified files
- `src/shared/Infrastructure/McpEventHandler.cs` — wrap `command.Execute` so
  (A) modal suppression is active only during on-UI-thread execution, and
  (C) a transaction left open by a handler is detected and logged the instant
  the command returns (the dialog guard also auto-dismisses the resulting
  "transaction discarded" modal instead of freezing the pump).
- `src/plugin-r22..r27/App.cs` — subscribe/unsubscribe `McpDialogGuard` next to
  the existing `Idling` hookup (one line each).

## Roadmap (next increments)
- (B) Async submit/poll wired through the transport callback + a `job_status`
  tool (registry already in place).
- (D) Extend `SchemaValidator` with key aliasing/coercion for robust JSON
  contracts.
- Product branding (ribbon tab, package id) and a build/deploy/test pass on
  Revit 2025/2026.

## Attribution
Base: **Khoa Le — `bimwright/rvt-mcp`** (Apache-2.0). Hardening layer:
**Horizun**. Whole work remains Apache-2.0.

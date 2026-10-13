# Migrating from 2.1.x to Horizun Revit MCP 3.0.0

## Before updating

Back up the Revit models and the user state in `%USERPROFILE%\.horizun\`. Finish or cancel queued work, close Revit, and keep the MCP clients closed while the installer changes the server and add-ins. Follow the installation route in [CLIENTS.md](CLIENTS.md) for each client. The server and every installed add-in must come from the **same** 3.0.0 build.

Do not restore a 2.1.x server beside a 3.0.0 add-in, or the reverse. The discovery record carries the bridge protocol version and the full contract hash; a mismatch is refused. The installed client registration can remain in place if it still points to the installed `horizun-mcp.exe`. Use the expanded executable path reported by the installer, never a literal `%LOCALAPPDATA%` in PowerShell configuration.

## Breaking changes

| Area | 3.0.0 behaviour | Action for clients and scripts |
|---|---|---|
| Bridge protocol | Protocol **3** adds authenticated cancellation of durable queued jobs and a terminal `cancelled` job state. A 2.1.x bridge half cannot safely use it. | Update server and add-in together, restart the MCP client and Revit, then call `horizun_health`. |
| Contract hash | The full tool contract has a new hash. Discovery and writes refuse mismatched halves. | Rebuild or reinstall both halves from the same commit if health reports a hash mismatch. Do not bypass the check. |
| Tool schema surface | `full` remains the default. `facade` is opt-in and gives a compact listing with exact schemas available through `horizun_describe_tool` and `horizun://contract/tools/{tool}` (and `/{variant}`). The facade has its own hash; it does not replace the full contract hash. | Existing clients can continue in `full`. Clients that enable `facade` must fetch the exact schema before constructing a call and must still handle contract refusals. |
| Python access | Arbitrary Revit Python remains an owner-controlled opt-in. Typed writes remain available under the safe write profile. | Do not assume a previous Python grant. Use the Revit control or the documented admin helper if the owner chooses to enable it. Never edit settings to override a refusal. |
| Python runtime state | `horizun_health.python_runtime` distinguishes `not_authorized`, `not_loadable` (with a cause), `cold`, and `warm`. IronPython dependencies use an isolated load context on .NET 8 and a private resolver on .NET Framework 4.8. | Read the state and cause before troubleshooting Python. A `cold` state becomes `warm` after successful loading; it is not a load failure. |
| CAD detail transfer | `horizun_cad_extract` can report source DWG text and a paged detail plan with per-entity gaps and coverage. A readable DWG source and AutoCAD are required for source TEXT/MTEXT; an embedded import alone cannot prove them. | Supply the source path and map layers to line styles. Check the reported gaps and original-source coverage before calling a detail complete. |
| Groups and linked views | `horizun_manage_groups` adds duplicate, annotation, level, type-change and pattern-placement operations. `horizun_manage_links operation=visible_elements` returns candidates, exclusions and uncertainty without using Revit's hazardous three-argument collector overload. | Review dependent annotations by view before a group write. Treat linked-view candidates as a screening result, not as exact final visibility. |
| Model identity and exchange | Cross-app handoffs use a local project registry with a `model_ref` bound to strong document evidence. An exchange is delivered only when its manifest and payload hashes verify. | Register each model explicitly before consuming a cross-app handoff; treat an unproven or ambiguous identity as a stop. See [INTEROP.md](INTEROP.md). |
| Changed reply field | In the dry run of `horizun_coordination operation=show`, the requested paint count is `elements_painted_requested`, matching the apply reply. The old `elements_painted` name implied paint had already happened. | Read `elements_painted_requested` for the requested count; use `overrides_applied` and `shown` to judge what was verified. |
| Changed write arguments | `horizun_apply_cad_update` requires the `apply_binding` block returned by `horizun_plan_cad_update`. A plan that releases fittings requires `accept_connections_not_rebuilt=true`. | Pass the plan's binding verbatim and explicitly accept any connections the operation will leave unreconstructed. |
| Tool operations and contracts | No existing tool name was removed between the public baseline and this build; `horizun_exchange` and `horizun_read_schedule` are added. Existing tools gained operations and stricter validation. A remembered call may be rejected with a contract URI and the failing argument path. | Refresh tools/list after upgrade; update saved prompts or automation from [TOOLS.md](TOOLS.md) and [TOOLS-EXTENDED.md](TOOLS-EXTENDED.md). Do not translate a rejected write into an unchecked Python retry. |

The protocol number above is the **local server ↔ Revit add-in bridge protocol**, not the MCP protocol negotiated between an MCP client and the server.

## Upgrade and verify

1. Close Revit. Use the published 3.0.0 installer, or, for a source checkout with the documented prerequisites, run `powershell -ExecutionPolicy Bypass -File .\install.ps1`. Wait for its installed-binary readback and durable status; an exit code by itself is insufficient.
2. Restart the MCP client after client registration completes. On first Revit start, check any Security dialog and choose **Always Load** only after verifying the build. Open a document and check **Horizun Hub → Connection status**.
3. Call `horizun_health` first. Confirm `status: healthy`, the expected 3.0.0 version and commit, and the active document. Compare the server and add-in contract hashes. If they differ, close Revit and reinstall the same build together.
4. Refresh tool schemas and update stored calls. Rehearse typed writes, specify the target document, and check the post-commit result. Where a typed call returns `fallback.allowed: true`, a permitted Python fallback may be used; otherwise resolve the typed refusal.
5. For workflows that exchange files with another local app, verify the manifest, payload SHA-256, producer state and `model_ref` before any consumer write. A file without its manifest is still in transit.

Existing model contents are not rewritten by installing 3.0.0. A workflow that writes to a model still requires its own target, rehearsal and verification.

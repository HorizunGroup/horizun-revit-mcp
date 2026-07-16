<!-- mcp-name: io.github.HorizunGroup/horizun-revit-mcp -->

<h1 align="center">Horizun Revit MCP</h1>

<p align="center">
  Hardened MCP gateway for Autodesk Revit 2022–2027 — built for <em>unattended</em> agent automation:<br/>
  modal-dialog suppression, async job submit/poll, tolerant JSON contracts, open-any-model.
</p>

<p align="center">
  <a href="https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/build.yml"><img src="https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-230%2B%20tools-6C47FF" alt="MCP tools" /></a>
</p>

> **Horizun Revit MCP** is a hardened distribution of
> [`bimwright/rvt-mcp`](https://github.com/bimwright/rvt-mcp) by Khoa Le (Apache-2.0).
> The whole work stays Apache-2.0; modifications are documented in
> [CHANGES-HORIZUN.md](CHANGES-HORIZUN.md) and [NOTICE](NOTICE).

---

## The Horizun hardening layer

Long agent runs against real Revit models die in predictable ways: a modal dialog freezes the
pipe, a heavy export outlives the transport timeout, a slightly-wrong JSON type burns a round
trip, and nothing works until a human opens the model. This layer fixes exactly those, live-tested
on Revit 2025/2026:

| | Hardening | What it does |
|---|---|---|
| **A** | Modal-dialog suppression | `DialogBoxShowing`/`FailuresProcessing` auto-handled **only while an MCP command runs** (`McpDialogGuard`); your interactive dialogs are untouched. |
| **B** | Async jobs | Any command with `"async": true` returns a `job_id` in milliseconds; poll `job_status` for the result. `revit_submit_async` + `revit_job_status` tools included. No more 60-second timeout deaths on IFC/NWC exports or sync-to-central. |
| **C** | Orphan-transaction detection | A handler that leaks an open transaction is detected and logged the instant it returns, instead of silently blocking every later command. |
| **D** | Tolerant JSON contracts | `"5"` → `5`, `"true"` → `true`, `elementId` → `element_id` — repaired **before** validation, only where the schema is unambiguous (`SchemaCoercion`). |
| | `open_document` | Open and activate any `.rvt`/`.rfa` from disk (detach/audit options) — agents no longer need a human to open the model first. |
| | Upstream fixes | `export_ifc` wrapped in a transaction (modern Revit's IFC exporter writes element GUIDs and throws without one). |

---

## Install

Clone and build from source (releases will follow):

```powershell
git clone https://github.com/HorizunGroup/horizun-revit-mcp.git
cd horizun-revit-mcp

# build the plugins you need (r22..r27 = Revit 2022..2027) and the server
dotnet build src/plugin-r26/RvtMcp.Plugin.R26.csproj -c Release
dotnet build src/server/RvtMcp.Server.csproj -c Release

# stage + install (only the years you want; -Client none = do not touch MCP client configs)
powershell -ExecutionPolicy Bypass -File .\scripts\stage-plugin-zip.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Years 2026 -Client none -WhatIf   # preview
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Years 2026 -Client none           # install
```

What `install.ps1` does:

- Finds Revit 2022–2027 and installs matching plugins
- Copies a self-contained server under `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\`
- Wires detected MCP clients with absolute paths (`-Client codex|opencode|claude|kilo|none` to override)

Need only one Revit year?  
`install.ps1 -Years 2024`

For AutoCAD, use [dwg-mcp](https://github.com/bimwright/dwg-mcp) separately — different product, different install.

### Check that it works

1. Open Revit with a model.
2. Start the MCP connection from the ribbon (Add-Ins → **Horizun MCP** panel).
3. From the MCP client, list tools, then call `revit_get_current_view_info`.

You should get something like:

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

If that fails, install is not done yet — fix client config / plugin load before anything else.

### Uninstall

From the setup ZIP root (or this repo’s `scripts/`):

```powershell
# Setup ZIP layout:
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes

# Clone layout:
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -Yes
```

Removes plugins, self-contained server, client entries, discovery files, logs, and ToolBaker cache.

---

## What this is

Horizun Revit MCP is a **local** bridge between an MCP client (Claude, Cursor, Codex, OpenCode, …) and a running Revit session.

Two processes:

| Piece | Role |
|--------|------|
| **RvtMcp.Server** | .NET 8 MCP server on stdio. No Revit reference — builds anywhere. |
| **RvtMcp.Plugin** | One thin add-in per Revit year (2022–2027). Runs inside Revit, executes on the UI thread. |

Agent → MCP → server → localhost TCP (≤2024) or Named Pipe (≥2025) → plugin → Revit API.

Everything stays on the machine. No cloud relay is required for the gateway itself.

There is no Node/TypeScript sidecar. Server, plugins, handlers, and ToolBaker are C# end to end. Shared command code lives in `src/shared/`; each year is a small shell project with `#if` where the API drifted. Details: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Why it exists

People who live in Revit already know what they want automated. The friction was always shipping that idea as software: learn enough C#/Dynamo, fight the API, package an add-in, survive version upgrades — or pay someone else, or buy a fixed tool that only half-matches the office.

Agents change the first half of that loop (describe the task, try it live). They do not remove transactions, units, selection, worksharing, or “did this just trash the model?” That is what this gateway is for: a **typed tool surface** for common work, plus an escape hatch when you need ad-hoc C# inside Revit, plus an **optional** path to turn repeated local patterns into personal tools (ToolBaker).

It is not a universal add-in for every firm. Offices differ. The bet is: start from a shared runtime, grow *your* tools on top.

**Scope posture (honest):** we do not mint a new MCP tool for every edge case. Prefer typed tools when they exist; for everything else use `revit_send_code_to_revit` (C# only). Family *project* management is covered; full Family Editor authoring suites and Revit Viewer hosts are out of scope for now — see [docs/roadmap.md](docs/roadmap.md).

---

## How a normal session looks

1. Revit open with a model; plugin connected (ribbon).
2. MCP client starts `horizun-revit-mcp` / the installed server.
3. Agent uses tools: query view/selection, create grids/rooms, sheets, MEP, export, … Lengths in **mm** at the tool boundary.
4. Several writes in one undo step: `revit_batch_execute`.
5. Multiple Revits running: `revit_list_available_targets` then `revit_switch_target` with a four-digit year (`2024`, not `R24`).

When no typed tool fits:

```text
revit_send_code_to_revit   # C# body, compiled and run inside the plugin
```

That tool is on by default (toolset `toolbaker`). Strip it with `--read-only` or `--disable-toolbaker` if you do not want agents compiling code in the model.

### ToolBaker (optional)

By default you already have:

- `revit_send_code_to_revit`
- `revit_list_baked_tools` / `revit_run_baked_tool` for tools you previously accepted

**Adaptive bake** (suggest new tools from usage) is **off** unless you enable it. When on, repeated patterns can show up under `revit_list_bake_suggestions`; you accept or dismiss explicitly. Nothing ships itself into your ribbon without that step.

Useful flags (also JSON / env — see [Configuration](#configuration)):

| Goal | What to turn on |
|------|------------------|
| Learn tools from repeated **typed** calls | `--enable-adaptive-bake` |
| Also cluster **`send_code`** bodies for suggestions | plus `--cache-send-code-bodies` (redacted; still local) |
| Short-lived disk journal of send_code bodies | `persistSendCodeBodies` + TTL (default privacy keeps this off) |

Bake compile runs **inside Revit** via Roslyn — end users do not need Visual Studio. Details and privacy notes: [docs/bake.md](docs/bake.md).

### Toast (optional)

Completion toast in Revit is **off** by default. Turn on with the ribbon **Toast** button, `enableToast` in config, or `BIMWRIGHT_ENABLE_TOAST=1`. Only finished calls are shown (no “in progress” toast). Capture success can show a thumbnail when the file sits under the path allowlist. Ribbon **Status** also prints toast + bake/privacy flags so you can see what is enabled without guessing.

---

## Architecture (short)

```text
MCP client (stdio)
    → RvtMcp.Server (.NET 8)
        → TCP (Revit 2022–2024) or Named Pipe (2025–2027)
            → Plugin shell (per year)
                → ExternalEvent → Revit API / transactions / undo
```

Handlers return plain DTOs — never live Revit objects on the wire.

---

## Tools

Counts (without counting personal baked tools):

| Mode | Tools | Notes |
|------|------:|-------|
| Default | **223** | All default-on toolsets; **`modify` and `delete` off** |
| `--toolsets all` | **230** | Adds `modify` + `delete` |
| `all` + adaptive bake | **233** | Adds 3 suggestion-lifecycle tools |

Tool names are MCP-facing as `revit_*`. Wire names between server and plugin stay unprefixed snake_case.

**Default-on toolsets:**  
`query`, `create`, `view`, `schedule`, `families`, `mep`, `graphics`, `export`, `toolbaker`, `meta`, `lint`, `sheets`, `materials`, `geometry`, `annotation`, `rooms`, `links`, `parameters`, `organization`, `workflows`, `structural`, `kei`

**Off unless you ask:** `modify`, `delete`  
Example: `--toolsets query,view,meta` or `--toolsets all`.  
`--read-only` drops every write-capable toolset.

| Toolset | What it covers | Default |
|---------|----------------|---------|
| `query` | View, selection, filters, stats, parameters, relationships, worksets, groups/assemblies | on |
| `create` | Grids, levels, rooms, line/point/surface-based elements, groups | on |
| `view` | Create views, sheets layout helpers, capture image, crop/scale | on |
| `meta` | Batch execute, multi-Revit targets, project info, purge unused (MVP), message | on |
| `lint` | View naming patterns, firm-profile detect, warnings summary | on |
| `schedule` | List/create schedules, fields, formulas, data | on |
| `families` | Load/unload, types, instances, audit, export `.rfa` (project-side) | on |
| `modify` | Operate/color elements, set parameters, change type, workset assign | off |
| `delete` | Delete by id | off |
| `annotation` | Tags, text, dimensions, regions, keynotes, checks | on |
| `export` | PDF/DWG/IFC/NWC helpers, room data, and related export tools | on |
| `mep` | Systems, connectors, networks, place terminals/fixtures, etc. | on |
| `graphics` | View filters, overrides, visibility/phase | on |
| `toolbaker` | send_code, list/run baked tools; suggestion tools only if adaptive on | on |
| `sheets` | Sheets, titleblocks, revisions, renumber | on |
| `materials` | Materials, appearance, assignment, takeoff | on |
| `geometry` | BBox, measure, clash, volume/area, … | on |
| `rooms` | Rooms/areas/spaces, finishes, separators | on |
| `links` | Revit/CAD links, coordinates | on |
| `parameters` | Project/shared parameters | on |
| `organization` | Saved selections, view templates | on |
| `workflows` | Composite clash/audit/sheet/takeoff-style flows | on |
| `structural` | Columns, beams, foundations, rebar, loads, … | on |
| `kei` | Active KEI project DB path, query/write SQLite (WAL-safe), equipment import | on |

### Representative tools

Not a full dump of 200+ schemas — just anchors agents and humans use often:

| Toolset | Tool | Role |
|---------|------|------|
| `query` | `revit_get_current_view_info` | Active view type, level, scale |
| `query` | `revit_get_selected_elements` | Current selection |
| `query` | `revit_ai_element_filter` | Category + parameter filter (mm) |
| `query` | `revit_get_element_details` | Location, bbox, workset, phase, … |
| `create` | `revit_create_grid` / `revit_create_level` / `revit_create_room` | Core layout |
| `create` | `revit_create_point_based_element` | Doors, furniture, … from type id |
| `view` | `revit_capture_view_image` | Raster capture (path allowlist) |
| `meta` | `revit_batch_execute` | One `TransactionGroup` for several commands |
| `meta` | `revit_list_available_targets` / `revit_switch_target` | Multi-Revit |
| `families` | `revit_load_family_from_path` | Load `.rfa` into the project |
| `toolbaker` | `revit_send_code_to_revit` | Escape hatch (C#) |
| `toolbaker` | `revit_list_baked_tools` / `revit_run_baked_tool` | Personal accepted tools |
| `toolbaker` | `revit_list_bake_suggestions` | Adaptive only |
| `lint` | `revit_analyze_view_naming_patterns` | Naming outliers |

Golden snapshots in tests pin the exact surface; if counts and code disagree, trust tests/code.

---

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Compile matrix covers all six shells. Runtime depth still varies by year — bake and custom C# should be rechecked on the years you care about.

**Host:** full Revit desktop only. Revit Viewer is not a supported target.

---

## Security and privacy

- Transport is local by default (loopback TCP / local named pipe).
- Discovery files under `%LOCALAPPDATA%\RvtMcp\` include a per-session auth token.
- Tool arguments are schema-checked before handlers run.
- Errors returned to the model are sanitized (path leakage reduced).
- `send_code` can run arbitrary C# in the Revit process — powerful and risky; disable toolbaker if that is unacceptable.
- Adaptive bake, body cache, and TTL journals are **opt-in** and stay under the user profile. Defaults do not write raw send_code bodies to long-lived logs.

More: [SECURITY.md](SECURITY.md), [docs/bake.md](docs/bake.md).

---

## Configuration

Precedence, high wins: **CLI → env (`BIMWRIGHT_*`) →** `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json`.

| Setting | CLI | Env | JSON |
|---------|-----|-----|------|
| Target year | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| LAN bind (plugin) | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker surface | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache send_code bodies (bake clusters) | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| Persist send_code journal | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| Completion toast | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=1` | `enableToast` |

After changing server flags, restart the MCP connection so the client picks up the new tool list.

---

## MCP clients

| Client | Wiring |
|--------|--------|
| Claude Code | project `.mcp.json` or `~/.claude.json` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode / Codex / Kilo | `install.ps1 -Client …` (scripted) |
| Cursor / Cline / VS Code Copilot | documented JSON layouts |
| Gemini CLI / Antigravity | `gemini mcp add` or settings JSON |

Installer auto-detect is usually enough; see [AGENTS.md](AGENTS.md) and `docs/mcp-config-*.md` when hand-editing.

---

## Repo layout

```text
horizun-revit-mcp/
├── src/
│   ├── RvtMcp.sln
│   ├── server/            # MCP server
│   ├── shared/            # Handlers, transport, ToolBaker, toast, …
│   ├── plugin-r22/ … r27/ # One shell per Revit year
├── tests/                 # xUnit + golden tool lists
├── scripts/               # install / uninstall / package
├── docs/                  # roadmap, bake, testing
├── AGENTS.md
└── ARCHITECTURE.md
```

---

## Development

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj
dotnet build src/server/RvtMcp.Server.csproj -c Release
dotnet build src/plugin-r26/RvtMcp.Plugin.R26.csproj -c Release
```

Close Revit before building plugins (DLL lock). Plugin projects deploy into `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` on a normal Debug/Release build.

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

Contribution norms and snapshot rules: [CONTRIBUTING.md](CONTRIBUTING.md).

### Maturity

Usable, not sacred. CI builds the six plugin shells and server tests. Runtime coverage is strongest on mid-range years; treat production models carefully and verify on *your* Revit build. Fresh-machine checklist: [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md).

---

## More docs

| Doc | Topic |
|-----|--------|
| [AGENTS.md](AGENTS.md) | Agent install protocol |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Processes, transport, DTO rules |
| [docs/bake.md](docs/bake.md) | Adaptive bake and body privacy |
| [docs/roadmap.md](docs/roadmap.md) | Near-term hardening and non-goals |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | KEI SQLite tools (default-on `kei` toolset) |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

---

## Credits

The base of this project is [`rvt-mcp`](https://github.com/bimwright/rvt-mcp) by
**Khoa Le (bimwright)** — an excellent C#-end-to-end Revit MCP with a single UI-thread
dispatch pump, centralized schema validation and 225+ handlers. Horizun adds the
hardening layer on top; both are Apache-2.0. Upstream siblings worth knowing:
[dwg-mcp](https://github.com/bimwright/dwg-mcp) (AutoCAD),
[nwd-mcp](https://github.com/bimwright/nwd-mcp) (Navisworks),
[ipt-mcp](https://github.com/bimwright/ipt-mcp) (Inventor).

**Horizun** is a BIM consultancy. This project is maintained independently of the
upstream author.

---

## License

Apache-2.0 — [LICENSE](LICENSE). Attribution and modification notices: [NOTICE](NOTICE), [CHANGES-HORIZUN.md](CHANGES-HORIZUN.md).

Revit and Autodesk are trademarks of Autodesk, Inc. Horizun Revit MCP is an independent, unofficial project, not affiliated with or endorsed by Autodesk or the upstream author.

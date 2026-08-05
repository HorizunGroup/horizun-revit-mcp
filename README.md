# Horizun Revit MCP — Model Context Protocol server for Autodesk Revit

Open-source **Model Context Protocol (MCP) server for Autodesk Revit**. Horizun
connects Codex, Claude, Cursor and other MCP clients to a running Revit session
for typed BIM queries, verified model edits, family authoring, exports and Power
BI workflows. **Free and open source, Apache-2.0.** Part of the [Horizun
Hub](https://horizunhub.com) ecosystem.

> **Official stable release: v0.6.0.** This is the public source repository for
> Horizun Revit MCP. It contains the complete implementation, typed Revit
> operations, tests, installation scripts, security model and benchmark evidence.

## Why Horizun is built to lead the Revit MCP benchmark

Horizun is measured by verified outcomes, not by a raw count of tool names:

| Differentiator | What the public source proves |
|---|---|
| Native Revit coverage | Typed creation and editing for architecture, structure, MEP, views, sheets, schedules, families and system-family types. |
| Family authoring | RFT → RFA compilation with parameters, formulas, types, reference planes, labeled dimensions, symbolic/model lines, nested point instances, solid/void forms and MEP connectors. |
| Interoperability | Verified PDF, DWG, IFC, Navisworks NWC, multi-view FBX, image and schedule export, plus direct Power BI push ingestion. |
| Reliability | Dry-run plans, single-use confirmations, durable idempotency, bounded queueing, one Revit API operation at a time and post-commit re-reads. |
| Federated models | Host and loaded-link inventory, queries, quantities, clashes and schedules with source-model identity and explicit coverage. |
| Honest failure modes | Unsupported or ambiguous inputs are refused with a reason; the bridge never reports success from a call that merely did not throw. |

The benchmark is reproducible from [`docs/BENCHMARK.md`](docs/BENCHMARK.md).
Public CI passes **292 core tests + 162 server tests**, builds the MCP server with
warnings treated as errors, audits NuGet dependencies and runs the repository-safe
part of the sensitive-data scan. Revit-dependent CI jobs require a private
self-hosted runner and are reported as skipped when it is unavailable. The
benchmark marks every capability whose current public evidence is still pending
a live Revit or Power BI fixture; a green hosted-CI check is never presented as
proof that those live operations ran.

The server metadata is also prepared for the [Official MCP Registry](https://registry.modelcontextprotocol.io/)
under `io.github.HorizunGroup/horizun-revit-mcp`, and the repository publishes a
machine-readable [llms.txt](llms.txt) discovery summary for AI systems and
indexers.

## Horizun Hub: the ecosystem around the MCP

[Horizun Hub](https://horizunhub.com) is the product ecosystem from Horizun
Group. Horizun Revit MCP is its open-source automation and Revit API layer; the
Hub adds the training, apps, data products and expert workflows that turn the
bridge into a complete BIM operating system:

- **Apps:** PowerBIM Exporter for Revit, PowerBIM Online, PowerBIM Exporter for
  Civil 3D, BuildMotion, CopyToExcel and Family Browser.
- **Academia:** PowerBIM + IA training and the growing course library.
- **Quantification and 4D/5D:** Revit + Navisworks examples, templates and
  construction-planning workflows.
- **Power BI and content:** dashboards, `.pbit` templates, visual assets and
  data workflows.
- **IA and automation:** agents, MCP workflows and scripts for standardising
  families and auditing models.
- **APS connection:** extraction of Autodesk Construction Cloud data into Power
  BI workflows.
- **Expert support:** two hours per month with a Horizun Group BIM expert,
  delivered through Zoom or Teams.
- **Learning media:** BIM Para Todos videos and tutorials covering Revit,
  Power BI, Speckle, Civil 3D and AI.

The MCP remains organisation-neutral: company standards, catalogues and audit
rules are supplied by the Hub workflows or by the caller, rather than compiled
into the bridge. See [`docs/HORIZUN-HUB.md`](docs/HORIZUN-HUB.md) for the full
relationship between the open-source gateway and the Hub ecosystem.

Point Claude — or any MCP client — at a running Revit and let it read and write
the model, under one contract: **a command never reports work it did not
verify.** Every typed write is re-read from the model after the commit; a silent
rollback becomes an error, not a false success. Counts come from re-reading the
model, never from counting calls that did not throw. The deliberately low-level
`horizun_execute_python` escape hatch is the documented exception.

**What this is.** The bridge: transport, safety guards, and a generic tool
surface over the Revit API. Organisation-neutral by design — no company's
standards, catalogues or naming rules are compiled in; where a command needs
one, it is an input supplied at call time.

**What this is not.** A methodology. The standards, catalogues, audit criteria
and reporting that turn these commands into delivery workflows — model audits,
classification, family homologation, pre-delivery QA — live in
[Horizun Hub](https://horizunhub.com). This repository is the socket; the Hub
is what plugs into it.

## Install

### Install with an agent — recommended

Paste one of these into the agent you already have open, in any folder:

**Claude Code**

```
Clone https://github.com/HorizunGroup/horizun-revit-mcp into this folder, read its
CLAUDE.md, and follow the install procedure there. When it finishes, register the
MCP server with yourself using the exact path the installer printed, and tell me
which version and commit ended up installed.
```

**Codex**

```
Clone https://github.com/HorizunGroup/horizun-revit-mcp into this folder, read its
AGENTS.md, and follow the install procedure there. When it finishes, register the
MCP server with yourself using the exact path the installer printed, and tell me
which version and commit ended up installed.
```

Claude Code reads `CLAUDE.md` (which imports [AGENTS.md](AGENTS.md)); Codex reads
[AGENTS.md](AGENTS.md) directly. Either way the agent checks the prerequisites,
builds from this public source against each Revit version installed on the
machine, installs the matching add-in binaries and MCP server, verifies their
commit and SHA-256, and registers the resulting server path under the name
`horizun-revit` — so it reads as what it is next to every other MCP server on
your machine. Revit must be closed. No prebuilt executable is downloaded by this
path.

### Prebuilt release installer — optional

Download `horizun-mcp-<version>-setup.exe` from the
[latest release](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest),
close every Revit window, and run it. The installer detects Revit 2023–2027 and
deploys a different add-in binary compiled against each installed year's own API.
It also installs the MCP server and reports exactly which years succeeded. No Git
or .NET SDK is required for this path.

The release carries `SHA256SUMS.txt` and a complete payload manifest. The current
build is not signed by a publicly trusted code-signing CA, so Windows/Revit may
show a publisher warning; verify the SHA-256 before running it.

The setup installs every supported Revit payload present in the release and the
MCP server. It also installs Start-menu helpers to register or verify Horizun in
Codex and Claude. Registration is explicit and unchecked by default: the helper
makes timestamped backups, preserves all other MCP entries and refuses to edit a
client that is still running and could overwrite its configuration.

For a command-line installation without Git or an SDK, download the repository's
small bootstrap and let it select the latest release. It downloads the setup and
`SHA256SUMS.txt` from the same GitHub release, verifies the complete SHA-256, and
only then launches the installer:

```powershell
$p = Join-Path $env:TEMP 'horizun-install-release.ps1'
Invoke-WebRequest 'https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1' -OutFile $p
powershell -ExecutionPolicy Bypass -File $p
```

Pass `-Version v0.6.0` to pin the official release instead of following `latest`.

### Build from source manually

This path builds everything from the repository, on your machine, against the
Revit already installed. **Nothing prebuilt is downloaded and run.**

**Prerequisites:** Windows, at least one Revit 2023–2027, the
[.NET SDK](https://dotnet.microsoft.com/download) (**8+** for Revit 2023–2026;
**10+** when building for Revit 2027), and **Revit closed** — the
installer refuses to run while Revit holds the add-in files, and changes nothing
when it refuses.

```powershell
git clone https://github.com/HorizunGroup/horizun-revit-mcp
cd horizun-revit-mcp
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It finds every Revit on the machine by its own `RevitAPI.dll`, builds the add-in
for each of those years and the MCP server, installs both, and reads every
installed binary back to prove it landed. A build failure changes nothing; a
failure after that rolls back and tells you the state you are in.

**The installer prints the exact path to register — use that one.** It is
already expanded for your machine, which matters: `%LOCALAPPDATA%` is expanded
by `cmd.exe` and **not** by PowerShell, so a config written with the variable
silently points nowhere.

```powershell
# Claude Code
claude mcp add horizun-revit -- "C:\Users\<YOU>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<YOU>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf, Claude Desktop, and other MCP clients
{
  "mcpServers": {
    "horizun-revit": {
      "command": "C:\\Users\\<YOU>\\AppData\\Local\\Programs\\Horizun\\MCP\\server\\horizun-mcp.exe"
    }
  }
}
```

TOML literal strings (single quotes) take Windows paths as they are; JSON needs
every backslash doubled. **Raise your client's tool timeout** if it has one: a
model scan or a batch open holds Revit's UI thread for minutes, and a 60-second
default gives up on work that is still running — the bridge then looks broken
while it is merely busy.

#### First Revit start

Two things to expect, neither of them a fault:

- Revit shows a **"Security - Unsigned Add-In"** dialog. Choose **Always Load**.
  This build is unsigned. Revit normally remembers that choice for this add-in's
  identity, but the prompt may return after a trust or policy reset. It can also
  open on a monitor you are not looking at — a Revit that seems stuck on startup
  with the CPU idle is often this dialog hiding.
- With a document open, a **Horizun Hub** tab appears in the ribbon. Its
  *Estado del puente* button answers "is this working, and which version?"
  without leaving Revit.

From your MCP client, `horizun_health` answers the same with the commit
included. To update later: `git pull`, close Revit, run `install.ps1` again.

## Architecture

```
Claude / MCP client
      │  (MCP over stdio)
Horizun.Server        ← the MCP server process; forwards tool calls
      │  (named pipe, token-authed)
Horizun.Revit         ← the Revit add-in: pipe transport + UI-thread dispatcher
      │  (bounded FIFO → ExternalEvent → Revit UI thread)
   Revit API
```

- **`Horizun.Revit`** — the add-in. `App` (IExternalApplication) starts a named-pipe
  server and publishes a discovery file; `Dispatcher` crosses each request onto
  Revit's UI thread via `ExternalEvent`; `Guard` and `Reconcile` are the "cannot
  lie" commit contract; commands live under `Commands/`.
- **`Horizun.Server`** — the MCP server. Wire format hand-rolled from the open MCP
  spec (no third-party SDK): discovers the pipe, speaks MCP over stdio, forwards
  to the plugin. Input/output schemas and behavioral effects live in one shared
  contract, so `tools/list` can answer with Revit closed without drifting from
  the add-in. It negotiates MCP through 2025-11-25 and returns both backward-
compatible text and `structuredContent`. Five tools are **host-resident**:
  they answer inside the server and never touch Revit.

## Tools

In Revit, over the pipe:

| Tool | What it does |
| --- | --- |
| `horizun_health` | Is the bridge alive, and WHICH Revit is on the other end — year, build, our own version and commit, and the document active right now (an explicit null when none is). |
| `get_document_info` | The open document, its counts and identity. |
| `horizun_open_document` | Open a model, refusing a file saved in another Revit version (opening upgrades it irreversibly) and refusing a workshared central unless asked twice. |
| `horizun_save_document` | Save, then prove it: the file's timestamp and size before and after. On a workshared model it says, loudly, that this is not a synchronize. |
| `horizun_relinquish_all` | Give back everything this user owns, and count what is still owned afterwards rather than assume zero. |
| `horizun_capture_view` | Export a view and hand the IMAGE back, so the caller can look at the model instead of only reading it. |
| `horizun_execute_python` | The execution fallback: Python against the whole API on the UI thread, stdlib included. **Enabled by default**; an explicit off in `settings.json` is respected. `preflight=true` validates permission, document, size, hash and syntax without executing; the structured `__output__` evidence contract classifies each run as `verified`, `completed_unverified`, `partial` or `failed` — a verified claim without evidence is downgraded, never believed. It detects an open transaction but cannot safely close or roll it back, and it has no typed command's dry-run, confirmation or post-commit guarantee. |
| `horizun_model_scan` | The census, under the honesty contract. |
| `horizun_write_params_verified` | Parameter writes, each re-read after commit. |
| `horizun_delete_verified` | Deletion with the cascade counted, `dry_run` first. |
| `horizun_document_session` | Read-only session and version inspection. |
| `horizun_audit_model` | Model checks with per-check pass/fail. |
| `horizun_quantities` | Quantities, with input rejected rather than guessed. |
| `horizun_clash` | Clash, where zero is a trustworthy zero. |
| `horizun_set_keynote` | Keynote writes with the blast radius reported first. |
| `horizun_family_apply` | Family edits in one transaction, under a geometry invariant that rolls the whole thing back if it moves. |
| `horizun_bind_shared_param` | Shared-parameter binding, with `VariesAcrossGroups` measured from the definition, not assumed. |
| `horizun_list_elements` | Bounded, paginated inventory by category across the host and loaded Revit links, with source model and link instance identity on every row. Unloaded links are reported, not silently skipped. |
| `horizun_query_model` | Composable query across host and loaded links: category, family/type/name/level, parameter predicates and 3D bounds; selected parameter projection, grouped summaries, coverage and stale-detecting cursors. |
| `horizun_navigate` | Select, frame or open host views from query results. Linked ids are explicitly refused because they are document-local. |
| `horizun_create_elements` | Atomic heterogeneous creation of levels, grids, walls, floors, ceilings, footprint roofs, rooms, family instances, structural framing/columns, ducts, pipes, conduits and cable trays in explicit units, with type/level resolution before the transaction and post-commit re-read. |
| `horizun_create_family` | Compile a loadable RFA from an RFT: parameters, formulas, types, reference planes, labeled dimensions, symbolic/model lines, nested point instances with parameter propagation, solid/void extrusion/blend/revolution/sweep/swept-blend forms and face-hosted MEP connectors; save, optionally load, and verify both file and project Family. Requires `full_write` or `unsafe_code`. |
| `horizun_manage_system_types` | Duplicate project-resident system-family types and write their parameters atomically. Wall/floor/roof/ceiling host types can replace the complete homogeneous compound structure: ordered layers, material, width, function, wrapping, shell/core boundaries, structural/variable layer and deck data. Runtime class, name, values and layer graph are re-read after commit. |
| `horizun_transform_elements` | Atomic move, copy, rotate, pin/unpin and type changes over explicit ids, verified from fresh locations, copies, pin state and type ids. |
| `horizun_manage_views` | Dependency-aware creation of floor/ceiling/structural plans, sections, elevations, drafting/3D views, duplicates, templates, sheets, viewports and schedule placements; aliases let later actions use objects created earlier in the batch. |
| `horizun_annotate` | Atomic text, tags and dimensions. Dimensions use stable Revit references rather than guessing faces from element ids. |
| `horizun_export` | Dry-run and verified PDF, DWG, configurable IFC, Navisworks NWC, multi-view FBX, image and schedule export. Only changed non-empty files matching the requested output family are attributed to the call. Requires `full_write` or `unsafe_code`. |
| `horizun_execute_plan` | Compose up to 100 typed writes into one ordered TransactionGroup. Later actions can consume exact prior results with `${key.path}`; any failure rolls the complete graph back. |
| `horizun_submit_job` | Queue any installed Revit-side tool (except Python/the queue itself), return a persistent job id, and poll it without blocking Revit. |
| `horizun_create_schedule` | Create a native Revit schedule with selected fields and sorting, optionally including linked elements. Defaults to `dry_run: true`, requires a target document and confirmation token, then re-reads the committed schedule. |
| `horizun_list_schedules` | List native schedules with their actual fields, linked-file setting, itemization and displayed body dimensions. |
| `horizun_get_schedule_data` | Read the displayed header and body cells of a native schedule with explicit row/column bounds and truncation metadata. |
| `horizun_split_floor_loops` | One floor per sketch loop, carrying the height offset onto each. |
| `horizun_split_multilayer_walls` | One wall per material layer, doors and windows re-hosted on the structural one. **Curved walls are REFUSED, not straightened.** |
| `horizun_split_multilayer_slabs` | One floor/ceiling per material layer, profile and curves intact. A slab whose hosted families cannot be put back rolls back alone. |
| `horizun_ungroup_and_mark` | Ungroup, stamping each member with its origin group — checked BEFORE anything is ungrouped, because an ungrouped-and-unmarked model is unrecoverable. |
| `horizun_regroup_by_param` | The reverse: rebuild the groups from that stamp. Annotation is excluded and reported, rather than failing the whole call. |
| `horizun_copy_slab_elevations` | Copy a warped floor's surface onto other floors. Names every destination that will LOSE an existing shape before it happens. |
| `horizun_embed_floors_in_toposolid` | Embed floors into terrain. Slabs touching at the same level merge into one outline; a real step between them does not. |
| `horizun_grade_toposolid_around_floors` | Offset, breaklines and a constant side slope out to daylight. Stations that never daylight are counted, not faked. |
| `horizun_rectangularize_walls` | Irregular orthogonal walls into rectangular fragments, from real solid geometry. Refuses curves and non-rectangular openings by name. |

The last nine keep their geometry in Python that ships beside the add-in
(`src/Horizun.Revit/Recipes/`), while the host owns the transaction, the
`dry_run`, and the re-read after the commit — see `Core/Recipe.cs`. All nine
default to `dry_run: true` and require a single-use confirmation token to write.

Host-resident (no Revit needed):

| Tool | What it does |
| --- | --- |
| `horizun_catalog_lookup` | Generic leaf resolution over a catalog file, `is_leaf` null ≠ false, sha256 provenance. |
| `horizun_job_status` | How a long run is going, read from disk WITHOUT touching Revit — answers while Revit is busy inside the very command it describes, survives a crash, and says whether the process that claimed the job is still alive. |
| `horizun_excel_write_rows` | Appends rows to `.xlsx` over the OPC package — no COM, no Excel installed. Backs the file up and re-reads every written cell. |
| `horizun_power_bi_push` | Push up to 10,000 primitive rows directly into a Power BI push semantic-model table. Credentials stay in server environment variables; a durable key prevents duplicate rows after a lost response. Requires `full_write` or `unsafe_code`. |
| `horizun_target` | Which Revit these tools are talking to, and how to change it. Two versions open at once is normal, and the expensive failure is a healthy bridge attached to the wrong instance. |

See [Family authoring](docs/FAMILY-AUTHORING.md) for the loadable-RFA and
system-family capability matrix, complete examples and the explicit in-place API
boundary.

**One command executes at a time; concurrent calls wait in a bounded FIFO queue.**
There are 16 waiting slots. A successful JSON response includes `bridge_queue`:
`queued` says whether another bridge request was ahead at admission, while
`waited_ms` also includes time waiting for Revit's UI thread to become available. A
cancellation removes a request only while it is still queued, proving that it
never ran; once it reaches Revit's UI thread the API cannot interrupt it. A full
queue applies backpressure explicitly instead of dropping work or growing without
limit. Ordinary calls and `horizun_submit_job`/`run_async` jobs alternate when both queues are busy, so
neither can starve the other. Every reply also carries **what Revit raised while
the command ran** — warnings, errors and modal dialogs — on success and failure.

## Direct Power BI connection

`horizun_power_bi_push` uses Microsoft's push semantic-model REST endpoint; it
does not automate Power BI Desktop. Configure authentication in the environment
of the MCP server, never in a tool call:

```powershell
# Option A: short-lived OAuth access token
$env:HORIZUN_POWER_BI_ACCESS_TOKEN = '<token with Dataset.ReadWrite.All>'

# Option B: Entra service principal; Horizun obtains the access token
$env:HORIZUN_POWER_BI_TENANT_ID = '<tenant-guid>'
$env:HORIZUN_POWER_BI_CLIENT_ID = '<application-guid>'
$env:HORIZUN_POWER_BI_CLIENT_SECRET = '<secret>'
```

The destination is fixed to `api.powerbi.com`; dataset/workspace ids must be
GUIDs; values are primitive JSON only; the union is limited to 75 columns,
strings to 4,000 characters and each call to 10,000 rows. These bounds follow
Microsoft's [push semantic-model limitations](https://learn.microsoft.com/power-bi/developer/embedded/push-datasets-limitations).
Only push semantic models accept this operation. Run the tool with its default
`dry_run: true`, then apply with `dry_run: false` and a new `idempotency_key`.
An identical retry replays the stored answer; a connection loss after upload is
reported `in_doubt` and is not sent again automatically.

## Public benchmark

The comparison is task-based rather than tool-count based. The cases, scoring
rules, evidence requirements and current Horizun results are in
[docs/BENCHMARK.md](docs/BENCHMARK.md). A feature scores only when its public
schema is typed, invalid input is refused before mutation, and the claimed
result is measured after the operation.

## Build and test

```bash
dotnet build src/Horizun.Revit -c Release -p:RevitYear=2026   # one year at a time
dotnet build src/Horizun.Server -c Release                    # the MCP server (Revit-free)
dotnet test tests/Horizun.Core.Tests
dotnet test tests/Horizun.Server.Tests
```

The plugin's TFM follows the runtime Revit itself uses — `net48` for ≤ 2024,
`net8` for 2025–2026, `net10` for 2027. To update an existing install:
`git pull`, close Revit, run `install.ps1` again. The server and the add-in hash
one shared contract and the server refuses an add-in whose hash differs, so the
two halves always ship together.

To check a real Revit — the half of the test story CI cannot reach:

```bash
pwsh scripts/verify-live.ps1 -Year 2026 -OldFile <a model saved in another Revit>
pwsh scripts/verify-queue-live.ps1 -Year 2026 -Document <active model title or path>
```

## Status

Working, in production use, and honest about its edges.

- **450+ tests** over the Revit-free surface. CI builds and tests exactly that
  surface and nothing else: a hosted runner has no `RevitAPI.dll`, so building
  the plugin there would be a lie. The Revit-bound half is verified live
  instead, with `scripts/verify-live.ps1`.
- **Built for five Revit years** — 2023 through 2027, each compiled against its
  own `RevitAPI.dll`.
- **Verified live** against real models rather than mocks: the full tool
  surface, the upgrade guard refusing a real older-year family, the commit
  contract's rollback on a geometry change, bounded FIFO ordering and capacity,
  cancellation-before-start with an independently checked absent side effect,
  and `job_status` answering while Revit's UI thread was inside the very command
  it describes.
- **Unsigned.** Revit raises its "Security - Unsigned Add-In" dialog on first
  load. Revit normally remembers the decision by add-in identity, but policy or
  trust-store changes can make the prompt return.
- **Known limits, stated**: `excel_write_rows` appends below an Excel Table
  without expanding the table's range (reported per call); a catalog that is
  neither UTF-8 nor Latin-1 is decoded as Latin-1 and says so; cancelling a
  request prevents it only while it is still queued. Once Revit starts the
  command, cancellation stops *you waiting* but cannot interrupt the Revit API
  on its UI thread. General creation of in-place families is not available in
  the public Revit API; Horizun creates loadable RFA families and project-resident
  system types instead of driving the modal family editor by UI automation.

## Security

`horizun_execute_python` runs arbitrary Python inside Revit with the rights of
the signed-in user. It is **enabled by default** during this early stage — a
fresh install (no `settings.json`, or one without these keys) reads as
`permission_profile: "unsafe_code"` and `enable_execute_python: true`, so the
tool is advertised and serves as the execution fallback when no typed command
covers an operation. An **explicit** choice in
`%USERPROFILE%\.horizun\settings.json` is always respected: `read_only`,
`safe_write`, `full_write` or `enable_execute_python: false` switch capability
off exactly as before, and a settings file that exists but cannot be parsed
falls **closed** (`read_only`, Python off) so a corrupted explicit restriction
never reads as consent. The `safe_write` profile
allows typed, reversible model edits but refuses opening/saving/relinquishing,
document-session changes and external export; `full_write` enables those.
`read_only` hides/refuses model mutations. `allowed_tools` and `denied_tools`
can narrow any profile. `scripts/enable-execute-python.ps1` remains as the
admin tool to re-enable (or restore) an explicitly disabled setup, and turns it
off with `-Disable`. There is no inbound network listener:
named pipes are not reachable across a network, and the server speaks stdio to
whatever launched it. The optional `horizun_power_bi_push` tool makes bounded
outbound HTTPS calls only to fixed Microsoft Entra and `api.powerbi.com`
endpoints; it accepts no URL or credential in tool arguments. The full threat model — what is defended, and what
deliberately is not — is in [docs/security-model.md](docs/security-model.md),
and it is written to be argued with. Report vulnerabilities privately through
the process in [SECURITY.md](SECURITY.md); do not place client/model data or
credentials in a public issue.

## Contributing

Focused fixes, tests and documentation improvements are welcome. Read
[CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request; public
contributions must remain organisation-neutral and contain no project data.

## License

**Apache License 2.0** — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
Third-party components remain under their own licenses, listed with versions in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The Autodesk Revit API is referenced at build time and never redistributed.
Revit, Autodesk and Autodesk Docs are trademarks of Autodesk, Inc. This project
is not affiliated with, endorsed by, or sponsored by Autodesk, Inc.

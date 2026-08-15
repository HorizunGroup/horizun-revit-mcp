# Horizun Revit MCP

An MCP gateway for Autodesk Revit. **Free and open source, Apache-2.0.** Part of
the [Horizun Hub](https://horizunhub.com) ecosystem.

Point Claude — or any MCP client — at a running Revit and let it read and write
the model, under one contract: **a command never reports work it did not
verify.** Every typed write is re-read from the model after the commit; a silent
rollback becomes an error, not a false success. Counts come from re-reading the
model, never from counting calls that did not throw. The deliberately low-level
`horizun_execute_python` fallback is the documented exception: the bridge does
not re-read the model after arbitrary code, so its results are labelled
**self-reported** rather than verified, and never presented as the same kind of
claim a typed write makes.

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

### Release installer — recommended

Download `horizun-mcp-<version>-setup.exe` from the
[latest release](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest),
close every Revit window, and run it. The installer detects Revit 2023–2027 and
deploys a different add-in binary compiled against each installed year's own API.
It also installs the MCP server and reports exactly which years succeeded. No Git
or .NET SDK is required for this path.

The release carries `SHA256SUMS.txt` and a complete payload manifest. The latest
public release predates the SignPath Foundation application submitted on
2026-08-15 and is not signed by a publicly trusted code-signing CA, so
Windows/Revit may show a
publisher warning; verify the SHA-256 before running it. Future stable releases
must satisfy the public signature gate in the
[code signing policy](CODE-SIGNING-POLICY.md) before publication. The intended
open-source service is: **Free code signing provided by SignPath.io, certificate
by SignPath Foundation.** The application is awaiting review; this is not a
claim that the current download is already signed.

The setup installs every supported Revit payload present in the release and the
MCP server. It also completes Codex/Claude registration automatically. If either
client is open, it does **not** edit underneath it: a user-level helper waits for
the client to close, makes timestamped backups, preserves every other MCP entry,
registers Horizun, verifies the configuration and completes `horizun_health`
after the first Revit start. Its durable status is
`%LOCALAPPDATA%\Horizun\install-status.json`; Start-menu helpers can resume or
inspect it. An advanced pre-uninstall helper can remove only the
`horizun-revit` client entries and, only when explicitly selected, purge local
state or self-signing trust.

For a command-line installation without Git or an SDK, paste **one command**.
It selects the latest release, downloads the setup and `SHA256SUMS.txt` from that
same GitHub release, verifies the complete SHA-256, installs quietly, and safely
finishes or schedules client registration and first-start health verification:

```powershell
irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1 | iex
```

Download the script first and pass `-Version vX.Y.Z` when a pinned release or
`-Interactive` Setup wizard is required. Quiet, latest and automatic client
completion are the defaults.

### Build from source

This path builds everything from the repository, on your machine, against the
Revit already installed. **Nothing prebuilt is downloaded and run.**

**Prerequisites:** Windows, at least one Revit 2023–2027, the
[.NET SDK](https://dotnet.microsoft.com/download) (**8+** for Revit 2023–2026;
**10+** when building for Revit 2027), and **Revit closed** — the
installer refuses to run while Revit holds the add-in files, and changes nothing
when it refuses.

#### Let an agent do it

Paste this into **Claude Code** or **Codex**, in any folder:

```
Clone https://github.com/HorizunGroup/horizun-revit-mcp into this folder, read its
AGENTS.md, and follow the install procedure there. Install and verify the binaries,
then confirm the automatic completion status. Do not edit an active client's
configuration; let the installed helper finish registration after that client exits.
```

Both agents pick up [AGENTS.md](AGENTS.md) automatically once they are inside the
repository (Claude Code also reads `CLAUDE.md`, which imports it). It has the
prerequisites, the failure modes, and the two surprises worth knowing before the
first Revit start.

#### Or run it yourself

```powershell
git clone https://github.com/HorizunGroup/horizun-revit-mcp
cd horizun-revit-mcp
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It finds every Revit on the machine by its own `RevitAPI.dll`, builds the add-in
for each of those years and the MCP server, installs both, and reads every
installed binary back to prove it landed. A build failure changes nothing; a
failure after that rolls back and tells you the state you are in.

Automatic completion normally handles registration. **If manual recovery is
needed, use the exact path printed by the installer.** It is
already expanded for your machine, which matters: `%LOCALAPPDATA%` is expanded
by `cmd.exe` and **not** by PowerShell, so a config written with the variable
silently points nowhere.

```powershell
# Manual fallback: persistent for this user, after closing Claude
claude mcp add --scope user horizun-revit -- "C:\Users\<YOU>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"

# Manual fallback: after closing Codex
codex mcp add horizun-revit -- "C:\Users\<YOU>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex timeout settings — %USERPROFILE%\.codex\config.toml
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

- Revit can show a **Security** add-in dialog when the publisher is not already
  trusted. After verifying the build, choose **Always Load**. Revit normally
  remembers that choice, but the prompt may return after a trust or policy reset. It can also
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
| `horizun_execute_python` | The execution fallback: Python against the whole API on the UI thread, stdlib included. **Enabled by default**; an explicit off in `settings.json` is respected. `preflight=true` validates permission, document, size, hash and syntax without executing. Results are **self-reported, not host-verified**: the structured `__output__` contract classifies each run as `self_reported_verified`, `completed_unverified`, `partial` or `failed` — there is no `verified` state on this path, `host_verified` is always false, and a verified claim without evidence is downgraded. It detects an open transaction but cannot safely close or roll it back, and it has no typed command's dry-run, confirmation or post-commit guarantee. |
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

Working and in production use, with stable promotion governed by published,
release-scoped evidence rather than by a local success claim.

- **Revit-free suites are enforced in CI.** CI builds and tests exactly that
  surface and nothing else: a hosted runner has no `RevitAPI.dll`, so building
  the plugin there would be a lie. The Revit-bound half is verified live
  instead, with `scripts/verify-live.ps1`.
- **Built for five Revit years** — 2023 through 2027, each compiled against its
  own `RevitAPI.dll`.
- **Live evidence is release-scoped.** `verify-live.ps1` refuses uncovered
  release-gate probes, and stable promotion requires a published report for
  every supported Revit year. If an artifact is absent, local experience or a
  compiled DLL is not substituted for it.
- **No publicly trusted publisher identity by default.** Revit can raise its
  security dialog on first load. Source installs offer an explicit per-user
  self-sign/trust helper; that local trust is not a public CA signature and can
  be removed independently during advanced cleanup.
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
and it is written to be argued with.

Horizun has no automatic telemetry or maintainer-operated data collection.
User-requested network operations and local state are described in the
[privacy policy](docs/PRIVACY.md). Stable Windows release signing is governed by
the [code signing policy](CODE-SIGNING-POLICY.md).

## License

**Apache License 2.0** — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
Third-party components remain under their own licenses, listed with versions in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The Autodesk Revit API is referenced at build time and never redistributed.
Revit, Autodesk and Autodesk Docs are trademarks of Autodesk, Inc. This project
is not affiliated with, endorsed by, or sponsored by Autodesk, Inc.

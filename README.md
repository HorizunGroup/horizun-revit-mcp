# Horizun Revit MCP

An MCP gateway for Autodesk Revit. **Free and open source, Apache-2.0.** Part of
the [Horizun Hub](https://horizunhub.com) ecosystem.

Point Claude — or any MCP client — at a running Revit and let it read and write
the model, under one contract: **a command never reports work it did not
verify.** Every write is re-read from the model after the commit; a silent
rollback becomes an error, not a false success. Counts come from re-reading the
model, never from counting calls that did not throw.

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

Everything is built from this repository, on your machine, against the Revit you
already have. **Nothing is downloaded and run as a binary.**

**Prerequisites:** Windows, at least one Revit 2023–2027, the
[.NET SDK 8+](https://dotnet.microsoft.com/download), and **Revit closed** — the
installer refuses to run while Revit holds the add-in files, and changes nothing
when it refuses.

### Let an agent do it

Paste this into **Claude Code** or **Codex**, in any folder:

```
Clone https://github.com/HorizunGroup/horizun-revit-mcp into this folder, read its
AGENTS.md, and follow the install procedure there. When it finishes, register the
MCP server with yourself using the exact path the installer printed, and tell me
which version and commit ended up installed.
```

Both agents pick up [AGENTS.md](AGENTS.md) automatically once they are inside the
repository (Claude Code also reads `CLAUDE.md`, which imports it). It has the
prerequisites, the failure modes, and the two surprises worth knowing before the
first Revit start.

### Or run it yourself

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
claude mcp add horizun -- "C:\Users\<YOU>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun]
command = 'C:\Users\<YOU>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf, Claude Desktop, and other MCP clients
{
  "mcpServers": {
    "horizun": {
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

### First Revit start

Two things to expect, neither of them a fault:

- Revit shows a **"Security - Unsigned Add-In"** dialog. Choose **Always Load**.
  This build is unsigned; the dialog **returns after every update** (the
  decision is remembered per binary), and it can open on a monitor you are not
  looking at — a Revit that seems stuck on startup with the CPU idle is almost
  always this dialog hiding.
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
      │  (ExternalEvent → Revit UI thread)
   Revit API
```

- **`Horizun.Revit`** — the add-in. `App` (IExternalApplication) starts a named-pipe
  server and publishes a discovery file; `Dispatcher` crosses each request onto
  Revit's UI thread via `ExternalEvent`; `Guard` and `Reconcile` are the "cannot
  lie" commit contract; commands live under `Commands/`.
- **`Horizun.Server`** — the MCP server. Wire format hand-rolled from the open MCP
  spec (no third-party SDK): discovers the pipe, speaks MCP over stdio, forwards
  to the plugin. Tool schemas live here because `tools/list` must answer with Revit
  closed. Four tools are **host-resident**: they answer inside the server and
  never touch Revit.

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
| `horizun_execute_python` | The escape hatch: Python against the whole API on the UI thread, stdlib included, with orphaned-transaction rollback. **Disabled by default**, enabled per machine. |
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

Host-resident (no Revit needed):

| Tool | What it does |
| --- | --- |
| `horizun_catalog_lookup` | Generic leaf resolution over a catalog file, `is_leaf` null ≠ false, sha256 provenance. |
| `horizun_job_status` | How a long run is going, read from disk WITHOUT touching Revit — answers while Revit is busy inside the very command it describes, survives a crash, and says whether the process that claimed the job is still alive. |
| `horizun_excel_write_rows` | Appends rows to `.xlsx` over the OPC package — no COM, no Excel installed. Backs the file up and re-reads every written cell. |
| `horizun_target` | Which Revit these tools are talking to, and how to change it. Two versions open at once is normal, and the expensive failure is a healthy bridge attached to the wrong instance. |

**One command at a time, and the second one is refused rather than queued.** A
Revit command cannot be interrupted from outside: when a call times out, the work
keeps the UI thread. So a later caller is told what is holding it and for how
long, instead of waiting for a second timeout. Every reply also carries **what
Revit raised while the command ran** — warnings, errors, and any modal dialog —
on success and failure alike.

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
```

## Status

Working, in production use, and honest about its edges.

- **400+ tests** over the Revit-free surface. CI builds and tests exactly that
  surface and nothing else: a hosted runner has no `RevitAPI.dll`, so building
  the plugin there would be a lie. The Revit-bound half is verified live
  instead, with `scripts/verify-live.ps1`.
- **Built for five Revit years** — 2023 through 2027, each compiled against its
  own `RevitAPI.dll`.
- **Verified live** against real models rather than mocks: the full tool
  surface, the upgrade guard refusing a real older-year family, the commit
  contract's rollback on a geometry change, cancellation measured mid-flight,
  and `job_status` answering while Revit's UI thread was inside the very
  command it describes.
- **Unsigned.** Revit raises its "Security - Unsigned Add-In" dialog until a
  certificate is in place, and it reappears after every update — the decision
  is remembered per binary, not permanently.
- **Known limits, stated**: `excel_write_rows` appends below an Excel Table
  without expanding the table's range (reported per call); a catalog that is
  neither UTF-8 nor Latin-1 is decoded as Latin-1 and says so; cancelling an
  MCP request stops *you waiting* and does not stop Revit, because the Revit
  API cannot interrupt work already on its UI thread.

## Security

`horizun_execute_python` runs arbitrary Python inside Revit with the rights of
the signed-in user. It is **disabled by default** and must be switched on per
machine in `%USERPROFILE%\.horizun\settings.json`. There is no network surface:
named pipes are not reachable across a network, and the server speaks stdio to
whatever launched it. The full threat model — what is defended, and what
deliberately is not — is in [docs/security-model.md](docs/security-model.md),
and it is written to be argued with.

## License

**Apache License 2.0** — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
Third-party components remain under their own licenses, listed with versions in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The Autodesk Revit API is referenced at build time and never redistributed.
Revit, Autodesk and Autodesk Docs are trademarks of Autodesk, Inc. This project
is not affiliated with, endorsed by, or sponsored by Autodesk, Inc.

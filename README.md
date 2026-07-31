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

## Install — tell your agent to do it

Everything is built from this tree, on your machine, against the Revit you
already have. **No executable downloads.** Clone the repo and tell Claude Code
or Codex:

> Instala este MCP siguiendo AGENTS.md

The agent reads [AGENTS.md](AGENTS.md) and runs the whole procedure: it checks
the prerequisites, builds the add-in for every Revit year on the machine (each
against its own `RevitAPI.dll`), builds the MCP server, installs both, verifies
every installed binary against what was staged, and tells you how to register
the server with your client.

Or run it yourself — same script, same result:

```bash
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Prerequisites: Windows, a Revit 2023–2027, the .NET SDK 8+, and **Revit
closed** (the script refuses otherwise, changing nothing). Then register the
server:

```bash
claude mcp add horizun -- "%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

On the next Revit start, expect the **"Security - Unsigned Add-In"** dialog and
choose *Always Load* — see Status below; it returns after every update, and it
can open on a monitor you are not looking at. Once a document is open, a
**Horizun Hub** ribbon tab appears; its *Estado del puente* button answers "is
this working, and which version?" without leaving Revit. From your client,
`horizun_health` answers the same question with the commit included.

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

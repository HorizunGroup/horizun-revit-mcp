# Connect Horizun Revit MCP to a client

Install the [Windows release](INSTALL.md) first. Every client connects to the
same installed `horizun-mcp.exe`; the extension does not bundle the server.

## Final step by client

| Client | Setup prepares | You finish |
|---|---|---|
| Codex | Deferred registration when its configuration exists | Close the client, let registration finish, then reopen it |
| Claude Code | Deferred user-scope registration when its configuration exists | Close the client, let registration finish, then reopen it |
| Claude Desktop | `.mcpb` and illustrated instructions in Documents\Horizun-Revit-MCP | Install the extension inside Claude Desktop and restart the app |
| ChatGPT Work | Helpers for the installed server's Secure MCP Tunnel | Complete the tunnel configuration and connection |
| Other stdio clients | The installed server executable | Register its full path in the client's configuration |

## Claude Desktop

**Video guide — Claude Desktop Free:** [install and connect Horizun Revit MCP](https://www.youtube.com/watch?v=3kp-we7MIvk).
This Spanish-language tutorial from Horizun Hub covers setup with the free
Claude Desktop plan. **Tutorial en español:** instalación y conexión con
Claude Desktop gratuito. Follow the written steps below for the current package.

1. Run Setup with Revit closed.
2. Open **Documents → Horizun-Revit-MCP**, the folder Setup opens at completion.
   Use the `.mcpb` and the included English or Spanish illustrated instructions.
3. In Claude Desktop open **Settings → Extensions**. Drag the `.mcpb` onto the
   page, or choose **Advanced settings → Install extension** and select it.
4. Review the access prompt, enable the extension and restart Claude Desktop.
5. Start Revit, open a model and call `horizun_health` from Claude Desktop.

The in-app extension steps are also documented in
[Anthropic's local MCP installation guide](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop).

Setup does not perform step 3. A successful Setup is not proof that the extension
has been installed or that the client is connected. Claude Code is not required.
On systems with redirected Documents, use the folder Setup actually reports.

If the package was not handed over, the staged copy is under
`%LOCALAPPDATA%\Programs\Horizun\MCP\server\integrations\claude-desktop`.
Copy it to a visible folder, or use the extension recovery helper below.

## Codex and Claude Code

The completion helper waits for running clients to close, preserves other MCP
entries, makes backups and verifies its changes. It records pending states in
`%LOCALAPPDATA%\Horizun\install-status.json`; do not infer connection from
Setup's exit code alone.

For a client installed after Horizun, or a missing configuration, close the
client and run the installed registration helper. The path below works from any
folder in Windows PowerShell; a source checkout and PowerShell 7 are not needed.

```powershell
$clientTools = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\client-tools'
& (Join-Path $clientTools 'register-client.ps1') -Client Both
```

### No duplicate registrations

Registering `horizun-revit` also removes what an earlier install left behind **for
this same product** in the same client, so two entries can never start two
instances. An entry goes only when it is not the current name and either runs the
very same `horizun-mcp.exe` being registered (any name) or carries a retired name
(`horizun`, `horizun-next`) and launches a `horizun-mcp` executable. A retired
name that launches some other program is kept and reported; no other server is
ever touched.

- **Codex:** the `[mcp_servers.<old>]` table and its `[mcp_servers.<old>.*]`
  subtables are cut from `config.toml` in the same write that adds the current
  entry, after a dated backup. The result is checked to be the original minus
  those tables (nothing added, every other table present) and, when a Python with
  `tomllib` is available, to still parse; otherwise nothing is written. Line
  endings are kept; the file is written as UTF-8 without a BOM, through a temporary
  file and a move, like every client file the installer touches.
- **Claude Code:** removed with `claude mcp remove <old> --scope user`, never by
  editing `~/.claude.json` (Claude Code rewrites it while it runs). If the CLI is
  not on PATH, or fails, the registration still completes and the exact command
  to run is printed. Entries scoped to a project are reported, not changed.
- **Claude Desktop:** removed from `claude_desktop_config.json` (backup first)
  by the extension helper, one member at a time, every other byte kept, UTF-8
  without a BOM; while Claude Desktop is running the entry is kept and
  the helper says so.

The run prints what it removed and is idempotent. `-KeepOtherEntries` (on
`register-client.ps1` and `install-claude-desktop-extension.ps1`) keeps them, for
running two builds side by side on purpose; `-WhatIfOnly` shows the change
without writing.

Manual CLI registration is also available after the client closes:

```powershell
$serverPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
claude mcp add --scope user horizun-revit -- "$serverPath"
codex mcp add horizun-revit -- "$serverPath"
```

Use only the command for your client. Codex configuration must retain:

```toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

Replace `<you>` with the real account path. JSON needs doubled backslashes;
TOML single-quoted strings do not. Other clients should use that same expanded
path and allow enough time for long Revit scans.

## Tool profiles: a shorter list at the start of every session

A client without deferred tool loading reads the whole `tools/list` before the first
question: about 105 000 tokens for the 113 tools of `safe_write`. A **tool profile** lists the
tools of one role instead, and every other tool stays callable.

| Profile (`safe_write`) | Lists | `tools/list` |
|---|---:|---:|
| `completo` (no profile; an upgrade keeps it) | 113 | 421 731 B (~105 000 tokens) |
| **`esencial`** (written by a new install) | 14 | 72 566 B (~18 100 tokens) |
| `esencial` + `HORIZUN_SCHEMA_MODE=compact` | 14 | 17 687 B (~4 400 tokens) |
| `consulta` | 37 | 149 849 B (~37 400 tokens) |
| `documentacion` | 34 | 157 401 B (~39 300 tokens) |
| `modelado` | 58 | 208 744 B (~52 100 tokens) |
| `auditoria` | 55 | 208 364 B (~52 000 tokens) |

- `esencial`: the `local` tools (health, document facts, list and query, model scan, capture,
  the typed writes, undo).
- `consulta` adds reading; `modelado` adds model writes, architecture, structure and MEP;
  `documentacion` adds views, sheets, annotation, planimetry and schedules; `auditoria` adds
  reading, audits, clash and coordination. A comma-separated list of toolsets also works
  (`documentacion,read`). English names are accepted (`essential`, `query`, `modeling`,
  `documentation`, `audit`, `full`).
- **It is visibility, not permission.** A profile also lists `horizun_list_tools` (search every
  callable tool, Spanish or English), `horizun_describe_tool`, `horizun_call_tool` (call any
  callable tool by name) and `horizun_activate_toolset` (show one more toolset for this
  session). A tool the profile does not list can still be called, under the same permission
  profile, dry run and re-read. What the permission profile, `denied_tools`, `allowed_tools`
  or `HORIZUN_TOOLSETS` refuse stays refused, and a profile never lists it.
- **Where it comes from**, first match wins: `HORIZUN_PROFILE` in the client's MCP server
  entry, then `"tool_profile"` in `%USERPROFILE%\.horizun\settings.json`, then nothing, which
  is `completo`. A **new** install writes `"tool_profile": "esencial"`; an upgrade, or a
  machine that already has `settings.json`, is left as it was. An unknown name lists every
  tool and `horizun_health.tool_profile.problem` says why.
- `horizun_health.tool_profile` reports the profile, its source, how many tools are listed
  and how many are callable.

Measured in the 27-task battery on an English Revit 2024: with `esencial` + `compact`, Claude
Haiku completed 26 of 27 tasks in two runs (about 55 000 input tokens per task) and qwen3:8b 13 of
27 (about 23 400). For a small or local model, add `HORIZUN_SCHEMA_MODE=compact` to the client
entry. Details in [diagnostico-modelos-pequenos.md](diagnostico-modelos-pequenos.md).

To see every tool again, set `"tool_profile": "completo"` in `settings.json` (or
`HORIZUN_PROFILE=completo` in the client) and restart the client.

## Small and local models

Every session pays for `tools/list` before the first question. A model served on this
machine, or any client with a short context window, can see a smaller surface. Three settings
do that, all in **the client's own** MCP server entry, and none widens the permission profile.

| Setting (`safe_write`) | Tools | `tools/list` |
|---|---:|---:|
| default (`HORIZUN_SCHEMA_MODE=full`) | 113 | 421 731 B (~105 000 tokens) |
| `HORIZUN_TOOLSETS=local` | 11 | 70 470 B (~17 600 tokens) |
| `HORIZUN_TOOLSETS=local` + `HORIZUN_SCHEMA_MODE=compact` | 11 | 15 393 B (~3 850 tokens) |
| `HORIZUN_SCHEMA_MODE=facade` | 4 | 4 352 B (~1 100 tokens), schemas on demand |

Tokens are characters / 4, as `horizun_health` reports them. Measured 2026-10-31; how, and the
full results, in [diagnostico-modelos-pequenos.md](diagnostico-modelos-pequenos.md).

### Which one to use

In a 27-task battery on an English Revit 2024:

- **`local` + `compact`** for a small or local model. Claude Haiku completed 26 of 27 tasks
  with it, as many as with `local` alone before this change, using 58 % fewer input tokens per
  task. qwen3:8b over Ollama completed 12 of 27 in two runs, as before, using 71–78 % fewer.
- **`local`** alone when the model reads long schemas well and you want every argument visible.
- **`facade`** when the context is tiny. A small model struggles with its three steps (list,
  describe, call): qwen3:8b completed 7 of 27.
- **Neither** for a large model with a long context: the full list stays the default.

### `HORIZUN_TOOLSETS=local`

Eleven tools: `horizun_health`, `get_document_info`, `horizun_list_elements`,
`horizun_query_model`, `horizun_model_scan`, `horizun_capture_view` (`full_write` and above), the typed writes
`horizun_create_elements`, `horizun_write_params_verified`, `horizun_delete_verified`,
`horizun_transform_elements`, `horizun_undo`, and `horizun_activate_toolset`. Selected
**alone**, `local` does not bring `horizun_target`, `horizun_job_status` and
`horizun_submit_job`: in the battery they were never used, or were called instead of
`horizun_health`. Add any other toolset (`local,documentation`) and the core returns. Model
diff, quantities, navigation and view management are in `read`, `audit`,
`coordination` and `documentation`. `local` never carries the Python surface.

### `HORIZUN_SCHEMA_MODE=compact`

The same tools as `full`, since a toolset still decides which ones appear. Each tool shows a
description of at most 200 characters and only its common arguments, and the short notes say
where each value comes from: the exact document title from `horizun_health`, a level id from
`horizun_query_model`, and that leaving out the view captures the active one. Calls are still
validated against the complete contract. An argument the short schema leaves out is still
accepted, and the exact schema is at `horizun://contract/tools/{tool}`. Dry run, confirmation
and re-read are unchanged. In this mode only, `horizun_query_model` answers
`response_mode=compact` when the call does not choose one.

### `HORIZUN_SCHEMA_MODE=facade`

`tools/list` carries `horizun_health`, `horizun_list_tools`, `horizun_describe_tool` and
`horizun_call_tool`. `horizun_list_tools` takes `query`, a description of the task in Spanish or
English with or without accents, for example `{"query": "cuántos muros hay"}`. It returns the
best matches, and separately the tools that exist but sit in a toolset that is not active.
`horizun_call_tool` is the same typed call, with the same checks.

### Toolsets on demand and Spanish names

A session whose client restricts toolsets in its **environment** (`HORIZUN_TOOLSETS`) can open
one more for that session only with `horizun_activate_toolset {"toolset": "documentacion"}`.
Nothing is saved, a restart forgets it, and it never opens what the permission profile or
`denied_tools`/`allowed_tools` switch off. A selection saved in `settings.json` is also enforced
by the add-in, so it is changed there, not from a session. Toolset names accept Spanish aliases
in any case, with or without accents: `consulta`/`lectura` (read), `modelado`/`modelo` (model),
`arquitectura`, `estructura`, `documentacion`, `planimetria`, `auditoria`, `coordinacion`,
`tablas` (schedules), `familia`, `interoperabilidad` and `administracion`.

`horizun_health` reports `toolsets.active`, `toolsets_not_active`, `tools_not_advertised`,
`activated_this_session` and `schema_mode`. Restart a client that does not refresh
`tools/list` after changing a setting.

### Setting it per client

Use the installed path the installer printed.

**Claude Desktop** reads `claude_desktop_config.json` (`%APPDATA%\Claude\` on Windows; an MSIX
install keeps it elsewhere, see above). Add `env` to the entry, then quit Claude Desktop
completely and start it again:

```json
"horizun-revit": {
  "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Horizun\\MCP\\server\\horizun-mcp.exe",
  "env": { "HORIZUN_TOOLSETS": "local", "HORIZUN_SCHEMA_MODE": "compact" }
}
```

**Claude Code** takes the server name **before** `-e`, because `-e` accepts several `KEY=value`
pairs and would read a name that follows it as one more:

```powershell
$serverPath = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\horizun-mcp.exe'
claude mcp add --scope user horizun-revit -e HORIZUN_TOOLSETS=local -e HORIZUN_SCHEMA_MODE=compact -- "$serverPath"
```

**Codex** (`%USERPROFILE%\.codex\config.toml`):

```toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
env = { HORIZUN_TOOLSETS = "local", HORIZUN_SCHEMA_MODE = "compact" }
```

**LM Studio** (0.3.17 or later) reads `mcp.json` in the `mcpServers` notation above; open it
from **Program → Install → Edit mcp.json**. Load the model with a context length that fits
(next section). Not yet verified here: that `env` reaches the server. Check
`horizun_health.schema_mode` after the first call.

**Ollama** serves the model and does tool calling. Ollama itself does not start MCP servers,
so it needs an MCP client that talks to it. The battery used a minimal loop over Ollama's
`/api/chat` that passes this server's tools to the model
(`scripts/eval/small-model-battery.py --agent ollama`). That is the route measured here.
Terminal MCP clients for Ollama, such as `ollmcp`, take the same `env` keys; they have not been
verified with Horizun yet.

### Context window

Local runtimes often start small; Ollama defaults to 4 096 tokens on GPUs under 24 GiB. The
battery ran qwen3:8b with `num_ctx=32768`, which fits `local` + `compact` with room for
replies. `local` alone (~17 600 tokens) also fits; `full` does not. On an 8 GB GPU,
`OLLAMA_FLASH_ATTENTION=1` and `OLLAMA_KV_CACHE_TYPE=q8_0` for `ollama serve` kept 76 % of the
model on the GPU, against 61 % without them. Check what the runtime allocated under `CONTEXT`
and `PROCESSOR` in `ollama ps`.

## ChatGPT Work

ChatGPT reaches the installed stdio server through OpenAI's Secure MCP Tunnel.
Setup installs the helper; **it does not connect ChatGPT**. That needs objects in
your OpenAI account (a tunnel, a runtime API key, a developer-mode app) and
OpenAI's own `tunnel-client`, which Horizun never downloads for you.

**1. Get the right package.** On
[OpenAI's tunnel-client releases](https://github.com/openai/tunnel-client/releases/latest)
download the **full client** for your machine:

| Windows | Download | Not these |
|---|---|---|
| x64 | `tunnel-client-v<version>-windows-amd64.zip` | `tunnel-client-runtime-…`, `tunnel-client-runtime-cloudflared-…` |
| ARM64 | `tunnel-client-v<version>-windows-arm64.zip` | `tunnel-client-runtime-…`, `tunnel-client-runtime-cloudflared-…` |

Extract the whole ZIP into one folder and keep its files together:
`tunnel-client.exe` runs the `cloudflared.exe` beside it. The *runtime* packages
only contain `run` - no `init`, `doctor` or `--mcp-command` - and renaming one
to `tunnel-client.exe` does not change that; the helper detects the variant from
the executable's own answers and says so.

**2. Run the helper**, pointing it at that executable once; it is remembered
after it has been proven compatible:

```powershell
$clientTools = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\client-tools'
& (Join-Path $clientTools 'chatgpt-tunnel.ps1') -Status -TunnelClientPath 'C:\path\to\tunnel-client.exe'
& (Join-Path $clientTools 'chatgpt-tunnel.ps1') -SetApiKey          # runtime key, read without echo, DPAPI
& (Join-Path $clientTools 'chatgpt-tunnel.ps1') -Init -TunnelId tunnel_...
& (Join-Path $clientTools 'chatgpt-tunnel.ps1') -Doctor
& (Join-Path $clientTools 'chatgpt-tunnel.ps1') -Start -IUnderstandTrafficLeavesThisMachine
```

Each step refuses, and says why, when something it needs is missing; nothing
after a failed step runs. The profile is written to
`%LOCALAPPDATA%\Horizun\integrations\chatgpt\profiles\horizun-revit.yaml` and
every step uses that directory. `-Stop` stops only the tunnel this helper started
(verified by process id, start time and executable); `-Revoke` also forgets the
key and that profile. A `horizun-revit` profile left in tunnel-client's default
directory by an earlier version is reported and left untouched.

**3. Connect ChatGPT**: create the developer-mode app with *Tunnel* as its
connection, then make one tool call.

**Reading `-Status`.** Four facts are reported separately, because they are
different facts:

| Layer | Evidence |
|---|---|
| process | the tunnel-client this helper started is running (verified identity) |
| local health | its `/readyz` answers 2xx, and the operating system shows it listening on loopback only |
| contact with OpenAI | `commands_poll_last_successful_timestamp_seconds` on its loopback `/metrics` is recent (below) |
| ChatGPT -> Revit | **never verified locally** - only a real tool call from ChatGPT proves it |

`/readyz` stays green while every poll fails, so the state is *configured* only
when the last successful poll is recent. "Recent" is derived from the poll
settings the tunnel actually runs with - `poll_timeout` and
`poll_deadline_guardrail`, resolved as tunnel-client resolves them (environment,
then profile, then its defaults of 30 s and 5 s). A healthy idle client completes
one empty poll per `poll_timeout + guardrail`; the window is two such cycles plus
20 s, **90 s with the defaults**, so one slow cycle is not a failure. A new process
is *connecting*, not failed, until its first poll could have completed
(`min(initial_poll_timeout, poll_timeout) + guardrail + 20 s`, 55 s by default).
These numbers come from OpenAI's configuration reference and were measured against
a local stand-in for the control plane; they have not been validated against
OpenAI's service.

The health listener is requested as `127.0.0.1:0` in the profile and again as a
flag on `run`, and `-Status` reads the addresses the process really listens on.

The runtime key reaches tunnel-client through its environment block. The tunnel
is started with ShellExecute (so it outlives the window that started it and holds
none of its handles), which copies the helper's own environment; the key is
placed there, in process scope only, for the instant of the start, and the
variable's previous value - or its absence - is restored whether the start
succeeds or fails. Nothing is written to the user or machine environment.

MCP requests and replies travel through OpenAI-hosted infrastructure while the
tunnel runs; see the [privacy policy](PRIVACY.md). Account and workspace
controls vary by organisation.

## Diagnose or recover an installed integration

```powershell
$clientTools = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\client-tools'
& (Join-Path $clientTools 'diagnose-integrations.ps1')
& (Join-Path $clientTools 'complete-install.ps1') -StatusOnly
& (Join-Path $clientTools 'install-claude-desktop-extension.ps1') -Diagnose
```

To hand over the Claude Desktop package again, close Claude Desktop and use:

```powershell
& (Join-Path $clientTools 'install-claude-desktop-extension.ps1') -Extension
```

That helper may ask where to put the file; use the exact returned path. Its
`pending_user_action` result means the in-app extension installation remains.
For an installation where `${HOME}` is not expanded, the diagnostic and helper
can prepare a package with the resolved server path for that machine.

Advanced configuration-file recovery still exists in that helper without
`-Extension`; it is a separate repair operation, not Setup's default procedure.
Do not edit a configuration underneath a running client.

The current Start menu contains the product folder and Hub link. Diagnostics
are available at the installed paths above. After recovery, verify through the
actual client with `horizun_health`: version, commit and active document.

Connecting a client does not grant Python permission. Arbitrary code remains
disabled until the owner explicitly enables it.

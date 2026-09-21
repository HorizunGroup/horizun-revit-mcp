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
| local health | its loopback `/readyz` answers 2xx |
| contact with OpenAI | `commands_poll_last_successful_timestamp_seconds` on its loopback `/metrics` is less than **90 s** old |
| ChatGPT -> Revit | **never verified locally** - only a real tool call from ChatGPT proves it |

`/readyz` stays green while every poll fails, so the state is *configured* only
when the last successful poll is recent. 90 s is three default poll cycles.

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

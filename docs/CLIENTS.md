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

1. Run Setup with Revit closed.
2. Open **Documents → Horizun-Revit-MCP**, the folder Setup opens at completion.
   Use the `.mcpb` and the included English or Spanish illustrated instructions.
3. In Claude Desktop open **Settings → Extensions**. Drag the `.mcpb` onto the
   page, or choose **Advanced settings → Install extension** and select it.
4. Review the access prompt, enable the extension and restart Claude Desktop.
5. Start Revit, open a model and call `horizun_health` from Claude Desktop.

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

The integration uses OpenAI's Secure MCP Tunnel for the installed stdio server.
It does not depend on Codex or Claude Code. Setup installs the helper; inspect
its status and follow the reported connection steps:

```powershell
$clientTools = Join-Path $env:LOCALAPPDATA 'Programs\Horizun\MCP\server\client-tools'
& (Join-Path $clientTools 'chatgpt-tunnel.ps1') -Status
```

Account and workspace controls can vary. Do not publish credentials or equate a
locally installed helper with a connected tunnel. See the helper's documented
options for configuration and the [privacy policy](PRIVACY.md).

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

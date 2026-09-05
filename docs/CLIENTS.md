# Connect Horizun Revit MCP to a client

Horizun Revit MCP has one runtime: the installed `horizun-mcp.exe` speaks MCP
over standard input/output and the Revit add-in speaks to it over the local named
pipe. Codex, Claude Code, Claude Desktop and ChatGPT Work all reach that same executable. There is
no client-specific Revit implementation.

## One installer, four clients

The Windows Setup attached to each GitHub release is the universal installation
path. It installs the server and every Revit 2023–2027 add-in present in the
package, then safely prepares each client found on the machine:

| Client | What Setup can finish | Remaining action |
|---|---|---|
| **Codex** | Adds `[mcp_servers.horizun-revit]` beside existing servers after Codex closes. | Restart Codex. |
| **Claude Code** | Adds `mcpServers.horizun-revit` beside existing servers after Claude closes. | Restart Claude Code. |
| **Claude Desktop** | Stages and validates a `.mcpb` that names the installed server. | Install that extension once inside Claude Desktop. |
| **ChatGPT Work** | Installs the tunnel helpers for the same server. | Create the OpenAI tunnel, store its API key locally and start it. |

The configuration formats are owned by the clients, so there is no single file
that all three consume. Setup is the single user-facing flow: it deploys one
server, writes the two safe configuration entries, and prepares the one extension
Claude Desktop requires.

Configuration is never rewritten underneath a running client. The completion
helper waits for a quiet window, creates a timestamped backup, preserves every
other MCP entry, reads the result back, and records durable status in:

```text
%LOCALAPPDATA%\Horizun\install-status.json
```

## Install from GitHub Releases

1. Open the latest GitHub release.
2. Download `horizun-mcp-<version>-setup.exe` and `SHA256SUMS.txt`.
3. Verify the setup hash:

   ```powershell
   Get-FileHash .\horizun-mcp-<version>-setup.exe -Algorithm SHA256
   ```

4. Close Revit. Run Setup once.
5. Restart the client you want to use, start Revit, open a document and call
   `horizun_health`.

The public Setup is unsigned under the repository's published signing policy.
Check its SHA-256 against the release before running it. The installer verifies
the staged and installed files independently.

The same release can be installed non-interactively from PowerShell:

```powershell
$s = irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1
& ([scriptblock]::Create($s)) -AllowUnsigned
```

Use `-Version <tag>` to pin a version or `-Interactive` to show the Setup wizard.

## Codex

Setup registers Codex automatically when `%USERPROFILE%\.codex\config.toml`
exists. It adds this table and leaves all other tables intact:

```toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

Manual recovery, after closing Codex:

```powershell
codex mcp add horizun-revit -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

Keep the two timeout values above. Large scans and batch model opens can take
minutes while Revit owns its UI thread.

## Claude Code

Setup registers Claude Code automatically when `%USERPROFILE%\.claude.json`
exists. The registration is at user scope and remains available across projects.

Manual recovery, after closing Claude Code:

```powershell
claude mcp add --scope user horizun-revit -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

Claude Code is optional. Claude Desktop does not invoke it or depend on it.

## Claude Desktop

Claude Desktop uses a Desktop Extension rather than either CLI configuration.
Setup ships the release `.mcpb` and a preparation helper. From the Start menu,
open **Horizun → Instalar o reparar la extensión de Claude Desktop**, or run:

```powershell
pwsh -File scripts/install-claude-desktop-extension.ps1
```

The wizard:

1. Detects classic and Store/MSIX installations.
2. Proves the installed server completes `initialize` and `tools/list`.
3. Rewrites the extension manifest with the absolute server path for this Windows user.
4. Stages the exact file under
   `%LOCALAPPDATA%\Horizun\integrations\claude-desktop\`.

Finish inside Claude Desktop:

1. Open **Settings → Extensions → Advanced settings**.
2. Select **Install Extension…** and choose the staged `.mcpb`.
3. Restart Claude Desktop.
4. Start Revit, open a document and call `horizun_health`.

This final click cannot be automated through a documented Claude Desktop command.
The installer records `pending_user_action` and the exact package path instead of
claiming the extension was installed.

Manual JSON configuration remains available as recovery only:

```json
{
  "mcpServers": {
    "horizun-revit": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Horizun\\MCP\\server\\horizun-mcp.exe",
      "args": []
    }
  }
}
```

Use the fully expanded path. JSON requires doubled backslashes.

```powershell
pwsh -File scripts/install-claude-desktop-extension.ps1 -ConfigFallback
```

The fallback refuses to edit the file while Claude Desktop is running because
the application can overwrite it from memory when it exits.

## ChatGPT Work

ChatGPT Work connects to the installed stdio server through OpenAI's Secure MCP
Tunnel and does not require Codex or Claude Code. This route was verified in the
ChatGPT desktop Work interface with a free account on 2026-09-04. Account and
workspace controls can vary, so diagnostics report observed tunnel state.

```powershell
pwsh -File scripts/chatgpt-tunnel.ps1 -Status
pwsh -File scripts/chatgpt-tunnel.ps1 -SetApiKey
pwsh -File scripts/chatgpt-tunnel.ps1 -Init -TunnelId tunnel_...
pwsh -File scripts/chatgpt-tunnel.ps1 -Start -IUnderstandTrafficLeavesThisMachine
```

The helper uses OpenAI's `tunnel-client`, protects the API key with Windows DPAPI
for the current user, opens no inbound port and runs the same `horizun-mcp.exe`.

## Diagnose and repair

```powershell
pwsh -File scripts/diagnose-integrations.ps1
pwsh -File scripts/install-claude-desktop-extension.ps1 -Diagnose
pwsh -File scripts/register-client.ps1 -Client Both -WhatIfOnly
```

Installed Start-menu shortcuts expose the same diagnostics. Every client should
ultimately run `horizun_health`; a healthy response names the active document,
product version, stamped commit and contract hash.

`horizun_execute_python` remains disabled by default for every client. Connecting
a client does not grant arbitrary-code permission.

# Horizun Revit MCP 1.2.0

Version 1.2.0 installs one local MCP bridge for Revit 2023–2027 and prepares it
for Codex, Claude Code and Claude Desktop. All three clients run the same
`horizun-mcp.exe`; client-specific setup only tells each application how to start
that executable.

## Install once

1. Download `horizun-mcp-1.2.0-setup.exe` and `SHA256SUMS.txt` from this release.
2. Verify the setup hash with
   `Get-FileHash .\horizun-mcp-1.2.0-setup.exe -Algorithm SHA256`.
3. Close Revit and run Setup.
4. Restart the MCP client, start Revit, open a document and call
   `horizun_health`.

The release is unsigned under the repository's published policy. The SHA-256
file, package manifest, SBOM and installed-byte verification accompany the
installer.

PowerShell installation without Git or the .NET SDK:

```powershell
$s = irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1
& ([scriptblock]::Create($s)) -Version v1.2.0 -AllowUnsigned
```

## Client completion

| Client | What the installer does | What you do |
|---|---|---|
| **Codex** | Adds the `horizun-revit` MCP table to `%USERPROFILE%\.codex\config.toml`, preserving every existing server. | Restart Codex. |
| **Claude Code** | Adds the user-scope `horizun-revit` server to `%USERPROFILE%\.claude.json`, preserving every existing server. | Restart Claude Code. |
| **Claude Desktop** | Builds and validates a machine-resolved `.mcpb` under `%LOCALAPPDATA%\Horizun\integrations\claude-desktop\`. | Settings → Extensions → Advanced settings → Install Extension; select that `.mcpb`, then restart Claude Desktop. |

Claude Desktop does not require Claude Code. If neither CLI exists, Setup still
installs the Revit bridge and prepares the Desktop Extension.

The three configuration formats cannot share one configuration file, but the
Windows installer is the universal flow. It waits rather than editing a running
client, takes timestamped backups and records progress in
`%LOCALAPPDATA%\Horizun\install-status.json`.

Full installation, repair and manual recovery instructions:
[Connecting a client](CLIENTS.md).

## First Revit start

Without an already trusted publisher, Revit displays a Security dialog. Verify
the downloaded hash, then choose **Always Load**. The dialog may open on another
monitor. Once a document is open, the **Horizun Hub** ribbon tab provides
**Estado del puente** for a local status check.

## Verification

A correct `horizun_health` response reports `status: healthy`, version `1.2.0`,
the release commit and the same contract hash from the server and add-in. A
contract mismatch means one side belongs to an older installation; close Revit
and run Setup again.

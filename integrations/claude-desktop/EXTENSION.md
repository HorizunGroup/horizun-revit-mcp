# Horizun Revit MCP — Claude Desktop extension

This extension connects Claude Desktop to an Autodesk Revit running on the same
Windows machine. It is the bridge's own packaging of itself; the tools, the
contract and the refusals are the ones documented in the repository.

## What it contains, and what it does not

It contains a manifest, two icons and this file. **It does not contain the MCP
server.** It declares the command of the `horizun-mcp.exe` that is already
installed:

```
%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe
```

That is deliberate. The server and the Revit add-in share a contract hash and
refuse to pair across versions, so a copy bundled inside this extension would be
frozen at the moment it was installed and would start refusing the add-in after
the next update. Pointing at the installed server means the extension follows
every update without a second action.

**Install the product first.** Without it, Claude Desktop will show the extension
as installed and the tools will never appear, because the command it names does
not exist.

## Installing

1. Install Horizun Revit MCP — the Windows installer, or `install.ps1` from the
   repository. Both put the server at the path above.
2. In Claude Desktop: **Settings → Extensions → Advanced settings → Install
   Extension**, and choose the `.mcpb` file.
3. Restart Claude Desktop.
4. Start Revit, open a document, and ask Claude to call `horizun_health`.

Claude Code is **not** required, is not used, and is not installed by this.

## Verifying

`horizun_health` is the first call for any session. It answers with the Revit
version, the process, the active document, the server version and the commit both
halves were built from. Every other command acts on the document it names.

If the tools do not appear:

- the server is not installed at the path above, or
- Claude Desktop has not been restarted since the extension was installed, or
- the host did not substitute `${HOME}` in the command.

The repository ships a repair path for all three:

```powershell
pwsh -File scripts/install-claude-desktop-extension.ps1 -Diagnose
```

## What it can and cannot do

- Reads and audits the **active** document of a running Revit. It does not open
  Revit and does not choose documents by remote path.
- Typed writes are re-read from the model after the commit; a command never
  reports work it did not verify.
- `horizun_execute_python` is **disabled by default**. Installing this extension
  does not enable it. Only the owner of the machine can, from inside Revit or
  through the administrative script.

Apache-2.0. Source: <https://github.com/HorizunGroup/horizun-revit-mcp>

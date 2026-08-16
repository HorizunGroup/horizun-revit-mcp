## Install

**You need:** Windows, at least one Revit 2023–2027, and **Revit closed**. Nothing else — no Git, no .NET SDK on this path.

**1 · Download and verify.** Take `horizun-mcp-<version>-setup.exe` and `SHA256SUMS.txt` from the assets below, then check the download before you run it:

```powershell
$exe  = Get-Item .\horizun-mcp-*-setup.exe
$want = (Select-String -Path .\SHA256SUMS.txt -Pattern 'setup.exe').Line.Split(' ')[0]
if ((Get-FileHash $exe -Algorithm SHA256).Hash -ieq $want) { "OK - matches the published hash" }
else { "STOP - it does not match. Do not run it." }
```

**2 · Run it.** Setup deploys a different add-in binary for each Revit year installed on the machine, plus the MCP server, and then completes Claude Code / Codex registration itself — waiting for an open client to close rather than editing its configuration underneath it. It never removes your other MCP entries.

**3 · Start Revit and check.** A **Horizun Hub** tab appears in the ribbon once a document is open; its *Estado del puente* button answers "is this working, and which version?" without leaving Revit. From your MCP client, `horizun_health` answers the same with the commit included.

Prefer one paste? This selects the latest release, downloads the setup and `SHA256SUMS.txt` from that same release, verifies the complete SHA-256, installs quietly and finishes client registration:

```powershell
irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1 | iex
```

> **Publisher warning.** Releases before 1.0.0 may be published **without a publicly trusted code-signing certificate**. When that is the case, Windows SmartScreen and Revit warn about an unknown publisher — that is expected, and the SHA-256 check above is how you verify the download instead. Public signing becomes a mandatory release gate at 1.0.0; see the [code signing policy](https://github.com/HorizunGroup/horizun-revit-mcp/blob/main/CODE-SIGNING-POLICY.md).

**Also in this release:** `manifest.json` (the exact payload), `package-hashes.json`, `sbom.json` (CycloneDX), and one `live-<year>.json` verification report per supported Revit year — this project promotes a build to stable only when a live report exists for every year, rather than on a local success claim.

Everything else — what the tools do, what they refuse, the security model — is in the [README](https://github.com/HorizunGroup/horizun-revit-mcp#readme).

---

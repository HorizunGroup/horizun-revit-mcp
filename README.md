# Horizun Revit MCP — Autodesk Revit automation

**English** · **[Español](README.es.md)**

Connect an MCP client to Autodesk Revit to inspect models, make verified BIM
edits, create families and prepare drawings, quantities and exports.
Free and open source under Apache-2.0. Built in Colombia 🇨🇴, part of
[Horizun Hub](https://horizunhub.com).

[![ci](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/ci.yml?branch=main&label=ci&logo=githubactions&logoColor=white)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/ci.yml) [![codeql](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/codeql.yml?branch=main&label=codeql&logo=github)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/codeql.yml) [![release](https://img.shields.io/github/v/release/HorizunGroup/horizun-revit-mcp?label=release&color=0696D7)](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest) [![Revit 2023–2027](https://img.shields.io/badge/Revit-2023%E2%80%932027-0696D7)](#install) [![MCP registry](https://img.shields.io/badge/MCP%20registry-io.github.HorizunGroup%2Fhorizun--revit--mcp-6E56CF)](https://registry.modelcontextprotocol.io/) [![license Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

## Install

**[Download the Windows installer](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest)**

- Requires **Windows x64, Revit 2023–2027 and an MCP client**.
- Setup includes the server runtime and matching Revit add-ins.
  **No Git, Visual Studio or .NET SDK is needed.**
- Close Revit before running Setup.
- Public releases are intentionally **unsigned**. SHA-256 checks verify the
  downloaded bytes; they do not authenticate a Windows publisher. Windows and
  Revit may show a publisher warning. See the [unsigned release policy](CODE-SIGNING-POLICY.md).

1. Open the release above and download `horizun-mcp-<version>-setup.exe` and
   `SHA256SUMS.txt` from **Assets**. The source ZIP is for development.
2. Verify the hash using the [installation guide](docs/INSTALL.md), then run Setup.
3. Complete your client's connection step:

| Client | Final connection step |
|---|---|
| **Codex / Claude Code** | Let Setup's helper register after the client closes, then reopen it. |
| **Claude Desktop** | Install the `.mcpb` delivered to **Documents\Horizun-Revit-MCP** from **Settings → Extensions**, then restart Claude Desktop. |
| **ChatGPT Work** | Complete the Secure MCP Tunnel setup for the installed server. |
| **Other stdio clients** | Register the installed executable using its full path. |

4. Start Revit, open a document and ask your client to call `horizun_health`.
   Confirm the active document and loaded version.

**Claude Desktop needs the extension installation inside the app.** Setup puts
the package and illustrated instructions in the Documents folder above. Drag
the package onto the Extensions page or use **Advanced settings → Install extension**.
The extension connects to the installed server; it does not replace Setup.
See [client instructions and recovery](docs/CLIENTS.md).

### Optional PowerShell bootstrap

This downloads the release installer, checks its hash and runs Setup quietly.
`-AllowUnsigned` acknowledges the publisher status described above. The same
client-specific final steps still apply; use `-Interactive` for the wizard.

```powershell
$s = irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1; & ([scriptblock]::Create($s)) -AllowUnsigned
```

The script is fetched from `main`; its checksum check applies to the downloaded
Setup. You can download and inspect the script first. See [installation options](docs/INSTALL.md).

## Version and compatibility

| Question | Authoritative source |
|---|---|
| Latest stable download | [GitHub latest release](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest), including its publication date and assets |
| Version of this source checkout | [Directory.Build.props](Directory.Build.props) |
| Version actually loaded | `horizun_health`: version, commit, Revit year and active document |
| MCP protocol implemented | **2025-11-25**, with earlier revisions listed in [ProtocolNegotiation.cs](src/Horizun.Server/ProtocolNegotiation.cs) |
| Published registry metadata | [Official registry entry](https://registry.modelcontextprotocol.io/v0.1/servers/io.github.HorizunGroup%2Fhorizun-revit-mcp/versions/latest) |

MCP 2026-07-28 support is pending. A recent product release does not imply support
for that protocol revision. Registry snapshots and search extracts may lag;
check the release before choosing a version. Upgrade release installations by
running the new Setup with Revit closed.

## What you can do

| Task | Examples and reference |
|---|---|
| Inspect and audit models | Host/link queries, quantities, clashes, schedules and model diagnostics. [First read-only audit](docs/QUICK-START-BIM.md) |
| Create and edit BIM elements | Levels, grids, architectural and structural elements, MEP, parameters and ordered plans. [Tool reference](docs/TOOLS.md) |
| Produce drawings | Views, sheets, tags, dimensions, detail elements and layout checks. [Drawing workflows](docs/PLANIMETRY-PRODUCTION.md) |
| Author families | Parameters, formulas, types, solids/voids and connectors. [Family authoring](docs/FAMILY-AUTHORING.md) |
| Convert DWG to BIM | Inspect, plan and apply a caller-supplied requirement set. [DWG workflow](docs/DWG-TO-BIM.md) |
| Quantify and deliver | Quantities by budget code, Excel, PDF/DWG/IFC/NWC/FBX exports and Power BI ingestion. [Quantities](docs/QUANTITIES-AND-BUDGET.md), [Power BI](docs/POWER-BI.md) |

[Workflow prompts](docs/WORKFLOWS.md) describe scope, permissions and expected
evidence. [Tool packs](docs/WHAT-CAN-HORIZUN-DO.md) and local permissions affect
which tools a client sees. A hidden tool is not proof that the product lacks it.

## Verification and evidence

**Typed writes are re-read after commit.** Read the per-tool result for rollback,
partial outcomes and limits. Arbitrary Python is **disabled by default**. The
owner's Python ON/OFF grant persists until revoked; its results are
**self-reported**, with `host_verified: false`.

Stable release assets include checksums, a payload manifest, SBOM and live Revit reports
for the supported years. These are publisher evidence for that release.
They do not establish a comparative result against other MCP servers or a
clean-machine client-installation success rate.

- [Benchmark method and historical results](docs/BENCHMARK.md)
- [Release policy](docs/RELEASE-POLICY.md) and [evidence status](docs/production-readiness.md)
- [Security model](docs/security-model.md), [privacy](docs/PRIVACY.md) and [vulnerability reporting](SECURITY.md)

Known limits include cancellation only before Revit begins a command, unavailable
coverage for unloaded links, and per-operation API/exporter constraints. Review
the [tool reference](docs/TOOLS.md) for the applicable limits.

## Development and ecosystem

[Build from source](docs/BUILDING.md) · [Architecture](docs/ARCHITECTURE.md) ·
[Contribute](CONTRIBUTING.md) · [Agent instructions](AGENTS.md) · [LLM summary](llms.txt)

The bridge is organisation-neutral. Project standards and catalogues are supplied
as inputs. [Horizun Hub](docs/HORIZUN-HUB.md) provides the wider ecosystem;
the optional [standards pack](standards/README.md) supplies editable examples.

**Apache-2.0:** [LICENSE](LICENSE), [NOTICE](NOTICE), [third-party notices](THIRD-PARTY-NOTICES.md).
The Revit API is not redistributed. Autodesk and Revit are trademarks of Autodesk;
this project is not affiliated with, endorsed by or sponsored by Autodesk.

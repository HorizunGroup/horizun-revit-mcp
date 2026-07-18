## Horizun Revit MCP

An MCP gateway for Autodesk Revit 2022–2027 whose tools **cannot report work they did not verify.**

Every write is re-read from the model after the commit; every count is the model's, not the caller's intent. This exists because we measured three real lies in the tools it replaces: a purge that reported 758 types deleted having deleted nothing (Revit rolled back silently), keynote tags reported placed while every one rendered empty, and a `Volume` parameter that disagreed with the real geometry by 42.7% on the beams you bill.

### Install

Download **`RvtMcp.Setup-*-win-x64.zip`**, unzip, and run `install.ps1`. It carries a self-contained MCP server and the six Revit plugin shells — no .NET SDK required on the target machine. The plugin auto-loads in Revit; point your MCP client at the server.

Prefer the .NET tool? `dotnet tool install --global Horizun.RevitMcp.Server` (from the attached `.nupkg`). Only need one Revit version's shell? Grab that `RvtMcp.Plugin.R##.zip`.

### The Horizun tool surface (13)

The four verbs every Revit process reduces to, plus the escape hatch and the host-side tools:

| Tool | What makes it different |
|---|---|
| `horizun_model_scan` | 12-section read; every bucket reports total vs shown vs truncated, every section `ok`/`failed(reason)` — a check that threw can't read as a clean zero |
| `horizun_write_params_verified` | One transaction, re-reads each value; outcomes are three-way `confirmed`/`not_written`/`unknown`, and it reports the type-write blast radius |
| `horizun_delete_verified` | Deletes/purges and re-resolves every id after the commit; names the unnamed dependents a purge cascades into |
| `horizun_document_session` | Open/save/close with a version gate that refuses an accidental irreversible upgrade; a save is "saved" only when the file on disk proves it |
| `horizun_set_keynote` | Announces that coding one element re-codes every sibling of its type *before* writing |
| `horizun_quantities` | Volume from all three sources Revit offers, side by side, with the disagreement measured — it refuses to pick one for you |
| `horizun_clash` | Reads the **linked** models, not just the host; a zero means zero, or it says why it's partial |
| `horizun_audit_model` | Pre-delivery audit; counts orphan group types (invisible file weight) and never hides a finding behind a score |
| `horizun_bind_shared_param` | The three shared-parameter gotchas that hang the session or lose bound categories, each measured back from the model |
| `horizun_family_apply` | Homologates a family in one transaction; rolls back if the geometry (Double-param count / `IsCustom`) moved |
| `horizun_catalog_lookup` | Leaf-vs-chapter from the parent column, never code shape — a real chapter code is refused |
| `horizun_excel_write_rows` | Edits an .xlsx through real Excel so Tables survive; verifies by reopening from disk, restores from backup on any mismatch |
| `horizun_execute_python` | Python against the live API **with the standard library** (`import json`, `re`, `csv`); a throw inside a transaction is rolled back for you |

### Attribution

Built on **Khoa Le — `bimwright/rvt-mcp`** (Apache-2.0). Hardening layer and the tools above: **Horizun**. The whole work remains Apache-2.0.

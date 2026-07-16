# KEI project SQLite via rvt-mcp (WAL-safe)

BIM delivery is shifting from humans alone authoring 3D/models to **AI agents
helping implement projects faster and more accurately**. Project metadata lives
in KEI SQLite under Revit’s process lock — agents must read/write **through
these tools**, never by replacing DB files from outside.

## Tools (toolset `kei`, on by default)

| MCP tool | Plugin command | Purpose |
|---|---|---|
| `revit_get_active_project_db` | `get_active_project_db` | Resolve `%APPDATA%\KEI\Database\Projects\*_local.db` |
| `revit_query_kei_database` | `query_kei_database` | Read-only SELECT / presets (`overview`, `equipment`, …) |
| `revit_write_kei_database` | `write_kei_database` | General DML write (any project table) via Revit + WAL |
| `revit_import_project_equipment` | `import_project_equipment` | Bulk upsert types + instances + TypedSpecs |

## Rules

1. **Keep Revit open** with the project loaded. Do **not** kill Revit to “unlock” SQLite.
2. Import / write run **inside the plugin process** with `PRAGMA journal_mode=WAL` + `busy_timeout` (default 30s).
3. Do **not** replace/copy `.db` / `.db-wal` while Revit or KEI holds the file.
4. Prefer `revit_import_project_equipment` for typed equipment bulk import; use `revit_write_kei_database` for broader project-data DML. DDL (`CREATE`/`DROP`/`ALTER`/`PRAGMA`/`ATTACH`) is blocked.

## General write (`revit_write_kei_database`)

```json
{
  "sql": "UPDATE ProjectEquipmentTypes SET Brand = 'ShinMaywa' WHERE ProjectTypeName = '005. Bơm nước thải'",
  "dryRun": false,
  "database": "auto",
  "busyTimeoutMs": 30000
}
```

Or multiple statements in one transaction (`statements` is a JSON **string** at the MCP boundary):

```json
{
  "statements": "[\"UPDATE ...\", \"INSERT INTO ...\"]",
  "dryRun": true
}
```

## Import payload example

```json
{
  "items": [
    {
      "projectTypeName": "005. Bơm nước thải",
      "categoryCode": "Pump",
      "nameVN": "Bơm nước thải",
      "nameEN": "Wastewater pump",
      "specsVN": "Lưu lượng: 167 m3/h\nCột áp: 19m\nCông suất: 15kW",
      "area": "BỂ THU GOM",
      "brand": "ShinMaywa",
      "unit": "Cái",
      "originalTag": "BM-TG",
      "status": "AIExtracted",
      "quantity": 2,
      "specs": [
        { "parameterCode": "FlowRate", "value": 167 },
        { "parameterCode": "Head", "value": 19 },
        { "parameterCode": "Power", "value": 15 }
      ]
    }
  ],
  "dryRun": false,
  "replaceMatching": true,
  "clearAiExtracted": false,
  "database": "auto",
  "busyTimeoutMs": 30000
}
```

MCP server parameter `items` is a **JSON string** (array serialized).

## Build / deploy

```powershell
# Revit should not lock the add-in DLL (close Revit OR accept deploy may fail if locked)
dotnet build src/plugin-r24/RvtMcp.Plugin.R24.csproj -c Debug
dotnet build src/server/RvtMcp.Server.csproj -c Debug -o publish/server-kei
```

Plugin auto-deploys to `%APPDATA%\Autodesk\Revit\Addins\2024\RvtMcp\`.

Grok CLI (`~/.grok/config.toml`):

```toml
[mcp_servers.rvt-mcp]
command = "<repo>/horizun-revit-mcp/publish/server-kei/RvtMcp.Server.exe"
enabled = true
startup_timeout_sec = 60
```

Restart Grok session after server rebuild. Restart Revit after plugin rebuild.

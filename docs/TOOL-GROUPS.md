# Tool groups and profiles (4.0.0) / Grupos de herramientas y perfiles

**[English](#english) · [Español](#español)**

## English

4.0 separates two questions that 3.x answered together:

| Question | Decided by | Effect |
|---|---|---|
| **May this call run?** (the *ceiling*) | `permission_profile`, `denied_tools` / `allowed_tools`, the Python grant, and the 3.x `tool_packs` / `toolsets` selection (`settings.json`, or `HORIZUN_TOOL_PACKS` / `HORIZUN_TOOLSETS` in the client's environment) | A tool outside the ceiling is refused on every path, exactly as in 3.x. |
| **Is it listed?** (*visibility*) | The tool **profile** and this session's activations | Only changes what `tools/list` prints. A reachable tool of an inactive group is still callable, directly or through `horizun_call_tool`. |

Every tool belongs to **exactly one group** (`ToolGroupCatalog` in `src/Horizun.Contracts/Contract.cs`, one table tool → group). Core is always listed: `health`, `target`, `job_status`, `submit_job`, `query_model`, `list_elements`, `navigate`, `capture_view`, `write_params_verified`, `delete_verified`, `verify_changes`, `undo`, `execute_python`, `request_python_access`, `save_document`, `open_document` and `horizun_toolsets`, plus the two meta tools `horizun_describe_tool` and `horizun_call_tool`.

Groups: `lectura`, `modelado`, `arquitectura`, `estructura`, `mep`, `documentacion`, `cad_ifc`, `auditoria`, `coordinacion`, `nube_cde`, `familias`, `datos`, `admin`. The 3.x toolset names are accepted as aliases (`read` → `lectura`, `documentation` → `documentacion`, `cad` → `cad_ifc`, `data`/`schedules`/`powerbi` → `datos`, `interoperability` → `cad_ifc` + `datos`, …).

### Profiles (Community contract, from the tools-list ledger; tokens = bytes/4.4 … bytes/3.5)

| Profile | Groups (besides core) | Tools listed | Tokens |
|---|---|---:|---:|
| `nucleo` | — | 19 | 11–14k |
| `analista_datos` | lectura, datos, familias | 48 | 27–34k |
| `auditor` | lectura, auditoria, documentacion | 54 | 44–55k |
| `documentador` | lectura, documentacion, datos | 58 | 44–56k |
| `modelador_arquitectura` / `_estructura` / `_mep` | lectura, modelado + one discipline | 55 / 50 / 43 | 32–48k |
| `coordinador` | lectura, coordinacion, nube_cde, cad_ifc, documentacion | 51 | 40–50k |
| `modelador` | lectura, modelado, arquitectura, estructura, mep, cad_ifc, familias | 69 | 46–58k |
| `completo` | all | 110 | 85–107k |

In Community the group `mep` holds no tools and `cad_ifc` and `coordinacion` hold one each: the MEP, DWG and IFC to BIM, clash and coordination, and family-authoring tools are in the Pro edition, so the profiles built on those groups list fewer tools here.

`tests/Horizun.Server.Tests/tools-list-ledger.json` pins each profile's bytes (`group_profile:*`); `ToolGroupTests` holds each one to a token budget.

### Where the profile comes from (first match wins)

1. `--profile <name>` on the server command line (the client's own MCP entry);
2. `HORIZUN_PROFILE=<name>` in the client's environment (`local` keeps meaning the 3.7 local schema surface);
3. `"tool_profile": "<name>"` in `settings.json` — what the installer writes;
4. nothing: `completo`, every tool, the 3.x list byte for byte.

A value is a profile name or a comma-separated list of groups. An unknown value lists everything and `horizun_health.tool_groups.profile_problem` says why. The 3.x ceiling keeps its own precedence (environment over settings, `tool_packs` over `toolsets`).

### Installer

- **New install:** a page offers the ten profiles; `modelador` is pre-selected; the choice is written as `tool_profile`.
- **Upgrade:** "keep what I have" is pre-selected and **writes nothing** — a machine without `tool_profile` keeps the 3.x behaviour (every tool listed). Choosing a profile writes it.
- Silent: `/PROFILE=<name>`. The result is in `install-result.txt` (`tool_profile=`). Helper: `server\client-tools\set-tool-profile.ps1`.

### In the session

`horizun_toolsets` — `action=list` (groups and profiles with sizes), `activate` / `deactivate` (`groups=[...]`), `profile` (`profile=<name>`), `reset`. It lives in memory only: it never writes `settings.json` and never affects another client. Every change that alters the list emits one `notifications/tools/list_changed`; the reply carries a one-line catalog of what became visible. A client that does not refresh (no `list_changed` support) calls the tools through `horizun_call_tool`; `horizun_describe_tool` gives the exact schema.

`horizun_call_tool` stands for any tool, so it carries the strongest hints (`destructiveHint: true`). A client that asks for confirmation per tool name sees one generic tool for those calls.

### Compact announcement (switch, default off)

`HORIZUN_COMPACT_SCHEMAS=1` announces the heaviest schemas (`create_elements`, `export`, `manage_views`, `plan_annotations`; in the Pro edition also `create_family`) with the discriminator values and shared arguments only (measured on the Pro catalog: 86.7 KB → 17.6 KB, about 20k tokens less). Arguments are still validated against the full contract; the per-variant schema is one `horizun_describe_tool {name, variant}` away. Off until the agent battery measures it.

## Español

4.0 separa dos preguntas que la 3.x respondía juntas: **¿puede correr?** (el *techo*: perfil de permisos, `denied_tools`/`allowed_tools`, la concesión de Python y la selección 3.x `tool_packs`/`toolsets`) y **¿se lista?** (la *visibilidad*: el perfil de herramientas y las activaciones de la sesión). Fuera del techo, la herramienta se rechaza siempre, como en 3.x. Dentro del techo pero en un grupo inactivo, no se lista y **se puede llamar igual**, directo o con `horizun_call_tool`.

En Community el grupo `mep` no tiene herramientas y `cad_ifc` y `coordinacion` tienen una cada uno: las herramientas MEP, DWG e IFC a BIM, de interferencias y coordinación y de autoría de familias están en la edición Pro.

Cada herramienta tiene **un solo grupo**. El núcleo siempre se lista (16 herramientas + `horizun_toolsets` + `horizun_describe_tool` y `horizun_call_tool`). Los nombres de toolsets 3.x valen como alias.

**De dónde sale el perfil** (gana el primero): `--profile` en la línea de comandos del servidor; `HORIZUN_PROFILE` en el entorno del cliente (`local` sigue siendo la superficie local de 3.7); `tool_profile` en `settings.json` (lo escribe el instalador); si no hay nada, `completo`: la lista 3.x idéntica.

**Instalador:** en una instalación nueva se elige el perfil (viene marcado `modelador`) y se escribe `tool_profile`. En una actualización viene marcado «mantener lo que tengo», que **no escribe nada**: sin `tool_profile`, todo sigue visible como en 3.x. En silencio: `/PROFILE=<nombre>`.

**En la sesión:** `horizun_toolsets` (`list`, `activate`, `deactivate`, `profile`, `reset`) cambia la lista al instante, solo en memoria, sin tocar `settings.json`, y emite un `list_changed` por cambio. Sin soporte de `list_changed`, se usa `horizun_call_tool`.

**Anuncio compacto:** `HORIZUN_COMPACT_SCHEMAS=1`, apagado por defecto hasta medirlo en la batería.

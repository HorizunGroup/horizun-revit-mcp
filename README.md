# Horizun Revit MCP — model, document, coordinate and deliver in Revit

**Made in Colombia 🇨🇴 by Horizun Group.**

Horizun Revit MCP is a free, open-source Windows MCP server and Revit add-in for
**Autodesk Revit 2023–2027**. Its complete catalog contains **122 tools** <!--inventory:tools-->
with **405 named suboperations and dispatch modes** <!--inventory:operations-->
for architectural and structural modeling, MEP, parametric families, drawings,
CAD-to-BIM, model audits, quantities, Excel, Power BI and exports.

Use natural-language objectives in your MCP client to create new BIM content,
inspect existing models and carry out multi-step production workflows. Typed
changes include rehearsal, explicit targets and post-commit verification;
owner-enabled Python extends the bridge to task-specific Revit API automation.
The downloadable Windows installer includes the server runtime and Revit add-ins.

**English** · **[Español](README.es.md)**

[![ci](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/ci.yml?branch=main&label=ci&logo=githubactions&logoColor=white)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/ci.yml) [![codeql](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/codeql.yml?branch=main&label=codeql&logo=github)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/codeql.yml) [![release](https://img.shields.io/github/v/release/HorizunGroup/horizun-revit-mcp?label=release&color=0696D7)](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest) [![Revit 2023–2027](https://img.shields.io/badge/Revit-2023%E2%80%932027-0696D7)](#install) [![MCP registry](https://img.shields.io/badge/MCP%20registry-io.github.HorizunGroup%2Fhorizun--revit--mcp-6E56CF)](https://registry.modelcontextprotocol.io/) [![license Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE) [![GitHub stars](https://img.shields.io/github/stars/HorizunGroup/horizun-revit-mcp?label=GitHub%20stars)](https://github.com/HorizunGroup/horizun-revit-mcp/stargazers)

**[Download](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest)** ·
**[Watch the demo](https://www.youtube.com/watch?v=tlFs5p3EM4M)** ·
[Install with Claude Desktop Free](#installation-video-claude-desktop-free) ·
[All tools](#complete-tool-catalog) · [Suboperations](#suboperations-and-modes) ·
[Release evidence](#tested-in-revit-published-release-evidence) · [Install](#install)

## Product at a glance

| Surface | What is available | Inspect it |
|---|---|---|
| MCP entry points | **122 tools** <!--inventory:tools-->, including **47 read-only** <!--inventory:reads--> and **75 with possible effects** <!--inventory:writes--> | [Generated inventory](docs/inventory.json) and full catalog below |
| Internal actions | **405 named suboperations and dispatch modes** <!--inventory:operations--> inside multi-operation tools | Exact selector values below |
| Revit coverage | 2023, 2024, 2025, 2026 and 2027 | Five matching add-ins and versioned live reports |
| New model content | 26 element kinds; parametric RFA authoring; structural and MEP planning | [Creation and family reference](docs/FAMILY-AUTHORING.md) |
| Drawings and deliverables | 24 view/sheet actions, 10 annotation actions, native schedules and sheet layout | [Planimetry production](docs/PLANIMETRY-PRODUCTION.md) |
| Export targets | PDF, DWG, IFC, NWC, FBX, images and schedule CSV | `horizun_export` |
| Verified release example | 1,230 passed probe executions across five Revit years in **v1.3.3** | [Published reports](#tested-in-revit-published-release-evidence) |
| Installation | Windows Setup with runtime included; no Git, Visual Studio or .NET SDK for end users | [Client-specific final steps](docs/CLIENTS.md) |

Counts describe the complete product surface. The active permission profile and
tool packs determine which tools a particular session exposes; the measured
70/79/80-tool profiles are explained below.

## Watch a real workflow: PDF plans to a Revit model

[![Watch the Horizun demo: PDF plans to a Revit model with AI](https://i.ytimg.com/vi/tlFs5p3EM4M/hqdefault.jpg)](https://www.youtube.com/watch?v=tlFs5p3EM4M)

**[PDF plans → Revit model with AI — watch on YouTube](https://www.youtube.com/watch?v=tlFs5p3EM4M)**
is a published Spanish-language demonstration from the Horizun Hub channel.
It presents a PDF-to-Revit workflow driven by an AI client. PDF interpretation
belongs to the client/model; Horizun supplies the operations executed in Revit.
The separate typed DWG workflow below reads CAD geometry and versioned rules.

## What you can build and deliver

### Architectural, structural and MEP modeling

`horizun_create_elements` accepts dependency-aware batches of levels, grids,
walls, floors, ceilings, roofs, rooms, family instances, structural beams and
columns, beam systems, wall foundations, ducts, pipes, conduit, cable trays,
fittings, inline accessories, MEP systems, openings, shafts, room separators,
profile walls, displacement sets and supported stairs.

The suboperation table lists all 26 creation kinds and five fitting choices.
`horizun_transform_elements` handles moves, copies, rotations, wall joins,
type/curve changes and tag placement. `horizun_manage_system_types` supports
compound material-layer structures and MEP junction preferences.
Structural and MEP planners produce requests that use these same creation tools.

### New parametric families, including MEP connectors

`horizun_create_family` creates a new `.rfa` from an installed `.rft`: parameters,
formulas, named types, solid/void extrusions, blends, revolutions, sweeps and
swept blends; reference planes and labeled dimensions; supported nested families;
and pipe, duct, electrical, conduit or cable-tray connectors. Save and optional
project load are verified. `horizun_family_apply` edits existing family data with
a geometry invariant. [RFA examples and template scope](docs/FAMILY-AUTHORING.md).

### Drawings, automated annotation and sheet production

Build floor/ceiling/structural/area plans, sections, elevations, callouts, drafting
and 3D views. Manage templates, phases, crops, view ranges and scope boxes; create,
duplicate and populate sheets; place schedules and align viewports. Annotation
tools support text, tags, linear/angular/radial/diameter/arc-length dimensions
and spot elevation/coordinate/slope labels.

Plan dimensions and tags from explicit criteria, inspect geometric references,
create 2D details, audit planimetry, apply cited corrections and pack sheet zones.
`horizun_plan_views` and the `deliverable-production` prompt coordinate delivery
stages and approval state. [Drawing workflows](docs/PLANIMETRY-PRODUCTION.md).

### DWG to BIM, with traceable revision updates

Inspect DWG instances, layers and curves. Supply a versioned requirement set to
map drawing content into an ordered BIM plan. Rehearse and apply the plan through
typed tools, with source fingerprints and provenance attached to created elements.
Audit the model against the drawing; plan and apply later DWG revisions while
identifying manual model edits that need review.
These are seven dedicated CAD tools, listed individually below.
[DWG-to-BIM examples](docs/DWG-TO-BIM.md).

### Audits, coordination, quantities and data exchange

Query the host and loaded links using category, family/type, level, parameter and
spatial filters. Scan model health, audit supplied requirements and make verified
parameter corrections. Coordinate clashes with persistent findings, assignment,
decisions and model-measured resolution. Plan reinforcement, apply supported
reinforcement requests and audit their results.

Produce material takeoffs, compare them against an Excel budget baseline, read
or append XLSX rows without Excel/COM, push approved data to Power BI and export
verified deliverables. [Quantities](docs/QUANTITIES-AND-BUDGET.md),
[Power BI](docs/POWER-BI.md), [audit starter](docs/QUICK-START-BIM.md).

### Specialized model transformations and terrain

Separate wall or slab layers, split floors by loops, rectangularize supported
wall geometry, ungroup with origin tracking and regroup by a parameter. Transfer
slab elevations, embed floors in toposolids and grade terrain with breaklines and
specified slopes. Wall-layer decomposition preserves the core wall's identity
and hosted elements. Bundled recipe tools use shipped Python for geometry under
host-controlled rehearsal, transactions and their declared readback checks;
arbitrary client-generated Python has a separate permission/evidence contract.

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

### Installation video: Claude Desktop Free

[![Install Horizun Revit MCP with Claude Desktop Free](https://i.ytimg.com/vi/3kp-we7MIvk/hqdefault.jpg)](https://www.youtube.com/watch?v=3kp-we7MIvk)

**[Connect Revit to Claude Desktop Free — watch the installation tutorial](https://www.youtube.com/watch?v=3kp-we7MIvk)**
is a Spanish-language guide published by Horizun Hub to installing and connecting
Horizun Revit MCP with the free Claude Desktop plan. Follow the Setup and `.mcpb`
steps above; the [client guide](docs/CLIENTS.md#claude-desktop) provides the written instructions.

### Optional PowerShell bootstrap

This downloads the release installer, checks its hash and runs Setup quietly.
`-AllowUnsigned` acknowledges the publisher status described above. The same
client-specific final steps still apply; use `-Interactive` for the wizard.

```powershell
$s = irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1; & ([scriptblock]::Create($s)) -AllowUnsigned
```

The script is fetched from `main`; its checksum check applies to the downloaded
Setup. You can download and inspect the script first. See [installation options](docs/INSTALL.md).

## Complete tool catalog

Every named MCP tool is listed here, grouped by the work it performs. These groups
are a reading aid; session **tool packs** are the configurable sets implemented
in [ToolPacks.cs](src/Horizun.Revit/Core/ToolPacks.cs). Descriptions are maintained
in [the bilingual catalog](docs/readme-catalog.json); counts and membership are
checked against the server-generated [inventory](docs/inventory.json).
The [detailed tool reference](docs/TOOLS.md) documents arguments and limitations.

<!-- BEGIN TOOL CATALOG -->
### Connection, documents and background jobs

| Tool | Capability |
|---|---|
| `get_document_info` | Inspect the open document's identity and element counts. |
| `horizun_document_session` | Inspect, open, save, save-as and close documents through explicit session operations. |
| `horizun_file_info` | Read RVT/RFA headers, version and worksharing metadata without opening or upgrading them. |
| `horizun_health` | Report bridge health, active document, Revit year, loaded version and commit. |
| `horizun_job_status` | Read progress and recovery state while Revit is busy or after a process restart. |
| `horizun_open_document` | Open a model with checks for version upgrades and workshared central files. |
| `horizun_relinquish_all` | Release owned worksharing elements and report remaining ownership. |
| `horizun_repair_memory` | Report and recover durable state this bridge keeps when a record is unreadable. |
| `horizun_save_document` | Save and inspect the resulting file's timestamp and size. |
| `horizun_selection_exchange` | Read what a person selected in Revit, and offer a set back for them to select. |
| `horizun_submit_job` | Submit long-running Revit operations with a persistent job identifier. |
| `horizun_target` | Select the Revit instance to control when multiple sessions or years are open. |

### Model queries, audits and correction

| Tool | Capability |
|---|---|
| `horizun_apply_corrections` | Rehearse and apply supported audit corrections with per-result verification. |
| `horizun_apply_ifc_plan` | Carry out an IFC plan through typed commands that rehearse and re-read their own work. |
| `horizun_audit_access` | Report what this bridge is allowed to do on this machine and who decided it. |
| `horizun_audit_model` | Evaluate supplied model requirements with findings, coverage and prevention-gate evidence. |
| `horizun_capture_view` | Export the active or named view as an image for visual review by the client. |
| `horizun_verify_changes` | Check what the last write changed for spatial conflicts and return an image with them highlighted. |
| `horizun_delete_verified` | Delete explicit IDs or purge unused content; preview cascades and verify removals. |
| `horizun_list_elements` | List and paginate elements across the host and loaded Revit links with model identity. |
| `horizun_model_scan` | Measure model health, warnings, worksets, links, families, views and cleanup candidates. |
| `horizun_navigate` | Select elements, clear selection, zoom and open views. |
| `horizun_plan_from_ifc` | Plan Revit elements from an IFC file under a declared mapping, without importing it. |
| `horizun_query_model` | Filter by category, type, level, parameters and spatial bounds; project fields and return grouped or compact summaries. |
| `horizun_validate_ids` | Check a model against an IDS specification and report each requirement it fails. |
| `horizun_query_classification` | Read the keynote and assembly-code tables, their use in the model, missing and unused codes, and families' lookup tables. |
| `horizun_model_diff` | Snapshot models and compare deliveries: added, deleted and changed elements with parameters, a coloured view, a plain summary and quality history. |
| `horizun_code_check` | Check the model against declarative requirement sets, including Colombian accessibility, egress and lighting rules, with not_decidable instead of guesses. |
| `horizun_undo` | List and undo the last verified Horizun batch, refusing when the model or file changed since. |

### Model creation, parameters and families

| Tool | Capability |
|---|---|
| `horizun_bind_shared_param` | Bind shared parameters to categories and inspect group-variation behavior. |
| `horizun_copy_between_documents` | Copy elements between open documents, keeping identity and reporting what was substituted. |
| `horizun_create_elements` | Create levels, grids, walls, slabs, roofs, rooms, family instances, structural members, MEP runs, openings and stairs in typed batches. |
| `horizun_create_family` | Create new parametric RFA files: solid/void forms, parameters, formulas, types, nested families and MEP connectors; optionally load them. |
| `horizun_family_apply` | Apply family edits in a transaction with a geometry invariant and rollback on drift. |
| `horizun_manage_materials` | Read, create and assign materials and their appearance and identity data. |
| `horizun_manage_system_types` | Duplicate system types; edit parameters, wall/slab/roof/ceiling layer structures and MEP junction preferences. |
| `horizun_set_keynote` | Assign keynote values with the affected instance/type scope made explicit. |
| `horizun_transform_elements` | Move, copy, rotate, pin, change types or curves and adjust tag heads/leaders over explicit targets. |
| `horizun_write_params_verified` | Write parameter values in batches and re-read each requested value. |
| `horizun_manage_parameters` | Read the full binding map, create and bind shared parameters, rebind or remove bindings, and manage Global Parameters. |
| `horizun_manage_curtain` | Read and edit curtain grids: grid lines, mullions and panel types, re-measured after each change. |
| `horizun_slab_shape` | Edit floor and roof shape: points, split lines and vertex elevations, with a reset. |
| `horizun_create_railing` | Create railings on stairs or ramps, or from a sketched path on a level. |
| `horizun_manage_phases` | Read phases, filters and design options; set element phases and create or edit phase filters. |
| `horizun_manage_assemblies_parts` | Create, divide, exclude and dissolve parts; create assemblies with their views and disassemble them. |
| `horizun_manage_styles` | Read and set object styles, subcategories, line styles, line patterns and fill patterns. |
| `horizun_manage_units` | Read and set project units, Project Information and the base and survey points (with explicit consent). |

### Drawings, dimensions, sheets and schedules

| Tool | Capability |
|---|---|
| `horizun_manage_views` | Create and configure plans, sections, elevations, callouts, 3D views, sheets, viewports and schedule placements. |
| `horizun_plan_views` | Plan room views and deliverable sets; track delivery status, approvals and invalidation. |
| `horizun_query_planimetry` | Inspect sheets, views, placements, annotations and drawing references with geometry and coverage. |
| `horizun_audit_planimetry` | Audit drawing requirements and return cited findings for correction. |
| `horizun_fix_planimetry` | Correct cited view, sheet, title-block, viewport, schedule and crop findings. |
| `horizun_pack_sheets` | Place views and schedules within supplied sheet zones and check resulting placements. |
| `horizun_plan_annotations` | Plan automatic tags and dimensions for grids, levels, curtain walls and openings using explicit criteria. |
| `horizun_annotate` | Create text, tags, linear/angular/radial/diameter/arc-length dimensions and spot elevations, coordinates or slopes. |
| `horizun_get_dimension_references` | Discover geometric references that can be used in dimension requests. |
| `horizun_query_dimensions` | Inspect existing dimensions, segments, references and measured values. |
| `horizun_edit_dimensions` | Apply supported dimension edits and verify the resulting dimension state. |
| `horizun_query_detail_2d` | Read a view's detail geometry, line styles, region types and placeable symbols. |
| `horizun_detail_2d` | Create detail lines/arcs/polylines, filled/masking regions and components in verified atomic batches. |
| `horizun_manage_revisions` | Create and update drawing revision records. |
| `horizun_create_schedule` | Create a native schedule with selected fields, sorting and optional linked elements. |
| `horizun_manage_schedules` | Create, duplicate and configure schedules, material takeoffs, sheet/view lists, revision schedules and keynote legends. |
| `horizun_list_schedules` | List schedules and inspect fields, linked-file settings and displayed dimensions. |
| `horizun_get_schedule_data` | Read displayed schedule cells with explicit row/column bounds and truncation information. |

### CAD / DWG to BIM and revision updates

| Tool | Capability |
|---|---|
| `horizun_apply_cad_plan` | Build the plan through typed commands; verify source hashes and stamp created elements with CAD provenance. |
| `horizun_apply_cad_update` | Apply supported revision changes and preserve provenance for the next update. |
| `horizun_audit_cad_model` | Compare the drawing against the model using provenance, geometry and measured differences. |
| `horizun_cad_connect` | Build the joins a drawing declares, placing the fitting each junction needs and verifying it. |
| `horizun_cad_extract` | Read a linked DWG directly - layers, lines, arcs, text and blocks - without importing it. |
| `horizun_cad_networks` | Derive the network a drawing describes: which ends meet, what belongs between them and which stay open. |
| `horizun_cad_review` | Report what a conversion could not settle, with the evidence for each held candidate. |
| `horizun_cad_symbols` | List the block symbols a drawing defines and where each one is placed. |
| `horizun_cad_unit_instances` | Find repeated units in a drawing and the transform that places each occurrence. |
| `horizun_manage_cad_links` | List, add, reload and repoint CAD links with measured state. |
| `horizun_plan_cad_update` | Plan changes between DWG revisions while identifying manual model edits that need review. |
| `horizun_plan_from_cad` | Interpret a DWG using supplied versioned rules and return a dependency-ordered BIM plan with omissions and provenance. |
| `horizun_query_cad` | Inspect DWG instances, layers, curve geometry, profiles and readable coverage. |

### Structure, reinforcement, MEP and coordination

| Tool | Capability |
|---|---|
| `horizun_acc_upload_status` | Read Desktop Connector log evidence of ACC folder assignment for local files. |
| `horizun_apply_reinforcement` | Apply supported reinforcement plans with typed checks and verification. |
| `horizun_audit_reinforcement` | Audit reinforcement against supplied requirements and report coverage. |
| `horizun_clash` | Detect scoped clashes, plan supported penetrations and record coordination findings. |
| `horizun_connect_mep` | Connect or disconnect named MEP connectors, refusing to close a visible gap by moving geometry. |
| `horizun_coordination` | Track coordination findings, assignments, decisions, evidence and model-detected resolution or regression. |
| `horizun_manage_links` | Add and manage Revit links/instances, paths, load state and pinning. |
| `horizun_plan_mep` | Plan pipe/duct routes and fittings; inspect networks through actual connector connectivity. |
| `horizun_plan_reinforcement` | Plan reinforcement from supplied host and reinforcement requirements. |
| `horizun_plan_structure` | Plan columns at grid intersections and beams along consecutive grid crossings. |
| `horizun_query_structure` | Inspect members, hosts, cover, rebar, reinforcement systems, connections and quantities. |
| `horizun_structural_connections` | Read and apply structural connection types between framing elements. |
| `horizun_mep_routing` | Read and edit routing preferences and catalogue sizes, resize MEP runs to catalogue sizes and plan sizes by flow. |
| `horizun_electrical` | List panels and circuits, create circuits, assign panels, add or remove elements and create panel schedules. |
| `horizun_resolve_clash` | Propose and apply the smallest safe move for MEP clashes, proved by re-detecting and rolled back if anything new clashes. |
| `horizun_federation_check` | Check that each federated model holds only its disciplines, its expected links and coherent shared coordinates. |

### Layers, groups and terrain

| Tool | Capability |
|---|---|
| `horizun_split_floor_loops` | Split a floor by sketch loops while carrying height offsets. |
| `horizun_split_multilayer_walls` | Separate wall layers while retaining the original core wall identity and its hosted elements. |
| `horizun_split_multilayer_slabs` | Separate floor/ceiling material layers with profile preservation and per-slab rollback. |
| `horizun_rectangularize_walls` | Decompose supported orthogonal wall geometry into rectangular fragments. |
| `horizun_ungroup_and_mark` | Ungroup model groups while recording each member's source group. |
| `horizun_regroup_by_param` | Rebuild model groups from a supplied grouping parameter. |
| `horizun_copy_slab_elevations` | Transfer a shaped floor surface onto explicit destination floors. |
| `horizun_embed_floors_in_toposolid` | Embed floor outlines and elevations into a toposolid. |
| `horizun_grade_toposolid_around_floors` | Grade terrain around floors with offsets, breaklines and specified side slopes. |
| `horizun_manage_groups` | List, create, redefine, rename, duplicate and swap model group types, asking before touching other instances. |
| `horizun_manage_worksets` | List, create and rename worksets, move elements between them and set per-view visibility. |

### Quantities, Excel, Power BI and deliverables

| Tool | Capability |
|---|---|
| `horizun_quantities` | Measure volumes and material takeoffs with units, grouping and model provenance. |
| `horizun_budget_compare` | Compare model quantities against an Excel baseline; optionally write approved Excel/Power BI outputs. |
| `horizun_catalog_lookup` | Resolve items from a supplied catalog with leaf-status and file-hash provenance. |
| `horizun_excel_read_rows` | Read XLSX rows, types and cached formula values without Excel or COM. |
| `horizun_excel_write_rows` | Append XLSX rows, create a backup and re-read written cells without Excel or COM. |
| `horizun_power_bi_push` | Send rows to a Power BI push semantic-model table with durable replay protection and destination receipts. |
| `horizun_export` | Export PDF, DWG, IFC, NWC, FBX, images and schedule CSV with output-file verification. |
| `horizun_link_schedule` | Import MS Project, Primavera or CSV schedules, link activities to elements, write dates and colour a 4D status view. |

### Composable workflows and custom API automation

| Tool | Capability |
|---|---|
| `horizun_execute_plan` | Compose up to 100 typed actions with dependencies, prior-result references and TransactionGroup rollback. |
| `horizun_execute_python` | Run client-generated Python against the Revit API, including preflight and script-supplied evidence; owner opt-in required. |
| `horizun_promote_script` | Promote a verified script to a named procedure so it stops being ad-hoc code. |
| `horizun_request_python_access` | Show an owner-approval request in Revit for custom Python execution. |
| `horizun_run_procedure` | Run a named, versioned procedure that this machine has stored, with its own consent. |

### ISO 19650 information management and openBIM delivery

| Tool | Capability |
|---|---|
| `horizun_project_context` | Validate, question and draft the project's ISO 19650 context: appointment, EIR/BEP/MIDP, CDE states, naming, delivery. |
| `horizun_information_container` | Name, seal, verify, inspect and transition information containers across WIP, Shared, Published and Archived folders. |
| `horizun_deliver_ifc` | Export an IFC and prove it: IDS on the file, property-set mapping coverage, georeference, BCF of failures and a sealed container. |
| `horizun_cde_cloud` | Read a cloud CDE (Autodesk Construction Cloud or an OpenCDE server) by state and cross it against the MIDP, read-only. |

<!-- END TOOL CATALOG -->

## Suboperations and modes

A named MCP tool can dispatch many actions. For example, creating a wall, a pipe
and a stair are choices under `horizun_create_elements`; creating a section and
placing a schedule are different actions under `horizun_manage_views`.

The table contains **405 named suboperations and dispatch modes** <!--inventory:operations-->
across 26 multi-operation tools. A choice is counted once per tool, selector
property and value, including nested selectors. Repeated `oneOf` schema paths
are deduplicated. Some selectors refine another action: these numbers describe
the exposed operation vocabulary, not 208 additional top-level tools.

<!-- BEGIN SUBOPERATIONS -->
| Tool | Selector | Named suboperations and modes |
|---|---|---|
| `horizun_document_session` | `operation` | `open`, `save`, `save_as`, `close`, `inspect` |
| `horizun_repair_memory` | `operation` | `list`, `advice`, `observe`, `remedy`, `quarantine`, `release` |
| `horizun_selection_exchange` | `operation` | `publish`, `read`, `clear`, `capabilities` |
| `horizun_audit_model` | `operation` | `save`, `save_as`, `sync_with_central`, `export`, `publish`, `close_with_save`, `batch_open_close` |
| `horizun_delete_verified` | `mode` | `ids`, `purge_unused` |
| `horizun_navigate` | `operation` | `select`, `clear_selection`, `zoom`, `select_and_zoom`, `open_view` |
| `horizun_validate_ids` | `operation` | `precheck`, `validate` |
| `horizun_query_classification` | `operation` | `keynote_table`, `assembly_code`, `family_lookup_tables`, `unused_codes`, `missing_codes` |
| `horizun_model_diff` | `operation` | `snapshot`, `list`, `compare`, `colorize`, `explain`, `record_quality`, `quality_trend` |
| `horizun_undo` | `operation` | `list`, `undo_last` |
| `horizun_create_elements` | `kind` | `level`, `grid`, `wall`, `floor`, `ceiling`, `roof`, `room`, `family_instance`, `structural_framing`, `structural_column`, `duct`, `pipe`, `conduit`, `cable_tray`, `fitting`, `wall_opening`, `slab_opening`, `beam_system`, `wall_foundation`, `accessory_inline`, `mep_system`, `shaft`, `room_separator`, `wall_profile`, `displacement`, `stairs` |
| `horizun_create_elements` | `fitting` | `elbow`, `union`, `transition`, `tee`, `takeoff`, `cross` |
| `horizun_create_family` | `kind` | `extrusion`, `blend`, `revolution`, `sweep`, `swept_blend`, `pipe`, `duct`, `electrical`, `conduit`, `cable_tray`, `symbolic`, `model` |
| `horizun_manage_materials` | `operation` | `create`, `duplicate`, `update` |
| `horizun_transform_elements` | `operation` | `wall_join`, `move`, `copy`, `rotate`, `mirror`, `pin`, `unpin`, `change_type`, `change_type_by_rule`, `realign_wall_sketch`, `set_curve`, `move_tag_head`, `set_tag_leader`, `array_linear`, `array_radial`, `rename_level` |
| `horizun_manage_parameters` | `operation` | `list_bindings`, `create_shared`, `create_project`, `rebind`, `remove_binding`, `global_list`, `global_create`, `global_set`, `global_delete` |
| `horizun_manage_curtain` | `operation` | `read`, `add_grid_line`, `remove_grid_line`, `set_mullions`, `set_panel_type` |
| `horizun_manage_curtain` | `mode` | `add`, `remove` |
| `horizun_slab_shape` | `operation` | `read`, `add_point`, `add_split_line`, `modify_subelement`, `reset_shape` |
| `horizun_manage_phases` | `operation` | `list`, `element_status`, `set_element_phases`, `create_phase_filter`, `edit_phase_filter`, `rename_phase`, `create_phase`, `assign_design_option` |
| `horizun_manage_assemblies_parts` | `operation` | `list`, `create_parts`, `divide_parts`, `exclude_parts`, `restore_parts`, `dissolve_parts`, `create_assembly`, `assembly_views`, `disassemble` |
| `horizun_manage_styles` | `operation` | `list_object_styles`, `set_object_style`, `create_subcategory`, `list_line_styles`, `create_line_style`, `list_line_patterns`, `create_line_pattern`, `list_fill_patterns`, `create_fill_pattern` |
| `horizun_manage_units` | `operation` | `read`, `set`, `project_information`, `base_points` |
| `horizun_manage_views` | `operation` | `create_floor_plan`, `create_ceiling_plan`, `create_structural_plan`, `create_area_plan`, `create_3d`, `create_drafting`, `create_section`, `create_elevation`, `create_callout`, `duplicate_view`, `apply_template`, `set_phase`, `assign_scope_box`, `set_view_range`, `set_crop`, `set_annotation_crop`, `create_sheet`, `create_placeholder_sheet`, `convert_placeholder_sheet`, `duplicate_sheet`, `place_view`, `place_schedule`, `set_viewport_type`, `align_viewports`, `create_filter`, `apply_filter`, `color_by_value`, `set_element_overrides`, `hide_elements`, `isolate_elements`, `reset_temporary`, `set_category_visibility`, `create_legend`, `place_legend_component`, `edit_filter`, `order_filters`, `explain_graphics`, `create_template`, `set_template_controls`, `sheet_set_list`, `sheet_set_create`, `sheet_set_update`, `sheet_set_delete` |
| `horizun_manage_views` | `mode` | `center`, `center_x`, `center_y`, `left`, `right`, `top`, `bottom` |
| `horizun_plan_views` | `operation` | `room_views`, `deliverable_set`, `delivery_open`, `delivery_status`, `delivery_record`, `delivery_approve`, `delivery_invalidate` |
| `horizun_query_planimetry` | `mode` | `inventory`, `sheets`, `views`, `placements`, `annotations`, `references` |
| `horizun_fix_planimetry` | `operation` | `set_view_template`, `set_view_scale`, `rename_view`, `rename_sheet`, `place_title_block`, `move_viewport`, `move_schedule`, `clear_element_override`, `set_crop` |
| `horizun_plan_annotations` | `operation` | `auto_tags`, `intent_dimension`, `dimension_set`, `auto_dimension_grids`, `auto_dimension_levels`, `auto_dimension_curtain_walls`, `auto_dimension_openings` |
| `horizun_annotate` | `operation` | `text`, `tag`, `dimension`, `angular_dimension`, `radial_dimension`, `diameter_dimension`, `arc_length_dimension`, `spot_elevation`, `spot_coordinate`, `spot_slope` |
| `horizun_query_detail_2d` | `mode` | `resources`, `elements` |
| `horizun_detail_2d` | `operation` | `create_detail_line`, `create_detail_arc`, `create_detail_polyline`, `create_filled_region`, `create_masking_region`, `place_detail_component`, `place_symbol`, `set_line_style` |
| `horizun_manage_revisions` | `operation` | `create_revision`, `update_revision` |
| `horizun_manage_schedules` | `operation` | `create`, `duplicate`, `rename`, `add_fields`, `remove_fields`, `set_field`, `set_filters`, `set_sorting`, `set_options` |
| `horizun_manage_schedules` | `kind` | `material_takeoff`, `sheet_list`, `view_list`, `revision_schedule`, `keynote_legend` |
| `horizun_cad_connect` | `fitting` | `direct`, `elbow`, `tee`, `cross`, `transition`, `none` |
| `horizun_manage_cad_links` | `operation` | `list`, `add`, `reload`, `repoint` |
| `horizun_query_cad` | `mode` | `instances`, `layers`, `geometry`, `coverage`, `profile`, `blocks` |
| `horizun_connect_mep` | `operation` | `connect`, `disconnect` |
| `horizun_coordination` | `operation` | `list`, `update`, `export`, `import`, `import_navisworks`, `show`, `evidence` |
| `horizun_manage_links` | `operation` | `list`, `unload`, `reload`, `pin`, `unpin`, `add`, `add_instance`, `change_path` |
| `horizun_plan_mep` | `operation` | `route_run`, `network_census` |
| `horizun_plan_mep` | `kind` | `pipe`, `duct` |
| `horizun_plan_structure` | `operation` | `columns_on_grid_intersections`, `beams_along_grids` |
| `horizun_query_structure` | `mode` | `members`, `hosts`, `covers`, `rebar`, `reinforcement_systems`, `connections`, `coverage`, `quantities` |
| `horizun_mep_routing` | `operation` | `read`, `set_rules`, `add_sizes`, `remove_sizes`, `resize`, `size_by_flow` |
| `horizun_mep_routing` | `action` | `add`, `remove`, `move` |
| `horizun_electrical` | `operation` | `list_panels`, `list_circuits`, `create_circuit`, `assign_panel`, `add_to_circuit`, `remove_from_circuit`, `panel_schedule` |
| `horizun_resolve_clash` | `operation` | `propose`, `apply` |
| `horizun_manage_groups` | `operation` | `list`, `create`, `add_members`, `remove_members`, `rename_type`, `duplicate_type`, `swap_type`, `ungroup`, `convert_to_link` |
| `horizun_manage_worksets` | `operation` | `list`, `create`, `rename`, `move_elements`, `set_default`, `visibility` |
| `horizun_quantities` | `mode` | `volume`, `takeoff` |
| `horizun_catalog_lookup` | `operation` | `leaf`, `search`, `bsdd_search`, `bsdd_search_dictionary`, `bsdd_class`, `bsdd_property`, `bsdd_dictionaries` |
| `horizun_link_schedule` | `operation` | `import`, `match`, `write`, `status_view` |
| `horizun_execute_plan` | `kind` | `plan`, `section`, `elevation` |
| `horizun_promote_script` | `operation` | `list`, `show`, `propose`, `review`, `approve`, `activate`, `deactivate`, `source`, `resolve`, `invocation` |
| `horizun_run_procedure` | `operation` | `start`, `advance`, `decide`, `record`, `reconcile`, `status`, `abandon` |
| `horizun_project_context` | `operation` | `schema`, `validate`, `questions`, `draft`, `elicit`, `ids_from_loin` |
| `horizun_information_container` | `operation` | `name`, `stamp`, `verify`, `inspect`, `transition`, `transmittal`, `record_review`, `register` |
| `horizun_cde_cloud` | `operation` | `list_projects`, `list_states`, `inspect`, `versions` |
<!-- END SUBOPERATIONS -->

Other typed options include the seven `horizun_export.format` values:
`pdf`, `dwg`, `ifc`, `nwc`, `fbx`, `image`, `schedule_csv`. The schema inventory
also records **1506 enumerated argument occurrences** <!--inventory:enumerated_variants-->
across all properties and paths; that figure includes configuration choices and
repeated paths, so it is not used as a tool count.

## Extensibility, permissions and tool discovery

**Compose new workflows:** `horizun_execute_plan` connects up to 100 typed actions
using prior results and named dependencies, with TransactionGroup rollback.
Task-specific project standards, catalogs and CAD interpretation rules are inputs.
**Extend to custom Revit API work:** an owner may enable `horizun_execute_python`
for client-generated Python, including the standard library, preflight and
structured script evidence. The Python ON/OFF grant persists until revoked.
Python results remain self-reported with `host_verified: false`.

The following profiles were measured against the same **1.3.3** server binary
in isolated settings directories using actual MCP `tools/list` calls:

| Profile and packs | Tools announced | Why |
|---|---:|---|
| `safe_write`, all packs, Python off | 70 | Default in-model work; external-effect tools are filtered |
| `full_write`, all packs, Python off | 79 | Adds file/export/document and other external-effect operations |
| `unsafe_code`, all packs, Python enabled | 80 | Complete catalog, including custom Python |
| `safe_write`, `core` only | 4 | Deliberately minimal connection and job-management set |

A session exposing fewer tools can be correctly configured for a narrow task.
Inspect `horizun_health` and the `horizun://security/current-profile` resource
before comparing a session with the complete catalog. Owners control permissions;
an evaluator does not need to enable arbitrary code just to read the catalog.

For clients that support them, the server also offers standard MCP Resources,
Prompts, Completions, logging and durable Tasks. Resources expose the compiled
tool contract, build identity, effective profile and BIM workflow guidance.
Named prompts cover family recipes, room documentation, audits and delivery.
These protocol features and workflow prompts are additional to the tool count.

Tool packs, compact/summary queries, selected-field projections and durable jobs
help manage context and long-running work. Calls use a bounded 16-slot FIFO
queue; queued work can be cancelled before execution. Long operations expose a
persistent job ID and readable status. [Architecture](docs/ARCHITECTURE.md).

## Tested in Revit: published release evidence

**v1.3.3**, published on **2026-09-15**, includes live release-gate reports for
the installed server and all five supported Revit years. The reports total
**1,230 passed probe executions**: 246 in each year, with zero failed, unverified
or not-covered probes in that suite. Each report identifies the release commit,
binary hashes and harness. [Release assets](https://github.com/HorizunGroup/horizun-revit-mcp/releases/tag/v1.3.3).

| Revit | Passed | Failed | Unverified | Not covered in this suite | Report |
|---|---:|---:|---:|---:|---|
| 2023 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2023.json) |
| 2024 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2024.json) |
| 2025 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2025.json) |
| 2026 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2026.json) |
| 2027 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2027.json) |

This is one release suite executed across five Revit versions. It is maintainer
evidence for those cases, not 1,230 different features or a comparison against
another product. The [machine-readable evidence index](docs/release-evidence.json)
records the source URLs and hashes. Ongoing GitHub CI checks core/server behavior,
Windows deployment, inventory/documentation consistency and CodeQL.

Public release history starts with
[v0.5.0 on 2026-08-02](https://github.com/HorizunGroup/horizun-revit-mcp/releases/tag/v0.5.0),
includes [v1.0.0 on 2026-08-26](https://github.com/HorizunGroup/horizun-revit-mcp/releases/tag/v1.0.0),
and contains **16 published non-prerelease releases as of 2026-09-15**.
The GitHub stars badge above shows the current community count; the public
[release history](https://github.com/HorizunGroup/horizun-revit-mcp/releases)
and [commit history](https://github.com/HorizunGroup/horizun-revit-mcp/commits/main/)
show maintenance directly.

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

## Scope and further reference

Typed results identify verification, partial outcomes and coverage. Unloaded
links and unsupported API/template/exporter cases are reported explicitly.
Use [TOOLS.md](docs/TOOLS.md) and [family scope](docs/FAMILY-AUTHORING.md) for
operation-specific boundaries. [Benchmark methodology](docs/BENCHMARK.md)
distinguishes historical design scoring from live execution evidence.

[Build from source](docs/BUILDING.md) · [Contribute](CONTRIBUTING.md) ·
[Agent instructions](AGENTS.md) · [LLM summary](llms.txt) ·
[Security](docs/security-model.md) · [Privacy](docs/PRIVACY.md) ·
[Release policy](docs/RELEASE-POLICY.md)

Built in Colombia 🇨🇴 and maintained by Horizun Group as part of
[Horizun Hub](https://horizunhub.com). The bridge is organisation-neutral;
project standards and catalogs are supplied as inputs. The optional
[standards pack](standards/README.md) provides editable examples.

**Apache-2.0:** [LICENSE](LICENSE), [NOTICE](NOTICE), [third-party notices](THIRD-PARTY-NOTICES.md).
The Revit API is not redistributed. Autodesk and Revit are trademarks of Autodesk;
this project is not affiliated with, endorsed by or sponsored by Autodesk.

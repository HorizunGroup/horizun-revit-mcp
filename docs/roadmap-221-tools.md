# RvtMcp-MCP — 221-Tool Roadmap

**Goal**: scale the MCP gateway from the v0.3.0 baseline (~32 tools) to **221 tools** in 15 waves.

**Status (2026-05-21)**: **109/221** tools done. Waves 1-4 committed in `5066f41` (families, MEP, graphics, export) + prior batches in `12d5ceb` (schedule + element/group/workset) + baseline v0.3.0.

**Waves 5-15 (≈112 tools) remain**.

## Conventions (recap — see `CLAUDE.md` for full)

- Handler-per-tool. Each tool = one `src/shared/Handlers/<Name>Handler.cs` implementing `IRevitCommand`.
- MCP wrapper class per toolset in `src/server/Program.cs` (`[McpServerToolType, Toolset("<name>")]`).
- Registration in `src/shared/Infrastructure/CommandDispatcher.cs` (Phase N block per wave).
- Toolset gating in `src/server/ToolsetFilter.cs` (KnownToolsets / DefaultOn / WriteCapable).
- Cross-version ElementId via `RevitCompat.ToElementId(long)` / `GetId(...)` / `CanRepresentElementId(long)`. NEVER `.IntegerValue` or `.Value` directly.
- DTO-only returns. Anonymous objects + primitives. Never serialize raw Revit objects.
- Units: external mm/m²/m³/deg, internal feet/sq ft/cu ft/rad. Conversion: `feet = mm / 304.8`, `sq ft = m² / 0.09290304`, `cu ft = m³ / 0.02831685`, `rad = deg × π/180`.
- Guarded JSON parse: `string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson)` inside try/catch `JsonException` → `CommandResult.Fail`.
- Each wave: 10-15 parallel sub-agents (one handler per agent) → wire-up → build (R22-R27) → GPT-5.5 review via opencode → fix pass → rebuild → commit.

---

## Wave 1 — Families (10 tools) — ✅ DONE (`5066f41`)

Toolset **`families`** (new, default-on, write-capable). Files in `src/shared/Handlers/`.

| Tool | Type | Handler file |
|---|---|---|
| `list_loaded_families` | R | ListLoadedFamiliesHandler.cs |
| `load_family_from_path` | W | LoadFamilyFromPathHandler.cs |
| `unload_family` | W | UnloadFamilyHandler.cs |
| `duplicate_family_type` | W | DuplicateFamilyTypeHandler.cs |
| `rename_family_type` | W | RenameFamilyTypeHandler.cs |
| `audit_families` | R | AuditFamiliesHandler.cs |
| `replace_family_type` | W | ReplaceFamilyTypeHandler.cs |
| `get_family_instances` | R | GetFamilyInstancesHandler.cs |
| `list_family_types_in_family` | R | ListFamilyTypesInFamilyHandler.cs |
| `export_family_to_path` | W | ExportFamilyToPathHandler.cs |

GPT review audit: `runs/families-handlers-review/` (1 BLOCKER + 8 MAJOR fixed).

## Wave 2 — MEP Systems (15 tools) — ✅ DONE (`5066f41`)

Toolset **`mep`** (promoted default-on, write-capable). Already had `detect_system_elements`.

| Tool | Type | Handler file |
|---|---|---|
| `create_duct` | W | CreateDuctHandler.cs |
| `create_pipe` | W | CreatePipeHandler.cs |
| `create_cable_tray` | W | CreateCableTrayHandler.cs |
| `create_conduit` | W | CreateConduitHandler.cs |
| `create_air_terminal` | W | CreateAirTerminalHandler.cs |
| `create_lighting_fixture` | W | CreateLightingFixtureHandler.cs |
| `list_mep_systems` | R | ListMepSystemsHandler.cs |
| `get_system_inventory` | R | GetSystemInventoryHandler.cs |
| `get_mep_element_connectors` | R | GetMepElementConnectorsHandler.cs |
| `connect_mep_elements` | W | ConnectMepElementsHandler.cs |
| `create_mep_fitting` | W | CreateMepFittingHandler.cs |
| `set_system_classification` | W | SetSystemClassificationHandler.cs |
| `get_panel_schedule` | R | GetPanelScheduleHandler.cs |
| `find_mep_disconnects` | R | FindMepDisconnectsHandler.cs |
| `analyze_mep_network` | R | AnalyzeMepNetworkHandler.cs |

GPT review audit: `runs/mep-handlers-review/` (0 BLOCKER + 8 MAJOR fixed, incl. canonical connector_id contract).

## Wave 3 — Filters / VG Overrides / Phases (12 tools) — ✅ DONE (`5066f41`)

Toolset **`graphics`** (new, default-on, write-capable).

| Tool | Type | Handler file |
|---|---|---|
| `create_view_filter` | W | CreateViewFilterHandler.cs |
| `apply_filter_to_view` | W | ApplyFilterToViewHandler.cs |
| `set_filter_overrides` | W | SetFilterOverridesHandler.cs |
| `list_view_filters` | R | ListViewFiltersHandler.cs |
| `remove_filter_from_view` | W | RemoveFilterFromViewHandler.cs |
| `override_element_graphics` | W | OverrideElementGraphicsHandler.cs |
| `clear_element_overrides` | W | ClearElementOverridesHandler.cs |
| `get_view_visibility` | R | GetViewVisibilityHandler.cs |
| `set_category_visibility` | W | SetCategoryVisibilityHandler.cs |
| `list_phases` | R | ListPhasesHandler.cs |
| `set_view_phase` | W | SetViewPhaseHandler.cs |
| `set_element_phase` | W | SetElementPhaseHandler.cs |

GPT review audit: `runs/graphics-handlers-review/` (2 BLOCKER + 13 MAJOR fixed).

## Wave 4 — Print / Export (15 tools) — ✅ DONE (`5066f41`)

Toolset **`export`** (promoted default-on, write-capable). Already had `export_room_data`.

| Tool | Type | Handler file |
|---|---|---|
| `export_pdf` | W | ExportPdfHandler.cs |
| `export_dwg` | W | ExportDwgHandler.cs |
| `export_dgn` | W | ExportDgnHandler.cs |
| `export_dwf` | W | ExportDwfHandler.cs |
| `export_ifc` | W | ExportIfcHandler.cs |
| `export_nwc` | W | ExportNwcHandler.cs |
| `export_fbx` | W | ExportFbxHandler.cs |
| `export_gbxml` | W | ExportGbxmlHandler.cs |
| `export_image` | W | ExportImageHandler.cs |
| `export_schedule_csv` | W | ExportScheduleCsvHandler.cs |
| `export_elements_data` | W | ExportElementsDataHandler.cs |
| `batch_export_sheets` | W | BatchExportSheetsHandler.cs |
| `list_export_settings` | R | ListExportSettingsHandler.cs |
| `create_view_sheet_set` | W | CreateViewSheetSetHandler.cs |
| `get_print_settings` | R | GetPrintSettingsHandler.cs |

GPT review audit: `runs/export-handlers-review/` (3 BLOCKER + 9 MAJOR fixed).

---

## Wave 5 — Sheets (12 tools) — ⏳ PENDING

Toolset **`sheets`** (new, default-on, write-capable). Sheet-specific ops (the `view` toolset already covers view creation + place-on-sheet).

| Tool | Type | Description |
|---|---|---|
| `create_sheet` | W | Create a new ViewSheet with title block (number + name). |
| `duplicate_sheet` | W | Duplicate a sheet including its placed viewports. |
| `create_placeholder_sheet` | W | Create a placeholder sheet (no views, only number + name). |
| `list_sheets` | R | All sheets with number, name, revisions, viewport count. |
| `set_titleblock_parameters` | W | Fill title block parameters (project info, date, scale, etc.). |
| `get_titleblock_parameters` | R | Read title block parameters for a sheet. |
| `list_titleblocks` | R | All available TitleBlock family types. |
| `place_schedule_on_sheet` | W | Place a ViewSchedule on a sheet at (x,y) mm. |
| `create_revision` | W | Add a project revision (date, description, issued state). |
| `assign_revision_to_sheet` | W | Link a revision to one or more sheets. |
| `list_revisions` | R | All revisions with assigned sheets. |
| `renumber_sheets` | W | Bulk renumber/reorder sheets with prefix/suffix/find-replace. |

## Wave 6 — Materials (10 tools) — ⏳ PENDING

Toolset **`materials`** (new, default-on, write-capable).

| Tool | Type | Description |
|---|---|---|
| `list_materials` | R | All materials in the document with class, color, identity. |
| `get_material_properties` | R | Physical, thermal, structural, identity asset values for one material. |
| `create_material` | W | New Material element with optional appearance/identity. |
| `duplicate_material` | W | Clone a material with a new name. |
| `set_material_appearance` | W | Color, transparency, gloss, surface pattern of a material. |
| `set_material_identity` | W | Manufacturer, model, cost, keynote, mark, URL. |
| `set_material_structural_asset` | W | Density, modulus, Poisson ratio, structural class. |
| `set_material_thermal_asset` | W | Conductivity, specific heat, emissivity, permeability. |
| `assign_material_to_element` | W | Bind a material to an element's material parameter or compound-structure layer. |
| `get_material_takeoff` | R | Per-material area/volume across the project. |

## Wave 7 — Geometry Analysis (12 tools) — ⏳ PENDING

Toolset **`geometry`** (new, default-on, read-only).

| Tool | Type | Description |
|---|---|---|
| `get_element_bounding_box` | R | Axis-aligned bounding box in mm (min/max xyz). |
| `get_element_geometry` | R | Vertex/edge/face/solid counts + bounding box. |
| `measure_distance_between_elements` | R | 3D distance between two elements (closest-point) in mm. |
| `clash_detection` | R | Pairwise geometric intersections between category sets. |
| `raycast_from_point` | R | First-hit element along a ray (uses ReferenceIntersector). |
| `find_elements_in_volume` | R | Elements whose bbox lies inside an axis-aligned volume or a room. |
| `compute_element_volume` | R | True solid volume in m³ (via geometry traversal, not parameter). |
| `compute_element_area` | R | Total face area in m². |
| `project_point_onto_face` | R | Closest point on a face from a given 3D point. |
| `find_overlapping_elements` | R | Same-category bbox overlap pairs (cheap pre-clash filter). |
| `get_element_centroid` | R | Geometric centroid in mm. |
| `analyze_geometry_complexity` | R | Per-element face/edge/solid counts for performance audits. |

## Wave 8 — Tags / Keynotes / Detail (12 tools) — ⏳ PENDING

Toolset **`annotation`** (existing, promote default-on, write-capable). Already has `tag_all_walls`, `tag_all_rooms`.

| Tool | Type | Description |
|---|---|---|
| `tag_elements` | W | Tag specific element ids in a view with a chosen tag family. |
| `tag_all_by_category` | W | Bulk tag every element of a category visible in a view. |
| `create_text_note` | W | Place a text note at (x,y) mm in a view. |
| `create_dimensions` | W | Linear dimension between references/elements/points. |
| `create_filled_region` | W | 2D filled region from polygon points + fill type. |
| `create_detail_line` | W | 2D detail line in view from start/end (mm). |
| `create_callout_view` | W | Callout view from a crop rectangle of a parent view. |
| `list_keynotes` | R | Project keynote table (key, label, parent). |
| `apply_keynote_to_element` | W | Set the Keynote parameter on element(s). |
| `find_untagged_elements` | R | Elements visible in a view but without a tag. |
| `find_undimensioned_elements` | R | Elements not referenced by any dimension. |
| `wipe_empty_tags` | W | Find + delete tags whose text resolves to empty (with dry-run). |

## Wave 9 — Rooms / Areas / Spaces deep (10 tools) — ⏳ PENDING

Toolset **`rooms`** (new, default-on, write-capable).

| Tool | Type | Description |
|---|---|---|
| `list_rooms` | R | All rooms with id, name, number, level, area (m²), volume (m³), perimeter. |
| `get_room_boundaries` | R | Room boundary polygon as ordered xy points in mm. |
| `get_room_openings` | R | Doors and windows whose host wall bounds this room. |
| `create_room_separator` | W | Model line of curve type RoomSeparation to close a region. |
| `create_area` | W | New Area element on an AreaPlan view at point. |
| `create_space` | W | MEP Space (for HVAC analysis) at point on a level. |
| `list_areas` | R | All areas grouped by AreaScheme (Gross/Rentable/etc.). |
| `compute_room_finishes` | R | Base, wall, ceiling, floor finish parameter values per room. |
| `auto_create_rooms_from_walls` | W | NewRooms2 — fill closed wall regions with rooms on a level. |
| `tag_all_areas` | W | Bulk tag every area on an AreaPlan view. |

## Wave 10 — Linked Models / CAD / Coordinates (10 tools) — ⏳ PENDING

Toolset **`links`** (new, default-on, write-capable).

| Tool | Type | Description |
|---|---|---|
| `list_linked_models` | R | All RVT links with file path, load state, instance count. |
| `list_linked_cad` | R | DWG/DXF imports + links with file path, scale, level. |
| `import_cad_to_view` | W | Import a DWG/DXF into a specified view. |
| `link_revit_model` | W | Link an external `.rvt` into the project. |
| `unload_link` | W | Unload a link (keep reference, drop geometry). |
| `reload_link` | W | Reload a previously unloaded link. |
| `get_link_elements` | R | Query elements inside a linked RVT model. |
| `acquire_coordinates_from_link` | W | Sync project base point from a linked file. |
| `publish_coordinates_to_link` | W | Push project coordinates to a linked file. |
| `set_project_base_point` | W | Set survey point / project base point coordinates. |

## Wave 11 — Shared + Project Parameters (8 tools) — ⏳ PENDING

Toolset **`parameters`** (new, default-on, write-capable). Already have `list_project_parameters` in `query`.

| Tool | Type | Description |
|---|---|---|
| `list_shared_parameters` | R | All shared parameters known to the application + which are bound. |
| `create_shared_parameter` | W | Add a new shared parameter to the .txt file. |
| `bind_shared_parameter` | W | Bind a shared parameter to categories as instance or type. |
| `create_project_parameter` | W | Pure project parameter (no shared file backing). |
| `list_project_parameter_bindings` | R | Each project parameter and the categories it is bound to. |
| `remove_parameter_binding` | W | Unbind a parameter from a category. |
| `export_shared_parameter_file` | R | Read the shared parameter .txt as structured DTO. |
| `set_parameter_value_by_guid` | W | Set a shared parameter value on elements by GUID instead of name. |

## Wave 12 — View Templates + Selection (10 tools) — ⏳ PENDING

Toolset **`organization`** (new, default-on, write-capable).

| Tool | Type | Description |
|---|---|---|
| `list_view_templates` | R | All view templates with view-type compatibility + included settings. |
| `create_view_template_from_view` | W | Capture current view's settings as a new template. |
| `apply_view_template` | W | Apply a template to one or more views. |
| `duplicate_view_template` | W | Clone a template under a new name. |
| `delete_view_template` | W | Remove a template (with usage warning). |
| `save_selection` | W | Save the current UI selection (or supplied ids) under a name. |
| `load_selection` | R | Return the element ids of a saved selection by name. |
| `list_saved_selections` | R | All SelectionFilterElement entries with member counts. |
| `delete_saved_selection` | W | Delete a saved selection by name. |
| `select_elements` | W | Set the active UI selection to specified element ids. |

## Wave 13 — Workflow Composites (8 tools) — ⏳ PENDING

Toolset **`workflows`** (new, default-on, write-capable). Each tool chains multiple existing tools server-side for common BIM workflows.

| Tool | Type | Description |
|---|---|---|
| `workflow_clash_review` | composite | Run clash_detection + colorize hits + create review markers + open 3D view. |
| `workflow_model_audit` | composite | model health + warnings + family audit + view cleanup; single report. |
| `workflow_room_documentation` | composite | For each room: callout view + section + finish schedule. |
| `workflow_sheet_set` | composite | Bulk: create sheets, place views, assign title blocks, set parameters. |
| `workflow_data_roundtrip` | composite | Export params to CSV, wait for edit signal, re-import. |
| `workflow_view_cleanup` | composite | Find unused/empty views + propose deletes with dry-run. |
| `workflow_naming_normalization` | composite | Apply a naming pattern across views, sheets, levels, grids (dry-run). |
| `workflow_takeoff_report` | composite | Material + quantity + cost roll-up across categories. |

## Wave 14 — Structural deep (12 tools) — ⏳ PENDING

Toolset **`structural`** (new, default-on, write-capable).

| Tool | Type | Description |
|---|---|---|
| `create_structural_column` | W | Point-based structural column on a level. |
| `create_structural_beam` | W | Line-based structural framing member (beam/brace/joist). |
| `create_structural_wall` | W | Wall with structural usage = Bearing/Shear/Combined. |
| `create_foundation_isolated` | W | Isolated foundation at a column point. |
| `create_foundation_wall` | W | Wall foundation under a wall. |
| `create_rebar_set` | W | Straight rebar inside a host element. |
| `create_rebar_stirrup` | W | Bent stirrup with shape and spacing. |
| `list_rebar` | R | All rebar in an element or view with bar size and bend shape. |
| `get_structural_loads` | R | Point/area/line loads on an element. |
| `set_structural_load` | W | Create or modify a load (point/line/area). |
| `analyze_structural_connections` | R | Joins / connections summary per element. |
| `tag_structural_framing` | W | Bulk tag all framing elements in a view. |

## Wave 15 — Final fill (8 tools) — ⏳ PENDING

Toolset assignments mixed: extends existing toolsets (`meta`, `view`).

| Tool | Type | Toolset | Description |
|---|---|---|---|
| `set_project_info` | W | meta | Update ProjectInformation: name, number, client, address, status. |
| `get_model_warnings_summary` | R | lint | Warnings grouped by type, with counts and example element ids. |
| `purge_unused` | W | meta | Find + delete unused families/types/materials (dry-run preview first). |
| `capture_view_image` | W | view | Quick screenshot of the active view to a file (simpler than `export_image`). |
| `set_view_crop` | W | view | Enable/disable crop region + set bounds (mm) or auto-fit to elements. |
| `set_view_scale` | W | view | Change a view's scale (1:50, 1:100, etc.). |
| `activate_view` | W | view | Open/zoom-to a view in the Revit UI. |
| `show_element_in_view` | W | view | Zoom-to and select specific elements in the active view. |

---

## Toolset summary (final state at 221)

| Toolset | Tools | Default | Write-capable |
|---|---:|---|---|
| `query` | 16 | on | — |
| `create` | 7 | on | yes |
| `modify` | 5 | off | yes |
| `delete` | 1 | off | yes |
| `view` | 3 + 5 (W15) = 8 | on | yes |
| `meta` | 4 + 2 (W15) = 6 | on | — |
| `lint` | 3 + 1 (W15) = 4 | on | — |
| `schedule` | 10 | on | yes |
| `families` | 10 | on | yes |
| `mep` | 16 | on | yes |
| `graphics` | 12 | on | yes |
| `export` | 16 | on | yes |
| `annotation` | 2 + 12 (W8) = 14 | on (after W8) | yes |
| `sheets` (W5) | 12 | on | yes |
| `materials` (W6) | 10 | on | yes |
| `geometry` (W7) | 12 | on | — |
| `rooms` (W9) | 10 | on | yes |
| `links` (W10) | 10 | on | yes |
| `parameters` (W11) | 8 | on | yes |
| `organization` (W12) | 10 | on | yes |
| `workflows` (W13) | 8 | on | yes |
| `structural` (W14) | 12 | on | yes |
| `toolbaker` | 3 (+3 adaptive) | on | yes |
| **Total** | **221** (224 with adaptive) | | |

---

## How to resume

For each pending wave:

1. **Read this file's wave section** + `CLAUDE.md` for conventions.
2. **Dispatch N parallel sub-agents** (one per tool). Each agent: read `RevitCompat.cs` + nearest sibling handler for reference, write `src/shared/Handlers/<Name>Handler.cs`, verify file exists (test-path + retry 3x). Constraints: no modifications to Program.cs / CommandDispatcher.cs / ToolsetFilter.cs / tests / docs; no `dotnet build`; no sub-spawning.
3. **Wire up**: add Phase N block to CommandDispatcher; add `<XxxTools>` class with `[McpServerTool]` methods to Program.cs; promote/add toolset in ToolsetFilter.
4. **Build**: `dotnet build src/RvtMcp.sln -c Debug` — Revit must be closed (build deploys add-ins). Must report 0 errors on all 6 plugin projects.
5. **GPT review** via opencode: write `runs/<wave-slug>/prompt-01.md` (use existing wave prompts as templates), dispatch `opencode run --model openai/gpt-5.5 --variant xhigh`, tee output to `output-01.txt`.
6. **Fix pass**: parse BLOCKER + MAJOR findings, write `prompt-02.md`, dispatch opencode again to apply fixes. Rebuild.
7. **Commit** when wave is clean: short conventional-commit subject + body listing tools.
8. After all 11 remaining waves: regenerate `tests/RvtMcp.Tests/Golden/tools-list.json` via `UPDATE_SNAPSHOTS=1 dotnet test`; update README badges + toolset table + CHANGELOG; tag a release.

## References

- Handler conventions: `CLAUDE.md`
- Cross-version helper: `src/shared/Infrastructure/RevitCompat.cs`
- Toolset gating: `src/server/ToolsetFilter.cs`
- Existing handler exemplars: `src/shared/Handlers/CreateLineBasedElementHandler.cs`, `CreatePointBasedElementHandler.cs`, `ListSchedulesHandler.cs`, `ExportRoomDataHandler.cs`, `ColorElementsHandler.cs`.
- Competitor tool inventory (research): `D:\Workspace\CloneFromGit\competition\SUMMARY_orchestrator.md`.

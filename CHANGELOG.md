# Changelog

## Unreleased

### Added

- Added 34 new MCP handlers across 3 new toolsets (increasing the total non-adaptive surface to 143 tools across 17 toolsets):
  - **Sheets (12 tools)**: `create_sheet`, `duplicate_sheet`, `create_placeholder_sheet`, `list_sheets`, `set_titleblock_parameters`, `get_titleblock_parameters`, `list_titleblocks`, `place_schedule_on_sheet`, `create_revision`, `assign_revision_to_sheet`, `list_revisions`, `renumber_sheets`.
  - **Materials (10 tools)**: `list_materials`, `get_material_properties`, `create_material`, `duplicate_material`, `set_material_appearance`, `set_material_identity`, `set_material_structural_asset`, `set_material_thermal_asset`, `assign_material_to_element`, `get_material_takeoff`.
  - **Geometry Analysis (12 tools)**: `get_element_bounding_box`, `get_element_geometry`, `measure_distance_between_elements`, `clash_detection`, `raycast_from_point`, `find_elements_in_volume`, `compute_element_volume`, `compute_element_area`, `project_point_onto_face`, `find_overlapping_elements`, `get_element_centroid`, `analyze_geometry_complexity`.
- Added 32 MCP handlers across annotation, rooms, and links (increasing the total non-adaptive surface to 175 tools across 19 toolsets):
  - **Annotation / Detail (12 tools)**: `tag_elements`, `tag_all_by_category`, `create_text_note`, `create_dimensions`, `create_filled_region`, `create_detail_line`, `create_callout_view`, `list_keynotes`, `apply_keynote_to_element`, `find_untagged_elements`, `find_undimensioned_elements`, `wipe_empty_tags`.
  - **Rooms / Areas / Spaces (10 tools)**: `list_rooms`, `get_room_boundaries`, `get_room_openings`, `create_room_separator`, `create_area`, `create_space`, `list_areas`, `compute_room_finishes`, `auto_create_rooms_from_walls`, `tag_all_areas`.
  - **Links / CAD / Coordinates (10 tools)**: `list_linked_models`, `list_linked_cad`, `import_cad_to_view`, `link_revit_model`, `unload_link`, `reload_link`, `get_link_elements`, `acquire_coordinates_from_link`, `publish_coordinates_to_link`, `set_project_base_point`.

### Changed

- Hardened new annotation, rooms, and links handlers around dry-run semantics, ElementId compatibility checks, destructive link scope validation, and transaction commit-status reporting.
- Made `send_code_to_revit` part of the default ToolBaker surface, without adaptive-bake gating or per-call Revit confirmation.
- Changed installer client wiring to a single auto-detect `bimwright-rvt` MCP entry while still deploying plugins for every detected Revit year.
- Added 15 non-schedule Revit data tools: 10 read tools for elements, parameters, groups, assemblies, and worksets, plus 5 write tools for parameters, type changes, worksets, and group creation.
- Added 10 Revit schedule tools in a new default-on `schedule` toolset: `list_schedules`, `get_schedule_definition`, `get_schedule_data`, `get_schedule_formulas`, `get_schedulable_fields`, `find_schedule_elements`, `create_schedule`, `add_schedule_field`, `update_schedule_field`, `apply_schedule_filter_sort`.

## v0.3.0 - ToolBaker redesign

### Breaking

- Removed `bake_tool`. It is no longer available as an MCP tool. Create new baked tools through the adaptive-bake suggestion flow and `accept_bake_suggestion` instead.

### Added

- Added adaptive-bake suggestion lifecycle tools: `list_bake_suggestions`, `accept_bake_suggestion`, and `dismiss_bake_suggestion`.
- Added accepted-tool indirection through `list_baked_tools` and `run_baked_tool`, including parameter validation and archived-tool guidance.
- Added Revit ribbon/runtime support for accepted baked tools, backed by a plugin runtime cache.
- Added local SQLite-backed bake storage (`bake.db`) plus local audit/usage records under `%LOCALAPPDATA%\Bimwright\`.
- Added compiler policy, privacy, registry, runtime cache, usage clustering, and ToolBaker handler tests.

### Changed

- Adaptive bake is opt-in and default off via `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` or `"enableAdaptiveBake": true`.
- Usage data for adaptive bake is stored locally under `%LOCALAPPDATA%\Bimwright\`.
- Accepted baked tools are discovered with `list_baked_tools` and executed with `run_baked_tool name=<tool_name>`.
- The Revit plugin reads `bake.db` and owns runtime/ribbon loading only; the server is the sole SQLite writer.
- `send_code_to_revit` is gated behind adaptive bake and continues to require per-call Revit confirmation.
- Baked tools no longer appear as promoted native MCP tools in v0.3.x; agents use the stable accepted-tool index instead.
- README and docs now reflect the current 32-tool default surface, 35-tool adaptive surface, supported MCP clients, and R22/R26/R27 accepted-tool smoke evidence.

### Fixed

- Fixed baked-tool dispatch bypasses by preventing `run_baked_tool` execution through `batch_execute` and isolating baked commands from core command lookup.
- Fixed stale cross-version compatibility metadata by refreshing baked-tool registry reads from `bake.db` before list/meta reads.
- Fixed Revit 2026/2027 accepted-tool startup failures by copying native `e_sqlite3.dll` beside `Bimwright.Rvt.Plugin.dll` during deploy and release staging.
- Fixed suggestion refresh hardening: bounded usage replay, capped/deduped suggestions, and guarded adaptive usage capture.

### Security

- Hardened durable logs, journals, prompts, markdown, and live responses so send-code outputs and sensitive literals are redacted or hashed before persistence.
- Added redaction boundary fixes for paths, filenames, escaped outputs, and legacy orphan call archives.
- Added compiler denylist coverage and plugin allow-list narrowing for baked C# execution.

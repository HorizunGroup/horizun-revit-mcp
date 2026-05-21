# Wave 13 - Workflow Composites Implementation Spec

## Status

Implemented in `src/shared/Handlers/Workflow*Handler.cs`. The handlers compose
the primitive tools and Revit APIs available through Waves 5, 6, 7, 8, 9, and
12. Dependencies that belong to Wave 15 are handled directly where practical
instead of returning preview placeholders.

## Scope

- Toolset: `workflows`
- Tools: 8 composite tools
- Default: on after implementation
- Write-capable: yes
- Primary goal: expose common BIM workflows as one MCP call while preserving an
  auditable step report and predictable rollback behavior.

## Dependencies

- Current dispatcher and batch precedent: `BatchExecuteHandler` and
  `BatchExecutor`.
- Primitive tools from earlier waves:
  - Geometry: `clash_detection`
  - Graphics: `override_element_graphics`, `create_view_filter`
  - Annotation: text notes, tags, callouts, dimensions
  - Sheets: create/list/place/set titleblock parameters
  - Materials: material takeoff
  - Rooms: room boundaries/openings/finishes
  - Organization: view templates, saved selections
  - Final fill: warnings summary, purge preview, view activation/show element

## Toolset Contract

Composite tools should not hide low-level failure details. Every workflow returns:

- `workflow`: tool name.
- `dry_run`: boolean.
- `status`: `succeeded`, `partial`, `failed`, or `preview`.
- `steps`: ordered array of `{ name, tool, status, inputs_summary, result,
  error }`.
- `created_ids`: element IDs created by the workflow.
- `modified_ids`: element IDs modified by the workflow.
- `warnings`: non-fatal issues and skipped actions.
- `rollback`: `{ strategy, rolled_back, reason }`.

For write workflows:

- `dry_run=true` is required by default for broad or destructive workflows.
- Use `TransactionGroup` when the workflow owns multiple write transactions.
- If a workflow calls existing handlers directly, preserve their DTOs in
  `steps[].result` and do not reinterpret errors as success.
- If any write step fails and `continue_on_error=false`, roll back the group.
- If `continue_on_error=true`, commit only steps whose handlers report success
  and return `status=partial` with exact failed step details.

## Tool Specs

### workflow_clash_review

- Type: composite write-capable.
- Handler: `WorkflowClashReviewHandler.cs`.
- MCP params:
  - `category_a` string, required.
  - `category_b` string, required.
  - `view_id` long, optional; defaults to active 3D view when suitable.
  - `max_pairs` int, optional, default 200, max 2000.
  - `create_review_view` bool, default true.
  - `color_hits` bool, default true.
  - `create_markers` bool, default false.
  - `dry_run` bool, default true.
  - `continue_on_error` bool, default false.
- Response DTO: common workflow envelope plus `clashes` with pair IDs,
  intersection/bounding-box summary, optional review view ID, optional marker IDs.
- Revit API strategy: run the Wave 7 `clash_detection` logic first. If not a
  dry run and requested, create or reuse a 3D review view, color involved
  elements through graphics overrides, and optionally place text notes/markers.
- Transaction behavior: one `TransactionGroup` for review view, overrides, and
  markers. Geometry detection remains read-only.
- Validation: categories must resolve to model categories; `view_id` must be a
  3D or graphics-overridable view; reject `max_pairs` above cap.
- Smoke test: two overlapping walls or ducts return at least one clash in
  preview; non-dry-run colors the clashing elements and returns modified IDs.

### workflow_model_audit

- Type: composite read-only by default; write-capable only when cleanup actions
  are enabled in a future extension.
- Handler: `WorkflowModelAuditHandler.cs`.
- MCP params:
  - `include_warnings` bool, default true.
  - `include_families` bool, default true.
  - `include_views` bool, default true.
  - `include_schedules` bool, default true.
  - `include_mep` bool, default true.
  - `limit_per_section` int, default 100, max 1000.
- Response DTO: `summary`, `sections`, `risk_counts`, `recommendations`,
  `truncated`.
- Revit API strategy: aggregate `analyze_model_statistics`,
  `get_model_warnings_summary`, `audit_families`, view naming tools, schedules,
  and optional MEP disconnect/network checks.
- Transaction behavior: none.
- Validation: section flags cannot all be false; limit is capped.
- Smoke test: blank model returns valid sections with zero or empty findings.

### workflow_room_documentation

- Type: composite write-capable.
- Handler: `WorkflowRoomDocumentationHandler.cs`.
- MCP params:
  - `room_ids` array of long, optional; omitted means all placed rooms up to
    `limit`.
  - `level_name` string, optional.
  - `create_callouts` bool, default true.
  - `create_finish_schedule` bool, default true.
  - `tag_rooms` bool, default true.
  - `sheet_id` long, optional.
  - `dry_run` bool, default true.
  - `limit` int, default 50, max 200.
- Response DTO: common workflow envelope plus per-room docs:
  `{ room_id, callout_view_id, schedule_id, tag_ids, warnings }`.
- Revit API strategy: use room boundary/finish tools, callout/text/tag tools,
  and schedule/sheet tools. Do not invent missing room boundaries; report
  unplaced/not-enclosed rooms as skipped.
- Transaction behavior: one `TransactionGroup`; use per-room `SubTransaction`
  so a bad room does not corrupt successful room reports.
- Smoke test: one enclosed room creates or previews one documentation record.

### workflow_sheet_set

- Type: composite write-capable.
- Handler: `WorkflowSheetSetHandler.cs`.
- MCP params:
  - `sheets` array, required. Each item: `sheet_number`, `sheet_name`,
    optional `titleblock_type_id`, optional `view_ids`, optional
    `schedule_ids`, optional `parameters`.
  - `renumber_strategy` string, optional: `none`, `prefix`, `replace`.
  - `dry_run` bool, default true.
  - `continue_on_error` bool, default false.
- Response DTO: common workflow envelope plus `sheets` array with created sheet
  IDs, placed viewports, placed schedules, and parameter update status.
- Revit API strategy: compose Wave 5 sheet/titleblock/schedule placement tools
  and existing `place_view_on_sheet` behavior.
- Transaction behavior: transaction group with per-sheet subtransactions.
- Validation: duplicate sheet numbers fail before mutation; empty `sheets` array
  fails; all view/schedule IDs must resolve unless `continue_on_error=true`.
- Smoke test: dry-run with two sheets reports proposed numbers and placements
  without creating sheets.

### workflow_data_roundtrip

- Type: composite write-capable.
- Handler: `WorkflowDataRoundtripHandler.cs`.
- MCP params:
  - `category` string, required.
  - `parameter_names` array of string, optional.
  - `export_path` string, required absolute path.
  - `import_path` string, optional absolute path.
  - `mode` string: `export_only`, `import_only`, `export_then_import`.
  - `dry_run` bool, default true for import modes.
  - `key_field` string, default `element_id`.
- Response DTO: exported file path, imported row counts, changed elements,
  skipped rows, validation errors.
- Revit API strategy: export with `export_elements_data`; import by mapping
  rows back to elements and applying `set_element_parameter_values` with
  unit-aware conversion.
- Transaction behavior: export-only is read/no transaction. Import uses one
  transaction with per-row validation before commit.
- Validation: reject relative paths, path traversal, missing import file, unknown
  parameters, and duplicate element rows.
- Smoke test: export-only creates a CSV/JSON; dry-run import reports proposed
  changes without mutation.

### workflow_view_cleanup

- Type: composite write-capable.
- Handler: `WorkflowViewCleanupHandler.cs`.
- MCP params:
  - `include_unused_views` bool, default true.
  - `include_empty_schedules` bool, default true.
  - `include_naming_outliers` bool, default true.
  - `delete_empty_views` bool, default false.
  - `dry_run` bool, default true.
  - `limit` int, default 200, max 1000.
- Response DTO: candidates grouped by type, proposed deletes, warnings,
  deleted IDs if committed.
- Revit API strategy: combine view collectors, schedule definition/data checks,
  naming analysis, and optional delete handler.
- Transaction behavior: no transaction for analysis; delete uses one transaction
  and must require `dry_run=false` plus explicit delete flag.
- Validation: never delete templates, sheets, active view, placed views, or views
  with dependent views unless explicitly supported later.
- Smoke test: naming-only mode reports outliers and no mutations.

### workflow_naming_normalization

- Type: composite write-capable.
- Handler: `WorkflowNamingNormalizationHandler.cs`.
- MCP params:
  - `target` string: `views`, `sheets`, `levels`, `grids`, or `all`.
  - `profile` string, optional.
  - `pattern` string, optional.
  - `ids` array of long, optional.
  - `dry_run` bool, default true.
  - `limit` int, default 200, max 1000.
- Response DTO: proposed renames, applied renames, conflicts, skipped items.
- Revit API strategy: use view naming analyzer/suggestion logic where possible;
  add direct rename behavior for sheets/levels/grids only with collision checks.
- Transaction behavior: one transaction for renames, with all proposed names
  validated before mutation.
- Validation: reject duplicate target names, invalid characters, unsupported
  element kinds, and read-only names.
- Smoke test: dry-run returns deterministic proposed names for a small set.

### workflow_takeoff_report

- Type: composite read-heavy, write-capable only when exporting to a file.
- Handler: `WorkflowTakeoffReportHandler.cs`.
- MCP params:
  - `categories` array of string, optional; defaults to common model categories.
  - `include_materials` bool, default true.
  - `include_quantities` bool, default true.
  - `include_cost` bool, default false.
  - `output_path` string, optional absolute JSON/CSV path.
  - `limit_per_category` int, default 500, max 5000.
- Response DTO: category summary, material summary, quantities, cost fields,
  output path, truncation warnings.
- Revit API strategy: combine material takeoff, material properties, element
  quantities, and optional file export.
- Transaction behavior: none for in-memory report; file output writes to disk
  but does not mutate Revit.
- Validation: categories must resolve; output path must be absolute and parent
  directory must exist.
- Smoke test: blank model returns empty category/material arrays and no failure.

## Wiring

- Add `WorkflowsTools` to `Program.cs` with all 8 MCP wrappers.
- Add `workflows` to `ToolsetFilter.KnownToolsets`, `DefaultOn`, and
  `WriteCapable`.
- Register 8 handlers in a new CommandDispatcher phase after primitive waves.
- Refresh golden snapshots after implementation.

## Acceptance Criteria

- Workflow tools do not appear before their primitive dependencies compile.
- Every workflow supports dry-run where broad writes or deletes are possible.
- Step report accurately distinguishes preview, committed, skipped, and failed
  work.
- `dotnet build src/RvtMcp.sln -c Debug` succeeds.
- Tool snapshot tests are updated and pass.

## Review Checklist

- BLOCKER: workflow reports success while a sub-step failed.
- BLOCKER: broad delete/rename operation can commit without dry-run/explicit flag.
- MAJOR: partial commit does not list committed and skipped IDs.
- MAJOR: composite tool duplicates low-level logic instead of reusing helpers or
  stable handler patterns.
- MAJOR: response can exceed guardrails with no limit/truncation signal.

## Known Risks

- This wave can easily become a second dispatcher. Keep orchestration thin and
  push domain logic into primitive handlers/helpers.
- Workflow tools should be implemented after primitive waves. Implementing them
  early will force speculative contracts and likely rework.

# Wave 15 - Final Fill Target Implementation Spec

## Status

Target implementation spec. This wave is pending.

## Scope

- Toolsets extended: `meta`, `lint`, and `view`
- Tools: 8
- Default: use existing default-on toolsets
- Write-capable: yes for `meta` and `view` additions; `lint` addition is read.
- Primary goal: close high-value gaps in project metadata, warnings, purge
  preview, and active-view operations.

## Dependencies

- Existing `meta`, `lint`, and `view` toolsets.
- Existing view creation/placement/layout handlers.
- Existing export image handler for image capture behavior.
- Existing delete/graphics/query helpers for purge/show operations.

## Toolset Contract

- `meta` additions mutate project-wide state and must support dry-run where
  broad deletion is possible.
- `lint` additions are read-only.
- `view` additions can affect UI state or view settings; they must validate the
  target view and document UI constraints.
- `--read-only` gating must remove write-capable `view` tools when this wave is
  implemented. Current gating should be corrected before or during this wave.

## Tool Specs

### set_project_info

- Type: W.
- Toolset: `meta`.
- Handler: `SetProjectInfoHandler.cs`.
- MCP params: optional strings `name`, `number`, `client_name`, `address`,
  `status`, `issue_date`, plus `parameters` object for named project
  information parameters.
- ParametersSchema: object with no required fields but at least one supplied
  field required at runtime.
- Response DTO: `{ updated, changed, skipped, project_info_id, warnings }`.
- Revit API strategy: resolve `doc.ProjectInformation`; set built-in and named
  parameters via writable `Parameter` objects.
- Transaction: one transaction.
- Validation: fail if no fields supplied; skip read-only/missing parameters with
  structured warnings unless all fields fail.
- Smoke test: set project number/status in a test model and read it back.

### get_model_warnings_summary

- Type: R.
- Toolset: `lint`.
- Handler: `GetModelWarningsSummaryHandler.cs`.
- MCP params: `include_examples` bool default true, `max_examples_per_type` int
  default 5 max 50, `include_element_ids` bool default true.
- Response DTO: `total_warnings`, `by_description`, `by_severity` if available,
  examples with warning text and failing element IDs.
- Revit API strategy: use `Document.GetWarnings()` and map `FailureMessage`
  data to DTOs. Do not serialize raw failure messages.
- Transaction: none.
- Validation: cap example count.
- Smoke test: blank model returns zero or current warning count without failure.

### purge_unused

- Type: W.
- Toolset: `meta`.
- Handler: `PurgeUnusedHandler.cs`.
- MCP params: `targets` array of strings optional (`families`, `types`,
  `materials`, `views`, `all`), `dry_run` bool default true,
  `include_system_types` bool default false, `limit` int default 500 max 5000.
- Response DTO: `dry_run`, `candidates`, `deleted_ids`, `skipped`, `warnings`,
  `truncated`.
- Revit API strategy: start with conservative collectors for unused loadable
  families/types/materials. Do not attempt to replicate Revit's full built-in
  Purge Unused dialog in v1.
- Transaction: no transaction for dry-run; one transaction for delete.
- Validation: `dry_run=false` required for deletion; never delete active view,
  placed views, titleblocks in use, material assets in use, or system types
  unless explicitly supported and safe.
- Smoke test: dry-run on sample model returns candidates only. Non-dry-run on a
  disposable model deletes a known unused family/type.

### capture_view_image

- Type: W to disk, no Revit document mutation.
- Toolset: `view`.
- Handler: `CaptureViewImageHandler.cs`.
- MCP params: `view_id` optional, `output_path` absolute string required,
  `pixel_size` int default 1600, `image_format` string `png|jpeg`.
- Response DTO: `{ exported, view_id, view_name, output_path, produced_files,
  warnings }`.
- Revit API strategy: reuse the robust portions of `ExportImageHandler` for
  `ImageExportOptions` and produced-file verification.
- Transaction: none.
- Validation: absolute output path, existing parent folder, exportable view, not
  a template, supported extension, safe filename.
- Smoke test: active floor plan exports an image and verifies produced file.

### set_view_crop

- Type: W.
- Toolset: `view`.
- Handler: `SetViewCropHandler.cs`.
- MCP params: `view_id` optional default active view, `enabled` bool optional,
  `visible` bool optional, `bounds` object optional with min/max x/y/z mm,
  `fit_element_ids` array optional, `padding_mm` optional default 100.
- Response DTO: view ID/name, old crop state, new crop state, crop box mm,
  warnings.
- Revit API strategy: resolve a non-template graphical view, set crop box
  active/visible where supported, compute bounding box from supplied elements
  if requested.
- Transaction: one transaction.
- Validation: reject views that do not support crop boxes, invalid bounds, empty
  `fit_element_ids`, and unresolved IDs.
- Smoke test: fit crop to two walls and verify crop active.

### set_view_scale

- Type: W.
- Toolset: `view`.
- Handler: `SetViewScaleHandler.cs`.
- MCP params: `view_id` optional default active view, `scale` int required.
- Response DTO: `{ view_id, old_scale, new_scale, changed }`.
- Revit API strategy: set `View.Scale` on views that support scale. Sheets and
  schedules should fail with clear errors.
- Transaction: one transaction.
- Validation: positive scale, target view supports scale, parameter not
  controlled by an uneditable template.
- Smoke test: set floor plan to 100 and verify readback.

### activate_view

- Type: W UI operation.
- Toolset: `view`.
- Handler: `ActivateViewHandler.cs`.
- MCP params: `view_id` long optional, `view_name` string optional,
  `allow_sheet` bool default true.
- Response DTO: previous active view ID/name, activated view ID/name, warnings.
- Revit API strategy: use `UIDocument.RequestViewChange(view)` where available
  and safe inside the external-event context; otherwise use documented UI API
  fallback if available.
- Transaction: none.
- Validation: exactly one view resolved; no templates; ambiguous names fail.
- Smoke test: activate a known floor plan from another active view.

### show_element_in_view

- Type: W UI operation.
- Toolset: `view`.
- Handler: `ShowElementInViewHandler.cs`.
- MCP params: `element_ids` array long required, `view_id` optional,
  `activate_view` bool default true, `select` bool default true,
  `zoom` bool default true.
- Response DTO: target view, selected IDs, missing IDs, hidden/not-visible
  warnings.
- Revit API strategy: optionally activate view, set `uidoc.Selection`, and use
  `uidoc.ShowElements` or equivalent zoom-to behavior.
- Transaction: none.
- Validation: non-empty element array, IDs in range, target view not template,
  all missing IDs reported.
- Smoke test: select and zoom to a wall in active plan.

## Wiring

- Extend existing `MetaTools`, `LintTools`, and `ViewTools` in `Program.cs`.
- Register 8 handlers in `CommandDispatcher`.
- Update `ToolsetFilter.WriteCapable` so `view` is stripped by `--read-only`.
  If `meta` remains mixed read/write, document the split or create a stricter
  read-only filter before relying on read-only mode.
- Refresh golden snapshots and README tool tables.

## Acceptance Criteria

- Mixed toolset additions appear in the correct existing toolsets.
- `--read-only` does not expose write-capable view operations.
- Purge cannot delete anything unless `dry_run=false`.
- UI operations return clear errors when no active UIDocument is available.
- Build and snapshot tests pass after implementation.

## Review Checklist

- BLOCKER: purge deletes in dry-run mode or deletes active/placed/template views.
- BLOCKER: view activation uses an API that is illegal in ExternalEvent context.
- MAJOR: image capture reports a file path that does not exist.
- MAJOR: crop/scale silently fails under view templates.
- MAJOR: project-info setter treats missing/read-only parameters as success.

## Known Risks

- `meta` is currently classified as not write-capable even though this wave adds
  write operations. Read-only filtering needs a clear decision before release.
- UI view activation and zoom behavior may need manual Revit smoke testing.

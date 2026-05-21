# Wave 03 - Graphics And Phases As-Built Spec

## Status

Done in the current codebase as of 2026-05-21.

- Toolset: `graphics`
- Registration surface: default-on and write-capable.
- Implemented tools: 12.
- Server wrappers: `src/server/Program.cs`, `GraphicsTools`.
- Dispatcher registrations: `src/shared/Infrastructure/CommandDispatcher.cs`, phase 15 graphics registrations.
- Handler location: `src/shared/Handlers/*Graphics*.cs`, `src/shared/Handlers/*Filter*.cs`, and `src/shared/Handlers/*Phase*.cs`.
- Review evidence: `runs/graphics-handlers-review/output-01.txt` found 2 blockers and 13 major issues; `runs/graphics-handlers-review/output-02.txt` confirmed the fixes were present. No build or live Revit smoke run is claimed by this spec.

## Scope

This wave exposes view filter, element override, category visibility, and phase tools for the active Revit document.

Included tools:

- `create_view_filter`
- `apply_filter_to_view`
- `set_filter_overrides`
- `list_view_filters`
- `remove_filter_from_view`
- `override_element_graphics`
- `clear_element_overrides`
- `get_view_visibility`
- `set_category_visibility`
- `list_phases`
- `set_view_phase`
- `set_element_phase`

Out of scope:

- Creating view templates or modifying template-controlled visibility beyond what Revit permits on the target view.
- Creating new phase definitions or phase filters.
- Bulk graphic standards management outside explicit view/filter/category/element operations.
- Guaranteeing that every Revit category or parameter supports every requested visibility, phase, or filter rule operation.

## Dependencies

- Revit API shell targets are `R22` through `R27`.
- The Revit add-in must have an open document for all tools except server-side parameter validation performed before dispatch.
- `Program.cs` maps MCP tool parameters to snake_case command payloads and sends them through `ToolGateway.SendToRevit`.
- `CommandDispatcher` maps command names to one handler instance per tool.
- Handlers use `Newtonsoft.Json.Linq` and the repo's guarded JSON parsing convention.
- Handlers use `RevitCompat` for `ElementId` range checks and `ElementId` creation.
- View-scoped graphics writes require `View.AreGraphicsOverridesAllowed()` to pass.
- Color parameters use HTML-style RGB strings. The handlers accept `#RRGGBB` and `RRGGBB` forms for override tools.
- Filter rule creation depends on Revit parameter metadata and reflective `ParameterFilterRuleFactory` calls so the same handler can run across supported Revit versions.
- Surface foreground color overrides depend on resolving a solid fill pattern when Revit requires a fill pattern id before the foreground color is visible.

## Toolset Contract

`graphics` is a default-on write-capable toolset in `ToolsetFilter`.

Server-side contract:

- `GraphicsTools` in `Program.cs` defines the MCP surface and parameter names.
- Optional `viewId` parameters are serialized as `view_id`; omitted or null means "use the active view" where the handler supports that default.
- Empty optional color strings are converted to null before dispatch.
- `create_view_filter` accepts `rules` as a string in the MCP wrapper. If nonblank, `Program.cs` parses it as a JSON array before sending it to Revit.
- `elementIds` and `categories` are sent as arrays.

Handler-side contract:

- Read tools do not start Revit transactions.
- Revit model/view-setting writes use explicit `Transaction` instances.
- `set_element_phase` uses per-element `SubTransaction` instances inside the main transaction so a failed created/demolished parameter pair does not leave a partially changed element.
- Most validation failures return a DTO with a boolean success flag set to false and an `error` string. Unexpected exceptions return a failed `RevitCommandResult`.
- DTOs contain simple JSON-safe values only: ids, names, booleans, counts, strings, arrays, and nested DTOs.

## Tool Specs

### `create_view_filter`

- Purpose: create a Revit `ParameterFilterElement` for one or more categories, optionally with parameter filter rules.
- Type: W.
- Handler file: `src/shared/Handlers/CreateViewFilterHandler.cs`.
- Expected parameters:
  - `name` string, required, nonblank.
  - `categories` array of category names or `BuiltInCategory` names, required, non-empty.
  - `rules` array, optional. Each rule supports `parameter_name`, `evaluator`, and `value`. Supported evaluators are `equals`, `not_equals`, `greater`, `less`, `contains`, `begins_with`, and `ends_with`.
- Response DTO summary:
  - `created`, `filter_id`, `name`, `category_count`, `rule_count`, `categories`, `unknown_categories`, `skipped_rules`, `rules`, `error`.
- Revit API strategy:
  - Resolve categories from `Document.Settings.Categories` and `BuiltInCategory`.
  - Resolve parameters from sample elements, built-in parameters, shared parameters, and project parameters where possible.
  - Convert numeric rule values from external units when the parameter spec is known: length to millimeters, area to m2, volume to m3, and angle to degrees.
  - Build rules via reflective `ParameterFilterRuleFactory` calls.
  - Create the filter with `ParameterFilterElement.Create`, using an `ElementFilter` overload where available and falling back to older rule-setting APIs where required.
- Transaction behavior:
  - One transaction named `Bimwright: create view filter`.
  - Rolls back on validation or rule-build failure before commit.
- Validation and failure modes:
  - Rejects blank names and empty category lists.
  - Reports unknown categories.
  - If rules were requested but no rule can be applied, returns `created=false` and does not create a match-all filter.
  - Reports skipped rule reasons in the DTO.
- Cross-version notes:
  - Uses reflective filter-rule construction and `RevitCompat` id handling for API drift across `R22` through `R27`.
  - Unit conversion depends on `Definition.GetDataType()` when available.
- Smoke/test expectations:
  - Create a category-only filter, list it, and apply it to a graphics-capable view.
  - Create a rule-based filter using a string and a numeric parameter.
  - Verify invalid rule input does not leave a broad match-all filter behind.

### `apply_filter_to_view`

- Purpose: add an existing parameter filter to a view and set its visibility.
- Type: W.
- Handler file: `src/shared/Handlers/ApplyFilterToViewHandler.cs`.
- Expected parameters:
  - `filter_id` integer, required.
  - `view_id` integer, optional. Defaults to the active view.
  - `visible` boolean, optional. Defaults to true.
- Response DTO summary:
  - `applied`, `filter_id`, `filter_name`, `view_id`, `view_name`, `already_applied`, `error`.
- Revit API strategy:
  - Resolve `ParameterFilterElement`.
  - Resolve the requested view or active view.
  - Check `AreGraphicsOverridesAllowed()`.
  - Use `View.GetFilters()`, `View.AddFilter()`, and `View.SetFilterVisibility()`.
- Transaction behavior:
  - One transaction for add/visibility update.
  - Existing filter membership is preserved; visibility is still updated.
- Validation and failure modes:
  - Fails if `filter_id` is out of Revit element-id range, not found, or not a `ParameterFilterElement`.
  - Fails if the target view cannot accept graphic overrides.
  - Returns `already_applied=true` when the filter was already on the view.
- Cross-version notes:
  - Uses `RevitCompat.CanRepresentElementId` before constructing ids.
- Smoke/test expectations:
  - Apply a known filter to the active plan view.
  - Re-run with the same filter and confirm `already_applied=true`.
  - Try a template or unsupported view and confirm a clean failure DTO.

### `set_filter_overrides`

- Purpose: set view-specific graphic overrides for a filter already applied to a view.
- Type: W.
- Handler file: `src/shared/Handlers/SetFilterOverridesHandler.cs`.
- Expected parameters:
  - `filter_id` integer, required.
  - `view_id` integer, optional. Defaults to the active view.
  - `projection_line_color` string, optional.
  - `surface_foreground_color` string, optional.
  - `cut_line_color` string, optional.
  - `transparency` integer 0 to 100, optional.
  - `halftone` boolean, optional.
  - `projection_line_weight` integer 1 to 16, optional.
- Response DTO summary:
  - `applied`, `filter_id`, `view_id`, `overrides_set`, `error`.
- Revit API strategy:
  - Resolve view and filter.
  - Verify the view supports overrides and the filter is applied to the view.
  - Start from `new OverrideGraphicSettings(view.GetFilterOverrides(filterId))` so unspecified settings are preserved.
  - Apply only supplied colors, transparency, halftone, and line weight.
  - For `surface_foreground_color`, also try to set a solid foreground fill pattern id so the color renders as a fill.
  - Commit through `View.SetFilterOverrides()`.
- Transaction behavior:
  - One transaction named for setting filter overrides.
  - Rolls back on exceptions.
- Validation and failure modes:
  - Rejects invalid ids, invalid RGB strings, transparency outside 0 to 100, and line weights outside 1 to 16.
  - Returns `applied=false` if the filter is not on the view or the view disallows overrides.
- Cross-version notes:
  - Solid fill pattern lookup is wrapped for tolerance across Revit versions and language differences.
  - Element id construction is range-checked through `RevitCompat`.
- Smoke/test expectations:
  - Apply a filter, set one override property, then set a second property and confirm the first property remains.
  - Verify surface foreground color renders with a solid fill where Revit requires the pattern id.
- Review lesson:
  - The fixed implementation must merge into existing `OverrideGraphicSettings`. Replacing the settings object would wipe unrelated filter override state.

### `list_view_filters`

- Purpose: list filter definitions or filters applied to a specific view.
- Type: R.
- Handler file: `src/shared/Handlers/ListViewFiltersHandler.cs`.
- Expected parameters:
  - `view_id` integer, optional.
  - `include_usage` boolean, optional. Used when listing all filter definitions.
- Response DTO summary:
  - With `view_id`: `view_id`, `view_name`, `total_filters`, `filters[]`.
  - Without `view_id`: `total_filters`, `include_usage`, `filters[]`.
  - Filter items include ids, names, category counts, categories, and view-specific visibility/enablement/override counts when available.
- Revit API strategy:
  - Collect `ParameterFilterElement` definitions from the document.
  - For a target view, use `View.GetFilters()`, `GetFilterVisibility()`, `GetIsFilterEnabled()` where available, and `GetFilterOverrides()`.
  - When `include_usage=true`, scan views and count which filters are applied to each view.
- Transaction behavior:
  - Read-only; no transaction.
- Validation and failure modes:
  - Fails if `view_id` is invalid, not found, or not a `View`.
  - Unsupported view filter APIs are caught and reported cleanly.
- Cross-version notes:
  - Some view/filter helper APIs vary by version; the handler wraps optional calls defensively.
- Smoke/test expectations:
  - List all filters.
  - List a known view's filters and confirm visibility and override counts match the UI.

### `remove_filter_from_view`

- Purpose: remove a filter from a view and optionally delete the filter definition when unused.
- Type: W.
- Handler file: `src/shared/Handlers/RemoveFilterFromViewHandler.cs`.
- Expected parameters:
  - `filter_id` integer, required.
  - `view_id` integer, optional. Defaults to the active view.
  - `delete_definition_if_unused` boolean, optional. Defaults to false.
- Response DTO summary:
  - `removed`, `filter_id`, `filter_name`, `view_id`, `view_name`, `definition_deleted`, `error`.
- Revit API strategy:
  - Resolve filter and target view.
  - Check `AreGraphicsOverridesAllowed()`.
  - Confirm the filter is applied before removal.
  - Use `View.RemoveFilter()`.
  - If deletion is requested, scan other non-template views for filter usage before calling `Document.Delete()`.
- Transaction behavior:
  - One transaction.
  - Rolls back when the target filter is not applied or when deletion fails.
- Validation and failure modes:
  - Rejects invalid ids and non-filter elements.
  - Returns a clean failure if the filter is not applied to the target view.
  - Does not delete the filter definition if any other view still uses it.
- Cross-version notes:
  - View scans skip unsupported/template views defensively.
- Smoke/test expectations:
  - Remove a filter from one view while it remains used elsewhere and confirm `definition_deleted=false`.
  - Remove from the last view with deletion requested and confirm the definition is gone.

### `override_element_graphics`

- Purpose: set per-element graphic overrides in a view.
- Type: W.
- Handler file: `src/shared/Handlers/OverrideElementGraphicsHandler.cs`.
- Expected parameters:
  - `element_ids` array of integers, required, non-empty.
  - `view_id` integer, optional. Defaults to the active view.
  - `projection_line_color` string, optional.
  - `surface_foreground_color` string, optional.
  - `cut_line_color` string, optional.
  - `transparency` integer 0 to 100, optional.
  - `halftone` boolean, optional.
  - `projection_line_weight` integer 1 to 16, optional.
- Response DTO summary:
  - `overridden`, `view_id`, `view_name`, `element_count`, `succeeded`, `failed[]`, `overrides_set[]`, `error`.
- Revit API strategy:
  - Resolve target view and validate graphics override support.
  - Resolve each element id.
  - For each element, read existing settings through `View.GetElementOverrides(elementId)`.
  - Merge supplied override properties into the existing settings and call `View.SetElementOverrides()`.
  - Try to attach a solid foreground fill pattern when `surface_foreground_color` is supplied.
- Transaction behavior:
  - One transaction around the batch.
  - Per-element failures are reported in `failed[]` without requiring all ids to succeed.
- Validation and failure modes:
  - Rejects empty element list, invalid colors, out-of-range transparency, and out-of-range line weights.
  - Reports invalid/missing elements by id.
  - Fails the whole call if the target view disallows overrides.
- Cross-version notes:
  - Uses `RevitCompat` for id range checks and guarded fill-pattern calls for API differences.
- Smoke/test expectations:
  - Apply a transparency override, then apply a line-color override to the same element and confirm transparency remains.
  - Include one invalid id and confirm the valid elements still report success.
- Review lesson:
  - The fixed implementation must merge with existing per-element overrides. Starting from a fresh `OverrideGraphicSettings` would erase unrelated existing settings.

### `clear_element_overrides`

- Purpose: clear per-element graphic overrides in a view.
- Type: W.
- Handler file: `src/shared/Handlers/ClearElementOverridesHandler.cs`.
- Expected parameters:
  - `element_ids` array of integers, optional. If omitted, the handler scans visible elements in the target view and clears those with detected overrides.
  - `view_id` integer, optional. Defaults to the active view.
- Response DTO summary:
  - `cleared`, `view_id`, `view_name`, `element_count`, `succeeded`, `failed[]`, `error`.
- Revit API strategy:
  - Resolve target view and validate graphics override support.
  - If ids are supplied, resolve those elements.
  - If ids are omitted, collect non-type elements visible in the view and detect override state through `View.GetElementOverrides()`.
  - Clear by calling `View.SetElementOverrides(elementId, new OverrideGraphicSettings())`.
- Transaction behavior:
  - One transaction around the clear operation.
- Validation and failure modes:
  - Reports invalid supplied ids in `failed[]`.
  - Fails the whole call if the target view disallows overrides.
  - The auto-scan path only clears elements where the handler detects a non-default override.
- Cross-version notes:
  - Override detection is defensive because some `OverrideGraphicSettings` property getters can throw or vary by version.
- Smoke/test expectations:
  - Override two elements, clear one by id, and confirm only that element resets.
  - Omit `element_ids` and confirm all detected overridden elements in the view reset.
- Review lesson:
  - The detection logic must include colors, line weights, transparency, halftone, detail level, pattern ids, and pattern visibility, not just a small subset of properties.

### `get_view_visibility`

- Purpose: read a view's visibility and graphics state.
- Type: R.
- Handler file: `src/shared/Handlers/GetViewVisibilityHandler.cs`.
- Expected parameters:
  - `view_id` integer, optional. Defaults to the active view.
  - `include_category_list` boolean, optional. Defaults to false.
- Response DTO summary:
  - `view_id`, `view_name`, `view_type`, `detail_level`, `discipline`, `scale`, `graphics_overrides_allowed`, `view_template_id`, `view_template_name`, `applied_filter_count`, `applied_filters[]`, `hidden_category_count`, `hidden_categories[]`, optional `categories[]`.
- Revit API strategy:
  - Resolve target view.
  - Read view metadata, template assignment, filters, hidden categories, and optional category visibility list.
  - Uses Revit view/category/filter getters only.
- Transaction behavior:
  - Read-only; no transaction.
- Validation and failure modes:
  - Fails if `view_id` is invalid, not found, or not a view.
  - Category reads are guarded so unsupported categories do not fail the entire call.
- Cross-version notes:
  - Optional category and filter getters are wrapped where needed for supported Revit versions.
- Smoke/test expectations:
  - Run against active plan, 3D, and sheet views and compare key fields against Revit UI.
  - Toggle one category hidden and verify it appears in the returned hidden category list.

### `set_category_visibility`

- Purpose: hide or show one or more categories in a view.
- Type: W.
- Handler file: `src/shared/Handlers/SetCategoryVisibilityHandler.cs`.
- Expected parameters:
  - `categories` array of category names or `BuiltInCategory` names, required, non-empty.
  - `hidden` boolean, required.
  - `view_id` integer, optional. Defaults to the active view.
- Response DTO summary:
  - `updated`, `view_id`, `view_name`, `hidden`, `succeeded[]`, `failed[]`, `error`.
- Revit API strategy:
  - Resolve categories from document categories and built-in category names.
  - Verify each category allows visibility control in the target view.
  - Call `View.SetCategoryHidden(category.Id, hidden)` per category.
- Transaction behavior:
  - One transaction around the batch.
  - Per-category failures are accumulated in `failed[]`.
- Validation and failure modes:
  - Rejects empty category lists and invalid `view_id`.
  - Reports unknown categories and categories that do not allow visibility control.
  - View template control can still block a category even if the category exists.
- Cross-version notes:
  - Category visibility support varies by view type and category; the handler checks support at runtime.
- Smoke/test expectations:
  - Hide a visible model category in a plan view, then show it again.
  - Include one unknown category and verify it is reported without hiding valid categories incorrectly.

### `list_phases`

- Purpose: list project phases and phase filters.
- Type: R.
- Handler file: `src/shared/Handlers/ListPhasesHandler.cs`.
- Expected parameters:
  - None.
- Response DTO summary:
  - `doc_title`, `total_phases`, `phases[]`, `total_phase_filters`, `phase_filters[]`.
  - Phase items include id, name, and sequence/order information. Phase filter items include id and name.
- Revit API strategy:
  - Read `Document.Phases`.
  - Collect `PhaseFilter` elements through `FilteredElementCollector`.
- Transaction behavior:
  - Read-only; no transaction.
- Validation and failure modes:
  - Blank or missing JSON parameters are accepted.
  - Unexpected collector failures return a failed command result.
- Cross-version notes:
  - Uses stable phase and phase-filter API surfaces across supported versions.
- Smoke/test expectations:
  - Compare phase order and phase-filter names with Manage > Phases in Revit.

### `set_view_phase`

- Purpose: set a view's phase and/or phase filter.
- Type: W.
- Handler file: `src/shared/Handlers/SetViewPhaseHandler.cs`.
- Expected parameters:
  - `view_id` integer, optional. Defaults to the active view.
  - `phase_id` integer, optional.
  - `phase_name` string, optional.
  - `phase_filter_id` integer, optional.
  - `phase_filter_name` string, optional.
  - At least one phase or phase-filter selector is required.
- Response DTO summary:
  - `updated`, `view_id`, `view_name`, `phase`, `phase_filter`, `error`.
- Revit API strategy:
  - Resolve target view.
  - Resolve phase by id or exact name.
  - Resolve phase filter by id or exact name.
  - Set `BuiltInParameter.VIEW_PHASE` and/or `BuiltInParameter.VIEW_PHASE_FILTER` through view parameters.
- Transaction behavior:
  - One transaction.
  - Rolls back if a requested parameter is missing or read-only.
- Validation and failure modes:
  - Fails if no phase or phase filter is requested.
  - Fails if names are ambiguous or not found, ids are invalid, or target parameters are read-only.
- Cross-version notes:
  - Uses stable built-in parameter ids with `RevitCompat` element-id conversion.
- Smoke/test expectations:
  - Set phase by name, then by id.
  - Set phase filter by name and confirm the UI updates.
  - Try a schedule/sheet/template that does not support the parameters and confirm clean failure.

### `set_element_phase`

- Purpose: set created and/or demolished phase values for elements.
- Type: W.
- Handler file: `src/shared/Handlers/SetElementPhaseHandler.cs`.
- Expected parameters:
  - `element_ids` array of integers, required, non-empty.
  - `phase_created_id` integer, optional.
  - `phase_created_name` string, optional.
  - `phase_demolished_id` integer, optional. `-1` clears demolished phase.
  - `phase_demolished_name` string, optional. `None` clears demolished phase.
  - At least one created or demolished phase selector is required.
- Response DTO summary:
  - `updated`, `element_count`, `succeeded`, `failed[]`, `phase_created`, `phase_demolished`, `error`.
- Revit API strategy:
  - Resolve requested phases by id or exact name.
  - Resolve each element id.
  - Set `BuiltInParameter.PHASE_CREATED` and/or `BuiltInParameter.PHASE_DEMOLISHED` on each element.
  - Use invalid element id semantics to clear demolished phase where supported.
- Transaction behavior:
  - One outer transaction for the batch.
  - One `SubTransaction` per element so paired created/demolished updates are atomic per element.
- Validation and failure modes:
  - Rejects empty element list and missing phase selectors.
  - Reports invalid ids, missing elements, missing phase parameters, read-only phase parameters, and incompatible phase order rules in `failed[]`.
  - Valid elements can still succeed when other elements fail.
- Cross-version notes:
  - Built-in phase parameters are stable, but element support is category and family dependent.
  - Uses `RevitCompat` range checks for ids.
- Smoke/test expectations:
  - Set created phase on several model elements and verify all return success.
  - Clear demolished phase with `phase_demolished_name="None"`.
  - Include one element whose phase parameter is read-only and verify it is isolated in `failed[]`.
- Review lesson:
  - The per-element `SubTransaction` is intentional. Without it, setting created phase could succeed while demolished phase fails on the same element, leaving partial state.

## Wiring

Server:

- `src/server/Program.cs` registers `GraphicsTools` under the `graphics` toolset.
- Each MCP wrapper calls `ToolGateway.SendToRevit()` with the command name used by the dispatcher.
- Parameter names are converted from C# wrapper names to snake_case JSON payload names.

Toolset filtering:

- `src/server/ToolsetFilter.cs` lists `graphics` in known toolsets, default-on toolsets, and write-capable toolsets.

Revit dispatcher:

- `src/shared/Infrastructure/CommandDispatcher.cs` registers:
  - `create_view_filter` -> `CreateViewFilterHandler`
  - `apply_filter_to_view` -> `ApplyFilterToViewHandler`
  - `set_filter_overrides` -> `SetFilterOverridesHandler`
  - `list_view_filters` -> `ListViewFiltersHandler`
  - `remove_filter_from_view` -> `RemoveFilterFromViewHandler`
  - `override_element_graphics` -> `OverrideElementGraphicsHandler`
  - `clear_element_overrides` -> `ClearElementOverridesHandler`
  - `get_view_visibility` -> `GetViewVisibilityHandler`
  - `set_category_visibility` -> `SetCategoryVisibilityHandler`
  - `list_phases` -> `ListPhasesHandler`
  - `set_view_phase` -> `SetViewPhaseHandler`
  - `set_element_phase` -> `SetElementPhaseHandler`

## Acceptance Criteria

- All 12 graphics commands are visible when the `graphics` toolset is enabled.
- The dispatcher has exactly one concrete handler registration for each command.
- Read-only commands perform no Revit transaction.
- Write commands use Revit transactions and return DTOs with explicit success flags and errors.
- Optional `view_id` consistently falls back to the active view for view-scoped commands.
- Invalid ids are range-checked before `ElementId` construction.
- Unsupported views fail cleanly before attempting graphics writes.
- Filter and element override tools preserve existing unspecified override properties.
- Surface foreground colors are paired with a solid fill pattern where required.
- Category visibility and phase tools report per-item failures instead of hiding partial work in a generic exception.
- `create_view_filter` never silently creates a match-all filter when the caller supplied rules that could not be applied.

## Review Checklist

- Confirm `Program.cs` wrapper command names match `CommandDispatcher` registrations.
- Confirm all DTOs are primitive JSON-safe shapes and do not expose raw Revit API objects.
- Confirm every write handler opens a transaction only after validation has enough context to avoid known bad writes.
- Recheck override merge behavior for both `set_filter_overrides` and `override_element_graphics`.
- Recheck that `surface_foreground_color` attempts solid fill pattern assignment.
- Recheck guarded JSON parsing for blank or null payloads.
- Recheck unit-aware numeric filter rules for length, area, volume, and angle specs.
- Recheck that `set_element_phase` keeps created/demolished updates atomic per element.
- Recheck unsupported view/template behavior for graphics write tools.
- Run live Revit smoke tests on a disposable model before marking a release candidate.

## Known Risks

- The docs task did not run `dotnet build` and did not run live Revit smoke tests.
- `create_view_filter` parses the `rules` string in `Program.cs` before dispatch. Invalid JSON can fail in the server wrapper before the handler DTO shape is returned.
- Parameter filter rule construction is limited by parameter discoverability and Revit's accepted rule types for each parameter.
- Unit-aware rule conversion depends on Revit parameter spec metadata; unknown double specs may be treated as raw internal values or skipped depending on handler logic.
- Category visibility and phase mutability are highly view/category/element dependent in Revit. A valid id can still fail because the target object is read-only, template-controlled, or unsupported by that Revit element type.

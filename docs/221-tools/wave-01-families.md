# Wave 01 - Families As-Built Spec

## Status

Done in the current codebase. The `families` toolset is implemented as a default-on, write-capable MCP toolset.

Source of truth for this spec:

- MCP wrappers: `src/server/Program.cs`, `FamiliesTools`
- Dispatcher registration: `src/shared/Infrastructure/CommandDispatcher.cs`, Phase 13
- Handlers: `src/shared/Handlers/*Family*Handler.cs` and `src/shared/Handlers/*Families*Handler.cs`
- Review lessons: `runs/families-handlers-review/output-01.txt` and `output-02.txt`
- Format contract: `docs/221-tools/README.md`

This file is an as-built spec for Waves 1-4 style documentation, not a target implementation plan.

## Scope

Wave 01 exposes ten family-management tools:

| Tool | Type | Handler |
|---|---|---|
| `list_loaded_families` | R | `ListLoadedFamiliesHandler.cs` |
| `load_family_from_path` | W | `LoadFamilyFromPathHandler.cs` |
| `unload_family` | W | `UnloadFamilyHandler.cs` |
| `duplicate_family_type` | W | `DuplicateFamilyTypeHandler.cs` |
| `rename_family_type` | W | `RenameFamilyTypeHandler.cs` |
| `audit_families` | R | `AuditFamiliesHandler.cs` |
| `replace_family_type` | W | `ReplaceFamilyTypeHandler.cs` |
| `get_family_instances` | R | `GetFamilyInstancesHandler.cs` |
| `list_family_types_in_family` | R | `ListFamilyTypesInFamilyHandler.cs` |
| `export_family_to_path` | W | `ExportFamilyToPathHandler.cs` |

## Dependencies

- Revit API family classes: `Family`, `FamilySymbol`, `ElementType`, `FamilyInstance`, `IFamilyLoadOptions`, `Document.EditFamily`, `SaveAsOptions`.
- System-family type classes used for listing: `WallType`, `FloorType`, `CeilingType`, `RoofType`, `PipeType`, `DuctType`, `ConduitType`, `CableTrayType`.
- Shared command infrastructure: `IRevitCommand`, `CommandResult`, `CommandDispatcher`, `ToolGateway.SendToRevit`.
- Cross-version ID helpers: `RevitCompat.ToElementId(long)`, `RevitCompat.GetId(...)`, `RevitCompat.CanRepresentElementId(long)`.
- JSON: `Newtonsoft.Json.Linq`.
- Unit convention: external metric values use mm, m2, m3, and degrees; Revit stores feet, square feet, cubic feet, and radians.

## Toolset Contract

- Toolset name: `families`.
- MCP wrapper class: `FamiliesTools` in `src/server/Program.cs`.
- Enabled by default: yes, through `ToolsetFilter.DefaultOn`.
- Write-capable: yes, through `ToolsetFilter.WriteCapable`.
- Dispatcher registrations: Phase 13 in `CommandDispatcher`.
- Public wrappers use C#-style parameter names and send snake_case JSON payloads to handlers.
- Handler return values are DTOs only. No raw Revit objects are serialized.
- Read tools open no Revit transaction. Write tools either open a Revit `Transaction` or, for family export, open a background family document and write an `.rfa` file.

## Tool Specs

### `list_loaded_families`

- Purpose: list loadable, in-place, and selected system-family groups in the active document.
- Type: R.
- Handler file: `src/shared/Handlers/ListLoadedFamiliesHandler.cs`.
- Expected parameters: `category_filter` optional string; `kind_filter` optional `all|system|loadable|inplace`, default `all`; `include_instance_count` optional bool, default `false`; `limit` optional int clamped to `1..10000`, default `1000`.
- Response DTO summary: `doc_title`, `total_families`, `returned_families`, `limit_hit`, and `families[]` with `id`, `name`, `category`, `kind`, `type_count`, `instance_count`, `is_editable`.
- Revit API strategy: collect `Family` elements for loadable/in-place families; synthesize system-family rows from known `ElementType` classes grouped by category and `FamilyName`; optionally count placed instances by matching symbol/type IDs.
- Transaction behavior: none.
- Validation/failure modes: fails on no open document or malformed JSON; clamps limit; unknown `kind_filter` values are not explicitly rejected and produce no matching branch.
- Cross-version notes: all element IDs pass through `RevitCompat`; system-family IDs are synthetic strings prefixed with `system:`.
- Smoke/test expectations: run with default options, with a category substring such as `Door`, with each `kind_filter`, and with `include_instance_count=true` on a small model.

### `load_family_from_path`

- Purpose: load an `.rfa` family file into the active model.
- Type: W.
- Handler file: `src/shared/Handlers/LoadFamilyFromPathHandler.cs`.
- Expected parameters: `path` required string; `overwrite_existing` optional bool, default `true`; `overwrite_parameter_values` optional bool, default `false`.
- Response DTO summary: on success, `loaded=true`, `family_id`, `family_name`, `category`, `kind="loadable"`, `symbol_ids`, `symbol_names`, `was_overwrite`, `warnings`; expected soft failures return `loaded=false` with `error`.
- Revit API strategy: validate file existence and `.rfa` extension, call `Document.LoadFamily(path, IFamilyLoadOptions, out Family)`, enumerate symbol IDs from the returned family.
- Transaction behavior: one transaction named `Bimwright: load family`; rollback on `LoadFamily` exception, `false` return, or null family; commit before returning success.
- Validation/failure modes: no document, invalid JSON, missing path, missing file, non-`.rfa` extension, Revit refusal, null returned `Family`, transaction failure. Review fixed the earlier no-op false-load path so `loaded=true` is only returned when Revit returned true and supplied a family.
- Cross-version notes: uses `IFamilyLoadOptions`, `Family.GetFamilySymbolIds`, `RevitCompat.GetId`; includes reflection fallback for older/unusual symbol enumeration.
- Smoke/test expectations: load a known sample `.rfa`, reload it with overwrite flags, verify returned symbol IDs exist, and verify invalid path and wrong extension return clean DTOs.

### `unload_family`

- Purpose: delete a loadable family from the document, optionally deleting placed instances first.
- Type: W, destructive.
- Handler file: `src/shared/Handlers/UnloadFamilyHandler.cs`.
- Expected parameters: public MCP wrapper sends `family_id` or `family_name`, `cascade_delete_instances`, and `dry_run`; handler schema also supports `allow_inplace`, default `false`, but the current `Program.cs` wrapper does not expose it.
- Response DTO summary: `unloaded`, `family_id`, `family_name`, `instances_deleted`, `types_removed`, `dry_run`, `warning`, `error`.
- Revit API strategy: resolve a `Family` by ID or unique name, reject system families, reject in-place families unless `allow_inplace=true`, collect family symbol IDs, count placed `FamilyInstance` elements, optionally delete instances and then `doc.Delete(family.Id)`.
- Transaction behavior: no transaction for validation, cascade refusal, or dry-run; one transaction named `Bimwright: unload family` for actual deletion; rolls back if instance or family deletion fails.
- Validation/failure modes: no document, bad JSON, missing or invalid ID/name, ambiguous name, non-family element, system family, in-place family, placed instances without cascade, delete failure, non-committed transaction.
- Cross-version notes: uses `RevitCompat` for every ID conversion and family symbol ID comparison.
- Smoke/test expectations: dry-run an unused loadable family, attempt unload with instances and `cascade_delete_instances=false`, unload a copied unused family, and verify in-place families are blocked through the public wrapper.

### `duplicate_family_type`

- Purpose: duplicate a loadable `FamilySymbol` or system `ElementType`, with optional type parameter overrides.
- Type: W.
- Handler file: `src/shared/Handlers/DuplicateFamilyTypeHandler.cs`.
- Expected parameters: `source_type_id` required string or integer ElementId; `new_type_name` required string; `type_parameter_overrides` optional object map of parameter display name to value.
- Response DTO summary: `duplicated`, `new_type_id`, `new_type_name`, `family_name`, `category`, `parameters_set` map, `error`.
- Revit API strategy: resolve source as `ElementType`, pre-check sibling name conflicts, call `ElementType.Duplicate(newTypeName)`, apply parameter overrides based on `StorageType`.
- Transaction behavior: one transaction named `Bimwright: duplicate type`; rolls back on duplicate failure or unhandled exception; returns structured soft-error DTOs for expected failures.
- Validation/failure modes: missing/bad ID, out-of-range ID, missing/blank name, non-object overrides, missing source type, duplicate type name, read-only/missing/unsupported parameters, Revit duplicate/commit failure.
- Cross-version notes: `RevitCompat` handles IDs; double parameter overrides convert length mm to feet, area m2 to square feet, volume m3 to cubic feet, and angle degrees to radians. Review fixed the earlier area/volume conversion bug.
- Smoke/test expectations: duplicate a loadable symbol, duplicate a system type, apply string/integer/double overrides, verify duplicate-name requests return `duplicated=false` without throwing.

### `rename_family_type`

- Purpose: rename a loadable or system family type.
- Type: W.
- Handler file: `src/shared/Handlers/RenameFamilyTypeHandler.cs`.
- Expected parameters: `type_id` required string or integer ElementId; `new_name` required string.
- Response DTO summary: `renamed`, `type_id`, `old_name`, `new_name`, `family_name`, `category`, `error`.
- Revit API strategy: resolve `ElementType`, compare current name, assign `type.Name = newName`.
- Transaction behavior: no transaction when the name is unchanged; otherwise one transaction named `Bimwright: rename type`; rollback on Revit `ArgumentException`, `InvalidOperationException`, or general exception.
- Validation/failure modes: no document, malformed JSON, missing/bad/out-of-range ID, missing/blank name, non-`ElementType`, duplicate or invalid Revit type name, non-committed transaction.
- Cross-version notes: uses `RevitCompat.ToElementId` and `GetId`; works on `FamilySymbol` and other `ElementType` subclasses.
- Smoke/test expectations: rename a copied type, rename back, test unchanged-name behavior, and test duplicate-name rejection.

### `audit_families`

- Purpose: read-only family quality audit for unused, in-place, duplicate-name, and high-type-count families.
- Type: R.
- Handler file: `src/shared/Handlers/AuditFamiliesHandler.cs`.
- Expected parameters: `include_unused`, `include_inplace`, `include_duplicate_names`, `include_high_type_count` optional bools, all default `true`; `high_type_count_threshold` optional int, default `20`, min `1`.
- Response DTO summary: `doc_title`, `total_families_scanned`, `unused_families`, `inplace_families`, `duplicate_name_groups`, `high_type_count_families`, `recommendations`.
- Revit API strategy: collect `Family` elements only, precompute symbol-to-family and category-to-family maps, count family instances by category, then build issue buckets and recommendations.
- Transaction behavior: none.
- Validation/failure modes: no document, malformed JSON; individual family/instance introspection failures are skipped.
- Cross-version notes: all ID math goes through `RevitCompat`; system families are excluded because they are not normally represented by `Family`.
- Smoke/test expectations: run in a sample model with at least one unused family, one in-place family if available, and a low high-type threshold to exercise all buckets.

### `replace_family_type`

- Purpose: replace instances of one type with another type across all model elements, the active view, or current selection.
- Type: W.
- Handler file: `src/shared/Handlers/ReplaceFamilyTypeHandler.cs`.
- Expected parameters: `from_type_id` required string/integer ElementId; `to_type_id` required string/integer ElementId; `scope` optional `all|active_view|selection`, default `all`; `view_id` optional when scope is `active_view`; `dry_run` optional bool, default `false`.
- Response DTO summary: `replaced`, `from_type_id`, `from_type_name`, `to_type_id`, `to_type_name`, `category`, `instance_count`, `successfully_changed`, `errors[]`, `dry_run`, `scope`, optional `note`.
- Revit API strategy: resolve both IDs as `ElementType`, require matching categories, collect matching instances by scope, activate target `FamilySymbol` if needed, call `Element.IsValidType(toTypeId)` and `ChangeTypeId(toTypeId)` per instance.
- Transaction behavior: none for dry-run or zero-match non-dry-run; otherwise one transaction named `Bimwright: replace type`. Review fixed the earlier zero-match mutation risk by returning before symbol activation.
- Validation/failure modes: malformed JSON, invalid/out-of-range IDs, non-`ElementType`, category mismatch, invalid scope, missing active view/selection/view ID, target activation failure, per-instance host incompatibility, commit failure.
- Cross-version notes: uses `RevitCompat` for all IDs, including active-view and selected-element comparisons.
- Smoke/test expectations: dry-run first, replace within a controlled selection, verify host-incompatible cases land in `errors[]`, and verify zero matches do not activate the target symbol.

### `get_family_instances`

- Purpose: list placed instances of a family, optionally narrowed to one type and/or active view.
- Type: R.
- Handler file: `src/shared/Handlers/GetFamilyInstancesHandler.cs`.
- Expected parameters: `family_id` or `family_name` required; `type_name` optional string; `view_only` optional bool, default `false`; `limit` optional int, default `1000`.
- Response DTO summary: `family_id`, `family_name`, `category`, `type_filter`, `view_only`, `total_instances`, `returned`, `limit_hit`, and `instances[]` with `id`, `type_id`, `type_name`, `level_id`, `level_name`, `location_kind`, `location_point_mm`, `location_line_start_mm`, `location_line_end_mm`, `host_id`, `host_name`, `mark`.
- Revit API strategy: resolve family by ID or unique name, enumerate symbol IDs, collect `FamilyInstance` elements globally or in the active view, match by symbol ID, convert point/curve locations to mm.
- Transaction behavior: none.
- Validation/failure modes: no document, malformed JSON, missing ID/name, invalid/out-of-range ID, non-family ID, ambiguous family name, family type enumeration failure, missing `type_name`, no active view when `view_only=true`.
- Cross-version notes: ID handling via `RevitCompat`; location unit conversion is feet to mm.
- Smoke/test expectations: query by ID and name, filter by type, compare global vs active-view counts, and verify hosted instances include host fields where available.

### `list_family_types_in_family`

- Purpose: deep-list all types in a single family, optionally including type parameter values.
- Type: R.
- Handler file: `src/shared/Handlers/ListFamilyTypesInFamilyHandler.cs`.
- Expected parameters: `family_id` or `family_name` required; `include_parameter_values` optional bool, default `true`; `include_built_in_only` optional bool, default `false`.
- Response DTO summary: `family_id`, `family_name`, `category`, `kind`, `is_editable`, and `types[]` with `id`, `name`, `is_active`, `parameters` map. Each parameter has `storage_type`, `value`, and optional `unit`.
- Revit API strategy: resolve `Family`, enumerate symbol IDs, inspect each `FamilySymbol.Parameters`, avoid duplicate display-name keys, convert values by storage type, and resolve `ElementId` parameters to referenced element names when possible.
- Transaction behavior: none.
- Validation/failure modes: no document, malformed JSON, missing ID/name, invalid/out-of-range ID, no family for ID/name, ambiguous family name; unreadable parameters are skipped.
- Cross-version notes: unit detection uses `Definition.GetDataType()` with reflection fallback; doubles convert from internal units to mm, m2, m3, and degrees.
- Smoke/test expectations: run with and without parameter values, compare built-in-only output, and verify length/area/volume/angle parameter values are metric.

### `export_family_to_path`

- Purpose: save a loadable family from the project back to an `.rfa` file.
- Type: W, file-system write.
- Handler file: `src/shared/Handlers/ExportFamilyToPathHandler.cs`.
- Expected parameters: `output_path` required absolute `.rfa` path; `family_id` or `family_name` required; `overwrite_existing` optional bool, default `false`.
- Response DTO summary: `exported`, `family_id`, `family_name`, `output_path`, `file_size_bytes`, `error`.
- Revit API strategy: validate output path and family identity, reject in-place/system families, open a background family document with `Document.EditFamily(family)`, call `familyDoc.SaveAs(outputPath, SaveAsOptions)`, then close the background document in `finally`.
- Transaction behavior: no active model transaction; writes to disk and opens/closes a separate family document.
- Validation/failure modes: malformed JSON, missing or relative output path, wrong extension, missing parent directory, existing file with overwrite disabled, missing/ambiguous family, in-place family, system family, `EditFamily` failure, `SaveAs` failure.
- Cross-version notes: uses `RevitCompat` for IDs; `SaveAsOptions.OverwriteExistingFile=true` is only reached after the handler's overwrite policy check.
- Smoke/test expectations: export a small loadable family to a temp absolute path, verify file size, verify overwrite disabled fails on second call, and verify in-place/system families are rejected.

## Wiring

- `Program.cs` defines `[McpServerToolType, Toolset("families")] public class FamiliesTools`.
- `CommandDispatcher` registers all ten handlers in the Phase 13 block.
- `ToolsetFilter.KnownToolsets`, `DefaultOn`, and `WriteCapable` include `families`.
- MCP wrapper methods serialize results with `JsonConvert.SerializeObject(result, Formatting.Indented)`.
- Public parameter names are camelCase in wrappers and snake_case in handler JSON.

## Acceptance Criteria

- All ten tools are visible when default toolsets are enabled.
- Read-only tools do not start Revit transactions.
- Model-mutating tools use one bounded transaction and roll back on failure.
- `export_family_to_path` is treated as write-capable because it writes an `.rfa` to disk.
- All returned payloads are DTOs and contain no raw Revit API objects.
- ElementId input/output is cross-version safe through `RevitCompat`.
- Family type parameter units follow the metric contract.

## Review Checklist

- Confirm `Program.cs` wrapper names still match handler command names.
- Confirm `CommandDispatcher` still registers every handler exactly once.
- Re-check the public `unload_family` wrapper if in-place unload support is required; the handler has `allow_inplace`, but the wrapper does not expose it.
- Re-check malformed JSON behavior on all handlers after future edits.
- For destructive calls, smoke with `dry_run` or disposable model content first.
- Verify no handler serializes a `Family`, `FamilySymbol`, `ElementType`, `Element`, `Document`, or `Parameter` object directly.

## Known Risks

- `UnloadFamilyHandler` supports `allow_inplace`, but `FamiliesTools.UnloadFamily` does not expose or forward it. Through the public MCP wrapper, in-place family unload remains blocked.
- `load_family_from_path` validates file existence and extension but does not require `path` to be absolute.
- Several handlers intentionally return structured `Ok` DTOs with an `error` field for expected Revit refusal cases instead of `CommandResult.Fail`; clients must inspect the tool-specific success flag.
- System-family support is partial by design: listing and type operations support selected system type classes, but unload/export reject system families.

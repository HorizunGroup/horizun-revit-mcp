# Wave 04 - Print And Export As-Built Spec

## Status

Done in the current codebase as of 2026-05-21.

- Toolset: `export`
- Registration surface: default-on and write-capable.
- Tool count in the as-built `export` toolset: 16.
- New wave 04 export/print tools: 15.
- Pre-existing baseline tool in the same toolset: `export_room_data`.
- Server wrappers: `src/server/Program.cs`, `ExportTools`.
- Dispatcher registrations: `src/shared/Infrastructure/CommandDispatcher.cs`, phase 16 export registrations plus the earlier room export registration.
- Handler location: `src/shared/Handlers/*Export*.cs`, `src/shared/Handlers/*Print*.cs`, and `src/shared/Handlers/CreateViewSheetSetHandler.cs`.
- Review evidence: `runs/export-handlers-review/output-01.txt` found 3 blockers and 9 major issues; `runs/export-handlers-review/output-02.txt` confirmed the fixes were present. No build or live Revit smoke run is claimed by this spec.

## Scope

This wave exposes file export, print-setting discovery, and view/sheet-set creation tools for the active Revit document.

Included tools:

- `export_room_data`
- `export_pdf`
- `export_dwg`
- `export_dgn`
- `export_dwf`
- `export_ifc`
- `export_nwc`
- `export_fbx`
- `export_gbxml`
- `export_image`
- `export_schedule_csv`
- `export_elements_data`
- `batch_export_sheets`
- `list_export_settings`
- `create_view_sheet_set`
- `get_print_settings`

Out of scope:

- Creating or editing named DWG, IFC, DWF, PDF, or print setup presets except for view/sheet sets.
- Creating Navisworks exporter support when the exporter is not installed.
- Guaranteeing export file names chosen internally by Revit beyond the handler's expected-path and timestamp-aware output detection.
- Publishing to cloud storage or network destinations beyond normal filesystem paths visible to Revit.

## Dependencies

- Revit API shell targets are `R22` through `R27`.
- The Revit add-in must have an open document.
- `Program.cs` maps MCP tool parameters to snake_case command payloads and sends them through `ToolGateway.SendToRevit`.
- `CommandDispatcher` maps command names to one handler instance per tool.
- Output folders must already exist unless a handler explicitly writes a file path whose parent folder exists.
- File exports require the Revit process to have filesystem permission to the target path.
- `export_pdf` depends on Revit's `PDFExportOptions`, which is available in the supported Revit range for this toolset.
- `export_nwc` depends on Navisworks export support being installed and available to Revit.
- `export_fbx` requires a valid non-template `View3D`.
- Schedule export requires a `ViewSchedule`.
- `create_view_sheet_set` writes to Revit print/view sheet set state through `PrintManager`.

## Toolset Contract

`export` is a default-on write-capable toolset in `ToolsetFilter`.

Server-side contract:

- `ExportTools` in `Program.cs` defines the MCP surface and parameter names.
- Optional `viewIds` and `sheetIds` are arrays of numeric element ids.
- Empty optional filename and setting-name strings are sent as empty strings unless the wrapper normalizes them for a handler.
- Export paths are sent as caller-provided strings; path validation is handled in the Revit-side handlers.

Handler-side contract:

- Most export tools do not start Revit transactions because they invoke Revit export APIs or write external files.
- `create_view_sheet_set` is the main Revit document/print-state write in this wave and uses a transaction.
- Read tools do not start transactions.
- Handlers validate rooted paths, existing folders, safe bare filenames, view/sheet ids, and format values before exporting where applicable.
- File-output DTOs report detected output paths only when those files are actually observed or exact expected paths exist.
- Timestamp-aware file detection is used in handlers where Revit can choose output names. The fixed behavior avoids reporting stale files from the target directory as if they were newly exported.

## Tool Specs

### `export_room_data`

- Purpose: export room metadata from the active document as structured DTO data.
- Type: R.
- Handler file: `src/shared/Handlers/ExportRoomDataHandler.cs`.
- Expected parameters:
  - None.
- Response DTO summary:
  - `projectName`, `totalRooms`, `totalAreaMsq`, `rooms[]`.
  - Room items include id/name/number and common room metrics such as area, perimeter, level, department, and volume when available.
- Revit API strategy:
  - Collect rooms with `FilteredElementCollector`, `BuiltInCategory.OST_Rooms`, and `WhereElementIsNotElementType()`.
  - Read room parameters and convert display values into DTO fields.
- Transaction behavior:
  - Read-only; no transaction.
- Validation and failure modes:
  - Empty room sets return a successful DTO with zero counts.
  - Unexpected collector or parameter failures return a failed command result.
- Cross-version notes:
  - Uses stable room category and parameter APIs.
- Smoke/test expectations:
  - Run against a model with placed rooms and compare count, numbers, and area totals against a Revit room schedule.

### `export_pdf`

- Purpose: export one or more views/sheets to PDF.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportPdfHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `view_ids` array of integers, optional. Defaults to the active view.
  - `combine` boolean, optional. Defaults to false.
  - `file_name` string, optional. Used for combined output where Revit supports it.
- Response DTO summary:
  - `exported`, `output_folder`, `file_count`, `files[]`, `note`, `error`.
- Revit API strategy:
  - Resolve views by id or use active view.
  - Reject templates and invalid ids.
  - Snapshot existing PDF files and modified times before export.
  - Configure `PDFExportOptions`, including `Combine` and optional `FileName`.
  - Call `Document.Export(outputFolder, viewIds, options)`.
  - Detect new/updated PDFs by timestamp and exact expected combined filename where possible.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on missing/rootless/nonexistent output folder.
  - Fails on invalid view ids, missing views, and view templates.
  - Rejects unsafe `file_name` values with path separators, rooted paths, or `..`.
  - If Revit reports success but no output file is detected, returns a note instead of stale file paths.
- Cross-version notes:
  - Uses `PDFExportOptions`, available in the supported Revit API band.
- Smoke/test expectations:
  - Export active sheet/view to a temp folder and confirm one fresh PDF.
  - Export multiple sheets with `combine=true` and a safe filename.
  - Try an unsafe filename and confirm validation fails before export.
- Review lesson:
  - Secondary names such as `file_name` must be safe bare filenames, and output reporting must be based on exact or timestamp-aware detection.

### `export_dwg`

- Purpose: export one or more views/sheets to DWG.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportDwgHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `view_ids` array of integers, optional. Defaults to the active view.
  - `settings_name` string, optional. Exact DWG export setup name.
  - `file_name_prefix` string, optional. Must be a safe bare prefix.
- Response DTO summary:
  - `exported`, `output_folder`, `file_count`, `files[]`, `settings_used`, `note`, `error`.
- Revit API strategy:
  - Resolve views by id or active view.
  - Resolve named `ExportDWGSettings` when `settings_name` is supplied; otherwise use default `DWGExportOptions`.
  - Snapshot existing DWG files and modified times before export.
  - Call `Document.Export(outputFolder, prefix, viewIds, options)`.
  - Return files created or modified by this export run.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid output folder, unsafe prefix, invalid/missing views, view templates, and unknown setting names.
  - Unknown setting errors should include useful available-setting context where the handler can provide it.
  - Revit export failures return `exported=false` with `error`.
- Cross-version notes:
  - Uses stable `DWGExportOptions` and `ExportDWGSettings` APIs.
- Smoke/test expectations:
  - Export active plan/sheet to DWG with default settings.
  - Export with a known DWG setup name.
  - Try an unsafe prefix and confirm no export occurs.
- Review lesson:
  - `file_name_prefix` is path-sensitive and must be rejected if it contains separators, rooted paths, or parent traversal.

### `export_dgn`

- Purpose: export one or more views/sheets to DGN.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportDgnHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `view_ids` array of integers, optional. Defaults to the active view.
  - `file_name_prefix` string, optional. Must be a safe bare prefix.
- Response DTO summary:
  - `exported`, `output_folder`, `file_count`, `files[]`, `note`, `error`.
- Revit API strategy:
  - Resolve views by id or active view.
  - Configure `DGNExportOptions`.
  - Snapshot existing DGN files and modified times before export.
  - Call `Document.Export(outputFolder, prefix, viewIds, options)`.
  - Return files created or updated by this run.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid output folder, unsafe prefix, invalid/missing views, and view templates.
  - Does not report pre-existing stale DGN files as new outputs.
- Cross-version notes:
  - Uses stable `DGNExportOptions` export API.
- Smoke/test expectations:
  - Export active view to an empty temp folder.
  - Re-run into the same folder and confirm updated file detection is still correct.

### `export_dwf`

- Purpose: export one or more views/sheets to DWF or DWFx.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportDwfHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `view_ids` array of integers, optional. Defaults to the active view.
  - `file_name` string, optional.
  - `use_dwfx` boolean, optional. Defaults to false.
- Response DTO summary:
  - `exported`, `output_folder`, `file_count`, `files[]`, `note`, `error`.
- Revit API strategy:
  - Resolve views by id or active view.
  - Build a `ViewSet`.
  - Choose `DWFExportOptions` or `DWFXExportOptions` based on `use_dwfx`.
  - Call `Document.Export(outputFolder, baseName, viewSet, options)`.
  - Detect `.dwf` or `.dwfx` outputs by timestamp-aware scan.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid output folder, invalid/missing views, and view templates.
  - The handler sanitizes the output base name for DWF-style export naming rather than treating every invalid character as a hard error.
  - If no output is detected, returns a note instead of stale files.
- Cross-version notes:
  - Uses Revit DWF/DWFx export APIs that are stable across supported versions.
- Smoke/test expectations:
  - Export active sheet as DWF and DWFx.
  - Verify the reported extension matches `use_dwfx`.

### `export_ifc`

- Purpose: export the active document to IFC.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportIfcHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `file_name` string, required. Bare filename; `.ifc` extension may be supplied or omitted.
  - `ifc_version` string, optional. Defaults to Revit default behavior.
- Response DTO summary:
  - `exported`, `output_path`, `ifc_version_used`, `file_size_bytes`, `error`.
- Revit API strategy:
  - Validate folder and filename.
  - Configure `IFCExportOptions`.
  - Parse and set requested `IFCVersion` when supplied and supported.
  - Call `Document.Export(outputFolder, fileName, options)`.
  - Verify the exact expected `.ifc` path exists and report its size.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid output folder, blank or unsafe filename, invalid IFC version, or no generated file.
  - Revit may fail if the model or IFC exporter cannot produce an IFC from the current state.
- Cross-version notes:
  - IFC enum members can vary. Unsupported version names are rejected or fall back only where the handler explicitly allows default behavior.
- Smoke/test expectations:
  - Export default IFC to a temp folder and confirm nonzero file size.
  - Try a known version value such as `IFC2x3` or `IFC4`.
  - Try an invalid version and confirm no file is written.

### `export_nwc`

- Purpose: export the active document to Navisworks NWC.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportNwcHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `file_name` string, required. Bare filename; `.nwc` extension may be supplied or omitted.
  - `export_scope_view_id` integer, optional.
- Response DTO summary:
  - `exported`, `output_path`, `error`.
- Revit API strategy:
  - Validate folder and filename.
  - Configure `NavisworksExportOptions`.
  - If a scope view is supplied, resolve it and set view-scoped export options.
  - Call `Document.Export(outputFolder, fileName, options)`.
  - Verify the exact expected `.nwc` output exists.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid folder, unsafe filename, invalid scope view, or missing generated output.
  - Fails cleanly when Navisworks export support is unavailable in the Revit installation.
- Cross-version notes:
  - NWC export availability depends on installed Revit/Navisworks components, not only API version.
- Smoke/test expectations:
  - Run on a machine with Navisworks exporter installed.
  - Export whole model and one view-scoped NWC.
  - Run on a machine without the exporter and confirm a clear failure.

### `export_fbx`

- Purpose: export a 3D view to FBX.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportFbxHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `file_name` string, required. Bare filename; `.fbx` extension may be supplied or omitted.
  - `view_id` integer, optional. Defaults to the active view.
- Response DTO summary:
  - `exported`, `output_path`, `view_id`, `view_name`, `error`.
- Revit API strategy:
  - Resolve the supplied view or active view.
  - Require a non-template `View3D`.
  - Build a `ViewSet` containing the 3D view.
  - Configure `FBXExportOptions`.
  - Call `Document.Export(outputFolder, fileName, viewSet, options)`.
  - Verify the exact expected `.fbx` path exists.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid output folder, unsafe filename, invalid view id, non-3D view, or view template.
  - Returns `exported=false` if Revit reports success but the expected file is absent.
- Cross-version notes:
  - Uses stable FBX export API surface, but exact exporter behavior can vary by Revit version.
- Smoke/test expectations:
  - Export a default 3D view.
  - Try a plan view id and confirm a validation failure.

### `export_gbxml`

- Purpose: export the active document to gbXML.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportGbxmlHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `file_name` string, required. Bare filename; `.xml` extension may be supplied or omitted.
- Response DTO summary:
  - `exported`, `output_path`, `file_size_bytes`, `error`.
- Revit API strategy:
  - Validate folder and filename.
  - Configure `GBXMLExportOptions`.
  - Call `Document.Export(outputFolder, fileName, options)`.
  - Verify the exact expected `.xml` output path and report size.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid output folder, unsafe filename, or missing output file.
  - Revit may fail if the model lacks the energy/space/room data needed for gbXML export.
- Cross-version notes:
  - Uses stable gbXML export API surface.
- Smoke/test expectations:
  - Run against a model with valid rooms/spaces and energy settings.
  - Confirm exact output path and nonzero file size.

### `export_image`

- Purpose: export a view to an image file.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportImageHandler.cs`.
- Expected parameters:
  - `output_path` string, required. Must be a rooted file path whose parent folder exists.
  - `view_id` integer, optional. Defaults to the active view.
  - `pixel_size` integer, optional. Defaults to 2048 and must be positive.
  - `image_format` string, optional. Supports `png` and JPEG-style values handled by the handler.
- Response DTO summary:
  - `exported`, `output_path`, `view_id`, `view_name`, `pixel_size`, `error`.
- Revit API strategy:
  - Resolve target view and reject templates.
  - Configure `ImageExportOptions` with `ExportRange=SetOfViews`, target file path base, image resolution, file type, and pixel size.
  - Call `Document.ExportImage(options)`.
  - Detect actual produced image path, including Revit's possible suffixing behavior.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on rootless paths, missing parent folder, invalid view, template view, unsupported image format, or non-positive pixel size.
  - Does not return nonexistent requested paths as successful outputs.
- Cross-version notes:
  - Image export file naming can vary by Revit version and view name; the handler checks actual generated files.
- Smoke/test expectations:
  - Export active view to PNG and JPEG.
  - Compare the returned path to actual files in the output folder.
  - Try a missing parent folder and confirm validation fails before export.
- Review lesson:
  - File reporting must use actual file existence and run-time detection; callers should not trust a requested path unless the handler observed it.

### `export_schedule_csv`

- Purpose: export a Revit schedule to CSV or delimiter-separated text.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportScheduleCsvHandler.cs`.
- Expected parameters:
  - `output_path` string, required. Must be a rooted file path whose parent folder exists.
  - `schedule_id` integer, optional.
  - `schedule_name` string, optional.
  - `delimiter` string, optional. Defaults to comma.
  - One schedule selector is required.
- Response DTO summary:
  - `exported`, `output_path`, `schedule_id`, `schedule_name`, `error`.
- Revit API strategy:
  - Resolve a `ViewSchedule` by id or exact name.
  - Configure `ViewScheduleExportOptions`, including field delimiter.
  - Call `ViewSchedule.Export(folder, fileName, options)`.
  - Verify the exact expected output path exists.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid output path, missing parent folder, missing selector, invalid id, no matching schedule, duplicate schedule names, schedule templates, or missing output file.
- Cross-version notes:
  - Uses stable schedule export APIs.
- Smoke/test expectations:
  - Export by id and by name.
  - Try a duplicate schedule name scenario and confirm the handler requires disambiguation.

### `export_elements_data`

- Purpose: export element metadata and selected parameters for a category to JSON or CSV.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/ExportElementsDataHandler.cs`.
- Expected parameters:
  - `category` string, required.
  - `output_path` string, required. Must be a rooted file path whose parent folder exists.
  - `parameter_names` array of strings, optional.
  - `format` string, optional. Supports `json` and `csv`, with extension-based behavior where implemented.
- Response DTO summary:
  - `exported`, `output_path`, `category`, `element_count`, `parameter_count`, `format`, `error`.
- Revit API strategy:
  - Resolve category from document categories.
  - Collect non-type elements in that category.
  - Emit stable element fields such as id, unique id, name, category, type/family where available, and requested parameter values.
  - Serialize to JSON or CSV and write the file.
  - Convert double parameters based on spec where possible: length-like values to millimeters, area to m2, volume to m3, and angle to degrees.
- Transaction behavior:
  - No Revit transaction; writes an external file.
- Validation and failure modes:
  - Fails on blank category, unknown category, invalid output path, missing parent folder, unsupported format, and filesystem write errors.
  - Missing requested parameters are represented in row data according to handler behavior instead of crashing the export.
- Cross-version notes:
  - Parameter spec names and data type APIs vary; the handler uses guarded spec detection and fallback raw values.
- Smoke/test expectations:
  - Export walls to JSON with default fields.
  - Export a category to CSV with a few requested parameter names.
  - Verify length-like parameter conversion for sizes such as pipe or duct dimensions.
- Review lesson:
  - The fixed unit conversion needs to include length-like double specs, not only area, volume, and angle.

### `batch_export_sheets`

- Purpose: export multiple sheets to PDF or DWG in one operation.
- Type: W, external file write.
- Handler file: `src/shared/Handlers/BatchExportSheetsHandler.cs`.
- Expected parameters:
  - `output_folder` string, required. Must be a rooted existing folder.
  - `format` string, required. Supports `pdf` and `dwg`.
  - `sheet_ids` array of integers, optional. If omitted, the handler selects sheets by filter/all-sheets behavior.
  - `sheet_number_filter` string, optional. Used when selecting sheets without explicit ids.
- Response DTO summary:
  - `exported`, `format`, `output_folder`, `sheet_count`, `file_count`, `files[]`, `note`, `error`.
- Revit API strategy:
  - Resolve explicit sheet ids or collect non-placeholder `ViewSheet` elements using the optional sheet-number filter.
  - Reject empty explicit `sheet_ids`.
  - Snapshot existing files and modified times before export.
  - For PDF, use `PDFExportOptions` and `Document.Export(outputFolder, sheetIds, options)`.
  - For DWG, use `DWGExportOptions` and `Document.Export(outputFolder, prefix, sheetIds, options)`.
  - Return files created or updated by this run.
- Transaction behavior:
  - No transaction.
- Validation and failure modes:
  - Fails on invalid folder, unsupported format, empty explicit sheet id array, invalid ids, non-sheet ids, and placeholder sheets.
  - If no sheets match the filter, returns a clean failure.
  - If export succeeds but no files are detected, returns a note rather than stale files.
- Cross-version notes:
  - PDF and DWG export APIs are stable in the supported version range, but generated names can vary by Revit version and sheet naming.
- Smoke/test expectations:
  - Export two explicit sheets to PDF.
  - Export sheets matching a number filter to DWG.
  - Pass an empty `sheet_ids` array and confirm it fails instead of exporting all sheets.
- Review lesson:
  - Empty explicit `sheet_ids` must not be treated the same as omitted `sheet_ids`; omitted can mean all/filter, empty means caller error.

### `list_export_settings`

- Purpose: list named export settings and view/sheet sets in the active document.
- Type: R.
- Handler file: `src/shared/Handlers/ListExportSettingsHandler.cs`.
- Expected parameters:
  - None.
- Response DTO summary:
  - `doc_title`, `dwg_export_settings[]`, `print_settings[]`, `view_sheet_sets[]`.
- Revit API strategy:
  - Collect `ExportDWGSettings`, named `PrintSetting` elements, and `ViewSheetSet` elements.
  - Read ids, names, and view counts where available.
- Transaction behavior:
  - Read-only; no transaction.
- Validation and failure modes:
  - Blank or missing JSON parameters are accepted.
  - Collector failures return a failed command result.
- Cross-version notes:
  - Some setting details are version-dependent; the handler keeps the DTO to common ids/names/counts.
- Smoke/test expectations:
  - Compare named DWG settings, print settings, and sheet sets with the Revit UI.

### `create_view_sheet_set`

- Purpose: create a named Revit view/sheet set for printing/export workflows.
- Type: W, Revit print/view sheet set write.
- Handler file: `src/shared/Handlers/CreateViewSheetSetHandler.cs`.
- Expected parameters:
  - `name` string, required, nonblank.
  - `view_ids` array of integers, required, non-empty.
- Response DTO summary:
  - Success: `created`, `set_id`, `name`, `view_count`, `error`.
  - Validation failure can include `skipped[]` with id and reason details.
- Revit API strategy:
  - Validate all requested ids before saving anything.
  - Resolve each id to a non-template `View` usable in a `ViewSet`.
  - Set `PrintManager.PrintRange=Select`.
  - Assign the `ViewSet` to `ViewSheetSetting.CurrentViewSheetSet.Views`.
  - Call `ViewSheetSetting.SaveAs(name)`.
  - Locate the saved `ViewSheetSet` to return its id.
- Transaction behavior:
  - One transaction.
  - Rolls back on validation or `SaveAs` failure.
- Validation and failure modes:
  - Fails on blank name, empty view list, invalid ids, missing elements, non-view elements, view templates, unusable views, or duplicate/invalid set names rejected by Revit.
  - If any requested id is unusable, the handler returns `created=false` and does not create a partial set.
- Cross-version notes:
  - Uses stable `PrintManager`, `ViewSet`, and `ViewSheetSetting` APIs.
- Smoke/test expectations:
  - Create a set from two sheets and confirm it appears in the print dialog.
  - Include one bad id and confirm no partial set is created.
- Review lesson:
  - This handler must be all-or-nothing for requested ids. Partial-save behavior would hide skipped views and create misleading sheet sets.

### `get_print_settings`

- Purpose: read print manager state and named print/view sheet set settings.
- Type: R.
- Handler file: `src/shared/Handlers/GetPrintSettingsHandler.cs`.
- Expected parameters:
  - None.
- Response DTO summary:
  - `doc_title`, `print_to_file`, `selected_printer`, `print_range`, `named_print_settings[]`, `view_sheet_sets[]`.
- Revit API strategy:
  - Read `Document.PrintManager` state.
  - Collect named `PrintSetting` elements.
  - Collect `ViewSheetSet` elements and include ids/names/view counts where available.
- Transaction behavior:
  - Read-only; no transaction.
- Validation and failure modes:
  - Missing printers or empty named settings return empty/null DTO fields rather than a write failure.
  - Unexpected Revit print-manager access errors return a failed command result.
- Cross-version notes:
  - Keeps the DTO focused on stable print manager fields.
- Smoke/test expectations:
  - Compare returned selected printer, print-to-file flag, print range, and named settings with Revit Print Setup.

## Wiring

Server:

- `src/server/Program.cs` registers `ExportTools` under the `export` toolset.
- Each MCP wrapper calls `ToolGateway.SendToRevit()` with the command name used by the dispatcher.
- Parameter names are converted from C# wrapper names to snake_case JSON payload names.

Toolset filtering:

- `src/server/ToolsetFilter.cs` lists `export` in known toolsets, default-on toolsets, and write-capable toolsets.

Revit dispatcher:

- `src/shared/Infrastructure/CommandDispatcher.cs` registers:
  - `export_room_data` -> `ExportRoomDataHandler`
  - `export_pdf` -> `ExportPdfHandler`
  - `export_dwg` -> `ExportDwgHandler`
  - `export_dgn` -> `ExportDgnHandler`
  - `export_dwf` -> `ExportDwfHandler`
  - `export_ifc` -> `ExportIfcHandler`
  - `export_nwc` -> `ExportNwcHandler`
  - `export_fbx` -> `ExportFbxHandler`
  - `export_gbxml` -> `ExportGbxmlHandler`
  - `export_image` -> `ExportImageHandler`
  - `export_schedule_csv` -> `ExportScheduleCsvHandler`
  - `export_elements_data` -> `ExportElementsDataHandler`
  - `batch_export_sheets` -> `BatchExportSheetsHandler`
  - `list_export_settings` -> `ListExportSettingsHandler`
  - `create_view_sheet_set` -> `CreateViewSheetSetHandler`
  - `get_print_settings` -> `GetPrintSettingsHandler`

## Acceptance Criteria

- All 16 as-built `export` toolset commands are visible when the `export` toolset is enabled.
- The dispatcher has exactly one concrete handler registration for each command.
- Export handlers validate output folders, output paths, filenames, formats, ids, and view/sheet/schedule types before invoking the export API.
- Secondary names such as `file_name` and `file_name_prefix` are not allowed to escape the output folder.
- Handlers report only actual detected output files or exact expected paths that exist.
- If Revit reports export success but no output file is detected, handlers return a clear note instead of stale directory contents.
- `batch_export_sheets` treats omitted `sheet_ids` and empty explicit `sheet_ids` differently.
- `create_view_sheet_set` is all-or-nothing for requested view ids and never saves a partial set after validation skipped any requested id.
- Read-only setting/list tools open no transactions.
- File-writing tools do not mutate the Revit model unless explicitly documented, with `create_view_sheet_set` being the Revit print/view-set write in this wave.

## Review Checklist

- Confirm `Program.cs` wrapper command names match `CommandDispatcher` registrations.
- Confirm all file path handlers reject rootless paths and missing parent/output folders.
- Confirm secondary filenames and prefixes reject separators, rooted paths, and parent traversal where the handler treats them as user-supplied bare names.
- Confirm timestamp-aware output detection is used for PDF, DWG, DGN, DWF/DWFx, and batch sheet exports.
- Confirm exact expected-path checks are used for IFC, NWC, FBX, gbXML, schedule CSV, image export, and element data export where applicable.
- Confirm no handler returns stale files from a prior export as if they were created by the current run.
- Confirm `batch_export_sheets` fails on empty explicit `sheet_ids`.
- Confirm `create_view_sheet_set` rolls back or returns `created=false` when any requested view id is unusable.
- Confirm `export_elements_data` unit conversion includes length-like specs as well as area, volume, and angle.
- Run live Revit smoke tests on a disposable model before marking a release candidate.

## Known Risks

- The docs task did not run `dotnet build` and did not run live Revit smoke tests.
- Export output naming is partly controlled by Revit. The handlers mitigate this with exact path and timestamp-aware detection, but callers should still inspect `files[]`, `output_path`, and `note`.
- `export_nwc` depends on Navisworks exporter availability. A valid model and path can still fail on machines without that component.
- `export_gbxml` depends on model energy/room/space readiness and can fail for reasons outside path validation.
- `export_fbx` requires a 3D view; active plan/sheet views are not valid defaults.
- Network paths, locked folders, antivirus, and file overwrite permissions can cause export failures after validation passes.
- DWF naming behavior sanitizes base names differently from strict bare-filename validators used by PDF/DWG/DGN-style secondary name fields.

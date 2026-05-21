# Wave 05 - Sheets Target Implementation Spec

## Status

- Status: pending implementation spec.
- Target toolset: `sheets`.
- Tool count: 12.
- Default exposure target: default-on.
- Write capability target: write-capable.
- Assigned implementation surface: future source changes in `src/shared/Handlers/`, `src/server/Program.cs`, `src/shared/Infrastructure/CommandDispatcher.cs`, `src/server/ToolsetFilter.cs`, tests, and golden snapshots.
- Current documentation-only task: this file is a target spec. It does not implement handlers or wiring.

## Scope

Wave 05 adds sheet-centric operations that are not already covered by the existing `view` and `export` toolsets. The existing `view` toolset already has `create_view`, `place_view_on_sheet`, and `analyze_sheet_layout`; the new `sheets` toolset should own sheet creation, sheet listing, title block parameter IO, schedule placement, revisions, and sheet renumbering.

In scope:

- Create normal and placeholder sheets.
- Duplicate a sheet with viewport/schedule layout.
- List sheets, title blocks, and revisions.
- Read and write title block parameters.
- Place schedules on sheets using sheet paper coordinates in millimeters.
- Create revisions and assign them to sheets.
- Bulk renumber sheets with collision checks.

Out of scope:

- Moving existing viewports after placement. Keep that in a later view/sheet-layout tool.
- Exporting sheets. `batch_export_sheets`, `export_pdf`, and related export tools already own this.
- Creating model views. `create_view` owns view creation.
- Editing revision clouds.
- Source changes in this documentation task.

## Dependencies

Read and align with these existing repo conventions:

- `CLAUDE.md`: handler-per-tool, DTO-only returns, guarded JSON parsing, RevitCompat for element ids, mm/m2/m3/deg external units.
- `docs/roadmap-221-tools.md`: Wave 05 tool list and toolset target.
- `src/server/Program.cs`: MCP wrappers use `[McpServerToolType, Toolset("<name>")]`; wrappers call `ToolGateway.SendToRevit(command, new { ... })` and serialize the returned `JObject`.
- `src/server/ToolsetFilter.cs`: `KnownToolsets`, `DefaultOn`, and `WriteCapable`.
- `src/shared/Infrastructure/CommandDispatcher.cs`: explicit handler registration by phase block.
- Nearby handlers:
  - `CreateViewHandler.cs`
  - `PlaceViewOnSheetHandler.cs`
  - `AnalyzeSheetLayoutHandler.cs`
  - `BatchExportSheetsHandler.cs`
  - `CreateViewSheetSetHandler.cs`
  - `ListSchedulesHandler.cs`
  - `GetScheduleDefinitionHandler.cs`

Implementation conventions for every handler:

- Implement `IRevitCommand`.
- `Name` equals the MCP command name.
- `ParametersSchema` is a JSON object schema string.
- Parse with `string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson)` inside `try/catch JsonException`.
- Return only DTOs, never raw Revit API objects.
- Use `RevitCompat.ToElementId(long)`, `RevitCompat.GetId(...)`, `RevitCompat.GetIdOrNull(...)`, and `RevitCompat.CanRepresentElementId(long)`.
- Use external millimeters for sheet coordinates; convert to Revit feet via `mm / 304.8`.
- Write tools use one explicit `Transaction` unless noted. Roll back on any exception.

## Toolset Contract

Add a new server wrapper:

```csharp
[McpServerToolType, Toolset("sheets")]
public class SheetsTools
{
    // 12 methods listed below.
}
```

Add toolset gating in `src/server/ToolsetFilter.cs`:

- Add `"sheets"` to `KnownToolsets`.
- Add `"sheets"` to `DefaultOn`.
- Add `"sheets"` to `WriteCapable`.

Add registration in `src/server/Program.cs`:

- `RegisterToolsets`: `if (enabled.Contains("sheets")) mcp = mcp.WithTools<SheetsTools>();`
- `ResolveRegisteredToolTypes`: `if (enabled.Contains("sheets")) types.Add(typeof(SheetsTools));`

Add dispatcher registration in `src/shared/Infrastructure/CommandDispatcher.cs` after the existing Phase 16 export block:

```csharp
// Phase 17: Sheets
Register(new Handlers.CreateSheetHandler());
Register(new Handlers.DuplicateSheetHandler());
Register(new Handlers.CreatePlaceholderSheetHandler());
Register(new Handlers.ListSheetsHandler());
Register(new Handlers.SetTitleblockParametersHandler());
Register(new Handlers.GetTitleblockParametersHandler());
Register(new Handlers.ListTitleblocksHandler());
Register(new Handlers.PlaceScheduleOnSheetHandler());
Register(new Handlers.CreateRevisionHandler());
Register(new Handlers.AssignRevisionToSheetHandler());
Register(new Handlers.ListRevisionsHandler());
Register(new Handlers.RenumberSheetsHandler());
```

Read-only MCP methods must use `ReadOnly = true, Idempotent = true`. Write methods should use `Destructive = false` unless the implementation deletes/replaces user data. `renumber_sheets` is write-capable but not destructive if it only changes sheet numbers/names.

## Tool Specs

### `create_sheet`

- Handler file: `src/shared/Handlers/CreateSheetHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "create_sheet", Destructive = false)]
public static async Task<string> CreateSheet(
    string sheetNumber,
    string sheetName,
    long? titleBlockTypeId = null,
    string titleBlockName = "")
```

- Wrapper payload: `{ sheet_number = sheetNumber, sheet_name = sheetName, title_block_type_id = titleBlockTypeId, title_block_name = titleBlockName }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["sheet_number", "sheet_name"],
  "properties": {
    "sheet_number": { "type": "string" },
    "sheet_name": { "type": "string" },
    "title_block_type_id": { "type": "integer" },
    "title_block_name": { "type": "string" }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "sheet_id": 123,
  "sheet_number": "A101",
  "sheet_name": "Floor Plan",
  "title_block_type_id": 456,
  "title_block_name": "A1 Metric",
  "error": null
}
```

- Revit API strategy:
  - Resolve title block by `title_block_type_id` first; otherwise by exact case-insensitive `FamilySymbol.Name` or `FamilyName + ": " + Name` under `BuiltInCategory.OST_TitleBlocks`.
  - If no title block is supplied, use the first title block type in the document.
  - Create via `ViewSheet.Create(doc, titleBlockId)`.
  - Set `SheetNumber` and `Name` after creation.
- Transaction behavior: one `Transaction(doc, "Bimwright: create sheet")`.
- Validation/failure modes:
  - Fail if no document is open.
  - Fail if `sheet_number` or `sheet_name` is blank.
  - Fail before transaction if sheet number already exists.
  - Fail if supplied title block id is out of range, not found, or not a title block type.
  - Return DTO with `created=false` for expected Revit validation failures such as duplicate sheet number.
- Cross-version notes:
  - Use `RevitCompat` for all ElementIds.
  - `ViewSheet.Create(Document, ElementId)` is available across Revit 2022-2027.
- Smoke/test expectations:
  - Unit/golden wrapper test exposes `create_sheet` under `sheets`.
  - Revit smoke: create a sheet with a known title block; verify `list_sheets` sees the new sheet and title block id.

### `duplicate_sheet`

- Handler file: `src/shared/Handlers/DuplicateSheetHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "duplicate_sheet", Destructive = false)]
public static async Task<string> DuplicateSheet(
    string newSheetNumber,
    long? sourceSheetId = null,
    string sourceSheetNumber = "",
    string newSheetName = "",
    string duplicateViewOption = "with_detailing",
    bool includeSchedules = true,
    bool includeRevisions = true,
    bool reuseViewsWhenAllowed = true)
```

- Wrapper payload: `{ source_sheet_id, source_sheet_number, new_sheet_number, new_sheet_name, duplicate_view_option, include_schedules, include_revisions, reuse_views_when_allowed }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["new_sheet_number"],
  "properties": {
    "source_sheet_id": { "type": "integer" },
    "source_sheet_number": { "type": "string" },
    "new_sheet_number": { "type": "string" },
    "new_sheet_name": { "type": "string" },
    "duplicate_view_option": { "type": "string", "enum": ["duplicate", "with_detailing", "as_dependent"] },
    "include_schedules": { "type": "boolean", "default": true },
    "include_revisions": { "type": "boolean", "default": true },
    "reuse_views_when_allowed": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "duplicated": true,
  "source_sheet_id": 100,
  "new_sheet_id": 200,
  "new_sheet_number": "A102",
  "new_sheet_name": "Second Floor Plan",
  "title_block_type_id": 456,
  "viewports": [
    {
      "source_viewport_id": 301,
      "source_view_id": 302,
      "new_view_id": 402,
      "new_viewport_id": 401,
      "center": { "x_mm": 120.0, "y_mm": 80.0 },
      "mode": "duplicated_view"
    }
  ],
  "schedules": [
    {
      "source_schedule_instance_id": 501,
      "schedule_id": 502,
      "new_schedule_instance_id": 601,
      "point": { "x_mm": 40.0, "y_mm": 35.0 }
    }
  ],
  "revision_ids": [10, 11],
  "warnings": [],
  "error": null
}
```

- Revit API strategy:
  - Resolve the source `ViewSheet` by id or sheet number.
  - Resolve the source title block instance on the source sheet, then use its `GetTypeId()` for the new sheet.
  - Create the new sheet with `ViewSheet.Create`.
  - Copy title block instance parameters only if they are writable and instance-scoped; skip read-only/type parameters and record warnings.
  - For each `Viewport` from `sourceSheet.GetAllViewports()`:
    - Resolve `Viewport`, source `View`, `GetBoxCenter()`, and `GetBoxOutline()`.
    - If `Viewport.CanAddViewToSheet(doc, newSheet.Id, sourceView.Id)` and `reuse_views_when_allowed` is true, place the same view.
    - Otherwise duplicate the source view using `View.Duplicate(...)` with `ViewDuplicateOption.Duplicate`, `WithDetailing`, or `AsDependent`.
    - Place with `Viewport.Create(doc, newSheet.Id, targetView.Id, originalCenter)`.
  - For schedules, collect `ScheduleSheetInstance` elements scoped to the source sheet and recreate them with `ScheduleSheetInstance.Create(doc, newSheet.Id, scheduleId, originalPoint)`.
  - If `include_revisions`, copy additional revision ids with `SetAdditionalRevisionIds`. Do not attempt to copy revision clouds.
- Transaction behavior: one `TransactionGroup` named `Bimwright: duplicate sheet` containing one transaction. Assimilate on full success; roll back the group on any hard failure.
- Validation/failure modes:
  - Fail if neither source id nor source sheet number resolves exactly one source sheet.
  - Fail if `new_sheet_number` is blank or already exists.
  - Fail if source sheet is a placeholder unless implementation explicitly supports placeholder duplication. Target behavior: reject placeholders and tell caller to use `create_placeholder_sheet`.
  - Fail if duplicate option is invalid.
  - Warn, do not fail, for title block parameters that are read-only.
  - Fail atomically if a source viewport cannot be reused or duplicated.
- Cross-version notes:
  - `Viewport.CanAddViewToSheet`, `Viewport.Create`, `View.Duplicate`, and `ScheduleSheetInstance.Create` are available across target versions.
  - Use `RevitCompat.GetId` for `Viewport.Id`, `View.Id`, and `ScheduleSheetInstance.Id`.
- Smoke/test expectations:
  - Smoke with a sheet containing one plan viewport and one schedule.
  - Verify the new sheet number, title block type, viewport count, schedule count, and original viewport centers in mm.

### `create_placeholder_sheet`

- Handler file: `src/shared/Handlers/CreatePlaceholderSheetHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "create_placeholder_sheet", Destructive = false)]
public static async Task<string> CreatePlaceholderSheet(string sheetNumber, string sheetName)
```

- Wrapper payload: `{ sheet_number = sheetNumber, sheet_name = sheetName }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["sheet_number", "sheet_name"],
  "properties": {
    "sheet_number": { "type": "string" },
    "sheet_name": { "type": "string" }
  }
}
```

- Response DTO: `{ "created": true, "sheet_id": 123, "sheet_number": "A000", "sheet_name": "Placeholder", "is_placeholder": true, "error": null }`
- Revit API strategy: use `ViewSheet.CreatePlaceholder(doc)`, then set `SheetNumber` and `Name`.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if number/name is blank.
  - Fail if sheet number already exists.
- Cross-version notes: placeholder sheet API is stable for Revit 2022-2027.
- Smoke/test expectations: create placeholder and verify `list_sheets(includePlaceholders=true)` returns `is_placeholder=true`.

### `list_sheets`

- Handler file: `src/shared/Handlers/ListSheetsHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_sheets", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListSheets(
    string numberFilter = "",
    string namePattern = "",
    bool includeRevisions = true,
    bool includeViewports = false,
    bool includePlaceholders = true,
    int limit = 1000)
```

- Wrapper payload: `{ number_filter, name_pattern, include_revisions, include_viewports, include_placeholders, limit }`
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "number_filter": { "type": "string" },
    "name_pattern": { "type": "string" },
    "include_revisions": { "type": "boolean", "default": true },
    "include_viewports": { "type": "boolean", "default": false },
    "include_placeholders": { "type": "boolean", "default": true },
    "limit": { "type": "integer", "default": 1000, "minimum": 1, "maximum": 10000 }
  }
}
```

- Response DTO:

```json
{
  "total": 25,
  "returned": 25,
  "limit_hit": false,
  "sheets": [
    {
      "id": 123,
      "sheet_number": "A101",
      "sheet_name": "Floor Plan",
      "is_placeholder": false,
      "viewport_count": 2,
      "schedule_count": 1,
      "title_block": { "instance_id": 300, "type_id": 301, "family_name": "TB", "type_name": "A1" },
      "revision_ids": [10],
      "revisions": [{ "id": 10, "sequence_number": 1, "revision_number": "1", "description": "IFC" }]
    }
  ]
}
```

- Revit API strategy:
  - Collect `ViewSheet` with `FilteredElementCollector(doc).OfClass(typeof(ViewSheet))`.
  - Filter by sheet number and name substrings, case-insensitive.
  - Count viewports via `GetAllViewports()`.
  - Count schedules via scoped collector of `ScheduleSheetInstance` where owner view equals sheet id.
  - Resolve one title block instance from sheet-scoped `OST_TitleBlocks`.
  - Resolve revisions from `sheet.GetAllRevisionIds()` when available; otherwise additional revisions only and include a warning.
- Transaction behavior: none.
- Validation/failure modes:
  - Clamp `limit` to 1-10000.
  - Skip sheets that fail introspection and increment `skipped`.
- Cross-version notes:
  - Some revision-number APIs vary; implement defensive helpers for sequence/revision number fields.
- Smoke/test expectations:
  - Verify active project sheet count and placeholder inclusion/exclusion.
  - Golden snapshot includes `list_sheets` as read-only and idempotent.

### `set_titleblock_parameters`

- Handler file: `src/shared/Handlers/SetTitleblockParametersHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "set_titleblock_parameters", Destructive = false)]
public static async Task<string> SetTitleblockParameters(
    object parameters,
    long? sheetId = null,
    string sheetNumber = "",
    string target = "instance")
```

- Wrapper payload: normalize `parameters` to a JSON object, then send `{ sheet_id, sheet_number, target, parameters }`.
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["parameters"],
  "properties": {
    "sheet_id": { "type": "integer" },
    "sheet_number": { "type": "string" },
    "target": { "type": "string", "enum": ["instance", "type", "both"], "default": "instance" },
    "parameters": { "type": "object", "additionalProperties": true }
  }
}
```

- Response DTO:

```json
{
  "updated": true,
  "sheet_id": 123,
  "sheet_number": "A101",
  "title_block_instance_id": 300,
  "title_block_type_id": 301,
  "parameters": {
    "Drawn By": { "status": "set", "storage_type": "String" },
    "Scale": { "status": "skipped", "reason": "read-only" }
  },
  "error": null
}
```

- Revit API strategy:
  - Resolve sheet by id, sheet number, or active sheet when both omitted.
  - Resolve title block instance from a sheet-scoped `FilteredElementCollector(doc, sheet.Id).OfCategory(OST_TitleBlocks)`.
  - Match parameters case-insensitively by `LookupParameter` first, then iterate `Parameters`.
  - Support `StorageType.String`, `Integer`, `Double`, and `ElementId`.
  - For double parameters, convert from external units when the parameter spec is length, area, volume, or angle. Otherwise pass numeric value through.
- Transaction behavior: one transaction. Keep atomic behavior: if any requested parameter is invalid, roll back unless the only issue is a read-only parameter that is reported as skipped.
- Validation/failure modes:
  - Fail if `parameters` is not an object or is empty.
  - Fail if sheet cannot be resolved.
  - Fail if sheet has no title block instance.
  - Fail if `target` is invalid.
  - Fail for unsupported parameter storage type.
- Cross-version notes:
  - Use `Definition.GetDataType()` and ForgeTypeId string matching, as existing family/schedule handlers do.
- Smoke/test expectations:
  - Set a writable title block text parameter, then verify via `get_titleblock_parameters`.
  - Verify read-only parameters are reported and do not crash the handler.

### `get_titleblock_parameters`

- Handler file: `src/shared/Handlers/GetTitleblockParametersHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "get_titleblock_parameters", ReadOnly = true, Idempotent = true)]
public static async Task<string> GetTitleblockParameters(
    long? sheetId = null,
    string sheetNumber = "",
    string target = "both",
    bool includeReadOnly = true)
```

- Wrapper payload: `{ sheet_id, sheet_number, target, include_read_only }`
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "sheet_id": { "type": "integer" },
    "sheet_number": { "type": "string" },
    "target": { "type": "string", "enum": ["instance", "type", "both"], "default": "both" },
    "include_read_only": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "sheet_id": 123,
  "sheet_number": "A101",
  "title_block_instance_id": 300,
  "title_block_type_id": 301,
  "instance_parameters": [
    { "name": "Drawn By", "storage_type": "String", "value": "KL", "is_read_only": false }
  ],
  "type_parameters": []
}
```

- Revit API strategy: same sheet/title block resolution as setter; enumerate parameters into DTOs with converted external units and value strings.
- Transaction behavior: none.
- Validation/failure modes: fail if sheet/title block cannot be resolved; fail if `target` invalid.
- Cross-version notes: parameter `Definition.GetDataType()` may throw for older or invalid params; catch and return `spec_type_id=null`.
- Smoke/test expectations: read parameters from a known title block and verify no raw Revit objects are serialized.

### `list_titleblocks`

- Handler file: `src/shared/Handlers/ListTitleblocksHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_titleblocks", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListTitleblocks(string namePattern = "", bool includeInactive = true, int limit = 1000)
```

- Wrapper payload: `{ name_pattern, include_inactive, limit }`
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "name_pattern": { "type": "string" },
    "include_inactive": { "type": "boolean", "default": true },
    "limit": { "type": "integer", "default": 1000, "minimum": 1, "maximum": 10000 }
  }
}
```

- Response DTO:

```json
{
  "total": 3,
  "returned": 3,
  "titleblocks": [
    {
      "type_id": 301,
      "family_id": 300,
      "family_name": "Company Titleblock",
      "type_name": "A1 Metric",
      "display_name": "Company Titleblock: A1 Metric",
      "is_active": true,
      "sheet_instance_count": 12
    }
  ]
}
```

- Revit API strategy: collect `FamilySymbol` in `OST_TitleBlocks`; count instances by symbol id with non-type title block collector.
- Transaction behavior: none.
- Validation/failure modes: clamp `limit`; skip symbols that throw during introspection.
- Cross-version notes: use `RevitCompat.GetId` for type and family ids.
- Smoke/test expectations: compare count with Revit project browser title block types.

### `place_schedule_on_sheet`

- Handler file: `src/shared/Handlers/PlaceScheduleOnSheetHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "place_schedule_on_sheet", Destructive = false)]
public static async Task<string> PlaceScheduleOnSheet(
    double xMm,
    double yMm,
    long? sheetId = null,
    string sheetNumber = "",
    long? scheduleId = null,
    string scheduleName = "")
```

- Wrapper payload: `{ sheet_id, sheet_number, schedule_id, schedule_name, x_mm = xMm, y_mm = yMm }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["x_mm", "y_mm"],
  "properties": {
    "sheet_id": { "type": "integer" },
    "sheet_number": { "type": "string" },
    "schedule_id": { "type": "integer" },
    "schedule_name": { "type": "string" },
    "x_mm": { "type": "number" },
    "y_mm": { "type": "number" }
  }
}
```

- Response DTO:

```json
{
  "placed": true,
  "sheet_id": 123,
  "sheet_number": "A101",
  "schedule_id": 500,
  "schedule_name": "Door Schedule",
  "schedule_instance_id": 600,
  "point": { "x_mm": 50.0, "y_mm": 80.0 },
  "error": null
}
```

- Revit API strategy:
  - Resolve sheet by id or number.
  - Resolve `ViewSchedule` by id or exact case-insensitive name.
  - Reject schedule templates, titleblock revision schedules, and internal keynote schedules.
  - Convert point to `new XYZ(x_mm / 304.8, y_mm / 304.8, 0)`.
  - Create with `ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point)`.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if sheet or schedule is ambiguous/not found.
  - Fail if schedule type cannot be placed.
  - Fail if coordinates are non-finite.
- Cross-version notes: `ScheduleSheetInstance.Create` is available in Revit 2022-2027.
- Smoke/test expectations: place a schedule and verify `list_sheets(includeViewports=true)` reports schedule count increment.

### `create_revision`

- Handler file: `src/shared/Handlers/CreateRevisionHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "create_revision", Destructive = false)]
public static async Task<string> CreateRevision(
    string description,
    string date = "",
    string issuedTo = "",
    string issuedBy = "",
    bool issued = false)
```

- Wrapper payload: `{ description, date, issued_to = issuedTo, issued_by = issuedBy, issued }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["description"],
  "properties": {
    "description": { "type": "string" },
    "date": { "type": "string" },
    "issued_to": { "type": "string" },
    "issued_by": { "type": "string" },
    "issued": { "type": "boolean", "default": false }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "revision_id": 10,
  "sequence_number": 1,
  "revision_number": "1",
  "date": "2026-05-21",
  "description": "Issued for coordination",
  "issued": false,
  "error": null
}
```

- Revit API strategy: use `Revision.Create(doc)`; set `Description`, `RevisionDate`, `IssuedTo`, `IssuedBy`, and `Issued`.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if description is blank.
  - Do not parse/enforce date format; store the supplied string because Revit revision date is a string field.
  - If `issued=true`, set all editable fields before setting `Issued`.
- Cross-version notes: revision API is stable; use defensive reflection only for optional number fields in the response.
- Smoke/test expectations: create a revision and verify `list_revisions` returns it.

### `assign_revision_to_sheet`

- Handler file: `src/shared/Handlers/AssignRevisionToSheetHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "assign_revision_to_sheet", Destructive = false)]
public static async Task<string> AssignRevisionToSheet(
    long revisionId,
    long[] sheetIds = null,
    string[] sheetNumbers = null,
    string mode = "append")
```

- Wrapper payload: `{ revision_id = revisionId, sheet_ids = sheetIds, sheet_numbers = sheetNumbers, mode }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["revision_id"],
  "properties": {
    "revision_id": { "type": "integer" },
    "sheet_ids": { "type": "array", "items": { "type": "integer" } },
    "sheet_numbers": { "type": "array", "items": { "type": "string" } },
    "mode": { "type": "string", "enum": ["append", "replace", "remove"], "default": "append" }
  }
}
```

- Response DTO:

```json
{
  "updated": true,
  "revision_id": 10,
  "mode": "append",
  "sheets": [
    { "sheet_id": 123, "sheet_number": "A101", "status": "assigned" }
  ],
  "error": null
}
```

- Revit API strategy:
  - Resolve revision id to `Revision`.
  - Resolve sheets by ids and/or numbers. Deduplicate by `ElementId`.
  - Use `ViewSheet.GetAdditionalRevisionIds()` and `ViewSheet.SetAdditionalRevisionIds(...)`.
  - `append`: union current additional revision ids with target revision.
  - `remove`: remove target revision from additional ids only.
  - `replace`: set additional ids to only the target revision. This does not remove revision cloud-derived revisions.
- Transaction behavior: one transaction for all sheets; roll back if any sheet fails validation.
- Validation/failure modes:
  - Fail if no sheets are supplied.
  - Fail on invalid id range, unknown revision, unknown sheet, duplicate ambiguous sheet number.
  - Warn in response if a revision remains because it is cloud-derived and cannot be removed via additional revision ids.
- Cross-version notes: use `ICollection<ElementId>` helpers; avoid LINQ methods that depend on reference equality for ElementId, compare `RevitCompat.GetId`.
- Smoke/test expectations: assign a revision to two sheets and verify `list_revisions(includeSheets=true)` reports both.

### `list_revisions`

- Handler file: `src/shared/Handlers/ListRevisionsHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_revisions", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListRevisions(bool includeSheets = true)
```

- Wrapper payload: `{ include_sheets = includeSheets }`
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "include_sheets": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "total": 2,
  "revisions": [
    {
      "id": 10,
      "sequence_number": 1,
      "revision_number": "1",
      "date": "2026-05-21",
      "description": "IFC",
      "issued": false,
      "issued_to": "",
      "issued_by": "",
      "sheet_ids": [123],
      "sheets": [{ "id": 123, "sheet_number": "A101", "sheet_name": "Floor Plan" }]
    }
  ]
}
```

- Revit API strategy:
  - Collect `Revision`.
  - For sheet assignment, collect all `ViewSheet`; for each revision use `sheet.GetAllRevisionIds()` and match ids.
- Transaction behavior: none.
- Validation/failure modes: skip revisions/sheets that fail introspection and include `skipped`.
- Cross-version notes: revision number APIs vary; use direct properties when available and reflection fallback for optional fields.
- Smoke/test expectations: list after `create_revision` and `assign_revision_to_sheet`.

### `renumber_sheets`

- Handler file: `src/shared/Handlers/RenumberSheetsHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "renumber_sheets", Destructive = false)]
public static async Task<string> RenumberSheets(
    object items = null,
    string find = "",
    string replace = "",
    string prefix = "",
    string suffix = "",
    bool dryRun = true)
```

- Wrapper payload:
  - Normalize `items` to an optional JSON array.
  - Send `{ items, find, replace, prefix, suffix, dry_run = dryRun }`.
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "items": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "sheet_id": { "type": "integer" },
          "sheet_number": { "type": "string" },
          "new_sheet_number": { "type": "string" },
          "new_sheet_name": { "type": "string" }
        }
      }
    },
    "find": { "type": "string" },
    "replace": { "type": "string" },
    "prefix": { "type": "string" },
    "suffix": { "type": "string" },
    "dry_run": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "dry_run": true,
  "updated": false,
  "count": 2,
  "changes": [
    { "sheet_id": 123, "old_sheet_number": "A101", "new_sheet_number": "B101", "old_sheet_name": "Floor", "new_sheet_name": "Floor" }
  ],
  "conflicts": [],
  "error": null
}
```

- Revit API strategy:
  - Two modes:
    - Explicit mode when `items` is supplied. Each item resolves a sheet and supplies `new_sheet_number` and/or `new_sheet_name`.
    - Transform mode when `items` is omitted. Apply `find`/`replace`, `prefix`, and `suffix` to every non-placeholder and placeholder sheet number.
  - Precompute every target sheet number before opening a transaction.
  - Detect collisions against unchanged existing sheets and within the target set.
  - For real writes, use temporary unique sheet numbers first to support swaps such as A101 <-> A102, then apply final numbers and names.
- Transaction behavior:
  - `dry_run=true`: no transaction.
  - `dry_run=false`: one transaction; rollback on any failure.
- Validation/failure modes:
  - Fail if no effective changes are requested.
  - Fail if target sheet number is blank.
  - Fail if explicit item does not identify exactly one sheet.
  - Fail before transaction on any duplicate target.
  - Return `conflicts` with detailed sheet ids/numbers instead of partially applying.
- Cross-version notes:
  - Sheet number uniqueness behavior is Revit-owned and stable; still preflight to produce useful errors.
- Smoke/test expectations:
  - Dry-run transform returns expected changes and leaves document unchanged.
  - Real run with two-sheet swap succeeds because temporary numbers are used.

## Wiring

Future implementation should wire in this order:

1. Add all 12 handler files.
2. Add the Phase 17 dispatcher registration block.
3. Add `SheetsTools` wrapper class in `Program.cs`.
4. Add `sheets` to `ToolsetFilter.KnownToolsets`, `DefaultOn`, and `WriteCapable`.
5. Add or update golden snapshot tests so `sheets` appears as default-on and write-capable, with read-only flags only on `list_sheets`, `get_titleblock_parameters`, `list_titleblocks`, and `list_revisions`.
6. Update README/tool counts only after source implementation is complete and verified.

## Acceptance Criteria

- All 12 handlers compile for Revit 2022-2027 plugin shells.
- `sheets` is visible by default through MCP tool discovery.
- `--read-only` removes `sheets` because the toolset is write-capable.
- Read-only sheet tools are individually marked `ReadOnly = true, Idempotent = true`.
- All write handlers validate fully before mutating where practical.
- All write handlers use transactions and roll back on hard failures.
- Sheet coordinates use external millimeters and internal feet.
- Every handler returns a stable DTO with `error` when the operation can produce expected user-level failures.
- No raw Revit API objects are serialized.
- ElementIds use `RevitCompat`.
- JSON parse failures return `CommandResult.Fail("Parameters must be a JSON object: ...")`.

## Review Checklist

- Handler names, command names, and wrapper names match exactly.
- `ParametersSchema` required fields match wrapper and handler validation.
- Source sheet duplication handles view reuse restrictions and does not silently skip viewports.
- Title block parameter write path does not mutate type parameters unless `target` asks for type/both.
- Revision assignment uses additional revision ids and documents cloud-derived limitations.
- Renumbering preflights all collisions before transaction and supports swaps.
- Golden snapshot marks toolset as default-on/write-capable.
- No `.IntegerValue` or `.Value` direct ElementId access.
- No source task should run `dotnet build` until implementation phase.

## Known Risks

- Duplicating a sheet cannot perfectly clone every manual annotation or external dependency. The target behavior is viewport/schedule/title-block/revision replication, with explicit warnings for skipped parameter copies.
- Reusing a view on a second sheet is not generally allowed for model views. The implementation must duplicate views when `Viewport.CanAddViewToSheet` rejects reuse.
- Sheet coordinates are Revit paper coordinates in feet internally. Users will supply millimeters; smoke tests should verify the origin and placement convention against `analyze_sheet_layout`.
- Removing a revision from additional revision ids will not remove revision-cloud-derived sheet revision membership.

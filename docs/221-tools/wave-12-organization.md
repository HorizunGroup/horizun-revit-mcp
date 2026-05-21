# Wave 12 - View Templates + Selection Target Implementation Spec

## Status

Target implementation spec. This wave is pending and should add the new
default-on, write-capable `organization` toolset.

Roadmap count: 10 tools.

## Scope

In scope:

- List view templates with compatibility and controlled setting metadata.
- Create, apply, duplicate, and delete view templates.
- Handle view template compatibility through Revit's own validation APIs.
- Save named selections as `SelectionFilterElement`.
- Load/list/delete saved selections.
- Set the active Revit UI selection from explicit element IDs or a saved
  selection.

Out of scope:

- View creation. Existing `view` tools remain responsible for creating views.
- View filter authoring. Existing `graphics` tools remain responsible for
  `ParameterFilterElement` filters.
- Browser/UI automation outside Revit's `UIDocument.Selection` APIs.
- Persisting selections outside the RVT document.
- Cross-document saved selection transfer.

## Dependencies

- `CLAUDE.md` handler rules: one handler per tool, guarded JSON parse, DTO-only
  results, no raw Revit objects.
- `docs/roadmap-221-tools.md` Wave 12 inventory.
- Existing handler patterns:
  - `src/shared/Handlers/GetCurrentViewHandler.cs`
  - `src/shared/Handlers/GetSelectedElementsHandler.cs`
  - `src/shared/Handlers/CreateViewHandler.cs`
  - `src/shared/Handlers/GetViewVisibilityHandler.cs`
  - `src/shared/Handlers/ApplyFilterToViewHandler.cs`
  - `src/shared/Handlers/SetCategoryVisibilityHandler.cs`
- Wiring patterns:
  - `src/server/Program.cs`
  - `src/shared/Infrastructure/CommandDispatcher.cs`
  - `src/server/ToolsetFilter.cs`
- Cross-version ID helper:
  - `src/shared/Infrastructure/RevitCompat.cs`
- Revit API surfaces verified in local package XML:
  - `View.IsTemplate`
  - `View.CreateViewTemplate()`
  - `View.IsValidViewTemplate(ElementId)`
  - `View.ViewTemplateId`
  - `View.ApplyViewTemplateParameters(View)`
  - `View.GetTemplateParameterIds()`
  - `View.GetNonControlledTemplateParameterIds()`
  - `View.SetNonControlledTemplateParameterIds(ICollection<ElementId>)`
  - `SelectionFilterElement.Create(Document, string)`
  - `SelectionFilterElement.GetElementIds()`
  - `SelectionFilterElement.SetElementIds(ICollection<ElementId>)`
  - `UIDocument.Selection.SetElementIds(ICollection<ElementId>)`
  - `UIDocument.ShowElements(ICollection<ElementId>)`

## Toolset Contract

- Toolset name: `organization`.
- Server wrapper class: `OrganizationTools` in `src/server/Program.cs`.
- Toolset attribute: `[McpServerToolType, Toolset("organization")]`.
- Dispatcher block: add `// Wave 12: Organization` registrations in
  `CommandDispatcher.cs`.
- Toolset gating:
  - Add `organization` to `ToolsetFilter.KnownToolsets`.
  - Add `organization` to `ToolsetFilter.DefaultOn`.
  - Add `organization` to `ToolsetFilter.WriteCapable`.
  - Update `ToolsetFilterTests` expected toolset count and write-capable
    expectations when implementation work reaches tests.
- View identity:
  - External view/template IDs are `long`.
  - Omitted target view IDs use the active view only where the tool explicitly
    says so.
- Selection identity:
  - Saved selections are Revit `SelectionFilterElement` elements stored in the
    active document.
  - Resolve saved selections by `selectionId` when supplied; otherwise by exact
    case-insensitive `name`.
  - Name matches must be unique. If multiple matches are possible, fail instead
    of guessing.
- Active UI selection behavior:
  - Only `select_elements` mutates the active UI selection.
  - `save_selection`, `load_selection`, `list_saved_selections`, and
    `delete_saved_selection` must not alter the current UI selection.
- Transaction behavior:
  - View template and `SelectionFilterElement` document mutations use
    transactions.
  - Active UI selection changes through `UIDocument.Selection.SetElementIds`
    do not require a document transaction.

## Tool Specs

### `list_view_templates`

- Type: read-only.
- Proposed handler: `src/shared/Handlers/ListViewTemplatesHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_view_templates", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListViewTemplates(
    string viewType = "",
    long? viewId = null,
    bool includeSettings = true,
    bool includeUsage = false,
    int limit = 500)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "viewType": {
      "type": "string",
      "description": "Optional ViewType name filter such as FloorPlan, CeilingPlan, ThreeD, Section, DraftingView."
    },
    "viewId": {
      "type": "integer",
      "description": "Optional target view id. When supplied, compatibility is checked with View.IsValidViewTemplate."
    },
    "includeSettings": { "type": "boolean", "default": true },
    "includeUsage": { "type": "boolean", "default": false },
    "limit": { "type": "integer", "default": 500, "minimum": 1, "maximum": 2000 }
  }
}
```

- Response DTO:

```json
{
  "count": 2,
  "returned": 2,
  "targetViewId": 101,
  "targetViewName": "Level 1",
  "templates": [
    {
      "templateId": 2001,
      "name": "A-Floor Plan",
      "viewType": "FloorPlan",
      "isCompatibleWithTarget": true,
      "controlledSettingCount": 42,
      "nonControlledSettingCount": 5,
      "controlledSettings": [
        { "id": -1005162, "name": "View Scale", "builtInParameter": "VIEW_SCALE" }
      ],
      "nonControlledSettings": [
        { "id": -1005110, "name": "View Name", "builtInParameter": "VIEW_NAME" }
      ],
      "appliedToViewCount": 3,
      "appliedToViews": [
        { "viewId": 301, "name": "Level 1", "viewType": "FloorPlan" }
      ]
    }
  ]
}
```

- Revit API strategy:
  - Collect `View` elements with `new FilteredElementCollector(doc).OfClass(typeof(View))`.
  - Keep only `view.IsTemplate`.
  - If `viewType` is supplied, compare against `template.ViewType.ToString()`
    case-insensitively.
  - If `viewId` is supplied, resolve a non-template `View` and call
    `view.IsValidViewTemplate(template.Id)` for every template.
  - If `includeSettings=true`, call `template.GetTemplateParameterIds()` and
    `template.GetNonControlledTemplateParameterIds()`. Controlled settings are
    template parameter IDs minus non-controlled IDs.
  - Resolve setting names by matching each parameter ID against
    `template.Parameters` where possible. If no parameter can be resolved,
    return the numeric ID and null name instead of failing.
  - If `includeUsage=true`, scan all non-template views and compare
    `view.ViewTemplateId` to the template ID.
- Transaction behavior: none.
- Validation and failure modes:
  - Fail if `viewId` is outside the current Revit version ID range.
  - Fail if `viewId` does not resolve to a non-template `View`.
  - Fail if `limit` is outside 1-2000.
- Cross-version notes:
  - `View.GetTemplateParameterIds` and `SetNonControlledTemplateParameterIds`
    are present in local R22 and R27 API docs.
  - Built-in parameter IDs may be negative. Use `RevitCompat.GetId` and do not
    assume positive element IDs.
- Smoke/test expectations:
  - Manual smoke with a floor plan target verifies compatible templates return
    `isCompatibleWithTarget=true`.
  - Manual smoke verifies `includeUsage=true` reports views that currently use a
    template.

### `create_view_template_from_view`

- Type: write-capable, not destructive.
- Proposed handler: `src/shared/Handlers/CreateViewTemplateFromViewHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "create_view_template_from_view", Destructive = false)]
public static async Task<string> CreateViewTemplateFromView(
    string templateName,
    long? sourceViewId = null,
    long[] controlledSettingIds = null,
    long[] nonControlledSettingIds = null,
    bool failIfNameExists = true)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["templateName"],
  "properties": {
    "templateName": { "type": "string" },
    "sourceViewId": {
      "type": "integer",
      "description": "Optional source view id. If omitted, uses the active view."
    },
    "controlledSettingIds": {
      "type": "array",
      "items": { "type": "integer" },
      "description": "Optional exact set of template setting parameter ids to control."
    },
    "nonControlledSettingIds": {
      "type": "array",
      "items": { "type": "integer" },
      "description": "Optional exact set of template setting parameter ids to leave uncontrolled."
    },
    "failIfNameExists": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "templateId": 2001,
  "templateName": "A-Floor Plan",
  "sourceViewId": 101,
  "sourceViewName": "Level 1",
  "viewType": "FloorPlan",
  "controlledSettingCount": 42,
  "nonControlledSettingCount": 5,
  "warnings": []
}
```

- Revit API strategy:
  - Resolve source view from `sourceViewId`; if omitted, use `doc.ActiveView`.
  - Fail if the source is null, not a view, or already a template.
  - Validate `templateName` uniqueness against existing templates.
  - In a transaction, call `sourceView.CreateViewTemplate()`, set `Name`, then
    adjust controlled settings if requested:
    - If `nonControlledSettingIds` is supplied, validate each ID is included in
      `template.GetTemplateParameterIds()` and call
      `template.SetNonControlledTemplateParameterIds(ids)`.
    - If `controlledSettingIds` is supplied, compute
      `allTemplateParameterIds - controlledSettingIds` and pass the result to
      `SetNonControlledTemplateParameterIds`.
    - Supplying both arrays is invalid.
- Transaction behavior:
  - Single transaction: `Bimwright: create view template`.
  - Roll back on duplicate name, invalid setting IDs, or API exception.
  - Check commit status.
- Validation and failure modes:
  - Fail on blank `templateName`, invalid `sourceViewId`, source view not found,
    source is a template, duplicate template name with `failIfNameExists=true`,
    or both setting arrays supplied.
  - If `failIfNameExists=false`, append ` (2)`, ` (3)`, etc. and report the
    actual name.
- Cross-version notes:
  - `CreateViewTemplate()` exists in local R22 and R27 API docs.
  - Some view types may throw on template creation. Return the exception message
    as a clean failure.
- Smoke/test expectations:
  - Manual smoke from an active floor plan creates a template and verifies it is
    listed by `list_view_templates`.
  - Manual smoke with a controlled settings subset verifies non-controlled IDs
    are persisted.

### `apply_view_template`

- Type: write-capable, not destructive.
- Proposed handler: `src/shared/Handlers/ApplyViewTemplateHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "apply_view_template", Destructive = false)]
public static async Task<string> ApplyViewTemplate(
    long templateId,
    long[] viewIds = null,
    string mode = "assign",
    bool replaceExisting = false)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["templateId"],
  "properties": {
    "templateId": { "type": "integer" },
    "viewIds": {
      "type": "array",
      "items": { "type": "integer" },
      "description": "Target views. If omitted, uses the active view."
    },
    "mode": {
      "type": "string",
      "enum": ["assign", "applyParameters"],
      "default": "assign",
      "description": "assign sets ViewTemplateId. applyParameters does a one-time ApplyViewTemplateParameters call."
    },
    "replaceExisting": {
      "type": "boolean",
      "default": false,
      "description": "Required to replace an existing assigned ViewTemplateId in assign mode."
    }
  }
}
```

- Response DTO:

```json
{
  "templateId": 2001,
  "templateName": "A-Floor Plan",
  "mode": "assign",
  "requested": 2,
  "appliedCount": 2,
  "failedCount": 0,
  "results": [
    {
      "viewId": 101,
      "viewName": "Level 1",
      "viewType": "FloorPlan",
      "previousTemplateId": null,
      "previousTemplateName": null,
      "applied": true,
      "error": null
    }
  ]
}
```

- Revit API strategy:
  - Resolve `templateId` to a `View` where `IsTemplate=true`.
  - Resolve targets from `viewIds`; if omitted, use active view.
  - Prevalidate every target:
    - Target must be a non-template `View`.
    - `target.IsValidViewTemplate(template.Id)` must be true.
    - In `assign` mode, if target already has a template and
      `replaceExisting=false`, fail before mutating.
  - In `assign` mode, set `target.ViewTemplateId = template.Id`.
  - In `applyParameters` mode, call `target.ApplyViewTemplateParameters(template)`.
- Transaction behavior:
  - Single all-or-nothing transaction: `Bimwright: apply view template`.
  - Commit status must be checked.
- Validation and failure modes:
  - Fail on invalid template ID, template not found, target not found, target is
    template, incompatible template, empty `viewIds`, unsupported `mode`, or
    existing assigned template without `replaceExisting=true`.
- Cross-version notes:
  - R22-R27 expose `IsValidViewTemplate`, `ViewTemplateId`, and
    `ApplyViewTemplateParameters`.
- Smoke/test expectations:
  - Manual smoke assigns a floor plan template to two floor plans.
  - Manual smoke attempts to assign a floor plan template to an incompatible
    view type and verifies a clean failure with no partial mutation.

### `duplicate_view_template`

- Type: write-capable, not destructive.
- Proposed handler: `src/shared/Handlers/DuplicateViewTemplateHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "duplicate_view_template", Destructive = false)]
public static async Task<string> DuplicateViewTemplate(long templateId, string newName)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["templateId", "newName"],
  "properties": {
    "templateId": { "type": "integer" },
    "newName": { "type": "string" }
  }
}
```

- Response DTO:

```json
{
  "duplicated": true,
  "sourceTemplateId": 2001,
  "sourceTemplateName": "A-Floor Plan",
  "templateId": 2002,
  "templateName": "A-Floor Plan - Copy",
  "viewType": "FloorPlan",
  "controlledSettingCount": 42,
  "nonControlledSettingCount": 5
}
```

- Revit API strategy:
  - Resolve `templateId` to a template `View`.
  - Validate `newName` is unique.
  - In a transaction, call `template.Duplicate(ViewDuplicateOption.Duplicate)`.
  - Resolve the returned ID to `View`, set `Name`, and report setting counts.
- Transaction behavior:
  - Single transaction: `Bimwright: duplicate view template`.
  - Roll back if duplicate creation or renaming fails.
- Validation and failure modes:
  - Fail on invalid ID, not a template, blank `newName`, or duplicate name.
  - If Revit rejects duplication for the template type, return the API exception
    message as a clean failure.
- Cross-version notes:
  - Verify `View.Duplicate` support on templates during build/manual smoke.
- Smoke/test expectations:
  - Manual smoke duplicates a template and verifies both source and copy appear
    in `list_view_templates`.

### `delete_view_template`

- Type: destructive write.
- Proposed handler: `src/shared/Handlers/DeleteViewTemplateHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "delete_view_template", Destructive = true)]
public static async Task<string> DeleteViewTemplate(
    long templateId,
    bool dryRun = true,
    bool clearFromViews = false)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["templateId"],
  "properties": {
    "templateId": { "type": "integer" },
    "dryRun": { "type": "boolean", "default": true },
    "clearFromViews": {
      "type": "boolean",
      "default": false,
      "description": "If true, clears ViewTemplateId from dependent views before deleting."
    }
  }
}
```

- Response DTO:

```json
{
  "dryRun": true,
  "wouldDelete": true,
  "deleted": false,
  "templateId": 2001,
  "templateName": "A-Floor Plan",
  "usedByViewCount": 2,
  "usedByViews": [
    { "viewId": 101, "name": "Level 1", "viewType": "FloorPlan" }
  ],
  "clearFromViews": false,
  "deletedElementIds": []
}
```

- Revit API strategy:
  - Resolve `templateId` to a template `View`.
  - Scan all non-template views where `ViewTemplateId == template.Id`.
  - If `dryRun=true`, return impact only.
  - If the template is in use and `clearFromViews=false`, fail without mutation.
  - If `clearFromViews=true`, set each dependent view's `ViewTemplateId` to
    `ElementId.InvalidElementId`, then call `doc.Delete(template.Id)`.
- Transaction behavior:
  - `dryRun=true`: no transaction.
  - `dryRun=false`: single transaction `Bimwright: delete view template`.
  - Commit status must be checked.
- Validation and failure modes:
  - Fail on invalid ID, template not found, ID not a template, in-use template
    without `clearFromViews=true`, or `doc.Delete` failure.
- Cross-version notes:
  - Use `RevitCompat.GetId(ElementId.InvalidElementId)` only for comparison DTOs;
    assign `ElementId.InvalidElementId` directly.
- Smoke/test expectations:
  - Manual smoke with `dryRun=true` reports dependent views and does not delete.
  - Manual smoke with `clearFromViews=true` clears dependent views and deletes
    the template.

### `save_selection`

- Type: write-capable, not destructive unless replacing an existing saved
  selection.
- Proposed handler: `src/shared/Handlers/SaveSelectionHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "save_selection", Destructive = false)]
public static async Task<string> SaveSelection(
    string name,
    long[] elementIds = null,
    bool replaceExisting = false,
    bool useActiveSelectionIfIdsOmitted = true)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["name"],
  "properties": {
    "name": { "type": "string" },
    "elementIds": {
      "type": "array",
      "items": { "type": "integer" },
      "description": "Optional element IDs. If omitted, the current UI selection is used."
    },
    "replaceExisting": { "type": "boolean", "default": false },
    "useActiveSelectionIfIdsOmitted": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "saved": true,
  "created": true,
  "replaced": false,
  "selectionId": 4001,
  "name": "Level 1 Doors",
  "source": "activeSelection",
  "count": 3,
  "elementIds": [101, 102, 103],
  "missingIds": []
}
```

- Revit API strategy:
  - Require `UIDocument`.
  - If `elementIds` is omitted and `useActiveSelectionIfIdsOmitted=true`, read
    `uidoc.Selection.GetElementIds()`.
  - If `elementIds` is supplied, validate every ID and resolve to existing
    elements.
  - Resolve an existing `SelectionFilterElement` by exact name.
  - If existing and `replaceExisting=false`, fail.
  - In a transaction, create `SelectionFilterElement.Create(doc, name)` or reuse
    the existing element, then call `SetElementIds`.
- Transaction behavior:
  - Single transaction: `Bimwright: save selection`.
  - Check commit status.
  - Do not alter active UI selection.
- Validation and failure modes:
  - Fail on blank name, omitted IDs with active selection disabled, empty
    resolved selection, invalid ID range, missing element, duplicate name without
    replace, or name rejected by Revit.
- Cross-version notes:
  - `SelectionFilterElement` API is present in local R22 and R27 docs.
- Smoke/test expectations:
  - Manual smoke saves current UI selection without passing IDs.
  - Manual smoke saves supplied IDs while a different UI selection is active and
    verifies the active UI selection did not change.

### `load_selection`

- Type: read-only.
- Proposed handler: `src/shared/Handlers/LoadSelectionHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "load_selection", ReadOnly = true, Idempotent = true)]
public static async Task<string> LoadSelection(
    string name = "",
    long? selectionId = null,
    bool includeElementSummary = false)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string" },
    "selectionId": { "type": "integer" },
    "includeElementSummary": { "type": "boolean", "default": false }
  }
}
```

- Response DTO:

```json
{
  "selectionId": 4001,
  "name": "Level 1 Doors",
  "count": 3,
  "elementIds": [101, 102, 103],
  "staleIds": [],
  "elements": [
    { "elementId": 101, "name": "Door 101", "category": "Doors", "typeName": "0915 x 2134mm" }
  ]
}
```

- Revit API strategy:
  - Resolve by `selectionId` if supplied; otherwise by `name`.
  - Cast to `SelectionFilterElement`.
  - Call `GetElementIds()`.
  - If `includeElementSummary=true`, resolve each element and return the same
    lightweight shape as `GetSelectedElementsHandler`.
  - Report IDs that no longer resolve as `staleIds`.
- Transaction behavior: none.
- Validation and failure modes:
  - Require exactly one identity: `selectionId` or `name`.
  - Fail if ID is not a `SelectionFilterElement`, name does not match, or name
    match is ambiguous.
  - Do not mutate active UI selection.
- Cross-version notes:
  - Use `RevitCompat.GetId` for IDs returned by `GetElementIds()`.
- Smoke/test expectations:
  - Manual smoke loads a saved selection and verifies the current UI selection
    remains unchanged.

### `list_saved_selections`

- Type: read-only.
- Proposed handler: `src/shared/Handlers/ListSavedSelectionsHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_saved_selections", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListSavedSelections(
    string nameFilter = "",
    bool includeElementIds = false,
    bool includeElementSummary = false,
    int limit = 500)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "nameFilter": { "type": "string" },
    "includeElementIds": { "type": "boolean", "default": false },
    "includeElementSummary": { "type": "boolean", "default": false },
    "limit": { "type": "integer", "default": 500, "minimum": 1, "maximum": 2000 }
  }
}
```

- Response DTO:

```json
{
  "count": 2,
  "returned": 2,
  "selections": [
    {
      "selectionId": 4001,
      "name": "Level 1 Doors",
      "count": 3,
      "staleCount": 0,
      "elementIds": [101, 102, 103],
      "elements": null
    }
  ]
}
```

- Revit API strategy:
  - Collect `SelectionFilterElement` through `FilteredElementCollector`.
  - Filter by case-insensitive substring when `nameFilter` is supplied.
  - Use `GetElementIds()` for member counts.
  - Resolve summaries only when requested.
- Transaction behavior: none.
- Validation and failure modes:
  - Fail if `limit` is outside 1-2000.
  - Skip no elements silently except stale member IDs, which must be counted.
- Cross-version notes:
  - Use `RevitCompat.GetId` for filter IDs and member IDs.
- Smoke/test expectations:
  - Manual smoke lists at least one saved selection created by `save_selection`.
  - Manual smoke with `includeElementIds=false` keeps response compact.

### `delete_saved_selection`

- Type: destructive write.
- Proposed handler: `src/shared/Handlers/DeleteSavedSelectionHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "delete_saved_selection", Destructive = true)]
public static async Task<string> DeleteSavedSelection(
    string name = "",
    long? selectionId = null,
    bool dryRun = true)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string" },
    "selectionId": { "type": "integer" },
    "dryRun": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "dryRun": true,
  "wouldDelete": true,
  "deleted": false,
  "selectionId": 4001,
  "name": "Level 1 Doors",
  "count": 3,
  "deletedElementIds": []
}
```

- Revit API strategy:
  - Resolve the `SelectionFilterElement` by ID or name.
  - If `dryRun=true`, return impact only.
  - If `dryRun=false`, call `doc.Delete(selection.Id)`.
- Transaction behavior:
  - `dryRun=true`: no transaction.
  - `dryRun=false`: single transaction `Bimwright: delete saved selection`.
  - Check commit status.
  - Do not alter active UI selection.
- Validation and failure modes:
  - Require exactly one identity: `selectionId` or `name`.
  - Fail if the resolved element is not a `SelectionFilterElement`.
- Cross-version notes:
  - `doc.Delete` returns deleted IDs. Convert with `RevitCompat.GetId`.
- Smoke/test expectations:
  - Manual smoke with `dryRun=true` leaves the selection listed.
  - Manual smoke with `dryRun=false` removes it from
    `list_saved_selections`.

### `select_elements`

- Type: UI write-capable, not a document transaction.
- Proposed handler: `src/shared/Handlers/SelectElementsHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "select_elements", Destructive = false)]
public static async Task<string> SelectElements(
    long[] elementIds = null,
    string savedSelectionName = "",
    long? savedSelectionId = null,
    bool clearExisting = true,
    bool zoomToSelection = false)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "elementIds": { "type": "array", "items": { "type": "integer" } },
    "savedSelectionName": { "type": "string" },
    "savedSelectionId": { "type": "integer" },
    "clearExisting": { "type": "boolean", "default": true },
    "zoomToSelection": { "type": "boolean", "default": false }
  }
}
```

- Response DTO:

```json
{
  "selected": true,
  "source": "savedSelection",
  "clearExisting": true,
  "zoomToSelection": false,
  "previousCount": 1,
  "selectedCount": 3,
  "elementIds": [101, 102, 103],
  "missingIds": [],
  "warnings": []
}
```

- Revit API strategy:
  - Require `UIDocument`.
  - Resolve target IDs from exactly one source:
    - explicit `elementIds`;
    - `savedSelectionId`;
    - `savedSelectionName`.
  - Validate each target ID is representable and resolves to an existing
    element in the active document.
  - If `clearExisting=false`, union target IDs with
    `uidoc.Selection.GetElementIds()`.
  - If explicit `elementIds` is an empty array and `clearExisting=true`, call
    `SetElementIds` with an empty collection to clear selection. This is the
    only empty-list success case.
  - Call `uidoc.Selection.SetElementIds(finalIds)`.
  - If `zoomToSelection=true` and final ID set is not empty, call
    `uidoc.ShowElements(finalIds)` and report any zoom failure as a warning
    after selection succeeds.
- Transaction behavior:
  - No `Transaction`; this mutates Revit UI selection state only.
- Validation and failure modes:
  - Fail when no ID source is supplied.
  - Fail when more than one ID source is supplied.
  - Fail on invalid ID range, missing element, stale saved selection member,
    ambiguous saved selection name, or `clearExisting=false` with explicit empty
    array.
  - Do not partially set a valid subset if any supplied ID is invalid.
- Cross-version notes:
  - Use `RevitCompat.ToElementId` after range checks.
  - UI selection API is available through `UIDocument.Selection` across R22-R27.
- Smoke/test expectations:
  - Manual smoke selects explicit element IDs and verifies
    `get_selected_elements` returns them.
  - Manual smoke selects a saved selection by name.
  - Manual smoke clears selection with `elementIds=[]` and
    `clearExisting=true`.

## Wiring

Implementation must update the following files when the wave is built:

- `src/shared/Infrastructure/CommandDispatcher.cs`
  - Add ten handler registrations in a `Wave 12: Organization` block.
- `src/server/Program.cs`
  - Add `OrganizationTools` with one `[McpServerTool]` method per tool.
  - Wrapper methods call `ToolGateway.SendToRevit("<tool_name>", new { ... })`
    and return `JsonConvert.SerializeObject(result, Formatting.Indented)`.
- `src/server/ToolsetFilter.cs`
  - Add `organization` to `KnownToolsets`, `DefaultOn`, and `WriteCapable`.
- `tests/RvtMcp.Tests/ToolsetFilterTests.cs`
  - Update hardcoded known toolset count and write-capable expectations.
- Golden snapshots:
  - Refresh `tests/RvtMcp.Tests/Golden/tools-list.json`.
  - Refresh `tests/RvtMcp.Tests/Golden/tools-list-adaptive-bake.json`.
- Public docs after implementation:
  - Update README/tool tables and `CHANGELOG.md` in the implementation PR.

## Acceptance Criteria

- All ten MCP wrappers appear under `Toolset("organization")`.
- Default configuration exposes `organization`; `--read-only` removes it
  because the toolset is write-capable.
- `list_view_templates` reports template compatibility with a supplied target
  view through `View.IsValidViewTemplate`.
- `create_view_template_from_view` creates a real Revit template view and can
  control/non-control explicit template setting IDs.
- `apply_view_template` prevents incompatible application and avoids partial
  mutations across a batch.
- `delete_view_template` defaults to `dryRun=true` and warns about dependent
  views.
- Saved selections are real `SelectionFilterElement` document elements.
- `load_selection`, `list_saved_selections`, and `delete_saved_selection` do
  not alter the active UI selection.
- `select_elements` is the only tool in this wave that mutates active UI
  selection and it does so without opening a document transaction.

## Review Checklist

- View template handlers distinguish template views from ordinary views.
- Compatibility checks use `View.IsValidViewTemplate`; no manual view-type-only
  compatibility shortcut decides success.
- Template setting IDs are validated against `GetTemplateParameterIds` before
  calling `SetNonControlledTemplateParameterIds`.
- Batch template application is all-or-nothing.
- Deleting templates cannot silently detach views unless `clearFromViews=true`.
- Saved selections use `SelectionFilterElement`, not an external sidecar file.
- Selection name resolution fails ambiguous matches.
- `select_elements` validates every target element before changing UI
  selection.
- Active UI selection tools require `UIDocument` and return clean failures when
  no UI document is available.
- No handler serializes raw Revit API objects.
- No handler uses `.IntegerValue` or `.Value` directly for element IDs.

## Known Risks

- View template controlled setting IDs are difficult to display with friendly
  names because many IDs are built-in parameter IDs. The DTO must preserve IDs
  even when names are null.
- `View.Duplicate(ViewDuplicateOption.Duplicate)` behavior for template views
  should be verified in live Revit across at least one pre-2024 and one
  post-2024 shell.
- `ApplyViewTemplateParameters` is a one-time copy and does not preserve a
  persistent template relationship. The `mode` field must make this explicit.
- Deleting a template used by views is high-impact. Keep `dryRun=true` by
  default and require `clearFromViews=true` before detaching dependent views.
- UI selection cannot be meaningfully proven by non-Revit unit tests. Manual
  smoke with `get_selected_elements` is required.

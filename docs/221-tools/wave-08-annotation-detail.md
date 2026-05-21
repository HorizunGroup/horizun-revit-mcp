# Wave 08 - Annotation / Detail Target Implementation Spec

## Status

Pending implementation. This spec defines the target state for Wave 8 only.

Toolset: `annotation`

Tool count: 12 new tools. Existing tools in this toolset remain:

- `tag_all_walls`
- `tag_all_rooms`

Default state after Wave 8: enabled by default.

Write capability: write-capable. Most tools create or modify annotation/detail elements. Read-only tools must be explicitly marked read-only in MCP wrappers.

Primary objective: expose safe view-specific tagging, text, dimensions, detail graphics, callouts, keynote assignment, and cleanup helpers while validating view compatibility, references, tag families, and dry-run behavior.

## Scope

Implement these tools:

| Tool | Type | Proposed handler file |
|---|---|---|
| `tag_elements` | W | `src/shared/Handlers/TagElementsHandler.cs` |
| `tag_all_by_category` | W | `src/shared/Handlers/TagAllByCategoryHandler.cs` |
| `create_text_note` | W | `src/shared/Handlers/CreateTextNoteHandler.cs` |
| `create_dimensions` | W | `src/shared/Handlers/CreateDimensionsHandler.cs` |
| `create_filled_region` | W | `src/shared/Handlers/CreateFilledRegionHandler.cs` |
| `create_detail_line` | W | `src/shared/Handlers/CreateDetailLineHandler.cs` |
| `create_callout_view` | W | `src/shared/Handlers/CreateCalloutViewHandler.cs` |
| `list_keynotes` | R | `src/shared/Handlers/ListKeynotesHandler.cs` |
| `apply_keynote_to_element` | W | `src/shared/Handlers/ApplyKeynoteToElementHandler.cs` |
| `find_untagged_elements` | R | `src/shared/Handlers/FindUntaggedElementsHandler.cs` |
| `find_undimensioned_elements` | R | `src/shared/Handlers/FindUndimensionedElementsHandler.cs` |
| `wipe_empty_tags` | W | `src/shared/Handlers/WipeEmptyTagsHandler.cs` |

Out of scope:

- Automatic placement optimization beyond simple default offsets.
- Full stable-reference authoring for arbitrary geometry faces.
- Deleting non-tag annotation in cleanup tools.
- Loading missing annotation families from disk.
- Editing external keynote files.

## Dependencies

- Follow `CLAUDE.md` conventions: handler-per-tool, DTO-only returns, guarded JSON parsing, `CommandResult.Ok/Fail`, and `RevitCompat`.
- Existing annotation handlers are minimal (`TagAllWallsHandler`, `TagAllRoomsHandler`). New handlers should use stricter validation and must not copy their silent catch-and-skip behavior without reporting failures.
- Use existing view/graphics/create handler patterns for view resolution, transactions, category resolution, id parsing, and mm-to-feet conversion.
- Wire commands in `src/shared/Infrastructure/CommandDispatcher.cs`.
- Add wrappers to the existing `[McpServerToolType, Toolset("annotation")]` class in `src/server/Program.cs`.
- Promote `annotation` in `src/server/ToolsetFilter.cs`:
  - Add to `DefaultOn`.
  - Add to `WriteCapable`.
  - It already appears in `KnownToolsets`; verify before editing.

Shared validation helpers recommended inside handlers:

- `ResolveView(Document doc, long? viewId)` defaulting to active view.
- `EnsureViewCanHostAnnotation(View view)` rejects schedules, legends where unsupported, templates, 3D views for 2D detail tools, sheets where a tool requires model/detail view, and null active view.
- `ResolveCategory(Document doc, string categoryName)` by exact category name first, then built-in category fallback.
- `ResolveFamilySymbol(Document doc, long? typeId, BuiltInCategory tagCategory, string typeName)` for tag/text/detail family validation.
- `TryReadIdArray(...)` with `RevitCompat.CanRepresentElementId`.
- `ToFeetPoint2d/3d(...)` for mm input conversion.

## Toolset Contract

All annotation/detail handlers must:

- Return DTO-only responses.
- Use guarded JSON parse.
- Use `RevitCompat` for every ElementId conversion/report.
- Use one transaction per mutating command, with rollback on command-level failure.
- Continue per-element in bulk commands where safe, but report every skipped/failed item.
- Validate target view type before opening a transaction where possible.
- Validate tag family/type compatibility before creating tags.
- Validate references before creating dimensions.
- Validate that an element is visible in the target view for tools that operate on visible annotation.
- Use `dry_run` for delete-like cleanup and preview-capable bulk operations where specified.

View validation rules:

- `tag_elements`, `tag_all_by_category`, `create_dimensions`, `create_detail_line`, `create_filled_region`, `create_text_note`, and `wipe_empty_tags` require a non-template view that supports annotation creation.
- `create_callout_view` requires a parent view type that supports callouts. Reject schedules, sheets, legends, templates, and unsupported 3D contexts.
- `find_untagged_elements` and `find_undimensioned_elements` should work only in a target view and must state that they evaluate visible elements in that view.

Reference validation rules:

- Dimensions need valid geometric references. Do not create dimensions from arbitrary element ids unless a supported reference can be derived.
- Supported initial reference modes:
  - `wall_centerline`: wall `LocationCurve` references where available.
  - `grid_curve`: grid curve reference.
  - `explicit_reference_stable`: stable representation strings parsed via `Reference.ParseFromStableRepresentation`.
  - `element_bbox_points`: fallback only when creating detail-line measurement aids, not real Revit dimensions.
- If a true Revit `ReferenceArray` cannot be built, fail cleanly.

Tag family validation rules:

- If `tag_type_id` is supplied, it must resolve to `FamilySymbol`.
- Tag type category must match the target tag category for requested mode:
  - Wall tags: `OST_WallTags`
  - Room tags: `OST_RoomTags`
  - Door tags: `OST_DoorTags`
  - Window tags: `OST_WindowTags`
  - Generic category tags: category-specific tag category where Revit exposes one; otherwise use `IndependentTag.Create` by category with default loaded tag.
- Activate inactive tag symbols inside the transaction before use.
- If no compatible tag family is loaded, fail before attempting to tag.

Dry-run behavior:

- `wipe_empty_tags` defaults to `dry_run = true`.
- `tag_all_by_category` should support `dry_run = false` default, but when `dry_run = true`, return the elements that would be tagged without creating tags.
- `apply_keynote_to_element` should support `dry_run = false` default; when true, validate elements/parameter writeability and return proposed changes without setting parameters.
- Any cleanup/delete-like behavior must preview ids and counts under dry run and must not open a transaction when `dry_run = true`.

## Tool Specs

### `tag_elements`

Proposed handler file: `src/shared/Handlers/TagElementsHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "tag_elements", Destructive = false)]
public static async Task<string> TagElements(long[] elementIds, long? viewId = null, long? tagTypeId = null, string orientation = "Horizontal", bool leader = false, double offsetX = 0, double offsetY = 0)
```

Send payload:

```csharp
new { element_ids = elementIds, view_id = viewId, tag_type_id = tagTypeId, orientation, leader, offset_x = offsetX, offset_y = offsetY }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_ids"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 },
    "view_id": { "type": "integer" },
    "tag_type_id": { "type": "integer" },
    "orientation": { "type": "string", "enum": ["Horizontal", "Vertical"], "default": "Horizontal" },
    "leader": { "type": "boolean", "default": false },
    "offset_x": { "type": "number", "default": 0, "description": "Tag offset in mm from element location/bbox center." },
    "offset_y": { "type": "number", "default": 0, "description": "Tag offset in mm from element location/bbox center." }
  }
}
```

Response DTO:

```json
{
  "tagged": true,
  "view_id": 100,
  "view_name": "Level 1",
  "requested": 2,
  "created": 1,
  "tags": [
    {
      "tag_id": 501,
      "element_id": 101,
      "tag_type_id": 300,
      "tag_type_name": "Wall Tag",
      "head_position": { "unit": "mm", "x": 1000.0, "y": 2000.0, "z": 0.0 }
    }
  ],
  "failed": [
    { "element_id": 102, "error": "Element is not visible in target view." }
  ],
  "error": null
}
```

Revit API strategy:

- Resolve target view, default active view.
- Validate view supports independent tags and is not a template.
- Resolve each target element and verify it is visible in the view using a view-scoped collector or `view.CanCategoryBeHidden`/bbox visibility heuristics where direct check is unavailable.
- Resolve tag type if supplied; otherwise use category-compatible default loaded tag where Revit can create by category.
- Compute tag head point from `LocationPoint`, midpoint of `LocationCurve`, or bbox center plus offsets.
- Create `IndependentTag.Create(doc, view.Id, new Reference(element), leader, TagMode.TM_ADDBY_CATEGORY, orientation, point)`.
- If a tag type was supplied and created tag supports type change, call `ChangeTypeId(tagType.Id)`.

Transaction behavior:

- One transaction: `Bimwright: tag elements`.
- Continue per element on recoverable failures.
- Roll back transaction only for command-level failures such as invalid view or no compatible tag family.

Validation/failure modes:

- Fail missing/empty `element_ids`.
- Fail incompatible `tag_type_id`.
- Per-element failure for not found, not visible, unsupported category, no tag reference, or Revit creation exception.

Cross-version notes:

- `IndependentTag.Create` overload availability differs. Use the overload available in R22 baseline and guarded compile constants/reflection only if necessary.
- `TagOrientation` values are stable for horizontal/vertical.

Smoke/test expectations:

- Tag one wall in plan view.
- Tag type mismatch fails before transaction.
- Unsupported view type fails cleanly.

### `tag_all_by_category`

Proposed handler file: `src/shared/Handlers/TagAllByCategoryHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "tag_all_by_category", Destructive = false)]
public static async Task<string> TagAllByCategory(string category, long? viewId = null, long? tagTypeId = null, bool skipExisting = true, bool leader = false, bool dryRun = false, int limit = 200)
```

Send payload:

```csharp
new { category, view_id = viewId, tag_type_id = tagTypeId, skip_existing = skipExisting, leader, dry_run = dryRun, limit }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["category"],
  "properties": {
    "category": { "type": "string" },
    "view_id": { "type": "integer" },
    "tag_type_id": { "type": "integer" },
    "skip_existing": { "type": "boolean", "default": true },
    "leader": { "type": "boolean", "default": false },
    "dry_run": { "type": "boolean", "default": false },
    "limit": { "type": "integer", "default": 200, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "dry_run": false,
  "category": "Walls",
  "view_id": 100,
  "view_name": "Level 1",
  "visible_count": 20,
  "already_tagged": 5,
  "candidate_count": 15,
  "created": 15,
  "tags": [
    { "tag_id": 501, "element_id": 101 }
  ],
  "failed": [],
  "truncated": false,
  "error": null
}
```

Revit API strategy:

- Resolve category by human category name.
- Collect non-type elements with `FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id)`.
- Detect existing tags in the same view by collecting `IndependentTag` and comparing tagged local element ids where API supports it.
- Reuse `tag_elements` internal placement rules.

Transaction behavior:

- No transaction for `dry_run = true`.
- One transaction for actual creation.
- Per-element failures do not abort unless no compatible tag family exists.

Validation/failure modes:

- Fail category unresolved.
- Fail unsupported target view.
- Fail if `limit > 500`.
- Return truncation metadata when candidates exceed limit.

Cross-version notes:

- Tagged element id access differs across Revit versions. Implement helper using available `IndependentTag` methods/properties and reflection fallback if needed.

Smoke/test expectations:

- Dry run reports candidates and creates no tags.
- Actual run creates tags and skipExisting avoids duplicates on second run.

### `create_text_note`

Proposed handler file: `src/shared/Handlers/CreateTextNoteHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "create_text_note", Destructive = false)]
public static async Task<string> CreateTextNote(string text, double x, double y, long? viewId = null, long? textTypeId = null, double width = 0, double rotationDeg = 0)
```

Send payload:

```csharp
new { text, x, y, view_id = viewId, text_type_id = textTypeId, width, rotation_deg = rotationDeg }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["text", "x", "y"],
  "properties": {
    "text": { "type": "string" },
    "x": { "type": "number" },
    "y": { "type": "number" },
    "view_id": { "type": "integer" },
    "text_type_id": { "type": "integer" },
    "width": { "type": "number", "default": 0, "description": "Optional wrapping width in mm. 0 means unwrapped." },
    "rotation_deg": { "type": "number", "default": 0 }
  }
}
```

Response DTO:

```json
{
  "created": true,
  "text_note_id": 501,
  "view_id": 100,
  "text_type_id": 300,
  "position": { "unit": "mm", "x": 1000.0, "y": 2000.0, "z": 0.0 },
  "width": 1200.0,
  "rotation_deg": 0.0,
  "error": null
}
```

Revit API strategy:

- Resolve target view.
- Resolve `TextNoteType` by id if supplied; otherwise use first available.
- Use `TextNote.Create(doc, view.Id, XYZ, text, textType.Id)` for unwrapped or `TextNoteOptions`/width overload where available.
- Set rotation if supported via `TextNote.Coord`/element transform API; otherwise fail if nonzero rotation cannot be applied.

Transaction behavior: one transaction.

Validation/failure modes:

- Fail empty text.
- Fail unsupported view.
- Fail missing text note type.
- Fail invalid width < 0.

Cross-version notes:

- `TextNote.Create` overloads differ. Use R22-compatible overloads first.

Smoke/test expectations:

- Creates text note in plan view.
- Invalid text type id fails.
- Unsupported view fails.

### `create_dimensions`

Proposed handler file: `src/shared/Handlers/CreateDimensionsHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "create_dimensions", Destructive = false)]
public static async Task<string> CreateDimensions(string referencesJson, long? viewId = null, long? dimensionTypeId = null, string lineJson = "")
```

Send payload:

```csharp
new { references = parsedReferences, view_id = viewId, dimension_type_id = dimensionTypeId, line = parsedLine }
```

Wrapper note: `referencesJson` is a JSON array string. `lineJson` is optional JSON object `{ "start": {"x":...,"y":...,"z":...}, "end": {...} }`.

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["references"],
  "properties": {
    "references": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "mode": { "type": "string", "enum": ["wall_centerline", "grid_curve", "explicit_reference_stable"] },
          "element_id": { "type": "integer" },
          "stable_reference": { "type": "string" }
        }
      }
    },
    "view_id": { "type": "integer" },
    "dimension_type_id": { "type": "integer" },
    "line": { "type": "object" }
  }
}
```

Response DTO:

```json
{
  "created": true,
  "dimension_id": 700,
  "view_id": 100,
  "dimension_type_id": 301,
  "reference_count": 2,
  "line": { "unit": "mm", "start": {}, "end": {} },
  "resolved_references": [
    { "mode": "grid_curve", "element_id": 101, "resolved": true }
  ],
  "failed_references": [],
  "error": null
}
```

Revit API strategy:

- Resolve view and verify it supports dimensions.
- Resolve at least two references into a `ReferenceArray`.
- For grids, use `Grid.Curve.Reference` where available.
- For walls, use `LocationCurve.Curve.Reference` only if Revit exposes a valid reference; otherwise fail with clear message.
- For stable references, call `Reference.ParseFromStableRepresentation(doc, stableReference)`.
- Dimension line:
  - If supplied, convert from mm to feet.
  - If omitted, infer a line between reference element bbox centers and offset slightly in view plane. If inference cannot be robust, fail and require `line`.
- Create via `doc.Create.NewDimension(view, line, referenceArray)`.
- Change type if `dimension_type_id` supplied.

Transaction behavior: one transaction. Roll back if dimension creation or type change fails.

Validation/failure modes:

- Fail fewer than two references.
- Fail unsupported reference mode.
- Fail when a true Revit reference cannot be resolved.
- Fail if supplied line is zero length.
- Fail invalid dimension type id.

Cross-version notes:

- Stable reference strings are model/version sensitive and should be treated as advanced input.
- Reference availability on curves/faces can vary; do not silently create fake dimensions.

Smoke/test expectations:

- Dimension between two grids.
- Stable reference parse failure reports failed reference.
- Zero-length line fails.

### `create_filled_region`

Proposed handler file: `src/shared/Handlers/CreateFilledRegionHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "create_filled_region", Destructive = false)]
public static async Task<string> CreateFilledRegion(string pointsJson, long? viewId = null, long? filledRegionTypeId = null)
```

Send payload:

```csharp
new { points = parsedPoints, view_id = viewId, filled_region_type_id = filledRegionTypeId }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["points"],
  "properties": {
    "points": {
      "type": "array",
      "items": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } } },
      "minItems": 3
    },
    "view_id": { "type": "integer" },
    "filled_region_type_id": { "type": "integer" }
  }
}
```

Response DTO:

```json
{
  "created": true,
  "filled_region_id": 800,
  "view_id": 100,
  "filled_region_type_id": 302,
  "point_count": 4,
  "area_estimate_m2": 12.5,
  "error": null
}
```

Revit API strategy:

- Resolve drafting/model view that can host detail items.
- Resolve `FilledRegionType` by id or use first available.
- Validate points form a closed non-self-intersecting-ish polygon. At minimum reject fewer than 3, duplicate adjacent points, and zero area.
- Convert points to `CurveLoop` of `Line.CreateBound(...)` in the view plane.
- Create with `FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop })`.

Transaction behavior: one transaction.

Validation/failure modes:

- Fail unsupported view.
- Fail no filled region type.
- Fail invalid polygon/zero area.
- Fail Revit curve loop creation exceptions.

Cross-version notes:

- `FilledRegion.Create` is available in R22-R27.

Smoke/test expectations:

- Creates rectangular filled region.
- Self/zero-area polygon fails before transaction where possible.

### `create_detail_line`

Proposed handler file: `src/shared/Handlers/CreateDetailLineHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "create_detail_line", Destructive = false)]
public static async Task<string> CreateDetailLine(double startX, double startY, double endX, double endY, long? viewId = null, long? lineStyleId = null)
```

Send payload:

```csharp
new { start_x = startX, start_y = startY, end_x = endX, end_y = endY, view_id = viewId, line_style_id = lineStyleId }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["start_x", "start_y", "end_x", "end_y"],
  "properties": {
    "start_x": { "type": "number" },
    "start_y": { "type": "number" },
    "end_x": { "type": "number" },
    "end_y": { "type": "number" },
    "view_id": { "type": "integer" },
    "line_style_id": { "type": "integer" }
  }
}
```

Response DTO:

```json
{
  "created": true,
  "detail_line_id": 900,
  "view_id": 100,
  "line_style_id": 400,
  "start": { "unit": "mm", "x": 0.0, "y": 0.0, "z": 0.0 },
  "end": { "unit": "mm", "x": 1000.0, "y": 0.0, "z": 0.0 },
  "length": 1000.0,
  "error": null
}
```

Revit API strategy:

- Resolve target view.
- Convert two points from mm to feet in view plane.
- Create `Line` and `doc.Create.NewDetailCurve(view, line)`.
- If `line_style_id` supplied, resolve `GraphicsStyle` and assign `detailCurve.LineStyle`.

Transaction behavior: one transaction.

Validation/failure modes:

- Fail zero-length line.
- Fail unsupported view.
- Fail invalid line style id or non-`GraphicsStyle`.

Cross-version notes:

- `NewDetailCurve` stable R22-R27.

Smoke/test expectations:

- Creates detail line in drafting/plan view.
- Rejects 3D/template/schedule views.
- Rejects zero-length line.

### `create_callout_view`

Proposed handler file: `src/shared/Handlers/CreateCalloutViewHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "create_callout_view", Destructive = false)]
public static async Task<string> CreateCalloutView(long parentViewId, double minX, double minY, double maxX, double maxY, long? viewFamilyTypeId = null, string name = "")
```

Send payload:

```csharp
new { parent_view_id = parentViewId, min_x = minX, min_y = minY, max_x = maxX, max_y = maxY, view_family_type_id = viewFamilyTypeId, name }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["parent_view_id", "min_x", "min_y", "max_x", "max_y"],
  "properties": {
    "parent_view_id": { "type": "integer" },
    "min_x": { "type": "number" },
    "min_y": { "type": "number" },
    "max_x": { "type": "number" },
    "max_y": { "type": "number" },
    "view_family_type_id": { "type": "integer" },
    "name": { "type": "string" }
  }
}
```

Response DTO:

```json
{
  "created": true,
  "callout_view_id": 1001,
  "parent_view_id": 100,
  "name": "Callout 1",
  "view_type": "Detail",
  "crop": {
    "unit": "mm",
    "min": { "x": 0.0, "y": 0.0 },
    "max": { "x": 1000.0, "y": 800.0 }
  },
  "error": null
}
```

Revit API strategy:

- Resolve parent `View`.
- Resolve `ViewFamilyType` compatible with detail/callout. If omitted, find first `ViewFamilyType` with `ViewFamily.Detail` or compatible callout type.
- Convert rectangle from mm to feet in parent view coordinates.
- Use `ViewSection.CreateCallout(doc, parentView.Id, viewFamilyType.Id, point1, point2)`.
- Apply name if supplied and unique.

Transaction behavior: one transaction. Roll back on creation/name failure.

Validation/failure modes:

- Fail unsupported parent view.
- Fail invalid/zero-area rectangle.
- Fail no compatible view family type.
- Fail duplicate invalid name with clear error.

Cross-version notes:

- `ViewSection.CreateCallout` is available in R22-R27 but view-family compatibility can vary by template/project.

Smoke/test expectations:

- Creates callout in floor plan.
- Rejects sheet/schedule/template parent.
- Rejects invalid rectangle.

### `list_keynotes`

Proposed handler file: `src/shared/Handlers/ListKeynotesHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "list_keynotes", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListKeynotes(string keyPrefix = "", string search = "", int limit = 200)
```

Send payload:

```csharp
new { key_prefix = keyPrefix, search, limit }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "properties": {
    "key_prefix": { "type": "string" },
    "search": { "type": "string" },
    "limit": { "type": "integer", "default": 200, "maximum": 1000 }
  }
}
```

Response DTO:

```json
{
  "source": "keynote_table",
  "keynote_table_id": 123,
  "returned": 25,
  "limit": 200,
  "truncated": false,
  "keynotes": [
    { "key": "A101", "text": "Concrete wall", "parent_key": "A", "full_path": ["A", "A101"] }
  ],
  "warnings": [],
  "error": null
}
```

Revit API strategy:

- Inspect keynote-related Revit API available in R22. Preferred target is `KeynoteTable.GetKeynoteTable(doc)` and its entries if available.
- If direct table entries are not accessible in a compatible way, fallback to collecting keynote tag/type data and return `source = "limited_project_keynote_references"` with warning.
- Filter by prefix/search and cap results.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail only for invalid parameter shape.
- If no keynote table exists, return empty keynotes plus warning rather than command failure.

Cross-version notes:

- Keynote APIs are less commonly used and may vary. Implementation must verify R22 compile surface before choosing methods.

Smoke/test expectations:

- Empty/no-keynote project returns empty list with warning.
- Project with keynote table returns filtered keys.

### `apply_keynote_to_element`

Proposed handler file: `src/shared/Handlers/ApplyKeynoteToElementHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "apply_keynote_to_element", Destructive = false)]
public static async Task<string> ApplyKeynoteToElement(long[] elementIds, string keynote, bool dryRun = false)
```

Send payload:

```csharp
new { element_ids = elementIds, keynote, dry_run = dryRun }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_ids", "keynote"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 },
    "keynote": { "type": "string" },
    "dry_run": { "type": "boolean", "default": false }
  }
}
```

Response DTO:

```json
{
  "dry_run": false,
  "requested": 2,
  "updated": 1,
  "changes": [
    { "element_id": 101, "old_keynote": "", "new_keynote": "A101", "parameter_name": "Keynote" }
  ],
  "failed": [
    { "element_id": 102, "error": "Keynote parameter is read-only." }
  ],
  "error": null
}
```

Revit API strategy:

- Resolve elements.
- Find keynote parameter by `BuiltInParameter.KEYNOTE_PARAM` if available, otherwise lookup parameter named `Keynote`.
- Validate parameter exists, is not read-only, and storage type is string.
- `dry_run = true`: return proposed changes without transaction.
- Actual run: set parameter to supplied keynote string.

Transaction behavior:

- No transaction for dry run.
- One transaction for actual changes.
- Per-element failures should not abort the whole batch unless transaction commit fails.

Validation/failure modes:

- Fail missing/empty ids.
- Fail empty keynote string.
- Per-element failures for not found, no parameter, read-only parameter, unsupported storage type.

Cross-version notes:

- Built-in keynote parameter naming is stable, but use fallback lookup to handle version/project differences.

Smoke/test expectations:

- Dry run reports proposed changes and no parameter changes.
- Actual run updates writable keynote.
- Read-only/missing parameter reported per element.

### `find_untagged_elements`

Proposed handler file: `src/shared/Handlers/FindUntaggedElementsHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "find_untagged_elements", ReadOnly = true, Idempotent = true)]
public static async Task<string> FindUntaggedElements(string category, long? viewId = null, int limit = 200)
```

Send payload:

```csharp
new { category, view_id = viewId, limit }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["category"],
  "properties": {
    "category": { "type": "string" },
    "view_id": { "type": "integer" },
    "limit": { "type": "integer", "default": 200, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "category": "Walls",
  "view_id": 100,
  "view_name": "Level 1",
  "visible_count": 20,
  "tagged_count": 15,
  "untagged_count": 5,
  "returned": 5,
  "limit": 200,
  "truncated": false,
  "untagged": [
    { "element_id": 101, "name": "Wall", "category": "Walls" }
  ],
  "error": null
}
```

Revit API strategy:

- Resolve category and view.
- Collect visible elements in the category with a view-scoped collector.
- Collect `IndependentTag` in the view and resolve tagged local element ids.
- Difference visible minus tagged.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail category unresolved.
- Fail unsupported/no view.
- Return warning if tag id resolution API is limited in the current Revit version.

Cross-version notes:

- Tagged element id APIs differ; use compatibility helper/reflection if necessary.

Smoke/test expectations:

- Before and after tagging, count changes as expected.
- Limit/truncation metadata works.

### `find_undimensioned_elements`

Proposed handler file: `src/shared/Handlers/FindUndimensionedElementsHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "find_undimensioned_elements", ReadOnly = true, Idempotent = true)]
public static async Task<string> FindUndimensionedElements(string category, long? viewId = null, int limit = 200)
```

Send payload:

```csharp
new { category, view_id = viewId, limit }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["category"],
  "properties": {
    "category": { "type": "string" },
    "view_id": { "type": "integer" },
    "limit": { "type": "integer", "default": 200, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "category": "Walls",
  "view_id": 100,
  "view_name": "Level 1",
  "visible_count": 20,
  "dimensioned_count": 12,
  "undimensioned_count": 8,
  "returned": 8,
  "limit": 200,
  "truncated": false,
  "undimensioned": [
    { "element_id": 101, "name": "Wall", "category": "Walls" }
  ],
  "warnings": ["Only dimensions with resolvable references in the target view are considered."],
  "error": null
}
```

Revit API strategy:

- Resolve category/view and collect visible elements.
- Collect `Dimension` elements in target view.
- For each dimension, inspect `Dimension.References` and resolve element ids where possible.
- Difference visible elements minus referenced elements.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail category unresolved.
- Fail unsupported/no view.
- If dimension references cannot be resolved in this Revit version, return warning and conservative result rather than false certainty.

Cross-version notes:

- `Dimension.References` and reference element resolution should be verified on R22.

Smoke/test expectations:

- Grid/wall dimension references reduce undimensioned count.
- Unsupported references produce warning.

### `wipe_empty_tags`

Proposed handler file: `src/shared/Handlers/WipeEmptyTagsHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "wipe_empty_tags", Destructive = true)]
public static async Task<string> WipeEmptyTags(long? viewId = null, bool dryRun = true, int limit = 200)
```

Send payload:

```csharp
new { view_id = viewId, dry_run = dryRun, limit }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "properties": {
    "view_id": { "type": "integer" },
    "dry_run": { "type": "boolean", "default": true },
    "limit": { "type": "integer", "default": 200, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "dry_run": true,
  "view_id": 100,
  "view_name": "Level 1",
  "scanned": 25,
  "empty_count": 3,
  "deleted": 0,
  "would_delete": [
    { "tag_id": 501, "tag_text": "", "owner_view_id": 100, "tagged_element_ids": [101] }
  ],
  "deleted_ids": [],
  "failed": [],
  "truncated": false,
  "error": null
}
```

Revit API strategy:

- Resolve view, default active view.
- Collect `IndependentTag` elements scoped to that view.
- A tag is empty when trimmed tag text is null/empty. Use `IndependentTag.TagText` where available; catch exceptions and report failed inspection.
- Include tagged local element ids where resolvable.
- `dry_run = true`: no transaction, return `would_delete`.
- `dry_run = false`: delete only identified empty tag ids with `doc.Delete`.

Transaction behavior:

- No transaction for dry run.
- One transaction for actual deletion.
- Per-tag delete failure should roll back only if Revit transaction state is invalid; otherwise report failed and continue when possible. If using one `doc.Delete(ICollection<ElementId>)`, catch command-level failure and return error DTO.

Validation/failure modes:

- Fail unsupported/no view.
- Fail `limit > 500`.
- Never delete when `dry_run = true`.
- Return `Destructive = true` wrapper because deletion is possible even though default dry run is safe.

Cross-version notes:

- `IndependentTag.TagText` and tagged element access can vary; verify R22-R27 compile behavior.

Smoke/test expectations:

- Dry run on view with empty tag reports `would_delete` and leaves model unchanged.
- Actual run deletes only empty tags.
- Non-empty tags remain.

## Wiring

Command dispatcher:

- Register the 12 new handlers in a Wave 8 / Annotation block near existing annotation handlers:
  - `TagElementsHandler`
  - `TagAllByCategoryHandler`
  - `CreateTextNoteHandler`
  - `CreateDimensionsHandler`
  - `CreateFilledRegionHandler`
  - `CreateDetailLineHandler`
  - `CreateCalloutViewHandler`
  - `ListKeynotesHandler`
  - `ApplyKeynoteToElementHandler`
  - `FindUntaggedElementsHandler`
  - `FindUndimensionedElementsHandler`
  - `WipeEmptyTagsHandler`

Program.cs:

- Extend the existing `AnnotationTools` class.
- Keep existing `tag_all_walls` and `tag_all_rooms` wrappers.
- Add wrappers for all 12 tools.
- Mark read-only wrappers:
  - `list_keynotes`
  - `find_untagged_elements`
  - `find_undimensioned_elements`
- Mark mutating wrappers `Destructive = false` except:
  - `wipe_empty_tags` must be `Destructive = true` because `dryRun = false` deletes tags.
- For JSON string parameters (`referencesJson`, `lineJson`, `pointsJson`), parse in wrapper and return a plain `"Error: ..."` string on invalid JSON as existing wrapper style does.

ToolsetFilter.cs:

- Ensure `"annotation"` remains in `KnownToolsets`.
- Add `"annotation"` to `DefaultOn`.
- Add `"annotation"` to `WriteCapable`.
- Confirm `--read-only` removes annotation tools after this change.

Golden snapshots/tests:

- Update tool-list golden snapshots only during wave integration.
- Tests should assert annotation is default-on after Wave 8 and absent under read-only mode.

## Acceptance Criteria

- All 12 handler files exist under `src/shared/Handlers/`.
- All new handlers use guarded JSON parsing.
- All new wrappers are present in `AnnotationTools`.
- `annotation` is default-on and write-capable in `ToolsetFilter`.
- All ElementId conversions use `RevitCompat`.
- Every mutating handler uses one transaction and rolls back on command-level failure.
- `wipe_empty_tags` defaults to dry run and does not open a transaction during dry run.
- `tag_all_by_category` dry run creates no tags.
- `apply_keynote_to_element` dry run sets no parameters.
- Tag creation validates target view and compatible tag family/type before creation.
- Dimension creation validates true Revit references and fails instead of creating fake dimensions.
- Read-only analysis tools clearly state they evaluate visible elements in one target view.
- Smoke tests cover one happy path and one validation/dry-run path per tool.

## Review Checklist

- [ ] No raw Revit API object returned.
- [ ] No direct `.IntegerValue` or `.Value` ElementId access.
- [ ] View compatibility is checked before transactions.
- [ ] Tag family/type category is checked before tag creation.
- [ ] Inactive symbols are activated inside transactions before use.
- [ ] Bulk operations report per-element failures.
- [ ] Dry-run paths do not start transactions and do not mutate the model.
- [ ] `wipe_empty_tags` is destructive in wrapper metadata despite dry-run default.
- [ ] `annotation` is included in `WriteCapable` so `--read-only` hides it.
- [ ] Existing `tag_all_walls` and `tag_all_rooms` behavior is not regressed.
- [ ] Missing `docs/221-tools/README.md` is resolved or this spec remains the local source of truth for Wave 8.

## Known Risks

- Revit tag APIs and tagged-element id access differ across versions; compatibility helpers may need reflection.
- Some categories do not have loaded tag families or do not support independent tags.
- True dimension references are hard to derive generically; the first implementation should prefer explicit stable references and grids/walls over broad unsupported promises.
- View-specific visibility checks can be approximate for some categories, phases, design options, linked elements, and view templates.
- Keynote table APIs need compile verification on R22 before committing to a direct table traversal implementation.
- Cleanup tools are destructive when dry run is disabled; review must verify the default remains `dry_run = true`.

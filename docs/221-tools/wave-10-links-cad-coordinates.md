# Wave 10 - Links / CAD / Coordinates Target Implementation Spec

## Status

Pending target specification for the `links` toolset.

This wave defines ten new tools from `docs/roadmap-221-tools.md`. The target implementation covers Revit links, CAD links/imports, linked-document read queries, shared coordinate operations, and project base point edits. The implementation must be conservative around file paths, link/import distinctions, loaded/unloaded states, and coordinate operations that affect model geolocation.

## Scope

- Toolset: `links`
- Default: enabled
- Write-capable: yes
- Tools:
  - `list_linked_models`
  - `list_linked_cad`
  - `import_cad_to_view`
  - `link_revit_model`
  - `unload_link`
  - `reload_link`
  - `get_link_elements`
  - `acquire_coordinates_from_link`
  - `publish_coordinates_to_link`
  - `set_project_base_point`

The implementation must distinguish Revit links from CAD links, CAD links from CAD imports, link types from link instances, and host document elements from linked document elements. For file-writing/linking tools, validate paths before starting transactions. For linked-document reads, enforce Revit API limitations: a document returned by `RevitLinkInstance.GetLinkDocument()` is read-only for this toolset and must not be saved, closed, or modified.

Out of scope for this wave: BIM 360/ACC cloud link creation, server path creation, link binding, link monitor-copy workflows, acquiring/publishing coordinates from arbitrary non-link elements, and editing nested linked documents.

## Dependencies

- `CLAUDE.md` command conventions:
  - one handler file per command under `src/shared/Handlers`
  - handlers implement `IRevitCommand`
  - responses use DTOs only
  - MCP wrappers live in `src/server/Program.cs`
  - handlers are registered in `CommandDispatcher`
- Existing handler patterns:
  - `ExportElementsDataHandler.cs` for file path validation style and DTO export conventions
  - `GetElementDetailsHandler.cs` for geometry DTOs, bounding boxes, transforms, and `RevitCompat` id handling
  - `CreateViewHandler.cs` for target-view validation style
  - `ColorElementsHandler.cs` for write transaction result DTOs
- Revit API surface:
  - `RevitLinkType`
  - `RevitLinkInstance`
  - `RevitLinkOptions`
  - `ImportInstance`
  - `CADLinkType`
  - `DWGImportOptions`
  - `Document.Link(...)`
  - `Document.Import(...)`
  - `Document.AcquireCoordinates(ElementId)`
  - `Document.PublishCoordinates(LinkElementId)`
  - `ExternalFileUtils`
  - `ExternalFileReference`
  - `ExternalFileReferenceType`
  - `LinkedFileStatus`
  - `ModelPath`
  - `ModelPathUtils`
  - `ImportPlacement`
  - `BasePoint.GetProjectBasePoint(Document)`
  - `BasePoint.GetSurveyPoint(Document)`
  - `BuiltInParameter.BASEPOINT_EASTWEST_PARAM`
  - `BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM`
  - `BuiltInParameter.BASEPOINT_ELEVATION_PARAM`
  - `BuiltInParameter.BASEPOINT_ANGLETON_PARAM`

## Toolset Contract

Add a new MCP wrapper class in `src/server/Program.cs`:

```csharp
[McpServerToolType, Toolset("links")]
public static class LinksTools
```

Register it in `RegisterToolsets` after the existing domain toolsets. Add `links` to `ToolsetFilter.KnownToolsets`, `DefaultToolsets`, and `WriteCapableToolsets`.

Add all handlers to `CommandDispatcher` in a new links phase, for example:

```csharp
// Phase 18: Linked Models / CAD / Coordinates tools
Register(new Handlers.ListLinkedModelsHandler());
Register(new Handlers.ListLinkedCadHandler());
Register(new Handlers.ImportCadToViewHandler());
Register(new Handlers.LinkRevitModelHandler());
Register(new Handlers.UnloadLinkHandler());
Register(new Handlers.ReloadLinkHandler());
Register(new Handlers.GetLinkElementsHandler());
Register(new Handlers.AcquireCoordinatesFromLinkHandler());
Register(new Handlers.PublishCoordinatesToLinkHandler());
Register(new Handlers.SetProjectBasePointHandler());
```

MCP wrapper methods must serialize snake_case payload keys and call `ToolGateway.SendToRevit`. Read-only wrappers set `ReadOnly=true` and `Idempotent=true`. Link creation, CAD import/link, coordinate, unload, reload, and base point wrappers are write-capable. `reload_link` and `unload_link` are not destructive but are not idempotent in user-visible model/session state.

All element ids exposed to MCP are `long`. Handlers must validate with `RevitCompat.CanRepresentElementId` before constructing `ElementId`.

## Tool Specs

### `list_linked_models`

- Handler file: `src/shared/Handlers/ListLinkedModelsHandler.cs`
- Purpose: list Revit link types and instances, including loaded/unloaded status and external paths.
- MCP wrapper:

```csharp
public static async Task<string> ListLinkedModels(
    bool includeInstances = true,
    bool includeUnloaded = true)
```

- MCP payload:

```json
{
  "include_instances": true,
  "include_unloaded": true
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "include_instances": { "type": "boolean" },
    "include_unloaded": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "total_types": 0,
  "total_instances": 0,
  "links": [
    {
      "link_type_id": 0,
      "name": "",
      "is_loaded": true,
      "linked_file_status": "Loaded",
      "path": "",
      "absolute_path": "",
      "path_type": "absolute|relative|server|cloud|unknown",
      "is_nested": false,
      "attachment_type": "Attachment|Overlay|Unknown",
      "instance_count": 0,
      "instances": [
        {
          "instance_id": 0,
          "name": "",
          "is_loaded": true,
          "transform": {
            "origin": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 },
            "basis_x": { "x": 1.0, "y": 0.0, "z": 0.0 },
            "basis_y": { "x": 0.0, "y": 1.0, "z": 0.0 },
            "basis_z": { "x": 0.0, "y": 0.0, "z": 1.0 }
          }
        }
      ]
    }
  ],
  "warnings": []
}
```

- Revit API strategy:
  - Collect `RevitLinkType` and `RevitLinkInstance`.
  - Use `RevitLinkType.IsLoaded(doc, typeId)` or equivalent available API, plus `GetLinkedFileStatus`.
  - Use `ExternalFileUtils.GetExternalFileReference(doc, linkType.Id)` when possible.
  - Convert `ModelPath` with `ModelPathUtils.ConvertModelPathToUserVisiblePath`.
  - Group instances by type id.
  - Include transform from `RevitLinkInstance.GetTotalTransform()`.
- Transaction behavior: no transaction.
- Validation/failure modes:
  - No links returns empty list.
  - Unloaded links with missing external reference return type information with path warning.
  - `include_unloaded=false` filters unloaded types.
- Cross-version notes:
  - Some link status helpers differ by Revit version; prefer methods present in Revit 2022 and guard newer calls.
  - Cloud/server paths may not convert to a local filesystem path. Keep both user-visible and absolute fields nullable.
- Smoke/test expectations:
  - Loaded Revit link reports one type and at least one instance.
  - Unloaded type appears only when `include_unloaded=true`.
  - Missing file reports non-loaded status and does not fail the command.

### `list_linked_cad`

- Handler file: `src/shared/Handlers/ListLinkedCadHandler.cs`
- Purpose: list CAD imports and CAD links, preserving the link/import distinction.
- MCP wrapper:

```csharp
public static async Task<string> ListLinkedCad(
    bool includeImports = true,
    bool includeLinks = true)
```

- MCP payload:

```json
{
  "include_imports": true,
  "include_links": true
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "include_imports": { "type": "boolean" },
    "include_links": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "total": 0,
  "items": [
    {
      "instance_id": 0,
      "type_id": 0,
      "name": "",
      "is_linked": true,
      "kind": "cad_link|cad_import",
      "linked_file_status": "Loaded|Imported|Missing|Unknown",
      "path": "",
      "absolute_path": "",
      "owner_view_id": 0,
      "owner_view_name": "",
      "view_specific": true,
      "transform": {
        "origin": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 }
      }
    }
  ],
  "counts": { "cad_links": 0, "cad_imports": 0 },
  "warnings": []
}
```

- Revit API strategy:
  - Collect `ImportInstance` elements.
  - Use `ImportInstance.IsLinked` to distinguish linked CAD from imported CAD.
  - Resolve `CADLinkType` from `ImportInstance.GetTypeId()`.
  - Use `ExternalFileUtils.GetExternalFileReference` where available for path/status. Imports may not have an external file reference after import.
  - Include owner view id/name for view-specific imports.
- Transaction behavior: no transaction.
- Validation/failure modes:
  - Fails if both include flags are false.
  - Missing path for imports is not a failure; return `kind=cad_import` and a warning only if a path was expected.
- Cross-version notes:
  - CAD imports and links both use `ImportInstance`; do not classify by type name.
  - `CADLinkType` file reference availability can differ between imported and linked CAD.
- Smoke/test expectations:
  - A linked DWG reports `is_linked=true`.
  - An imported DWG reports `is_linked=false`.
  - Filtering with `include_imports=false` excludes imports.

### `import_cad_to_view`

- Handler file: `src/shared/Handlers/ImportCadToViewHandler.cs`
- Purpose: import or link a DWG/DXF into a target view.
- MCP wrapper:

```csharp
public static async Task<string> ImportCadToView(
    string path,
    long? viewId = null,
    bool link = false,
    string placement = "origin",
    string unit = "default",
    bool thisViewOnly = true,
    bool visibleLayersOnly = true)
```

- MCP payload:

```json
{
  "path": "C:\\models\\site.dwg",
  "view_id": 123,
  "link": false,
  "placement": "origin|center|shared",
  "unit": "default|millimeter|meter|inch|foot",
  "this_view_only": true,
  "visible_layers_only": true
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["path"],
  "properties": {
    "path": { "type": "string" },
    "view_id": { "type": "integer" },
    "link": { "type": "boolean" },
    "placement": { "type": "string", "enum": ["origin", "center", "shared"] },
    "unit": { "type": "string", "enum": ["default", "millimeter", "meter", "inch", "foot"] },
    "this_view_only": { "type": "boolean" },
    "visible_layers_only": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "kind": "cad_link|cad_import",
  "instance_id": 0,
  "type_id": 0,
  "path": "",
  "view": { "element_id": 0, "name": "", "view_type": "" },
  "placement": "origin",
  "this_view_only": true,
  "visible_layers_only": true,
  "linked_file_status": "Loaded|Imported|Unknown",
  "warnings": []
}
```

- Revit API strategy:
  - Validate `path` is rooted, canonicalizable, exists, and has `.dwg` or `.dxf` extension.
  - Resolve target view from `view_id` or active view.
  - Build `DWGImportOptions`:
    - map `placement` to `ImportPlacement`
    - set `ThisViewOnly`
    - set `VisibleLayersOnly`
    - map `unit` only when not `default`
  - If `link=true`, call `doc.Link(path, options, view, out elementId)`.
  - If `link=false`, call `doc.Import(path, options, view, out elementId)`.
  - Resolve resulting `ImportInstance` and return `IsLinked`.
- Transaction behavior:
  - One transaction named `MCP: Import CAD To View`.
  - Commit only when Revit returns success and a valid element id.
- Validation/failure modes:
  - Relative path fails.
  - Missing file fails.
  - Unsupported extension fails.
  - `this_view_only=true` in a 3D view fails before transaction.
  - Invalid placement/unit fails.
  - Revit returning false from `Import`/`Link` fails with no success DTO.
- Cross-version notes:
  - Supported DWG import units and placement enum names should be mapped with guarded parsing for Revit 2022.
  - `ImportPlacement.Shared` requires shared coordinates to be meaningful; return warning if no shared coordinates are configured.
- Smoke/test expectations:
  - Link a small DWG into a floor plan and verify `is_linked=true`.
  - Import the same DWG and verify `is_linked=false`.
  - Relative path and missing path fail before transaction.

### `link_revit_model`

- Handler file: `src/shared/Handlers/LinkRevitModelHandler.cs`
- Purpose: create a Revit link type and one link instance.
- MCP wrapper:

```csharp
public static async Task<string> LinkRevitModel(
    string path,
    string placement = "origin",
    bool relative = false,
    bool reuseExistingType = false)
```

- MCP payload:

```json
{
  "path": "C:\\models\\linked.rvt",
  "placement": "origin|shared",
  "relative": false,
  "reuse_existing_type": false
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["path"],
  "properties": {
    "path": { "type": "string" },
    "placement": { "type": "string", "enum": ["origin", "shared"] },
    "relative": { "type": "boolean" },
    "reuse_existing_type": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "reused_existing_type": false,
  "link_type_id": 0,
  "link_instance_id": 0,
  "path": "",
  "placement": "origin",
  "linked_file_status": "Loaded",
  "warnings": []
}
```

- Revit API strategy:
  - Validate `path` is rooted, canonicalizable, exists, has `.rvt` extension, and is not the active document path.
  - Convert to `ModelPath` with `ModelPathUtils.ConvertUserVisiblePathToModelPath`.
  - If `reuse_existing_type=true`, find existing `RevitLinkType` with the same user-visible or absolute path.
  - Otherwise call `RevitLinkType.Create(doc, modelPath, new RevitLinkOptions(relative))`.
  - Create one instance with `RevitLinkInstance.Create(doc, linkTypeId, ImportPlacement.Origin)` or `ImportPlacement.Shared`.
  - Return link status after creation.
- Transaction behavior:
  - One transaction named `MCP: Link Revit Model`.
  - Roll back if type creation succeeds but instance creation fails.
- Validation/failure modes:
  - Relative path fails for v1. The `relative` flag controls how Revit stores the path after an absolute input path is accepted.
  - Missing file or non-RVT extension fails.
  - Current document path fails.
  - Placement values other than `origin` or `shared` fail because `RevitLinkInstance.Create` supports those placements.
  - Existing link type with same path fails unless `reuse_existing_type=true`.
- Cross-version notes:
  - Link creation APIs require a modifiable document and may load the linked document as part of type creation.
  - Cloud model links are not supported by this local-path contract.
- Smoke/test expectations:
  - Link a local RVT with origin placement and verify type/instance ids.
  - Re-link same path without reuse fails clearly.
  - Re-link same path with reuse creates an additional instance or returns documented reuse behavior.

### `unload_link`

- Handler file: `src/shared/Handlers/UnloadLinkHandler.cs`
- Purpose: unload a Revit link type by type id or instance id.
- MCP wrapper:

```csharp
public static async Task<string> UnloadLink(
    long? linkTypeId = null,
    long? linkInstanceId = null,
    string scope = "all_users")
```

- MCP payload:

```json
{
  "link_type_id": 123,
  "link_instance_id": null,
  "scope": "all_users|local_user"
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "link_type_id": { "type": "integer" },
    "link_instance_id": { "type": "integer" },
    "scope": { "type": "string", "enum": ["all_users", "local_user"] }
  }
}
```

- Response DTO:

```json
{
  "unloaded": true,
  "link_type_id": 0,
  "name": "",
  "scope": "all_users",
  "status_before": "Loaded",
  "status_after": "Unloaded",
  "path": "",
  "warnings": ["This operation can clear the undo history."]
}
```

- Revit API strategy:
  - Resolve `RevitLinkType` directly from `link_type_id` or via `RevitLinkInstance.GetTypeId()`.
  - Read status before unload.
  - For `all_users`, call `RevitLinkType.Unload(...)` with an appropriate callback where required by the API version.
  - For `local_user`, use local unload API only when available and valid for the document/worksharing state; otherwise fail with a scope-specific message.
- Transaction behavior:
  - Do not start a transaction. Revit link unload APIs require all transaction phases to be finished and can clear undo history.
- Validation/failure modes:
  - Exactly one of `link_type_id` or `link_instance_id` should be supplied.
  - Non-link ids fail.
  - Already unloaded link returns success with `unloaded=false` and status unchanged.
  - Nested or non-loadable links fail with status.
- Cross-version notes:
  - `Unload` overloads/callback requirements vary by Revit version; isolate version-specific calls behind a small helper.
  - Local unload behavior is worksharing-dependent.
- Smoke/test expectations:
  - Loaded Revit link unloads and then appears unloaded in `list_linked_models`.
  - Unloaded link returns a non-mutating success.
  - Calling while a transaction is open is never done by the handler.

### `reload_link`

- Handler file: `src/shared/Handlers/ReloadLinkHandler.cs`
- Purpose: reload a Revit link type by type id or instance id.
- MCP wrapper:

```csharp
public static async Task<string> ReloadLink(
    long? linkTypeId = null,
    long? linkInstanceId = null)
```

- MCP payload:

```json
{
  "link_type_id": 123,
  "link_instance_id": null
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "link_type_id": { "type": "integer" },
    "link_instance_id": { "type": "integer" }
  }
}
```

- Response DTO:

```json
{
  "reloaded": true,
  "link_type_id": 0,
  "name": "",
  "status_before": "Unloaded",
  "status_after": "Loaded",
  "path": "",
  "warnings": ["This operation can clear the undo history."]
}
```

- Revit API strategy:
  - Resolve `RevitLinkType`.
  - Read file reference and status before reload.
  - Call `RevitLinkType.Reload()`.
  - Read status after reload.
- Transaction behavior:
  - Do not start a transaction. Reload requires all transaction phases to be finished and can clear undo history.
- Validation/failure modes:
  - Exactly one id selector should be supplied.
  - Missing file fails with linked file status and path.
  - Non-link ids fail.
- Cross-version notes:
  - Reload result types and exceptions differ by version; normalize into `reloaded`, `status_before`, `status_after`, and `warnings`.
- Smoke/test expectations:
  - Previously unloaded link reloads and appears loaded in `list_linked_models`.
  - Missing RVT path fails without crashing.

### `get_link_elements`

- Handler file: `src/shared/Handlers/GetLinkElementsHandler.cs`
- Purpose: read elements from a linked Revit document through a link instance.
- MCP wrapper:

```csharp
public static async Task<string> GetLinkElements(
    long linkInstanceId,
    string category = "",
    int limit = 500,
    bool includeBoundingBox = false)
```

- MCP payload:

```json
{
  "link_instance_id": 123,
  "category": "",
  "limit": 500,
  "include_bounding_box": false
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["link_instance_id"],
  "properties": {
    "link_instance_id": { "type": "integer" },
    "category": { "type": "string" },
    "limit": { "type": "integer", "minimum": 1, "maximum": 5000 },
    "include_bounding_box": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "link_instance": { "element_id": 0, "name": "", "type_id": 0 },
  "linked_document": { "title": "", "path": "", "is_read_only_context": true },
  "transform_to_host": {
    "origin": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 }
  },
  "total": 0,
  "returned": 0,
  "elements": [
    {
      "linked_element_id": 0,
      "linked_unique_id": "",
      "name": "",
      "category": "",
      "family": "",
      "type": "",
      "host_bbox": {
        "min": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 },
        "max": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 }
      }
    }
  ],
  "read_limitations": [
    "Linked documents are read-only in this tool and must not be saved, closed, or modified."
  ],
  "warnings": []
}
```

- Revit API strategy:
  - Resolve `RevitLinkInstance`.
  - Call `GetLinkDocument()`.
  - If null, return failure indicating the link is unloaded or unavailable.
  - Query `FilteredElementCollector(linkDoc).WhereElementIsNotElementType()`.
  - Apply category filter by built-in category name, category display name, or exact category id string.
  - Transform bounding boxes and locations to host coordinates with `linkInstance.GetTotalTransform()`.
- Transaction behavior: no transaction in host or linked document.
- Validation/failure modes:
  - Unloaded link returns failure.
  - Non-link instance id fails.
  - Invalid category returns empty result with warning or fails if the category string matches no known category; choose one behavior and document it in handler tests.
  - Limit range fails.
- Cross-version notes:
  - Linked document modification restrictions are API rules, not version-specific behavior. Never start transactions on `linkDoc`.
  - Bounding box transforms should handle null bounding boxes.
- Smoke/test expectations:
  - Loaded link returns walls or levels when filtered.
  - Unloaded link fails cleanly.
  - Bounding boxes are transformed into host coordinates.

### `acquire_coordinates_from_link`

- Handler file: `src/shared/Handlers/AcquireCoordinatesFromLinkHandler.cs`
- Purpose: acquire shared coordinates from a Revit link instance or linked CAD instance into the host document.
- MCP wrapper:

```csharp
public static async Task<string> AcquireCoordinatesFromLink(
    long linkInstanceId,
    bool confirm = false)
```

- MCP payload:

```json
{
  "link_instance_id": 123,
  "confirm": false
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["link_instance_id"],
  "properties": {
    "link_instance_id": { "type": "integer" },
    "confirm": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "acquired": true,
  "target": {
    "element_id": 0,
    "kind": "revit_link|cad_link",
    "name": ""
  },
  "project_location_before": { "name": "", "id": 0 },
  "project_location_after": { "name": "", "id": 0 },
  "project_base_point_before": { "east_west_mm": 0.0, "north_south_mm": 0.0, "elevation_mm": 0.0, "angle_to_true_north_deg": 0.0 },
  "project_base_point_after": { "east_west_mm": 0.0, "north_south_mm": 0.0, "elevation_mm": 0.0, "angle_to_true_north_deg": 0.0 },
  "warnings": []
}
```

- Revit API strategy:
  - Resolve the id to either `RevitLinkInstance` or linked `ImportInstance`.
  - Reject CAD imports where `ImportInstance.IsLinked=false`.
  - Require `confirm=true`; otherwise return a failure/preflight DTO explaining the coordinate impact.
  - Capture project location and base point before mutation.
  - Call `doc.AcquireCoordinates(elementId)`.
  - Capture project location and base point after mutation.
- Transaction behavior:
  - One transaction named `MCP: Acquire Coordinates From Link`.
  - Commit only after `AcquireCoordinates` succeeds.
- Validation/failure modes:
  - `confirm=false` fails before transaction.
  - Non-link id fails.
  - Unloaded Revit link fails.
  - CAD import, not linked CAD, fails.
  - API exceptions are surfaced with target id and kind.
- Cross-version notes:
  - Revit API supports acquire from Revit link instances and import instances, but behavior depends on whether the import is linked and has usable coordinate data.
  - Shared coordinate updates are project-level and can affect many views/exports.
- Smoke/test expectations:
  - `confirm=false` creates no changes.
  - Acquiring from a test link changes reported base point/project location when link has shared coordinates.
  - CAD import is rejected while CAD link is accepted when Revit supports it.

### `publish_coordinates_to_link`

- Handler file: `src/shared/Handlers/PublishCoordinatesToLinkHandler.cs`
- Purpose: publish host shared coordinates to a linked Revit model.
- MCP wrapper:

```csharp
public static async Task<string> PublishCoordinatesToLink(
    long linkInstanceId,
    long? linkedProjectLocationId = null,
    bool confirm = false)
```

- MCP payload:

```json
{
  "link_instance_id": 123,
  "linked_project_location_id": null,
  "confirm": false
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["link_instance_id"],
  "properties": {
    "link_instance_id": { "type": "integer" },
    "linked_project_location_id": { "type": "integer" },
    "confirm": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "published": true,
  "link_instance": { "element_id": 0, "name": "", "type_id": 0 },
  "linked_project_location_id": 0,
  "linked_document_title": "",
  "linked_document_path": "",
  "warnings": [
    "Publishing coordinates can modify the linked RVT's shared coordinate data."
  ]
}
```

- Revit API strategy:
  - Resolve `RevitLinkInstance`.
  - Reject `ImportInstance`; Revit API publish coordinates does not support DWG/CAD import instances.
  - Require `confirm=true`; otherwise return a preflight failure with target link info.
  - Resolve `LinkElementId` for the link instance and optional linked project location id. If no project location id is supplied, use the linked document active project location when `GetLinkDocument()` is available.
  - Call `doc.PublishCoordinates(linkElementId)`.
- Transaction behavior:
  - One transaction named `MCP: Publish Coordinates To Link`.
  - Commit only after `PublishCoordinates` succeeds.
- Validation/failure modes:
  - `confirm=false` fails before transaction.
  - Non-Revit-link id fails.
  - Unloaded link fails.
  - Invalid linked project location id fails before mutation when the linked document is available.
  - Worksharing, read-only linked file, or permission failures surface as Revit API errors.
- Cross-version notes:
  - `LinkElementId` constructors and project-location handling should be compiled against Revit 2022 and 2026 references.
  - Linked documents obtained through `GetLinkDocument()` are still not directly modified by this handler; the host API call performs the publish.
- Smoke/test expectations:
  - `confirm=false` does not publish.
  - Publishing to a writable local Revit link succeeds and returns target link metadata.
  - Passing a CAD link/import id fails with an explicit unsupported-target message.

### `set_project_base_point`

- Handler file: `src/shared/Handlers/SetProjectBasePointHandler.cs`
- Purpose: set project base point or survey point numeric parameters.
- MCP wrapper:

```csharp
public static async Task<string> SetProjectBasePoint(
    double eastWest,
    double northSouth,
    double elevation = 0,
    double angleToTrueNorth = 0,
    string pointKind = "project_base_point",
    bool dryRun = false)
```

- MCP payload:

```json
{
  "east_west": 0.0,
  "north_south": 0.0,
  "elevation": 0.0,
  "angle_to_true_north": 0.0,
  "point_kind": "project_base_point|survey_point",
  "dry_run": false
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["east_west", "north_south"],
  "properties": {
    "east_west": { "type": "number" },
    "north_south": { "type": "number" },
    "elevation": { "type": "number" },
    "angle_to_true_north": { "type": "number" },
    "point_kind": { "type": "string", "enum": ["project_base_point", "survey_point"] },
    "dry_run": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "dry_run": false,
  "updated": true,
  "point_kind": "project_base_point",
  "element_id": 0,
  "before": {
    "east_west_mm": 0.0,
    "north_south_mm": 0.0,
    "elevation_mm": 0.0,
    "angle_to_true_north_deg": 0.0
  },
  "after": {
    "east_west_mm": 0.0,
    "north_south_mm": 0.0,
    "elevation_mm": 0.0,
    "angle_to_true_north_deg": 0.0
  },
  "warnings": []
}
```

- Revit API strategy:
  - Resolve point:
    - `BasePoint.GetProjectBasePoint(doc)` for `project_base_point`
    - `BasePoint.GetSurveyPoint(doc)` for `survey_point`
  - Read parameters:
    - `BASEPOINT_EASTWEST_PARAM`
    - `BASEPOINT_NORTHSOUTH_PARAM`
    - `BASEPOINT_ELEVATION_PARAM`
    - `BASEPOINT_ANGLETON_PARAM`
  - Convert millimeters to feet and degrees to radians.
  - If `dry_run=true`, return before/after preview without mutation.
  - Set writable parameters in one transaction.
- Transaction behavior:
  - `dry_run=true`: no transaction.
  - `dry_run=false`: one transaction named `MCP: Set Project Base Point`.
- Validation/failure modes:
  - Non-finite numeric values fail.
  - Invalid `point_kind` fails.
  - Missing base point/survey point fails.
  - Read-only parameters fail before partial update when detectable.
  - Transaction rolls back if any parameter set fails.
- Cross-version notes:
  - `BasePoint.GetProjectBasePoint` and `GetSurveyPoint` are available in supported versions, but parameter write behavior can vary in workshared or template-locked documents.
  - Angle storage is radians even though MCP uses degrees.
- Smoke/test expectations:
  - Dry run returns proposed after values and does not modify the model.
  - Setting project base point updates all four parameters.
  - Read-only/workshared restriction returns a clean failure with no partial change.

## Wiring

- Add `LinksTools` to `src/server/Program.cs` with one MCP method per tool.
- Add each tool name to the corresponding handler `Name` property exactly as listed in this spec.
- Add handlers to `CommandDispatcher` in a new links phase after rooms.
- Add `links` to `ToolsetFilter` as known, default-on, and write-capable.
- Keep all JSON payload keys snake_case.
- Validate file paths in handlers, not only in MCP wrappers:
  - reject empty paths
  - reject non-rooted local paths for creation/link/import tools
  - canonicalize with `Path.GetFullPath`
  - reject missing files before transaction
  - enforce `.rvt`, `.dwg`, and `.dxf` extensions per tool
  - reject active document path for `link_revit_model`
- Use helper methods where they reduce duplication:
  - link type/instance resolution
  - external file reference to DTO conversion
  - import placement parsing
  - base point read/write conversion
  - host transform application for linked element DTOs

## Acceptance Criteria

- The `links` toolset is discoverable through MCP when default toolsets are enabled.
- All ten tool names dispatch through `CommandDispatcher`.
- Revit links and CAD references are listed without starting transactions.
- CAD links and CAD imports are distinguished with `ImportInstance.IsLinked`.
- `import_cad_to_view` validates file paths and target view compatibility before mutation.
- `link_revit_model` creates a link type and instance from a valid local RVT path and rejects current-document/self links.
- `unload_link` and `reload_link` call the Revit link APIs without opening transactions.
- `get_link_elements` reads from linked documents without saving, closing, modifying, or transacting against them.
- Coordinate tools require `confirm=true` before mutation.
- `publish_coordinates_to_link` rejects CAD targets.
- `set_project_base_point` supports dry run and rolls back on partial parameter update failure.
- Handler tests cover invalid paths, invalid ids, unloaded links, link/import distinction, and response DTO shape.

## Review Checklist

- Handler file name matches the spec for each tool.
- `Name` property matches the MCP tool name exactly.
- `ParametersSchema` includes required fields, enum values, and numeric ranges.
- MCP wrapper method serializes the documented payload keys.
- No handler returns raw Revit objects.
- All element-id conversions use `RevitCompat`.
- File path validation happens before transaction start.
- Link unload/reload handlers do not start transactions.
- Linked-document reads do not modify, save, or close linked documents.
- Coordinate tools require explicit confirmation.
- All coordinate/base point units are converted explicitly.
- Cross-version compile is considered for Revit 2022 through 2027.
- Smoke tests include loaded links, unloaded links, CAD links, CAD imports, and missing files.

## Known Risks

- Link unload/reload APIs can clear undo history. The tools must warn callers and avoid wrapping those APIs in transactions.
- Shared coordinate operations can change project geolocation and downstream exports. `confirm=false` must be the default for acquire/publish wrappers.
- Cloud, server, and workshared paths do not behave like local filesystem paths. Creation tools should only support local rooted paths in v1; list tools can report whatever Revit exposes.
- Linked document APIs expose a document handle but do not permit normal document-modifying operations. `get_link_elements` must remain read-only.
- CAD import/link behavior depends on DWG unit metadata, active view type, and shared coordinate setup.
- Revit link placement supports only the placement modes exposed by the API for link instances. Do not silently map unsupported placement modes.

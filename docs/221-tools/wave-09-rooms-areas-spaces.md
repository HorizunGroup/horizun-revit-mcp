# Wave 09 - Rooms / Areas / Spaces Target Implementation Spec

## Status

Pending target specification for the `rooms` toolset.

This wave defines ten new tools from `docs/roadmap-221-tools.md`. Existing `create_room` and `export_room_data` handlers are compatibility references only; do not rename or remove them while implementing this wave. The implementation target is an MCP toolset that can inspect and create rooms, areas, spaces, area tags, room separators, and room-finish summaries with DTO-only responses.

## Scope

- Toolset: `rooms`
- Default: enabled
- Write-capable: yes
- Tools:
  - `list_rooms`
  - `get_room_boundaries`
  - `get_room_openings`
  - `create_room_separator`
  - `create_area`
  - `create_space`
  - `list_areas`
  - `compute_room_finishes`
  - `auto_create_rooms_from_walls`
  - `tag_all_areas`

The implementation must explicitly classify placed rooms, unplaced rooms, and not-enclosed rooms. It must handle area schemes, area plans, MEP spaces, wall/floor/ceiling finish parameters, and room separator transactions. External units are millimeters, square meters, cubic meters, and degrees. Internal Revit storage units remain feet, square feet, cubic feet, and radians.

Out of scope for this wave: deleting rooms/areas/spaces, changing existing baseline tool names, generating room schedules, changing area scheme definitions, and creating MEP zones.

## Dependencies

- `CLAUDE.md` command conventions:
  - one handler file per command under `src/shared/Handlers`
  - handlers implement `IRevitCommand`
  - responses use DTOs only
  - MCP wrappers live in `src/server/Program.cs`
  - handlers are registered in `CommandDispatcher`
- Existing handler patterns:
  - `CreateRoomHandler.cs` for room creation, JSON parsing, transaction naming, and unit conversion
  - `ExportRoomDataHandler.cs` for room collection and DTO export
  - `GetElementDetailsHandler.cs` for geometry/location DTOs and `RevitCompat` element-id handling
  - `CreateViewHandler.cs` for view lookup/creation transaction style
  - `ColorElementsHandler.cs` for transaction result DTO style
- Revit API surface:
  - `Autodesk.Revit.DB.Architecture.Room`
  - `Autodesk.Revit.DB.Area`
  - `Autodesk.Revit.DB.Mechanical.Space`
  - `SpatialElement.GetBoundarySegments(SpatialElementBoundaryOptions)`
  - `BoundarySegment.GetCurve()`, `ElementId`, and `LinkElementId`
  - `Document.Create.NewRoom(Level, UV)` and `Document.Create.NewRoom(Phase)`
  - `Document.Create.NewRooms2(...)`
  - `Document.Create.NewRoomBoundaryLines(SketchPlane, CurveArray, View)`
  - `Document.Create.NewArea(ViewPlan, UV)`
  - `ViewPlan.CreateAreaPlan(...)`
  - `Document.Create.NewAreaTag(ViewPlan, Area, UV)`
  - `Document.Create.NewSpace(Level, Phase, UV)` and `Document.Create.NewSpace(Level, UV)`
  - `BuiltInCategory.OST_Rooms`, `OST_Areas`, `OST_MEPSpaces`, `OST_RoomSeparationLines`
  - `CurveElementType.RoomSeparation`

## Toolset Contract

Add a new MCP wrapper class in `src/server/Program.cs`:

```csharp
[McpServerToolType, Toolset("rooms")]
public static class RoomsTools
```

Register it in `RegisterToolsets` after the existing domain toolsets. Add `rooms` to `ToolsetFilter.KnownToolsets`, `DefaultToolsets`, and `WriteCapableToolsets`.

Add all handlers to `CommandDispatcher` in a new rooms phase, for example:

```csharp
// Phase 17: Rooms / Areas / Spaces deep tools
Register(new Handlers.ListRoomsHandler());
Register(new Handlers.GetRoomBoundariesHandler());
Register(new Handlers.GetRoomOpeningsHandler());
Register(new Handlers.CreateRoomSeparatorHandler());
Register(new Handlers.CreateAreaHandler());
Register(new Handlers.CreateSpaceHandler());
Register(new Handlers.ListAreasHandler());
Register(new Handlers.ComputeRoomFinishesHandler());
Register(new Handlers.AutoCreateRoomsFromWallsHandler());
Register(new Handlers.TagAllAreasHandler());
```

MCP wrapper methods must serialize snake_case payload keys and call `ToolGateway.SendToRevit`. Read-only wrappers set `ReadOnly=true` and `Idempotent=true`. Write wrappers set `Destructive=false`; `auto_create_rooms_from_walls` is not idempotent when `dry_run=false`.

All element ids exposed to MCP are `long`. Handlers must validate with `RevitCompat.CanRepresentElementId` before constructing `ElementId`.

## Tool Specs

### `list_rooms`

- Handler file: `src/shared/Handlers/ListRoomsHandler.cs`
- Purpose: list rooms with status classification and optional parameters.
- MCP wrapper:

```csharp
public static async Task<string> ListRooms(
    string levelName = "",
    string phaseName = "",
    string status = "all",
    bool includeParameters = false,
    int limit = 5000)
```

- MCP payload:

```json
{
  "level_name": "optional level name",
  "phase_name": "optional phase name",
  "status": "all|placed|unplaced|not_enclosed",
  "include_parameters": false,
  "limit": 5000
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "level_name": { "type": "string" },
    "phase_name": { "type": "string" },
    "status": { "type": "string", "enum": ["all", "placed", "unplaced", "not_enclosed"] },
    "include_parameters": { "type": "boolean" },
    "limit": { "type": "integer", "minimum": 1, "maximum": 20000 }
  }
}
```

- Response DTO:

```json
{
  "total": 0,
  "returned": 0,
  "filters": { "level_name": "", "phase_name": "", "status": "all" },
  "counts": { "placed": 0, "unplaced": 0, "not_enclosed": 0 },
  "rooms": [
    {
      "element_id": 0,
      "unique_id": "",
      "name": "",
      "number": "",
      "level": { "element_id": 0, "name": "" },
      "phase": { "element_id": 0, "name": "" },
      "status": "placed",
      "location": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 },
      "area_m2": 0.0,
      "perimeter_m": 0.0,
      "volume_m3": 0.0,
      "department": "",
      "occupancy": "",
      "parameters": {}
    }
  ]
}
```

- Revit API strategy:
  - Collect `Room` elements with `FilteredElementCollector(doc).OfCategory(OST_Rooms).WhereElementIsNotElementType()`.
  - Filter by `Level.Name` and `Room.CreatedPhaseId`/phase name when provided.
  - Classify:
    - `placed`: has a `LocationPoint` or valid level, `Area > tolerance`, and at least one boundary loop.
    - `not_enclosed`: has placement context but `Area <= tolerance` or `GetBoundarySegments(...)` returns null/empty.
    - `unplaced`: no usable `Location`, no boundary loops, and no positive area. Include rooms created with `NewRoom(Phase)`.
  - Use `SpatialElementBoundaryOptions` with default finish boundary location.
  - Convert area/perimeter/volume to metric.
- Transaction behavior: no transaction.
- Validation/failure modes:
  - Invalid `status` fails with allowed values.
  - `limit` outside range fails.
  - Missing level/phase filter returns an empty list, not failure.
  - Parameter extraction must ignore unreadable/storage-unsupported parameters instead of failing the whole command.
- Cross-version notes:
  - Use `RevitCompat.GetId` and `GetIdOrNull`.
  - Avoid APIs newer than Revit 2022 unless guarded.
  - Room phase/level parameters may be unavailable for unplaced rooms; return null DTO fields.
- Smoke/test expectations:
  - Model with one placed room, one unplaced room, and one not-enclosed room returns all three statuses.
  - `status=placed` excludes unplaced and not-enclosed rooms.
  - `include_parameters=false` omits the parameters payload.

### `get_room_boundaries`

- Handler file: `src/shared/Handlers/GetRoomBoundariesHandler.cs`
- Purpose: return room boundary loops, boundary elements, and linked boundary references.
- MCP wrapper:

```csharp
public static async Task<string> GetRoomBoundaries(
    long roomId,
    string boundaryLocation = "finish",
    bool includeBoundaryElements = true)
```

- MCP payload:

```json
{
  "room_id": 123,
  "boundary_location": "finish|center|core_center|core_boundary",
  "include_boundary_elements": true
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["room_id"],
  "properties": {
    "room_id": { "type": "integer" },
    "boundary_location": { "type": "string", "enum": ["finish", "center", "core_center", "core_boundary"] },
    "include_boundary_elements": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "room": { "element_id": 0, "name": "", "number": "", "status": "placed" },
  "boundary_location": "finish",
  "loop_count": 0,
  "loops": [
    {
      "loop_index": 0,
      "closed": true,
      "length_m": 0.0,
      "segments": [
        {
          "segment_index": 0,
          "curve_type": "Line",
          "start": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 },
          "end": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 },
          "length_m": 0.0,
          "element_id": 0,
          "link_element_id": null,
          "element_category": "Walls",
          "element_name": ""
        }
      ]
    }
  ],
  "warnings": []
}
```

- Revit API strategy:
  - Resolve `room_id` to `Room`.
  - Map `boundary_location` to `SpatialElementBoundaryLocation`.
  - Call `Room.GetBoundarySegments(options)`.
  - For each `BoundarySegment`, convert curve end points to millimeters. Preserve curve type and tessellated points for arcs if line endpoints are insufficient.
  - Include `BoundarySegment.ElementId` and `BoundarySegment.LinkElementId` when available.
- Transaction behavior: no transaction.
- Validation/failure modes:
  - Invalid id range fails before `ElementId` construction.
  - Missing element or non-room id fails.
  - Unplaced/not-enclosed room succeeds with `loop_count=0` and warning.
  - Unsupported curve geometry returns a simplified tessellation and warning.
- Cross-version notes:
  - `LinkElementId` availability differs by boundary source; return null when invalid.
  - Use guarded enum parsing for boundary location.
- Smoke/test expectations:
  - Rectangular room returns one loop with four segments.
  - Room bounded by linked model reports link element references when Revit exposes them.
  - Not-enclosed room returns an empty loop list and non-fatal warning.

### `get_room_openings`

- Handler file: `src/shared/Handlers/GetRoomOpeningsHandler.cs`
- Purpose: list doors and windows associated with a room.
- MCP wrapper:

```csharp
public static async Task<string> GetRoomOpenings(
    long roomId,
    bool includeDoors = true,
    bool includeWindows = true)
```

- MCP payload:

```json
{
  "room_id": 123,
  "include_doors": true,
  "include_windows": true
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["room_id"],
  "properties": {
    "room_id": { "type": "integer" },
    "include_doors": { "type": "boolean" },
    "include_windows": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "room": { "element_id": 0, "name": "", "number": "" },
  "openings": [
    {
      "element_id": 0,
      "unique_id": "",
      "category": "Doors",
      "family": "",
      "type": "",
      "mark": "",
      "from_room_id": 0,
      "to_room_id": 0,
      "host_id": 0,
      "location": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 },
      "width_mm": 0.0,
      "height_mm": 0.0
    }
  ],
  "counts": { "doors": 0, "windows": 0 },
  "warnings": []
}
```

- Revit API strategy:
  - Resolve room and phase.
  - Collect door/window `FamilyInstance` elements.
  - For doors, compare `FromRoom` and `ToRoom` for the room phase when available.
  - For windows, use `FamilyInstance.Room`, `FromRoom`, `ToRoom`, bounding box intersection, or host-wall boundary adjacency as fallbacks.
  - Return host id, type dimensions, mark, family, and location point.
- Transaction behavior: no transaction.
- Validation/failure modes:
  - Fails if both `include_doors` and `include_windows` are false.
  - Missing room/non-room id fails.
  - Unplaced rooms return no openings with warning.
- Cross-version notes:
  - Room association properties can be phase-sensitive; use room created phase when possible and null-check every property access.
  - Some families do not expose width/height parameters; return null numeric fields or omit values consistently.
- Smoke/test expectations:
  - Door between two rooms appears for both rooms with correct from/to ids.
  - Exterior window in a room is returned when `include_windows=true`.
  - Unplaced room returns an empty opening list without throwing.

### `create_room_separator`

- Handler file: `src/shared/Handlers/CreateRoomSeparatorHandler.cs`
- Purpose: create room separation lines from supplied model-space points.
- MCP wrapper:

```csharp
public static async Task<string> CreateRoomSeparator(
    string points,
    long? viewId = null,
    string levelName = "",
    bool closeLoop = false)
```

The wrapper parses `points` as a JSON array before sending it to Revit.

- MCP payload:

```json
{
  "points": [{ "x": 0.0, "y": 0.0, "z": 0.0 }],
  "view_id": 123,
  "level_name": "Level 1",
  "close_loop": false
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["points"],
  "properties": {
    "points": {
      "type": "array",
      "minItems": 2,
      "items": {
        "type": "object",
        "required": ["x", "y"],
        "properties": {
          "x": { "type": "number" },
          "y": { "type": "number" },
          "z": { "type": "number" }
        }
      }
    },
    "view_id": { "type": "integer" },
    "level_name": { "type": "string" },
    "close_loop": { "type": "boolean" }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "view": { "element_id": 0, "name": "", "view_type": "FloorPlan" },
  "level": { "element_id": 0, "name": "" },
  "curve_count": 0,
  "separator_ids": [0],
  "length_m": 0.0,
  "closed_loop": false
}
```

- Revit API strategy:
  - Resolve a plan view from `view_id`, or active view, or `level_name`.
  - Require a plan-like view that accepts room separation lines.
  - Convert millimeter points to feet.
  - Build a `CurveArray` of `Line` curves. If `close_loop=true`, add final segment from last point to first point when needed.
  - Create or reuse a horizontal `SketchPlane` at the target level.
  - Call `doc.Create.NewRoomBoundaryLines(sketchPlane, curveArray, view)`.
- Transaction behavior:
  - One transaction named `MCP: Create Room Separator`.
  - Roll back if fewer than one valid segment is produced.
  - Check transaction commit status before returning success.
- Validation/failure modes:
  - Invalid JSON points fail in the MCP wrapper before dispatch.
  - Duplicate adjacent points and zero-length segments are rejected.
  - Non-plan active view without `view_id` or `level_name` fails.
  - Points with non-finite coordinates fail.
- Cross-version notes:
  - Use `CurveElementType.RoomSeparation` only for verification; creation is through `NewRoomBoundaryLines`.
  - Do not assume all plan views support room separators.
- Smoke/test expectations:
  - Two points create one separator line in a floor plan.
  - Closed rectangle creates four separator lines.
  - Running in a 3D view without a target view fails cleanly.

### `create_area`

- Handler file: `src/shared/Handlers/CreateAreaHandler.cs`
- Purpose: create an area in an existing or newly created area plan.
- MCP wrapper:

```csharp
public static async Task<string> CreateArea(
    double x,
    double y,
    long? areaPlanViewId = null,
    string areaPlanViewName = "",
    string areaSchemeName = "",
    string levelName = "",
    bool createAreaPlanIfMissing = false,
    string name = "",
    string number = "")
```

- MCP payload:

```json
{
  "x": 0.0,
  "y": 0.0,
  "area_plan_view_id": 123,
  "area_plan_view_name": "",
  "area_scheme_name": "",
  "level_name": "",
  "create_area_plan_if_missing": false,
  "name": "",
  "number": ""
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["x", "y"],
  "properties": {
    "x": { "type": "number" },
    "y": { "type": "number" },
    "area_plan_view_id": { "type": "integer" },
    "area_plan_view_name": { "type": "string" },
    "area_scheme_name": { "type": "string" },
    "level_name": { "type": "string" },
    "create_area_plan_if_missing": { "type": "boolean" },
    "name": { "type": "string" },
    "number": { "type": "string" }
  }
}
```

- Response DTO:

```json
{
  "area": {
    "element_id": 0,
    "name": "",
    "number": "",
    "area_m2": 0.0,
    "status": "placed"
  },
  "area_plan": { "element_id": 0, "name": "", "level_name": "", "area_scheme_name": "" },
  "created_area_plan": false,
  "location": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 }
}
```

- Revit API strategy:
  - Resolve `ViewPlan` from `area_plan_view_id` or `area_plan_view_name`.
  - If not provided and `create_area_plan_if_missing=true`, resolve `AreaScheme` and `Level`, then call `ViewPlan.CreateAreaPlan`.
  - Call `doc.Create.NewArea(viewPlan, new UV(xFeet, yFeet))`.
  - Set `ROOM_NAME`/area name and number parameters when writable.
  - Classify placed/not-enclosed by area value and boundary availability.
- Transaction behavior:
  - Use a `TransactionGroup` named `MCP: Create Area` when the handler must create both area plan and area.
  - Use one transaction when creating only the area.
  - Roll back the group if area creation fails after creating an area plan.
- Validation/failure modes:
  - Missing area plan and `create_area_plan_if_missing=false` fails.
  - Ambiguous area scheme or level name fails with candidate names.
  - Point outside an area boundary may create a not-enclosed area or fail depending on Revit state; return explicit status/warning.
  - Non-area plan view fails.
- Cross-version notes:
  - Area plan creation API is available across the supported range but overloads should be verified against Revit 2022 references.
  - Area name/number parameters vary by localized templates; set by built-in parameter first, then safe named fallback.
- Smoke/test expectations:
  - Existing area plan plus valid point creates one area.
  - Missing plan with create flag creates an area plan and area in one group.
  - Invalid scheme name fails without creating partial elements.

### `create_space`

- Handler file: `src/shared/Handlers/CreateSpaceHandler.cs`
- Purpose: create an MEP space at a point.
- MCP wrapper:

```csharp
public static async Task<string> CreateSpace(
    double x,
    double y,
    string levelName = "",
    string phaseName = "",
    string name = "",
    string number = "")
```

- MCP payload:

```json
{
  "x": 0.0,
  "y": 0.0,
  "level_name": "",
  "phase_name": "",
  "name": "",
  "number": ""
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["x", "y"],
  "properties": {
    "x": { "type": "number" },
    "y": { "type": "number" },
    "level_name": { "type": "string" },
    "phase_name": { "type": "string" },
    "name": { "type": "string" },
    "number": { "type": "string" }
  }
}
```

- Response DTO:

```json
{
  "space": {
    "element_id": 0,
    "name": "",
    "number": "",
    "status": "placed",
    "area_m2": 0.0,
    "volume_m3": 0.0
  },
  "level": { "element_id": 0, "name": "" },
  "phase": { "element_id": 0, "name": "" },
  "location": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 }
}
```

- Revit API strategy:
  - Resolve level from `level_name` or active view gen level.
  - Resolve phase from `phase_name` or document active phase/default phase.
  - Call `doc.Create.NewSpace(level, phase, new UV(xFeet, yFeet))` when phase is available, otherwise supported overload.
  - Set name/number parameters when writable.
  - Classify status using `Space.Area`, `Location`, and boundary loops.
- Transaction behavior: one transaction named `MCP: Create Space`.
- Validation/failure modes:
  - Missing level fails.
  - Ambiguous level/phase names fail with candidates.
  - Space creation can fail when the project has no MEP settings or point is invalid; surface Revit exception message.
- Cross-version notes:
  - `Autodesk.Revit.DB.Mechanical.Space` namespace must be referenced explicitly.
  - Phase overload availability should be compiled against Revit 2022 and 2026.
- Smoke/test expectations:
  - Valid level and point in an enclosed room creates one placed space.
  - Point outside enclosure returns not-enclosed status or fails cleanly.
  - Invalid phase name fails before transaction starts.

### `list_areas`

- Handler file: `src/shared/Handlers/ListAreasHandler.cs`
- Purpose: list areas across area schemes and area plans.
- MCP wrapper:

```csharp
public static async Task<string> ListAreas(
    string areaSchemeName = "",
    string levelName = "",
    string status = "all",
    int limit = 5000)
```

- MCP payload:

```json
{
  "area_scheme_name": "",
  "level_name": "",
  "status": "all",
  "limit": 5000
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "area_scheme_name": { "type": "string" },
    "level_name": { "type": "string" },
    "status": { "type": "string", "enum": ["all", "placed", "not_enclosed"] },
    "limit": { "type": "integer", "minimum": 1, "maximum": 20000 }
  }
}
```

- Response DTO:

```json
{
  "total": 0,
  "returned": 0,
  "counts": { "placed": 0, "not_enclosed": 0 },
  "areas": [
    {
      "element_id": 0,
      "unique_id": "",
      "name": "",
      "number": "",
      "status": "placed",
      "area_m2": 0.0,
      "level": { "element_id": 0, "name": "" },
      "area_scheme": { "element_id": 0, "name": "" },
      "area_plan": { "element_id": 0, "name": "" },
      "location": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 }
    }
  ]
}
```

- Revit API strategy:
  - Collect `Area` elements from `OST_Areas`.
  - Resolve associated area plan via owner view or view-specific data.
  - Resolve `ViewPlan.AreaScheme` for area scheme metadata.
  - Classify `placed` when area is positive and location exists, otherwise `not_enclosed`.
- Transaction behavior: no transaction.
- Validation/failure modes:
  - Unknown area scheme or level filter returns empty result.
  - Invalid status fails.
  - Limit range errors fail before collection.
- Cross-version notes:
  - `AreaScheme` access through area plan should be null-safe.
  - Owner view may be invalid for some elements; return null view DTO.
- Smoke/test expectations:
  - Areas from at least two schemes can be filtered by scheme name.
  - Not-enclosed area appears with status `not_enclosed`.

### `compute_room_finishes`

- Handler file: `src/shared/Handlers/ComputeRoomFinishesHandler.cs`
- Purpose: summarize room finish parameters and inferred boundary finish hosts.
- MCP wrapper:

```csharp
public static async Task<string> ComputeRoomFinishes(
    long[]? roomIds = null,
    string levelName = "",
    bool includeEmpty = true,
    int limit = 5000)
```

- MCP payload:

```json
{
  "room_ids": [123],
  "level_name": "",
  "include_empty": true,
  "limit": 5000
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "room_ids": { "type": "array", "items": { "type": "integer" } },
    "level_name": { "type": "string" },
    "include_empty": { "type": "boolean" },
    "limit": { "type": "integer", "minimum": 1, "maximum": 20000 }
  }
}
```

- Response DTO:

```json
{
  "total": 0,
  "rooms": [
    {
      "room": { "element_id": 0, "name": "", "number": "", "level_name": "" },
      "finishes": {
        "base": "",
        "floor": "",
        "ceiling": "",
        "wall": ""
      },
      "boundary_materials": [
        {
          "element_id": 0,
          "category": "Walls",
          "type_name": "",
          "material_names": [""],
          "length_m": 0.0
        }
      ],
      "warnings": []
    }
  ],
  "summary": {
    "wall_finishes": {},
    "floor_finishes": {},
    "ceiling_finishes": {},
    "base_finishes": {}
  }
}
```

- Revit API strategy:
  - Resolve target rooms from ids or filters.
  - Read built-in room finish parameters where available:
    - base finish
    - floor finish
    - ceiling finish
    - wall finish
  - For placed rooms, walk boundary segments and collect wall/host type names and material names for context.
  - Do not infer finish quantities as authoritative unless material takeoff APIs are explicitly added later.
- Transaction behavior: no transaction.
- Validation/failure modes:
  - Non-room ids fail with explicit id list.
  - If finish parameters are empty and `include_empty=false`, omit the room.
  - Boundary material extraction failures become warnings per room.
- Cross-version notes:
  - Built-in room finish parameter names differ across versions/templates; use built-in ids first and safe named fallback.
  - Material APIs can return paint/layer data differently; keep DTO explicit about source.
- Smoke/test expectations:
  - Room with finish parameter values returns those strings.
  - Empty finish room is omitted when `include_empty=false`.
  - Boundary wall material warnings do not fail the entire command.

### `auto_create_rooms_from_walls`

- Handler file: `src/shared/Handlers/AutoCreateRoomsFromWallsHandler.cs`
- Purpose: create rooms for available enclosed plan circuits on a level.
- MCP wrapper:

```csharp
public static async Task<string> AutoCreateRoomsFromWalls(
    string levelName,
    string phaseName = "",
    string namePrefix = "",
    string numberPrefix = "",
    int startNumber = 1,
    bool dryRun = true,
    int limit = 500)
```

- MCP payload:

```json
{
  "level_name": "Level 1",
  "phase_name": "",
  "name_prefix": "",
  "number_prefix": "",
  "start_number": 1,
  "dry_run": true,
  "limit": 500
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "required": ["level_name"],
  "properties": {
    "level_name": { "type": "string" },
    "phase_name": { "type": "string" },
    "name_prefix": { "type": "string" },
    "number_prefix": { "type": "string" },
    "start_number": { "type": "integer", "minimum": 1 },
    "dry_run": { "type": "boolean" },
    "limit": { "type": "integer", "minimum": 1, "maximum": 2000 }
  }
}
```

- Response DTO:

```json
{
  "dry_run": true,
  "level": { "element_id": 0, "name": "" },
  "phase": { "element_id": 0, "name": "" },
  "candidate_count": 0,
  "created_count": 0,
  "candidates": [
    {
      "index": 0,
      "area_m2": 0.0,
      "centroid": { "x_mm": 0.0, "y_mm": 0.0, "z_mm": 0.0 },
      "would_create": true,
      "room_id": null,
      "warnings": []
    }
  ],
  "created_room_ids": [0],
  "warnings": []
}
```

- Revit API strategy:
  - Resolve `Level` and `Phase`.
  - Use plan topology/plan circuits for the level/phase when available.
  - Skip circuits that already contain a room.
  - In dry run, report candidates only.
  - In create mode, create rooms with `Document.Create.NewRoom(existingUnplacedRoom, PlanCircuit)` where applicable, or use the circuit centroid with `NewRoom(Level, UV)` when a direct circuit overload is not usable.
  - Set generated name/number values if prefixes are supplied.
- Transaction behavior:
  - `dry_run=true`: no transaction.
  - `dry_run=false`: use `TransactionGroup` named `MCP: Auto Create Rooms From Walls`; create rooms in one transaction or batched transactions inside the group.
  - Roll back the group if any unexpected exception occurs. Per-circuit known failures may be skipped with warnings only if no partial room was created for that circuit.
- Validation/failure modes:
  - Missing or ambiguous level/phase fails.
  - Existing rooms in circuits are skipped, not failure.
  - Candidate count over `limit` fails unless caller raises the limit.
  - Duplicate generated numbers must fail before mutation or be auto-suffixed only if explicitly documented in the response.
- Cross-version notes:
  - Plan topology behavior differs in remodel/phase-heavy projects; include phase in all topology queries.
  - Avoid relying on internal order of plan circuits for stable numbering; sort by centroid Y descending then X ascending.
- Smoke/test expectations:
  - Dry run on a level with two empty enclosed circuits returns two candidates and creates no elements.
  - Create mode creates rooms with predictable generated numbers.
  - Re-running create mode skips already-roomed circuits.

### `tag_all_areas`

- Handler file: `src/shared/Handlers/TagAllAreasHandler.cs`
- Purpose: place area tags for untagged areas in an area plan.
- MCP wrapper:

```csharp
public static async Task<string> TagAllAreas(
    long? areaPlanViewId = null,
    string areaPlanViewName = "",
    bool skipExisting = true,
    long? tagTypeId = null)
```

- MCP payload:

```json
{
  "area_plan_view_id": 123,
  "area_plan_view_name": "",
  "skip_existing": true,
  "tag_type_id": 456
}
```

- `ParametersSchema` shape:

```json
{
  "type": "object",
  "properties": {
    "area_plan_view_id": { "type": "integer" },
    "area_plan_view_name": { "type": "string" },
    "skip_existing": { "type": "boolean" },
    "tag_type_id": { "type": "integer" }
  }
}
```

- Response DTO:

```json
{
  "area_plan": { "element_id": 0, "name": "", "area_scheme_name": "" },
  "processed_count": 0,
  "created_count": 0,
  "skipped_existing_count": 0,
  "failed_count": 0,
  "created_tag_ids": [0],
  "items": [
    {
      "area_id": 0,
      "tag_id": 0,
      "status": "created|skipped_existing|failed",
      "message": ""
    }
  ]
}
```

- Revit API strategy:
  - Resolve target area plan from id/name or active view.
  - Collect areas visible/owned by the area plan.
  - Collect existing area tags in the same view and map by tagged area id.
  - Use each area's location point or boundary centroid as tag UV.
  - Call `doc.Create.NewAreaTag(viewPlan, area, uv)`.
  - If `tag_type_id` is supplied, change the created tag type when allowed.
- Transaction behavior:
  - One transaction named `MCP: Tag All Areas`.
  - Per-area creation failures are captured as item failures unless Revit invalidates the transaction.
- Validation/failure modes:
  - Non-area plan view fails.
  - Invalid tag type id fails before mutation.
  - Areas without a usable point are skipped with item status `failed`.
- Cross-version notes:
  - Area tag creation overloads should be verified against Revit 2022; keep wrapper parameters stable even if internal overload selection differs.
  - Tagged area id access can differ by tag API version; use safe parameter/reference fallback.
- Smoke/test expectations:
  - Area plan with three untagged areas creates three tags.
  - Re-run with `skip_existing=true` creates zero additional tags.
  - Invalid tag type id fails with no new tags.

## Wiring

- Add `RoomsTools` to `src/server/Program.cs` with one MCP method per tool.
- Add each tool name to the corresponding handler `Name` property exactly as listed in this spec.
- Add handlers to `CommandDispatcher` in a new rooms phase.
- Add `rooms` to `ToolsetFilter` as known, default-on, and write-capable.
- Do not change existing `create_room` or `export_room_data` wrappers unless a later compatibility task explicitly assigns that work.
- Keep all JSON payload keys snake_case. Keep response DTO properties serialized to snake_case or the repo's established JSON policy.
- Add internal helper methods only when they reduce duplication across the new handler files:
  - id parsing with `RevitCompat`
  - level/phase/view lookup
  - room/area/space status classification
  - feet-to-metric conversion

## Acceptance Criteria

- The `rooms` toolset is discoverable through MCP when default toolsets are enabled.
- All ten tool names dispatch through `CommandDispatcher`.
- Read-only commands perform no transactions.
- Write commands use named transactions and return committed element ids only after commit success.
- Room listing distinguishes `placed`, `unplaced`, and `not_enclosed`.
- Room boundary output includes loop/segment geometry and boundary element ids when available.
- Room opening output handles doors and windows without failing on phase-null associations.
- Room separators are created only in valid plan views and fail cleanly elsewhere.
- Area creation works against existing area plans and can create a missing area plan when explicitly requested.
- Space creation supports level and phase targeting.
- Finish computation returns room finish parameters and boundary context without claiming unsupported quantity takeoff precision.
- Auto-room creation supports dry run and create mode, and does not duplicate rooms in already-roomed circuits.
- Area tagging skips existing tags when requested.
- Handler tests cover JSON validation, invalid ids, missing target elements, and response DTO shape.

## Review Checklist

- Handler file name matches the spec for each tool.
- `Name` property matches the MCP tool name exactly.
- `ParametersSchema` includes required fields and enum/range constraints.
- MCP wrapper method serializes the documented payload keys.
- No handler returns raw Revit objects.
- All element-id conversions use `RevitCompat`.
- Unit conversion is explicit and consistent with existing handlers.
- Every transaction has a descriptive `MCP:` name.
- Write handlers fail before starting a transaction when validation can be done upfront.
- Per-item failures are represented in DTOs for batch tools.
- Cross-version compile is considered for Revit 2022 through 2027.
- Smoke tests include placed, unplaced, and not-enclosed spatial elements.

## Known Risks

- Room/area/space APIs are sensitive to phase, design option, view, and template state; implementation must avoid assuming the active view is suitable.
- Not-enclosed and unplaced classification can vary by Revit state. The DTO must expose enough evidence, such as area, location, and boundary count, for callers to understand the classification.
- Area scheme and area plan APIs can be ambiguous in localized templates. Name matching should report candidates on ambiguity.
- Finish parameters are template-dependent. The tool should report parameter values and boundary context, not authoritative finish quantities.
- Auto-creating rooms from plan circuits can produce user-visible model changes across many circuits. Keep `dry_run=true` as the MCP default.
- Room separator creation changes enclosure behavior for subsequent room/area operations. The transaction result must include created separator ids and the target view.

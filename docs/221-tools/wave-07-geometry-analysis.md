# Wave 07 - Geometry Analysis Target Implementation Spec

## Status

Pending implementation. This spec defines the target state for Wave 7 only.

Toolset: `geometry`

Tool count: 12

Default state: enabled by default.

Write capability: read-only. No handler in this wave may open a Revit `Transaction`, `SubTransaction`, or `TransactionGroup`, mutate selection, mutate view state, create elements, delete elements, write files, or call APIs whose purpose is document modification.

Primary objective: expose safe geometry inspection, measurement, and pre-clash analysis without returning raw Revit API objects or unbounded geometry payloads.

## Scope

Implement these tools:

| Tool | Type | Proposed handler file |
|---|---|---|
| `get_element_bounding_box` | R | `src/shared/Handlers/GetElementBoundingBoxHandler.cs` |
| `get_element_geometry` | R | `src/shared/Handlers/GetElementGeometryHandler.cs` |
| `measure_distance_between_elements` | R | `src/shared/Handlers/MeasureDistanceBetweenElementsHandler.cs` |
| `clash_detection` | R | `src/shared/Handlers/ClashDetectionHandler.cs` |
| `raycast_from_point` | R | `src/shared/Handlers/RaycastFromPointHandler.cs` |
| `find_elements_in_volume` | R | `src/shared/Handlers/FindElementsInVolumeHandler.cs` |
| `compute_element_volume` | R | `src/shared/Handlers/ComputeElementVolumeHandler.cs` |
| `compute_element_area` | R | `src/shared/Handlers/ComputeElementAreaHandler.cs` |
| `project_point_onto_face` | R | `src/shared/Handlers/ProjectPointOntoFaceHandler.cs` |
| `find_overlapping_elements` | R | `src/shared/Handlers/FindOverlappingElementsHandler.cs` |
| `get_element_centroid` | R | `src/shared/Handlers/GetElementCentroidHandler.cs` |
| `analyze_geometry_complexity` | R | `src/shared/Handlers/AnalyzeGeometryComplexityHandler.cs` |

Out of scope:

- Returning meshes, full `Solid`, `Face`, `Edge`, `Curve`, `Reference`, `BoundingBoxXYZ`, or any other raw Revit object.
- Long-running whole-model exact solid clash by default.
- Mutating the active view, selection, graphics, temporary isolate state, or document.
- Exporting geometry to disk.

## Dependencies

- Follow `CLAUDE.md` conventions: handler-per-tool, DTO-only returns, guarded JSON parsing, `CommandResult.Ok/Fail`, and cross-version ElementId via `RevitCompat`.
- Add all handlers under `src/shared/Handlers/`.
- Wire commands in `src/shared/Infrastructure/CommandDispatcher.cs` in a new Wave 7 / Geometry block.
- Add a new `[McpServerToolType, Toolset("geometry")]` wrapper class in `src/server/Program.cs`.
- Add `geometry` to `ToolsetFilter.KnownToolsets` and `ToolsetFilter.DefaultOn`.
- Do not add `geometry` to `ToolsetFilter.WriteCapable`.
- Use `Autodesk.Revit.DB.Options` with `ComputeReferences` only when a tool actually needs stable references. Default to `ComputeReferences = false`.
- Use `ViewDetailLevel` from parameters where useful, but default to `Medium`.
- Use external units in responses: mm, m2, m3, degrees where applicable. Internal Revit units remain feet, square feet, cubic feet, radians.
- Enforce response-size safeguards in the handlers themselves. `ResponseSizeGuard` is observability-only, so geometry tools must avoid constructing oversized DTOs.

Shared helper recommendations:

- Implement private helper methods inside each handler unless duplication becomes excessive.
- Accept duplicate helper code across this wave rather than creating a large shared geometry abstraction prematurely.
- Common helpers should include: guarded `JObject` parse, id array parsing with `RevitCompat.CanRepresentElementId`, point conversion, bounding box conversion, category resolution, safe geometry traversal, and limit normalization.

## Toolset Contract

All geometry handlers must:

- Be read-only and side-effect-free.
- Return `CommandResult.Fail(...)` for invalid request shape, invalid id ranges, missing required parameters, or no open document.
- Return `CommandResult.Ok(...)` with per-item `failed` entries for mixed-success batch queries where some ids are invalid or elements lack geometry.
- Include `unit` fields on all geometric quantities.
- Round coordinate outputs to 3 decimals for mm and 6 decimals for m2/m3 unless a tool-specific field needs more precision.
- Use `RevitCompat.GetId(...)` and `RevitCompat.ToElementId(...)`.
- Never use `ElementId.IntegerValue` or `ElementId.Value` directly.
- Never serialize Revit API objects directly.
- Default `limit` values must be conservative, with hard maximums.
- Include truncation metadata when results are capped:
  - `limit`
  - `returned`
  - `truncated`
  - `truncation_reason`
- Prefer bounding-box prefilters before expensive solid geometry.

Recommended limits:

- Per-element geometry summaries: default `limit = 100`, hard max `500`.
- Exact pairwise operations: default `max_pairs = 1000`, hard max `10000`.
- Clash result rows: default `max_results = 100`, hard max `500`.
- Vertex samples: default `include_samples = false`; if true, hard max `sample_limit = 50` per element.
- Whole-model category collection: require at least one category filter or explicit ids when possible.

Bounding-box versus solid-geometry limitations:

- Bounding boxes are axis-aligned in model coordinates and may include symbolic/annotation/control geometry depending on Revit element behavior.
- Bounding-box overlap is a cheap candidate filter, not proof of physical clash.
- Solid extraction can fail or return zero solids for view-specific, symbolic, import, in-place, analytical, or family/control geometry.
- Solid intersections are more accurate but expensive and may be sensitive to detail level and Revit's geometry kernel tolerances.
- Tools must report which strategy was used: `strategy = "bounding_box"`, `"solid"`, or `"bounding_box_prefilter_then_solid"`.

Response-size safeguards:

- Do not return full vertex/edge/face lists by default.
- Return counts and aggregate metrics first.
- When optional samples are requested, return only small samples and include `sampled = true`.
- For `clash_detection`, `find_overlapping_elements`, and `analyze_geometry_complexity`, cap both scanned candidate counts and returned rows.
- If a request would exceed a hard limit, fail fast with a message naming the limit and how to narrow the query.

## Tool Specs

### `get_element_bounding_box`

Proposed handler file: `src/shared/Handlers/GetElementBoundingBoxHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "get_element_bounding_box", ReadOnly = true, Idempotent = true)]
public static async Task<string> GetElementBoundingBox(long[] elementIds, long? viewId = null, bool includeTransform = false)
```

Send payload:

```csharp
new { element_ids = elementIds, view_id = viewId, include_transform = includeTransform }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_ids"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 },
    "view_id": { "type": "integer", "description": "Optional view-specific bounding box. Omit for model bbox." },
    "include_transform": { "type": "boolean", "default": false }
  }
}
```

Response DTO:

```json
{
  "unit": "mm",
  "requested": 2,
  "returned": 1,
  "view_id": 123,
  "view_name": "Level 1",
  "boxes": [
    {
      "element_id": 456,
      "unique_id": "...",
      "name": "Wall 1",
      "category": "Walls",
      "min": { "x": 0.0, "y": 0.0, "z": 0.0 },
      "max": { "x": 1000.0, "y": 200.0, "z": 3000.0 },
      "center": { "x": 500.0, "y": 100.0, "z": 1500.0 },
      "size": { "x": 1000.0, "y": 200.0, "z": 3000.0 },
      "has_transform": false,
      "transform": null
    }
  ],
  "failed": [
    { "element_id": 999, "error": "Element not found." }
  ],
  "truncated": false,
  "error": null
}
```

Revit API strategy:

- Resolve optional `view_id` to `View`; use active document only.
- Call `element.get_BoundingBox(view)` when `view_id` is supplied, otherwise `element.get_BoundingBox(null)`.
- Convert `BoundingBoxXYZ.Min` and `Max` from feet to mm.
- If `include_transform = true`, return `BoundingBoxXYZ.Transform` basis/origin in mm for origin and raw basis vectors.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if `element_ids` is missing or empty.
- Fail if `view_id` is out of range or does not resolve to a `View`.
- Per-element failure for not found or no bounding box.
- Hard cap `element_ids` at 500.

Cross-version notes:

- Use `RevitCompat.ToElementId` and `GetId`.
- `BoundingBoxXYZ` is stable across R22-R27.

Smoke/test expectations:

- Read bounding boxes for one wall and one family instance.
- Verify optional `view_id` changes or preserves values without mutation.
- Verify out-of-range id fails cleanly on R22/R23/R24.

### `get_element_geometry`

Proposed handler file: `src/shared/Handlers/GetElementGeometryHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "get_element_geometry", ReadOnly = true, Idempotent = true)]
public static async Task<string> GetElementGeometry(long[] elementIds, string detailLevel = "Medium", bool includeSamples = false, int sampleLimit = 20)
```

Send payload:

```csharp
new { element_ids = elementIds, detail_level = detailLevel, include_samples = includeSamples, sample_limit = sampleLimit }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_ids"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 },
    "detail_level": { "type": "string", "enum": ["Coarse", "Medium", "Fine"], "default": "Medium" },
    "include_samples": { "type": "boolean", "default": false },
    "sample_limit": { "type": "integer", "default": 20, "minimum": 0, "maximum": 50 }
  }
}
```

Response DTO:

```json
{
  "requested": 1,
  "returned": 1,
  "detail_level": "Medium",
  "results": [
    {
      "element_id": 456,
      "name": "Wall 1",
      "category": "Walls",
      "unit": "mm",
      "bounding_box": { "min": {}, "max": {}, "center": {}, "size": {} },
      "geometry": {
        "solid_count": 1,
        "non_empty_solid_count": 1,
        "face_count": 6,
        "edge_count": 12,
        "curve_count": 0,
        "mesh_count": 0,
        "instance_count": 0,
        "vertex_sample_count": 0,
        "vertex_samples": null
      },
      "limitations": []
    }
  ],
  "failed": [],
  "truncated": false,
  "error": null
}
```

Revit API strategy:

- Use `Options { DetailLevel = ..., ComputeReferences = false, IncludeNonVisibleObjects = false }`.
- Traverse `GeometryElement`.
- For `Solid`, count faces/edges and ignore solids with `Volume <= 1e-9` unless they still expose meaningful faces.
- For `GeometryInstance`, traverse `GetInstanceGeometry()` and increment `instance_count`.
- For `Mesh`, count mesh and vertices but do not return full mesh.
- For `Curve`, count curve.
- Optional vertex samples may come from tessellated edges or mesh vertices, capped at `sample_limit`.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if `element_ids` missing/empty.
- Reject more than 100 ids by default unless a future wrapper exposes a larger explicit limit; hard max 500.
- Fail invalid `detail_level`.
- Per-element failure for not found or `get_Geometry` exception.

Cross-version notes:

- `Options.DetailLevel`, `GeometryInstance.GetInstanceGeometry`, `Solid.Faces`, and `Solid.Edges` are stable R22-R27.
- Do not rely on newer API properties for geometry identifiers.

Smoke/test expectations:

- Wall returns nonzero solid/face/edge counts.
- Model line/detail item returns curve or no-solid summary without error.
- `include_samples = false` returns no large arrays.

### `measure_distance_between_elements`

Proposed handler file: `src/shared/Handlers/MeasureDistanceBetweenElementsHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "measure_distance_between_elements", ReadOnly = true, Idempotent = true)]
public static async Task<string> MeasureDistanceBetweenElements(long elementId1, long elementId2, string strategy = "bbox")
```

Send payload:

```csharp
new { element_id_1 = elementId1, element_id_2 = elementId2, strategy }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_id_1", "element_id_2"],
  "properties": {
    "element_id_1": { "type": "integer" },
    "element_id_2": { "type": "integer" },
    "strategy": { "type": "string", "enum": ["bbox", "location", "solid_bbox_prefilter"], "default": "bbox" }
  }
}
```

Response DTO:

```json
{
  "unit": "mm",
  "element_id_1": 101,
  "element_id_2": 202,
  "strategy": "bbox",
  "distance": 150.25,
  "intersects": false,
  "point_1": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "point_2": { "x": 150.25, "y": 0.0, "z": 0.0 },
  "bbox_1": {},
  "bbox_2": {},
  "limitations": ["bbox distance is axis-aligned-box distance, not exact solid-to-solid clearance"],
  "error": null
}
```

Revit API strategy:

- `bbox`: compute shortest distance between two axis-aligned bounding boxes. Return zero when boxes overlap.
- `location`: if both elements expose `LocationPoint` or bound `LocationCurve`, approximate closest point from point/curve endpoints and curve projection where possible.
- `solid_bbox_prefilter`: use bbox first; if boxes overlap, report `distance = 0` and `intersects = true` as candidate overlap. Do not attempt full solid distance in this wave unless implementation can keep bounded runtime.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if ids invalid/out of range.
- Fail if either element not found.
- Fail if bounding box missing for selected strategy.
- Cleanly report unsupported exact solid distance rather than pretending to compute it.

Cross-version notes:

- Curve projection and bounding boxes are stable R22-R27.
- Avoid API calls added after R22.

Smoke/test expectations:

- Two separated walls produce positive bbox distance.
- Overlapping/adjacent elements return zero with `intersects = true`.
- Elements without bbox return clean error.

### `clash_detection`

Proposed handler file: `src/shared/Handlers/ClashDetectionHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "clash_detection", ReadOnly = true, Idempotent = true)]
public static async Task<string> ClashDetection(string[] categoriesA, string[] categoriesB, long? viewId = null, string strategy = "bbox_then_solid", int maxPairs = 1000, int maxResults = 100)
```

Send payload:

```csharp
new { categories_a = categoriesA, categories_b = categoriesB, view_id = viewId, strategy, max_pairs = maxPairs, max_results = maxResults }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["categories_a", "categories_b"],
  "properties": {
    "categories_a": { "type": "array", "items": { "type": "string" }, "minItems": 1 },
    "categories_b": { "type": "array", "items": { "type": "string" }, "minItems": 1 },
    "view_id": { "type": "integer" },
    "strategy": { "type": "string", "enum": ["bbox", "bbox_then_solid"], "default": "bbox_then_solid" },
    "max_pairs": { "type": "integer", "default": 1000, "minimum": 1, "maximum": 10000 },
    "max_results": { "type": "integer", "default": 100, "minimum": 1, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "strategy": "bounding_box_prefilter_then_solid",
  "categories_a": ["Walls"],
  "categories_b": ["Pipes"],
  "scanned_a": 12,
  "scanned_b": 30,
  "pairs_considered": 360,
  "bbox_candidates": 4,
  "solid_tests": 4,
  "returned": 2,
  "limit": 100,
  "truncated": false,
  "clashes": [
    {
      "a": { "element_id": 101, "category": "Walls", "name": "Basic Wall" },
      "b": { "element_id": 202, "category": "Pipes", "name": "Pipe" },
      "bbox_intersects": true,
      "solid_intersects": true,
      "intersection_volume_m3": 0.003,
      "bbox_overlap": { "unit": "mm", "min": {}, "max": {} },
      "confidence": "solid"
    }
  ],
  "failed": [],
  "warnings": [],
  "error": null
}
```

Revit API strategy:

- Resolve category names through `doc.Settings.Categories` and fallback to `BuiltInCategory` labels when possible.
- Collect non-type elements by category. Scope to `view_id` collector if supplied.
- Build bbox DTOs and reject elements with no bbox.
- Generate pair candidates only where bboxes overlap.
- For `bbox`, return bbox candidates only with `confidence = "bbox_candidate"`.
- For `bbox_then_solid`, extract solids for each candidate and use `BooleanOperationsUtils.ExecuteBooleanOperation(solidA, solidB, BooleanOperationsType.Intersect)`. Treat non-null intersection solid with volume above tolerance as clash.
- Cache extracted solids per element to avoid repeated geometry traversal.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if categories missing/empty or cannot resolve.
- Fail if `max_pairs` or `max_results` exceeds hard max.
- If candidate pair count exceeds `max_pairs`, stop at limit and mark `truncated = true`.
- Per-pair solid extraction/intersection failures go to `failed` and do not abort the whole run.
- Reject same-category self-pairs with identical ids.

Cross-version notes:

- `BooleanOperationsUtils` is available in R22-R27, but can throw for invalid solids. Catch per pair.
- Category ids must use `RevitCompat.GetId` when reported.

Smoke/test expectations:

- Bbox strategy returns candidates for intentionally overlapping categories.
- Solid strategy catches and reports failed solids without throwing.
- Large model request truncates deterministically with metadata.

### `raycast_from_point`

Proposed handler file: `src/shared/Handlers/RaycastFromPointHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "raycast_from_point", ReadOnly = true, Idempotent = true)]
public static async Task<string> RaycastFromPoint(double x, double y, double z, double dirX, double dirY, double dirZ, long view3dId, string[] categories = null, double maxDistance = 100000)
```

Send payload:

```csharp
new { x, y, z, dir_x = dirX, dir_y = dirY, dir_z = dirZ, view_3d_id = view3dId, categories, max_distance = maxDistance }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["x", "y", "z", "dir_x", "dir_y", "dir_z", "view_3d_id"],
  "properties": {
    "x": { "type": "number" },
    "y": { "type": "number" },
    "z": { "type": "number" },
    "dir_x": { "type": "number" },
    "dir_y": { "type": "number" },
    "dir_z": { "type": "number" },
    "view_3d_id": { "type": "integer" },
    "categories": { "type": "array", "items": { "type": "string" } },
    "max_distance": { "type": "number", "default": 100000, "description": "Maximum hit distance in mm." }
  }
}
```

Response DTO:

```json
{
  "unit": "mm",
  "origin": { "x": 0.0, "y": 0.0, "z": 1200.0 },
  "direction": { "x": 1.0, "y": 0.0, "z": 0.0 },
  "view_3d_id": 333,
  "hit": true,
  "hit_element": { "element_id": 101, "name": "Wall", "category": "Walls" },
  "proximity": 2450.0,
  "global_point": { "x": 2450.0, "y": 0.0, "z": 1200.0 },
  "reference_stable_representation": "optional-if-safe",
  "error": null
}
```

Revit API strategy:

- Convert origin and direction from supplied values. Origin is mm; direction is unitless.
- Normalize direction; fail if vector length is near zero.
- Resolve `view_3d_id` to non-template `View3D`.
- Build `ReferenceIntersector` using optional `ElementMulticategoryFilter`; otherwise use all model elements.
- Call `FindNearest(originFeet, directionNormalized)`.
- Reject hits where proximity exceeds `max_distance` in feet.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if `view_3d_id` is not a `View3D` or is a template.
- Fail if category filters cannot resolve.
- Return `hit = false` when no hit, not a failed command.
- Fail if direction vector is zero or `max_distance <= 0`.

Cross-version notes:

- `ReferenceIntersector` requires a 3D view and is available R22-R27.
- Stable reference strings may vary by version/model and should be optional, not a primary contract.

Smoke/test expectations:

- Ray from a known point toward a wall returns nearest wall.
- Zero direction returns failure.
- No hit within max distance returns `hit = false`.

### `find_elements_in_volume`

Proposed handler file: `src/shared/Handlers/FindElementsInVolumeHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "find_elements_in_volume", ReadOnly = true, Idempotent = true)]
public static async Task<string> FindElementsInVolume(string volumeJson = "", long? roomId = null, string[] categories = null, long? viewId = null, string match = "intersects", int limit = 200)
```

Send payload:

```csharp
new { volume = parsedVolume, room_id = roomId, categories, view_id = viewId, match, limit }
```

Wrapper note: `volumeJson` is a JSON object string `{ "min": {"x":...}, "max": {"x":...} }`; parse with `JObject.Parse` only when non-empty.

ParametersSchema shape:

```json
{
  "type": "object",
  "properties": {
    "volume": {
      "type": "object",
      "properties": {
        "min": { "type": "object" },
        "max": { "type": "object" }
      }
    },
    "room_id": { "type": "integer" },
    "categories": { "type": "array", "items": { "type": "string" } },
    "view_id": { "type": "integer" },
    "match": { "type": "string", "enum": ["inside", "intersects"], "default": "intersects" },
    "limit": { "type": "integer", "default": 200, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "unit": "mm",
  "source": "axis_aligned_volume",
  "match": "intersects",
  "volume": { "min": {}, "max": {} },
  "scanned": 500,
  "returned": 24,
  "limit": 200,
  "truncated": false,
  "elements": [
    { "element_id": 101, "name": "Wall", "category": "Walls", "bounding_box": {} }
  ],
  "failed": [],
  "error": null
}
```

Revit API strategy:

- Exactly one of `volume` or `room_id` must be supplied.
- For `volume`, convert min/max from mm to feet and use bbox intersection/containment tests.
- For `room_id`, resolve `Autodesk.Revit.DB.Architecture.Room`, get its bounding box as coarse volume. Do not claim exact room-boundary containment in this wave.
- Collect elements by optional categories and optional view.
- Return bbox-based matches only.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if neither or both `volume` and `room_id` supplied.
- Fail malformed min/max or min > max.
- Fail unresolved categories only if none resolve; otherwise report `unknown_categories`.
- Hard cap returned elements at 500.

Cross-version notes:

- Room class namespace `Autodesk.Revit.DB.Architecture` is available R22-R27.
- Bbox behavior can differ for room-bounding/linked/symbolic elements; report limitation.

Smoke/test expectations:

- Axis-aligned volume around selected elements returns them.
- Room mode reports `source = "room_bounding_box"` and limitation.
- `inside` and `intersects` produce different counts on boundary cases.

### `compute_element_volume`

Proposed handler file: `src/shared/Handlers/ComputeElementVolumeHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "compute_element_volume", ReadOnly = true, Idempotent = true)]
public static async Task<string> ComputeElementVolume(long[] elementIds, string detailLevel = "Medium")
```

Send payload:

```csharp
new { element_ids = elementIds, detail_level = detailLevel }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_ids"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 },
    "detail_level": { "type": "string", "enum": ["Coarse", "Medium", "Fine"], "default": "Medium" }
  }
}
```

Response DTO:

```json
{
  "unit": "m3",
  "requested": 2,
  "returned": 2,
  "elements": [
    {
      "element_id": 101,
      "name": "Wall",
      "category": "Walls",
      "solid_count": 1,
      "volume_m3": 1.234567,
      "source": "solid_geometry",
      "limitations": []
    }
  ],
  "total_volume_m3": 1.234567,
  "failed": [],
  "error": null
}
```

Revit API strategy:

- Traverse solids recursively through `GeometryElement` and `GeometryInstance`.
- Sum `Solid.Volume` for valid solids where volume is above tolerance.
- Convert cubic feet to m3 using `m3 = ft3 * 0.02831685`.
- Do not use material takeoff or volume parameters; this is geometry-derived.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail missing/empty ids.
- Per-element warning if no valid solids found; return `volume_m3 = 0` with limitation rather than fail when element exists.
- Hard cap 500 ids.

Cross-version notes:

- `Solid.Volume` is stable R22-R27.
- Geometry availability depends on detail level and family/import content.

Smoke/test expectations:

- Wall/floor returns positive volume.
- Detail line or annotation returns zero with limitation.
- Batch totals equal sum of per-element values.

### `compute_element_area`

Proposed handler file: `src/shared/Handlers/ComputeElementAreaHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "compute_element_area", ReadOnly = true, Idempotent = true)]
public static async Task<string> ComputeElementArea(long[] elementIds, string detailLevel = "Medium")
```

Send payload:

```csharp
new { element_ids = elementIds, detail_level = detailLevel }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_ids"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 },
    "detail_level": { "type": "string", "enum": ["Coarse", "Medium", "Fine"], "default": "Medium" }
  }
}
```

Response DTO:

```json
{
  "unit": "m2",
  "requested": 1,
  "returned": 1,
  "elements": [
    {
      "element_id": 101,
      "name": "Wall",
      "category": "Walls",
      "solid_count": 1,
      "face_count": 6,
      "area_m2": 12.345678,
      "source": "solid_faces",
      "limitations": []
    }
  ],
  "total_area_m2": 12.345678,
  "failed": [],
  "error": null
}
```

Revit API strategy:

- Traverse valid solids and sum `Face.Area`.
- Convert square feet to m2 using `m2 = ft2 * 0.09290304`.
- Do not use element area parameters.

Transaction behavior: no transaction.

Validation/failure modes:

- Same id validation as `compute_element_volume`.
- For no valid faces, return zero plus limitation.

Cross-version notes:

- `Face.Area` is stable R22-R27.

Smoke/test expectations:

- Wall/floor returns positive area.
- Element with no solids returns zero and limitation.

### `project_point_onto_face`

Proposed handler file: `src/shared/Handlers/ProjectPointOntoFaceHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "project_point_onto_face", ReadOnly = true, Idempotent = true)]
public static async Task<string> ProjectPointOntoFace(long elementId, double x, double y, double z, int faceIndex = 0, string detailLevel = "Medium")
```

Send payload:

```csharp
new { element_id = elementId, x, y, z, face_index = faceIndex, detail_level = detailLevel }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_id", "x", "y", "z"],
  "properties": {
    "element_id": { "type": "integer" },
    "x": { "type": "number" },
    "y": { "type": "number" },
    "z": { "type": "number" },
    "face_index": { "type": "integer", "default": 0, "minimum": 0 },
    "detail_level": { "type": "string", "enum": ["Coarse", "Medium", "Fine"], "default": "Medium" }
  }
}
```

Response DTO:

```json
{
  "unit": "mm",
  "element_id": 101,
  "input_point": { "x": 10.0, "y": 20.0, "z": 30.0 },
  "face_index": 0,
  "face_count": 6,
  "projected": true,
  "projected_point": { "x": 10.0, "y": 0.0, "z": 30.0 },
  "distance": 20.0,
  "uv": { "u": 0.1, "v": 0.2 },
  "normal": { "x": 0.0, "y": -1.0, "z": 0.0 },
  "error": null
}
```

Revit API strategy:

- Extract faces from solids in deterministic traversal order.
- Select by `face_index`.
- Call `Face.Project(pointFeet)` and return `IntersectionResult`.
- Use `Face.ComputeNormal(uv)` when UV exists.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if element not found or no faces.
- Fail if `face_index` out of range; include actual face count.
- Return `projected = false` with error if Revit returns null projection.

Cross-version notes:

- Face traversal order is not a stable identity contract across model edits or Revit versions. Document `face_index` as transient.
- Do not expose raw `Reference` as required input in this wave.

Smoke/test expectations:

- Project point onto wall face returns projected point and distance.
- Out-of-range face index returns clean failure.

### `find_overlapping_elements`

Proposed handler file: `src/shared/Handlers/FindOverlappingElementsHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "find_overlapping_elements", ReadOnly = true, Idempotent = true)]
public static async Task<string> FindOverlappingElements(string category, long? viewId = null, int maxPairs = 1000, int maxResults = 100)
```

Send payload:

```csharp
new { category, view_id = viewId, max_pairs = maxPairs, max_results = maxResults }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["category"],
  "properties": {
    "category": { "type": "string" },
    "view_id": { "type": "integer" },
    "max_pairs": { "type": "integer", "default": 1000, "maximum": 10000 },
    "max_results": { "type": "integer", "default": 100, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "strategy": "bounding_box",
  "category": "Walls",
  "scanned": 100,
  "pairs_considered": 1000,
  "returned": 12,
  "limit": 100,
  "truncated": false,
  "overlaps": [
    {
      "a": { "element_id": 101, "name": "Wall A" },
      "b": { "element_id": 102, "name": "Wall B" },
      "overlap_box": { "unit": "mm", "min": {}, "max": {} }
    }
  ],
  "warnings": ["bbox overlap is a pre-clash filter, not proof of solid intersection"],
  "error": null
}
```

Revit API strategy:

- Resolve one category.
- Collect non-type elements in category, optionally scoped to view.
- Compute pairwise bbox overlap, skipping identical ids.
- Return only bbox overlap candidates.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if category cannot resolve.
- Enforce pair/result caps.
- Elements without bbox are skipped and counted in `skipped_no_bbox`.

Cross-version notes:

- Bbox and category APIs stable R22-R27.

Smoke/test expectations:

- Duplicated/overlapping same-category elements return pairs.
- Large category truncates with deterministic pair count.

### `get_element_centroid`

Proposed handler file: `src/shared/Handlers/GetElementCentroidHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "get_element_centroid", ReadOnly = true, Idempotent = true)]
public static async Task<string> GetElementCentroid(long[] elementIds, string strategy = "solid_then_bbox")
```

Send payload:

```csharp
new { element_ids = elementIds, strategy }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "required": ["element_ids"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 },
    "strategy": { "type": "string", "enum": ["bbox", "solid_then_bbox"], "default": "solid_then_bbox" }
  }
}
```

Response DTO:

```json
{
  "unit": "mm",
  "requested": 1,
  "returned": 1,
  "centroids": [
    {
      "element_id": 101,
      "name": "Wall",
      "category": "Walls",
      "strategy": "solid",
      "centroid": { "x": 100.0, "y": 200.0, "z": 1500.0 },
      "volume_m3": 1.23,
      "fallback_used": false,
      "limitations": []
    }
  ],
  "failed": [],
  "error": null
}
```

Revit API strategy:

- For `bbox`, use bbox center.
- For `solid_then_bbox`, compute volume-weighted centroid from valid solids using `Solid.ComputeCentroid()` when available; if solid centroid fails or no solids, fallback to bbox center and mark `fallback_used = true`.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail missing/empty ids.
- Per-element failure if no solid and no bbox.

Cross-version notes:

- If `Solid.ComputeCentroid()` is unavailable or throws in a target version, catch and fallback to bbox.
- Do not compile against APIs not present in R22 without verifying. If needed, use direct call only if R22 supports it, otherwise reflection fallback.

Smoke/test expectations:

- Wall returns centroid.
- Annotation/no-solid element falls back or fails clearly.

### `analyze_geometry_complexity`

Proposed handler file: `src/shared/Handlers/AnalyzeGeometryComplexityHandler.cs`

MCP wrapper:

```csharp
[McpServerTool(Name = "analyze_geometry_complexity", ReadOnly = true, Idempotent = true)]
public static async Task<string> AnalyzeGeometryComplexity(long[] elementIds = null, string[] categories = null, long? viewId = null, string detailLevel = "Medium", int limit = 200)
```

Send payload:

```csharp
new { element_ids = elementIds, categories, view_id = viewId, detail_level = detailLevel, limit }
```

ParametersSchema shape:

```json
{
  "type": "object",
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" } },
    "categories": { "type": "array", "items": { "type": "string" } },
    "view_id": { "type": "integer" },
    "detail_level": { "type": "string", "enum": ["Coarse", "Medium", "Fine"], "default": "Medium" },
    "limit": { "type": "integer", "default": 200, "maximum": 500 }
  }
}
```

Response DTO:

```json
{
  "detail_level": "Medium",
  "scanned": 200,
  "returned": 200,
  "limit": 200,
  "truncated": false,
  "summary": {
    "total_solids": 123,
    "total_faces": 456,
    "total_edges": 789,
    "top_complexity_score": 321
  },
  "elements": [
    {
      "element_id": 101,
      "name": "Complex Family",
      "category": "Generic Models",
      "solid_count": 4,
      "face_count": 120,
      "edge_count": 300,
      "mesh_count": 0,
      "curve_count": 2,
      "geometry_instance_count": 1,
      "complexity_score": 426,
      "rank": 1
    }
  ],
  "failed": [],
  "error": null
}
```

Revit API strategy:

- Source elements from explicit `element_ids` or category filters. If neither is supplied, require `view_id` or fail to avoid accidental whole-model scans.
- Use the same bounded geometry traversal as `get_element_geometry`.
- Complexity score proposal: `face_count + edge_count + mesh_vertex_count + (solid_count * 5) + (geometry_instance_count * 10)`.
- Sort descending by score and return top `limit`.

Transaction behavior: no transaction.

Validation/failure modes:

- Fail if no explicit ids/categories/view scope are supplied.
- Enforce limit hard max 500.
- Per-element geometry failures go to `failed`.

Cross-version notes:

- Use stable geometry types only.
- Avoid LINQ over raw Revit collections when exceptions could abort traversal; catch per element.

Smoke/test expectations:

- Family-heavy model returns ranked complexity.
- Missing scope fails fast.
- Limit is honored.

## Wiring

Command dispatcher:

- Add a Wave 7 block after Wave 6 / Materials if that wave exists by implementation time, otherwise after the latest implemented wave block.
- Register all 12 handlers:
  - `GetElementBoundingBoxHandler`
  - `GetElementGeometryHandler`
  - `MeasureDistanceBetweenElementsHandler`
  - `ClashDetectionHandler`
  - `RaycastFromPointHandler`
  - `FindElementsInVolumeHandler`
  - `ComputeElementVolumeHandler`
  - `ComputeElementAreaHandler`
  - `ProjectPointOntoFaceHandler`
  - `FindOverlappingElementsHandler`
  - `GetElementCentroidHandler`
  - `AnalyzeGeometryComplexityHandler`

Program.cs:

- Add:

```csharp
[McpServerToolType, Toolset("geometry")]
public class GeometryTools
{
    // 12 [McpServerTool] wrappers, all ReadOnly = true, Idempotent = true.
}
```

- Every wrapper returns `JsonConvert.SerializeObject(result, Formatting.Indented)` and catches `Exception ex` as existing wrappers do.
- For JSON object/string parameters such as `volumeJson`, parse to `JObject` in the wrapper before sending.

ToolsetFilter.cs:

- Add `"geometry"` to `KnownToolsets`.
- Add `"geometry"` to `DefaultOn`.
- Do not add `"geometry"` to `WriteCapable`.

Golden snapshots/tests:

- Add/update tool list snapshots only during the final wave integration step, not in handler-only work.
- Unit tests should assert new tools are discoverable after wiring and marked read-only.

## Acceptance Criteria

- All 12 handler files exist under `src/shared/Handlers/`.
- Each handler implements `IRevitCommand` with exact command name matching the MCP tool name.
- All handlers use guarded JSON parse:

```csharp
request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
```

inside a `try/catch JsonException`.

- No Wave 7 handler opens a Revit transaction or mutates the document/UI.
- All id handling uses `RevitCompat`.
- All geometry quantities are converted to external units and labelled.
- Expensive tools enforce hard limits and return truncation metadata.
- `clash_detection` clearly distinguishes bbox candidates from solid-confirmed clashes.
- `find_elements_in_volume` clearly distinguishes axis-aligned volume from room bounding-box mode.
- `get_element_geometry` and `analyze_geometry_complexity` never return unbounded vertex/face/edge arrays.
- Toolset gating exposes `geometry` by default and keeps it available under `--read-only`.
- Smoke tests cover one happy path and one validation failure per tool.

## Review Checklist

- [ ] No raw Revit API object is returned.
- [ ] No `.IntegerValue` or `.Value` direct ElementId access.
- [ ] No `Transaction`, `SubTransaction`, or `TransactionGroup` in Wave 7 handlers.
- [ ] Every wrapper is `[McpServerTool(..., ReadOnly = true, Idempotent = true)]`.
- [ ] Every collector has a scope or a limit where whole-model enumeration could be large.
- [ ] Solid extraction catches per-element exceptions.
- [ ] Boolean solid operations catch per-pair exceptions.
- [ ] Bbox-only tools do not claim exact geometry results.
- [ ] Returned arrays are bounded by defaults and hard maximums.
- [ ] Missing `docs/221-tools/README.md` is resolved or this spec remains the local source of truth for Wave 7.

## Known Risks

- Revit solid geometry extraction can be slow or fail for some families, imports, parts, in-place elements, and analytical elements.
- `BooleanOperationsUtils` can throw even for apparently valid solids; per-pair isolation is required.
- `ReferenceIntersector` requires a valid non-template 3D view and can miss hidden elements depending on view visibility.
- Bounding boxes may overstate extents and produce false positives.
- `Face` traversal order is not a stable persistent reference system.
- Large models can create explosive pair counts; hard caps are non-negotiable.

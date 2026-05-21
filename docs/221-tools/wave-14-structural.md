# Wave 14 - Structural Deep Target Implementation Spec

## Status

Target implementation spec. This wave is pending.

## Scope

- Toolset: `structural`
- Tools: 12
- Default: on after implementation
- Write-capable: yes
- Primary goal: create and inspect common structural elements while keeping
  Revit API version risk explicit.

## Dependencies

- Existing create handlers for level/type lookup and point/line placement.
- Query handlers for element details, type parameters, and family types.
- Revit structural categories, structural family symbols, rebar APIs, and load
  APIs across Revit 2022-2027.

## Toolset Contract

- Structural creation tools accept IDs where possible and names only as
  convenience lookup inputs. Ambiguous names must fail.
- Geometry inputs are public mm/deg and converted at API boundaries.
- Creation tools return created element IDs, resolved type IDs, level IDs, and
  placement metadata.
- Rebar and load APIs have known cross-version risk. If a required API is not
  available for a Revit year, return a structured unsupported message instead
  of compile-time branching into broken code.

## Tool Specs

### create_structural_column

- Type: W.
- Handler: `CreateStructuralColumnHandler.cs`.
- MCP params: `type_id` long optional, `type_name` string optional, `x_mm`,
  `y_mm`, `z_mm` doubles optional default 0, `level_id` long optional,
  `level_name` string optional, `height_mm` double optional,
  `structural_usage` string optional, `rotation_deg` double optional.
- ParametersSchema: object requiring either level ID/name or resolvable active
  level; point coordinates in mm.
- Response DTO: `{ created_id, type_id, level_id, base_point_mm, height_mm,
  structural_type, warnings }`.
- Revit API strategy: resolve a `FamilySymbol` in structural column category,
  activate if needed, place via `NewFamilyInstance` with structural type column,
  set top/base constraints or height where supported.
- Transaction: one transaction.
- Validation: reject non-column symbols, ambiguous names, invalid level, negative
  height, and unsupported placement type.
- Smoke test: create one column on Level 1 and verify category/type/level.

### create_structural_beam

- Type: W.
- Handler: `CreateStructuralBeamHandler.cs`.
- MCP params: `type_id`/`type_name`, `start` and `end` points in mm,
  `level_id`/`level_name`, `usage` string `beam|brace|joist` optional,
  `z_offset_mm` optional.
- Response DTO: created ID, type, level, curve endpoints, length mm.
- Revit API strategy: resolve structural framing symbol, create a line curve,
  call family instance creation with beam structural type, set offsets where
  available.
- Transaction: one transaction.
- Validation: non-zero line length, structural framing symbol, valid level.
- Smoke test: create horizontal beam and verify length.

### create_structural_wall

- Type: W.
- Handler: `CreateStructuralWallHandler.cs`.
- MCP params: `wall_type_id`/`wall_type_name`, start/end mm, `level_id` or
  `level_name`, `height_mm`, `structural_usage` string
  `bearing|shear|combined`, optional `offset_mm`.
- Response DTO: wall ID, type, level, height, structural flag/usage.
- Revit API strategy: use Wall.Create or sibling wall creation helper, then set
  structural parameter/usage if writable.
- Transaction: one transaction.
- Validation: wall type only, height positive, line length positive.
- Smoke test: created wall has structural flag true where supported.

### create_foundation_isolated

- Type: W.
- Handler: `CreateFoundationIsolatedHandler.cs`.
- MCP params: foundation `type_id`/`type_name`, point mm, `level_id` or
  `level_name`, optional `host_column_id`, `rotation_deg`.
- Response DTO: created foundation ID, type, level, host ID if used.
- Revit API strategy: resolve structural foundation symbol and place point-based
  family instance. If host column is supplied, validate category and placement
  compatibility.
- Transaction: one transaction.
- Validation: reject non-foundation symbol and incompatible host.
- Smoke test: place isolated footing at origin.

### create_foundation_wall

- Type: W.
- Handler: `CreateFoundationWallHandler.cs`.
- MCP params: `wall_id` long required or start/end/level fallback,
  `foundation_type_id`/`foundation_type_name`, optional offsets.
- Response DTO: created foundation ID, host wall ID, type ID.
- Revit API strategy: prefer wall-foundation API or create a wall foundation
  hosted under the resolved wall when available. If the API surface differs,
  isolate compile-time branches.
- Transaction: one transaction.
- Validation: host must be a wall; reject curtain/in-place cases if unsupported.
- Smoke test: create foundation under a basic structural wall.

### create_rebar_set

- Type: W.
- Handler: `CreateRebarSetHandler.cs`.
- MCP params: `host_id` long required, `bar_type_id`/`bar_type_name`,
  `layout_rule` string, `spacing_mm`, `quantity`, cover offsets, optional curve
  points.
- Response DTO: rebar ID(s), host ID, bar type, layout, quantity.
- Revit API strategy: use Rebar creation APIs available in R22-R27. Prefer
  simple straight bars first; return unsupported for complex shape-driven input
  until explicitly implemented.
- Transaction: one transaction.
- Validation: host supports rebar, bar type exists, spacing/quantity valid.
- Smoke test: create simple straight rebar in a concrete host where available.

### create_rebar_stirrup

- Type: W.
- Handler: `CreateRebarStirrupHandler.cs`.
- MCP params: `host_id`, `bar_type_id`/`bar_type_name`, `shape_id`/`shape_name`,
  width/height/cover mm, spacing or quantity.
- Response DTO: rebar ID, shape ID, bar type, layout.
- Revit API strategy: shape-driven rebar where available. If shape lookup is
  ambiguous or API support differs, fail with a clear shape-resolution error.
- Transaction: one transaction.
- Validation: host supports rebar; shape has compatible parameters.
- Smoke test: dry model with no rebar shape returns clean missing-shape error.

### list_rebar

- Type: R.
- Handler: `ListRebarHandler.cs`.
- MCP params: `host_id` optional, `view_id` optional, `include_geometry` bool
  default false, `limit` default 500 max 5000.
- Response DTO: rebar items with ID, host ID, type, shape, bar diameter,
  quantity, spacing, length summary, visibility/view scope.
- Revit API strategy: collect Rebar elements optionally scoped by host/view.
- Transaction: none.
- Validation: IDs must resolve; cap limit and report `truncated`.
- Smoke test: blank model returns empty array.

### get_structural_loads

- Type: R.
- Handler: `GetStructuralLoadsHandler.cs`.
- MCP params: `element_id` optional, `load_type` optional
  `point|line|area|all`, `view_id` optional, `limit` default 500.
- Response DTO: loads with ID, type, host, force/moment components, units,
  coordinate system, warnings.
- Revit API strategy: collect load elements/categories available in the Revit
  structural API. Return unsupported categories cleanly for unavailable years.
- Transaction: none.
- Validation: limit cap; element/view filters must resolve.
- Smoke test: model with no loads returns zero counts.

### set_structural_load

- Type: W.
- Handler: `SetStructuralLoadHandler.cs`.
- MCP params: `action` string `create|update`, `load_id` optional for update,
  `load_type`, `host_id`, force/moment values, point/line/area geometry in mm,
  `case_name` or `case_id`.
- Response DTO: load ID, action, host ID, load case, values, warnings.
- Revit API strategy: use load creation/update APIs by load type. Validate load
  case before transaction.
- Transaction: one transaction.
- Validation: fail unsupported load types/year combinations, invalid host, and
  missing required geometry.
- Smoke test: unsupported load setup returns explicit unsupported/missing case
  error rather than crash.

### analyze_structural_connections

- Type: R.
- Handler: `AnalyzeStructuralConnectionsHandler.cs`.
- MCP params: `element_ids` optional, `category_filter` optional, `limit`
  default 500.
- Response DTO: connection summary, joined/intersecting framing, warnings,
  unsupported details.
- Revit API strategy: inspect joins, analytical connections where available, and
  spatial adjacency for structural framing/columns/walls.
- Transaction: none.
- Validation: cap limit and skip unsupported categories with warnings.
- Smoke test: simple column/beam model reports adjacency/join status.

### tag_structural_framing

- Type: W.
- Handler: `TagStructuralFramingHandler.cs`.
- MCP params: `view_id` optional defaults active view, `tag_type_id` optional,
  `element_ids` optional, `tag_columns` bool default false, `dry_run` bool
  default true.
- Response DTO: candidate IDs, created tag IDs, skipped IDs, warnings.
- Revit API strategy: similar to existing tag handlers but category-scoped to
  structural framing and optionally columns.
- Transaction: one transaction only when `dry_run=false`.
- Validation: view must support tags; tag family must match target category.
- Smoke test: dry-run returns candidate framing in active plan/section view.

## Wiring

- Add `StructuralTools` to `Program.cs`.
- Add `structural` to `KnownToolsets`, `DefaultOn`, and `WriteCapable`.
- Register 12 handlers in `CommandDispatcher`.
- Refresh golden snapshots and README tool tables.

## Acceptance Criteria

- Structural tools compile across R22-R27.
- Unsupported structural/rebar/load API cases return clear DTO errors.
- Creation tools validate type/category before opening transactions.
- Rebar and load tools have manual smoke notes if automated tests cannot cover
  them.

## Review Checklist

- BLOCKER: direct `ElementId.IntegerValue`/`.Value` usage.
- BLOCKER: rebar/load API branch does not compile for one Revit target.
- MAJOR: structural family placement ignores `FamilyPlacementType`.
- MAJOR: load units are not documented or converted.
- MAJOR: ambiguous type/name lookup selects the first match.

## Known Risks

- Rebar and load APIs are the highest cross-version risk in this wave.
- Some structural families are host/work-plane/face based. Category validation
  alone is not enough for reliable placement.

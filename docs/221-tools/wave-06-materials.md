# Wave 06 - Materials Target Implementation Spec

## Status

- Status: pending implementation spec.
- Target toolset: `materials`.
- Tool count: 10.
- Default exposure target: default-on.
- Write capability target: write-capable.
- Assigned implementation surface: future source changes in `src/shared/Handlers/`, `src/server/Program.cs`, `src/shared/Infrastructure/CommandDispatcher.cs`, `src/server/ToolsetFilter.cs`, tests, and golden snapshots.
- Current documentation-only task: this file is a target spec. It does not implement handlers or wiring.

## Scope

Wave 06 adds first-class material management and material takeoff tools. The current repo has only `get_material_quantities` under `query`, which groups area/volume by material for one human category name. The new `materials` toolset should own material inventory, material property reads, creation/duplication, appearance and identity writes, physical/thermal asset writes, element assignment, and project-wide takeoff.

In scope:

- List and inspect Revit `Material` elements.
- Create and duplicate materials.
- Set shading/graphics appearance fields and fill-pattern references.
- Set common identity parameters.
- Create or replace structural and thermal property assets.
- Assign a material to element material parameters or compound-structure layers.
- Compute project material takeoff by material with optional category filtering.

Out of scope:

- Editing arbitrary rendering asset internals beyond stable material shading properties.
- Purging unused materials. That belongs to Wave 15 `purge_unused`.
- Creating fill patterns.
- Editing family documents.
- Source changes in this documentation task.

## Dependencies

Read and align with these existing repo conventions:

- `CLAUDE.md`: handler-per-tool, DTO-only returns, guarded JSON parsing, RevitCompat for ids, external units.
- `docs/roadmap-221-tools.md`: Wave 06 tool list and toolset target.
- `src/server/Program.cs`: MCP wrapper and toolset registration style.
- `src/server/ToolsetFilter.cs`: default and write-capable toolset gating.
- `src/shared/Infrastructure/CommandDispatcher.cs`: explicit handler registration.
- Nearby handlers:
  - `GetMaterialQuantitiesHandler.cs`
  - `ListLoadedFamiliesHandler.cs`
  - `DuplicateFamilyTypeHandler.cs`
  - `SetElementParameterValuesHandler.cs`
  - `GetElementParametersHandler.cs`
  - `ExportElementsDataHandler.cs`

Implementation conventions for every handler:

- Implement `IRevitCommand`.
- `Name` equals the MCP command name.
- `ParametersSchema` is a JSON object schema string.
- Parse with `string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson)` inside `try/catch JsonException`.
- Return only DTOs, never raw Revit API objects.
- Use `RevitCompat` for all ElementIds.
- Use external units:
  - area: m2
  - volume: m3
  - length: mm
  - density: kg/m3
  - modulus/stress: MPa
  - thermal conductivity: W/(m*K)
  - specific heat: J/(kg*K)
- Use `UnitUtils.ConvertToInternalUnits` with `UnitTypeId` where available for non-simple asset units. If a target Revit API lacks a specific `UnitTypeId`, fail with a clear unsupported-unit message instead of guessing silently.

## Toolset Contract

Add a new server wrapper:

```csharp
[McpServerToolType, Toolset("materials")]
public class MaterialsTools
{
    // 10 methods listed below.
}
```

Add toolset gating in `src/server/ToolsetFilter.cs`:

- Add `"materials"` to `KnownToolsets`.
- Add `"materials"` to `DefaultOn`.
- Add `"materials"` to `WriteCapable`.

Add registration in `src/server/Program.cs`:

- `RegisterToolsets`: `if (enabled.Contains("materials")) mcp = mcp.WithTools<MaterialsTools>();`
- `ResolveRegisteredToolTypes`: `if (enabled.Contains("materials")) types.Add(typeof(MaterialsTools));`

Add dispatcher registration in `src/shared/Infrastructure/CommandDispatcher.cs` after Wave 05 sheets, or after the current Phase 16 export block if Wave 05 is not implemented yet:

```csharp
// Phase 18: Materials
Register(new Handlers.ListMaterialsHandler());
Register(new Handlers.GetMaterialPropertiesHandler());
Register(new Handlers.CreateMaterialHandler());
Register(new Handlers.DuplicateMaterialHandler());
Register(new Handlers.SetMaterialAppearanceHandler());
Register(new Handlers.SetMaterialIdentityHandler());
Register(new Handlers.SetMaterialStructuralAssetHandler());
Register(new Handlers.SetMaterialThermalAssetHandler());
Register(new Handlers.AssignMaterialToElementHandler());
Register(new Handlers.GetMaterialTakeoffHandler());
```

Read-only MCP methods:

- `list_materials`
- `get_material_properties`
- `get_material_takeoff`

Write MCP methods:

- `create_material`
- `duplicate_material`
- `set_material_appearance`
- `set_material_identity`
- `set_material_structural_asset`
- `set_material_thermal_asset`
- `assign_material_to_element`

## Tool Specs

### `list_materials`

- Handler file: `src/shared/Handlers/ListMaterialsHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_materials", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListMaterials(
    string namePattern = "",
    string classFilter = "",
    bool includeAssets = true,
    bool includeUseCount = false,
    int limit = 1000)
```

- Wrapper payload: `{ name_pattern = namePattern, class_filter = classFilter, include_assets = includeAssets, include_use_count = includeUseCount, limit }`
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "name_pattern": { "type": "string" },
    "class_filter": { "type": "string" },
    "include_assets": { "type": "boolean", "default": true },
    "include_use_count": { "type": "boolean", "default": false },
    "limit": { "type": "integer", "default": 1000, "minimum": 1, "maximum": 10000 }
  }
}
```

- Response DTO:

```json
{
  "total": 42,
  "returned": 42,
  "limit_hit": false,
  "materials": [
    {
      "id": 123,
      "name": "Concrete - Cast-in-Place",
      "material_class": "Concrete",
      "material_category": "",
      "color": { "red": 180, "green": 180, "blue": 180 },
      "transparency": 0,
      "appearance_asset_id": 456,
      "structural_asset_id": 457,
      "thermal_asset_id": 458,
      "use_count": 12
    }
  ]
}
```

- Revit API strategy:
  - Collect `Material` with `FilteredElementCollector(doc).OfClass(typeof(Material))`.
  - Filter by case-insensitive name substring and material class substring.
  - Read `MaterialClass`, `MaterialCategory`, `Color`, `Transparency`, `AppearanceAssetId`, `StructuralAssetId`, and `ThermalAssetId`.
  - If `include_use_count`, scan non-type elements and count elements where `GetMaterialIds(false)` includes the material id.
- Transaction behavior: none.
- Validation/failure modes:
  - Clamp `limit` to 1-10000.
  - Skip materials that fail introspection and increment `skipped`.
- Cross-version notes:
  - Asset id properties are stable across Revit 2022-2027, but can be invalid ids. Return `null` for invalid ids.
- Smoke/test expectations:
  - Golden snapshot exposes read-only `list_materials`.
  - Revit smoke compares returned total against a direct material collector in a test model.

### `get_material_properties`

- Handler file: `src/shared/Handlers/GetMaterialPropertiesHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "get_material_properties", ReadOnly = true, Idempotent = true)]
public static async Task<string> GetMaterialProperties(
    long? materialId = null,
    string materialName = "",
    bool includeAssets = true,
    bool includeParameters = true)
```

- Wrapper payload: `{ material_id = materialId, material_name = materialName, include_assets = includeAssets, include_parameters = includeParameters }`
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "material_id": { "type": "integer" },
    "material_name": { "type": "string" },
    "include_assets": { "type": "boolean", "default": true },
    "include_parameters": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "id": 123,
  "name": "Concrete",
  "material_class": "Concrete",
  "material_category": "",
  "color": { "red": 180, "green": 180, "blue": 180 },
  "transparency": 0,
  "shininess": 64,
  "smoothness": 50,
  "patterns": {
    "surface_foreground_pattern_id": 10,
    "surface_background_pattern_id": null,
    "cut_foreground_pattern_id": 11,
    "cut_background_pattern_id": null
  },
  "identity": {
    "manufacturer": "",
    "model": "",
    "cost": "",
    "keynote": "",
    "mark": "",
    "url": ""
  },
  "appearance_asset": { "id": 456, "name": "Default" },
  "structural_asset": { "id": 457, "name": "Concrete", "density_kg_per_m3": 2400.0 },
  "thermal_asset": { "id": 458, "name": "Concrete", "conductivity_w_per_m_k": 1.4 },
  "parameters": []
}
```

- Revit API strategy:
  - Resolve material by id first, else exact case-insensitive name.
  - Read common material properties directly.
  - Read identity through built-in parameters when available and case-insensitive parameter fallback:
    - Manufacturer
    - Model
    - Cost
    - Keynote
    - Mark
    - URL
  - For structural/thermal assets, resolve `PropertySetElement` from `Material.StructuralAssetId` and `Material.ThermalAssetId`, then read the asset object defensively.
  - For appearance, resolve `AppearanceAssetElement` and return id/name only unless implementation can safely read asset properties without raw serialization.
- Transaction behavior: none.
- Validation/failure modes:
  - Fail if neither material id nor name is supplied.
  - Fail if name has zero matches or more than one match.
  - Fail on invalid id range.
- Cross-version notes:
  - Use reflection-safe reads for optional asset properties that differ between API versions.
- Smoke/test expectations:
  - Read a known material by id and by name and compare ids.
  - Verify no raw `Asset`, `Material`, or `Parameter` object appears in serialized output.

### `create_material`

- Handler file: `src/shared/Handlers/CreateMaterialHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "create_material", Destructive = false)]
public static async Task<string> CreateMaterial(
    string name,
    string materialClass = "",
    string materialCategory = "",
    int? red = null,
    int? green = null,
    int? blue = null,
    int? transparency = null)
```

- Wrapper payload: `{ name, material_class = materialClass, material_category = materialCategory, red, green, blue, transparency }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["name"],
  "properties": {
    "name": { "type": "string" },
    "material_class": { "type": "string" },
    "material_category": { "type": "string" },
    "red": { "type": "integer", "minimum": 0, "maximum": 255 },
    "green": { "type": "integer", "minimum": 0, "maximum": 255 },
    "blue": { "type": "integer", "minimum": 0, "maximum": 255 },
    "transparency": { "type": "integer", "minimum": 0, "maximum": 100 }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "material_id": 123,
  "name": "New Concrete",
  "material_class": "Concrete",
  "material_category": "",
  "color": { "red": 180, "green": 180, "blue": 180 },
  "transparency": 0,
  "error": null
}
```

- Revit API strategy:
  - Preflight duplicate material name with exact ordinal match.
  - Create via `Material.Create(doc, name)`.
  - Resolve returned id to `Material`.
  - Set `MaterialClass`, `MaterialCategory`, `Color`, and `Transparency` when supplied.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if name is blank.
  - Fail if name already exists.
  - Fail if RGB channels are incomplete or outside 0-255.
  - Fail if transparency outside 0-100.
- Cross-version notes:
  - `Material.Create` and basic material properties are stable across Revit 2022-2027.
- Smoke/test expectations:
  - Create a uniquely named material and verify `list_materials(namePattern=...)` returns it.

### `duplicate_material`

- Handler file: `src/shared/Handlers/DuplicateMaterialHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "duplicate_material", Destructive = false)]
public static async Task<string> DuplicateMaterial(
    string newName,
    long? sourceMaterialId = null,
    string sourceMaterialName = "")
```

- Wrapper payload: `{ source_material_id = sourceMaterialId, source_material_name = sourceMaterialName, new_name = newName }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["new_name"],
  "properties": {
    "source_material_id": { "type": "integer" },
    "source_material_name": { "type": "string" },
    "new_name": { "type": "string" }
  }
}
```

- Response DTO:

```json
{
  "duplicated": true,
  "source_material_id": 100,
  "new_material_id": 200,
  "new_name": "Concrete Copy",
  "error": null
}
```

- Revit API strategy:
  - Resolve source material by id or exact case-insensitive name.
  - Preflight duplicate `new_name`.
  - Use `sourceMaterial.Duplicate(newName)` and resolve the returned material/id according to the concrete API signature in this repo's Revit references.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if source is missing or ambiguous.
  - Fail if `new_name` is blank or already exists.
- Cross-version notes:
  - Compile-check the return type of `Material.Duplicate` against all target Revit package references; adapt locally without reflection if the signature is uniform.
- Smoke/test expectations:
  - Duplicate an existing material and verify copied color/transparency/asset ids match source.

### `set_material_appearance`

- Handler file: `src/shared/Handlers/SetMaterialAppearanceHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "set_material_appearance", Destructive = false)]
public static async Task<string> SetMaterialAppearance(
    long? materialId = null,
    string materialName = "",
    int? red = null,
    int? green = null,
    int? blue = null,
    int? transparency = null,
    int? shininess = null,
    int? smoothness = null,
    bool? useRenderAppearanceForShading = null,
    long? surfaceForegroundPatternId = null,
    long? surfaceBackgroundPatternId = null,
    long? cutForegroundPatternId = null,
    long? cutBackgroundPatternId = null)
```

- Wrapper payload uses the same names in snake_case.
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "material_id": { "type": "integer" },
    "material_name": { "type": "string" },
    "red": { "type": "integer", "minimum": 0, "maximum": 255 },
    "green": { "type": "integer", "minimum": 0, "maximum": 255 },
    "blue": { "type": "integer", "minimum": 0, "maximum": 255 },
    "transparency": { "type": "integer", "minimum": 0, "maximum": 100 },
    "shininess": { "type": "integer", "minimum": 0, "maximum": 128 },
    "smoothness": { "type": "integer", "minimum": 0, "maximum": 128 },
    "use_render_appearance_for_shading": { "type": "boolean" },
    "surface_foreground_pattern_id": { "type": "integer" },
    "surface_background_pattern_id": { "type": "integer" },
    "cut_foreground_pattern_id": { "type": "integer" },
    "cut_background_pattern_id": { "type": "integer" }
  }
}
```

- Response DTO:

```json
{
  "updated": true,
  "material_id": 123,
  "name": "Concrete",
  "changed": {
    "color": "set",
    "transparency": "set",
    "surface_foreground_pattern_id": "set"
  },
  "error": null
}
```

- Revit API strategy:
  - Resolve material.
  - Set `Material.Color` only when all RGB channels are supplied.
  - Set `Transparency`, `Shininess`, `Smoothness`, and `UseRenderAppearanceForShading` when supplied and property setters are available.
  - Resolve each fill pattern id to `FillPatternElement`; reject model patterns if Revit requires drafting patterns for material surface/cut patterns.
  - Set foreground/background surface/cut pattern ids where API properties exist.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if no update field is supplied.
  - Fail if RGB is partial.
  - Fail if numeric ranges are invalid.
  - Fail if pattern id is invalid or not a fill pattern.
- Cross-version notes:
  - Pattern property names changed historically. Target Revit 2022-2027 should expose foreground/background pattern ids; compile-check all shells.
  - Do not use `AppearanceAssetEditScope` in the first implementation unless tests prove stable behavior across all target versions.
- Smoke/test expectations:
  - Set color/transparency on a duplicate material and verify via `get_material_properties`.

### `set_material_identity`

- Handler file: `src/shared/Handlers/SetMaterialIdentityHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "set_material_identity", Destructive = false)]
public static async Task<string> SetMaterialIdentity(
    long? materialId = null,
    string materialName = "",
    string manufacturer = null,
    string model = null,
    string cost = null,
    string keynote = null,
    string mark = null,
    string url = null,
    string materialClass = null,
    string materialCategory = null)
```

- Wrapper payload uses snake_case keys.
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "material_id": { "type": "integer" },
    "material_name": { "type": "string" },
    "manufacturer": { "type": "string" },
    "model": { "type": "string" },
    "cost": { "type": "string" },
    "keynote": { "type": "string" },
    "mark": { "type": "string" },
    "url": { "type": "string" },
    "material_class": { "type": "string" },
    "material_category": { "type": "string" }
  }
}
```

- Response DTO:

```json
{
  "updated": true,
  "material_id": 123,
  "name": "Concrete",
  "fields": {
    "manufacturer": { "status": "set" },
    "keynote": { "status": "set" },
    "material_class": { "status": "set" }
  },
  "error": null
}
```

- Revit API strategy:
  - Resolve material.
  - Set `Material.MaterialClass` and `Material.MaterialCategory` directly when supplied.
  - Set identity parameters by stable parameter names with case-insensitive fallback:
    - `Manufacturer`
    - `Model`
    - `Cost`
    - `Keynote`
    - `Mark`
    - `URL`
  - Accept all identity values as strings. Let Revit parameter storage conversion handle numeric cost only if the target parameter is not string.
- Transaction behavior: one transaction; rollback on hard failure.
- Validation/failure modes:
  - Fail if no identity field supplied.
  - Return field-level status for not-found/read-only parameters; do not fail the whole tool if at least one field was set.
  - Fail if all supplied fields are not found/read-only.
- Cross-version notes:
  - Built-in parameter ids for material identity can differ; prefer name fallback over hard-coding only enum values.
- Smoke/test expectations:
  - Set manufacturer/keynote on a duplicate material and verify via `get_material_properties`.

### `set_material_structural_asset`

- Handler file: `src/shared/Handlers/SetMaterialStructuralAssetHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "set_material_structural_asset", Destructive = false)]
public static async Task<string> SetMaterialStructuralAsset(
    long? materialId = null,
    string materialName = "",
    string assetName = "",
    string structuralClass = "generic",
    double? densityKgPerM3 = null,
    double? youngModulusMpa = null,
    double? poissonRatio = null,
    double? shearModulusMpa = null)
```

- Wrapper payload uses snake_case keys.
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "material_id": { "type": "integer" },
    "material_name": { "type": "string" },
    "asset_name": { "type": "string" },
    "structural_class": { "type": "string", "default": "generic" },
    "density_kg_per_m3": { "type": "number" },
    "young_modulus_mpa": { "type": "number" },
    "poisson_ratio": { "type": "number" },
    "shear_modulus_mpa": { "type": "number" }
  }
}
```

- Response DTO:

```json
{
  "updated": true,
  "material_id": 123,
  "structural_asset_id": 456,
  "asset_name": "Concrete structural",
  "fields": {
    "density_kg_per_m3": { "status": "set", "value": 2400.0 },
    "young_modulus_mpa": { "status": "set", "value": 30000.0 }
  },
  "error": null
}
```

- Revit API strategy:
  - Resolve material.
  - Create a new `StructuralAsset` rather than mutating an asset shared by other materials.
  - Set `Name`, `StructuralAssetClass`, and isotropic `StructuralBehavior` when available.
  - Set density, Young's modulus, Poisson ratio, and shear modulus.
  - For isotropic vector properties, set all XYZ components to the same converted value.
  - Create a `PropertySetElement` from the asset and assign its id to `Material.StructuralAssetId`.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if no structural field supplied.
  - Fail for negative density/modulus/shear values.
  - Fail if Poisson ratio is outside 0-0.5.
  - Fail if required Revit API asset property is unavailable in a target version.
- Cross-version notes:
  - Compile-check the exact `StructuralAsset` property names and unit expectations in all target references.
  - Use `UnitUtils.ConvertToInternalUnits` with relevant `UnitTypeId` members when available; do not silently use hard-coded conversions for physical asset units without a test.
- Smoke/test expectations:
  - Set structural density on a duplicate material and verify a non-invalid `structural_asset_id` plus returned density from `get_material_properties`.

### `set_material_thermal_asset`

- Handler file: `src/shared/Handlers/SetMaterialThermalAssetHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "set_material_thermal_asset", Destructive = false)]
public static async Task<string> SetMaterialThermalAsset(
    long? materialId = null,
    string materialName = "",
    string assetName = "",
    double? conductivityWPerMK = null,
    double? specificHeatJPerKgK = null,
    double? emissivity = null,
    double? permeability = null,
    double? densityKgPerM3 = null)
```

- Wrapper payload uses snake_case keys.
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "material_id": { "type": "integer" },
    "material_name": { "type": "string" },
    "asset_name": { "type": "string" },
    "conductivity_w_per_m_k": { "type": "number" },
    "specific_heat_j_per_kg_k": { "type": "number" },
    "emissivity": { "type": "number" },
    "permeability": { "type": "number" },
    "density_kg_per_m3": { "type": "number" }
  }
}
```

- Response DTO:

```json
{
  "updated": true,
  "material_id": 123,
  "thermal_asset_id": 789,
  "asset_name": "Concrete thermal",
  "fields": {
    "conductivity_w_per_m_k": { "status": "set", "value": 1.4 },
    "emissivity": { "status": "set", "value": 0.9 }
  },
  "error": null
}
```

- Revit API strategy:
  - Resolve material.
  - Create a new `ThermalAsset` rather than mutating a shared asset.
  - Set thermal conductivity, specific heat, emissivity, permeability, and density when supplied and supported.
  - Create a `PropertySetElement` from the asset and assign its id to `Material.ThermalAssetId`.
- Transaction behavior: one transaction.
- Validation/failure modes:
  - Fail if no thermal field supplied.
  - Fail for negative conductivity, specific heat, permeability, or density.
  - Fail if emissivity is outside 0-1.
  - Return field-level unsupported status if a property does not exist; fail if none were set.
- Cross-version notes:
  - Compile-check property names against all target Revit references.
  - Use UnitUtils for non-dimensionless fields where Revit expects internal units.
- Smoke/test expectations:
  - Set emissivity and conductivity on a duplicate material and verify through `get_material_properties`.

### `assign_material_to_element`

- Handler file: `src/shared/Handlers/AssignMaterialToElementHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "assign_material_to_element", Destructive = false)]
public static async Task<string> AssignMaterialToElement(
    long[] elementIds,
    long? materialId = null,
    string materialName = "",
    string parameterName = "",
    long? compoundLayerIndex = null,
    bool allowTypeMutation = false,
    string duplicateTypeName = "")
```

- Wrapper payload: `{ element_ids = elementIds, material_id = materialId, material_name = materialName, parameter_name = parameterName, compound_layer_index = compoundLayerIndex, allow_type_mutation = allowTypeMutation, duplicate_type_name = duplicateTypeName }`
- ParametersSchema:

```json
{
  "type": "object",
  "required": ["element_ids"],
  "properties": {
    "element_ids": { "type": "array", "items": { "type": "integer" } },
    "material_id": { "type": "integer" },
    "material_name": { "type": "string" },
    "parameter_name": { "type": "string" },
    "compound_layer_index": { "type": "integer", "minimum": 0 },
    "allow_type_mutation": { "type": "boolean", "default": false },
    "duplicate_type_name": { "type": "string" }
  }
}
```

- Response DTO:

```json
{
  "updated": true,
  "material_id": 123,
  "results": [
    {
      "element_id": 500,
      "status": "set",
      "mode": "parameter",
      "parameter_name": "Structural Material"
    }
  ],
  "error": null
}
```

- Revit API strategy:
  - Resolve material by id or name.
  - Resolve all element ids up front and reject missing/non-representable ids before transaction.
  - Parameter assignment mode:
    - If `parameter_name` is supplied, find an element parameter with that name.
    - Else find the first writable `StorageType.ElementId` material-like parameter by name containing `Material`, with known fallbacks such as `Structural Material`.
    - Set parameter to material id.
  - Compound-structure mode:
    - If `compound_layer_index` is supplied, get the element type and its `CompoundStructure`.
    - Reject unless the type is a compound host type such as wall, floor, roof, or ceiling.
    - If `allow_type_mutation=false`, require `duplicate_type_name` and duplicate the type before modifying it, then assign the element to the duplicated type.
    - Set the selected layer material id and call `SetCompoundStructure`.
  - Do not mutate a shared type unless `allow_type_mutation=true`.
- Transaction behavior: one transaction; atomic across all supplied elements.
- Validation/failure modes:
  - Fail if `element_ids` is empty.
  - Fail if material cannot be resolved.
  - Fail if both parameter and compound strategies are impossible for an element.
  - Fail if compound layer index is outside range.
  - Fail if a duplicate type name already exists.
  - Return per-element statuses only after a committed transaction.
- Cross-version notes:
  - Compound structure APIs are stable, but exact host type coverage should be compile-checked.
  - Element type reassignment should use existing project helper patterns from type-changing handlers.
- Smoke/test expectations:
  - Assign material to a wall compound layer using a duplicated wall type, then verify material takeoff includes the material.
  - Assign material through a direct writable material parameter on a test family instance if available.

### `get_material_takeoff`

- Handler file: `src/shared/Handlers/GetMaterialTakeoffHandler.cs`
- MCP wrapper:

```csharp
[McpServerTool(Name = "get_material_takeoff", ReadOnly = true, Idempotent = true)]
public static async Task<string> GetMaterialTakeoff(
    string categoryFilter = "",
    string materialNamePattern = "",
    bool includeElements = false,
    int elementLimit = 100)
```

- Wrapper payload: `{ category_filter = categoryFilter, material_name_pattern = materialNamePattern, include_elements = includeElements, element_limit = elementLimit }`
- ParametersSchema:

```json
{
  "type": "object",
  "properties": {
    "category_filter": { "type": "string" },
    "material_name_pattern": { "type": "string" },
    "include_elements": { "type": "boolean", "default": false },
    "element_limit": { "type": "integer", "default": 100, "minimum": 0, "maximum": 10000 }
  }
}
```

- Response DTO:

```json
{
  "material_count": 3,
  "element_count": 120,
  "materials": [
    {
      "material_id": 123,
      "material_name": "Concrete",
      "total_area_m2": 250.5,
      "total_volume_m3": 34.2,
      "element_count": 40,
      "categories": [
        { "category": "Walls", "area_m2": 100.0, "volume_m3": 20.0, "element_count": 10 }
      ],
      "elements": [
        { "element_id": 500, "category": "Walls", "area_m2": 12.3, "volume_m3": 2.1 }
      ]
    }
  ],
  "truncated_elements": false
}
```

- Revit API strategy:
  - Collect all non-type elements, optionally filtered by case-insensitive `Category.Name`.
  - For each element, call `GetMaterialIds(false)`.
  - For each material id, read material, call `GetMaterialArea(materialId, false)` and `GetMaterialVolume(materialId)`.
  - Convert square feet to m2 with `* 0.09290304`.
  - Convert cubic feet to m3 with `* 0.02831685`.
  - Group by material id, then by category.
  - If `include_elements`, include per-element rows up to `element_limit`.
- Transaction behavior: none.
- Validation/failure modes:
  - Clamp `element_limit` to 0-10000.
  - Skip elements that throw on material queries and include `skipped_elements`.
  - Material name pattern filters after material resolution.
- Cross-version notes:
  - Existing `GetMaterialQuantitiesHandler` uses the same Revit material quantity APIs and conversion approach; keep behavior aligned but broaden scope beyond one category.
- Smoke/test expectations:
  - Compare output for `categoryFilter="Walls"` against existing `get_material_quantities("Walls")` on the same model; totals should be within rounding tolerance.

## Wiring

Future implementation should wire in this order:

1. Add all 10 handler files.
2. Add the Phase 18 dispatcher registration block.
3. Add `MaterialsTools` wrapper class in `Program.cs`.
4. Add `materials` to `ToolsetFilter.KnownToolsets`, `DefaultOn`, and `WriteCapable`.
5. Add or update golden snapshot tests so `materials` appears as default-on and write-capable.
6. Add focused tests for parameter/object schema exposure where the current test harness supports it.
7. Update README/tool counts only after source implementation is complete and verified.

## Acceptance Criteria

- All 10 handlers compile for Revit 2022-2027 plugin shells.
- `materials` is visible by default through MCP tool discovery.
- `--read-only` removes `materials` because the toolset is write-capable.
- Read-only material tools are individually marked `ReadOnly = true, Idempotent = true`.
- All write handlers use transactions and roll back on hard failures.
- Material and element ids use `RevitCompat`.
- All outputs are DTOs and serialize without raw Revit objects.
- Material takeoff units are m2 and m3, aligned with existing `get_material_quantities`.
- Asset write tools never mutate a shared structural/thermal asset in place unless a later implementation explicitly introduces a safe opt-in.
- Ambiguous material names fail with "use material_id".

## Review Checklist

- Handler names, command names, wrapper names, and schemas match exactly.
- Material resolution by id handles Revit 2026+ long ElementIds.
- Name-based resolution is exact and ambiguity-safe.
- Appearance setters validate RGB/transparency/pattern ids before transaction.
- Identity setters report field-level read-only/not-found statuses.
- Structural/thermal assets are created as new `PropertySetElement` instances and assigned to the material.
- Compound-structure assignment does not mutate shared types unless explicitly requested.
- Takeoff results are grouped by material id, not only by material name.
- Golden snapshot marks `materials` as default-on/write-capable and preserves read-only flags.
- No direct `.IntegerValue` or `.Value` ElementId access.
- No source task should run `dotnet build` until implementation phase.

## Known Risks

- Revit material physical/thermal asset unit expectations are easy to get wrong. Implementation must compile and smoke-test asset round trips instead of assuming fixed conversion constants.
- Appearance render assets are deep Revit asset graphs. This wave targets stable material shading and pattern properties first; deeper render asset editing should be a later tool if needed.
- Material assignment to compound structures can affect all elements of a type. The spec requires duplication or explicit `allow_type_mutation=true` to avoid accidental model-wide changes.
- Material takeoff values depend on Revit's material quantity APIs and model geometry. Some categories/elements return zero area or volume even when they visually contain material.

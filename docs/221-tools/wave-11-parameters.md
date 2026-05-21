# Wave 11 - Shared + Project Parameters Target Implementation Spec

## Status

Target implementation spec. This wave is pending and should add the new
default-on, write-capable `parameters` toolset.

Roadmap count: 8 tools.

Baseline overlap: the repo already exposes `list_project_parameters` in the
`query` toolset through `ListProjectParametersHandler.cs`. Keep that existing
tool stable for compatibility. The new `list_project_parameter_bindings` tool is
a richer parameters-toolset surface and should not replace or rename the
existing query tool.

## Scope

In scope:

- Read shared parameter definitions from the active Revit shared-parameter file
  or an explicit absolute shared-parameter file path.
- Create a missing shared-parameter file when a creation/binding tool requests
  it, then verify it by reopening it through Revit's API.
- Create shared parameter definitions with stable GUID identity.
- Bind shared parameters to document categories through `Document.ParameterBindings`.
- Preserve the difference between instance and type bindings.
- List project parameter bindings with category, binding kind, GUID, and data
  type metadata.
- Remove whole bindings or selected categories from an existing binding.
- Export the shared parameter file as structured DTO data.
- Set shared parameter values by GUID, including unit-aware double conversion.

Out of scope:

- Family document parameter management through `FamilyManager`.
- Global parameters.
- Schedule field manipulation for newly created parameters.
- Revit UI dialogs for editing project parameters.
- A fake implementation of pure non-shared project parameter creation if the
  public Revit API path is not verified.

## Dependencies

- `CLAUDE.md` handler rules: one handler per tool, guarded JSON parse, DTO-only
  results, no raw Revit objects.
- `docs/roadmap-221-tools.md` Wave 11 inventory and common unit contract.
- Existing handler patterns:
  - `src/shared/Handlers/ListProjectParametersHandler.cs`
  - `src/shared/Handlers/GetElementParametersHandler.cs`
  - `src/shared/Handlers/GetTypeParametersHandler.cs`
  - `src/shared/Handlers/SetElementParameterValuesHandler.cs`
- Wiring patterns:
  - `src/server/Program.cs`
  - `src/shared/Infrastructure/CommandDispatcher.cs`
  - `src/server/ToolsetFilter.cs`
- Cross-version ID helper:
  - `src/shared/Infrastructure/RevitCompat.cs`
- Revit API surfaces verified in local package XML:
  - `Application.SharedParametersFilename`
  - `Application.OpenSharedParameterFile()`
  - `DefinitionFile.Groups`
  - `Definitions.Create(ExternalDefinitionCreationOptions)`
  - `ExternalDefinitionCreationOptions.GUID`
  - `Document.ParameterBindings`
  - `BindingMap.Insert`, `BindingMap.ReInsert`, `BindingMap.Remove`
  - `Application.Create.NewCategorySet()`
  - `Application.Create.NewInstanceBinding(CategorySet)`
  - `Application.Create.NewTypeBinding(CategorySet)`
  - `SharedParameterElement.Create(Document, ExternalDefinition)`

## Toolset Contract

- Toolset name: `parameters`.
- Server wrapper class: `ParametersTools` in `src/server/Program.cs`.
- Toolset attribute: `[McpServerToolType, Toolset("parameters")]`.
- Dispatcher block: add `// Wave 11: Parameters` registrations in
  `CommandDispatcher.cs`.
- Toolset gating:
  - Add `parameters` to `ToolsetFilter.KnownToolsets`.
  - Add `parameters` to `ToolsetFilter.DefaultOn`.
  - Add `parameters` to `ToolsetFilter.WriteCapable`.
  - Update `ToolsetFilterTests` expected toolset count and write-capable
    expectations when implementation work reaches tests.
- Public IDs:
  - Element IDs and category IDs are `long`.
  - Shared parameter GUIDs are canonical lowercase `D` format strings in DTOs.
- Parameter group IDs:
  - MCP input uses Forge group IDs when possible, for example
    `autodesk.parameter.group:pg_data`.
  - Empty `parameterGroupId` defaults to `GroupTypeId.Data`.
- Data type IDs:
  - MCP input uses Forge data type IDs, for example `autodesk.spec.aec:string`.
  - The implementation may provide a small alias map for `string`, `text`,
    `integer`, `number`, `length`, `area`, `volume`, `angle`, `yesno`, and
    `url`, but the response must include the resolved Forge type ID.
- Shared parameter file path:
  - `sharedParameterFilePath` must be absolute when supplied.
  - If omitted, use `app.Application.SharedParametersFilename`.
  - If both are empty and a creation tool has `createFileIfMissing=true`, use:
    `%LOCALAPPDATA%\RvtMcp\shared-parameters.txt`.
  - If the implementation temporarily changes
    `app.Application.SharedParametersFilename`, restore the original value in
    `finally`.
- Missing shared-parameter file creation:
  - Create the parent directory if needed.
  - Create a minimal Revit shared parameter file with this ASCII header:

```text
# This is a Revit shared parameter file.
# Do not edit manually.
*META	VERSION	MINVERSION
META	2	1
*GROUP	ID	NAME
*PARAM	GUID	NAME	DATATYPE	DATACATEGORY	GROUP	VISIBLE	DESCRIPTION	USERMODIFIABLE	HIDEWHENNOVALUE
```

  - After writing the file, call `OpenSharedParameterFile()` and fail if Revit
    still returns null.
- Binding behavior:
  - Insert new bindings with `BindingMap.Insert`.
  - For existing bindings, merge categories and use `BindingMap.ReInsert` only
    when the tool contract allows rebinding.
  - Never silently change an instance binding into a type binding or the reverse.
- Write transaction names:
  - `Bimwright: bind shared parameter`
  - `Bimwright: remove parameter binding`
  - `Bimwright: set parameter by GUID`
  - File-only shared parameter creation does not need a `Transaction` unless it
    also creates a `SharedParameterElement` in the project.

## Tool Specs

### `list_shared_parameters`

- Type: read-only.
- Proposed handler: `src/shared/Handlers/ListSharedParametersHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_shared_parameters", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListSharedParameters(
    string sharedParameterFilePath = "",
    string groupName = "",
    bool includeBindings = true,
    int limit = 1000)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "sharedParameterFilePath": {
      "type": "string",
      "description": "Optional absolute .txt path. If omitted, uses Revit Application.SharedParametersFilename."
    },
    "groupName": {
      "type": "string",
      "description": "Optional exact shared-parameter group name filter."
    },
    "includeBindings": {
      "type": "boolean",
      "default": true
    },
    "limit": {
      "type": "integer",
      "default": 1000,
      "minimum": 1,
      "maximum": 5000
    }
  }
}
```

- Response DTO:

```json
{
  "sharedParameterFilePath": "C:\\path\\shared-parameters.txt",
  "exists": true,
  "groupCount": 2,
  "definitionCount": 12,
  "returned": 12,
  "includeBindings": true,
  "definitions": [
    {
      "name": "Fire Rating",
      "guid": "11111111-2222-3333-4444-555555555555",
      "groupName": "Bimwright",
      "dataTypeId": "autodesk.spec.aec:string",
      "dataTypeLabel": "Text",
      "visible": true,
      "userModifiable": true,
      "hideWhenNoValue": false,
      "description": "Door fire rating",
      "isBound": true,
      "bindingKind": "instance",
      "parameterGroupId": "autodesk.parameter.group:pg_data",
      "categories": [
        { "id": -2000014, "name": "Doors" }
      ]
    }
  ],
  "warnings": []
}
```

- Revit API strategy:
  - Resolve the shared parameter file with the toolset helper.
  - Call `app.Application.OpenSharedParameterFile()`.
  - Iterate `DefinitionFile.Groups` and each group's `Definitions`.
  - Cast definitions to `ExternalDefinition` to read `GUID`, visibility,
    modifiability, description, and data type.
  - If `includeBindings=true`, iterate `doc.ParameterBindings.ForwardIterator()`
    once and build a GUID-to-binding map from `ExternalDefinition.GUID`.
- Transaction behavior: none.
- Validation and failure modes:
  - Fail if no document is open.
  - Fail if `sharedParameterFilePath` is supplied but not absolute.
  - Return a structured success DTO with `exists=false` only when a path is
    resolved but the file is missing and this is a read tool. Do not create it.
  - Fail if `limit` is outside 1-5000.
- Cross-version notes:
  - Use `definition.GetDataType()` and `ForgeTypeId.TypeId` across R22-R27.
  - Use `RevitCompat.GetId(category.Id)` for category DTOs.
- Smoke/test expectations:
  - Snapshot includes wrapper schema and read-only flags.
  - Unit-level static review verifies the handler never starts a transaction.
  - Manual Revit smoke: set a shared parameter file with at least one group and
    verify bound/unbound definitions are distinguished.

### `create_shared_parameter`

- Type: write-capable, not destructive.
- Proposed handler: `src/shared/Handlers/CreateSharedParameterHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "create_shared_parameter", Destructive = false)]
public static async Task<string> CreateSharedParameter(
    string name,
    string dataTypeId,
    string groupName = "Bimwright",
    string guid = "",
    string sharedParameterFilePath = "",
    bool createFileIfMissing = true,
    string description = "",
    bool visible = true,
    bool userModifiable = true,
    bool hideWhenNoValue = false)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["name", "dataTypeId"],
  "properties": {
    "name": { "type": "string" },
    "dataTypeId": {
      "type": "string",
      "description": "Forge data type id or supported alias such as string, integer, number, length, area, volume, angle, yesno."
    },
    "groupName": { "type": "string", "default": "Bimwright" },
    "guid": {
      "type": "string",
      "description": "Optional GUID. If omitted, handler generates a new GUID."
    },
    "sharedParameterFilePath": { "type": "string" },
    "createFileIfMissing": { "type": "boolean", "default": true },
    "description": { "type": "string" },
    "visible": { "type": "boolean", "default": true },
    "userModifiable": { "type": "boolean", "default": true },
    "hideWhenNoValue": { "type": "boolean", "default": false }
  }
}
```

- Response DTO:

```json
{
  "created": true,
  "alreadyExisted": false,
  "sharedParameterFilePath": "C:\\path\\shared-parameters.txt",
  "fileCreated": true,
  "groupName": "Bimwright",
  "name": "Fire Rating",
  "guid": "11111111-2222-3333-4444-555555555555",
  "dataTypeId": "autodesk.spec.aec:string",
  "dataTypeLabel": "Text",
  "visible": true,
  "userModifiable": true,
  "hideWhenNoValue": false
}
```

- Revit API strategy:
  - Resolve or create the shared parameter file through the helper.
  - Open the file through `OpenSharedParameterFile()`.
  - Resolve or create the `DefinitionGroup` by exact `groupName`.
  - If `guid` is supplied, search all groups for the same GUID first. Return
    `alreadyExisted=true` if found and the name/data type match; fail if the
    GUID exists with a different name or data type.
  - In the target group, fail if the same name exists with a different GUID.
  - Create `ExternalDefinitionCreationOptions(name, new ForgeTypeId(resolvedDataTypeId))`.
  - Set `GUID`, `Description`, `Visible`, `UserModifiable`, and
    `HideWhenNoValue`.
  - Call `group.Definitions.Create(options)` and cast to `ExternalDefinition`.
- Transaction behavior:
  - File-only operation. Do not start a Revit `Transaction`.
  - If a later implementation also creates `SharedParameterElement` in the
    project, that project mutation must use `Bimwright: create shared parameter`.
- Validation and failure modes:
  - Fail on blank `name`, blank `dataTypeId`, blank `groupName`, invalid GUID,
    non-absolute path, or missing file with `createFileIfMissing=false`.
  - Fail if the file cannot be reopened by Revit after creation.
  - Fail if the data type alias cannot resolve to a ForgeTypeId.
  - Return clean duplicate diagnostics; do not create a second definition with
    the same display name in the same group.
- Cross-version notes:
  - Use the ForgeTypeId constructor path. Do not use deprecated
    `ParameterType` unless a version-specific compile failure proves it is
    needed.
  - `HideWhenNoValue` exists in the local R22 and R27 API docs; still compile
    all shells because shared-parameter file format is version-sensitive.
- Smoke/test expectations:
  - Static test covers generated GUID and supplied GUID schema.
  - Manual smoke creates a file in `%LOCALAPPDATA%\Bimwright`, adds one text
    parameter, then verifies `list_shared_parameters` returns it with the same
    GUID.

### `bind_shared_parameter`

- Type: write-capable, not destructive.
- Proposed handler: `src/shared/Handlers/BindSharedParameterHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "bind_shared_parameter", Destructive = false)]
public static async Task<string> BindSharedParameter(
    string guid,
    string[] categories,
    string bindingKind = "instance",
    string parameterGroupId = "autodesk.parameter.group:pg_data",
    string sharedParameterFilePath = "",
    bool allowRebind = false)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["guid", "categories"],
  "properties": {
    "guid": { "type": "string" },
    "categories": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Category display names or BuiltInCategory tokens such as OST_Doors."
    },
    "bindingKind": {
      "type": "string",
      "enum": ["instance", "type"],
      "default": "instance"
    },
    "parameterGroupId": {
      "type": "string",
      "default": "autodesk.parameter.group:pg_data"
    },
    "sharedParameterFilePath": { "type": "string" },
    "allowRebind": {
      "type": "boolean",
      "default": false,
      "description": "If true, merges category changes into an existing binding with ReInsert."
    }
  }
}
```

- Response DTO:

```json
{
  "bound": true,
  "createdSharedParameterElement": true,
  "alreadyBound": false,
  "rebuiltBinding": false,
  "name": "Fire Rating",
  "guid": "11111111-2222-3333-4444-555555555555",
  "bindingKind": "instance",
  "parameterGroupId": "autodesk.parameter.group:pg_data",
  "categoryCount": 2,
  "categories": [
    { "id": -2000014, "name": "Doors" },
    { "id": -2000011, "name": "Walls" }
  ],
  "warnings": []
}
```

- Revit API strategy:
  - Resolve the `ExternalDefinition` by GUID from the shared parameter file.
  - Resolve each requested category by exact case-insensitive category name, or
    by `BuiltInCategory` token when the input starts with `OST_`.
  - Create `CategorySet` with `app.Application.Create.NewCategorySet()`.
  - Create `InstanceBinding` or `TypeBinding` using `app.Application.Create`.
  - In a transaction, ensure the document has a `SharedParameterElement` for the
    definition. Use `SharedParameterElement.Lookup(doc, guid)` if available in
    the target API; otherwise collect `SharedParameterElement` and compare
    GUIDs. Create it through `SharedParameterElement.Create(doc, definition)`
    only when absent.
  - Search `doc.ParameterBindings` for the same GUID. If absent, call
    `Insert(definition, binding, groupTypeId)`.
  - If present with the same binding kind and all categories already included,
    return `alreadyBound=true`.
  - If present and categories differ, require `allowRebind=true`; merge category
    sets and call `ReInsert(definition, mergedBinding, groupTypeId)`.
  - If present with the opposite binding kind, fail unless `allowRebind=true`.
    Even then, return a warning because changing instance/type semantics can
    affect existing values.
- Transaction behavior:
  - Single transaction: `Bimwright: bind shared parameter`.
  - Commit status must be checked. Report failure if not committed.
- Validation and failure modes:
  - Fail on invalid GUID, empty categories array, missing category, ambiguous
    category name, unsupported `bindingKind`, missing shared parameter file, or
    GUID not found in file.
  - Fail if `Insert` or `ReInsert` returns false.
  - Do not partially bind a subset of categories after one category fails.
- Cross-version notes:
  - Use `ForgeTypeId` overloads for parameter groups where available in all
    current packages.
  - Category IDs use `RevitCompat.GetId`.
- Smoke/test expectations:
  - Manual smoke binds a text parameter to `Doors` as instance and verifies it
    appears on a door instance.
  - Manual smoke binds another parameter to `Walls` as type and verifies the
    type parameter appears on the wall type.
  - Review must check that rebind never drops pre-existing categories.

### `create_project_parameter`

- Type: write-capable, not destructive if supported.
- Proposed handler: `src/shared/Handlers/CreateProjectParameterHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "create_project_parameter", Destructive = false)]
public static async Task<string> CreateProjectParameter(
    string name,
    string dataTypeId,
    string[] categories,
    string bindingKind = "instance",
    string parameterGroupId = "autodesk.parameter.group:pg_data",
    bool allowIfApiUnsupported = false)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["name", "dataTypeId", "categories"],
  "properties": {
    "name": { "type": "string" },
    "dataTypeId": { "type": "string" },
    "categories": { "type": "array", "items": { "type": "string" } },
    "bindingKind": {
      "type": "string",
      "enum": ["instance", "type"],
      "default": "instance"
    },
    "parameterGroupId": {
      "type": "string",
      "default": "autodesk.parameter.group:pg_data"
    },
    "allowIfApiUnsupported": {
      "type": "boolean",
      "default": false,
      "description": "Must remain false unless the implementation verifies a pure project parameter API path."
    }
  }
}
```

- Response DTO when supported:

```json
{
  "created": true,
  "apiSupported": true,
  "name": "Internal Review Status",
  "parameterElementId": 12345,
  "guid": null,
  "isShared": false,
  "bindingKind": "instance",
  "parameterGroupId": "autodesk.parameter.group:pg_data",
  "categories": [
    { "id": -2000014, "name": "Doors" }
  ]
}
```

- Required behavior while the pure API path remains unverified:
  - Do not create a shared-backed definition and call it a pure project
    parameter.
  - Return `CommandResult.Fail` with:
    `create_project_parameter requires a verified pure project parameter API path for Revit R22-R27; use create_shared_parameter + bind_shared_parameter for shared-backed parameters.`
- Revit API strategy:
  - Before implementation, verify a public API that creates a non-shared
    `ParameterElement` or `InternalDefinition` and can be bound through
    `Document.ParameterBindings`.
  - The local R22 and R27 API XML checked for this spec shows
    `ParameterElement.GetDefinition` but not a public `ParameterElement.Create`
    method for pure project parameters. It does show shared parameter definition
    creation through `ExternalDefinitionCreationOptions`.
  - If a future API path is found, it must create a parameter with no GUID and
    `isShared=false`, then insert the binding with the same category resolution
    and transaction checks as `bind_shared_parameter`.
- Transaction behavior:
  - If supported, use one transaction: `Bimwright: create project parameter`.
  - If unsupported, do not start a transaction.
- Validation and failure modes:
  - Same category, binding kind, data type, and parameter group validation as
    `bind_shared_parameter`.
  - Fail if the only available path would create an `ExternalDefinition` with a
    GUID.
- Cross-version notes:
  - Treat this as a cross-version blocker until R22, R24, R25, R26, and R27 all
    compile with the chosen API path.
- Smoke/test expectations:
  - Until supported, tests should assert the failure message is explicit and
    does not mutate the document.
  - If supported later, manual smoke must verify the new parameter has no GUID in
    `list_project_parameter_bindings` and does not appear in the shared
    parameter file.

### `list_project_parameter_bindings`

- Type: read-only.
- Proposed handler: `src/shared/Handlers/ListProjectParameterBindingsHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "list_project_parameter_bindings", ReadOnly = true, Idempotent = true)]
public static async Task<string> ListProjectParameterBindings(
    bool includeCategories = true,
    bool includeShared = true,
    bool includeProject = true,
    string nameFilter = "",
    string guid = "",
    int limit = 1000)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "includeCategories": { "type": "boolean", "default": true },
    "includeShared": { "type": "boolean", "default": true },
    "includeProject": { "type": "boolean", "default": true },
    "nameFilter": { "type": "string" },
    "guid": { "type": "string" },
    "limit": { "type": "integer", "default": 1000, "minimum": 1, "maximum": 5000 }
  }
}
```

- Response DTO:

```json
{
  "count": 3,
  "returned": 3,
  "bindings": [
    {
      "name": "Fire Rating",
      "parameterElementId": 12345,
      "isShared": true,
      "guid": "11111111-2222-3333-4444-555555555555",
      "bindingKind": "instance",
      "parameterGroupId": "autodesk.parameter.group:pg_data",
      "groupLabel": "Data",
      "dataTypeId": "autodesk.spec.aec:string",
      "dataTypeLabel": "Text",
      "categoryCount": 1,
      "categories": [
        { "id": -2000014, "name": "Doors" }
      ]
    }
  ]
}
```

- Revit API strategy:
  - Iterate `doc.ParameterBindings.ForwardIterator()`.
  - For each `Definition`, identify shared definitions by
    `definition as ExternalDefinition`.
  - Resolve `ParameterElement` by collecting `ParameterElement` and comparing
    `GetDefinition()` identity/name/GUID where available.
  - Use existing `ListProjectParametersHandler` helpers as a starting point but
    add filtering, parameter element ID, `isShared`, and richer DTO names.
- Transaction behavior: none.
- Validation and failure modes:
  - Fail if both `includeShared=false` and `includeProject=false`.
  - Fail on invalid `guid`.
  - Fail if `limit` is outside 1-5000.
- Cross-version notes:
  - Use `GetDataType()` and `GetGroupTypeId()` in guarded try/catch, as the
    existing handler does.
- Smoke/test expectations:
  - Existing `list_project_parameters` output remains unchanged.
  - Manual smoke after `bind_shared_parameter` verifies this tool reports the
    same GUID, binding kind, and categories.

### `remove_parameter_binding`

- Type: destructive write.
- Proposed handler: `src/shared/Handlers/RemoveParameterBindingHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "remove_parameter_binding", Destructive = true)]
public static async Task<string> RemoveParameterBinding(
    string name = "",
    string guid = "",
    string[] categories = null,
    bool removeAllCategories = false,
    bool dryRun = true)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string" },
    "guid": { "type": "string" },
    "categories": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Optional subset of bound categories to remove."
    },
    "removeAllCategories": { "type": "boolean", "default": false },
    "dryRun": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "dryRun": true,
  "wouldRemoveBinding": false,
  "removedBinding": false,
  "rebuiltBinding": false,
  "name": "Fire Rating",
  "guid": "11111111-2222-3333-4444-555555555555",
  "bindingKind": "instance",
  "removedCategories": [
    { "id": -2000014, "name": "Doors" }
  ],
  "remainingCategories": [
    { "id": -2000011, "name": "Walls" }
  ],
  "warnings": []
}
```

- Revit API strategy:
  - Resolve the target binding by GUID when supplied; otherwise by exact
    case-insensitive name.
  - If `removeAllCategories=true`, call `doc.ParameterBindings.Remove(definition)`.
  - If `categories` is supplied, remove only those categories from the binding's
    `ElementBinding.Categories`, create a replacement `CategorySet`, create the
    same binding kind, and call `ReInsert`.
  - If category removal would leave zero categories, fail unless
    `removeAllCategories=true`.
- Transaction behavior:
  - `dryRun=true`: no transaction.
  - `dryRun=false`: single transaction `Bimwright: remove parameter binding`.
  - Commit status must be checked.
- Validation and failure modes:
  - Require exactly one identity path: `guid` or `name`.
  - Fail on invalid GUID, ambiguous name, missing binding, categories not
    currently bound, and empty `categories` when `removeAllCategories=false`.
  - Do not delete a `SharedParameterElement` or edit the shared-parameter file.
    This tool removes document bindings only.
- Cross-version notes:
  - Use `BindingMap.Remove` and `BindingMap.ReInsert`; both exist in the local
    R22 and R27 API XML.
- Smoke/test expectations:
  - Manual smoke with `dryRun=true` shows the same category counts before and
    after.
  - Manual smoke with subset category removal verifies other categories remain
    bound.
  - Manual smoke with full removal verifies the parameter disappears from bound
    elements but the shared file definition remains listed.

### `export_shared_parameter_file`

- Type: read-only.
- Proposed handler: `src/shared/Handlers/ExportSharedParameterFileHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "export_shared_parameter_file", ReadOnly = true, Idempotent = true)]
public static async Task<string> ExportSharedParameterFile(
    string sharedParameterFilePath = "",
    bool includeRawLines = false,
    bool includeBindings = true)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "properties": {
    "sharedParameterFilePath": { "type": "string" },
    "includeRawLines": { "type": "boolean", "default": false },
    "includeBindings": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "sharedParameterFilePath": "C:\\path\\shared-parameters.txt",
  "exists": true,
  "lineCount": 25,
  "groupCount": 1,
  "definitionCount": 3,
  "groups": [
    {
      "name": "Bimwright",
      "definitions": [
        {
          "name": "Fire Rating",
          "guid": "11111111-2222-3333-4444-555555555555",
          "dataTypeId": "autodesk.spec.aec:string",
          "visible": true,
          "userModifiable": true,
          "hideWhenNoValue": false,
          "isBound": true
        }
      ]
    }
  ],
  "rawLines": null
}
```

- Revit API strategy:
  - Use the same shared-parameter file resolution helper.
  - Read raw lines with `File.ReadAllLines` only after validating absolute path.
  - Open through `OpenSharedParameterFile()` for canonical group/definition DTOs.
  - Use the binding map helper from `list_shared_parameters` when
    `includeBindings=true`.
- Transaction behavior: none.
- Validation and failure modes:
  - Fail if path is not absolute.
  - Return `exists=false` when the resolved file is missing.
  - Do not create or modify the shared-parameter file.
- Cross-version notes:
  - Shared parameter file parsing is delegated to Revit API where possible; raw
    line parsing is only for echo/debug output.
- Smoke/test expectations:
  - Manual smoke compares `definitionCount` with `list_shared_parameters`.
  - If `includeRawLines=true`, verify the response is still DTO-safe and does
    not exceed size guard limits on normal files.

### `set_parameter_value_by_guid`

- Type: write-capable, not destructive.
- Proposed handler: `src/shared/Handlers/SetParameterValueByGuidHandler.cs`.
- MCP wrapper:

```csharp
[McpServerTool(Name = "set_parameter_value_by_guid", Destructive = false)]
public static async Task<string> SetParameterValueByGuid(
    long[] elementIds,
    string guid,
    string value,
    string valueType = "auto",
    string unit = "auto",
    string target = "auto",
    bool allOrNothing = true)
```

- Handler `ParametersSchema`:

```json
{
  "type": "object",
  "required": ["elementIds", "guid", "value"],
  "properties": {
    "elementIds": { "type": "array", "items": { "type": "integer" } },
    "guid": { "type": "string" },
    "value": {
      "type": "string",
      "description": "String transport value parsed according to parameter storage type."
    },
    "valueType": {
      "type": "string",
      "enum": ["auto", "string", "integer", "double", "elementId", "display"],
      "default": "auto"
    },
    "unit": {
      "type": "string",
      "enum": ["auto", "internal", "mm", "m2", "m3", "deg"],
      "default": "auto"
    },
    "target": {
      "type": "string",
      "enum": ["auto", "instance", "type"],
      "default": "auto"
    },
    "allOrNothing": { "type": "boolean", "default": true }
  }
}
```

- Response DTO:

```json
{
  "requested": 2,
  "updatedCount": 2,
  "failedCount": 0,
  "guid": "11111111-2222-3333-4444-555555555555",
  "value": "90",
  "valueType": "auto",
  "unit": "auto",
  "target": "instance",
  "allOrNothing": true,
  "updated": [
    {
      "elementId": 101,
      "name": "Door 101",
      "category": "Doors",
      "parameterName": "Fire Rating",
      "storageType": "String",
      "oldValue": "60",
      "oldDisplayValue": "60",
      "newValue": "90",
      "newDisplayValue": "90",
      "valueTypeUsed": "string",
      "unitUsed": null
    }
  ],
  "failed": []
}
```

- Revit API strategy:
  - Parse and validate GUID.
  - Validate every element ID with `RevitCompat.CanRepresentElementId`.
  - Resolve each element and then resolve the shared parameter by GUID:
    - `target=instance`: `element.get_Parameter(guid)`.
    - `target=type`: resolve `doc.GetElement(element.GetTypeId())` and call
      `get_Parameter(guid)` on the type element.
    - `target=auto`: prefer instance parameter; fall back to type parameter and
      report which target was used.
  - For string/integer/double/elementId storage, mirror
    `SetElementParameterValuesHandler` validation but use GUID lookup.
  - For `valueType=display`, call `Parameter.SetValueString(value)` and report
    `valueTypeUsed="display"`.
  - For double storage and `valueType=auto` or `double`, convert by data type:
    - length: input `mm`, internal feet.
    - area: input `m2`, internal square feet.
    - volume: input `m3`, internal cubic feet.
    - angle: input `deg`, internal radians.
    - unknown spec: require `unit=internal` or return failure with `unit_note`.
  - For `unit` supplied explicitly, it overrides spec inference only when
    compatible with the parameter storage.
- Transaction behavior:
  - `allOrNothing=true`: pre-validate all target parameters, then mutate inside
    one transaction `Bimwright: set parameter by GUID`. Roll back on the first
    mutation error.
  - `allOrNothing=false`: use a transaction with per-element `SubTransaction`
    so one failed element does not claim to be committed.
  - Always check transaction commit status.
- Validation and failure modes:
  - Fail on invalid GUID, empty element array, missing element, missing
    parameter, read-only parameter, incompatible storage type, parse failure,
    unsupported unit, or pre-2024 element ID range overflow.
  - When `target=auto` finds both instance and type parameters, prefer instance
    and add a warning.
  - For type parameters, avoid double-setting the same type when multiple
    instances of that type are passed. Report `skippedDuplicateTypeIds`.
- Cross-version notes:
  - Use `RevitCompat.ToElementId` and `GetId`.
  - Do not use `.IntegerValue` or `.Value` directly.
  - `Element.get_Parameter(Guid)` is the intended shared-parameter lookup path.
- Smoke/test expectations:
  - Manual smoke sets a text shared parameter by GUID on two door instances.
  - Manual smoke sets a length shared parameter with `unit=mm` and verifies
    Revit display value changes correctly.
  - Manual smoke sets a type-bound shared parameter once when two instances of
    the same type are supplied.

## Wiring

Implementation must update the following files when the wave is built:

- `src/shared/Infrastructure/CommandDispatcher.cs`
  - Add the eight handler registrations in a `Wave 11: Parameters` block.
- `src/server/Program.cs`
  - Add `ParametersTools` with one `[McpServerTool]` method per tool.
  - Wrapper methods call `ToolGateway.SendToRevit("<tool_name>", new { ... })`
    and return `JsonConvert.SerializeObject(result, Formatting.Indented)`.
- `src/server/ToolsetFilter.cs`
  - Add `parameters` to `KnownToolsets`, `DefaultOn`, and `WriteCapable`.
- `tests/RvtMcp.Tests/ToolsetFilterTests.cs`
  - Update hardcoded known toolset count and write-capable expectations.
- Golden snapshots:
  - Refresh `tests/RvtMcp.Tests/Golden/tools-list.json`.
  - Refresh `tests/RvtMcp.Tests/Golden/tools-list-adaptive-bake.json`.
- Public docs after implementation:
  - Update README/tool tables and `CHANGELOG.md` in the implementation PR.

## Acceptance Criteria

- All eight MCP wrappers appear under `Toolset("parameters")`.
- `--toolsets parameters` exposes only this toolset plus required meta behavior.
- Default configuration exposes `parameters`; `--read-only` removes it because
  the toolset is write-capable.
- `list_shared_parameters` and `export_shared_parameter_file` do not create or
  modify files.
- `create_shared_parameter` can create a missing shared-parameter file and
  returns a stable GUID.
- `bind_shared_parameter` preserves existing categories when rebinding and does
  not change instance/type binding without explicit permission.
- `list_project_parameter_bindings` reports shared GUIDs and project/non-shared
  bindings distinctly.
- `remove_parameter_binding` defaults to `dryRun=true`.
- `set_parameter_value_by_guid` handles string, integer, double, and ElementId
  storage, including mm/m2/m3/deg conversion for double parameters.
- `create_project_parameter` must not misrepresent a shared-backed parameter as
  pure project-only. If no verified API path exists, the handler fails clearly.

## Review Checklist

- Parameter file path handling restores `Application.SharedParametersFilename`
  in `finally`.
- Shared parameter file creation writes a valid header and verifies through
  `OpenSharedParameterFile()`.
- GUID matching, not display name, is the identity for shared parameters.
- Existing `list_project_parameters` behavior and wrapper remain unchanged.
- Category resolution fails ambiguous matches and supports both display names
  and `OST_*` tokens where documented.
- Rebind logic merges categories instead of dropping them.
- Write handlers check `Transaction.Commit()` result before returning success.
- Unit conversion follows the roadmap contract: mm, m2, m3, deg externally.
- No handler serializes Revit API objects.
- No handler uses `.IntegerValue` or `.Value` directly for element IDs.
- `create_project_parameter` has an explicit support check and does not fake
  pure project parameters through shared parameter backing.

## Known Risks

- Pure non-shared project parameter creation is the unresolved API risk. The
  local R22 and R27 package XML exposes `ParameterElement.GetDefinition` and
  shared parameter creation APIs, but not a direct public
  `ParameterElement.Create` path for pure project parameters.
- Shared parameter file format is sensitive. The creation helper must be tested
  in a live Revit session, not only by file existence.
- Rebinding parameter categories can affect existing data visibility. This is
  why `allowRebind` must be explicit.
- Type-bound parameter updates by GUID can accidentally touch one type multiple
  times when many instances share a type. The handler must de-duplicate type
  IDs.
- `Parameter.SetValueString` is locale-sensitive. Prefer typed conversion for
  numeric values and reserve `valueType=display` for caller-intended display
  strings.

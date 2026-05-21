# Wave 02 - MEP Systems As-Built Spec

## Status

Done in the current codebase. The `mep` toolset is implemented as a default-on, write-capable MCP toolset.

Source of truth for this spec:

- MCP wrappers: `src/server/Program.cs`, `MepTools`
- Dispatcher registration: `src/shared/Infrastructure/CommandDispatcher.cs`, Phase 14 plus the earlier `detect_system_elements` registration
- Handlers: relevant MEP handlers under `src/shared/Handlers`
- Review lessons: `runs/mep-handlers-review/output-01.txt` and `output-02.txt`
- Format contract: `docs/221-tools/README.md`

This file is an as-built spec for Waves 1-4 style documentation, not a target implementation plan.

## Scope

Wave 02 adds fifteen MEP tools and keeps the pre-existing `detect_system_elements` tool in the same `mep` toolset. As-built public tool count for `mep` is therefore sixteen.

| Tool | Type | Handler |
|---|---|---|
| `detect_system_elements` | R | `DetectSystemElementsHandler.cs` |
| `create_duct` | W | `CreateDuctHandler.cs` |
| `create_pipe` | W | `CreatePipeHandler.cs` |
| `create_cable_tray` | W | `CreateCableTrayHandler.cs` |
| `create_conduit` | W | `CreateConduitHandler.cs` |
| `create_air_terminal` | W | `CreateAirTerminalHandler.cs` |
| `create_lighting_fixture` | W | `CreateLightingFixtureHandler.cs` |
| `list_mep_systems` | R | `ListMepSystemsHandler.cs` |
| `get_system_inventory` | R | `GetSystemInventoryHandler.cs` |
| `get_mep_element_connectors` | R | `GetMepElementConnectorsHandler.cs` |
| `connect_mep_elements` | W | `ConnectMepElementsHandler.cs` |
| `create_mep_fitting` | W | `CreateMepFittingHandler.cs` |
| `set_system_classification` | W/R | `SetSystemClassificationHandler.cs` |
| `get_panel_schedule` | R | `GetPanelScheduleHandler.cs` |
| `find_mep_disconnects` | R | `FindMepDisconnectsHandler.cs` |
| `analyze_mep_network` | R | `AnalyzeMepNetworkHandler.cs` |

## Dependencies

- Revit MEP API classes: `Duct`, `Pipe`, `CableTray`, `Conduit`, `MechanicalSystem`, `PipingSystem`, `ElectricalSystem`, `MEPSystem`, `MEPCurve`, `Connector`, `ConnectorManager`, `FamilyInstance`.
- Revit creation factories: `Duct.Create`, `Pipe.Create`, `CableTray.Create`, `Conduit.Create`, `Document.Create.NewFamilyInstance`, `NewElbowFitting`, `NewTeeFitting`, `NewUnionFitting`, `NewCrossFitting`, `NewTransitionFitting`.
- Shared command infrastructure: `IRevitCommand`, `CommandResult`, `CommandDispatcher`, `ToolGateway.SendToRevit`.
- Cross-version ID helpers: `RevitCompat.ToElementId(long)`, `RevitCompat.GetId(...)`, `RevitCompat.GetIdOrNull(...)`, `RevitCompat.CanRepresentElementId(long)`.
- Units: external coordinates and sizes are mm; handler internals convert mm to feet and feet to mm.
- JSON: `Newtonsoft.Json.Linq`.

## Toolset Contract

- Toolset name: `mep`.
- MCP wrapper class: `MepTools` in `src/server/Program.cs`.
- Enabled by default: yes, through `ToolsetFilter.DefaultOn`.
- Write-capable: yes, through `ToolsetFilter.WriteCapable`.
- Dispatcher registrations: `detect_system_elements` in the older MEP analysis block, and the fifteen Wave 02 tools in Phase 14.
- Canonical connector identity: write tools accept `connector_index` / `connector_index_1` / `connector_index_2` as Revit `Connector.Id` values. These should be taken from `get_mep_element_connectors.connectors[].connector_id`, not from an ordinal.
- Public `domain_filter` values for newer tools are `all`, `mechanical`, `piping`, and `electrical`.

## Tool Specs

### `detect_system_elements`

- Purpose: walk connected MEP elements from a seed element and group results by coarse category.
- Type: R.
- Handler file: `src/shared/Handlers/DetectSystemElementsHandler.cs`.
- Expected parameters: `elementId` required integer. This older tool uses camelCase handler input.
- Response DTO summary: `systemName`, `elementCount`, `boundingBox` in mm with min/max coordinates, and `byCategory` with `pipes`, `fittings`, `accessories`, `equipment`.
- Revit API strategy: resolve seed element, get `ConnectorManager`, breadth-first traverse `Connector.AllRefs`, skip non-real owner IDs, classify visited elements by built-in category, aggregate bounding boxes.
- Transaction behavior: none.
- Validation/failure modes: no document, missing seed, seed element not found, seed has no MEP connectors. Malformed JSON and out-of-range IDs are not guarded as consistently as newer handlers.
- Cross-version notes: uses `RevitCompat.ToElementId` and `GetId`; output coordinates convert feet to mm.
- Smoke/test expectations: seed with a known pipe or duct segment, verify traversal count, category buckets, and bounding box.

### `create_duct`

- Purpose: create an HVAC duct segment between two points.
- Type: W.
- Handler file: `src/shared/Handlers/CreateDuctHandler.cs`.
- Expected parameters: required `start_x`, `start_y`, `start_z`, `end_x`, `end_y`, `end_z` in mm; optional `duct_type_id`, `system_type_id`, `level_id`, `width`, `height`, `diameter`.
- Response DTO summary: `created`, `duct_id`, `duct_type`, `system_type`, `level`, `length_mm`, `error`.
- Revit API strategy: resolve or default first `DuctType`, first `MechanicalSystemType`, and nearest `Level`; call `Duct.Create`; set diameter or width/height parameters when supplied.
- Transaction behavior: one transaction named `Bimwright: create duct`; rolls back on create or parameter failure.
- Validation/failure modes: no document, invalid JSON, zero-length segment, invalid IDs, missing type/system type/level, `Duct.Create` null, Revit exception. Required coordinate keys are schema-required, but the handler reads them with `Value<double>` and does not explicitly reject every missing key.
- Cross-version notes: uses stable `Duct.Create` and `RevitCompat`; sizes and coordinates are mm externally, feet internally.
- Smoke/test expectations: create round and rectangular ducts in a disposable model, verify returned length and that the target type/system/level names match the request or fallback.

### `create_pipe`

- Purpose: create a plumbing pipe segment between two points.
- Type: W.
- Handler file: `src/shared/Handlers/CreatePipeHandler.cs`.
- Expected parameters: required start/end coordinates in mm; optional `pipe_type_id`, `system_type_id`, `level_id`, `diameter`.
- Response DTO summary: `created`, `pipe_id`, `pipe_type`, `system_type`, `level`, `length_mm`, `diameter_mm`, `error`.
- Revit API strategy: resolve or default `PipeType`, `PipingSystemType`, and nearest `Level`; call `Pipe.Create`; set `RBS_PIPE_DIAMETER_PARAM` if positive diameter is supplied; read back length and diameter.
- Transaction behavior: one transaction named `Bimwright: create pipe`; rolls back on failure.
- Validation/failure modes: no document, invalid JSON, missing/non-number coordinates, zero length, invalid IDs, missing pipe type/system type/level, `Pipe.Create` null, Revit exception.
- Cross-version notes: uses canonical `Pipe.Create(Document, systemTypeId, pipeTypeId, levelId, start, end)`, stable across R22-R27; IDs use `RevitCompat`.
- Smoke/test expectations: create with explicit and default types, create with diameter, verify returned diameter and length are plausible.

### `create_cable_tray`

- Purpose: create an electrical cable tray segment between two points.
- Type: W.
- Handler file: `src/shared/Handlers/CreateCableTrayHandler.cs`.
- Expected parameters: required start/end coordinates in mm; optional `cable_tray_type_id`, `level_id`, `width`, `height`.
- Response DTO summary: `created`, `cable_tray_id`, `type`, `level`, `length_mm`, `width_mm`, `height_mm`, `error`.
- Revit API strategy: resolve or default first `CableTrayType`, nearest `Level`, call `CableTray.Create`, set width/height built-in parameters when supplied, read back dimensions.
- Transaction behavior: one transaction named `Bimwright: create cable tray`; checks commit status.
- Validation/failure modes: no document, malformed JSON, missing/non-number coordinates, zero length, non-integer/bad IDs, no type/level, non-positive width/height, create failure, non-committed transaction.
- Cross-version notes: `CableTray.Create` and ElementId conversion are wrapped for R22-R27 compatibility.
- Smoke/test expectations: create with default type, create with explicit width/height, verify read-back dimensions.

### `create_conduit`

- Purpose: create an electrical conduit segment between two points.
- Type: W.
- Handler file: `src/shared/Handlers/CreateConduitHandler.cs`.
- Expected parameters: required start/end coordinates in mm; optional `conduit_type_id`, `level_id`, `diameter`.
- Response DTO summary: `created`, `conduit_id`, `type`, `level`, `length_mm`, `diameter_mm`, `error`.
- Revit API strategy: resolve or default first `ConduitType`, nearest `Level`, call `Conduit.Create`, set `RBS_CONDUIT_DIAMETER_PARAM` if supplied, read length and diameter.
- Transaction behavior: one transaction named `Bimwright: create conduit`; rolls back on failure.
- Validation/failure modes: no document, invalid JSON, missing coordinates, zero length, invalid IDs, missing type/level, create failure. Diameter is not explicitly checked for positive value before setting, so Revit may reject invalid sizes.
- Cross-version notes: uses `Conduit.Create` and `RevitCompat`; mm to feet conversions are local constants.
- Smoke/test expectations: create default and explicit-type conduit; verify returned length and diameter.

### `create_air_terminal`

- Purpose: place an air terminal family instance.
- Type: W.
- Handler file: `src/shared/Handlers/CreateAirTerminalHandler.cs`.
- Expected parameters: `type_id` required integer; `x`, `y`, `z` required coordinates in mm; optional `level_id`; optional `host_id`.
- Response DTO summary: `created`, `instance_id`, `type_name`, `family_name`, `level`, `hosted`, `warning`, `error`.
- Revit API strategy: resolve `FamilySymbol`, require category `OST_DuctTerminal`, inspect `FamilyPlacementType`, resolve nearest/explicit level and optional host, activate symbol, call `NewFamilyInstance` with hosted or level-based overload.
- Transaction behavior: one transaction named `Bimwright: create air terminal`; rolls back on failure.
- Validation/failure modes: no document, invalid JSON, missing/bad type, wrong category returns `created=false`, hosted/work-plane families without `host_id` return a soft error, non-hosted one-level families ignore host with warning, host/level not found, `NewFamilyInstance` null, Revit exception.
- Cross-version notes: `FamilyPlacementType` preflight came from review; IDs use `RevitCompat`; coordinates convert mm to feet.
- Smoke/test expectations: place a non-hosted terminal, attempt a hosted terminal without host and expect clean error, place with a valid host.

### `create_lighting_fixture`

- Purpose: place a lighting fixture family instance.
- Type: W.
- Handler file: `src/shared/Handlers/CreateLightingFixtureHandler.cs`.
- Expected parameters: `type_id` required integer; `x`, `y`, `z` required coordinates in mm; optional `level_id`; optional `host_id`.
- Response DTO summary: `created`, `instance_id`, `type_name`, `family_name`, `level`, `hosted`, `warning`, `error`.
- Revit API strategy: resolve `FamilySymbol`, require category `OST_LightingFixtures`, apply the same placement-type preflight as air terminals, resolve level/host, activate symbol, call `NewFamilyInstance`.
- Transaction behavior: one transaction named `Bimwright: create lighting fixture`; rolls back on failure.
- Validation/failure modes: no document, invalid JSON, missing type, wrong category, hosted/work-plane family without host returns soft error, ignored host warning for non-hosted families, host/level not found, null instance, Revit exception.
- Cross-version notes: uses Revit family placement metadata and `RevitCompat`; coordinates are mm externally.
- Smoke/test expectations: place a level-based fixture, validate ceiling-hosted family behavior with and without `host_id`.

### `list_mep_systems`

- Purpose: list mechanical, piping, and electrical systems in the model.
- Type: R.
- Handler file: `src/shared/Handlers/ListMepSystemsHandler.cs`.
- Expected parameters: `domain_filter` optional `all|mechanical|piping|electrical`, default `all`; `limit` optional int clamped to `1..10000`, default `1000`.
- Response DTO summary: `doc_title`, `total_systems`, `returned`, and `systems[]` with `id`, `name`, `domain`, `system_type`, `element_count`, `is_well_connected`.
- Revit API strategy: collect `MechanicalSystem`, `PipingSystem`, and `ElectricalSystem`; resolve system type name from `GetTypeId`.
- Transaction behavior: none.
- Validation/failure modes: no document, malformed JSON, invalid domain filter. Per-system property reads are guarded.
- Cross-version notes: `IsWellConnected` is read only for mechanical/piping systems; electrical defaults to true because Revit does not expose an equivalent consistently.
- Smoke/test expectations: run each domain filter and verify returned counts against a model with HVAC, piping, and electrical systems.

### `get_system_inventory`

- Purpose: return member elements and category counts for one MEP system.
- Type: R.
- Handler file: `src/shared/Handlers/GetSystemInventoryHandler.cs`.
- Expected parameters: `system_id` or `system_name` required; `include_parameters` optional bool, default `false`; `limit` optional int clamped to `1..20000`, default `2000`.
- Response DTO summary: `system_id`, `system_name`, `domain`, `system_type`, `total_elements`, `returned`, `limit_hit`, `category_breakdown`, `elements[]` with `id`, `category`, `type_name`, `name`, optional `parameters`.
- Revit API strategy: resolve `MEPSystem` by ID or unique case-insensitive name across mechanical/piping/electrical collectors; iterate `system.Elements`; optionally read up to 20 parameters per element as strings.
- Transaction behavior: none.
- Validation/failure modes: no document, invalid JSON, missing identity, bad ID, non-system ID, no name match, ambiguous name, failure to read system elements.
- Cross-version notes: works across `MechanicalSystem`, `PipingSystem`, and `ElectricalSystem`; ID conversion uses `RevitCompat`.
- Smoke/test expectations: query by ID and by name, test ambiguous duplicate-name handling if available, verify parameter inclusion on a small system.

### `get_mep_element_connectors`

- Purpose: inspect connector metadata for one MEP element.
- Type: R.
- Handler file: `src/shared/Handlers/GetMepElementConnectorsHandler.cs`.
- Expected parameters: `element_id` required integer.
- Response DTO summary: `element_id`, `element_category`, `connector_count`, `connectors[]` with `connector_id`, `ordinal`, `domain`, `shape`, `origin_mm`, `is_connected`, `connected_to_element_ids`, `radius_mm`, `width_mm`, `height_mm`, `flow`, `direction`.
- Revit API strategy: get `ConnectorManager` from `MEPCurve.ConnectorManager` or `FamilyInstance.MEPModel.ConnectorManager`; enumerate connectors; read safe shape, origin, refs, size, flow, and direction.
- Transaction behavior: none.
- Validation/failure modes: no document, malformed JSON, missing/bad/out-of-range element ID, element not found. Elements without connector managers return zero connectors, not a hard failure.
- Cross-version notes: review established `connector_id` as the canonical downstream identifier and retained `ordinal` as informational only.
- Smoke/test expectations: inspect a pipe, duct, fitting, and equipment instance; verify `connector_id` values can be used by connection/fitting write tools.

### `connect_mep_elements`

- Purpose: connect two MEP elements by compatible connectors.
- Type: W.
- Handler file: `src/shared/Handlers/ConnectMepElementsHandler.cs`.
- Expected parameters: `element_id_1` and `element_id_2` required; optional `connector_index_1` and `connector_index_2`, both representing `connector_id` values from `get_mep_element_connectors`.
- Response DTO summary: `connected`, `element_id_1`, `element_id_2`, `connector_1_origin_mm`, `connector_2_origin_mm`, `gap_mm`, `error`.
- Revit API strategy: resolve both elements and connector managers; if explicit connector IDs are supplied, match only `Connector.Id`; otherwise select nearest open physical connectors with matching domains; call `Connector.ConnectTo`.
- Transaction behavior: one transaction named `Bimwright: connect MEP elements`; returns `connected=false` DTO if `ConnectTo` fails.
- Validation/failure modes: no document, bad JSON, missing IDs, same element, missing elements, missing connectors, only one explicit connector supplied, connector ID not found, no compatible open connector pair, Revit connect failure.
- Cross-version notes: connector identity contract is `Connector.Id`, not enumeration order; origin/gap output converts feet to mm.
- Smoke/test expectations: connect two aligned pipe or duct segments by automatic nearest connectors, then repeat with explicit connector IDs.

### `create_mep_fitting`

- Purpose: create an elbow, tee, union, cross, or transition fitting from supplied connectors.
- Type: W.
- Handler file: `src/shared/Handlers/CreateMepFittingHandler.cs`.
- Expected parameters: `fitting_kind` required `elbow|tee|union|cross|transition`; `connectors` required array of `{ element_id, connector_index }`, where `connector_index` is the `connector_id` from `get_mep_element_connectors`.
- Response DTO summary: `created`, `fitting_id`, `fitting_kind`, `fitting_category`, `error`.
- Revit API strategy: validate connector count for kind, resolve each element's `ConnectorManager`, match connectors by `Connector.Id`, then call the matching `Document.Create.New*Fitting` factory.
- Transaction behavior: one transaction named `Bimwright: create MEP fitting`; rolls back on failure.
- Validation/failure modes: no document, bad JSON, unknown kind, wrong connector count, missing element ID or connector ID, element not found, no connectors, connector ID not found, null fitting, Revit factory failure.
- Cross-version notes: review removed the old ordinal fallback; this is now Connector.Id-only for deterministic behavior.
- Smoke/test expectations: create an elbow between two pipe connectors, attempt wrong connector count and bad connector ID, and verify clean `created=false` DTOs.

### `set_system_classification`

- Purpose: add MEP elements to an existing mechanical or piping system, or report current memberships when no target system is supplied.
- Type: W/R. It is a write-capable toolset member, but omitting `system_id` runs a read-only report path.
- Handler file: `src/shared/Handlers/SetSystemClassificationHandler.cs`.
- Expected parameters: `element_ids` required integer array; `system_id` optional integer target `MechanicalSystem` or `PipingSystem`.
- Response DTO summary: `system_id`, `system_name`, `added`, `already_member`, `failed[]`, `dry_report`, `error`.
- Revit API strategy: read branch inspects each element's connectors and current `connector.MEPSystem`; write branch resolves target system, computes target domain, pre-checks a free End connector in that domain, then calls `MechanicalSystem.Add(connMgr.Connectors)` or `PipingSystem.Add(connMgr.Connectors)`.
- Transaction behavior: none when `system_id` is omitted; one transaction named `Bimwright: set system classification` for write branch.
- Validation/failure modes: no document, bad JSON, missing/non-array/empty element IDs, out-of-range IDs, non-integer system ID, missing/non-mechanical/non-piping system, element not found, no connectors, already member, no free End connector in target domain, Revit Add rejection, non-committed transaction.
- Cross-version notes: domain preflight was added from review; electrical systems are not target systems for write branch.
- Smoke/test expectations: run read report on known elements, add a loose pipe/duct element to a compatible system, verify domain-mismatch elements land in `failed[]`.

### `get_panel_schedule`

- Purpose: read circuits assigned to an electrical panel.
- Type: R.
- Handler file: `src/shared/Handlers/GetPanelScheduleHandler.cs`.
- Expected parameters: `panel_id` or `panel_name` required.
- Response DTO summary: `panel_id`, `panel_name`, `panel_type`, `total_circuits`, and `circuits[]` with `id`, `circuit_number`, `name`, `rating_amps`, `load_va`, `voltage`, `poles`, `element_count`.
- Revit API strategy: resolve an `OST_ElectricalEquipment` `FamilyInstance`; `panel_name` uses `RBS_ELEC_PANEL_NAME` and falls back to `Element.Name`; collect `ElectricalSystem` elements where `BaseEquipment.Id` equals the panel ID.
- Transaction behavior: none.
- Validation/failure modes: no document, bad JSON, missing identity, invalid/out-of-range panel ID, non-panel element, no matching panel name, ambiguous panel name.
- Cross-version notes: review fixed lookup/return naming to use the electrical Panel Name parameter instead of `FamilyInstance.Name`.
- Smoke/test expectations: query by `panel_id`, query by panel-name parameter, verify circuit count against Revit panel schedule.

### `find_mep_disconnects`

- Purpose: find MEP elements with unconnected physical End connectors.
- Type: R.
- Handler file: `src/shared/Handlers/FindMepDisconnectsHandler.cs`.
- Expected parameters: `domain_filter` optional `all|mechanical|piping|electrical`, default `all`; `view_only` optional bool, default `false`; `limit` optional int clamped to `1..20000`, default `2000`.
- Response DTO summary: `total_open_connectors`, `elements_with_disconnects`, `returned`, `limit_hit`, and `disconnects[]` with `element_id`, `category`, `type_name`, `open_connector_count`, `open_connectors[]` containing `index`, `domain`, `origin_mm`.
- Revit API strategy: collect `MEPCurve` and `FamilyInstance` elements with `MEPModel`, optionally from active view; inspect only unconnected `ConnectorType.End` connectors; map `mechanical` to `DomainHvac`.
- Transaction behavior: none.
- Validation/failure modes: no document, bad JSON, invalid domain filter, candidate collection failure. Individual bad elements/connectors are skipped.
- Cross-version notes: review changed the public filter name from `hvac` to `mechanical`; this tool's `open_connectors[].index` is an ordinal for reporting, not the canonical connector ID contract used by write tools.
- Smoke/test expectations: run model-wide and active-view scans, filter by each domain, and verify known capped or connected runs do not appear.

### `analyze_mep_network`

- Purpose: summarize topology and health of one MEP system.
- Type: R.
- Handler file: `src/shared/Handlers/AnalyzeMepNetworkHandler.cs`.
- Expected parameters: `system_id` or `system_name` required.
- Response DTO summary: `system_id`, `system_name`, `domain`, `system_type`, `is_well_connected`, `element_count`, `category_breakdown`, `base_equipment`, `open_connector_count`, `issues`, `recommendations`.
- Revit API strategy: resolve system by ID or unique name across all system domains, read system type and `IsWellConnected`, iterate members for category counts and open End connector count, read `BaseEquipment`, derive issues/recommendations.
- Transaction behavior: none.
- Validation/failure modes: no document, bad JSON, missing identity, out-of-range ID, non-system ID, no name match, duplicate name match. Review fixed duplicate `system_name` handling to fail and ask for `system_id`.
- Cross-version notes: `IsWellConnected` is only direct for mechanical/piping and defaults true otherwise; IDs use `RevitCompat`.
- Smoke/test expectations: analyze a healthy system, a system with open connectors, and duplicate-name behavior if the model can create it.

## Wiring

- `Program.cs` defines `[McpServerToolType, Toolset("mep")] public class MepTools`.
- `MepTools` exposes `detect_system_elements` plus the fifteen Wave 02 tools.
- `CommandDispatcher` registers `DetectSystemElementsHandler` in the older MEP analysis block and registers the Wave 02 handlers in Phase 14.
- `ToolsetFilter.KnownToolsets`, `DefaultOn`, and `WriteCapable` include `mep`.
- The public wrapper keeps legacy field names such as `connectorIndex1`/`connectorIndex2`, but sends snake_case JSON with `connector_index_1`/`connector_index_2`.

## Acceptance Criteria

- All sixteen as-built `mep` tools are visible when default toolsets are enabled.
- Read-only analysis/listing tools open no Revit transactions.
- Creation, connection, fitting, and system-classification write paths use bounded Revit transactions and roll back or return structured failure DTOs on expected failures.
- Connector write paths use `connector_id` values returned by `get_mep_element_connectors`, never ordinals.
- Public MEP coordinates, lengths, widths, heights, diameters, connector origins, and gaps are reported in mm.
- System name resolution fails on ambiguous names where the handler supports name lookup.

## Review Checklist

- Confirm every `MepTools` wrapper command name still matches a dispatcher registration.
- Confirm `get_mep_element_connectors` still returns `connector_id` and `ordinal` separately.
- Confirm `connect_mep_elements` and `create_mep_fitting` remain Connector.Id-only.
- Confirm `set_system_classification` still preflights target-domain End connectors before calling Revit `Add`.
- Confirm panel lookup continues to use `RBS_ELEC_PANEL_NAME`.
- Confirm public domain filters remain aligned as `mechanical|piping|electrical`, not mixed with legacy `hvac`.
- For smoke runs, use disposable MEP elements because write tools can permanently add/connect/delete topology.

## Known Risks

- `detect_system_elements` is older than the Wave 02 handlers and has weaker JSON/range guarding.
- Some create handlers still parse JSON with `JObject.Parse(paramsJson)` rather than a blank-safe parse; callers should send a JSON object every time.
- `create_duct` relies on schema-required coordinates but does not explicitly reject every missing coordinate key in the handler.
- `find_mep_disconnects.open_connectors[].index` is an ordinal for diagnostics and should not be fed to connector write tools.
- `set_system_classification` only writes to mechanical and piping systems; electrical assignment is read/report only in the current handler.

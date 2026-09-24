# Extended tool reference

Detail that does not fit the budgeted `tools/list` descriptions. The full
contract is always available at `horizun://contract/tools`.

## Styles, units and electrical

Three multi-operation tools share one write ritual (`Commands/VerifiedModelEdit.cs`):
`dry_run` (the default) applies the edit inside a transaction, regenerates, re-reads
every requested property through a `PostconditionCheck` and rolls back, reporting
Revit's rollback status and a single-use `confirmation_token` bound to the measured
"before" state. The apply spends the token, runs inside a `TransactionGroup`,
re-reads before and after the inner commit and only assimilates a fully verified
checklist; anything else rolls the group back. The reply publishes the checklist
re-read from the committed model (`postconditions`) and an `application` block.
Read operations need no token and accept an optional `target_document` guard.

### `horizun_manage_styles`

| operation | what it does |
|---|---|
| `list_object_styles` | Without `category`: top-level model and annotation categories. With `category` (OST_ name, id or name): that category and its subcategories. Rows: projection/cut weight, colour, projection line pattern, material, `graphics_style_id`. |
| `set_object_style` | `category` (+ optional `subcategory`) and any of `projection_weight`, `cut_weight` (cuttable categories only), `color` (#RRGGBB), `line_pattern` (name or `Solid`), `material` (exact name). |
| `create_subcategory` | `category` (parent) + `name`, with the same optional style fields. Refused when the parent does not allow subcategories or the name exists. |
| `list_line_styles` / `create_line_style` | The same, fixed to the Lines category (a line style IS a Lines subcategory). |
| `list_line_patterns` / `create_line_pattern` | `name` + `segments` `[{type: dash|space|dot, length}]` in `units` (mm default); re-read segment by segment. |
| `list_fill_patterns` / `create_fill_pattern` | `name`, `target` drafting/model, `fill` solid/hatch/crosshatch, `angle` (degrees), `spacing`, `spacing2`; re-read solid flag, grid count, first grid angle and spacing. |

Nothing here deletes a style. Subcategories, line patterns and fill patterns are
removed with `horizun_delete_verified` on the published ids; built-in categories
cannot be deleted.

### `horizun_manage_units`

| operation | what it does |
|---|---|
| `read` | FormatOptions per spec: unit, accuracy, symbol, zero/space suppression, digit grouping; plus the document's decimal and grouping symbols. Default: length, area, volume, angle, slope, air flow, pipe flow, current, potential, power. `specs=[...]` or `all=true` for more. Specs are resolved against the running Revit by short name (`length`), unversioned id or full ForgeTypeId, because spec versions differ between years. |
| `set` | `spec` with any of `unit`, `accuracy`, `symbol` (`none` clears it), `suppress_*`, `use_digit_grouping`; and/or `decimal_symbol`, `digit_grouping_symbol`. Invalid accuracy/unit/symbol is refused naming the valid ones. |
| `project_information` | Without `values`: the named fields (name, number, client, address, status, issue_date, author, building_name, organization_name, organization_description) and every Project Information parameter. With `values`: text and integer parameters written by field or parameter name, each re-read. Other storage types go through `horizun_write_params_verified`. |
| `base_points` | Without `project_position`: Project Base Point and Survey Point (internal and shared positions, pinned) and the project position at the base point (E/W, N/S, elevation, angle to true north). With `project_position` (`east_west`, `north_south`, `elevation` in `units`, `angle_to_true_north` in degrees) **and** `confirm_shared_coordinates=true`: `ProjectLocation.SetProjectPosition` at the project base point. |

**Risk of `base_points` writes.** Re-specifying shared coordinates moves the model
against everything positioned by them: linked models placed by shared coordinates,
coordinate-based IFC/DWG/NWC exports and survey ties. In a workshared model it
needs ownership of the shared coordinates. Only the project coordinator should
decide it; the extra `confirm_shared_coordinates` flag exists so a token alone
cannot do it.

### `horizun_electrical`

| operation | what it does |
|---|---|
| `list_panels` | Electrical equipment: panel name, family/type, distribution system and its voltage, circuits fed, summed apparent load (VA), Revit's total connected load and demand current strings. |
| `list_circuits` | Every `ElectricalSystem` (optionally `panel_id`): type, panel, number, load name, member ids, apparent (VA) and true (W) load, voltage, poles, length (m), voltage drop (V up to 2025; Revit's own parameter string on 2026–2027, where the API property was deprecated and then removed). |
| `create_circuit` | `element_ids` (MEP family instances), `system_type` (default PowerCircuit), optional `panel_id`. Re-reads members, type and panel. |
| `assign_panel` | `circuit_id` + `panel_id` via `SelectPanel`; re-reads the base equipment. |
| `add_to_circuit` / `remove_from_circuit` | `circuit_id` + `element_ids`; re-reads the member set. Emptying a circuit is refused (delete it instead). |
| `panel_schedule` | `panel_id` (+ optional `template_id`): `PanelScheduleView.CreateInstanceView`; re-reads the view's panel. Refused when the panel already has one. |

Voltage, poles and distribution-system compatibility are Revit's decisions: the
rehearsal performs the real call, so a refusal returns Revit's message with nothing
committed.

### Resumen (español)

Tres herramientas multi-operación con el mismo rito de escritura: ensayo en una
transacción que se revierte (dry_run por defecto), token de un solo uso, aplicación
dentro de un TransactionGroup y relectura de cada valor pedido tras el commit; si
algo no se verifica, el grupo se revierte. `horizun_manage_styles` lista y edita
estilos de objeto, crea subcategorías, estilos de línea, patrones de línea y de
relleno (no borra: eso es `horizun_delete_verified`). `horizun_manage_units` lee y
cambia el formato de unidades por spec (ForgeTypeId, 2023–2027), lee/escribe
Información de proyecto y lee los puntos base; escribir coordenadas compartidas
exige además `confirm_shared_coordinates=true`, porque mueve el modelo respecto de
vínculos, exportaciones y topografía. `horizun_electrical` lista tableros y
circuitos, crea circuitos, asigna tablero, agrega/quita elementos y crea la tabla
de tablero.

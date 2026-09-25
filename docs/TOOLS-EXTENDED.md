# Extended tool notes

Detail that does not belong in `tools/list` descriptions. One section per topic.

## Wire parse errors and cancellation retries

### Parse errors (-32700)

A line on stdin that is not valid JSON is answered with JSON-RPC error -32700,
`id: null`, and an `error.data` object:

| field | meaning |
|---|---|
| `hint` | `unescaped_backslash` (a Windows path such as `C:\x` not written as `C:\\x`), `concatenated_messages` (two messages on one line), `truncated_message` (the line ended inside a string or object, usually a raw newline inside a string value), `byte_order_mark`, `bare_word` (an unquoted word where JSON needs a quoted string, `:` or `,`), `not_json`, `invalid_json` |
| `line`, `column`, `offset`, `length` | where the parser stopped; `offset` is the zero-based character index in the received line |
| `shape`, `shape_caret` | a window of up to 20 characters either side of `offset`, with every letter shown as `a`, every digit as `0` and every non-ASCII character as `?`; quotes, backslashes and punctuation are kept |
| `content_echoed` | always `false`. The words, numbers, paths and tokens you sent are never repeated |

The server log line carries the same fields and the client name that
`initialize` declared (`clientInfo.name`, reduced to 40 safe characters).

Build every message with a JSON serializer (`ConvertTo-Json`, `json.dumps`) and end each one with a newline.
`scripts/hz-call.ps1` checks its arguments before it starts a server. It takes
`-ArgumentsObject @{ path = 'C:\folder\a.rvt' }` so a caller never has to escape
JSON by hand.

### Cancellation and timeout: what a retry does

When a tool call is cancelled by the client or times out, the error detail
(`revit_transport_failed`) includes a `retry` object, and the message ends with a
matching `RETRY:` sentence:

| `retry.verdict` | when | what to do |
|---|---|---|
| `same_key_runs_fresh` | the request was removed from Revit's queue before it started, or was never sent | send the identical call again. If it had an `idempotency_key` and `confirmation_token`, reuse them: neither was consumed |
| `same_key_replays_recorded_answer` | the request may have started, and it carried an `idempotency_key` | send the identical call with the **same** key. Revit runs one command at a time, so the retry waits behind the original and then replays the recorded answer from the durable ledger without writing again. A new key would write a second time |
| `inspect_model_first` | the request may have started, and it carried no key | nothing can prove what happened, so inspect the model before sending anything |

If the call waited 60 s or more, the sentence also names `horizun_submit_job`
and `horizun_job_status`, and `retry.prefer` is set to `horizun_submit_job`.
The size of the batch (`retry.batch_items`) is reported but does not trigger
this advice: in 1,447 logged `horizun_create_elements` calls the slowest took
3.4 s. Progress: any `tools/call` that sends `_meta.progressToken` already gets
`notifications/progress` every 5 s, with the bridge's queue or run state when
the bridge can see it.

---

**Resumen (español).** Un -32700 ahora dice dónde falló (línea, columna,
posición), qué tipo de fallo parece (`hint`: barra invertida sin escapar, dos
mensajes en una línea, mensaje cortado, BOM, palabra sin comillas) y una ventana
de texto donde cada letra se muestra como `a` y cada dígito como `0`. Nunca se
repite el contenido enviado. Una llamada cancelada o que agotó el tiempo indica
qué pasa si se reintenta. Si el trabajo no empezó, la misma llamada corre de
nuevo. Si pudo empezar y lleva `idempotency_key`, la misma clave devuelve la
respuesta registrada sin escribir otra vez; una clave nueva duplicaría la
escritura. Si pudo empezar y no lleva clave, hay que revisar el modelo antes de
reenviar nada. Si la llamada esperó 60 s o más, se recomienda
`horizun_submit_job`.



## Phases, design options, parts and assemblies

Two tools, each with an `operation` switch. Reads need no token. Every write takes
`target_document`, runs as a dry run by default and returns a single-use
`confirmation_token`; the apply spends it.

**How a write is verified.** The write runs inside a `TransactionGroup`. The dry
run applies it (every inner transaction commits), reads a `PostconditionCheck`
back from the model and rolls the whole group back. The apply does the same
write, checks it while the group can still be undone (any unverified property
rolls everything back), assimilates, then reads the checklist again from the
committed model. That second reading is what the reply publishes in
`postconditions`.

### `horizun_manage_phases`

| operation | writes | what it does |
|---|---|---|
| `list` | no | Phases in Revit's order; each phase filter with `presentation` for `new`, `existing`, `demolished`, `temporary` (`by_category`, `overridden`, `hidden`); design options with option set, `is_primary`, member count and the first 100 member ids; the active option. `design_options_writable` is always `false`. |
| `element_status` | no | For `element_ids`: created and demolished phase, `ElementOnPhaseStatus` in `phase_id` (default: last phase), and the design option (`main_model` when the element is in none). |
| `set_element_phases` | yes | Sets `created_phase_id` and/or `demolished_phase_id` (`-1` clears the demolition). The final pair is checked in order: demolition in the same phase is allowed (Temporary), before the creation is refused. Only elements where `HasPhases` and `ArePhasesModifiable` are true. |
| `create_phase_filter` | yes | `PhaseFilter.Create` with a unique `name`, then the presentations you name. |
| `edit_phase_filter` | yes | `filter_id` plus a new `name` and/or `presentation`. |
| `rename_phase` | yes | `phase_id` plus a unique `name`. |
| `create_phase` | refused | `no_phase_creation_api`. |
| `assign_design_option` | refused | `no_design_option_assignment_api`. |

**API limits (checked in RevitAPI.xml for 2023–2027, the same in every year).** No
call creates, inserts, deletes or reorders a phase. `Element.DesignOption` is
read-only, and nothing adds elements to an option set, moves them between options
or sets the active option. Python cannot do these things either, so the refusals
offer no fallback. Moving an element into a secondary option hides it from every
view that shows the primary option or the main model. That is why the refusal
tells you to review views before you do it by hand.

### `horizun_manage_assemblies_parts`

| operation | writes | what it does |
|---|---|---|
| `list` | no | Every part (id, excluded, source ids, original category), every assembly (name, naming category, members). With `element_ids`: whether each one is valid for parts, its part ids and its assembly. |
| `create_parts` | yes | `PartUtils.CreateParts` on `element_ids`. It refuses elements that already have parts or that fail `AreElementsValidForCreateParts`. Verified: each source has associated parts. |
| `divide_parts` | yes | `PartUtils.DivideParts` of part `element_ids` by `reference_ids`, which must be levels, grids or reference planes. The API requires a sketch plane even without curves, so a horizontal one is created. Verified: each divided part has at least two derived parts. The cut geometry itself is not checked. |
| `exclude_parts` / `restore_parts` | yes | Sets `Part.Excluded`, re-read per part. |
| `dissolve_parts` | yes | Deletes the `PartMaker` of each original in `element_ids`. This is the only way the API removes parts, and it loses every division, exclusion and part parameter. Verified: the originals still exist and have no parts. |
| `create_assembly` | yes | `AssemblyInstance.Create` with `naming_category_id` (default: the first member's category, checked with `IsValidNamingCategory`) and an optional unique `name`. The name is set in a second transaction because Revit only allows it after the creating transaction commits. Verified: members, naming category, name. |
| `assembly_views` | yes | `views` from `3d`, `plan`, `section_a`, `section_b`, `elevation_front`, `part_list`. Verified: each view's `AssociatedAssemblyInstanceId`. |
| `disassemble` | yes | `AssemblyInstance.Disassemble`. Verified: the instance is gone and every former member exists outside any assembly. |

The tool is marked destructive because `dissolve_parts` and `disassemble` remove elements.

### Live probes

`scripts/live-probes/phases-options-parts.probes.ps1` runs in the write tier of
`verify-live.ps1`. It creates three walls of its own at x ≥ 720 m and runs these
cases:

1. `list` reads the phases and the phase filters.
2. `create_phase` refuses with the API reason.
3. `set_element_phases` sets the first/last phase on one wall, reads the wall back as demolished, then restores it.
4. Design options `list`. It reports `not_covered` when the fixture has no options.
5. `create_parts` then `dissolve_parts` on one wall.
6. `create_assembly` of the other two walls, then `disassemble`.

The probe deletes the three walls afterwards. `phases-options-parts.tests.ps1` runs
the module offline against a fake `Call` and `Apply`. `divide_parts`,
`exclude_parts`, `assembly_views`, the phase-filter writes and `rename_phase` have
no live probe yet.

### Resumen (español)

- `horizun_manage_phases` lee:
  - las fases en orden;
  - los filtros de fase, con su presentación por estado nuevo, existente, demolido y temporal;
  - las opciones de diseño y el estado de fase de cada elemento.
- `horizun_manage_phases` escribe fases de elementos, crea y edita filtros de fase y renombra fases.
- La API de Revit 2023–2027 no permite dos cosas, y la herramienta las rechaza con el motivo y sin fallback a Python:
  - crear o reordenar fases;
  - mover elementos entre opciones de diseño.
- `horizun_manage_assemblies_parts` lista, crea, divide, excluye, restaura y disuelve partes. También crea ensamblajes, genera sus vistas y los desensambla.
- Cada escritura:
  1. se ensaya dentro de un TransactionGroup que se revierte;
  2. se aplica con el token;
  3. se relee del modelo confirmado.

  Si algo no cuadra, se revierte entera.



## Views, schedules and DWG layers

Extends three existing tools instead of adding new ones. Every write keeps the
existing contract: dry run by default, a confirmation token bound to what the ids
resolve to, one transaction, and a re-read after the commit.

### `horizun_manage_views` — filters, V/G and templates

| Operation | What it does | What is re-read |
|---|---|---|
| `edit_filter` | Replaces the rules of an existing filter (`filter_id`, `filter_name` or `filter_key`), optionally its `categories`; `rules` + `match` as in `create_filter`. Revit's own `ElementFilterIsAcceptableForParameterFilterElement` is asked before any transaction. | The filter's rules as Revit hands them back, as a text signature (`AND(`/`OR(`, rule class, parameter id, evaluator, value), compared with the signature of the filter that was written; the categories. The reply carries `rules_reread`. |
| `order_filters` | Puts `filter_ids` (ALL the filters of the view, top first) in that order. Revit has no `SetFilterOrder` in 2023–2027, so every filter is removed and added back, restoring its overrides, visibility and enabled flag. | `GetOrderedFilters()` and every filter's restored state. |
| `apply_filter` + `enabled` | The filter's *Enable Filter* flag (`SetIsFilterEnabled`). | `GetIsFilterEnabled`. |
| `explain_graphics` | READ. For 1–20 `element_ids` in a view: every layer that can decide how the element looks — element override, each filter in order (enabled, matches, visible, overrides), the category row — and the winner per property plus the visibility verdict. When the view's template governs filters or V/G, those rows come from the template and say `from_template`. The dry run already carries the report. | Recomputed after the commit on the apply path. |
| `set_category_visibility` | Now takes `subcategory` (by name), `overrides` (colours, weight, `line_pattern` by name or `Solid`, transparency, halftone) and/or `hidden`. A view whose template governs the category's V/G row is refused naming the template and the alternative: the same action with `view_id=<template>`, or `set_template_controls controlled=false`. | Hidden flag and each override field that was set. |
| `create_template` | `View.CreateViewTemplate` from `view_id`, named `name`. | `IsTemplate` and the name. |
| `set_template_controls` | Template `view_id`, `parameters` (BuiltInParameter names such as `VIS_GRAPHICS_MODEL`, or labels), `controlled` true/false. A parameter the template cannot govern is refused listing the ones it can. | `GetNonControlledTemplateParameterIds`. |
| `apply_template` with `template_view_id: -1` | Removes the template. | `ViewTemplateId` is invalid. |

Precedence used by `explain_graphics` (Revit's, not ours): element override, then
filters top-down (a higher filter wins property by property; disabled or
non-matching filters contribute nothing), then the category row, then object
styles. Visibility is not a precedence: any layer that hides wins.

### `horizun_create_schedule` — multi-category and key schedules

- `category: "OST_MultiCategory"` (or `multi_category`) creates a multi-category
  schedule (`ViewSchedule.CreateSchedule` with `InvalidElementId`, the same on
  2023–2027; `BuiltInCategory` has no `OST_MultiCategory`). `Category` is accepted
  as a field alias. The re-read checks the committed category id is invalid.
- `key_schedule: true` + `key_rows` (0–500) creates a key schedule over one
  category and inserts that many key rows. The re-read checks `IsKeySchedule` and
  counts the key elements owned by the schedule. `include_links` and `itemized` do
  not apply to key schedules and are refused when sent.

### `horizun_export` — the DWG layer table

- `format: "dwg_layers"`, `output_path` ending in `.json`, `dwg_setup.name`:
  reads the named DWG export setup's layer table and writes it to the file. If the
  setup does not exist it is created (from `dwg_setup.source`, or with Revit's
  defaults). `dwg_setup.layers` rows (`category` as a BuiltInCategory token or the
  display name, optional `subcategory`, `layer`, `color`, `cut_layer`, `cut_color`
  with AutoCAD index colours 1–255) edit existing rows; a row the table does not
  have is refused, never added.
- **Persistence is measured, not assumed.** After `SetExportLayerTable` +
  `SetDWGExportOptions`, the table is re-read from a fresh `FindByName` before the
  commit (a mismatch rolls back and names the rows) and again after it; each edit
  reports `persisted`. Save/close/reopen is not part of the measurement.
- `format: "dwg"` with `dwg_setup: { "name": ... }` exports using that setup
  (`DWGExportOptions.GetPredefinedOptions`); `acad_version` still overrides the
  file version. The layer mapping is not proved from the DWG binary.

API availability, checked in each `RevitAPI.xml` 2023–2027: `View.GetOrderedFilters`,
`SetIsFilterEnabled`, `ParameterFilterElement.SetElementFilter`,
`ViewSchedule.CreateKeySchedule`, `BaseExportOptions.Get/SetExportLayerTable`,
`ExportDWGSettings.Create/FindByName/SetDWGExportOptions` exist in all five years;
`SetFilterOrder` exists in none. Whether each year keeps the layer-table write is
recorded per year by the live probe `scripts/live-probes/views-schedules-dwg.probes.ps1`.

**Resumen (español).** `horizun_manage_views` suma editar reglas de filtros,
reordenar/habilitar filtros, un informe de precedencia (elemento > filtros en
orden > categoría > estilos de objeto; si la plantilla gobierna V/G, esas filas son
de la plantilla), V/G por categoría y subcategoría con patrón de línea, y
plantillas (crear, qué parámetros gobiernan, aplicar/quitar con `-1`). Una vista
cuya plantilla gobierna V/G se rechaza indicando editar la plantilla.
`horizun_create_schedule` crea tablas multicategoría (`OST_MultiCategory`) y de
claves (`key_schedule`, `key_rows`). `horizun_export` lee y escribe la tabla de
capas de una configuración DWG con nombre (`format: dwg_layers`) y exporta con
ella; la persistencia se mide releyendo antes y después del commit, por año.



## Groups and worksets

### `horizun_manage_groups`

| operation | needs | what is re-read after the commit |
|---|---|---|
| `list` | - (optional `type_id`, `max_rows`) | read only: every group type (kind `model` / `detail` / `attached_detail`), its instances, member ids (500 per instance), nested groups, parent group, attached detail group types |
| `create` | `element_ids`, `name` | the group exists, its members equal `element_ids`, the type carries `name` |
| `add_members` / `remove_members` | one `group_ids` entry (the reference), `element_ids`, `scope` when the type has other instances | the reference's members, the type name, whether the old type was deleted or kept, the new type's instance count, and every other instance |
| `rename_type` | `type_id`, `name` | the name and an unchanged instance count |
| `duplicate_type` | `type_id`, `name` | the new type's name, zero instances on it, the source's count unchanged |
| `swap_type` | `group_ids`, target `type_id` | each instance's type and the target's instance count |
| `ungroup` | `group_ids` | each group is gone and each former member exists outside any group |
| `convert_to_link` | - | refused: `code: api_absent` |

**Redefining a group.** Revit has no edit-group API. The reference instance is
ungrouped, the member set changed, and a new group type is made from it. The
reference gets a NEW group id and its instance parameters are not carried. When
the type has other instances the call must say `scope`:

- `all_instances`: every other instance is swapped onto the new type, the old type
  is deleted and the new one takes its name. Their member ids change, and members
  removed from them are deleted, not left loose. The new type's origin is Revit's,
  so each swapped instance is moved back by a displacement MEASURED from a member
  whose (category, type) is unique before and after; then every member it held
  before is checked by category, type and bounding box (0.001 ft). No unique
  member, no measurement: the whole change rolls back.
- `this_instance`: only the reference moves to a new type named `name`; the other
  instances stay on the old type, untouched.

A type with attached detail group types is refused: the regroup would orphan them.

**Convert to link.** `GroupType` offers `LoadFrom` and nothing that saves a group as
a model, in every RevitAPI 2023–2027. Do it in Revit (select the group, Link).

### `horizun_manage_worksets`

Every operation, `list` included, needs a workshared model; otherwise the reply is
`code: not_workshared` and nothing is read or written.

| operation | needs | re-read |
|---|---|---|
| `list` | - | read only: user worksets with open, editable, owner, visible-by-default, element count, and the active workset |
| `create` | `name` | the workset exists with that name |
| `rename` | `workset_id`, `name` | the name (a workset owned by another user is refused) |
| `move_elements` | destination `workset_id`, `element_ids` OR `category` (BuiltInCategory, host only, ≤ 10,000) | each moved element's `WorksetId`, and that no skipped element moved |
| `set_default` | `workset_id` (open) | the active workset id |
| `visibility` | `workset_id`, `view_ids`, `visibility` = `visible` / `hidden` / `use_global` | each view's workset visibility |

Elements owned by another user are reported with the owner (`borrowed_by_other`)
and never forced; elements whose workset parameter is read-only (group members,
hosted sub-elements) are reported `workset_not_editable`. Both make the outcome
`partial`, not `verified_applied`. `set_default` changes a session setting, so its
dry run is a measured preview, not a provisional change.

### Resumen en español

`horizun_manage_groups` lista, crea, redefine (añadir/quitar miembros), renombra,
duplica, cambia de tipo y desagrupa grupos. Revit no tiene API de "editar grupo":
la instancia de referencia se desagrupa y se reagrupa (obtiene un id nuevo). Si el
tipo tiene otras instancias, `scope` es obligatorio: `all_instances` cambia todas
(se recolocan por un desplazamiento medido y se comprueba cada miembro) y
`this_instance` solo la de referencia, con un tipo nuevo. `convert_to_link` se
rechaza tipado (`api_absent`): no existe en la API 2023–2027.
`horizun_manage_worksets` solo opera sobre modelos workshared (si no,
`not_workshared`): lista, crea, renombra, mueve elementos (los prestados por otro
usuario se reportan, nunca se fuerzan), fija el workset activo y la visibilidad
por vista. Todo con ensayo por defecto, token de un solo uso y relectura tras el
commit.



## MEP routing and sizing

`horizun_mep_routing` is one multi-operation tool over the routing preferences and
size catalogs of pipes, ducts, conduits and cable trays. It is separate from
`horizun_manage_system_types` on purpose: that tool duplicates types and writes their
parameters; a routing rule is not a parameter, a size catalog is not an element type,
and a resize is an instance write whose side effects land on other elements.

| operation | writes | what it does | what is re-read after commit |
|---|---|---|---|
| `read` | no | `type_id`: the type's rules per `RoutingPreferenceRuleGroupType` (part, description, size ranges), preferred junction and the fittings a rule can name. Conduit/cable-tray types carry no routing preferences; their elbow/tee/cross/transition/union are listed instead. `segment_id`: material, schedule, roughness, every nominal/inner/outer size. `element_ids`: kind, size, catalog and whether the size is in it. Nothing: every MEP type, every pipe segment, and the duct (round/rectangular/oval), conduit (per standard) and cable-tray catalogs. | - |
| `set_rules` | yes | Ordered `add` / `remove` / `move` edits per group on a pipe or duct type, plus optional `junction` (Tee/Tap). An add without `min_size`/`max_size` covers all sizes. Indexes refer to the list as the previous edit left it. | Every touched group equals the list computed from the list read before plus the edits; every untouched group is unchanged; the junction. |
| `add_sizes` / `remove_sizes` | yes | `catalog` = `segment` (with `segment_id`), `conduit` (with `conduit_standard`), `duct_round`, `duct_rectangular`, `duct_oval` or `cable_tray`. Segment and conduit sizes need `inner` and `outer`; conduit sizes also `bend_radius`. A size already present (add) or absent (remove) is refused; a size still used by an element is refused before any write, naming the elements. | Each size present with its inner/outer/bend, or absent; every other size unchanged. |
| `resize` | yes | `element_ids` or `system_id` (piping or duct network), to `diameter` or `width`+`height`. Each value must be a size of that element's own catalog (the pipe's segment, the duct shape's list, the conduit type's standard, the cable-tray list). Runs already at that size are skipped and listed. | Each run's size parameters equal the request, and every connector connected before is still connected. The fittings Revit removed/replaced, retyped or inserted (transitions) are reported in `result`. |
| `size_by_flow` | no | Per pipe or round/rectangular duct: the smallest catalog size (`used_in_sizing`) whose free area carries the flow at or below `max_velocity` (m/s). Flow is the element's calculated flow, or `flow` (L/s) for all. Pipes use the segment's inner diameter; rectangular ducts hold the current `height` (or the one given) and pick the width. `resize_calls` groups the proposals into ready `resize` requests. | - |

Writes follow the bridge's two-step flow: the dry run (default) applies the change in
a transaction, verifies it, rolls it back and returns a `confirmation_token`; the apply
spends the token, re-verifies inside a TransactionGroup and rolls everything back on
any mismatch. Units are `mm` (default), `in` or `feet`; catalog sizes match at
1e-5 ft (about 0.003 mm).

**Limits.** `size_by_flow` claims velocity only: no friction, pressure drop or fluid
properties, because Revit's duct/pipe sizing dialog has no public API and a
friction-based size would be a guess. Oval ducts, conduits and cable trays are not
sized by flow. Flex pipes and flex ducts are outside `resize` and carry the Python
fallback grant. Moving a rule whose criterion is not a size range is refused rather
than dropping the criterion. The API members used (`RoutingPreferenceManager`,
`RoutingPreferenceRule`, `PrimarySizeCriterion`, `Segment`/`PipeSegment` sizes,
`DuctSizeSettings`, `ConduitSizeSettings`, `CableTraySizes`) read identically in the
2023-2027 API documentation, so there is no per-year branch.

**Resumen (español).** `horizun_mep_routing` lee y edita las preferencias de
enrutamiento (reglas por grupo con rangos de tamaño, unión preferida), los segmentos
de tubería y los catálogos de tamaños de ducto, conduit y bandeja; cambia el tamaño de
tubos, ductos, conduits y bandejas solo a tamaños de su propio catálogo, verifica
releyendo tras el commit y reporta los accesorios que Revit reemplazó o insertó; y
propone tamaños por caudal con un límite de velocidad (solo velocidad, sin fricción:
el dimensionamiento de Revit no tiene API pública) sin escribir nada.



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



## Curtain grids, railings, slab shape and arrays

All four follow the same discipline: dry run by default, a single-use
`confirmation_token` for the apply, the committed model re-read through a
`PostconditionCheck`, and the whole edit rolled back when it disagrees. Units are
`mm` unless `units` says otherwise.

### `horizun_manage_curtain`

`element_id` is a curtain wall or a curtain system (`grid_index` picks the system's grid).

| operation | arguments | re-read after commit |
|---|---|---|
| `read` | – | u/v lines (ends, `offset` on walls, segments, mullions on each, lock/pin), mullions (type, the grid line they sit on by geometry), panels (type, category, `is_door`, centre), `base_z` |
| `add_grid_line` | `direction` u\|v, `offset` (walls: v = along the location line from its start, u = above the base) **or** `point` | exactly one new line of that direction, at the offset within 0.5 mm (or through the point) |
| `remove_grid_line` | `grid_line_id` | the line is gone and the count fell by one |
| `set_mullions` | `grid_line_id`, `mode` add\|remove, `mullion_type_id` (add), `segment_index` (else every segment) | each targeted segment carries a mullion of that type / none |
| `set_panel_type` | `panel_ids` **or** `point`, `type_id` (panel, curtain-wall door/window, or wall type) | the panel's type, including when Revit replaces the element (new id reported) |

Revit keeps no link between a mullion and its grid line: membership is geometric
(within 1 mm). A segment that already carries a mullion is refused for `add`
(Revit would silently leave it); change its type with `horizun_transform_elements`
`change_type`. Grid lines and panels governed by the wall type's layout may be
refused by Revit; the refusal and the rollback are reported.

### `horizun_slab_shape`

`element_id` is a floor or a roof. `read` lists vertices `[x, y, z, type]`, creases
and the slab top. `add_point` takes `points: [[x, y, offset]]`; `modify_subelement`
takes `points` for existing vertices, or `start`/`end` (`[x, y]`) plus `offset` for
one crease; `add_split_line` takes `start`/`end` and adds a missing interior end
point first; `reset_shape` erases the shape points.

The API changed between years, measured from each year's `RevitAPI.xml`:
2023 has only the `SlabShapeEditor` property and `DrawPoint`/`DrawSplitLine`;
2024 adds `GetSlabShapeEditor()`; 2025 adds `AddPoint`/`AddSplitLine`; 2026–2027
drop the property and the `Draw*` calls. The command compiles the right call per year.
`SlabShapeVertex.Position.Z` is not documented as absolute or relative to the slab
top, so both readings are measured and the one that held is returned in
`evidence.z_convention`.

### `horizun_create_railing`

Either `host_id` (a stair or ramp; `placement` treads\|stringer; Revit creates one
railing per side it decides, all ids are returned and each host is re-read) or `path`
(an open polyline of XYZ points on `level_id`, checked with
`Railing.IsValidPathForRailing`; `GetPath()` is compared segment by segment in plan).
`base_offset` sets `STAIRS_RAILING_HEIGHT_OFFSET`. The height is the type's and is
reported as `type_height`.

### Arrays in `horizun_transform_elements`

`array_linear` (`vector`) and `array_radial` (`axis_start`, `axis_end`,
`angle_degrees`) with `count` (members including the original: linear 2–200,
radial 3–200), `anchor` second\|last and `group` (default false: copies are free
elements; true keeps Revit's associated array, whose id is returned). The array is
created in the active view. Member k of each source is expected at k × step:
anchor=second uses the vector/angle as the step, anchor=last splits it over
count − 1 (a full 360° turn over count). Every copy is matched against that formula
within 1e-5 ft; grouped members are judged by the elements inside their groups.
A radial copy's position is checked, not its axes.

### Resumen (español)

`horizun_manage_curtain` lee y edita rejillas de muro cortina (líneas U/V con
offset medido sobre el muro, montantes por segmento, tipo de panel incluidas
puertas de muro cortina). `horizun_slab_shape` edita la forma de losas y cubiertas
con guardas por año de la API (2023 propiedad + `DrawPoint`; 2025+ `AddPoint`;
2026+ solo `GetSlabShapeEditor`) y relee la elevación de cada vértice, informando
qué lectura de `Position.Z` se cumplió. `horizun_create_railing` crea barandas sobre
escalera/rampa o por boceto y relee tipo, anfitrión y trayecto.
`horizun_transform_elements` suma `array_linear` y `array_radial`, con o sin
asociación, y verifica cada copia contra la fórmula. Todo en ensayo por defecto,
con token y rollback si la relectura no coincide.



## Clash resolution and batch undo

### horizun_resolve_clash

Detection is common; resolving a clash **with a verification** is the point of this tool.
It works on findings of the ledger that `horizun_clash record_findings=true` maintains
(see `horizun_coordination list`).

**propose** (read-only, default). For each `finding_ids` entry:

1. Roles. Pipes, ducts, conduits, cable trays and flex runs are *movable*. Structure
   (structural framing/columns/foundations, structural walls and floors) and
   architecture are never moved: the row is `report_only` with
   `report_only_no_movable_side`. When both sides are runs, the smaller cross section
   moves; equal sections are `ambiguous_both_movable` (a design decision).
2. Safety. A run in a linked model (`linked_element_not_writable`), a pinned run
   (`pinned`) or a run with any connected connector (`connected_run_not_safe` -
   moving one segment would tear the network) is reported, not moved.
3. Geometry (`Core/ClashResolveRules.cs`). The mover's centreline and exact section are
   compared with the fixed element's bounding box projected on each escape direction:
   `shift` perpendicular to the run in plan, and `elevation` (vertical) for horizontal
   runs only. The run's own axis is never an escape. Distances are whole millimetres,
   rounded away from the clash, and include `clearance_mm` (default 50). Candidates over
   `max_move_mm` (default 600) are rejected.
4. Third elements. A candidate whose moved box would reach any other model element is
   rejected (`would_touch_other_elements`); if none survives, the finding is report-only.

Each proposal carries `kind`, `distance_mm`, `affected_elements`, a `prediction`, and the
ready `next_arguments` for apply.

**apply**. `dry_run` defaults to true and issues a token bound to the proposals. With the
token, inside one TransactionGroup: the neighbourhood (every host model element whose box
meets the swept region of each mover, grown by the clearance) is measured on solids
BEFORE the move; the runs are moved; positions are re-read; the same neighbourhood is
measured AFTER. The group is kept only when every targeted pair is gone and no pair
appears that was not there before. Anything else - including a boolean that failed, so
the result is unmeasured - rolls the whole group back and returns `new_clashes` and the
`postconditions` checklist. A kept apply records an undo batch and marks each finding
`resolved_by_model` with the measurement written into its history.

Limit: the re-detection covers host elements only; a clash the move creates against a
linked model is not seen (declared in `WriteVerificationCatalog`).

### horizun_undo

Revit exposes no Undo through its API. After a verified commit,
`horizun_transform_elements` (move, copy, rotate, mirror, pin/unpin, change_type,
set_curve on a line, move_tag_head), `horizun_write_params_verified`,
`horizun_create_elements` and `horizun_resolve_clash` record an inverse in
`<data root>/undo/<document hash>.json` (last 20 batches) and report it as an `undo`
block. Operations without an inverse (set_tag_leader, wall_join, a non-line set_curve,
a parameter whose previous value was unreadable) record the batch as **not undoable** by
name, so `undo_last` never skips past it to an older batch.

- `list` - the document's batches, newest first, and whether `undo_last` is available.
- `undo_last` - dry run first. Refused when the document was saved or synchronized since
  the batch (its VersionGUID/NumberOfSaves stamp moved), or when any element the batch
  touched no longer carries the state the batch left (location, type, pin, orientation,
  tag head, or the parameter value). Otherwise every inverse runs in one transaction in
  reverse order (created -> deleted, parameters -> previous values, moves -> the opposite
  vector, rotations -> the opposite angle, mirrors -> the same plane); a delete that would
  take elements the batch did not create rolls back; every element is re-read against
  the state the batch found, in a `PostconditionCheck`.

### Resumen (español)

`horizun_resolve_clash propose` convierte hallazgos abiertos del ledger en correcciones
candidatas conservadoras: solo mueve tramos MEP del modelo anfitrión, sin conexiones ni
pin, el mínimo más la holgura (desplazamiento perpendicular o cambio de elevación);
estructura, arquitectura, vínculos, tramos conectados o movimientos que tocarían a un
tercero quedan como "solo informar". `apply` mueve dentro de un TransactionGroup y
vuelve a detectar sobre sólidos: el par debe desaparecer sin clashes nuevos o se revierte
todo; solo entonces el hallazgo pasa a `resolved_by_model`. `horizun_undo` deshace el
último lote Horizun registrado, y se niega si el documento se guardó o sincronizó desde
entonces o si esos elementos cambiaron.



## Parameters and classification

### `horizun_manage_parameters`

| operation | writes | what it does |
|---|---|---|
| `list_bindings` | no | Every entry of the BindingMap: name, parameter element id, data type (SpecTypeId), Instance/Type, categories, group, shared + GUID, VariesAcrossGroups. |
| `create_shared` | model + SPF | Reuses or creates the definition `name` in group `spf_group` of `spf_path` (file created if missing), then binds it to `categories` as `binding_kind` in `group` (default `PG_DATA`). Instance bindings get `SetAllowVaryBetweenGroups(true)` unless `allow_vary_between_groups=false`. |
| `create_project` | no | Refused by name: RevitAPI 2023-2027 has no call that creates a non-shared project parameter (`SharedParameterElement.Create` is the only factory; measured by reflection over all five years). No Python fallback is offered, because a script meets the same absent API. |
| `rebind` | model | Changes the category set (`categories` is the FINAL set), Instance/Type and/or group of an existing binding, found by `guid` or a unique `name`. |
| `remove_binding` | model | Removes the binding; the parameter element stays in the document. |
| `global_list` | no | Every Global Parameter: value (internal, display, formatted), formula, reporting, affected elements. |
| `global_create` / `global_set` / `global_delete` | model | `value` in the project's display units for measurable specs; `formula`; `associate: [{element_id, parameter}]`. `value` and `formula` are exclusive; a value on a formula-driven global is refused. |

**Data types.** 2023+ has no `ParameterType`. `data_type` accepts a SpecTypeId id
(`autodesk.spec.aec:length-2.0.0`, version optional), a SpecTypeId path (`String.Text`,
`Boolean.YesNo`, `Length`) or a legacy ParameterType name (`Text`, `YesNo`, `Integer`,
`Number`, `Length`, `Area`, `Volume`, `Angle`, `URL`, `Material`...), mapped in
`Core/ParameterClassificationRules.cs`.

**Rehearsal and verification.** `dry_run` defaults to true: the write runs in a
transaction that is rolled back and the reply carries the postcondition checklist and a
single-use `confirmation_token`. For `create_shared` the rehearsal runs against a
temporary COPY of the SPF, so the caller's file is never touched by a dry run. Apply
re-runs the same plan, checks it before the commit and re-reads it after:
binding kind, categories, group, data type, VariesAcrossGroups, and for `create_shared`
the SPF FILE parsed from disk (GUID, name, group). `rebind` / `remove_binding` count the
elements (instances or types) that hold a value in the categories being dropped; that
count is part of the plan the token binds (`ExpectedCascadeCount`), so a model that
gained values since the dry run is refused as a stale plan.

**Residual gaps.** The SPF definition is written before the transaction; a binding that
then fails leaves the definition in the file (`spf_definition_created` says so).
`global_set` checks each association, not that the element parameter already shows the
global's value.

### `horizun_query_classification` (read only)

| operation | returns |
|---|---|
| `keynote_table` / `assembly_code` | `source` (element id, path, server id, version status, file exists, link status), every entry (code, parent, text, level) with `types` and `instances` carrying that code, `codes_in_use`. |
| `unused_codes` | Codes of `table` that no type carries. |
| `missing_codes` | Placed types (one or more instances) without a code, and types (or materials, for keynotes) whose code is not in the table. |
| `family_lookup_tables` | Per family (all, or `family_id`): size table names, columns (name, spec) and row count. |

Codes are read from `KEYNOTE_PARAM` (types and materials) and from the assembly code
parameter of types (`UNIFORMAT_CODE` up to 2025, `ASSEMBLY_CODE` from 2026 - renamed in
the API, guarded per year). An empty or unloaded table is reported with a `warning`,
because it makes every code read as absent. Lookup tables are read with
`FamilySizeTableManager.GetFamilySizeTableManager(projectDocument, familyId)`: the
family is not opened, edited or reloaded.

### Live probes

`scripts/live-probes/parameters.probes.ps1` (offline test: `parameters.tests.ps1`):
list_bindings; create_project refused; create_shared of `HZ_PROBE_<run>` (Text, Walls,
Instance) into a temporary SPF under ScratchRoot, verified in the model and in the file,
then removed with remove_binding; a Length global created at 1500, read back, set to
2500 and deleted; keynote_table and family_lookup_tables read.

---

**Resumen en español.** `horizun_manage_parameters` lista el BindingMap completo, crea
parámetros compartidos en un SPF (el ensayo usa una copia temporal del SPF) y los
bindea, cambia o retira bindings contando los valores que se perderían, y gestiona
Global Parameters (valor en unidades de visualización, fórmula, asociación a parámetros
de elementos, borrado) releyendo el valor calculado. `create_project` se rechaza con
nombre: ninguna API de Revit 2023-2027 crea parámetros de proyecto no compartidos.
`horizun_query_classification` es solo lectura: tablas de keynote y Assembly Code (ruta,
estado, entradas, padre, uso por código), códigos sin uso, tipos sin código o con
códigos que no existen en la tabla, y tablas de búsqueda de familias leídas desde el
proyecto sin abrir la familia.



## Model diff, explanation and quality history

`horizun_model_diff` answers the question a contractor asks of every new
delivery - *what changed?* - without a coordination model or a linked copy.

### Operations

| operation | reads | writes |
|---|---|---|
| `snapshot` | active document, or `file_path` + `expected_version` opened in the background (detached when workshared) and closed without saving | `%USERPROFILE%\.horizun\snapshots\<id>.json.gz` + `<id>.meta.json` |
| `list` | the meta files | nothing |
| `compare` | two sides: a snapshot id or `active` | `snapshots\exports\<comparison>.csv` and `.json` |
| `colorize` | `before` snapshot vs the active model | a NEW view duplicated from `view_id` with overrides (the only model write) |
| `explain` | the active model, the project's quality history, `project_context_path` | nothing |
| `record_quality` | runs `horizun_model_scan` (summary) or `horizun_audit_model` in process | one line appended to `quality-history\<project>.jsonl` |
| `quality_trend` | the JSONL | `quality-history\<project>.trend.csv` |

### What a snapshot holds

Model elements only (model categories, not types, not view-specific): UniqueId,
element id, BuiltInCategory and category name, family and type (and the type's
UniqueId), level, workset, created/demolished phase, bounding box and location
(point or curve end points) in internal feet, every instance parameter and -
once per type - every type parameter, normalised (`d:` doubles rounded to 1e-9
in internal units, `i:` integers, `s:` strings trimmed to 256 characters, `e:`
element ids, `n:` no value), and a hash of volume, area and bounding-box extent.
`EDITED_BY` is skipped: who borrowed an element last is not a model change.
`max_elements` (default 50,000, cap 500,000) and `categories` bound it; a
truncated snapshot says so and so does every comparison using it. The document
facts carry the title, path, Revit version, saved format, VersionGUID and number
of saves when Revit exposes them.

### How a comparison decides

- **Identity is the UniqueId.** An element in both sides is compared; otherwise
  it is added or deleted.
- **Modified** means any of: type (family/type/type UniqueId), level, workset,
  phases, a move above `move_tolerance_mm` (default 1 mm; location first, else
  bounding-box centre, reported as `moved_mm`), geometry hash, or an instance
  parameter whose value differs (numbers within 1e-6 internal units are equal).
  Each change carries `before` and `after`.
- **Type parameters** are reported once per type in `type_changes`, not on each
  instance.
- **Re-created models.** When the smaller side shares under 50 % of its
  UniqueIds (and holds at least 10 elements), `summary.identity_warning` says so.
  `heuristic_match=true` then pairs deleted and added elements with the same
  category, family and type whose anchor points lie within 50 mm, nearest first,
  each used once; every such row has `inferred=true` and `before_unique_id`.
- **Discipline** is inferred from the BuiltInCategory (structure, mechanical,
  plumbing, electrical, site, architecture) - Revit declares none per element.
- `detail` is paged (`offset`, `limit` up to 1,000); the exports hold every row.
  CSV cells that start with `= + - @` (and are not numbers) are prefixed with `'`.

### colorize

`dry_run` (default) resolves the comparison and issues a token bound to the
document, `before`, `view_id` and the exact set of element UniqueIds to colour.
The apply duplicates the view (no detailing), removes its template, names it
`Horizun diff <snapshot>`, overrides added elements green (0,170,0) and modified
orange (255,140,0) - projection line colour plus a solid surface pattern when the
model has one - re-reads every override before committing (rolls back on any
mismatch) and again after. Elements not visible in the view are counted in
`not_in_view`; deleted elements cannot be coloured and are counted too.

### Quality history

`record_quality` stores what the scan or audit itself reported: for
`model_scan`, every bucket `total` of every section with `status: ok` as
`section.bucket`, plus `document.*_count` and `file_size_mb`; a failed section
contributes nothing and is listed in `failed`. For `audit_model`, each finding's
`count` and `is_issue`, and `health.score` when a health profile produced one.
`complete=false` marks a run that did not see the whole model. Malformed lines in
the JSONL are counted in `malformed_lines`, never skipped silently. `quality_trend`
returns one wide row per run (filter with `metrics`, `*` suffix allowed), ready
to pass as `rows` to `horizun_power_bi_push`.

### Limits

- A background open refuses when the file's version differs from the host
  (no upgrade), and cloud models are not opened by `snapshot`.
- The geometry hash is cheap by design: a reshaped element with the same volume,
  area and extent is not detected as a geometry change.
- `colorize` re-reads the projection line colour of each override; the surface
  pattern is set but not compared.

### Resumen (español)

`horizun_model_diff` responde "¿qué cambió entre estas dos entregas?". `snapshot`
guarda una instantánea del modelo activo o de un `.rvt` abierto en segundo plano
(desvinculado si es colaborativo) y cerrado sin guardar; `compare` reporta
añadidos, borrados y modificados (parámetros antes/después, movimientos sobre la
tolerancia, cambios de tipo) por categoría, disciplina inferida y nivel, con
paginación y exportación CSV/JSON. La identidad es el UniqueId; si el modelo fue
re-creado se avisa y `heuristic_match` empareja por categoría/tipo/ubicación,
marcando cada par como `inferred`. `colorize` es la única escritura: duplica una
vista y colorea con dry_run, token y relectura. `explain` resume solo hechos
medidos y, con `project_context_path`, lo que falta para ISO 19650 según el mismo
validador de `horizun_project_context`. `record_quality` y `quality_trend` llevan
el historial de calidad en JSONL y lo entregan listo para Power BI.



## Impact preview (MCP Apps)

**What it is.** An MCP App served at `ui://horizun/impact-preview`
(`text/html;profile=mcp-app`, MCP Apps extension, spec 2026-01-26). A host that
supports MCP Apps shows it beside the reply of a bulk write's **rehearsal**
(`dry_run=true`, the default) so a person can see what the write will touch before
approving it, and take some elements out.

**Which tools declare it.** Only the five whose rehearsal payload it knows how to read,
through `_meta.ui.resourceUri` in `tools/list`:

| Tool | Rows shown | What "exclude" removes from the request |
|---|---|---|
| `horizun_write_params_verified` | one per write (`rows[].index`), before -> requested | that entry of `writes[]` |
| `horizun_set_keynote` | one per resolved target (type or instance), current -> new keynote | every id in `element_ids` that resolved to that target |
| `horizun_delete_verified` | one per requested id and per cascade (`change_preview`) | `mode=ids`: the id from `ids`; `mode=purge_unused`: the id is added to `protect_ids`. A cascade row cannot be excluded alone: exclude the id that causes it |
| `horizun_transform_elements` | one per element (`change_preview`), operation / type / pin before -> after | the id from each operation's `element_ids`; an operation left empty is dropped |
| `horizun_create_elements` | one per planned element (`create:<index>`), kind, type, level | that entry of `elements[]` |

A rehearsal expanded from a CSV (`tabular_source`) is shown but cannot be narrowed row by
row: the rows are generated from the file on every call.

**What it shows.** How many elements the resolved plan holds (`plan_resolved.elements`),
counts by category and by level, a table of the rows with before -> after where the reply
has both, and warnings: elements you did not name that change too (shared type,
cascade), a blast radius that is a lower bound, unresolved entries, a withheld token,
and "only N of M rows are shown - the rest WILL be applied" when `change_preview` was
truncated (it carries at most 50 rows; exclusion is only possible for shown rows).

**The contract it keeps.** A `confirmation_token` approves ONE request: all five commands
fold the field the app narrows into their plan hash (`ImpactPreviewAppTests` asserts it
against the command sources). So the app never spends an old token on fewer elements:

1. With rows excluded, **Rehearse without N excluded** calls the same tool again with
   `dry_run=true`, the reduced request, and no token or idempotency key. The new
   rehearsal replaces the old one on screen, with a check that the excluded rows left
   the plan and that it did not grow.
2. With nothing excluded, **Apply** calls the tool with the arguments of the rehearsal on
   screen, `dry_run=false`, that rehearsal's own `confirmation_token` and a fresh
   `idempotency_key` (kept for a retry of the same token, so a retry replays instead of
   writing twice). The reply's `application.state` is shown; the button is then spent.

The app only calls tools when the host grants it (`hostCapabilities.serverTools`) and it
knows the original arguments (`ui/notifications/tool-input`); otherwise it is a read-only
view and the apply goes through the chat as always. After a re-rehearsal or an apply it
tells the model what happened through `ui/update-model-context`. The command's own gate
still decides: a stale plan, an expired token or a different document is refused by the
bridge exactly as for any other client.

**What it does not do.** It fetches nothing and loads nothing: no URL, no CDN, empty CSP
(`connectDomains`, `resourceDomains`, `frameDomains`, `baseUriDomains`). It renders only
what the rehearsal said. No view thumbnail: `horizun_capture_view` holds Revit's UI thread
and would be a tool call the user did not ask for. A host without MCP Apps sees exactly
the reply it saw before.

**Hosts.** The MCP Apps project lists ChatGPT, Claude, VS Code, Goose, Postman, MCPJam,
mcp-use and Alpic Playground as supporting hosts
([ext-apps README](https://github.com/modelcontextprotocol/ext-apps), read 2026-09-24).
Not measured here in any of them; the flow was exercised against a scripted host that
speaks the 2026-01-26 messages.

**Tests.** `tests/Horizun.Server.Tests/ImpactPreviewAppTests.cs` (resource, mime type,
CSP, declaring tools, tools/list cost, no network, plan-hash coverage, field names tied to
the emitting source) and `ImpactPreview/adapter.test.js`, the pure adapter under node
against five synthetic fixtures (no live capture of these rehearsals existed when it was
written). `scripts/live-probes/impact-preview.probes.ps1` measures the real replies in
Revit, including that a full-plan token is refused for a narrowed request.

### Resumen (español)

`ui://horizun/impact-preview` es una MCP App que muestra el **ensayo** (dry_run) de las
cinco escrituras masivas - `write_params_verified`, `set_keynote`, `delete_verified`,
`transform_elements`, `create_elements`: total por categoría y nivel, antes -> después por
fila, advertencias (colaterales, cascada, vista truncada, token retenido) y una casilla
por fila para **excluirla**. El token ata la petición completa (el campo que se reduce
entra en el hash del plan de cada comando), así que excluir filas pide un **nuevo
ensayo** de la petición reducida, y **Aplicar** solo gasta el token del ensayo que está en
pantalla, con una idempotency_key nueva. HTML autocontenido sin URLs ni CSP abierta, modo
claro/oscuro, tabla accesible. Sin herramientas nuevas: tools/list crece 305 bytes.
Hosts según el proyecto MCP Apps: ChatGPT, Claude, VS Code, Goose, Postman, MCPJam,
mcp-use y Alpic Playground (no medido aquí en ninguno).



## Code checks, 4D and federation

### `horizun_code_check` — a requirement set with geometry

Runs the grammar of [requirement-set.md](requirement-set.md) over the active
model. That grammar had a loader (`Core/RequirementSet.cs`) and no tool; this is
the tool, and it runs `docs/requirement-sets/*.json` unchanged. It is separate from
`horizun_audit_model` (whose `requirement_set` gates the audit's aggregate counts)
and from `horizun_audit_access` (a flat threshold map without per-rule selectors,
citations or `not_decidable`).

Additions to the grammar, all optional:

| Key | Where | Meaning |
|---|---|---|
| `measure` | assertion | A geometric quantity the bridge computes, instead of `parameter` (exactly one of the two). |
| `between` | operator | `value: [min, max]`, inclusive. |
| `unit` | assertion | `mm`, `m`, `m2`, `lx`: a numeric parameter is converted to it first; a parameter whose spec does not accept the unit reads as blank. |
| `missing_is` | assertion | `fails` (default) or `not_decidable` for an absent or blank value. |
| `name_matches`, `parameter_equals`, `measure_range` | selector | Element name regex; `{parameter, value}`; `{measure, min?, max?}` selects elements whose measure lies in `(min, max]`. |
| `source` | rule | Norm and numeral, echoed on every rule row. |
| `unverified_value` | rule | The threshold was not verified against the norm's text: the value may be omitted and every element is `not_decidable`. |
| `config` | rule | Configuration of `exit_count_minus_required`. |

Unknown keys in a rule, selector or assertion are refused, like unknown top-level keys.

Measures (`Core/CodeCheckRules.cs`):

| Measure | Source | Quality |
|---|---|---|
| `door_clear_width_mm` | door Width (instance, then type) | UPPER bound: below a minimum it fails for certain, above it is `not_decidable`. |
| `ramp_slope_percent`, `ramp_run_length_mm`, `ramp_width_mm`, `ramp_landing_length_mm` | the ramp's own planar top faces | exact geometry; a ramp with no flat top face has no landing measure. |
| `stair_riser_mm`, `stair_tread_mm`, `stair_2r_plus_t_mm`, `stair_run_width_mm` | `Stairs.ActualRiserHeight/ActualTreadDepth`, narrowest `StairsRun.ActualRunWidth` | exact; handrails not deducted. |
| `space_illuminance_lx` | Space `Average Estimated Illumination` | 0 or empty is `not_decidable` (Revit computed nothing). |
| `exit_count_minus_required` | per Level: placed rooms, doors | needs `config`: an occupant load (`occupant_load_parameter`, or `occupancy_parameter` + `area_per_person_by_group: {group: m2}` so each room takes the factor of the group it declares, or one `area_per_person_m2`), `exit_door` (`{parameter, value}` or `{mark_prefix}`) and `required_exits: [{max_load, exits}]`; any missing piece - including a room with no group or a group the table does not list - is `not_decidable`. |
| `travel_distance_m` | — | never computed here: always `not_decidable`. `horizun_audit_access` with `route_view_id` routes a real path. |

Outcomes are `passes`, `fails`, `not_decidable`, `unreadable`. A rule's verdict is
`fails` if any element fails, `not_decidable` if any element is undecided or the
rule examined nothing, `passes` otherwise. Parameters may be named by display name
or by `BuiltInParameter` token (`ALL_MODEL_MARK`), which does not change with the
language of Revit. Categories accept `OST_*` tokens or display names.

Example sets in `standards/` (data, not compiled in; review before use):

- `co-ntc6047-accesibilidad.json` — NTC 6047:2013: door clear width 800 mm (16.1.2),
  ramp width 1 200 mm (8.2.3), slope by run length (8.2.2, Tabla 2), landing 1 500 mm
  (8.2.4), stairs riser ≤ 180, tread ≥ 260, 2C+H 600–660, flight width 1 200 (11.1–11.2).
  The 1:14 row of Tabla 2 is ambiguous in the PDF (3 640 or 3 920 mm) and that band
  is marked `unverified_value`.
- `co-nsr10-titulo-k-evacuacion.json` — NSR-10 Título K, K.3, read on 2026-09-24 from
  the text of the Comisión Asesora Permanente
  ([idrd.gov.co copy](https://idrd.gov.co/sites/default/files/documentos/Construcciones/11titulo-k-nsr-100.pdf),
  pages K-15 to K-23) and checked against MinVivienda's
  [anexo técnico de modificaciones](https://minvivienda.gov.co/sites/default/files/consultasp/Anexo%20t%C3%A9cnico_4.pdf),
  whose only change to Título K is the K.3.2.5 cross-reference. Values below; "set"
  marks the ones the set applies. Exit doors, 1 200 mm stairs and room occupancy groups
  are selected through placeholder parameters (`Exit Door`, `NSR10 Stair Class`,
  `Occupancy Group`) that a project renames.

  | Requirement | Value | Numeral | In the set |
  |---|---|---|---|
  | Riser (contrahuella) | 100 to 180 mm | K.3.8.3.4 (b) | set |
  | Tread (huella), straight flight | ≥ 280 mm | K.3.8.3.4 (a) | set |
  | 2 risers + 1 tread | 620 to 640 mm | K.3.8.3.4 (c) | set |
  | Curved flights / circular / spiral tread | ≥ 240 mm at 1/3 (≤ 420 outer) / ≥ 250 mm / ≥ 190 mm at 300 mm | K.3.8.3.4 (d), K.3.8.3.9, K.3.8.3.10 | no (not separated by the selector) |
  | Stair width, load < 50 | ≥ 900 mm | K.3.8.3.3 | set (floor for every evacuation stair) |
  | Stair width, load > 50 or public use | ≥ 1 200 mm | K.3.8.3.3 | set, on stairs marked by a placeholder |
  | Stair width inside dwellings / single-family | 900 / 750 mm | K.3.8.3.3 | no (K.3.8.3 excludes stairs inside dwellings) |
  | Landing depth; rise between landings | = stair width, ≤ 1.20 m needed; < 2.40 m (assembly, institutional), < 3.50 m others | K.3.8.3.5 | no measure |
  | Stair headroom | ≥ 2.0 m | K.3.8.3.7 | no measure |
  | Exit door clear width | ≥ 800 mm (bedrooms 700; each leaf of a split door ≥ 700) | K.3.8.2.1 | set (upper bound: nominal leaf) |
  | Exit door height | ≥ 2.0 m | K.3.8.2.1 | set (`DOOR_HEIGHT`) |
  | Doors in series; opening force | ≥ 2.10 m apart; < 250 N | K.3.8.2.3, K.3.8.2.6 | no measure |
  | Exit access (corridor) width | ≥ 900 mm and ≥ capacity by Tabla K.3.3-2 | K.3.3.4 | no measure |
  | Width per person: corridors, doors, passages / stairs (mm) | A 5/8, C 5/10, F 6/10, I-1 6/10, I-2…I-5 13/15, L 5/10, P 10/18, R 5/10; −50 % with a complete extinguishing system | Tabla K.3.3-2, K.3.3.3.1 | no (per-exit load not in the model) |
  | Occupant load factor (m² net per occupant) | A 28; C-1 10; C-2 3 (street level and below) / 6 (other floors); F 9; I-1 11; I-2 7; I-3 2; I-4 2.8; I-5 0.3; L-1 0.7; L-2 1.3; L-3 0.7; L-4 0.7; L-5 0.3; P 9; R 18; E, T by occupancy; M the largest of its parts | Tabla K.3.3-1 | set (`area_per_person_by_group`) |
  | Minimum exits by occupant load | 0–100: 1; 101–500: 2; 501–1 000: 3; > 1 000: 4 | K.3.4.2, Tabla K.3.4-1 | set (per level) |
  | Travel distance to an exit, without / with sprinklers (m) | A-1 60/75; A-2 90/120; C-1 60/90; C-2 60/75; F-1 60/75; F-2 90/120; I 45/60; L 60/75; P not allowed/22; R 60/75; +30 % if straight, no intermediate stairs, to exterior at grade | K.3.6.5, Tabla K.3.6-1 | no value (group-dependent and never computed) |
  | Travel inside a room of ≤ 6 people; dead-end corridors | ≤ 15 m; ≤ 6 m | K.3.6.3, K.3.5.1.3 | no measure |
  | Evacuation ramp: slope; width; landings; headroom | 1:12 (printed "8 %"); ≥ 1.10 m (exceptions 0.90–2.4 m); 1.8–3.6 m; ≥ 2.0 m | K.3.8.6.2, .4, .7, .5 | no (the 1:12 vs 8 % reading is ambiguous; not transcribed) |
- `co-retilap-iluminancia.json` — RETILAP as modified by Resolución 40150 de 2024,
  Libro 3, Tabla 3.2.2.6 a (maintained illuminance Ēm per space type), matched on
  Space names; plus a Room template with a declared illuminance parameter.

### `horizun_link_schedule` — 4D

`operation`: `import` (file only), `match` (read), `write` and `status_view`
(MutatingUnlessDryRun: dry run by default, `confirmation_token`, `idempotency_key`).

- Formats by extension: `.xml` MS Project MSPDI (summary tasks and UID 0 skipped;
  `Id` is the UID), `.csv`/`.txt` with header `id,name,start,finish[,wbs,percent_complete,actual_start,actual_finish]`
  (Spanish aliases accepted; `,` `;` or TAB), `.xer` Primavera P6 (TASK rows, WBS
  path from PROJWBS without the project node, WBS summary rows skipped). Dates must
  be ISO (`yyyy-MM-dd`…): `05/02/2026` is rejected by line, never guessed. A DTD in
  the XML is refused.
- `match`: `{parameter, key: id|wbs}` — candidates are the model elements that carry
  the parameter; or `{rules:[{activity, category, level?, parameter?, value?}]}`.
  An element whose key names two activities, or that two rules assign differently,
  is `ambiguous` and linked to neither. Reply lists links, elements without activity
  and activities without elements.
- `write`: `{activity_parameter, start_parameter?, finish_parameter?}` — TEXT INSTANCE
  parameters only; a missing, read-only, non-text parameter or a group member is an
  unresolved row. A refused `Set` rolls the batch back; every row is re-read.
- `status_view`: `view_id`, `as_of`. Duplicates the view (the source is untouched),
  detaches the copy from its template and colours each linked element: done green,
  in_progress amber, future grey, late red. `late` needs percent complete or actual
  dates; without them the status is the planned one. Each override is re-read.
  The token binds the schedule file's SHA-256: an edited file is a stale plan.

### `horizun_federation_check` — federation QA (read-only)

`rules`: `models:[{match, discipline?, allowed_categories?, forbidden_categories?}]`
(`match` is a regex on the model title, `$host` for the host; a model no rule claims
is `unclassified`, two rules make it `ambiguous`), `expected_links:[{name_matches,
count?=1, workset_matches?}]` (missing / fewer / duplicated / wrong workset, and
links no entry expected), `same_site` (default true). Same site is measured: three
points of each link are taken to shared coordinates through the link's own
project location and through the instance transform plus the host's; the largest
disagreement is compared with `tolerance_mm` (default 10). An unloaded link is
`not_decidable`. Link workset and phase are reported.

### Resumen (español)

- `horizun_code_check`: evalúa requirement-sets declarativos (parámetros y medidas
  geométricas). Las normas son datos en `standards/`; cada regla cita norma y
  numeral; lo no calculable o no verificado es `not_decidable`, nunca `passes`.
- `horizun_link_schedule`: 4D con MS Project XML, CSV o XER; empareja por parámetro
  o reglas sin elegir nunca entre dos actividades; escribe actividad/fechas y
  colorea una vista duplicada por estado, con ensayo, token y relectura.
- `horizun_federation_check`: categorías fuera de lugar por modelo, vínculos
  esperados/faltantes/duplicados, workset y fase, y coordenadas compartidas
  coherentes medidas en tres puntos.

## Spatial coherence after every write (`spatial_check`, `horizun_verify_changes`)

A typed write re-reads its postconditions: that proves the request was carried out,
not that the result makes sense. Measured in field use: a modelling session left a
column and a door in the same place with every postcondition true.

**Automatic.** Every call that leaves the model changed carries `spatial_check`, and
`attention` as the FIRST key of the reply when it found something. The dispatcher
watches `DocumentChanged` for the duration of the call (`Core/ChangeWatch.cs`), keeps
what the call added or modified after its own rollbacks, and checks those model
elements (`Core/SpatialCoherence.cs`): every element whose solid intersects theirs
(`ElementIntersectsElementFilter`), the shared volume (`BooleanOperationsUtils`), and
how the two relate. The rules (`Core/SpatialCoherenceRules.cs`, unit-tested) judge:

| Situation | Verdict |
|---|---|
| door or window sharing solid with a column, beam, another wall, MEP, furniture, stair | error — blocked opening |
| two elements of the same type occupying ≥95 % of the same volume | error — duplicate |
| duct/pipe/tray/conduit through a structural column, beam or foundation | error — clash |
| MEP against MEP without a connector between them | warning |
| same category overlapping without a join (walls, floors, columns) | warning |
| furniture, fixtures, equipment against structure or each other | warning |
| host and hosted, joined elements, MEP connected, curtain members, the structural frame (beam–column–slab), walls on slabs, MEP through walls/floors | expected, not a finding |
| shared volume below ~28 cm³ (touching faces) | nothing |

It never rolls anything back — the write already committed and verified what was
asked. It is bounded (800 elements, 8 s) and says `partial` when a bound stopped it.
Data-only tools (parameters, keynotes, worksets, materials, schedules, views…) are
skipped: `DocumentChanged` cannot tell a moved element from a renamed one. Links are
not examined (use `horizun_clash`). `HORIZUN_SPATIAL_CHECK=off` disables it for a
process, for a bulk import that checks once at the end.

**On demand.** `horizun_verify_changes` checks the elements the LAST Horizun write in
the active document changed (kept in memory since Revit started) or the `element_ids`
given, and returns the findings plus an IMAGE: a temporary isometric 3D view with a
section box around them — blue changed, red in an error, orange in a warning,
annotations hidden — created in a transaction group that is always rolled back
(`image.temporary_view_rollback = RolledBack`). Call it after a modelling batch and
look at the image: the check sees solids, not intent (a wrong level or room, or a
missing element, needs the picture).

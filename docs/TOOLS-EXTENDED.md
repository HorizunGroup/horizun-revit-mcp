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

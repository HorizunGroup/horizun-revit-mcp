# Extended tool operations

Detail that does not fit the `tools/list` budget. Each section belongs to one front;
the schema in `horizun://contract/tools` stays the source of truth for argument names.

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

# Extended tool notes

Detail that does not fit in a tool's advertised description. The contract itself is
served as `horizun://contract/tools`.

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

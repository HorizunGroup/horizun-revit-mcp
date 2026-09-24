# Extended tool notes

Detail that does not fit the budgeted tools/list descriptions. Each section is owned
by one tool family.

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

# Extended tool notes

Detail that does not fit in `tools/list`. Each section belongs to one area of the bridge.

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

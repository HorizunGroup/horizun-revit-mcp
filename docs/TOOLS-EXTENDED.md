# Extended tool notes

Detail that does not fit the `tools/list` budget. Each section belongs to one tool
or one family of operations; the advertised description stays short and points here.

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

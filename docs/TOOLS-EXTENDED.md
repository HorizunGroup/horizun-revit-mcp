# Extended tool notes

Detail that does not fit in the tools/list budget. Each section belongs to one
front; the advertised descriptions stay short and point here.

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
| `exit_count_minus_required` | per Level: placed rooms, doors | needs `config`: `area_per_person_m2` or `occupant_load_parameter`, `exit_door` (`{parameter, value}` or `{mark_prefix}`) and `required_exits: [{max_load, exits}]`; any missing piece is `not_decidable`. |
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
- `co-nsr10-titulo-k-evacuacion.json` — NSR-10 Título K, K.3: stairs, exit doors,
  exits per floor, travel distance. The published text could not be read when the set
  was written, so EVERY threshold is `unverified_value` and carries no number.
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

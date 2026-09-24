# Extended tool notes

Detail that does not fit in a tool's `tools/list` description. The contract
(`horizun://contract/tools`) remains the source of truth for arguments.

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

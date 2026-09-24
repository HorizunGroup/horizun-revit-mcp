# Information management (ISO 19650)

ISO 19650 is an international standard, not an organisation's standard, so this
bridge models its concepts directly — the appointment, the requirement and
planning documents, the common data environment (CDE) and its container states,
information container naming. What stays **project data**, never compiled in:
codes, field patterns, catalogues, paths and parameter names.

## Project context & intake

Every project can carry one `project-context.json` that answers, once, the
questions every later task would otherwise have to guess: who we are in the
appointment, which stage we are in, where the EIR/BEP/MIDP are, where the CDE
folders are and who approves each transition, how containers are named, which
classification is used, where the model sits on the earth, and what the IFC
delivery must look like.

### The schema

- File: [`schemas/project-context.v1.schema.json`](../schemas/project-context.v1.schema.json)
  — JSON Schema draft 2020-12, `$id` `https://horizunhub.com/schemas/project-context/v1`.
- Embedded in the server, so an installed bridge validates against exactly the
  schema it was built with. Also published as the MCP resource
  `horizun://schemas/project-context/v1` and by `horizun_project_context operation=schema`.
- Only `schema_version` (always `1`) and `project.code` are required. Everything
  else is optional **on purpose**: a field that is not known is left out and
  listed in `intake.missing`. Nothing is invented to make the file look complete.
- Dates are ISO 8601 (`YYYY-MM-DD`). No credentials or tokens ever belong in the
  file; `cde.project_ref` is an id or URL without secrets.

| Block | What it holds |
|---|---|
| `project` | code (the first field of every container name), name, client, location |
| `appointment` | role (`appointing_party`, `lead_appointed_party`, `appointed_party`), organisation (originator) code, task teams, stage and stage scheme |
| `documents` | OIR/PIR/AIR, EIR, BEP (`kind`: pre- or post-appointment), MIDP, TIDPs, responsibility matrix, LOIN, IDS, information protocol — each `{path, status, version, date}` with status `missing`/`draft`/`received`/`approved` |
| `deliverables` | the MIDP in machine-readable form (container name, task team, due date, required status, format) |
| `cde` | platform, project reference, synchronised root, one folder per state (`wip`, `shared`, `published`, `archived`), where work happens today, who approves each transition |
| `naming` | scheme, separator, ordered fields with patterns, status codes, revision patterns |
| `classification` | system, catalogue path, the type parameter that holds the code, optional instance override and leaf pattern |
| `georeference` | CRS, survey point, project base point, true north |
| `delivery` | IFC version, MVD, Pset mapping, IDS files, site placement; sheet format and naming |
| `software` | Revit year |
| `intake` | completion date, missing topics, assumptions, who confirmed |

### The tool: `horizun_project_context`

Host-resident: it answers inside the server and never touches Revit, so it works
before Revit is even open.

| Operation | Does |
|---|---|
| `schema` | Returns the embedded schema and its SHA-256. |
| `validate` | Reads a file and reports **one of four states**, kept apart because each calls for a different next step. |
| `questions` | Returns the ordered intake questions that are still open for a file (or all of them without one). |
| `draft` | Applies answers `{ "<JSON pointer>": value }` onto the existing file (or an empty context), validates, and returns the document. |

**The four states of `validate`:**

- `invalid` — the file breaks the schema. `errors` lists each violation with its
  JSON pointer and keyword. Fix these first.
- `inconsistent` — the file follows the schema but contradicts itself. `coherence`
  lists each finding by rule: a deliverable name with the wrong number of fields,
  a field that does not match its pattern, a project field that is not
  `project.code`, a required status that `naming.status_codes` does not declare, a
  task team that `appointment.task_teams` does not declare, something that looks
  like a credential. Warnings (not contradictions) are also listed: a BEP without
  its `kind`, a CDE with some state folders but not all, a document declared
  approved with no path, an `intake.missing` entry that is already answered.
- `incomplete` — valid and coherent, but intake questions are unanswered.
  `missing` lists them in intake order, each with the pointer it would fill and
  why it matters under ISO 19650.
- `complete` — valid, coherent, and every intake question has an answer. A
  document recorded as `status: missing` counts as answered (the answer is "it
  does not exist"), and is listed in `documents_declared_missing` as the finding
  it is.

**The intake order.** Project identity first (the schema needs `project.code`),
then: role in the appointment → stage → EIR (exists? where?) → BEP (pre/post,
exists?) → MIDP/TIDP → responsibility matrix → CDE (platform, root, the
WIP/Shared/Published/Archived folders, where work happens today, who approves
each transition) → naming → classification → LOIN/IDS → georeference/CRS → IFC
delivery (version, MVD, Pset mapping) → Revit version. Each question carries an
`id`, a `topic`, the target `pointer`, a `type`, its `options` when it has them
(enum questions offer exactly the schema's values), `text.es`/`text.en` and
`why.es`/`why.en`. Questions that do not apply are skipped, not asked: no
"where is the EIR?" once the EIR is recorded as missing.

**Writing.** `draft` rehearses by default (`dry_run: true`). With
`dry_run: false` it writes the file, then reads it back and compares bytes and
content before reporting it written. It:

- never replaces an existing file without `overwrite: true` — and builds on top
  of it, so a second intake round fills gaps instead of erasing the first;
- never writes an `invalid` context or one that carries a credential;
- never creates the project folder;
- derives `intake.missing` from what is still unanswered, unless the caller sets it;
- writes only under the `full_write` or `unsafe_code` permission profile. The
  tool is classified `ExternalSideEffectOnRequest`: every profile may read,
  validate and rehearse; the write itself asks the profile first. Under a
  stricter profile, the drafted `document` is returned for the person to save.

### The prompt: `project-intake`

"Start a BIM project the ISO 19650 way" (optional argument `path`). It guides the
agent through `questions` → ask the person in short blocks, with options and
reasons, never answering for them → `draft` with `dry_run: true` and a
confirmation → `draft` with `dry_run: false` → `validate`. The server
instructions tell a client to offer this prompt when work starts on a project
that has no `project-context.json`, or whose context is incomplete.

### Asking through the client

When the client declared MCP elicitation, `operation: "elicit"` lets the server
ask the same questions through the client's forms. See [Elicitation](#elicitation).

---

## Resumen en español

`project-context.json` es el archivo por proyecto que responde de una vez las
preguntas de gestión de información ISO 19650: rol en la designación, fase,
EIR/BEP/MIDP/TIDP, matriz de responsabilidades, CDE (plataforma, carpetas por
estado WIP/Compartido/Publicado/Archivado, dónde se trabaja hoy y quién aprueba
cada transición), nomenclatura, clasificación, LOIN/IDS, georreferenciación,
entrega IFC y versión de Revit. Su esquema es
`schemas/project-context.v1.schema.json` (también el recurso
`horizun://schemas/project-context/v1`). Solo `schema_version` y `project.code`
son obligatorios; lo desconocido se deja fuera y se lista en `intake.missing`,
nunca se inventa.

La herramienta `horizun_project_context` (sin Revit) tiene cuatro operaciones:
`schema`, `validate` (distingue **inválido**, **incoherente**, **incompleto** y
**completo**), `questions` (las preguntas de arranque pendientes, en orden, en
español e inglés, con opciones y su porqué) y `draft` (arma el contexto desde las
respuestas; ensaya por defecto, y al escribir relee el archivo, nunca sobrescribe
sin `overwrite: true` y nunca escribe un contexto inválido ni una credencial;
escribir exige el perfil `full_write`). El prompt `project-intake` guía al agente
por ese recorrido. Si el cliente declaró elicitation MCP, la operación `elicit`
hace las mismas preguntas mediante formularios del cliente (ver
[Elicitation](#elicitation)).

## Information containers & CDE

ISO 19650 is an international standard, so Horizun models its concepts directly:
the **information container**, its **suitability status**, its **revision** and the
four **CDE states** (`wip`, `shared`, `published`, `archived`). What stays an
argument is every concrete rule a project chooses: which fields make a name, their
order and patterns, the status codes, the revision patterns and the folders. Nothing
organisation-specific is compiled in.

Everything here works on **local or synced folders** (a desktop connector, a synced
document library, a plain folder). `horizun_information_container` never calls a cloud API; reading a CDE in the cloud is the separate, read-only `horizun_cde_cloud` (see Cloud CDE reader).

### The container object

```json
{
  "fields": { "project": "HZ01", "originator": "HRZ", "volume": "ZZ", "level": "XX",
              "type": "M3", "role": "A", "number": "0001" },
  "field_order": ["project", "originator", "volume", "level", "type", "role", "number"],
  "separator": "-",
  "field_patterns": { "project": "^[A-Z0-9]{2,6}$" },
  "status": "S2",
  "revision": "P01",
  "title": "General arrangement",
  "file_name": "name"
}
```

- **Name** = the fields in `field_order`, joined by `separator`:
  `HZ01-HRZ-ZZ-XX-M3-A-0001`.
- `field_order` may be omitted only when the fields are exactly the seven ISO 19650-2
  fields; the order of a name is never guessed.
- Patterns are full-match regular expressions. `field_patterns` is merged over the
  ISO 19650-2 defaults; `status_codes` (ordered: position is rank) and
  `revision_patterns` replace the defaults when given. Every reply says which rules
  were defaults and which were yours.
- Default status codes: `S0` (WIP), `S1`–`S4` (shared), `A1`, `B1`, `CR`
  (published). Default revisions: preliminary `P01` / `P01.01`, contractual `C01`.
  A preliminary revision with a published status (or the reverse) is a *warning*,
  never a refusal.
- `file_name`: `name` (default) names the file after the container;
  `name_status_revision` appends status and revision
  (`HZ01-HRZ-ZZ-XX-M3-A-0001-S2-P01`), useful when one folder must hold several
  revisions of the same container.
- Unknown keys are refused by name.

### The sidecar

`<file>.container.json` beside the file (`schema: horizun.container/v1`): name,
fields, naming (order, separator, file-name mode), status, revision, title, file,
bytes, SHA-256, `created_utc`, tool, source document, Revit year, and - after a
transition - `state`, `approved_by`, `transitioned_from` and `transition_id`.

It is written through a temporary file and a no-overwrite move, **read back
exactly**, and the file is **re-hashed** afterwards. An existing sidecar is never
overwritten by any tool.

### `horizun_information_container`

A host-resident tool (it answers with Revit closed).

| operation | what it does | writes |
|---|---|---|
| `name` | compose and validate; every problem listed | never |
| `stamp` | write the sidecar for an existing file whose name matches the container | only with `dry_run=false` |
| `verify` | file vs sidecar: `match`, `modified` (bytes changed after sealing), `renamed`, `name_inconsistent`, `missing_sidecar`, `sidecar_invalid` | never |
| `inspect` | walk the state folders; paginated findings | never |
| `transition` | copy a sealed container to the next state, rename, stamp, verify, log | only with `dry_run=false` |

**inspect** takes `root` + `states` (`{wip, shared, published, archived}`, absolute or
relative to `root`) or a `project_context_path` (a `project-context.json`,
`schema_version` 1, whose `cde`, `naming` and `deliverables` are read). It reports:
`name_noncompliant`, `missing_sidecar`, `orphan_sidecar`, `hash_mismatch`,
`sidecar_inconsistent`, `state_status_mismatch` (an S-status sitting in `published`,
for instance), `same_revision_different_content`, and `revision_incoherent` (the
newest published revision is below the newest shared one of the same kind; `P` and
`C` are never ordered against each other). With MIDP `deliverables` it adds
`deliverable_missing`, `deliverable_insufficient_state` and `deliverable_overdue`
(due before `as_of` and not at the required status); a pair of custom codes that
cannot be ordered is `deliverable_not_assessable`, never a pass. Archived copies do
not satisfy a deliverable. Findings are sorted and paginated (`offset`, `limit`,
`total_findings`, `next_offset`); a folder not read is `covered: false`, never an
empty one. `.horizun/`, `~$` lock files and `.tmp` files are ignored.

**transition** goes `wip -> shared -> published -> archived`, one step at a time:

1. the source must be inside the `from_state` folder and **match its sidecar** (stamp
   it first; an edited source is refused);
2. the new status must belong to the target state (`S*` shared, `A*`/`B*`/`CR`
   published); `approved_by` is **required** for `shared -> published`;
3. the file is copied to a temporary name, its SHA-256 compared with the source, then
   moved into place without overwriting; the sidecar is written and read back;
4. one JSON line is appended to `<root>/.horizun/cde-transitions.jsonl` and re-read.

It never moves, deletes or overwrites. The same transition sent again answers
`already_transitioned`; a different one onto an existing destination is refused.
`dry_run` defaults to `true`.

**Permission.** The tool is `ExternalSideEffectOnRequest`: every profile may call it,
and `stamp`/`transition` with `dry_run=false` require `full_write` (or
`unsafe_code`), checked per call.

### In `horizun_export`

`horizun_export` accepts an optional `information_container`. When present:

- it is validated **before anything is exported**; an invalid container refuses with
  every problem named and nothing written;
- the produced file takes the container's name, keeping the directory and extension of
  `output_path` (the reply shows both paths);
- after the export is verified, the sidecar is written beside the file and read back;
- `status` and `revision` are required; `image` export and PDF with
  `pdf_combine=false` are refused (one container is one file); an existing sidecar
  refuses the export whatever `overwrite` says.

Without the argument, `horizun_export` behaves exactly as before.

`horizun_pack_sheets` does not take a container: it arranges views on a sheet and
produces no file. Name the PDF of that sheet when you export it.

`horizun_manage_revisions` is unchanged: Revit derives a revision's number from its
numbering sequence (it cannot be written), and a Revit revision has no suitability
status field. Validate the code with `operation=name` and carry it in the container.

### Resumen en español

Horizun modela los conceptos de ISO 19650 (contenedor de información, estado de
idoneidad, revisión y los estados del CDE `wip`/`shared`/`published`/`archived`);
todo lo concreto (campos, orden, patrones, códigos, carpetas) llega como argumento o
desde `project-context.json`. Solo trabaja sobre carpetas locales o sincronizadas;
nunca llama APIs de nube.

- `horizun_information_container`: `name` compone y valida; `stamp` escribe el
  sidecar `<archivo>.container.json` (SHA-256, bytes, estado, revisión) y lo relee;
  `verify` detecta un archivo modificado después del sellado; `inspect` recorre las
  carpetas de estado y reporta nombres que no cumplen, archivos sin sidecar, sidecars
  huérfanos, hashes que no cuadran, revisiones incoherentes y, con el MIDP,
  entregables faltantes, vencidos o en estado insuficiente; `transition` **copia** (no
  mueve ni borra) al siguiente estado, renombra, sella, verifica por SHA-256 y registra
  en `.horizun/cde-transitions.jsonl`. `approved_by` es obligatorio para
  compartido→publicado. `stamp` y `transition` ensayan por defecto y escribir exige
  `full_write`.
- `horizun_export` acepta `information_container` opcional: se valida antes de
  exportar, el archivo toma el nombre del contenedor y se escribe y verifica el
  sidecar. Sin el argumento, nada cambia.
- `horizun_pack_sheets` no produce archivo y `horizun_manage_revisions` no puede
  escribir el número de revisión ni tiene campo de estado: ninguno de los dos recibe el
  contenedor.

## Transmittals and approval register

A transition moves information between CDE states; ISO 19650-2 (§5.6-5.7) also
needs a record of **what was issued to whom, for what purpose**, and of **what the
receiving party said about it**. Three more operations of
`horizun_information_container` keep that record beside the CDE, in
`<root>/.horizun/`. None of them moves, renames or deletes a container.

| operation | what it does | writes |
|---|---|---|
| `transmittal` | issue a numbered transmittal for sealed containers in one state | `transmittals/<id>.json` + `.md` + `.csv`, only with `dry_run=false` |
| `record_review` | record the receiving party's outcome for a transmittal or a container | one line in `reviews.jsonl`, only with `dry_run=false` |
| `register` | the approval register: history per container, with incoherences | never |

**transmittal** takes `root` + `states` (or a `project_context_path`), the `state`
the containers sit in (`shared`, `published` or `archived`; WIP is never issued),
`file_paths`, `sender` and `recipients` (`{name, organization, role}`, name and
organization required), `purpose` (a status code such as `S3`; its meaning comes
from the status codes in force - `naming`, the project context, else the ISO
19650-2 defaults - and an unknown code is refused), optional `note` and
`approved_by`.

- Every file must be inside the state folder and **match its sidecar now**: the
  SHA-256 is re-measured when the transmittal is prepared and again immediately
  before the record is written. One mismatch refuses the whole transmittal and
  nothing is written.
- The number is `<project>-TR-0001` (`project` or the context's `project.code`), one
  sequence per project in `transmittals/<project>.sequence.json`. The sequence file
  is opened with an **exclusive handle** for the whole issue, so concurrent calls
  queue and never share a number; the handle dies with the process, so a crash
  leaves no stale lock. The next number is also above every existing
  `<project>-TR-NNNN.*` file, so a number never reuses a file even if the sequence
  was lost. A corrupt sequence file refuses rather than guessing a number.
- `state=published` needs `approved_by`, unless every container's sidecar already
  carries the approval its transition recorded.
- The record `<id>.json` (`schema: horizun.transmittal/v1`) holds id, sequence,
  `issued_utc`, sender, recipients, purpose (code, description and where the
  description came from), state, and per container: name, title, status, revision,
  state, file, path relative to the root, bytes, SHA-256, `approved_by` and
  `transition_id`. `<id>.md` is a readable render; `<id>.csv` (UTF-8 with BOM, one
  row per container) opens in a spreadsheet, and a cell that would read as a formula
  is prefixed with `'`. All three are written without overwriting and read back;
  afterwards every container is re-hashed against the record (`postcheck`).
- A status that differs from the purpose is a warning (`status_differs_from_purpose`).
- The same containers (same bytes) issued again to the same recipients for the same
  purpose answer `already_issued` with the existing number. To issue them again on
  purpose, say why in `note`.
- The rehearsal (`dry_run=true`, the default) shows `next_id_preview`; the number is
  not reserved until the transmittal is issued.

**record_review** takes `outcome` (`accepted`, `accepted_with_comments`,
`rejected`), `reviewed_by` (required), `reviewer_organization`, `comments`
(required unless `accepted`), `reviewed_on` (YYYY-MM-DD, default today, never in the
future) and what was reviewed: `transmittal_id` (must exist; add `container` to
review one of its containers) or a `container` sealed in a declared state folder
(`revision` when there are several). One line (`schema: horizun.review/v1`) is
appended to `reviews.jsonl` and found again on re-read; the same review sent twice
answers `already_recorded`. **A rejection changes no state and moves no file**: it is
a record; the originator revises and issues again.

**register** (read-only) joins `cde-transitions.jsonl`, `transmittals/*.json` and
`reviews.jsonl` per container: `states_reached`, `last_transition_to`, `present_in`
(the state folders holding a sealed copy, when `states` are declared), `approvals`
(who approved each transition), `transmittals`, `latest_review` and the `history`
ordered in time. Filters: `container`, `state` (containers that reached it), `since`
/ `until` (event dates); `offset`/`limit` page over containers. It reports these
incoherences, most consequential first:

| kind | meaning |
|---|---|
| `transmittal_hash_changed` | an issued file's bytes changed after the transmittal (the recipients hold different bytes) |
| `transmittal_file_missing` | an issued file is no longer where it was issued from |
| `transmittal_path_outside_root` | a transmittal cites a path that does not resolve under the root |
| `publication_without_approval` | a `-> published` transition logged without `approved_by` (old or edited logs) |
| `review_unknown_transmittal` | a review cites a transmittal that does not exist |
| `review_unknown_container` | a review cites a container with no transition, transmittal or sealed copy |
| `review_container_not_in_transmittal` | a review cites a container its transmittal did not carry |
| `transmittal_unreadable`, `log_line_unreadable` | a record that could not be read, with its line number - never skipped silently |

`sources` says which records exist and how many were read: a register with no logs
yet is reported as such, never as an empty history.

**Permission.** As for `stamp` and `transition`: `transmittal` and `record_review`
with `dry_run=false` need `full_write` (or `unsafe_code`), checked per call;
rehearsals and `register` work at every profile.

### Resumen en español

Tres operaciones nuevas de `horizun_information_container` llevan el registro de
emisiones y aprobaciones de ISO 19650-2 (§5.6-5.7) en `<root>/.horizun/`, sin mover
ni borrar ningún contenedor:

- `transmittal` emite un transmittal numerado (`<proyecto>-TR-0001`, secuencia por
  proyecto bajo un bloqueo exclusivo, a prueba de concurrencia y sin reutilizar nunca
  un número) de contenedores sellados de un estado: de/para (nombre, organización,
  rol), propósito (código de estado y su significado), contenedores con nombre,
  título, estado, revisión, archivo, bytes y SHA-256 **re-medido** (si no coincide con
  el sidecar, se niega entero). Escribe `<id>.json`, `<id>.md` y `<id>.csv` sin
  sobrescribir y los relee. Lo publicado exige `approved_by` salvo que ya conste en
  los sidecars. Ensaya por defecto.
- `record_review` registra el resultado de revisión de la parte receptora
  (`accepted`, `accepted_with_comments`, `rejected`, con comentarios, quién y cuándo)
  en `reviews.jsonl`, solo añadiendo. Un rechazo no cambia estados ni mueve archivos.
- `register` (solo lectura, paginado) une transiciones, transmittals y revisiones por
  contenedor (cuándo pasó a cada estado, quién aprobó, en qué transmittal viajó, qué
  dijo la revisión), con filtros por contenedor, estado y fecha, y detecta
  incoherencias: archivo cambiado después de emitido, revisión de un contenedor o
  transmittal inexistente, publicación sin `approved_by` en logs viejos.

Escribir exige `full_write`, igual que `stamp` y `transition`.

## Verified IFC delivery

`horizun_deliver_ifc` turns "export an IFC and hope" into one call whose verdict
is the **file's**, not the model's. It runs, in order:

1. **precheck** *(optional, advisory)* — the `horizun_validate_ids` pre-check over
   the live model. It catches expensive problems before exporting, but a Revit
   parameter is not evidence of an IFC property set, so it **never** decides
   readiness.
2. **export** — the IFC is written with every option explicit: `ifc_version`
   (required, closed list), `ifc_filter_view_id`, `export_base_quantities`,
   `split_walls_and_columns`, `space_boundary_level`, the exporter's own
   user-defined property-set file (`pset_mapping_path`), the coordinate basis
   (`site_placement`: `shared`, `survey_point`, `project_base_point`,
   `internal`) and, optionally, `export_ifc_common_property_sets` /
   `export_internal_revit_property_sets`. The gate passes only when a new,
   non-empty file is measured at the exact planned path; it is hashed (SHA-256)
   from disk.
3. **schema_header** — the head of the file must be ISO-10303-21 with a
   `FILE_SCHEMA` of the family the requested version must produce (IFC2X3, IFC4
   or IFC4X3 — an IFC4X3 file is *not* accepted as IFC4), and the file must end
   with `END-ISO-10303-21` (a truncated write fails).
4. **ids_validate** — the IDS evaluated on the **exported file** (the same
   evaluator as `horizun_validate_ids operation=validate`). Any failing
   specification fails the gate; anything undecided makes it `not_decidable`,
   never `passed`.
5. **pset_mapping** — every property the mapping declares is looked for in the
   exported file on the IFC classes the mapping names (an occurrence is credited
   with its type's sets; a `...Type` class is checked on its own sets). The
   report gives coverage *n of m* per property and example GlobalIds of the
   entities that lack it. `pset_min_coverage` (default `1`) sets the share that
   must carry each property.
6. **bcf** *(optional, needs `ids_path`)* — one BCF 2.1 topic per failed
   specification, with the failing GlobalIds as the selection, written as
   `<name>.ids-issues.bcf` and re-read structurally (every markup and viewpoint
   re-parsed, topics and GlobalIds counted). No failure, no file: the gate is
   then `skipped`.

Every gate reports `passed`, `failed`, `skipped` or `not_decidable`.
`deliverable_ready` is `true` only when `export` and `schema_header` passed and
every other requested, non-advisory gate passed (or was skipped because there
was nothing to do). A file that is not ready stays where it was written, and
`blocking` names why.

`dry_run` defaults to `true`: the rehearsal returns the plan (effective options
and how each one reaches the exporter, the model's current georeference, the
IDS and mapping to be used with their SHA-256, output paths, the advisory
precheck) and writes nothing. The confirmation token binds the destination,
every option, the filter view, the **content** of the mapping and the IDS, and
which targets already exist.

### Georeference

The reply always carries `georeference_in_model` — survey point and project base
point (internal and shared, mm), the active location's east/west, north/south,
elevation and angle to true north, and the site location (latitude, longitude,
place, time zone, coordinate system id). After export it adds
`georeference_in_file`: the `IfcSite` placement and reference
latitude/longitude/elevation, any `IfcMapConversion` with its target CRS, and the
true-north direction the file declares. These are **observed, not judged** —
compare them with the model before sending the file.

### How each option reaches the exporter

| Option | Channel | How the delivery knows it held |
|---|---|---|
| `FileVersion`, `FilterViewId`, `ExportBaseQuantities`, `WallAndColumnSplitting`, `SpaceBoundaryLevel` | typed `IFCExportOptions` properties, compiled against Revit 2023–2027 | version: `schema_header`; the rest: requested, not provable from the file |
| `ExportUserDefinedPsets`, `ExportUserDefinedPsetsFileName` | `IFCExportOptions.AddOption`, read by name by the Revit IFC exporter | `pset_mapping` gate |
| `SitePlacement` (`Shared`, `Site`, `Project`, `Internal`) | `AddOption` | observed in `georeference_in_file` |
| `ExportIFCCommonPropertySets`, `ExportInternalRevitPropertySets` | `AddOption`, only when given | requested, not provable |

The named options follow the documented behaviour of the open-source Revit IFC
exporter; they are not readable back from the option object, which is why each
one is either checked in the file or labelled `requested_unverifiable`.

### The mapping file

The mapping **is** the exporter's user-defined property-set file — the same file
is handed to the exporter and then read back as the list of what the IFC must
carry. Fields are separated by **TAB**; a line written with spaces would export
no set at all, so it is refused by line number before anything is exported.

```text
# Generic delivery mapping - fields separated by TAB
#PropertySet:<TAB><Pset name><TAB>I|T<TAB><IFC classes, comma separated>
#<TAB><Property name><TAB><Data type><TAB>[Revit parameter, if different]
PropertySet:	Org_Identity	I	IfcWall,IfcSlab,IfcColumn,IfcBeam
	AssetCode	Text	Asset Code
	Zone	Label	Zone
PropertySet:	Org_TypeData	T	IfcWall,IfcSlab
	Manufacturer	Label
	FireRating	Label	Fire Rating
```

The exporter writes a property only when its Revit parameter has a value. A
property missing from an element therefore means an empty parameter **or** a
mapping the exporter did not apply; the file cannot tell those apart, and the
GlobalIds in the report are where to look.

### Naming

Give `output_name`, or an `information_container` (`fields`, `field_order`,
`separator`, `field_patterns`); the name is the fields joined in `field_order`.
If both are given they must agree. Only the name is used here.

### Resumen en español

`horizun_deliver_ifc` es una **entrega IFC verificada** en una sola llamada. El
veredicto es el del **archivo**: se exporta con opciones explícitas (versión,
vista de filtro, cantidades base, división de muros/columnas, límites de
espacio, archivo de property sets definido por el usuario y base de
coordenadas), se relee la cabecera (`FILE_SCHEMA` y cierre `END-ISO-10303-21`),
se valida el IDS **sobre el IFC exportado**, se busca en el archivo cada
propiedad del mapeo con cobertura *n de m* y GlobalIds de los faltantes, y
opcionalmente se escribe un BCF con un tema por especificación fallada, releído
estructuralmente. El pre-chequeo del modelo es solo orientativo y nunca decide.
`deliverable_ready` es `true` solo si pasan todos los gates solicitados. El
ensayo (`dry_run`, por defecto) devuelve el plan, la georreferencia actual del
modelo y no escribe nada. El archivo de mapeo es el mismo formato del
exportador de Revit, separado por **tabuladores**.

## Elicitation

MCP elicitation is the server asking the client to ask its user, in the middle of
a tool call. `horizun_project_context` uses it for the ISO 19650 intake:
`operation: "elicit"` sends the pending intake questions as short forms and
applies the answers exactly as `draft` does.

### What the specification says, and what this server does

Verified against modelcontextprotocol.io on 2026-09-24:

- **2025-06-18** introduced `elicitation/create` (server → client request) with
  `message` and a `requestedSchema` that is a flat object of primitives: string
  (formats `email`, `uri`, `date`, `date-time`), number/integer, boolean and enum
  (`enum` + `enumNames`). The client declares `"elicitation": {}` at
  initialize. The answer is `action: "accept" | "decline" | "cancel"`, with
  `content` on accept. Servers MUST NOT request sensitive information.
- **2025-11-25** adds modes: `"elicitation": { "form": {}, "url": {} }`, where an
  empty object means form only; requests carry `mode` (`"form"` may be omitted);
  titled enums are `oneOf` of `{const, title}`; multi-select enums are arrays;
  URL mode (`url`, `elicitationId`, error `-32042`) is for sensitive
  interactions. Servers MUST NOT send a mode the client did not declare.
- **2026-07-28** no longer sends elicitation as a request. Verified against the
  specification source (`modelcontextprotocol/modelcontextprotocol`,
  `docs/specification/2026-07-28/basic/patterns/mrtr.mdx`, `client/elicitation.mdx`,
  `changelog.mdx`, SEP-2322 and SEP-2577) on 2026-09-24:
  - *Multi round-trip requests* (SEP-2322): servers "MUST send server-to-client
    requests (such as `roots/list`, `sampling/createMessage`, or
    `elicitation/create`) using the MRTR pattern". The tool answers with an
    `InputRequiredResult` — `resultType: "input_required"`, an `inputRequests` map
    (server-chosen keys → `{method, params}`) and an opaque `requestState` — and the
    client calls again with a new JSON-RPC id, `params.inputResponses` (same keys →
    the `ElicitResult`) and `params.requestState` echoed unchanged. Only
    `tools/call`, `prompts/get` and `resources/read` may return it.
  - `requestState` "MUST" be treated as attacker-controlled; when it influences
    business logic its integrity MUST be protected (HMAC or AEAD) and failing state
    rejected; servers SHOULD bind it to the principal, a short expiry and the
    originating request, and MUST enforce single use server-side when a state may
    be consumed only once.
  - The capability is per request, in
    `_meta["io.modelcontextprotocol/clientCapabilities"].elicitation`, with the same
    modes as 2025-11-25 (empty object = form only). A retry that lacks information
    the server still needs SHOULD get a new `InputRequiredResult`, not an error;
    malformed `inputResponses` SHOULD be a JSON-RPC error.
  - The version is not negotiated: there is no `initialize`; each request names
    `io.modelcontextprotocol/protocolVersion` in `_meta`, and `server/discover`
    lists the versions a server implements.

This server implements **form mode** in every revision that has elicitation:
through `elicitation/create` requests for sessions negotiated at 2025-06-18 or
2025-11-25 via `initialize`, and through `InputRequiredResult` rounds for
2026-07-28 requests (below). It never uses URL mode (nothing in the intake is
sensitive; a credential in an answer is refused by `draft` anyway). Enums are sent
as `oneOf` const/title under 2025-11-25 and 2026-07-28 and as `enum` + `enumNames`
under 2025-06-18; no field is `required`, so a person can always leave an unknown
empty.

### 2026-07-28: rounds instead of requests

The same intake, the same forms, the same answer rules — but nothing waits for
the client:

1. `tools/call` `horizun_project_context` `{operation: "elicit", ...}` → a result
   with `resultType: "input_required"`, one entry in `inputRequests`
   (`intake_form_<n>`: `elicitation/create`, `mode: "form"`, the block's
   `requestedSchema`) and a `requestState`. No `content`, no cache hints.
2. The client asks the person and calls again with **the same `arguments`**, plus
   `inputResponses: {"intake_form_<n>": {action, content}}` and the
   `requestState`. The answer is applied exactly as in the legacy path; if a block
   is left, the reply is the next `InputRequiredResult`.
3. When no block is left, or the person cancels, the reply is the ordinary
   `resultType: "complete"` result: `rounds`, `answers`, `unanswered`, `draft`.
   `dry_run` still defaults to true; with `dry_run: false` the file is written
   only at this last step, through `draft`, and re-read. Nothing is written
   between rounds.

A retry without the pending key in `inputResponses` gets the same form and the
same state again (nothing consumed). `decline` leaves that block open and moves to
the next; `cancel` ends the intake. There is no per-form wait, so
`timeout_seconds` (default 300, 10–540) is the life of each `requestState`.

**The state is sealed and trusted for nothing the client could change.** It is
`payload.HMAC-SHA256` under a 256-bit key generated per server process and never
stored. The payload carries the tool name, a SHA-256 of the canonicalised
`arguments`, the request's `clientInfo.name`, the answers so far, the questions
asked and the form in flight, an expiry and a nonce. A retry is refused with
`code: "request_state_rejected"` (nothing applied, nothing written) and a
`reason`:

| reason | when |
|---|---|
| `malformed` | not a state this server format produces |
| `tampered` | the MAC does not verify — altered, or issued by an earlier server process (a server restart ends every open intake) |
| `mismatch` | issued for other arguments: another `path`, `dry_run` flipped, other `answers` — a state can never carry answers to a file the person was not asked about |
| `principal_mismatch` | issued to a client with another `clientInfo.name` |
| `expired` | older than `timeout_seconds`; the answers it held come back in `answers` so they are not lost, and are not acted on |
| `replayed` | already used: each state is consumed once, when its answer is applied (a bounded in-memory set, pruned by expiry) |

The payload is signed, not encrypted: it holds what the person typed and nothing
the client did not already see. A malformed `inputResponses` (not an object) or
`requestState` (not a string) is `-32602`. A task-augmented call cannot elicit
(`task_augmented_call`), nor can the tool when it runs inside another tool such as
a procedure step (`nested_call`): the round belongs to the `tools/call` the client
sent.

### Roots, sampling and logging under 2026-07-28

SEP-2577 deprecates roots, sampling and logging from 2026-07-28: they "remain
fully functional", capability negotiation is unchanged, and implementations
SHOULD warn when a deprecated feature is negotiated. This server:

- never sends `roots/list` or `sampling/createMessage` in any revision, and does
  not read the client's `roots`/`sampling` capabilities;
- keeps logging as it was in the legacy revisions (`logging/setLevel`, capability
  `logging`), untouched;
- under 2026-07-28 emits `notifications/message` only for a request that names
  `_meta["io.modelcontextprotocol/logLevel"]`, on that request's own response
  stream. Because "servers that emit log message notifications MUST declare the
  logging capability" (server/utilities/logging), `server/discover` now declares
  `logging: {}` in the modern block too — it previously omitted it while still
  emitting. An unknown level is refused with `-32602`, as that page asks. The
  first such request writes a one-time deprecation warning to the server log
  (never to the wire).

### Is 2026-07-28 accepted?

Yes, and it was before this change: `Protocol/McpRevision.cs` lists it,
`server/discover` advertises it, and a request declaring it in `_meta` is served
statelessly (`RequestEnvelope`, `ResultEnvelope`, `SubscriptionStream`,
`ModernTasks`). `ProtocolNegotiation` (the `initialize` answer) deliberately does
**not** offer it: that revision has no `initialize`. What was missing for this
feature was only the MRTR half of elicitation, which previously answered
`elicitation_unsupported` / `input_required_result_not_implemented`; that reason
no longer exists. Open points: `prompts/get` and `resources/read` never return
`InputRequiredResult` (nothing there needs input), and no 2026-07-28 client was
available to verify against — the proof is the stdio tests.

### When it refuses

If the client did not declare `elicitation` (or only `url`) — at `initialize`, or
in a 2026-07-28 request's own `_meta` — the negotiated revision predates
2025-06-18, the call was sent as a task, or the tool runs inside another tool,
nothing is sent and the call fails with structured content
`code: "elicitation_unsupported"`, a `reason` (`client_did_not_declare`,
`form_mode_not_declared`, `protocol_version`, `task_augmented_call`,
`nested_call`) and `fallback: "ask_in_chat"`. The agent then asks in the chat: `questions` lists
every pending question with its options, `draft` applies the answers. The
`project-intake` prompt and the server instructions say exactly this.

### How `elicit` asks

- One form per thematic block (project, appointment, stage, EIR, BEP, MIDP/TIDP,
  responsibility matrix, CDE, naming, classification, LOIN/IDS, georeference,
  IFC, software), at most 6 fields, in `language` `es` or `en` (default `en`).
- A question that depends on another in the same block waits for it: "where is
  the EIR?" is asked in a follow-up form only when the EIR exists.
- Lists and tables (task teams, TIDPs, naming fields, status codes, IDS, survey
  point) are not flat primitives and are never squeezed into a form: they come
  back as `not_elicitable`, for the chat.
- **accept** applies each valid field; a value outside a question's options is
  rejected (`not_an_option`), never coerced; an empty field is `left_blank`.
  **decline** leaves that block open and moves to the next topic. **cancel**
  ends the intake. No answer within `timeout_seconds` (default 300, 10–540) is
  `timed_out` and ends it too. The whole call stays under 540 s so it answers
  before the host-tool deadline.
- The result carries `rounds` (what was asked and what came back), `answers`
  (`{pointer: value}`), `unanswered` (every open question with its reason) and
  `draft` (the same payload `draft` returns). `dry_run` defaults to true; with
  `dry_run: false` a write that would be refused (existing file without
  `overwrite`, missing folder, profile below `full_write`) is refused **before
  the first form**, and a successful write is re-read before it is reported.
  Passing an earlier `answers` back continues where a call stopped without
  asking those questions again.

### The transport underneath

Server → client requests use string ids `horizun-server-<n>`, which a client
never sends. The reader recognises a response by its shape (no `method`; a
`result` or `error`) before the session's request-id rule, hands it to the
waiting request and moves on — the tool waits on its own thread, so the reader
keeps answering other requests while a form is open. Each request ends on its
answer, its timeout, the tool call's cancellation, or the channel closing (stdin
EOF or a lost stdout fails every pending request at once, so shutdown is not held
by an open form). When the server stops waiting it sends
`notifications/cancelled` for its own request. At most 8 server → client
requests wait at once.

### Clients

Claude Code documents support for MCP elicitation ("Respond to MCP elicitation
requests", code.claude.com/docs/en/mcp, read 2026-09-24). No other client was
verified for this change; the capability a client declares at `initialize` is
what decides, never its name.

### Resumen en español

La elicitation MCP permite que el servidor le pida al cliente que pregunte a su
usuario durante una llamada. `horizun_project_context` con `operation: "elicit"`
envía las preguntas pendientes del arranque ISO 19650 como formularios cortos
(uno por bloque temático, máximo 6 campos, en `es` o `en`) y aplica las
respuestas igual que `draft`: ensaya por defecto, y con `dry_run: false` escribe
y relee; lo que no podría escribirse se rechaza antes del primer formulario. Solo
se usa el modo formulario, y solo si el cliente declaró `elicitation` en
`initialize` con la revisión 2025-06-18 o 2025-11-25; si no, la llamada falla con
`code: "elicitation_unsupported"` y el agente pregunta en el chat. Aceptar aplica
cada campo válido (un valor fuera de las opciones se rechaza, nunca se adivina);
rechazar (`decline`) deja ese bloque abierto y sigue con el siguiente; cancelar
o agotar el tiempo termina el arranque. Las listas y tablas no caben en un
formulario y vuelven como `not_elicitable`. El resultado lista todo lo que quedó
sin responder y por qué. Por debajo, las peticiones servidor→cliente usan ids
`horizun-server-<n>`, el lector entrega cada respuesta sin bloquearse y cada
espera termina por respuesta, tiempo, cancelación o cierre del canal.

**Revisión 2026-07-28 (MRTR, SEP-2322).** Ahí no hay petición servidor→cliente:
la herramienta devuelve un `InputRequiredResult` (`resultType: "input_required"`,
un formulario en `inputRequests` y un `requestState`) y el cliente vuelve a
llamar con los mismos `arguments`, `inputResponses` y el `requestState` intacto.
Cada ronda aplica sus respuestas como siempre; al terminar (o al cancelar) llega
el resultado normal, con `dry_run` por defecto y, si se escribe, relectura. El
estado va firmado con HMAC-SHA256 (clave por proceso), atado a la herramienta, a
los argumentos (otra `path` o `dry_run` → `mismatch`), al `clientInfo.name`,
caduca con `timeout_seconds` y se consume una sola vez; cualquier fallo devuelve
`request_state_rejected` sin aplicar ni escribir nada. La capacidad se lee de
`_meta` de cada petición. Roots y sampling no se usan; logging sigue igual en
2025-*, y en 2026-07-28 (obsoleto por SEP-2577 pero vigente) se declara
`logging` en `server/discover` porque el servidor emite registros cuando la
petición pide `logLevel`, y un nivel desconocido da `-32602`.

## Cloud CDE reader

`horizun_cde_cloud` reads a CDE **in the cloud**, where `horizun_information_container`
reads local or synced folders. It is host-resident (it answers with Revit closed) and
**read-only**: it never uploads, moves, renames, approves or deletes anything in the
cloud. It is classified `ReadOnly` with `openWorldHint: true`.

| provider | what it talks to |
|---|---|
| `acc` | Autodesk Construction Cloud / BIM 360 Docs through the APS Data Management API: `GET /project/v1/hubs` → `/hubs/{hub}/projects/{project}` → `/topFolders` → `GET /data/v1/projects/{project}/folders/{folder}/contents` → items and their tip versions → `GET /data/v1/projects/{project}/items/{item}/versions`. Scope `data:read`. |
| `opencde` | a buildingSMART OpenCDE server: Foundation API discovery (`GET /foundation/versions`, `GET /foundation/{version}/auth`) and the Documents API 1.0 query surface (`POST /document-versions`, the server-provided `document_versions` link). |

| operation | acc | opencde |
|---|---|---|
| `list_states` | maps cloud folders to `wip`/`shared`/`published`/`archived` from `cde.states` (paths such as `Project Files/01_WIP`, from the top folder). A segment matches only an **exact** folder name; a case-only difference is named and not taken. Folders listed on the way that are neither a state folder nor on the path to one come back as `unmapped_folders`. A local path (`C:\...`) in `cde.states` is reported as not a cloud folder. | discovery only: the Documents API has no folders, so `states_mappable: false`. |
| `inspect` | walks each state folder (subfolders up to depth 8, at most 5,000 files; the first 500 are listed in `files`): per file the path, item id, tip version number, last modified, size and file type; the name checked against the project's naming; status and revision read from the **name** (`file_name: name_status_revision`); `state_status_mismatch` when the name's status belongs to another state; and the MIDP cross. | reads the given `document_ids` only (the latest version of each), checks names and crosses the MIDP; every document has `state: null`. |
| `versions` | `item_id` → every version, newest first. | `document_id` → its latest version, then the server's `document_versions` link. |

**One core, two readers.** Name compliance, the cross-state revision rule, the MIDP
cross (`deliverable_missing`, `deliverable_insufficient_state`, `deliverable_overdue`,
`deliverable_not_assessable`) and the sorted, paginated findings come from
`ContainerInspection`, the same code the local `inspect` of
`horizun_information_container` now uses. The APS API exposes no content hash, so the
SHA-256 and sidecar rules of the local reader do not apply in the cloud.

**Coverage is the contract.** Every reply carries `coverage_complete`, per-state
`coverage` with the `gaps` that were not read, and an `http` block (`calls`,
`retries`, `budget`, `budget_exhausted`, `errors` with path and status — never a
response body). Anything unread makes `coverage_complete: false`: a 401/403 (not
retried), a spent `max_calls` budget (default 300, max 5000; retries and the token
request count), the depth or file limit, a state folder not found. A deliverable
judged `missing` then carries a `caveat`: it may sit in the part that was not read.
`429` and `502`/`503`/`504` are retried up to 4 times, honouring `Retry-After`,
otherwise after 1, 2, 4 and 8 s (cap 30 s). Pagination follows `links.next`. OpenCDE
`inspect` is `coverage_complete: false` by construction.

**Credentials** never travel in arguments or in `project-context.json` (an argument
such as `client_secret` is refused as unknown). With nothing configured the call is
refused **before any request**.

- `acc`, in this order: `HORIZUN_APS_ACCESS_TOKEN`; the 3-legged token file
  `%USERPROFILE%\.horizun\aps-token.json` (`access_token`, `refresh_token`,
  `expires_at`), refreshed when it has expired and `HORIZUN_APS_CLIENT_ID` is set —
  APS replaces the refresh token when it is used, so the new pair is written back
  through a temporary file and read back; 2-legged `HORIZUN_APS_CLIENT_ID` +
  `HORIZUN_APS_CLIENT_SECRET` (`APS_CLIENT_ID` / `APS_CLIENT_SECRET` are also read).
  Token endpoint `POST https://developer.api.autodesk.com/authentication/v2/token`,
  with the client credential in a Basic header. A 2-legged token sees only what the
  APS app is provisioned for in the ACC account (a custom integration).
- `opencde`: `HORIZUN_OPENCDE_ACCESS_TOKEN`, an OAuth2 token issued by the server's
  `oauth2_token_url`. This tool does not run the interactive authorization-code flow.
- Tokens are sent only to `developer.api.autodesk.com`, or to the named OpenCDE
  server and the Documents API base it advertised. A pagination or document link to
  any other host is refused before the request.

**Limits, stated.** The OpenCDE Documents API 1.0 cannot enumerate a project: document
selection happens in the CDE's own web UI (`POST /select-documents` returns a browser
URL), and the API carries no CDE state. So `opencde` reads documents by id and never
maps states. The published schema is kept deliberately terse (tools/list has a byte
budget): arguments are `operation`, `provider`, `project_context_path`, `hub_id`,
`project_id`, `states`, `naming`, `deliverables`, `as_of`, `offset`, `limit`,
`max_calls`, `item_id`, `server_url`, `document_ids` and `document_id`. In ACC, status and revision come from file names, not from ACC review or
approval workflows.

References: the APS OpenAPI descriptions
(<https://github.com/autodesk-platform-services/aps-sdk-openapi>, `datamanagement` and
`authentication`), the APS documentation (<https://aps.autodesk.com/en/docs/data/v2/>,
<https://aps.autodesk.com/en/docs/oauth/v2/>), the buildingSMART OpenCDE Documents API
release 1.0 (<https://github.com/buildingSMART/documents-API>) and the Foundation API
release 1.1 (<https://github.com/buildingSMART/foundation-API>).

### Resumen en español

`horizun_cde_cloud` lee un CDE **en la nube** en solo lectura: nunca sube, mueve,
renombra, aprueba ni borra. `provider=acc` (ACC/BIM 360 Docs por la API Data
Management de APS, alcance `data:read`) u `opencde` (buildingSMART OpenCDE:
descubrimiento Foundation y Documents API 1.0). `list_states` asigna las carpetas de
la nube a los cuatro estados ISO 19650 según `cde.states` (`Project Files/01_WIP`),
solo por nombre **exacto**, y reporta las carpetas sin asignar; `inspect` lista por
estado archivos, versión, fecha y tamaño, valida el nombre y cruza el MIDP (faltantes,
vencidos, en estado insuficiente) con el **mismo núcleo** que el `inspect` local;
`versions` da el historial de un ítem. Paginación, tope de llamadas, reintentos con
backoff ante 429 y `coverage_complete=false` ante cualquier cosa no leída (nunca
"vacío" por error). Credenciales solo desde variables de entorno del servidor o el
archivo de token 3-legged; sin credenciales, se niega sin hacer una sola llamada.
OpenCDE no permite listar un proyecto sin el flujo interactivo del navegador: lee
documentos por id y no asigna estados.

## Live verification of the ISO 19650 tools

`scripts/verify-live.ps1` measures these tools against a running Revit, the way
every other tool that touches Revit is measured. The probes live in
`scripts/live-iso19650.probes.ps1` and are exercised **without Revit** by
`scripts/live-iso19650.tests.ps1` (a fake bridge that answers in the real
shapes and simulates each defect the probes exist to catch, plus the glue in
`verify-live.ps1` executed from its own text).

| Case | Tool | What must hold |
|---|---|---|
| ISO-D1 | `horizun_deliver_ifc` | dry run: plan, `georeference_in_model`, token, mapping and IDS bound by SHA-256; the output folder is unchanged |
| ISO-D2 | `horizun_deliver_ifc` | apply: `export`, `schema_header` (FILE_SCHEMA family IFC4) and `information_container` passed; `horizun_information_container verify` = `match` with the delivered SHA-256 |
| ISO-D3 | `horizun_deliver_ifc` | `ids_validate` and `pset_mapping` are decided on the file (passed or failed, never undecided), `bcf` follows the IDS, and `deliverable_ready` equals what the gates imply |
| ISO-D4 | `horizun_deliver_ifc` | a second apply onto the sealed name, even with `overwrite=true`, is refused before export; the IFC, its sidecar and the folder are unchanged |
| ISO-D5 | `horizun_deliver_ifc` | IFC4x3 is planned on Revit 2024+ and refused by name on 2023 |
| ISO-D6 *(opt-in)* | `horizun_deliver_ifc` | `-IsoReadyProbe`: after a verified write of the code to every element of the class, the same delivery is `deliverable_ready=true` |
| ISO-E1 / E2 | `horizun_export` | IFC export with a container takes the container's name and is sealed (`match`); a repeat onto the sealed name is refused whatever `overwrite` says |
| ISO-E3 | `horizun_export` | a real (non-placeholder) sheet prints as one sealed PDF; NOT COVERED when the model has none |
| ISO-H1…H3 | `horizun_project_context` | `validate` keeps invalid / inconsistent / incomplete apart; `questions` is ordered and bilingual; a `draft` rehearsal writes no file |
| ISO-H4…H7 | `horizun_information_container` | `inspect` of a temporary CDE finds the bad name, the missing sidecar, the missing and the overdue deliverable and writes nothing; shared→published without `approved_by` is refused; with it, the rehearsal passes on a sealed container; the apply lands a verified, logged copy that replays as `already_transitioned` under `full_write`, or is refused by the profile with nothing written |

Nothing is assumed about the fixture. The delivery class is **discovered** by
counting elements (ducts → IfcDuctSegment first, then pipes, cable trays,
floors, columns, framing, walls, generic models; `-QuantityCategory` goes first
when it is one of them), and the TAB mapping (`HZ_Delivery.Code` from
`Comments`) and the IDS are generated for that class in a folder per run. The
delivery uses IFC4 in every year; IFC4x3 is its own case. The section writes no
parameter unless `-IsoReadyProbe` is passed; `-IsoMappedParameter` names the
Revit parameter the mapping reads (display name, `Comments` under the ENU
runner).

The write cases need `-WriteProbes` and the disposable `WriteDocument`; without
them they are NOT COVERED by name. The host-resident cases run in every run; H6
and H7 need a sealed container, which comes from ISO-E1 or, under `full_write`,
from a host-side `stamp`. Temporary files go into one folder per run under the
harness scratch directory (never the repository) and are removed only when every
ISO case passed. Besides the main report (`iso19650` block and one probe row per
case), the run writes `iso19650-<year>-<run>.json` beside `-Json`, a
`horizun.live-evidence/2` record that `scripts/consolidate-live-session.py`
consumes (case = probe × harness × year × document).

### Resumen en español

`verify-live.ps1` mide las herramientas ISO 19650 contra un Revit real con las
sondas de `scripts/live-iso19650.probes.ps1`, que `live-iso19650.tests.ps1`
ejercita sin Revit. `deliver_ifc`: ensayo sin archivos con plan, georreferencia y
token; aplicación con un mapeo TAB y un IDS generados por corrida para la clase
que el modelo **tiene** (se descubre contando elementos), con `export`,
`schema_header` e `information_container` aprobados y `verify = match`; IDS y
mapeo decididos sobre el archivo y `deliverable_ready` coherente con los gates;
rechazo de un segundo sellado; IFC4x3 planificado en 2024+ y rechazado por nombre
en 2023. `export` con contenedor (IFC y PDF si hay lámina real) y rechazo de la
repetición. Sin Revit: `project_context` validate/questions/draft (ensayo) y
`information_container` inspect/transition sobre un CDE temporal con la regla de
`approved_by`. La sonda no escribe parámetros salvo con `-IsoReadyProbe`. Los
archivos temporales van a una carpeta por corrida fuera del repositorio, y cada
corrida deja además un registro `horizun.live-evidence/2` para el consolidador.

## Worked examples

[`examples/`](../examples/) holds copyable tool calls for everything on this page:
the start-up (`iso19650-startup/`: `questions` → `draft` rehearsal → `draft` write →
`validate`, plus a complete `project-context.example.json`), containers
(`information-containers/`: `name` → `stamp` → `inspect` → `transition`), and a
verified IFC delivery (`ifc-delivery/`) whose TAB-separated Pset mapping and IDS 1.0
file agree: `HZ_Delivery.Code` on every `IfcDuctSegment`. Each file carries `tool`,
a bilingual `title`/`about` and the exact `arguments`.

They cannot drift from the contract: `ExamplePayloadTests` validates every
`examples/**/*.json` against the `InputSchema` of the tool it declares, rehearses the
project-context drafts and composes every container name, and
`ExampleDeliveryFilesTests` runs the example IDS and mapping against a hand-written
IFC. Names, codes and paths are placeholders under `C:/proyectos/demo/`; element ids
are placeholders to resolve in your own model.

### Resumen en español

[`examples/`](../examples/) trae llamadas copiables para todo lo de esta página: el
arranque (`questions` → ensayo de `draft` → escritura → `validate`, y un
`project-context.example.json` completo), los contenedores (`name` → `stamp` →
`inspect` → `transition`) y una entrega IFC verificada cuyo mapeo de Psets (separado
por TAB) y cuyo IDS 1.0 coinciden: `HZ_Delivery.Code` en cada `IfcDuctSegment`. Un
test valida cada archivo contra el `InputSchema` de la herramienta que declara, así
que no se desactualizan; nombres, códigos, rutas e ids son marcadores.

## LOIN to IDS

A project context may carry a **structured level of information need** in the
optional `loin` block (`schema_version` stays `1`; the block is additive, and
`documents.loin` still points at the LOIN document itself). The concepts are
those of **ISO 7817-1:2024** *Building information modelling — Level of
information need — Part 1: Concepts and principles*, which superseded
**EN 17412-1:2020**
([ISO catalogue](https://www.iso.org/standard/82914.html);
[openBIM knowledge base, LOIN step](https://openbim-knowledgebase.org/en/docs/level-of-information-need-basics/chapter-9-step-2-level-of-information-need/)):

- **prerequisites** — the *purpose* of the information, the *information delivery
  milestone*, and the *actors* who provide and receive it;
- **geometrical information** — *detail*, *dimensionality* (0D point, 1D line,
  2D surface, 3D volume), *location* (absolute, or relative to another object),
  *appearance* and *parametric behaviour*;
- **alphanumerical information** — how the object is *identified* and which
  *information content* it carries;
- **documentation** — documents that must accompany the objects.

The standard leaves the object breakdown open; here `applies_to` names an IFC
entity (and predefined type), a classification (system + code, optional bSDD URI)
and/or a Revit category.

```json
"loin": {
  "source": "EIR rev 2",
  "requirements": [{
    "id": "W-01", "purpose": "Fire safety review", "milestone": "Stage 4",
    "actors": { "provider": "Architect", "receiver": "Fire engineer" },
    "applies_to": { "ifc_entity": "IfcWall", "revit_category": "OST_Walls" },
    "occurrence": "optional",
    "ifc_versions": ["IFC4"],
    "geometry": { "detail": "simplified envelope", "dimensionality": "3D", "location": "absolute" },
    "alphanumeric": {
      "attributes": [{ "name": "Name", "pattern": "W-[0-9]{3}" }],
      "properties": [
        { "property_set": "Pset_WallCommon", "name": "FireRating", "data_type": "IfcLabel",
          "allowed_values": ["EI60", "EI90"] },
        { "property_set": "Qto_WallBaseQuantities", "name": "Width", "data_type": "IfcLengthMeasure",
          "min_inclusive": 0.1, "max_inclusive": 0.5, "unit": "m" }
      ]
    },
    "documentation": [{ "name": "Fire test certificate", "format": "pdf" }]
  }]
}
```

### `validate` and the loin block

The schema checks the shape (enums for dimensionality, location, occurrence,
cardinality and IFC versions; `data_type` letters only; patterns must compile).
Coherence adds rules, reported like every other finding:

| Rule | Severity | Meaning |
|---|---|---|
| `loin_duplicate_requirement_id` | error | Two requirements share an id (it becomes the IDS specification identifier). |
| `loin_property_type_conflict` | error | The same `property_set` + `name` is declared with two data types, anywhere in the block. |
| `loin_unknown_ifc_entity` | error | `ifc_entity` is not an entity of the schema(s) the requirement targets (its `ifc_versions`, else `delivery.ifc.version`, else IFC4 **and** IFC4X3_ADD2). The message says where the name does exist. |
| `loin_entity_version_specific` | warning | No version declared and the entity exists in only one of IFC4 / IFC4X3_ADD2. |
| `loin_bounds_conflict`, `loin_bounds_inverted`, `loin_bounds_on_non_numeric` | error | Bounds that cannot be written or cannot be satisfied. |
| `loin_unit_not_ids_default` | warning | Bounds in a unit other than the IFC default (SI); they will not be translated. |
| `loin_pattern_anchor` | warning | An XML Schema pattern always matches the whole value; `^` and `$` are literal characters there. |
| `loin_property_repeated`, `loin_applies_to_empty` | warning | Redundant or unanchored requirements. |

The entity lists are embedded (`schemas/ids/ifc-entities.txt`): 653 names for
IFC2X3 and 776 for IFC4 (the `ENTITY` declarations of buildingSMART's
`IFC2X3_TC1.exp` and `IFC4_ADD2_TC1.exp`) and 876 for IFC4X3_ADD2.

### `ids_from_loin`

`horizun_project_context operation=ids_from_loin path=<project-context.json>`
translates the alphanumerical part into an **IDS 1.0** file (namespace
`http://standards.buildingsmart.org/IDS`):

| LOIN | IDS |
|---|---|
| requirement | one `<specification>`: `identifier` = id, `name` = id + purpose, `description` = purpose, milestone and actors as text, `ifcVersion` from `ifc_versions` or `delivery.ifc.version` (IFC2X3 / IFC4 / IFC4X3_ADD2) |
| `applies_to.ifc_entity` / `predefined_type` | applicability `<entity>` (upper case) |
| `applies_to.classification` | applicability `<classification>` (system, value) |
| `occurrence` | applicability `minOccurs`/`maxOccurs`: required 1..unbounded, optional 0..unbounded (default), prohibited 0..0 |
| `alphanumeric.attributes` | requirement `<attribute>` with cardinality and value |
| `alphanumeric.properties` | requirement `<property>` with `dataType` (upper case), cardinality, `uri`, and a value: one allowed value → `simpleValue`; several → `xs:enumeration`; `pattern` → `xs:pattern`; bounds → `xs:minInclusive` … on `xs:double`/`xs:integer` |
| single common purpose / milestone | `<info><purpose>` / `<milestone>` |

**Never invented.** Everything IDS cannot express is returned in `not_translated`,
one line per item with `handling` (`omitted` or `description_text`) and why:
every geometry aspect, every documentation item, the actors (carried only as
description text), a Revit category, free-text identification and notes, a
classification URI in applicability, bounds in a non-default unit (IDS values are
in the IFC default unit and this bridge does not convert), and whole requirements
that cannot become a checkable specification (no IFC version, no entity or
classification, or nothing alphanumerical with an optional occurrence).

**Proved before it is reported.** The generated XML is validated against the
published **ids.xsd 1.0.0**, embedded verbatim (`schemas/ids/ids-1.0.xsd`); the
two XML Schema definitions it imports (`xs:restriction`, `xs:occurs`) are supplied
from `schemas/ids/xmlschema-subset.xsd`, so validation never touches the network.
It is then parsed by `IdsReader`, the reader `horizun_validate_ids` and
`horizun_deliver_ifc` use; any file problem, undecidable specification or
unsupported facet makes the proof fail. A file that fails either proof is never
written.

**Writing.** `dry_run` defaults to `true` and returns the XML (`ids_xml`), the
proof and `would_refuse`. With `dry_run=false` and `output_path` (a `.ids` in an
existing folder) it writes atomically, never replaces a file without
`overwrite=true`, needs the `full_write` profile, and then re-reads the bytes,
compares SHA-256, re-validates against the XSD and re-reads with `IdsReader`
before reporting `written: true`. Optional `requirement_ids`, `milestone` and
`info` (`title`, `author` — an e-mail, as ids.xsd requires — `version`, `date`).
A loin block with `loin_*` errors, or an invalid context, is refused.

### Resumen en español

El bloque opcional `loin` de `project-context.json` recoge el **nivel de
información necesario** según **ISO 7817-1:2024** (antes EN 17412-1:2020):
requisitos previos (propósito, hito de entrega, actores), información geométrica
(detalle, dimensionalidad, ubicación, apariencia, comportamiento paramétrico),
alfanumérica (identificación y propiedades) y documentación. `validate` reporta
sus incoherencias (propiedad con dos tipos, entidad IFC inexistente en IFC4 /
IFC4X3_ADD2, ids repetidos, límites imposibles). `ids_from_loin` traduce la parte
alfanumérica a **IDS 1.0** y **lista, sin inventar**, lo que IDS no puede
expresar (geometría, documentación, actores, categoría de Revit, límites en otra
unidad). El archivo se valida contra el `ids.xsd` 1.0 embebido y con el lector
IDS del propio puente; ensaya por defecto y, al escribir, relee y vuelve a validar.

## bSDD lookup

The [buildingSMART Data Dictionary](https://github.com/buildingSMART/bSDD) (bSDD)
publishes classification systems (Uniclass 2015, the IFC schema itself, national
dictionaries) as classes with properties, allowed values, units and relations,
each with a stable URI. Two uses here: **mapping the project's classification
outward** (find the published class, its URI and relations) and **feeding the
LOIN / IDS with standard properties**.

To keep `tools/list` within its size budget this is not a new tool: it is five
read-only operations of **`horizun_catalog_lookup`** (host-resident, no Revit).
Without `operation`, or with `operation=leaf`, that tool is the catalogue leaf
check exactly as before.

| operation | bSDD endpoint | arguments |
|---|---|---|
| `bsdd_search` | `GET /api/TextSearch/v2` | `text` (2–200 chars), `dictionary_uris`, `offset`, `limit` (≤100) |
| `bsdd_search_dictionary` | `GET /api/SearchInDictionary/v1` | `uri` (the dictionary), `text`, `related_ifc_entity`, `language_code`, `offset`, `limit` |
| `bsdd_class` | `GET /api/Class/v1` (with properties, relations, child classes) | `uri`, `language_code` |
| `bsdd_property` | `GET /api/Property/v5` | `uri`, `language_code` |
| `bsdd_dictionaries` | `GET /api/Dictionary/v1` | `uri` (optional), `offset`, `limit` |

Endpoints and parameter names are those of buildingSMART's published OpenAPI
description ([`bSDD OpenAPI.yaml`](https://github.com/buildingSMART/bSDD/blob/master/Documentation/bSDD%20OpenAPI.yaml),
read 2026-09-24; `Property/v4` is deprecated there, hence v5). They are public
GETs on `https://api.bsdd.buildingsmart.org`; nothing authenticates and nothing is
written to bSDD. The host is a constant: URIs travel only as query parameters.
Example dictionary URI: `https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1`.

**Toward the LOIN.** `bsdd_class` returns, beside the class's properties,
`loin_property_suggestions`: each a `loin_property` built only from what bSDD
published (`property_set`, `name` = property code, `allowed_values`, `pattern`,
bounds, a single `unit`, `uri`), with `complete` and `still_needed`. `data_type`
is **never** filled from bSDD's `dataType`: `String`/`Real`/`Integer` are not IFC
defined types, and choosing `IfcLengthMeasure` over `IfcPositiveRatioMeasure` is
a person's decision. A suggestion without a property set is marked incomplete.

**Cache and cap.** Every successful answer is cached in
`%USERPROFILE%\.horizun\bsdd-cache` (one JSON file per request URL, written
atomically) and reused for `max_age_hours` (default 168, i.e. 7 days) unless
`refresh=true`. Network calls are capped at 30 per minute and 500 per server
process; a cached answer costs nothing. Responses above 8 MB are refused.

**Without network** the reply is an error that says bSDD could not be reached and
that nothing is assumed ("an unreachable dictionary is not an empty one"). Only
when an **expired** cached copy exists is it returned, with `source: stale_cache`,
its age and a warning. HTTP 404 is an answer (`found: false`); other 4xx are
errors carrying bSDD's message; 429 and 5xx are named as such.

The replies carry dictionary text published by third parties; the tool is
already marked as returning external content, so it passes through the same
content safety as model text.

### Resumen en español

La consulta al **bSDD** (diccionario de datos de buildingSMART) vive en
`horizun_catalog_lookup` como cinco operaciones de solo lectura (`bsdd_search`,
`bsdd_search_dictionary`, `bsdd_class`, `bsdd_property`, `bsdd_dictionaries`),
para no agrandar `tools/list`. Sirve para mapear la clasificación del proyecto
hacia afuera y para alimentar el LOIN/IDS con propiedades estándar: `bsdd_class`
devuelve sugerencias `loin_property` sin inventar nunca el tipo de dato IFC. Usa
la API pública (`/api/TextSearch/v2`, `/api/SearchInDictionary/v1`,
`/api/Class/v1`, `/api/Property/v5`, `/api/Dictionary/v1`), guarda caché 7 días
en `%USERPROFILE%\.horizun\bsdd-cache`, limita las llamadas (30/min, 500 por
proceso) y, sin red, lo dice claramente; solo entrega una copia caducada si
existe, marcada `stale_cache`.

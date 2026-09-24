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

### Not yet: elicitation

The intake is conversational: the agent asks. MCP elicitation (the server asking
the client for structured input) is not implemented, because this server has no
server→client request channel yet — no outbound request ids, no correlation of
client responses, and no capability-gated dispatch for them. When that exists, an
`elicit` operation can send each enum question as an elicitation schema.

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
por ese recorrido. La elicitation MCP no está implementada: el servidor aún no
tiene canal de peticiones servidor→cliente.

## Information containers & CDE

ISO 19650 is an international standard, so Horizun models its concepts directly:
the **information container**, its **suitability status**, its **revision** and the
four **CDE states** (`wip`, `shared`, `published`, `archived`). What stays an
argument is every concrete rule a project chooses: which fields make a name, their
order and patterns, the status codes, the revision patterns and the folders. Nothing
organisation-specific is compiled in.

Everything here works on **local or synced folders** (a desktop connector, a synced
document library, a plain folder). No tool calls a cloud API.

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

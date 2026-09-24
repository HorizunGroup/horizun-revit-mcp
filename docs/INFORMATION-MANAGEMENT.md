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

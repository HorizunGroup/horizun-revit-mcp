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

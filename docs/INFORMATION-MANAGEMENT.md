# Information management (ISO 19650)

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

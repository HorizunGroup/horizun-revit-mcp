# Extended tool notes

Detail that does not fit the short descriptions in tools/list. Each section is owned by
the change that added it.

## Clash resolution and batch undo

### horizun_resolve_clash

Detection is common; resolving a clash **with a verification** is the point of this tool.
It works on findings of the ledger that `horizun_clash record_findings=true` maintains
(see `horizun_coordination list`).

**propose** (read-only, default). For each `finding_ids` entry:

1. Roles. Pipes, ducts, conduits, cable trays and flex runs are *movable*. Structure
   (structural framing/columns/foundations, structural walls and floors) and
   architecture are never moved: the row is `report_only` with
   `report_only_no_movable_side`. When both sides are runs, the smaller cross section
   moves; equal sections are `ambiguous_both_movable` (a design decision).
2. Safety. A run in a linked model (`linked_element_not_writable`), a pinned run
   (`pinned`) or a run with any connected connector (`connected_run_not_safe` -
   moving one segment would tear the network) is reported, not moved.
3. Geometry (`Core/ClashResolveRules.cs`). The mover's centreline and exact section are
   compared with the fixed element's bounding box projected on each escape direction:
   `shift` perpendicular to the run in plan, and `elevation` (vertical) for horizontal
   runs only. The run's own axis is never an escape. Distances are whole millimetres,
   rounded away from the clash, and include `clearance_mm` (default 50). Candidates over
   `max_move_mm` (default 600) are rejected.
4. Third elements. A candidate whose moved box would reach any other model element is
   rejected (`would_touch_other_elements`); if none survives, the finding is report-only.

Each proposal carries `kind`, `distance_mm`, `affected_elements`, a `prediction`, and the
ready `next_arguments` for apply.

**apply**. `dry_run` defaults to true and issues a token bound to the proposals. With the
token, inside one TransactionGroup: the neighbourhood (every host model element whose box
meets the swept region of each mover, grown by the clearance) is measured on solids
BEFORE the move; the runs are moved; positions are re-read; the same neighbourhood is
measured AFTER. The group is kept only when every targeted pair is gone and no pair
appears that was not there before. Anything else - including a boolean that failed, so
the result is unmeasured - rolls the whole group back and returns `new_clashes` and the
`postconditions` checklist. A kept apply records an undo batch and marks each finding
`resolved_by_model` with the measurement written into its history.

Limit: the re-detection covers host elements only; a clash the move creates against a
linked model is not seen (declared in `WriteVerificationCatalog`).

### horizun_undo

Revit exposes no Undo through its API. After a verified commit,
`horizun_transform_elements` (move, copy, rotate, mirror, pin/unpin, change_type,
set_curve on a line, move_tag_head), `horizun_write_params_verified`,
`horizun_create_elements` and `horizun_resolve_clash` record an inverse in
`<data root>/undo/<document hash>.json` (last 20 batches) and report it as an `undo`
block. Operations without an inverse (set_tag_leader, wall_join, a non-line set_curve,
a parameter whose previous value was unreadable) record the batch as **not undoable** by
name, so `undo_last` never skips past it to an older batch.

- `list` - the document's batches, newest first, and whether `undo_last` is available.
- `undo_last` - dry run first. Refused when the document was saved or synchronized since
  the batch (its VersionGUID/NumberOfSaves stamp moved), or when any element the batch
  touched no longer carries the state the batch left (location, type, pin, orientation,
  tag head, or the parameter value). Otherwise every inverse runs in one transaction in
  reverse order (created -> deleted, parameters -> previous values, moves -> the opposite
  vector, rotations -> the opposite angle, mirrors -> the same plane); a delete that would
  take elements the batch did not create rolls back; every element is re-read against
  the state the batch found, in a `PostconditionCheck`.

### Resumen (español)

`horizun_resolve_clash propose` convierte hallazgos abiertos del ledger en correcciones
candidatas conservadoras: solo mueve tramos MEP del modelo anfitrión, sin conexiones ni
pin, el mínimo más la holgura (desplazamiento perpendicular o cambio de elevación);
estructura, arquitectura, vínculos, tramos conectados o movimientos que tocarían a un
tercero quedan como "solo informar". `apply` mueve dentro de un TransactionGroup y
vuelve a detectar sobre sólidos: el par debe desaparecer sin clashes nuevos o se revierte
todo; solo entonces el hallazgo pasa a `resolved_by_model`. `horizun_undo` deshace el
último lote Horizun registrado, y se niega si el documento se guardó o sincronizó desde
entonces o si esos elementos cambiaron.

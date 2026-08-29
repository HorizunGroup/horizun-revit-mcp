# Planimetry production

Horizun can now read, audit, compose, annotate, revise and visually review Revit
planimetry directly from the model. PDF export is not part of the control loop.
The database tools establish measurable facts; the captured sheet images let an
LLM judge qualities the Revit database does not encode.

## End-to-end flow

1. Call `horizun_health` and identify the active document.
2. Run `horizun_query_planimetry` with complete pagination, then
   `horizun_audit_planimetry` with the approved inline requirement set.
3. Correct explicit findings with `horizun_fix_planimetry`.
4. Compose sheets with `horizun_pack_sheets`. The input fixes sheet, priority,
   margin and gap; the packer chooses coordinates and treats every unselected
   placement as an obstacle. Unplaced content is measured as a real provisional
   viewport/schedule with rollback, including viewport labels; no paper size is
   inferred from an empty `View.Outline`.
5. Use `horizun_plan_annotations operation=auto_tags` for collision-aware tag
   points, `operation=intent_dimension` for a dimension chain chosen from
   semantic references, or the `auto_dimension_*` family (grids, levels,
   curtain walls, openings — over the host or ONE named link instance) for
   whole chains grouped by direction, ordered positionally and deduplicated
   against dimensions already in the view. All return a complete
   `horizun_annotate` dry-run call. For per-room deliverables,
   `horizun_plan_views operation=room_views` plans oriented elevations,
   crossing sections and a cropped plan per room, returning a complete
   `horizun_manage_views` dry-run call; schedule definitions are edited with
   `horizun_manage_schedules`, whose declared-whole filters and sorting make
   replays idempotent.
6. Run that dry run, inspect the provisional Revit rehearsal, and spend only its
   single-use confirmation token. An explicit tag type is validated and re-read.
7. Create or update the paper trail with `horizun_manage_revisions`, including
   sheet assignments and revision-cloud loops in view-plane coordinates.
8. Invoke the MCP prompt `planimetry-review`. It captures every sheet directly
   with `horizun_capture_view`, requires the LLM to inspect the actual PNG, and
   cross-references visual findings with the model inventory and audit.

## Safety boundary

The planners are read-only. Every model write defaults to a dry run, names its
target document, binds the resolved model state, requires a single-use token and
re-reads its postconditions. Packing and revisions are whole-batch operations:
one failure rolls back the entire arrangement or paper trail. A failed image
capture, truncated page, ambiguous dimension reference or unreadable bounding
box is `UNKNOWN`/refused, never interpreted as clean.

Standards remain data. Margins, gaps, required tags, selectors, dimension types,
revision text and visual acceptance criteria come from the project or approved
requirement set; none is compiled into the bridge.

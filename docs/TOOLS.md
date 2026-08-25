# Tool reference

Every tool Horizun Revit MCP exposes, what it does, and where it refuses.
The [README](../README.md) has the short version; this page is the complete
surface.

Three properties hold across the whole table, and they are why the list is
shorter than a raw tool count would be:

- **Typed writes are re-read after the commit.** A count comes from reading the
  model again, never from a call that did not throw.
- **Destructive and bulk operations default to `dry_run: true`** and require a
  single-use confirmation token to write.
- **An ambiguous request is refused with a reason**, not resolved by guessing.
  Where the bridge cannot tell two readings apart, it says so.

`horizun_execute_python` is the one documented exception to the first property,
and it is labelled as such everywhere it appears.

## In Revit, over the pipe

| Tool | What it does |
| --- | --- |
| `horizun_health` | Is the bridge alive, and WHICH Revit is on the other end — year, build, our own version and commit, and the document active right now (an explicit null when none is). |
| `get_document_info` | The open document, its counts and identity. |
| `horizun_open_document` | Open a model, refusing a file saved in another Revit version (opening upgrades it irreversibly) and refusing a workshared central unless asked twice. |
| `horizun_save_document` | Save, then prove it: the file's timestamp and size before and after. On a workshared model it says, loudly, that this is not a synchronize. |
| `horizun_relinquish_all` | Give back everything this user owns, and count what is still owned afterwards rather than assume zero. |
| `horizun_capture_view` | Export a view and hand the IMAGE back, so the caller can look at the model instead of only reading it. |
| `horizun_request_python_access` | Ask the machine owner visibly in Revit for persistent Python access. The MCP caller can show the question but cannot approve it, bypass the acknowledgement or queue it unattended. |
| `horizun_execute_python` | The execution fallback: Python against the whole API on the UI thread, stdlib included. **Disabled by default**. The owner may grant persistent access from the **Python ON/OFF** ribbon button until that same user revokes it, or configure the same durable opt-in administratively. `preflight=true` validates permission, document, size, hash and syntax without executing. Results are **self-reported, not host-verified**: the structured `__output__` contract classifies each run as `self_reported_verified`, `completed_unverified`, `partial` or `failed` — there is no `verified` state on this path, `host_verified` is always false, and a verified claim without evidence is downgraded. It detects an open transaction but cannot safely close or roll it back, and it has no typed command's dry-run, confirmation or post-commit guarantee. Long scripts arrive as `code_path` (a `.py` on this machine, decoded properly and named in tracebacks) instead of an inline string; what Revit raised comes back as `dialogs` and `failures` beside `__output__`, and `revit_raised(since)` reads the same record from inside the running script — which is how a batch learns that "Opening was canceled" was a dialog nobody was there to answer. |
| `horizun_model_scan` | The census, under the honesty contract. |
| `horizun_write_params_verified` | Parameter writes, each re-read after commit. |
| `horizun_delete_verified` | Deletion with the cascade counted, `dry_run` first. `mode` is mandatory: omitting it is refused and can never select `purge_unused`. |
| `horizun_document_session` | Read-only session and version inspection. |
| `horizun_audit_model` | Model checks with per-check pass/fail. |
| `horizun_quantities` | Quantities, with input rejected rather than guessed. |
| `horizun_clash` | Clash, where zero is a trustworthy zero. |
| `horizun_set_keynote` | Keynote writes with the blast radius reported first. |
| `horizun_family_apply` | Family edits in one transaction, under a geometry invariant that rolls the whole thing back if it moves. |
| `horizun_bind_shared_param` | Shared-parameter binding, with `VariesAcrossGroups` measured from the definition, not assumed. |
| `horizun_list_elements` | Bounded, paginated inventory by category across the host and loaded Revit links, with source model and link instance identity on every row. Unloaded links are reported, not silently skipped. |
| `horizun_query_model` | Composable query across host and loaded links: category, family/type/name/level, parameter predicates and 3D bounds; selected parameter projection, grouped summaries, coverage and stale-detecting cursors. |
| `horizun_navigate` | Select, frame or open host views from query results. Linked ids are explicitly refused because they are document-local. |
| `horizun_create_elements` | Atomic heterogeneous creation of levels, grids, walls, floors, ceilings, footprint roofs, rooms, family instances, structural framing/columns, ducts, pipes, conduits and cable trays in explicit units, with type/level resolution before the transaction and post-commit re-read. |
| `horizun_create_family` | Compile a loadable RFA from an RFT: parameters, formulas, types, reference planes, labeled dimensions (with optional family view, explicit linear dimension type, lock and EQ, verified against the planes they measure), symbolic/model lines, nested point instances with parameter propagation, solid/void extrusion/blend/revolution/sweep/swept-blend forms and face-hosted MEP connectors. After the verified SaveAs the family document is CLOSED and the saved file REOPENED, and every dimension is re-read from the bytes on disk before the RFA may be called verified or loaded into the project — a non-empty SaveAs is not verification. Requires `full_write` or `unsafe_code`. |
| `horizun_manage_system_types` | Duplicate project-resident system-family types and write their parameters atomically. Wall/floor/roof/ceiling host types can replace the complete homogeneous compound structure: ordered layers, material, width, function, wrapping, shell/core boundaries, structural/variable layer and deck data. Runtime class, name, values and layer graph are re-read after commit. |
| `horizun_transform_elements` | Atomic move, copy, rotate, pin/unpin and type changes over explicit ids, verified from fresh locations, copies, pin state and type ids. |
| `horizun_manage_views` | Dependency-aware creation of floor/ceiling/structural plans, sections, elevations, drafting/3D views, duplicates, templates, sheets, viewports and schedule placements; aliases let later actions use objects created earlier in the batch. |
| `horizun_get_dimension_references` | Semantic, read-only discovery of dimensionable references: wall side faces, centerlines, grids, levels, reference planes, edges, endpoints, nearest/farthest planar face from an explicit probe point. Every candidate carries a stable representation, the geometry that justified it, a 0.1 mm fingerprint and a structured reason when it is not dimensionable; equivalent candidates come back marked ambiguous instead of chosen. Linked references are refused with a structured reason, not guessed. |
| `horizun_annotate` | Atomic text, tags and dimensions — linear (simple and chains), angular, radial, diameter, arc-length and spot elevation/coordinate — with total rollback. The dry run CREATES the batch provisionally and rolls it back, so constructible is Revit's answer; explicit tag types are proven valid and re-read, and the existing tag count is bound so a concurrent duplicate makes the plan stale. The token binds every dimension reference's stable representation and geometry fingerprint, and a model that moves refuses as `stale_plan`. Apply verifies in a still-reversible state and rolls the whole batch back on any failed check; `expected_value` makes the intended measurement a postcondition. Radial/diameter/arc-length need the 2025 API and are refused by name on 2023/2024; spot slope has no creation API in any year. |
| `horizun_query_dimensions` | Complete dimension reads: view, type, curve, every reference with existence and stable representation, `AreReferencesAvailable` and broken-reference counts, per-segment values and overrides, EQ/lock, internal feet and display units. Deterministic, paginated, exact totals. |
| `horizun_edit_dimensions` | Atomic, read-back-verified dimension edits: type (same style only), line move by vector, prefix/suffix/above/below/value override per dimension or per segment, EQ/lock, text-position reset. The token binds each dimension's rehearsed state, so a dimension somebody else edited refuses as `stale_plan`; any failed postcondition rolls the whole batch back. Reference replacement is refused by name — the API has no setter in any supported year. |
| `horizun_query_planimetry` | The DOCUMENTATION surface, from the database rather than a PDF, in six explicit modes: `inventory` (the census — a total that could not be computed is absent and named, never zero), `sheets` (title blocks with type and extent, sheet outline, placed views, viewports, schedule placements, revisions, guide grid, requested parameters), `views` (template, scale, discipline, phase, level, crop and annotation crop AS GEOMETRY, scope box, view range, parent/dependents, filters, sheet placement), `placements` (viewports AND ScheduleSheetInstances with box outline, label outline and their union in SHEET coordinates), `annotations` (dimensions with broken/linked/unreadable reference splits, tags with targets and orphan state, text with the empty flag, 2D detail — each with a view-plane box projected by all eight corners), and `references` (elevation markers, callouts, reference viewers — resolved, missing, or an explicit `unknown` with the reason, never inferred from a name). Read-only, deterministic, cursors bound to arguments AND result set, exact totals, named unreadables, explicit coverage. |
| `horizun_audit_planimetry` | Judge the same surface and return FINDINGS — blocking, advisory or unknown, no 0–100 score. The universal catalog holds only what is true without a company standard (overlapping placements beyond an explicit tolerance where touching is NOT overlapping, sheets with zero or several title blocks, broken placements and references, orphaned and duplicated tags, empty text, degenerate detail, annotations demonstrably outside an ACTIVE crop); everything with a number or a name arrives as an INLINE requirement set — naming, allowed scales/templates/types, margins, gaps, required parameters, forbidden overrides, tag coverage with visibility-scoped counting, host/link separation and explicit exclusions. An unreadable fact is ALWAYS `unknown` and never a pass; a check with unknowns is never `passed`; a malformed set is refused whole; every finding cites the set's id, version and SHA-256. Read-only; `fixable` is false on every finding and the deliberate non-judgements are published in `not_covered`. See [PLANIMETRY-AUDIT.md](PLANIMETRY-AUDIT.md). |
| `horizun_fix_planimetry` | Turn a finding from `horizun_audit_planimetry` into a TYPED correction. Nine operations, closed: `set_view_template`, `set_view_scale`, `rename_view`, `rename_sheet`, `place_title_block`, `move_viewport`, `move_schedule`, `clear_element_override`, `set_crop` — every final value explicit, never inferred. Each action CITES the finding it repairs and is refused when that finding or its observed state moved, it is unknown, or its requirement-set hash differs. Dry run materialises and rolls back the batch; apply uses one `TransactionGroup`, verifies while reversible, re-reads after commit and re-runs the audit to separate `resolved`, `persistent`, `new` and `undetermined`. Layout/annotation/revision decisions live on the dedicated production tools below rather than being guessed from a finding. |
| `horizun_pack_sheets` | Deterministically pack an ordered mix of unplaced views/schedules and existing placements on one sheet. The caller fixes priority, margin and gap; every unselected placement is a fixed obstacle. Because `View.Outline` can be empty before placement, unplaced content is first measured through a real provisional viewport/schedule and confirmed rollback, including the viewport label and its offset from Revit's insertion point. Dry run then creates/moves the whole arrangement provisionally and verifies extents, containment and clearance; one item that cannot fit refuses the whole plan. Apply consumes its single-use token before the measurement transaction, then verifies one atomic `TransactionGroup`. |
| `horizun_plan_annotations` | Read-only automation for `auto_tags` and `intent_dimension`. Tags get deterministic collision-aware head points, existing tags are skipped and unreadable visibility is named. Dimensions are composed from `horizun_get_dimension_references`: exactly one compatible unambiguous semantic reference per target, ordered along an explicit/automatic axis with signed offset. The result is a complete `horizun_annotate` dry-run request, never a hidden write. |
| `horizun_manage_revisions` | Create/update revisions, add them to explicit sheets and create revision-cloud loops in explicit views. The complete paper trail is provisionally materialised and rolled back during dry run; apply verifies revision fields, sheet assignments and each cloud's revision/owner view before and after the atomic group commits. |
| `horizun_query_detail_2d` | The 2D-detail surface of ONE view, read-only: line styles, filled-region types with `IsMasking` read from each type, placeable view-based symbols with activation state (mode `resources`); or the view's existing detail curves, filled regions and instances with normalised geometry and deterministic 0.1 mm signatures (mode `elements`). Never resolves resources by name — every answer is ids, so ambiguity cannot happen. |
| `horizun_detail_2d` | Verified 2D drafting in one atomic batch: detail lines, arcs, polylines, filled and masking regions (`IsMasking` read from the TYPE, both mismatches refused), view-based components/symbols, and line-style changes over existing curves or same-batch keys. Loops validated pure before Revit is asked (closed, non-degenerate, non-self-intersecting, one exterior containing every hole); rehearsed by provisional creation; the token binds views, types, styles and normalised geometry (`stale_plan` on drift); apply verifies inside a TransactionGroup and rolls the whole batch back on any failed check. Coordinates are view-plane; a non-zero third component is refused, never projected silently. |
| `horizun_export` | Dry-run and verified PDF, DWG, configurable IFC, Navisworks NWC, multi-view FBX, image and schedule export. Only changed non-empty files matching the requested output family are attributed to the call. Requires `full_write` or `unsafe_code`. |
| `horizun_execute_plan` | Compose up to 100 typed writes into one ordered TransactionGroup. Later actions can consume exact prior results with `${key.path}`; any failure rolls the complete graph back. |
| `horizun_submit_job` | Queue any installed Revit-side tool (except Python/the queue itself), return a persistent job id, and poll it without blocking Revit. |
| `horizun_create_schedule` | Create a native Revit schedule with selected fields and sorting, optionally including linked elements. Defaults to `dry_run: true`, requires a target document and confirmation token, then re-reads the committed schedule. |
| `horizun_list_schedules` | List native schedules with their actual fields, linked-file setting, itemization and displayed body dimensions. |
| `horizun_get_schedule_data` | Read the displayed header and body cells of a native schedule with explicit row/column bounds and truncation metadata. |
| `horizun_split_floor_loops` | One floor per sketch loop, carrying the height offset onto each. |
| `horizun_split_multilayer_walls` | One wall per material layer, doors and windows re-hosted on the structural one. **Curved walls are REFUSED, not straightened.** |
| `horizun_split_multilayer_slabs` | One floor/ceiling per material layer, profile and curves intact. A slab whose hosted families cannot be put back rolls back alone. |
| `horizun_ungroup_and_mark` | Ungroup, stamping each member with its origin group — checked BEFORE anything is ungrouped, because an ungrouped-and-unmarked model is unrecoverable. |
| `horizun_regroup_by_param` | The reverse: rebuild the groups from that stamp. Annotation is excluded and reported, rather than failing the whole call. |
| `horizun_copy_slab_elevations` | Copy a warped floor's surface onto other floors. Names every destination that will LOSE an existing shape before it happens. |
| `horizun_embed_floors_in_toposolid` | Embed floors into terrain. Slabs touching at the same level merge into one outline; a real step between them does not. |
| `horizun_grade_toposolid_around_floors` | Offset, breaklines and a constant side slope out to daylight. Stations that never daylight are counted, not faked. |
| `horizun_rectangularize_walls` | Irregular orthogonal walls into rectangular fragments, from real solid geometry. Refuses curves and non-rectangular openings by name. |

The last nine keep their geometry in Python that ships beside the add-in
(`src/Horizun.Revit/Recipes/`), while the host owns the transaction, the
`dry_run`, and the re-read after the commit — see `Core/Recipe.cs`. All nine
default to `dry_run: true` and require a single-use confirmation token to write.

## Host-resident — answered by the server, no Revit needed

| Tool | What it does |
| --- | --- |
| `horizun_catalog_lookup` | Generic leaf resolution over a catalog file, `is_leaf` null ≠ false, sha256 provenance. |
| `horizun_job_status` | How a long run is going, read from disk WITHOUT touching Revit — answers while Revit is busy inside the very command it describes, survives a crash, and says whether the process that claimed the job is still alive. |
| `horizun_excel_write_rows` | Appends rows to `.xlsx` over the OPC package — no COM, no Excel installed. Backs the file up and re-reads every written cell. |
| `horizun_power_bi_push` | Push up to 10,000 primitive rows directly into a Power BI push semantic-model table. Credentials stay in server environment variables; a durable key prevents duplicate rows after a lost response. Requires `full_write` or `unsafe_code`. |
| `horizun_target` | Which Revit these tools are talking to, and how to change it. Two versions open at once is normal, and the expensive failure is a healthy bridge attached to the wrong instance. |

See [Family authoring](FAMILY-AUTHORING.md) for the loadable-RFA and
system-family capability matrix, complete examples and the explicit in-place API
boundary.

**One command executes at a time; concurrent calls wait in a bounded FIFO queue.**
There are 16 waiting slots. A successful JSON response includes `bridge_queue`:
`queued` says whether another bridge request was ahead at admission, while
`waited_ms` also includes time waiting for Revit's UI thread to become available. A
cancellation removes a request only while it is still queued, proving that it
never ran; once it reaches Revit's UI thread the API cannot interrupt it. A full
queue applies backpressure explicitly instead of dropping work or growing without
limit. Ordinary calls and `horizun_submit_job`/`run_async` jobs alternate when both queues are busy, so
neither can starve the other. Every reply also carries **what Revit raised while
the command ran** — warnings, errors and modal dialogs — on success and failure.

## Ordering, queueing and backpressure

**One command executes at a time; concurrent calls wait in a bounded FIFO queue.**
There are 16 waiting slots. A successful JSON response includes `bridge_queue`:
`queued` says whether another bridge request was ahead at admission, while
`waited_ms` also includes time waiting for Revit's UI thread to become
available. A cancellation removes a request only while it is still queued,
proving that it never ran; once it reaches Revit's UI thread the API cannot
interrupt it. A full queue applies backpressure explicitly instead of dropping
work or growing without limit. Ordinary calls and
`horizun_submit_job`/`run_async` jobs alternate when both queues are busy, so
neither can starve the other. Every reply also carries **what Revit raised while
the command ran** — warnings, errors and modal dialogs — on success and failure.

## Permission profiles

`read_only` hides and refuses model mutations. `safe_write` is the fresh-install
default and allows typed,
reversible model edits but refuses opening/saving/relinquishing, document-session
changes and external export. `full_write` enables those. `unsafe_code` is the
explicit administrative profile eligible for durable `horizun_execute_python`.
`allowed_tools` and `denied_tools` narrow any profile. The complete rules, and
what the bridge deliberately does not defend against, are in
[security-model.md](security-model.md).

The MCP may call `horizun_request_python_access` to show the consent question,
but only the person in Revit can answer it. The ribbon's **Python ON/OFF** button
grants persistent access until that same user revokes it on the next press. The server
announces the effective change with `notifications/tools/list_changed`; restart
only clients that do not implement that standard notification.

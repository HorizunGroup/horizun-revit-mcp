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
| `horizun_delete_verified` | Deletion with the cascade counted, `dry_run` first. |
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
| `horizun_create_family` | Compile a loadable RFA from an RFT: parameters, formulas, types, reference planes, labeled dimensions, symbolic/model lines, nested point instances with parameter propagation, solid/void extrusion/blend/revolution/sweep/swept-blend forms and face-hosted MEP connectors; save, optionally load, and verify both file and project Family. Requires `full_write` or `unsafe_code`. |
| `horizun_manage_system_types` | Duplicate project-resident system-family types and write their parameters atomically. Wall/floor/roof/ceiling host types can replace the complete homogeneous compound structure: ordered layers, material, width, function, wrapping, shell/core boundaries, structural/variable layer and deck data. Runtime class, name, values and layer graph are re-read after commit. |
| `horizun_transform_elements` | Atomic move, copy, rotate, pin/unpin and type changes over explicit ids, verified from fresh locations, copies, pin state and type ids. |
| `horizun_manage_views` | Dependency-aware creation of floor/ceiling/structural plans, sections, elevations, drafting/3D views, duplicates, templates, sheets, viewports and schedule placements; aliases let later actions use objects created earlier in the batch. |
| `horizun_annotate` | Atomic text, tags and dimensions. Dimensions use stable Revit references rather than guessing faces from element ids. |
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

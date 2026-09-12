# Production improvements — implementation and acceptance

This work extends branch base commit `5498141`. It is not a release or evidence
that Horizun outperforms Cortex. No installer or live model is changed by editing
this source tree.

## Verification performed — 2026-09-07

- Core: 3,786 passed, zero failed/skipped; Server: 477 passed, zero failed/skipped.
- Revit add-in: compiled against installed APIs for 2023, 2024, 2025, 2026 and
  2027, with zero warnings and errors in each final build.
- Public consistency passed. Isolated receipt tests passed in both Windows
  PowerShell 5.1 and PowerShell 7 (no real HTTP); benchmark runner syntax passed.
- Final TRX evidence is local under
  `artifacts/production-improvements-2026-09-07/` (core.trx and server.trx);
  these generated artifacts are not part of a published source package.
- No deployment or live model mutation. Revit 2025 and 2026 were open at handoff.
  Native room/family acceptance, live cache invalidation, latency measurements
  and cross-product comparisons remain pending.

The final regression run followed implementation. Two old wiring checks were
updated narrowly for the permission-checked room planner and the explicit,
symmetrically detached cache invalidation subscriptions; they still reject other
unexpected tool-specific execution decisions and event subscriptions.

## Implemented scope

| Area | Implementation | Limit |
| --- | --- | --- |
| Measurement | Query collection vs remaining command time, byte count, recent p50/p95; reproducible read-only runner | Command time excludes queue/transport; client time includes process startup. No inferred tokens or UI blocking time. |
| Query | Streaming summary counts, native category prefilter, per-call type/level lookup reuse | MEP summary still needs detailed collection. Full pagination semantics preserved. |
| Progressive responses | Inventory-only scan samples, explicit omissions and full expansion request; prompt guidance | Geometry and coverage are never shortened. Summary reduces payload, not scan computation. |
| Cache | Opt-in bounded JSON DTO cache: 32 entries, 8 MiB, 5 seconds; event invalidation | Host-only, non-workshared, model scope. Federation and view scope bypass. Hits still execute through the dispatcher. Fresh verification uses bypass. |
| Room documentation | Existing room planner integrated into atomic execute_plan; explicit templates, scales and sheet placements | Rejects partial room coverage. Multiple kinds need compatible template IDs per kind. No implicit sheet layout decisions. |
| Content | rectangular_prism / rectangular_tube recipes into existing RFA compiler, flex and PNG required | XY footprint is fixed; only height is parametric. Not an arbitrary family generator or an automated visual approval. |
| Diagnostic correction | Existing audit/correction/re-audit flow exposed in operating guidance | No new score or duplicate correction engine. Unsupported corrections remain findings. |
| Maintenance | Public-file checks ignore generated build/runtime trees; receipt mapping and bounded acknowledgement | Telemetry still requires administrator opt-in. Delivery is at-least-once, deduplicated by receipt_hash. |

## Room workflow example

Discover actual IDs first. Values below are illustrative, never automatic targets.
Use `horizun_execute_plan` with the normal target, dry-run and confirmation cycle:

```json
{
  "target_document": "Disposable fixture",
  "workflow": {
    "name": "document_rooms",
    "room_plan": {
      "room_ids": [101], "plan_view_id": 201, "kinds": ["plan", "sections"],
      "units": "mm", "scale": 50, "margin": 500,
      "name_pattern": "{room_number} {room_name} - {kind} {index}",
      "orient_to_walls": true
    },
    "view_types": { "section": 301 },
    "view_templates": { "plan": 401, "section": 402 },
    "room_sheets": [{
      "room_id": 101, "number": "A101", "name": "Room documentation",
      "title_block_type_id": 501,
      "placements": [{"kind": "plan", "index": 1, "point": [200, 150]}]
    }]
  },
  "dry_run": true
}
```

## Family recipe example

Add this recipe to `horizun_create_family` with explicit target, existing RFT path,
new RFA destination and units. Existing overwrite/load/confirmation policy remains:

```json
{
  "recipe": {
    "name": "rectangular_tube",
    "width": 200, "depth": 300, "wall": 20,
    "height_parameter": "Height",
    "types": [{"name": "Short", "height": 500}, {"name": "Tall", "height": 1000}]
  }
}
```

Both recipes reject raw geometry/parameter additions, invalid wall thickness,
duplicate names, equal heights, and disabling flex or thumbnail export.

## Acceptance gate

Implementation is not production acceptance. At the end of implementation:

1. Run Core and Server regression suites, public consistency and isolated receipt tests.
2. Compile against installed Revit APIs for every supported year; do not deploy
   one half of the server/add-in contract.
3. Install the matched build with Revit closed, then verify health and both binary
   hashes before any live benchmark. This is a separate deployment step.
4. Run the [read-only benchmark](../scripts/benchmark-query-readonly.ps1) with
   explicit binary SHA256s, build commit, Revit year, active document and a new output
   directory. Use at least 30 repetitions for reported percentiles. It does not
   run the full competitive production/content/diagnosis matrix.
5. In a disposable fixture: compare summaries to full queries (including text
   note types and linked/closed-workset coverage), test cache reuse/expiry and
   invalidation after mutation/undo/document switch, create room views/sheets,
   independently reread scale/template/placement and inspect saved recipe RFAs.
6. Inspect flex measurements and PNGs. Test invalid/partial batches, conflicting
   templates and stale confirmation tokens; verify rollback and no duplicates.

The common Cortex comparison cases remain in the
[benchmark review](BENCHMARK-REVIEW-2026-09-07.md). They are not marked passed by
the unit tests or the query-only runner.

# Wave 14 Manual Verification - Live Revit Smoke Test

Run server with `--toolsets structural`. Open a sample structural model.

- [ ] (skipped) `create_structural_column` at `(0, 0, 0)`, level `Level 1`: returns `created_id`; visible in 3D view.
- [ ] (skipped) `create_structural_beam` from `(0,0,3000)` to `(5000,0,3000)`, level `Level 2`: returns `created_id`; visible joining columns.
- [ ] (skipped) `create_structural_wall` from `(0,0)` to `(5000,0)`, `height_mm=3000`: `isStructural=true` confirmed in Properties panel.
- [ ] (skipped) `create_foundation_isolated` at `(0,0,0)` with `host_column_id` pointing to the column: footing appears under column.
- [ ] (skipped) `create_foundation_wall` with `wall_id` of structural wall: `WallFoundation` appears under wall.
- [ ] (skipped) `list_rebar` without filters: returns `count` and `total_matched`; if model has no rebar, `items=[]`.
- [ ] (skipped) `get_structural_loads` without filters: returns count of any point, line, or area loads.
- [ ] (skipped) `set_structural_load action=update` on an existing load: `changed_fields > 0`; force value in Properties panel reflects change.
- [ ] (skipped) `set_structural_load action=create`: returns `status=not_implemented`.
- [ ] (skipped) `analyze_structural_connections`: returns `joined_count > 0` for elements that share joins.
- [ ] (skipped) `tag_structural_framing` on a plan view with beams: `tagged > 0`; tags visible.

Live Revit smoke was not run in this recovery pass. If any item fails later, log it to `docs/221-tools/wave-14-issues.md` and address before merging.

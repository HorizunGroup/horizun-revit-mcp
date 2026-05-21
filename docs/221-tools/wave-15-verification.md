# Wave 15 Manual Verification - Live Revit Smoke Test

Run server with default toolsets. Open any sample model.

- [ ] (skipped) `set_project_info name="Test Project" number="P-001"`: `changed_fields` contains both; Manage > Project Information reflects values.
- [ ] (skipped) `get_model_warnings_summary`: returns `total_warnings`; if model has no warnings, `total_warnings=0`.
- [ ] (skipped) `purge_unused dry_run=true`: returns `safe_to_purge` count; no symbols deleted.
- [ ] (skipped) `purge_unused dry_run=false`: deletes symbols on a scratch model only.
- [ ] (skipped) `capture_view_image output_path=%TEMP%\bimwright-test.png`: file appears at given path.
- [ ] (skipped) `capture_view_image output_path=C:\Windows\test.png`: returns sandbox error.
- [ ] (skipped) `set_view_crop enabled=true`: crop visible in view.
- [ ] (skipped) `set_view_crop fit_element_ids=[<wall id>] padding_mm=200`: crop fits wall plus padding.
- [ ] (skipped) `set_view_scale scale=50`: view scale changes to `1:50`.
- [ ] (skipped) `activate_view view_name="Level 1"`: active view switches.
- [ ] (skipped) `show_element_in_view element_ids=[<wall id>] zoom=true`: Revit zooms to wall.

Read-only mode, start server with `--read-only`:

- [ ] (skipped) `set_view_scale` returns `{ "error": "read_only_mode" }`.
- [ ] (skipped) `capture_view_image` returns `{ "error": "read_only_mode" }`.
- [ ] (skipped) `get_model_warnings_summary` still works.
- [ ] (skipped) `activate_view` returns `{ "error": "read_only_mode" }`.

Live Revit smoke was not run in this recovery pass. If any item fails later, log it to `docs/221-tools/wave-15-issues.md` and address before merging.

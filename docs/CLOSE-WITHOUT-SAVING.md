# Closing without saving

Use `horizun_health` first to identify the active document, then name that document
by its full path (or unambiguous title) in `target_document`. A named document
may also be inactive. Call `horizun_document_session` with `operation=close`,
`save_on_close=false`, `discard_unsaved=true`, `activate_other=true` and
`dry_run=true`. The rehearsal changes nothing and describes any activation and
unsaved work. Apply the same request with `dry_run=false` and its
`confirmation_token` when one is issued. A modified or unreadable modified state
requires the token; a known clean document does not. The token binds the document
and request and expires; it does not freeze edits between rehearsal and apply.

The result rereads the open-document collection and compares file timestamps in
`disk_changed`. Only `false` is evidence that the measured timestamps did not
change; `null` means the comparison was unavailable. This is not a byte hash.
Unsaved work discarded by this operation cannot be recovered by the bridge.

For an unattended disposable test session, `scripts/live-probes/close-test-revit.ps1`
accepts `-TestDirectory` and the owned `-RevitPid`. It rejects paths outside the
directory and reparse points, rehearses and checks each selected document, then
requests a window close. It never terminates the process forcibly.

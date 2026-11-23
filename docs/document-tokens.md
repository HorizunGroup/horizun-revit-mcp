# Document token protocols

| Tool and operation | Token | Rehearsal |
|---|---|---|
| `horizun_open_document` | No | No dry run; guards check before opening. |
| `horizun_document_session` `open` | No | No token rehearsal. |
| `document_session` `save`, `save_as` | No | Same operation with `dry_run=true` validates arguments and document; it does not prove filesystem or Revit acceptance. |
| `document_session` `close` | Only to discard unsaved or unknown changes | Same operation with `dry_run=true` and `discard_unsaved=true`; apply with `dry_run=false` and its token. |
| `document_session` `new_project` | Yes | Same arguments with `dry_run=true`; apply with `dry_run=false`, the token and a new `idempotency_key`. |

Tokens bind the rehearsed operation and expire; repeat the rehearsal if rejected.

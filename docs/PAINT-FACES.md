# Verified face paint

`horizun_wall_finish` accepts `operation=paint_faces` with an explicit
`target_document` and 1–200 `pairs`:

```json
{
  "operation": "paint_faces",
  "target_document": "Example model",
  "pairs": [{
    "element_id": 123,
    "face_reference": "<stable face reference read from this model>",
    "action": "paint",
    "material_id": 456
  }]
}
```

Use `action=remove` without `material_id` to remove paint. Faces must belong to
the explicit host element; linked geometry and duplicate face actions refuse
before writing. Obtain stable face references from the existing dimension
reference reader; a face index is not a stable identity.

The default rehearsal records face geometry, current paint/material and target
material identity in the confirmation plan. Apply using `dry_run=false` and its
confirmation token. The shared edit runner re-reads committed faces using their
stable references, checks `IsPainted` and the exact `GetPaintedMaterial` ID, checks
the face geometry fingerprint, and rolls back the group on failed verification.
It verifies again after assimilation. The caller receives per-face evidence.

Removing paint from an already unpainted face is idempotent. Material creation
and layer material replacement remain their existing separate operations. Live
validation is pending for native faces, painted faces, removal, rejected linked
references, stale confirmations and transaction rollback in each supported year.

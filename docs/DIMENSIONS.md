# Dimension production

The complete workflow, end to end: discover dimensionable geometry, select
references semantically, rehearse the dimension, confirm, create it, read every
attribute back, and edit it later. Every step below is a real MCP call with the
fields that matter; nothing here is pseudocode.

Three rules hold throughout, and they are the same three the rest of the bridge
lives by:

- **Nothing is reported verified that was not re-read from the model.** A
  dimension "created" is a dimension whose class, view, type, curve, references,
  segments and values were read back and compared, field by field.
- **The dry run is a rehearsal, not a parse.** `horizun_annotate` CREATES the
  whole batch inside a transaction and rolls it back, so `constructible` is
  Revit's own answer and the rollback status in the reply is Revit's too.
- **The confirmation token binds the model, not the request.** Every reference's
  stable representation, its owner and a 0.1 mm geometry fingerprint, the
  effective dimension type (including a materialised default) and the measured
  value are part of the plan. If any of them moves before you apply, the apply
  refuses as a **stale plan** instead of dimensioning something you never saw.

Units: every coordinate travels in the request's `units` (`mm`, `m` or `feet`);
values come back in internal feet AND presented text. `expected_value` follows
`units`, except for `angular_dimension`, where it is degrees.

Call `horizun_health` first, always — the commands act on the ACTIVE document,
and health is what tells you which one that is.

## 1. Find the two walls

```json
{ "tool": "horizun_query_model", "arguments": {
    "categories": ["OST_Walls"], "name": "A-101",
    "return_fields": ["unique_id", "category", "name", "type"],
    "max_rows": 10, "include_links": false } }
```

The reply's rows carry `element_id` — say `211001` and `211002`. Coverage tells
you whether any part of the federation was unavailable; a clean answer over half
a model never reads like a clean answer over all of it.

## 2. Get their exterior faces

```json
{ "tool": "horizun_get_dimension_references", "arguments": {
    "view_id": 312000,
    "element_ids": [211001, 211002],
    "selectors": ["exterior_face"],
    "units": "mm" } }
```

`view_id` is required because compatibility is view-dependent. Each row is a
candidate:

```json
{ "element_id": 211001,
  "selector": "exterior_face",
  "reference_type": "face",
  "stable_representation": "8f9c.../43/INSTANCE/8f2a...",
  "geometry": { "kind": "planar_face", "origin": [...], "normal": [0, -1, 0] },
  "compatible_with_dimension": true,
  "geometry_fingerprint": "a41c...",
  "ambiguous": false }
```

The exterior face of a wall comes from `HostObjectUtils`, never from a guess.
On an element where the selector does not apply, you get a structured warning
for that element — not a silently different face.

## 3. Select without ambiguity

When two candidates are geometrically equivalent — two coplanar faces within
0.1 mm — BOTH come back with `ambiguous: true` and a shared `ambiguity_group`.
The tool never picks one for you, because the difference it cannot see may be
the one your deliverable depends on. Pick by `stable_representation` (or refine
with `nearest_face` plus an explicit `probe_point`) and keep the
`geometry_fingerprint` of what you chose: it is how the apply will later prove
the face you approved is the face that got dimensioned.

## 4. Rehearse the dimension

```json
{ "tool": "horizun_annotate", "arguments": {
    "target_document": "MOD_ARQ_A",
    "units": "mm",
    "actions": [{
      "operation": "dimension",
      "view_id": 312000,
      "line_start": [0, -500, 0], "line_end": [6000, -500, 0],
      "references": ["8f9c.../43/INSTANCE/8f2a...", "77b1.../43/INSTANCE/91c0..."],
      "expected_value": 6000, "expected_tolerance": 0.5 }] } }
```

`dry_run` defaults to true. The reply's `rehearsal` block reports that the batch
was provisionally created and rolled back (`transaction_status: "RolledBack"`,
`rolled_back_confirmed: true`), each action's `constructible`, and the measured
value. `plan_resolved` shows what the token binds; `confirmation_token` is
issued only when every action is valid AND constructible. No `dimension_type_id`
was named, so the reply names the default type that was materialised — that
identity is in the plan too, and changing the default before the apply is a
stale plan like any other drift.

## 5. Apply with the token

```json
{ "tool": "horizun_annotate", "arguments": {
    "target_document": "MOD_ARQ_A", "units": "mm",
    "actions": [ ...exactly the rehearsed actions... ],
    "dry_run": false,
    "confirmation_token": "<from step 4>",
    "idempotency_key": "dim-a101-exterior-2026-08-24" } }
```

Success answers `state: "committed_verified"` with one row per action:

```json
{ "index": 0, "element_id": 501234, "unique_id": "c0ffee...", "verified": true,
  "verification": { "checks": [
    { "field": "class",              "read": "Dimension",  "match": true },
    { "field": "owner_view_id",      "requested": 312000,  "read": 312000, "match": true },
    { "field": "dimension_type_id",  "requested": 42780,   "read": 42780,  "match": true },
    { "field": "references",         "requested": 2,       "read": 2,      "match": true },
    { "field": "value",              "requested_mm": 6000, "read_feet": 19.685, "match": true },
    { "field": "references_available", "read": true, "match": true } ] } }
```

The checks are the evidence; `verified: true` alone is never the answer.

## 6. Read the dimension back later

```json
{ "tool": "horizun_query_dimensions", "arguments": {
    "element_ids": [501234], "units": "mm" } }
```

The row carries the owner view, the type and its style, the curve (an unbound
dimension line is reported as origin+direction rather than invented endpoints),
every reference with its stable representation and whether the referenced
element still exists, `references_available`, `broken_references`, per-segment
values with their overrides, EQ and lock, and the value in feet and in `mm`.

## 7. Edit it

```json
{ "tool": "horizun_edit_dimensions", "arguments": {
    "target_document": "MOD_ARQ_A", "units": "mm",
    "actions": [{ "element_id": 501234,
                  "prefix": "±", "value_override": "VER PLANO" }] } }
```

Dry-run first, then apply with the token, exactly as in steps 4–5. The token
binds the dimension's rehearsed state — its type, curve, overrides and
references — so a colleague's edit in between refuses as a stale plan instead
of being overwritten. Every applied field answers requested/read/match;
`value_override: ""` clears the override and the read-back proves it. What you
cannot do is swap references: `Dimension.References` has no setter in any Revit
2023–2027, so the honest route — delete and recreate through `horizun_annotate`
— is named in the refusal itself.

## 8. Reading the failure states

- **`rolled_back`** — creation or a postcondition failed, and the WHOLE batch
  was rolled back inside the TransactionGroup. `rollback_confirmed: true` means
  Revit itself answered `RolledBack`; the model holds none of the batch, not
  even the actions that individually succeeded. Retry is safe.
- **`stale_plan`** — the refusal beginning `THE MODEL MOVED AFTER THE DRY RUN`.
  The request is identical, the document is the same, but a face moved, a
  reference vanished, a view or the default type changed. Nothing was written.
  Re-run the dry run, read the CURRENT plan, approve that one.
- **`uncertain`** — the one state that means the bridge does not know: Revit did
  not confirm a rollback (`rollback_confirmed: false`). The reply says exactly
  which transaction status was read. Inspect the model before retrying;
  `horizun_query_dimensions` over the target view is the fastest census.
- **Broken references** — `horizun_query_dimensions` reports
  `references_available: false` and counts `broken_references` when a referenced
  element no longer exists. References into RVT links are labelled `linked` and
  never counted broken: "not inspected" is not "gone".

## What is refused, and why it stays refused

| Request | Answer | Reason |
| --- | --- | --- |
| `radial_dimension` / `diameter_dimension` / `arc_length_dimension` on Revit 2023/2024 | typed refusal naming `RadialDimension.Create` / `ArcLengthDimension.Create` and 2025 | the API classes do not exist in those years; a Python fallback would call the same absent class, so none is offered |
| `spot_slope` | typed refusal, every year | Revit 2023–2027 exposes no creation API for spot slopes |
| an element INSIDE a loaded RVT link | **supported** — see [LINKED-DIMENSIONS.md](LINKED-DIMENSIONS.md) | discovery takes `linked_targets`, creation takes the host-document stable representations it returns, the plan binds the link's placement, and query resolves through the live link |
| a link INSTANCE named in `element_ids` | structured `link_references_not_supported` redirecting to `linked_targets` | the instance and an element inside it are different subjects; enumerating references on the instance itself would hand back the link's own bounding representation |
| an UNLOADED link named in `linked_targets` | structured `link_unloaded` | an unloaded link has no geometry in this session — nothing to enumerate, nothing to fingerprint, nothing a dimension could hang off |
| a link inside a link | structured `nested_link_not_supported` | `Reference.CreateLinkReference` lifts a reference exactly one level; manufacturing a deeper representation would produce a string that parses and then dimensions something else |
| MEP-curve centerlines (pipes, ducts, cable trays, conduits) | discovery row marked `compatible_with_dimension: false` with a structured code | measured live: on Revit 2024–2027 the centerline reference exists and `NewDimension` refuses it (`mep_centerline_rejected_by_dimension_api`); on Revit 2023 no geometry `Options` combination exposes a reference-carrying centerline at all (`no_stable_centerline`). Either way the row tells you before you try — dimension to faces or edges instead |
| replacing an existing dimension's references | typed refusal naming the missing setter | `Dimension.References` is read-only in every supported year |
| a leader option on linear dimensions | not published | `Dimension.Leader` does not exist in the API; spot dimensions take `leader` at creation, which does |

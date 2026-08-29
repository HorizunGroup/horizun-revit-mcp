# Dimensioning against linked models

The host-only rule is gone. `horizun_get_dimension_references`,
`horizun_annotate`, `horizun_query_dimensions` and `horizun_plan_annotations`
all understand references that live inside a loaded RVT link — and they refuse,
by name, every case where the link cannot answer for itself.

Read [DIMENSIONS.md](DIMENSIONS.md) first: everything there still holds. This
document is only what changes when the geometry belongs to somebody else's
model.

## What a linked reference is, and what it is not

Four ids are involved and they are not interchangeable. Confusing them is the
single most common way linked dimensioning goes wrong, so the bridge never
prints one where another belongs:

| Field | What it identifies | Whose document |
| --- | --- | --- |
| `link_instance_id` | the `RevitLinkInstance` placed in the host | **host** |
| `link_type_id` | the `RevitLinkType` it was placed from | **host** |
| `linked_element_id` | the element inside the linked model | **linked** |
| `element_id` | on a linked row this is the LINK INSTANCE, never the linked element | **host** |

`Reference.LinkedElementId` is the third of these. It is not the link instance,
and a row that puts it in `element_id` describes an element that does not exist
in the document the caller is holding.

A linked reference's stable representation is a HOST-document string produced by
`Reference.ConvertToStableRepresentation(hostDoc)` and parsed back by
`Reference.ParseFromStableRepresentation(hostDoc, …)`. Two placements of the
same link produce two different representations for the same linked element,
because they are two different pieces of host geometry — which is exactly why
identity here always includes the instance and never only the document.

## Discovery

`horizun_get_dimension_references` gains one optional field, `linked_targets`.
`element_ids` and `filter` are untouched and still mean host elements:

```json
{ "tool": "horizun_get_dimension_references", "arguments": {
    "view_id": 312000,
    "linked_targets": [
      { "link_instance_id": 880011, "linked_element_ids": [211001, 211002] }
    ],
    "selectors": ["exterior_face"],
    "units": "mm" } }
```

Every entry names its OWN link instance, so two placements of the same file are
two entries and never an ambiguity the bridge has to resolve. A `linked_targets`
entry with an empty `linked_element_ids` is refused: a whole linked model is not
a reasonable default answer to "what can I dimension here?".

`linked_targets` composes with `element_ids` in one call — the common case is a
host grid and a linked wall face in a single dimension — and the combined target
count is what the 200-target limit counts.

Each linked row carries its provenance in full:

```json
{ "element_id": 880011,
  "linked": true,
  "link": {
    "link_instance_id": 880011,
    "link_instance_unique_id": "…",
    "link_type_id": 880002,
    "link_name": "MOD_EST_A.rvt",
    "linked_document_title": "MOD_EST_A",
    "linked_document_identity": "…",
    "linked_element_id": 211001,
    "linked_element_unique_id": "…",
    "linked_element_category": "Walls",
    "linked_element_class": "Wall",
    "transform": { "origin": [...], "basis_x": [...], "basis_y": [...], "basis_z": [...],
                   "determinant": 1.0, "handedness": "right", "identity": false,
                   "has_rotation": true, "has_reflection": false },
    "transform_fingerprint": "…" },
  "selector": "exterior_face",
  "reference_type": "face",
  "stable_representation": "880011:0:RVTLINK/…",
  "geometry": { "kind": "plane", "origin": [...], "normal": [...],
                "origin_in_link": [...], "normal_in_link": [...] },
  "compatible_with_dimension": true,
  "geometry_fingerprint": "…",
  "ambiguous": false }
```

`geometry` is in HOST coordinates — where the dimension will live. The
`*_in_link` twins are the same facts in the linked model's own coordinates, so a
caller comparing against the linked file does not have to invert the transform
itself. The fingerprint is built over the HOST-space facts, so moving the link
changes the identity of every reference it carries, which is the whole point.

Every reference row keeps the deterministic order documented in DIMENSIONS.md.
Linked rows sort after host rows for the same element id, then by link instance,
then by linked element id — one total order over a federated answer.

## What is refused, and why

Each of these is decided BEFORE a transaction opens. None is a guess and none is
a silent skip; the code travels in the row so a client branches instead of
parsing prose.

| Code | Situation |
| --- | --- |
| `link_unloaded` | the `RevitLinkType` reports a status other than `Loaded` |
| `link_document_unavailable` | `GetLinkDocument()` returned null on a link that claims to be loaded |
| `not_a_link_instance` | the id in `link_instance_id` is not a `RevitLinkInstance` |
| `linked_element_missing` | the id does not exist in the linked document |
| `linked_element_is_type` | the id is an `ElementType`, not a placed instance |
| `nested_link_not_supported` | the named element inside the link is itself a link instance |
| `link_reference_not_creatable` | `Reference.CreateLinkReference` produced nothing usable |
| `link_reference_unreadable` | the reference exists but its geometry could not be read in this view |
| `link_transform_moved` | the instance's total transform changed after the plan was minted |
| `linked_document_changed` | the linked document's identity changed after the plan was minted |
| `link_instance_changed` | the link instance itself was replaced or deleted after the plan was minted |

`link_unloaded` is the one people meet first and it is deliberately not
softened: an unloaded link has no geometry, so there is nothing to dimension to
and nothing to fingerprint. Reload the link and ask again.

`nested_link_not_supported` exists because `Reference.CreateLinkReference` lifts
a reference exactly one level. A reference obtained from a link inside a link
cannot be expressed in the host document by that API, and manufacturing a
representation for it would produce a string that parses and then dimensions
something else.

## Creation

`horizun_annotate` takes linked references in the same `references` array. The
strings are host-document stable representations, so nothing about the call
shape changes:

```json
{ "tool": "horizun_annotate", "arguments": {
    "target_document": "MOD_ARQ_A", "units": "mm",
    "actions": [{ "operation": "dimension", "view_id": 312000,
                  "line_start": [0, -500, 0], "line_end": [6000, -500, 0],
                  "references": ["880011:0:RVTLINK/…", "8f9c…/43/INSTANCE/8f2a…"],
                  "expected_value": 6000 }] } }
```

Host and linked references may be mixed inside one dimension: that is the common
case (a grid in the host, a face in the structure link). Each reference keeps
its own provenance in `plan_resolved.references[]`, and the verification block
after the commit reports the provenance of every reference that was actually
read back off the created dimension.

**Linked GEOMETRY constructs; linked DATUMS do not — measured.** On live Revit
2026 (2026-08-26) the provisional rehearsal created a `LinearDimension` between
a linked wall's two faces and measured it at exactly 200 mm, while the same
call against a linked grid's reference answered `Invalid number of references`.
So: faces and edges through a link carry dimensions; a linked grid, level or
reference plane is real, stable, and marked
`compatible_with_dimension: false` with code
`linked_datum_rejected_by_dimension_api` — and `horizun_annotate` refuses it
by name BEFORE any transaction. Dimension to linked geometry, or to the host's
own datums. For the same reason, `auto_dimension_*` refuses `link_instance_id`
outright: grid/level chains over a link would plan dimensions whose every
rehearsal fails, and curtain-grid-line / opening-centre references are
datum-backed and unproven.

`spot_elevation` and `spot_coordinate` take a linked reference too. `angular`,
`radial`, `diameter` and `arc_length` take linked references on the years where
the underlying API class exists at all — the per-year refusals in DIMENSIONS.md
are unchanged and are checked FIRST, so an unavailable class is never reported
as a link problem.

What does NOT change: the rehearsal still provisionally creates the whole batch
and rolls it back, so `constructible` on a linked action is Revit's own answer
to "can a dimension actually hang off this linked face in this view?" and not
this bridge's opinion.

## The token binds the link

A linked plan binds everything a host plan binds, and then:

- the link instance's unique id;
- the instance's `GetTotalTransform()`, fingerprinted on the same 0.1 mm grid
  as every other geometric fact;
- the linked document's identity (title plus a hash of its path);
- the linked element's unique id;
- the reference's host-space geometry fingerprint.

Move the link 1 mm between the rehearsal and the apply and the apply refuses
with `stale_plan`, naming `link_transform_moved`. Point the link at a different
file and it refuses with `linked_document_changed`. Delete the instance and it
refuses with `link_instance_changed`. Nothing is written in any of the three.

## Reading them back

`horizun_query_dimensions` resolves what it can and labels what it cannot:

- a `linked: true` row carries the `link` block above, resolved through the live
  link when that link is loaded;
- when the link is UNLOADED the row says `link_state: "unloaded"` and the linked
  element is reported as `unknown`, never as broken — "we could not look" is not
  "it is gone";
- `link.transform_fingerprint` is the instance's CURRENT fingerprint. The bridge
  does not stamp storage onto dimensions it creates, so it never claims to know
  where the link stood when the dimension was drawn; it reports where the link
  stands now and lets the caller compare against the plan it kept;
- `coverage` gains `linked_references`, `unloaded_link_references` and
  `unreadable_link_references` beside the existing counters, so a clean-looking
  census over a half-loaded federation cannot read like a clean census over all
  of it.

## Editing

`horizun_edit_dimensions` treats a dimension with linked references exactly like
any other for text, prefix, suffix and override edits: those fields live on the
dimension in the host document and the link is not involved.

What it still refuses, for the reason it always refused it, is replacing
references: `Dimension.References` has no setter in any Revit 2023–2027. On a
linked dimension the refusal names the link as well, because the honest route —
delete and recreate through `horizun_annotate` — needs that link instance to
still be loaded and still be where it was.

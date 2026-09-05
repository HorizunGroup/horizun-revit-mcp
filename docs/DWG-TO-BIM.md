# DWG → BIM

Turn a linked drawing into a model, without a single layer name, family or
office standard compiled into this bridge. The mapping is **yours**: a document
you write, version and review, and the bridge refuses it whole when it is
malformed rather than guessing at what you meant.

The tools are `horizun_manage_cad_links`, `horizun_query_cad`,
`horizun_plan_from_cad`, `horizun_apply_cad_plan`, `horizun_audit_cad_model`,
`horizun_plan_cad_update` and `horizun_apply_cad_update`.
[docs/TOOLS.md](TOOLS.md) has the one-line description of each.

---

## The order, and why it is not negotiable

```
link ─▶ read ─▶ plan ─▶ rehearse ─▶ apply ─▶ audit ─▶ (next revision) update
```

**Convert the walls before the doors.** A plan is computed *before* it is
applied, so one run cannot build a wall and then host a door in it — the wall
does not exist at the moment the plan is made. `horizun_plan_from_cad` refuses
with `host_not_found` and says exactly this. Convert the wall layers, look at
what you got, then plan the openings against the model that now has walls.

That refusal is the shape of the whole path: where two readings are possible and
only a person can choose, the bridge stops rather than choosing.

---

## The requirement set

One document. Five top-level keys and nothing else — a misspelt section is a
section that silently does not run, so an unknown key is refused by name.

```jsonc
{
  "schema": "horizun.cad-requirements/1",
  "requirement_set": { "id": "acme-arch", "version": "1.2.0", "title": "Acme architectural" },
  "source":     { "units": "millimeter" },
  "tolerances": { "point_mm": 1.0, "gap_mm": 25.0, "angle_degrees": 2.0, "arc_sagitta_mm": 5.0 },
  "rules": [ /* … */ ]
}
```

`source.units` must agree with what the link declares. It is checked, and a
mismatch is **refused** rather than converted: a drawing read at the wrong scale
builds at the wrong scale, and 25.4× wrong looks entirely plausible on screen.

### A rule

```jsonc
{
  "id": "external-walls",
  "precedence": 20,
  "discipline": "architecture",
  "layers":         ["A-WALL-EXT*"],
  "exclude_layers": ["A-WALL-EXT-PATT"],
  "produces": "wall",
  "category": "OST_Walls",
  "family_type": "Basic Wall: Exterior - Brick on Block",
  "level": "Level 1",
  "height_mm": 3000,
  "structural": true,
  "min_confidence": 0.6,
  "geometry": {
    "from": "double_lines",
    "min_thickness_mm": 100,
    "max_thickness_mm": 400,
    "min_overlap_fraction": 0.6,
    "bridge_openings_mm": 1500
  }
}
```

**`geometry.from`** — how the drawing says the thing exists:

| `from` | reads | typical use |
|---|---|---|
| `double_lines` | two parallel lines a thickness apart | straight walls |
| `double_arcs` | two **concentric** arcs a thickness apart | curved walls |
| `closed_loops` | a closed ring, with nested rings as holes | floors, ceilings, roofs, rooms, openings, shafts |
| `single_lines` | one line | grids, beams, pipe and duct centrelines, room separators |
| `point_clusters` | marks close together | doors, windows, columns, fixtures |
| `blocks` | block references | reserved; the harvester's business |

**`produces`** is a closed vocabulary. A value outside it is refused with the
list, because a rule that produces nothing is a rule that reads as a clean
conversion of an empty drawing.

**`structural`** says the thing bears load. It is not a label: a structural wall
and an architectural one of the same thickness are different elements to every
analytical model and every structural schedule. Omit it and the document's own
default stands — emitting `false` would be this bridge deciding.

**`bridge_openings_mm`** is the one that surprises people, and it is opt-in on
purpose. A plan drawing shows a wall **interrupted at every door and window**,
because that is what a plan section of a building looks like. Read literally, a
wall with two openings becomes three walls with gaps: the count is wrong in
every schedule, the walls do not join, and the doors have nowhere to live. This
number says how wide a break may be to be read as an opening rather than as the
end of one wall. It stays opt-in because two separate walls in line across a
corridor look exactly like one wall with a wide opening, and only somebody who
knows the building can say which. **The number is that judgement, written down**,
and every gap it crosses is named in the candidate's assumptions.

---

## What the drawing cannot tell you

Measured, and published in every reply's `harvest_coverage` rather than left to
be discovered:

- **Text is unreachable.** No string is reachable from imported DWG geometry at
  any depth; text arrives as curves on its own layer. The layer name survives,
  the words do not — so grid names, room numbers and door marks come from your
  requirement set, not from the drawing.
- **There is no entity handle.** `GeometryObject.Id` collides. Identity is
  derived from a quantised surrogate of what an entity is and where it sits,
  which is what makes an audit and an incremental update possible at all.
- **Hatches** arrive as zero-volume solids, and are reported rather than counted
  as geometry.

[ADR-001](ADR-001-direct-dwg-reader.md) records why the bridge does not open the
DWG itself to get around this, and the conditions under which that would change.

---

## Names, and everything else the drawing does not carry

Text is unreachable, so a grid has no name and a room has no number until
something supplies one. The requirement set is that something, and `naming` is
where it says so.

```jsonc
"naming": {
  "strategy": "ordered",        // ordered | by_semantic_id | by_position
  "axis": "x",                  // ordered: which way to count
  "direction": "ascending",
  "values": ["A", "B", "C", "D"],
  "order_tolerance_mm": 50,
  "on_unnamed": "refuse"        // refuse | review | leave_unnamed
}
```

**There is no default strategy, and `ordered` refuses without an `axis`.** An
implicit order is whichever line Revit returned first, which is not stable
between runs let alone between machines — so a grid would get the name "A" for a
reason nobody wrote down and nobody could reproduce.

Three ways to say which name goes where, and each refuses rather than guesses:

| `strategy` | says | refuses when |
|---|---|---|
| `ordered` | count along an axis and hand out `values` in order | no axis; two candidates within `order_tolerance_mm` of each other along it, because then there is no first one |
| `by_semantic_id` | this exact geometry gets this exact name | a candidate the table does not name, per `on_unnamed` |
| `by_position` | whatever is nearest this point gets this name | nothing is within `tolerance_mm`, or two things are |

**A name the model already holds is refused before anything is built.** Revit
refuses a duplicate grid name at creation, so a plan carrying one would fail
*after* building part of the batch — half a conversion, and a person left to work
out which half.

**Rooms take a `name` and a `number`,** and they are separate: they are edited
for different reasons and a report that folded them together would name the wrong
one. Both are set inside the same transaction that creates the room and re-read
from the model afterwards, so `identity_verified` is a fact about the document
rather than about the call.

---

## Parameters a rule declares

A drawing carries no fire rating, no phase, no cost code. A layer does.

```jsonc
"parameters": {
  "Fire Rating": "60",
  "Comments":    { "value": "converted from A-WALL-EXT", "scope": "instance", "required": true }
}
```

A bare value is an instance write. The long form adds:

- **`scope`** — `instance` (default) or `type`. **A type write changes every
  instance of that type in the model, including ones this conversion did not
  create.** It is allowed, because it is sometimes exactly right; what it may
  never do is happen quietly. `horizun_write_params_verified` states the blast
  radius before its token is spent — how many elements would change, and how many
  of them were never named. **A rehearsal of the CONVERSION does not**, and says
  so: the elements do not exist yet, so there is no id to write against, and the
  reply reports `rehearsed: false` with the values it would write rather than a
  verdict it did not earn. A misspelt parameter name is therefore found by the
  apply, not by the dry run.
- **`required`** — `true` by default. A nice-to-have may proceed without being
  written; a fire rating may not.

**A value can land and still not be re-read as the bytes you passed.** A unit
string on a numeric parameter — `"900 mm"` on a sill height — is applied through
Revit's own parser, so the writer can confirm it only against the formatted value
it reads back. That is written, and it is reported as written, with the weaker
evidence named beside it rather than folded into the verdict. What is NOT written
is a row that failed and a row the writer could not re-read at all; treating the
second as success would be the one claim this bridge exists not to make.

**They are not written atomically with the elements, and the reply says so.**
Revit commits the create before the ids exist to write against, so the parameters
are a second transaction. A failure there leaves elements built and not
annotated, and the stage reports `applied_without_parameters` rather than calling
itself clean. **The ids are kept**, so the fix is to write the parameters — never
to build the elements again.

Every value goes through `horizun_write_params_verified`, the one writer in this
bridge that coerces, refuses and re-reads. A conversion that wrote them itself
would be a second set of rules about what "60" means.

`allow_structural` sits beside them and is deliberately **not** the same key as
`structural`: one describes what an element *is*, the other is what a person
*accepts*. Reading the first as the second would let a rule that merely describes
a load-bearing slab authorise cutting a hole in it.

---

## Holes, shafts and room separators

Three `produces` values that look similar in a drawing and are three different
elements in Revit.

| `produces` | builds | needs |
|---|---|---|
| `opening` | a hole through ONE floor, roof or ceiling | a ring, and a slab under it |
| `shaft` | a hole through EVERY slab between two storeys | a ring, `base_level` and `top_level` |
| `room_separator` | a model curve that bounds a room | a chain of curves, and a plan of its own storey |

**A shaft is not a hole in a slab.** Measured across Revit 2023–2027: there are
four `NewOpening` overloads and they build different things. Reading the shaft as
one opening per floor is the tempting shortcut and it is wrong in a way nobody
sees for months — the shaft stops existing the day somebody adds a storey, and it
is a different element in every schedule. A shaft rule that names only one storey
is refused: a plan drawing shows one ring and says nothing about height, and a
shaft that stopped at the wrong storey looks entirely correct in plan.

**A hole does not find its host the way a door does.** A door belongs to the wall
it is *near*; a hole belongs to the slab it is *inside*. The nearest floor to a
ring drawn over a courtyard is the floor around the courtyard, and cutting that
one would be a hole the drawing does not show. So the point is projected onto
each slab's own horizontal faces — a bounding box covers the courtyard an
L-shaped floor does not have. Several slabs covering one point is a building with
storeys, and the rule's `level` is what decides; without it, the plan refuses and
names the contenders.

**A separator is drawn THROUGH a view, and it must be a plan of its own storey.**
This is not a formality. Measured on 2026: `NewRoomBoundaryLines` with a view
whose storey is not the sketch plane's does not raise — the process ends
mid-transaction and the bridge sees a closed pipe. The plan finds a plan view of
the storey the separator sits on or refuses; there is no falling back to whatever
is on screen, because that fallback is the bug. And the check runs in the
*rehearsal*, because a dry run that answers `valid` and is then followed by a
crash is worse than no dry run at all.

---

## Reading a drawing you have never seen

`horizun_query_cad` with `mode: "profile"` answers the measurable half of
"where do I start", and refuses the other half.

For every layer it runs **every** geometry source and reports what each found —
measured by the same reader the conversion runs, so a count there is a count
here — with the thicknesses, areas and lengths it observed. It ranks them by how
much of the layer a reading consumed, and among equals by the fewest pieces:
`single_lines` reads every segment as its own candidate, so a ranking by count
would pick it everywhere and offer a ring of four segments as four lines.

It hands back a requirement-set skeleton with the bands already filled in from
what it measured, widened a tenth at each end so the run that produced a number
is not excluded by it — **and every `produces` set to null**. That skeleton does
not load until a person fills them in, on purpose. No organisation's layer
convention is compiled into this bridge, and one that was would convert the next
organisation's drawing wrong into a model that looked entirely plausible. The
drawing says where the geometry is. It does not say what the building is.

`structure_found` is the honest summary per layer: false when nothing reads as a
run, a curved run or a ring. Hatching and annotation land there — something will
always claim a mark, so what says a layer carries no building is that flag rather
than an absence.

---

## More than one drawing

A building is a plan per storey and often a plan per block, converted under the
same rules into the same model. Measured, with two drawings on two storeys:

- An incremental update for one drawing proposes **no orphans** for the other's
  elements. They were built from a different file and are none of that run's
  business.
- Each audit matches its own drawing's count and reports the other's elements as
  `built_from_another_drawing`, once each. Not swept in — that would report
  another storey as work this drawing failed to build. Not ignored — that would
  let a model quietly accumulate conversions nobody remembers.
- What still needs saying out loud is a **re-issue**: nothing in a DWG says one
  file supersedes another, so `supersedes_sha256` is a statement you make. An
  update against a drawing the model has never seen, in a model that holds work
  from other drawings under the same rules, refuses rather than treating the
  whole existing conversion as untouched and this drawing as entirely new work.

---

## Auditing what you built

`horizun_audit_cad_model` is **read-only, and that is the point**: an audit that
could change what it measures cannot be used as evidence. It reads the drawing
exactly as the plan did, then compares.

Matching is a **ladder**, and the rung is part of the answer — revision (same
entity, same issue), semantic (same entity, a re-cut file), geometry (same
shape, different layer), position (no provenance, something merely standing
there). Findings come from a closed vocabulary and **every code is reported with
its count, including the zeros**, because "no unhosted doors" and "hosting was
never checked" must not be the same absent key.

The ones worth knowing:

| code | means |
|---|---|
| `unhosted` | a door or window hosted in **nothing**. It cuts no opening, and it schedules, tags and renders exactly like a real one |
| `type_differs` | right element, wrong type — type carries thickness, fire rating and cost |
| `size_differs` | right line, wrong thickness or diameter; every quantity carries the second number |
| `moved` | matched, but off the drawn line by more than the tolerance |
| `extent_differs` | on the line, different length — usually a Revit wall join, and said so when the thickness explains it |
| `drawing_not_built` / `built_not_in_drawing` | the two sides disagree about something existing |

---

## The next revision

`horizun_plan_cad_update` answers *what changed*, which is a different question
from *what to do about it*. Every action carries a classification from a closed
list: `unchanged`, `added`, `removed`, `moved`, `reshaped`, `retyped`,
`relayered`, `resized`, `rehosted`, `manually_diverged`, `ambiguous`,
`conflict`.

Three rules hold throughout:

1. **Nothing is ever deleted automatically.** An entity that moved far enough
   reads as a new one, so a deletion and a relocation look identical from here.
2. **A judgement is never taken silently.** When more than one candidate could
   be an existing element moved, *all* of them are held — the winner is a
   preference, not a finding.
3. **The drawing moving and a person moving it need opposite treatment.**
   Telling them apart needs the geometry the element was BUILT with, which
   provenance records; where that record is missing the answer is review, not a
   guess.

You state which drawing this supersedes (`supersedes_sha256`). Nothing in a DWG
says one file is a re-issue of another, so it is a statement you make — and
without it, an update would report your whole existing conversion as untouched
and the new drawing as entirely new work.

### Which placement, and has it moved

A drawing is not one thing in a model: it is a **file** (bytes on disk, or none
for an embedded import) **placed** one or more times, each placement its own
`ImportInstance` with its own transform. Provenance v2 keeps those three
identities apart, and the update is scoped by **placement**, never by file:

- `placement.id` is the ImportInstance UniqueId. An update for one placement
  claims only elements stamped with that id (or with a placement you name in
  `supersedes_placement_ids`). Two links of one file share a hash and nothing
  else; the other placement's elements are reported under `scope.other_placement`
  and are never claimed, orphaned or re-stamped.
- `source.identity.mode` says which identity the run is using: `file_hash` when
  the file is on disk; `embedded_placement` when there is no external file (then
  `source_hash: unavailable` — Revit keeps no bytes it will hand back — and
  identity is the placement id plus its transform); `source_file_missing` when a
  path is recorded and nothing is there (the run plans against the geometry Revit
  last loaded, and says so).
- When the run can claim **nothing**, it refuses with `scope_unidentified`,
  naming what it looked for and what exists, instead of reporting zero changes
  about a conversion it never looked at. `supersedes_unstated` still fires when
  other files are stamped and no lineage was stated.
- A placement that no longer sits where it sat when its elements were built is
  refused with `placement_moved` and the delta (`delta_mm`, `rotation_degrees`).
  Nothing is re-matched as if the drawing had changed. If the move was
  deliberate, pass `accept_placement_move: true` to **both** the plan and the
  apply: the plan is re-derived under the new transform — an element still on
  its built line follows the drawing (`set_curve`, classified `moved`), one
  already where the drawing now puts it is left and re-stamped, one a person
  also moved is `conflict`. The person-moved / drawing-moved distinction is kept
  throughout.
- `horizun_apply_cad_update` replays a repeated `idempotency_key` with the same
  actions (`replayed: true`, nothing runs) and refuses the same key over
  different actions. A run that ended `partial` is remembered against its
  placement for the session, and the next apply there carries it as
  `previous_partial`.

**Provenance v1 → v2.** Elements stamped by 1.1.x–1.2.0 carry no placement id.
They are still read (`provenance_version: "v1"`), and the planner treats them
explicitly: when the model holds at most one placement of their file — or their
exact v1 source fingerprint matches this placement — they are claimed and listed
under `migrated_from_v1`; the apply then rewrites those records as v2 without
touching a single element's geometry, and reports the count (`migrated_from_v1`,
inside `provenance_rewritten`). Plan the same drawing again and there is nothing
left to migrate.

When two or more placements of that file exist, the v1 record cannot say which
one built it, and the whole run is **refused** with `ambiguous_v1` naming every
placement that could have — before the claimable count is consulted, before the
placement transform is compared, and before a single action is derived. The
refusal is unconditional: it fires even when other elements of that placement
are perfectly claimable. It has to, because out-of-scope is not the same as
safe — the drawing entity behind an excluded element matches nothing in scope
and comes back as a `create`, so a plan that carried on would put a second wall
on top of the one already standing. The refusal also says explicitly not to
reach for `horizun_plan_from_cad`, which against a model that already holds the
conversion builds the whole drawing again. Settle the ownership instead: delete
or repoint the placement that did not build those elements so exactly one
remains, or delete the elements and convert them again under one placement.

**How the migration is proved, and what the proof is worth.** The migration can
only be exercised against a record that genuinely lacks a placement id, and this
build stamps v2 on everything it touches — so on any machine where the previous
release is not installed there is nothing to migrate. Step 13 of
`scripts/live/verify-dwg-incremental.ps1` therefore *converts* with this build
and then **demotes** the result through `CadProvenanceV1Fixture`, which writes
the retired v1 schema built from `CadProvenanceV1Shape`: the v1 definition as it
stood in `CadProvenanceStore` before provenance v2 (`git show
c56a1be^`) — the same GUID, schema name, vendor id, access levels and
documentation, the same fifteen fields in the same order with the same types and
the same single `Number` unit spec, and with the five fields v2 added simply
absent. Nothing about the shape is invented, `CadProvenanceV1ShapeTests` pins it
against the field constants still standing in `CadProvenanceStore.cs`, and every
other value in the record is one this build's own converter wrote.

That establishes that **this build's reader, scope rules, planner and apply
handle a record of v1's shape**. It does **not** establish that a 1.1.x *binary*
wrote that shape: no old binary is run, and no fixture can make that claim. The
evidence for the shape itself is documentary — the definition in this
repository's own history, cited above.

The fixture is not part of the product. No command resolves it, no tool exposes
it, and a Core test fails the build if any file under `Commands/` so much as
names it; the harness reaches it by reflection through
`horizun_execute_python`, which the machine owner has to have granted first. On
a machine that has not, step 13 records `fixture_missing` — not passed, and not
a product failure.

---

## Proving it on your own machine

The harnesses under `scripts/live/` are the evidence, and they are versioned so
anyone can re-run them:

```bash
pwsh -File scripts/live/verify-dwg-all.ps1
```

Eighteen harnesses in a fixed order, preceded by an identity step and followed by one
roll-up, and a refusal to add up results that do not come from the same build. The
roll-up is then re-checked by arithmetic rather than by reading: every declared count
recomputed from that artifact's own probes, the totals recomputed as the sum of the
artifacts, and every harness blob compared against the file committed at that head. `docs/DWG-PROGRAM-STATE.json` (a private ledger, kept out of the public
repository) is generated from that roll-up by `scripts/generate-dwg-state.ps1` —
every number in it was measured by something else.

The order runs from the narrowest claim to the widest, so the first failure is
the most specific one. `redteam` is last on purpose: it claims nothing here can
be talked into the wrong answer, and a suite that ran it early would report the
narrow failures beneath it as attacks that succeeded.

Each harness also runs on its own and writes an artifact naming the candidate it
measured. A run whose harness file does not match the commit it reports says so
in that artifact rather than being quietly trusted.

---

## Shipping a change to any of this

The server and the add-in share a **contract hash** and each computes it. A
server that meets an add-in on a different build refuses every call rather than
sending an argument the far end will silently ignore — which is the right
behaviour and makes deploying one half a way to break a working setup.

So: close Revit, run `install.ps1`, and both halves move together. There is no
partial deployment and no flag that permits one.

**This phase changed the contract** — `horizun_query_cad` gained a mode, the
requirement-set schema gained rule keys — so the hash moved and an add-in left on
an older build will refuse. That is the guard working. Update both.

**This build changes stored state, by migration.** Provenance moved from v1 to
v2 — a new Extensible Storage GUID carrying the placement id, its transform and
the source path beside the fields v1 had. Revit will not let a schema gain a
field once a document holds it, so the v1 GUID is never touched and stays
readable forever: a model converted by 1.1.x–1.2.0 audits and plans against
this build with no action on your part, its records reported as
`provenance_version: "v1"`. What is rewritten, and only when you apply an
update that claims them, is the record on each claimed v1 element — it becomes
v2, the v1 entity is removed from that element after the v2 write lands, and
the apply reports the count as `migrated_from_v1`. Nothing else in a document
is rewritten, and a v1 element two placements could have built is never
rewritten at all — the whole run is refused instead, before anything is
modified.

**What a rollback costs.** Reverting the add-in to a build before this one
leaves v1 records readable and every v2 record **invisible**: the older build
looks for the v1 GUID only, so elements stamped or migrated by this build are
reported as `bim_without_source` by its audit and rebuilt by its update. That
is loud rather than silent, and it is the reason to migrate a model once and
not go back. A requirement set using `naming`, `parameters`, `allow_structural`,
`base_level`/`top_level`, or `produces: shaft | room_separator | opening` is
refused by the older loader with the unknown key named. That is a refusal rather
than a silent drop, which is the property worth having: an old build cannot
convert a new set into a model that looks plausible and is missing everything the
set was for.

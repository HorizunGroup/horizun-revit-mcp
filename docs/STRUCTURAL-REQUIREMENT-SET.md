# The structural requirement set

`horizun.structural-requirements/1` — the neutral artefact that says what
reinforcement is wanted. It is the input to `horizun_plan_reinforcement`,
`horizun_apply_reinforcement` and `horizun_audit_reinforcement`.

## What it is for

This bridge does not design. It does not choose a diameter, a spacing, a cover,
a grade, a hook angle, a lap length or a ratio — every one of those is a number
somebody is answerable for. They arrive here, declared, from a person or from a
code that person chose.

That is the whole purpose of the artefact: it is where the engineering enters,
so that the bridge below it can be a bridge. No standard is compiled in. A code
may arrive AS a requirement set, in which case the audit measures against the
numbers that set declares and reports what it found — never that a design
complies with anything.

## The shape

```json
{
  "schema": "horizun.structural-requirements/1",
  "requirement_set": { "id": "beams-b1", "version": "1.0.0", "title": "B1 beam cage" },
  "units": "millimeter",
  "tolerances": { "length_mm": 2.0, "spacing_mm": 2.0, "cover_mm": 1.0, "angle_degrees": 1.0 },

  "bar_types": [
    { "id": "T10", "type_name": "10M", "nominal_diameter_mm": 10 },
    { "id": "T20", "type_name": "20M", "nominal_diameter_mm": 20 }
  ],
  "hook_types": [
    { "id": "H135", "type_name": "Stirrup/Tie Hook - 135 deg" },
    { "id": "STRAIGHT", "none": true }
  ],

  "cover_rules": [
    { "id": "beam-cover", "host": { "category": "OST_StructuralFraming" },
      "face": "common", "cover_type_name": "Beam, Sides", "distance_mm": 40 }
  ],

  "reinforcement_rules": [
    {
      "id": "b1-stirrups",
      "host": { "element_ids": [5001] },
      "bar_type": "T10",
      "style": "stirrup_tie",
      "curve_mm": [[0,40,40],[0,260,40],[0,260,460],[0,40,460]],
      "closed": true,
      "normal": [1,0,0],
      "start": { "hook_type": "H135", "orientation": "left" },
      "end":   { "hook_type": "H135", "orientation": "right" },
      "layout": { "rule": "maximum_spacing", "spacing_mm": 200, "array_length_mm": 3800 },
      "allow_new_shape": true,
      "mark": "S1",
      "required": true
    }
  ]
}
```

## Every length is millimetres

By definition, not by declaration. A `units` field with any other value is
**refused rather than converted**. Accepting a second unit would mean every
number in every rule had to be read together with a field a hand-edited set can
lose — and feet read as millimetres is a bar 300 times too short, with nothing
in the reply looking wrong.

## What is declared and never inferred

| Field | Why it cannot be derived |
| --- | --- |
| `normal` | The direction the SET marches in. The same bar distributes along a beam or up a column depending on it, and one bar's own curves do not say which. |
| `style` | `standard` or `stirrup_tie`. A closed rectangle is a stirrup in a beam and an edge bar in a slab; the geometry does not say which. |
| `curve_mm` | Where the steel goes inside a member is a design decision. |
| `bar_type` | A bar type carries a diameter, a bend radius and a grade. |
| `allow_new_shape` | Creating a shape family puts it in the project browser, in schedules and in everybody else's model. |

`style` is **required**. It used to default to `standard`, which is a design
decision taken in silence — and this page already listed it as declared, so the
code and the document disagreed about it.

The **only** defaults in the whole schema are `include_first_bar`,
`include_last_bar` and `bars_on_normal_side` — Revit's own, echoed back into
every plan so they are visible rather than assumed.

## Layouts

A closed vocabulary of five, because those are the five Revit has:

| `rule` | needs | produces |
| --- | --- | --- |
| `single` | nothing | one bar |
| `fixed_number` | `number`, `array_length_mm` | spacing = array / (n − 1) |
| `number_with_spacing` | `number`, `spacing_mm` | array = spacing × (n − 1) |
| `maximum_spacing` | `spacing_mm`, `array_length_mm` | count rounds **up**, so no gap exceeds the maximum |
| `minimum_clear_spacing` | `spacing_mm`, `array_length_mm` | count rounds **down**; the gap is measured between bar **surfaces**, so the diameter is part of the sum |

For `minimum_clear_spacing` the diameter used is the bar type's **model**
diameter, read from the model — measured, that is the one Revit's own count uses,
and feeding it the nominal one made the plan predict a count the model would
never reproduce. `nominal_diameter_mm` on a bar type is optional and is not
required for this layout. See `ADR-003`.

The last two round in opposite directions on purpose, and that is what makes
them two arms rather than one with a sign.

**A value this layout would not use is refused, not ignored.** `fixed_number`
with a `spacing_mm` beside it is a refusal: the two can disagree, and silently
preferring one is how a set ends up at a pitch nobody asked for.

## Running the same set twice

A rule that has already built a set **in that host** is refused, by name:
`this_rule_already_built_a_set_in_this_host`. Nothing else stopped a second
deliberate run from putting a second coincident cage in the same beam — the
idempotency ledger only protects a *retry of one call*, and re-rehearsing
produces an identical plan that reads as a first-time operation. The result would
be doubled steel, doubled quantities and a duplicate mark, and coincident bars are
on the audit's published list of things it does not look for.

Delete the existing set, or give the rule a different id if a second layer is
intended.

## Ambiguity is a refusal

A `type_name` that matches two definitions in the model is **review**, never a
tie broken by whichever Revit returned first. That is a coin toss recorded as a
decision, and the bar that comes out has a diameter nobody chose.

A selector matching many hosts is **not** ambiguity — a rule applies to every
beam of a type on purpose.

## `required: false`

A rule that could not be resolved stops the whole apply, unless it says
`required: false`. Then it is reported, skipped, and the rest proceeds. This is
the one place a partial result is legitimate, and it has to be asked for.

## Containment: where the steel actually is

Every rule is measured against the host's **own boundary** — the solid Revit
holds, triangulated — and not against its bounding box. The answer is one of five
words, published on every planned row as `containment.containment`:

| word | means |
| --- | --- |
| `inside` | every sampled point of every bar SURFACE is in concrete |
| `inside_cover_violated` | in the concrete, but closer to a face than the declared cover |
| `partially_outside` | some of the bar is in the air |
| `completely_outside` | none of the centreline is in concrete |
| `not_evaluable` | the boundary could not be trusted — **not** a pass |

**The plan refuses everything that is not `inside`**, before a transaction is
opened — `bar_outside_host_solid`, `bar_partly_outside_host_solid`,
`bar_short_of_the_declared_cover`, `containment_not_evaluable`. The last two used
to pass the rehearsal and then fail the apply's own verification *after* the
commit, which is a worse place to learn it: nothing changes between those two
moments, so the refusal belongs where no steel exists yet.

The apply requires `inside` to report a verified write, and the audit raises
`bar_partially_outside_host`, `bar_outside_host`, `cover_violated` or
`containment_not_evaluable`. All three call the same code, so a plan that says a
set fits and an audit that later says it does not are disagreeing about the
**model**, not about arithmetic.

Two things the check refuses rather than approximates: a bar type whose **model
diameter** the model will not report, because without it the surface of the bar
cannot be located and only its centreline could be tested — a different question,
quietly answered; and a host with a solid Revit would not give up, because what
is left is perfectly closed and a bar in the missing part would read as outside
the member.

`fit` is still published beside it and is still a projection onto the
distribution axis. It answers "is this set too long for its host" and nothing
else; the reply says so in `this_is_a_projection`.

Two things about how the bar is modelled, stated rather than implied. A rebar has
**flat ends**, so the radius tapers to nothing over the last radius-worth of an
open bar — within one radius of an end this is a centreline test, which is the
price of not reporting every full-length bar as half a diameter out in the air. A
**closed** shape has no ends and carries its full radius all the way round. And
where a host has a curved face, its boundary here is a many-sided prism sitting
slightly INSIDE the real surface, so a bar close to that surface is reported
marginally worse than it is, never better — the reply declares this in
`boundary_is_approximated`.

## Stirrup zones

`stirrup_zone_rules` declares what a schedule declares: a profile, a direction,
and zones along it.

```jsonc
{
  "id": "B1",
  "host": { "element_ids": [1234] },
  "bar_type": "s10",
  "profile_mm": [[0,-102,48],[0,102,48],[0,102,552],[0,-102,552],[0,-102,48]],
  "closed": true,
  "along": [1, 0, 0],
  "span_mm": 6000,              // or "span": "host_length"
  "start_offset_mm": 50,
  "symmetric": false,
  "minimum_clear_between_zones_mm": 50,
  "zones": [
    { "name": "start",  "length_mm": 1000,
      "layout": { "rule": "maximum_spacing", "spacing_mm": 100,
                  "include_last_bar": false } },
    { "name": "middle",
      "layout": { "rule": "maximum_spacing", "spacing_mm": 200,
                  "include_last_bar": false } },
    { "name": "end",    "length_mm": 1000,
      "layout": { "rule": "maximum_spacing", "spacing_mm": 100 } }
  ]
}

**The zone before a boundary gives up its last bar.** Two zones that meet put a
bar on the same station, and the set refuses that
(`two_zones_put_a_bar_in_the_same_place`). The way out is the one Revit was
measured to honour: the zone BEFORE the boundary declares
`include_last_bar: false`, as the start and middle zones do above. The other
spellings are refused by the layout, by name, because they were measured NOT to
build what they say (ADR-003, item 12): `include_first_bar: false` on a
`maximum_spacing` layout (`first_bar_suppression_not_honoured` — Revit 2026 kept
the bar, twice) and both ends off in one zone (`both_end_bars_suppressed`).
`symmetric` mirrors the first zone with BOTH its ends kept, whatever the
original declared: the boundary before the mirror belongs to the middle zone's
last bar.
```

It **expands** into ordinary reinforcement rules, one per zone, named
`B1#start`, `B1#middle`, `B1#end`. That is not an implementation detail you can
ignore: those are the ids provenance records, the ids the audit matches on, and
the ids that appear in a refusal. The expansion is deterministic, which is the
only reason the audit can find what the apply wrote.

`style` defaults to `stirrup_tie` **here and nowhere else**, because the rule is
called `stirrup_zone_rules`. A zone rule wanting straight bars says `standard`.

Exactly one zone may leave `length_mm` out; that zone is the rest of the span.
`symmetric: true` mirrors the FIRST zone at the far end and requires the last
declared zone to be the remainder — a symmetric run with a declared end zone is
two statements about the same metre of beam.

Refused, by name: `more_than_one_zone_without_a_length`,
`zones_longer_than_the_span`, `remainder_zone_has_no_length_left`,
`zone_layout_longer_than_the_zone`, `two_zones_put_a_bar_in_the_same_place`,
`two_zones_put_bars_closer_than_declared`, `symmetric_conflicts_with_the_declared_zones`,
`zone_name_repeated`, `zone_length_not_positive`, `offsets_not_usable`,
`span_not_usable`, `zone_layout_refused`.

A zone rule must resolve to **one** host. The profile is declared in model
coordinates, so it is already in one member; expanding it against several would
put the same outline in all of them, in the same place in space.

### Zones that know the host's cover — `cover`

MEASURED (ADR-003 item 7): Revit keeps a hosted array at least the **host's
cover plus the bar's model radius** from each end of the host, whatever the
declaration says. A zone rule laid out in bare model coordinates therefore
declares stations Revit moves, and the apply correctly reports a model that does
not carry what was asked for — that is the state the live probe Z4 pins.

The `cover` block tells the zone planner that number:

```jsonc
"cover": { "source": "host" }                            // read at resolve time
"cover": { "source": "declared", "distance_mm": 40 }     // or stated
```

With it, the zones are laid out on the **usable span** — the host span less
`cover + bar radius` at each end — and `start_offset_mm` / `end_offset_mm` are
measured from the usable span's ends, so `start_offset_mm: 50` under a 30 mm
clamp puts the first stirrup 80 mm in. The bar radius is the **model** radius
(the one Revit counts with), the span is what `span_mm` or `span: host_length`
gives, and the profile is **not** moved by the cover: it is still the outline
at the START of the host span, in model coordinates, exactly as declared. There
is no mode that declares a profile relative to the host section.

What comes back is a **prediction**, marked `predicted_from_host_cover` on every
expanded row, and only the apply's post-commit comparison proves it: the check
`cover_prediction` holds the first bar Revit drew to the predicted station
within `tolerances.length_mm` (measured, Revit anchors the first bar), and the
last to its station within one model bar diameter — the same bound the array
check has always held. The audit re-expands the rule identically, so it compares
the model against the same stations.

Without the block nothing changes: the zones lie where they were declared, and
every existing set and every piece of live evidence reads as before.

Refused, by name: `cover_leaves_no_span` (twice the clamp reaches the host
length), `cover_needs_the_bar_diameter` (the bar type reports no model
diameter — the plan will not compute with zero), `host_cover_not_readable`
(`source: host` on a host with no common cover; set one with a cover rule or
declare the distance), `cover_not_usable`. In the set itself: `declared` needs
`distance_mm`, `host` refuses one beside it, and the words are `host` and
`declared`.

**Not live-verified.** This block was implemented and tested offline (the
arithmetic, the boundary counts at exact multiples, the refusals); the live
probe that would prove the prediction on a real host is defined in
`verify-rebar-geometry.ps1` as Z5 and has not yet been run.

## Slab and wall mats

`mat_rules` declares "top X at 150, top Y at 200" and derives the centrelines
from the host's own boundary.

```jsonc
{
  "id": "S1",
  "host": { "element_ids": [42] },
  "face_normal": [0, 0, 1],
  "components": [
    { "name": "top_x", "direction": [1,0,0], "bar_type": "t12",
      "offset_from_face_mm": 31, "end_cover_mm": 25, "side_cover_mm": 25,
      "layout": { "rule": "maximum_spacing", "spacing_mm": 150 } },
    { "name": "top_y", "direction": [0,1,0], "bar_type": "t12",
      "offset_from_face_mm": 43, "end_cover_mm": 25, "side_cover_mm": 25,
      "layout": { "rule": "maximum_spacing", "spacing_mm": 200 } }
  ]
}
```

Per component the bridge works out where the face is (the outermost plane along
`face_normal`), how long each bar is (the host's extent along `direction`, less
`end_cover_mm` at both ends), where the array runs (the extent across it, less
`side_cover_mm`), and how deep the bar sits (`offset_from_face_mm`). Because the
extents are measured along the DECLARED directions, a slab at an angle is
measured in its own directions rather than the world's.

It expands into reinforcement rules named `S1#top_x`, `S1#top_y`, exactly as
zones do.

`face_normal` is declared and never inferred: a slab has two faces and the
geometry does not say which was meant. `offset_from_face_mm` is declared and
never derived from a cover: the second layer of a mat sits under the first by an
amount that is a decision, not a measurement.

The refusal worth knowing about is `two_layers_occupy_the_same_plane`. Two
crossing layers at one depth would be built inside one another, and **nothing
else in this bridge would report it** — both sets sit inside the host, both meet
their cover, and both re-read exactly as asked. Two PARALLEL layers at one depth
are not the same mistake and are allowed.

Also refused: `bar_direction_is_not_in_the_face`, `no_room_left_along_the_bar`,
`no_room_left_across_the_array`, `component_name_repeated`,
`offset_from_face_not_usable`, `face_normal_not_usable`,
`host_boundary_not_available`, `mat_layout_refused`.

A mat rule must resolve to **one** host, for the same reason a zone rule must: it
is derived from one host's face, extents and edges.

### Mats that know about openings — `openings`

The host mesh already carries every hole — Revit's solids subtract openings —
so the bridge reads the **rings of the face** the mat sits under: the boundary
edges of the mesh triangles lying in the face plane, chained into rings, the
largest ring the outline and every other ring an opening. For each bar it then
works out the stretch along the bar where the bar's **body** (centreline ±
model radius) would be over the void.

Every decision about a hole is declared:

```jsonc
"openings": { "policy": "omit",   "minimum_size_mm": 300 }
"openings": { "policy": "trim",   "minimum_size_mm": 300, "clearance_mm": 50 }
"openings": { "policy": "ignore", "minimum_size_mm": 300 }
```

| `policy` | does |
| --- | --- |
| `omit` | every bar whose body would be over a considered opening is not built |
| `trim` | such a bar stops `clearance_mm` short of the opening on each side; each remaining stretch is built as its own bar (stretches under 1 mm are dropped and named) |
| `ignore` | the bars are built as declared and the crossings are **reported**; containment then refuses any bar really over the void, as it always did |

`minimum_size_mm` is declared, never defaulted: an opening whose **largest
dimension** (the ring's diameter) is below it is ignored and said so. A sleeve
a bar may run past and a shaft it may not are the same shape at different sizes,
and where that line sits is a decision. `clearance_mm` belongs to `trim` only;
beside `omit` or `ignore` it is refused as a value that would be ignored.

The bars affected are the ones whose **position line passes through the
opening** — a bar beside a hole, however close, is not touched. A bar whose
centreline lies exactly on the opening's edge is clear when its radius is zero
and affected when it is not, because half its body is then over the void; the
tests pin both. Under `omit` and `trim` the array is split into **runs** of
consecutive bars that share a fate, each run one Revit set named
`S1#top_x#run2`, and a trimmed run one set per stretch, `S1#top_x#run2#seg1`;
a run of one bar is `layout: single`. A component nothing touched keeps its
plain id. Every decision is reported per component in the planned row's
`openings.component`: `bars_omitted`, `bars_trimmed` with segment lengths,
`bars_crossing`, `openings_considered` / `openings_ignored` with sizes, and the
runs.

**No trimming bars and no extra edge bars are added.** What replaces the steel
an opening removed is a design decision, and the bridge leaves it to the person
who owns it. The reply says so under `no_replacement_steel`.

Without the block the mat behaves as before — **until a bar would cross a
hole**, when the rule is refused as `openings_present_and_no_policy_declared`,
naming the openings found (size along and across the bars) and the three
policies. That replaces a late `bar_partly_outside_host_solid` from containment
with an early refusal that says what to decide. A mat whose bars miss every hole
still builds as one rule per component.

After the commit the apply runs `clear_of_openings` on the bars Revit drew, by
the same arithmetic: under `omit` and `trim` no drawn position may have its body
over a considered opening, under `trim` none may stop inside the clearance, and
under `ignore` the crossings are reported and not asserted. `inside_host_solid`
still runs beside it.

Also refused: `face_loops_not_extractable` (an openings block on a face whose
rings could not be read — remove the block to build against the outer extents
and let containment decide), `openings_leave_no_bars` (the policy removes every
bar of a component).

**Not live-verified.** Implemented and tested offline against a synthetic slab
with a hole (loop extraction, the three policies, the boundary bars, the
refusals); the live probes M6 and M7 in `verify-rebar-geometry.ps1` are defined
and have not yet been run against Revit.

## What is not implemented

Recorded here rather than left to be discovered:

- **Per-face cover.** `face` accepts only `common`. Setting cover on one face
  needs a stable way to name that face across a re-issue, and this bridge does
  not have one. Per-face cover is READ by `horizun_query_structure mode=hosts`
  and is not written.
- **Free-form bars, area reinforcement, path reinforcement, fabric.** The schema
  has no way to ask for them.
- **Laps, couplers and splices.** Not in the schema and not audited; they appear
  in the audit's `not_checked` list. Stirrup ZONES are now in the schema.
- **Bar type creation.** Names must resolve to types the model already has.
- **Replacement steel around an opening.** A mat now omits or trims the bars
  an opening interrupts (see `openings`), but it does not add trimming bars or
  edge bars around the hole. What replaces the steel is a design decision.
- **Openings that are not rings of the face.** A hole is found as an inner ring
  of the face the mat sits under. A recess that does not go through, a sloped
  face whose triangles do not share one plane, or a host whose solid Revit will
  not give up, has no ring to read — with an `openings` block that is
  `face_loops_not_extractable`; without one, containment still catches a bar
  over a void after the fact.
- **A per-face cover for zones.** `cover: { source: host }` reads the host's
  COMMON cover; a host whose faces carry different covers has none and is
  refused. The clamp is applied equally at both ends of the run.
- **A profile declared relative to the host section.** `profile_mm` is model
  coordinates, and the cover block moves the zones ALONG the host only; it does
  not move the outline in from the host's faces.
- **A host whose section changes along its length.** A zone profile is one
  outline moved along the run; a haunched beam needs a rule per section.

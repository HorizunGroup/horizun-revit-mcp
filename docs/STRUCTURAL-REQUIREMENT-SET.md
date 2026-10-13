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
  "clearances": { "between_rules_mm": 25 },

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

## Hook lengths — `lengths_mm`

A hook type names the bend. How long the hook is, is a separate number Revit keeps on the BAR
TYPE, once per hook type: its **Hook Length** (`RebarBarType.GetHookLength` / `SetHookLength`),
auto-calculated unless somebody fixed it. A detail like "stirrup #3 with 135° hooks of .10" fixes it
at 100 mm, so a `hook_types` entry may declare it per bar type. **It is Revit's Hook Length, measured
the way Revit measures it - not the straight tail after the bend.** Measured on Revit 2024: a 10M
stirrup hook of 135° declared 100 mm draws a straight tail of 71.18 mm.

```json
"bar_types":  [ { "id": "N3", "type_name": "#3" } ],
"hook_types": [ { "id": "H135", "type_name": "Stirrup/Tie - 135 deg",
                  "lengths_mm": { "N3": 100 } } ]
```

The key is a `bar_types` id and the value is millimetres, greater than zero.
Refused, each with its path (`hook_types['H135'].lengths_mm['N3']`): a key no
`bar_types` entry declares, a value that is zero, negative, not finite or not a
number, `lengths_mm` on a `none: true` entry, an empty or non-object
`lengths_mm`, and two entries that give one pair of type names two different
lengths.

**This changes a TYPE.** Revit stores the hook length on the bar type, once per
hook type (`RebarBarType.SetAutoCalcHookLengths` / `SetHookLength`; the same
members in every Revit from 2023 to 2027). Every bar of that type with that
hook takes it — bars this set never built included. So:

- the plan lists every declared pair in a top-level `bar_type_changes`: the
  length the model carries now (auto-calculated or fixed), the declared one, and
  `change`. A pair already fixed at the declared length, within the length
  tolerance, is listed with `change: false`. An auto-calculated length is a
  change even when its number matches today: it follows the bend diameter, and
  the declaration is a fixed length;
- the apply switches auto-calculation off and writes the length in the same
  transaction, before any bar is built, then re-reads every declared pair from
  its bar type. A pair that does not read back fixed at the declared length
  fails the apply's verification like any other check. A bar type or hook type
  name that resolves to nothing, or to two types, is a refusal, never optional;
- the audit compares each pair once and reports `hook_length_differs` (error)
  for a different or auto-calculated length, `unreadable` when it could not be
  read.

## Which way a hook turns — judged on the drawing

`orientation` (`left` / `right`) is Revit's word, and **left** is the side `normal x tangent`, the
tangent being the bar's direction at that end. Revit reports a word back, and the word is not
always the drawing. MEASURED on Revit 2024 (anonymized regression case roof, etapa 1b): a straight bar with a 180
degree hook at both ends, normal +Y, declared `left`, matched the shape `M_01`, drew both hooks on
the left - and `GetHookOrientation` read `right`. Declared `right`, it drew them on the right and
read `right`. A bent bar of the same mat read the word it was given.

So the apply and the audit judge **the side the hook is drawn on**, measured from the centreline
Revit drew (`HookSide`: the hook's tail against `normal x tangent` at its end). The word decides
only when the drawing cannot say (no hook drawn, or a tail not displaced across the bar in its
plane). The apply's `termination_orientation_*` check carries `drawn` beside `read`; the audit's
facts carry `drawn_orientation`, and a word that disagrees with a drawing that is right is the
info finding `orientation_label_differs_from_drawing`, not an error. A hook DRAWN the other way is
`orientation_differs` whatever the word says.

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

The apply and the audit measure the bar Revit DREW, tessellated - hooks and bends included, the same
call in both. (Etapa 1b, measured: the apply used to read each drawn curve's two end points only, so a 180
degree hook became its chord, and a hook 2.04 mm inside its cover on a sloped slab end passed the apply that
the audit then flagged.) The plan measures the declared centreline, which has no hooks yet: whether a hook
keeps its cover is decided after the commit, as a hooked anchorage is.

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

**Live-verified 2026-09-26** (Revit 2026, disposable HZ_WRITE): Z5 in
`verify-rebar-geometry.ps1` passed - a zone told the host cover predicts the
stations Revit draws, and all three sets verify against the host solid.

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
is derived from one host's face, extents and edges. A face that continues into other
elements - a roof of two floors - is declared with `into` beside components that
follow the face (see `follow_face` below).

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

**Live-verified 2026-09-26** (Revit 2026, disposable HZ_WRITE): M6 (omit drops
the bars over the hole and every run verifies clear of it) and M7 (no openings
policy is refused by name, naming the hole) passed in `verify-rebar-geometry.ps1`,
19 of 19 cases. That first run also caught a create_elements defect: a beam
carries no Element.LevelId, so the level postcondition rolled back every beam
until it read the Reference Level parameter.

### Mats that follow their face — `follow_face` and `into`

A flat mat lies in ONE plane, the outermost face along `face_normal`. That is right for a
flat slab and for one uniformly sloped one, and wrong for a roof. Measured on a real roof:
two floors, a flat strip and a 6% slope meeting at a break, whose top and bottom bars run
continuously across both pieces bent at the break, each layer at its cover from the face it
lies under; and whose bars across the slope are each straight but at their OWN height, so
they cannot be one Revit array. A component with `follow_face` is laid that way, from the
host meshes the bridge already reads. Nobody casts rays.

```jsonc
{
  "id": "ROOF-TOP",
  "host": { "element_ids": [ 101 ] },       // the flat strip
  "into": [ 102 ],                           // the slope: the face continues into it
  "face_normal": [ 0, 0, 1 ],
  "components": [
    { "name": "along", "direction": [1,0,0], "bar_type": "N12", "offset_from_face_mm": 32,
      "end_cover_mm": 25, "side_cover_mm": 32, "allow_new_shape": true,
      "follow_face": { "planarity_mm": 0.5, "min_facet_mm": 100 },
      "start_hook": { "hook_type": "H180" }, "end_hook": { "hook_type": "H180" },
      "layout": { "rule": "maximum_spacing", "spacing_mm": 200 } },
    { "name": "across", "direction": [0,1,0], "bar_type": "N10", "offset_from_face_mm": 45,
      "end_cover_mm": 25, "side_cover_mm": 32, "allow_new_shape": true,
      "follow_face": { "planarity_mm": 0.5, "min_facet_mm": 100 },
      "layout": { "rule": "maximum_spacing", "spacing_mm": 200 } }
  ]
}
```

What the bridge works out, per bar station across the array:

- **The face.** The triangles of the host, and of every `into` element, whose outward normal
  points along `face_normal` (more than 89.4° from it is a side). Triangles within
  `planarity_mm` of one plane are one **facet**. The face under a station is the outermost
  facet over every stretch along the bar. A seam narrower than 0.5 mm between two triangles
  or two elements is closed; it is not a hole.
- **The centreline.** Each facet's plane is moved inward by `offset_from_face_mm` along that
  facet's OWN normal, so the cover is the cover to the face the bar lies under, on both sides
  of a break. The bar bends where two offset planes meet: in a valley the higher of the two
  governs, under a ridge the lower. That is where the inward offset of a polyhedron has its
  creases, and it is never rounded. On the measured roof, with a 40 mm offset, the top bar
  bends 1.199 mm past the break and the bottom bar 1.199 mm before it. Bent AT the break, the
  bar would sit 40 mm under one face and 39.93 under the other.
- **Bends only at real breaks.** Coplanar triangles merge into one facet, and a vertex where
  the line does not turn is removed. A bar that runs parallel to a fold and within reach of
  it takes the crease too, not just the facet it happens to be over.
- **The ends.** The face's own extent at that station, less `end_cover_mm`. A `run` window is
  measured from the face's extent along the bars and must stay inside that at every station.
- **The sets.** Stations next to each other whose polylines are the same, translated (within
  0.01 mm), with the same host and stretches, are ONE Revit set: a **run**. Across a slope
  every station has its own height, so every station is its own set; a run of one is
  `layout: single`. One run covering every bar keeps the component's plain id and its
  declared layout. Otherwise the runs are `<id>#<component>#run1`, `#run2`, ... in station
  order, with `#segN` when an opening cut a bar into stretches. The ids are deterministic,
  and the audit matches on them.
- **The host of each set.** The element most of the bar lies on hosts it. The other elements
  it lies on are its `into`, so containment is measured against their union, as for any
  `into` rule. On the roof the bars along the slope are hosted by the slope and run into the
  strip. The bars across it are hosted by whichever piece they lie on, and run into nothing.
- **Hooks.** `start_hook` and `end_hook` stay on the ends at the face's edge, and on a bent bar
  too. An end an opening cut is straight.
- **Openings.** The holes are the rings of the facing triangles that wind the other way, read
  element by element. The `openings` policy applies to them exactly as on a flat mat:
  `omit`, `trim`, `ignore`, `minimum_size_mm`, and `clear_of_openings` after the commit.

The plan reports each set under `follow_face`. It gives the first bar's polyline in its own
(along, up) frame (`polyline_uh_mm`) and the facets under it with their normals and measured
deviation. It also gives the stations the set carries, its host and into elements, and the
component's station and run counts.

What is refused, by name and with the measurement:

| code | when |
| --- | --- |
| `face_steps_under_the_bar` | two facets meet with a jump larger than `planarity_mm`, or the offset face is discontinuous (a fold that ends under the bar). A bar can bend over a fold; it cannot follow a step |
| `face_not_planar_enough_to_follow` | the bar would bend between two facets of a CURVED Revit face. That bend comes from the tessellation, not the face, and the offset is honest only to the chord tolerance. The bend's angle is reported. A mesh that says it has a curved face without saying which triangles is treated as unknown, not as planar |
| `face_facet_too_small_to_follow` | a stretch of a bent bar shorter than `min_facet_mm`, with its length |
| `no_face_under_the_bar` | a station the face's plan does not reach, or a bar that would run over a stretch with no face whose two sides are different facets |
| `no_face_to_follow` | no triangle faces along `face_normal` |
| `mat_into_needs_follow_face` | `into` beside a component without `follow_face`. A flat component is measured on its host alone, so `into` would be ignored for it |
| `follow_face_not_usable` | `planarity_mm` or `min_facet_mm` not finite and positive |

Both numbers in `follow_face` are required and never defaulted. `planarity_mm` is how far a
facet's triangles may stray from one plane and still be one facet, which is the tolerance the
face is treated as planar within. `min_facet_mm` is how short a facet under a bar may be before
bending onto it follows the triangulation instead of the face. `into` on a mat is ids only,
never the host itself, and every id must exist (`into_element_not_found`). The covers of every
element that hosts a set are held as a flat mat holds its host's: `end_cover_mm` equal to the
common cover, `side_cover_mm` at least the cover plus the radius, or the offset at least the
cover of the face it lies under.

The verification does not change. The apply and the audit compare each built bar with its
planned polyline exactly as they compare any bent bar. Containment is measured against the
host, or the union with `into`, as before.

**How the measured roof is declared** with the pieces that already exist and this:

- **The roof:** one mat rule for the top (`face_normal: [0,0,1]`) and one for the bottom
  (`[0,0,-1]`). Each is hosted by one floor, `into` the other, and every component follows
  the face. The bars along the slope become one bent set; the bars across it become one set
  on the flat strip and a single per station on the slope.
- **The ground slab (contrapiso) with holes, one centred mesh:** a flat mat on the slab with
  `openings: { policy: omit | trim, minimum_size_mm }`. A mat's array runs from side cover
  to side cover, so equal `side_cover_mm` on both sides is what centres it; `spread` makes it
  a band. `follow_face` is not needed on a flat slab.
- **The curb on the slab edge, two running bars and dowels every 200 mm hooked at the top:**
  the curb is framing or a wall. Its two running bars are a `section_rules` layer or two
  `reinforcement_rules`. The dowels are a `reinforcement_rules` entry hosted in the curb and
  declared `into` the slab, with `anchorage` in the slab, `layout: maximum_spacing 200` along
  the curb, and `end: { hook_type }` at the top.

**Live:** `scripts/live/verify-rebar-slabface.ps1`, 4/4 in Revit 2023-2027 (at 0a411522). T1 is
the roof's top, T2 its bottom with 180° hooks, T3 a variable-thickness slab (a floor type whose
one layer is variable, its far edge raised with `horizun_slab_shape`), T4 the `into` refusal.
Revit builds the bent bar, the bar hosted in one floor and running into the next keeps its
host's cover inside it, and every per-station single re-reads as planned. The acceptance
building's B4 measures the same roof: every across-bar within 0.000 mm of the face height less
its cover and offsets.

## Section rules — reinforcement declared the way a schedule declares it

`section_rules` lay bars relative to each host's own section instead of in model
coordinates: faces, counts and bar types, plus a perimeter stirrup with zones. One
rule serves every member a schedule names with one mark.

```json
"section_rules": [
  {
    "id": "V12",
    "host": { "category": "OST_StructuralFraming", "mark": "V-12" },
    "cover": { "distance_mm": 40 },
    "layers": [
      { "face": "top",    "count": 3, "bar_type": "N5", "extend": "host_length" },
      { "face": "bottom", "count": 2, "bar_type": "N5", "extend": "host_length" },
      { "name": "bottom-extra", "face": "bottom", "count": 1, "bar_type": "N4",
        "between": "bottom", "extend": { "from": "start", "run_mm": 1500 } }
    ],
    "stirrups": {
      "bar_type": "N3",
      "start": { "hook_type": "H135" }, "end": { "hook_type": "H135" },
      "cover": { "source": "host" },
      "zones": [ { "name": "all", "layout": { "rule": "maximum_spacing", "spacing_mm": 150 } } ]
    },
    "mark": "V-12"
  }
]
```

- **The example, read as a schedule would.** Three continuous top bars and two bottom ones,
  each laid the host's length less the end cover; one extra #4 bastón 1500 mm long from the
  start, in the gap BETWEEN the two bottom bars (two layers on one face over the same stretch
  are otherwise `layers_coincide`), its end cover kept. The example is run as written by
  `SectionRuleDocExampleTests`.
- **The host's frame is measured, not declared.** A member with a straight location
  line runs along it, its section's up is world Z made square to the line (so an
  inclined beam's bars sit on the slope); a member placed at a point (a column) runs
  up world Z with its facing as the section's up. Width and depth are the extents
  of the host's own solid in that frame. A solid that does not fill that box — a
  haunch, a chamfer, a section turned about its axis — is refused as
  `section_not_rectangular`; reinforce it with `reinforcement_rules`.
- **Host selectors** accept `mark` (the element's Mark, exactly) and `level` (the
  name of the level it is on) besides `category`, `type_name` and `element_ids`.
- **`source_selector` (provenance).** A host may carry the selector it had before a tool narrowed it to `element_ids` - `RequirementSetChunks.Split` writes it on every rule of every piece. It must be an object with host selector keys only (it does not nest), is NEVER used to choose elements, survives the section-rule expansion and is echoed per row by `horizun_audit_reinforcement` as `source_selector`, so an audit of a piece says where its ids came from.
- **Cover** is one form only: `{ "source": "host" }` (the host's common cover, read
  when the rule is resolved; a host with none is refused, never zero),
  `{ "distance_mm": n }`, or all five of `top_mm`, `bottom_mm`, `left_mm`,
  `right_mm`, `end_mm`.
- **Faces.** `top` and `bottom` rows span the width inside the stirrup; `left` and
  `right` place intermediate bars between the bottom and top rows; `perimeter` is the
  four corners and nothing else (`perimeter_count_is_not_four`).
- **Extent.** `host_length` (the solid's length less the end cover),
  `{ from_mm, to_mm }` measured from the host's start face, or a *bastón* laid from
  one end: `{ from: start | end, run_mm }` or `{ from, run_ratio }` (0.25 = a quarter
  of the member's MEASURED length, not the clear span), the end cover kept. Two rows on
  one face need two names (`top-start`, `top-end`), and must not overlap along the
  member: every layer on a face is spread across the same width, so a continuous pair
  and bastones on one face would coincide — refused as `layers_coincide`. Bastones laid
  BETWEEN continuous bars are a placement this build does not make. `clear_span` is refused
  (`clear_span_not_implemented`) until supports are read.
- **Extent from named axes** (stage 1c, what a schedule card states):
  `{ axis, toward, from_mm, to_mm }` - stations from the grid named `axis`, positive
  toward the grid named `toward` (`from_mm` may be negative: beyond the axis, away from
  `toward`). The start termination is at `from_mm`, the end at `to_mm`, whichever way
  the host was drawn. Section `stirrups` take the same object as `extent` (instead of
  `start_offset_mm` / `end_offset_mm`): the zones fill from `from_mm` to `to_mm`, the
  first zone at `from_mm`. The PLAN finds each name among the model's grids, exactly as
  written, and measures where the host's axis crosses it in plan; refused by name:
  `axis_not_in_model`, `axis_name_not_unique`, `axis_not_straight`,
  `axis_parallel_to_host`, `axes_coincide_on_host`, `host_not_between_axes` (the host
  does not lie between the two axes at all), `explicit_extent_outside_the_host`. A member of
  several spans modelled as several hosts of one mark takes in each host the steel that
  lies wholly in it; steel crossing from one host into the next is refused as outside,
  and a rule none of whose steel lands on any host of its mark is
  `nothing_of_the_rule_in_any_host`. A lap on a layer measured from axes is `at_mm` from
  that same axis, positive toward `toward` (it may be negative, before the axis), and turns
  with the axes like the extent does; `at_ratio` (a share of the host's length) and `crank`
  (a station from the host's start face) are refused on such a layer.
- **Laps.** `laps: [{ at_mm | at_ratio, length_mm | length_db, toward? }]` splices a layer at
  stations measured along the host from its start face (`at_ratio` a share of the
  measured length). Each splice is a declared overlap — in mm, or in bar diameters
  with the multiplier the caller chose; no code's lap length is compiled in. The
  layer becomes one piece per stretch, ids `...#<layer>#p1`, `#p2`…, each running half
  a lap past its splice. The pieces are NOT collinear: every other piece sits one bar
  diameter across, centre to centre - in contact - toward the inside of the section
  (`toward: "inside"`, the default) or, asked, the outside (`toward: "outside"`). One
  layer alternates around one line, so its laps name one direction; a mixed layer is
  refused. A moved piece that would leave the cage the bars are laid in is
  `lap_offset_does_not_fit`. The audit measures each lap at that declared distance
  across (`declared_across_mm` / `measured_across_mm` in the reply's `laps`): a
  collinear pair is `lap_insufficient`. Only the
  layer's own ends keep its `start`/`end` terminations. A lap that does not lie
  inside the bar is `lap_outside_the_bar`; two that touch are `laps_overlap`.
  `stagger` is refused: staggering is a real detail this build does not lay.
- **Between.** `between: "<layer>"` on a top or bottom layer lays it in the GAPS of an
  earlier layer of the rule on the same face: one bar at the middle of each gap, on that
  layer's line - so it has one bar fewer than that layer. Without it, two layers on one face
  over the same stretch are `layers_coincide`. The parser refuses a name that is not an
  earlier layer, another face, a side face, and a lapped or cranked layer on either side (a
  lapped piece moves across). The plan refuses by name: `between_count_not_one_per_gap`,
  `between_outside_its_layer` (it runs past the layer whose gaps it fills),
  `between_bars_do_not_fit` (half a gap, centre to centre, is less than the two bars' radii)
  and `between_layer_not_laid_in_this_host`.
- **Couplers** (L7). A splice may be a coupler instead of an overlap:
  `laps: [{ at_mm | at_ratio, coupler_type_name }]` - exactly one of `length_mm`, `length_db`
  or `coupler_type_name`, never both and never neither.
  - The pieces are **collinear** and meet at the station: piece 1 ends there, piece 2 starts
    there. Nothing moves across, so `toward` beside a coupler is refused.
  - One layer is spliced one way: couplers and overlaps in the same layer are refused.
  - Only the layer's own ends keep its terminations, so a coupled end carries no hook.
  - The coupler type is found by name in the Rebar Couplers category:
    `coupler_type_not_found` / `coupler_type_ambiguous`. No coupler family is loaded for you.
  - The apply builds both pieces, regenerates, and joins their coupled ends with
    `RebarCoupler.Create`. A coupler Revit refuses (`RebarCouplerError` other than
    `ValidationSuccessfuly` - bars not touching, a size the type does not take...) rolls the
    batch back, naming the error. A partner piece that was not built (a `required: false` row
    refused) leaves its coupler unplaced, listed in `couplers_not_placed`.
  - **Re-read** (`checks.coupler_next` on the first piece): `GetCouplerId` at its coupled end and
    `GetCouplerId(0)` on the next are the same coupler, it sits on two bars
    (`CouplerLinkTwoBars`), and its type is the declared one.
  - **Audit:** `missing_coupler` (absent from either bar, or a different coupler on each),
    `coupler_type_differs`, and an unreadable coupler is `unknown`; each splice is listed in the
    reply's `couplers`. Whether Revit judges the coupler compatible with the bars is its own to
    refuse at creation and is not re-checked (`coupler_incompatible` stays in `not_checked`).
  - **Measured (Revit 2024 and 2026):** Revit leaves the coupled pieces with a GAP inside the
    coupler (a 56 mm CPL16M left the second piece 12 mm short of the station), and no coupler
    type parameter predicts it. So a coupled end is held to what a coupled splice is: on the
    bar's declared line, inside the coupler's own body, with the declared station inside it too.
    The distance it moved is the trim: the apply reports `coupled_end_trim_mm` and takes it off
    the expected length; the audit publishes `coupled_end_seated` (info) and compares the length
    and the shape from there. An end outside the coupler, off the line, or a coupler that is not
    at the station stays a difference.
  - **Revit seats a coupler at END 0 of both bars** and reverses a bar that offers it its end 1
    (measured, both years, whichever order the two bars are given in). So the piece before the
    station is declared FROM the station back to the layer's start - the layer's start hook on its
    end, turned the other way round - and both coupled ends are end 0.
  - **Two or more couplers per layer** (stage 1b). A piece between two couplers is coupled at
    both ends: its end 0 is seated in the coupler before it, and its end 1 in the coupler after it.
    Measured (Revit 2024, `verify-rebar-placed` P6; acceptance B2 in 2025 and 2027): Revit honours
    the end 1 of a middle piece, each coupler is shared by its two neighbours, and each piece's
    length is its stretch less at most the coupler's body at each coupled end (1779.5 / 1819.5 /
    1847.0 mm built against 1791.5 / 1831.5 / 1847.0 with a 56 mm body). The trims, the seating and
    the audit are as for one coupler, end by end.
- **Stirrups** are read exactly as a stirrup zone rule (zones, offsets, `symmetric`,
  `cover`, hooks), with the outline and the run supplied by the host: the outline is
  the stirrup centreline half a diameter inside the cover, the run is the solid's
  length.
- **What it becomes.** Each host expands into ordinary rules with ids
  `<id>#h<element id>#<layer>` (the layer's `name`, or its face) and a stirrup zone
  rule `<id>#h<element id>#stirrups`, whose zones become `...#stirrups#<zone>`. Plan,
  apply, audit, provenance and quantities see only those, so everything said above
  about containment, idempotency and the audit holds for them.

## Slabs — windows, openings, ribs, drop panels

- **A mat component over part of its host.**
  - `run: { from_mm, to_mm }` limits where its bars run, along their direction, measured from the
    host's own extent. Top bars over a support line (negativos) are a top mat with a `run`
    window either side of the support.
  - `spread: { from_mm, to_mm }` limits where the array marches across the bars: a band.
  - A window that reaches inside the host's covers is `mat_window_outside_the_host`. Revit would
    clamp the bars there.
- **Openings:** `openings: { policy: omit | trim | ignore, ... }`, as above. Bars beside a hole are
  declared as ordinary `reinforcement_rules`.
- **Ribbed slabs** (aligeradas, nervadas): the joists are framing, reinforced with
  `section_rules`, and the topping is a floor with a mat.
- **Drop panels** (ábacos): a thicker floor under the slab, with its own mat.
- **A non-structural floor or wall** cannot host steel. It is refused as `host_not_structural`,
  naming the parameter.
- **A section rule whose declared cover is below the host's common cover** is refused as
  `section_cover_below_the_host_cover`. Revit holds hosted bars to the host's cover, so declare at
  least that, or read it with `cover: { source: host }`.

## Foundations

- **Footings, combined footings, pile caps and strip footings** are reinforced with `mat_rules`:
  - a bottom mat (`face_normal: [0, 0, -1]`);
  - a top mat (`[0, 0, 1]`);
  - side bars, as a mat on a side face (a horizontal normal).
- **A host with no common cover.** Its faces carry different covers, typically a strip footing
  cast against earth at the bottom.
  - The mat is held to the cover of the exposed face it lies under, read from the model.
  - A layer whose surface would sit inside it is refused as `mat_offset_below_the_face_cover`.
    Revit would otherwise move it there.
- **Foundation beams** are framing and take `section_rules`.
- **Piles: `axis: "vertical"`.** A point-placed structural foundation is framed as a vertical
  member, up its height, only when the section rule says `axis: "vertical"`. Without it, a
  point-placed host that is not a structural column is still refused (a footing is reinforced
  with mats).

## Column detailing — crossties, bayoneta, circular sections

**Crossties** (`ties` on a section rule, which must have `stirrups`):

```json
"ties": [ { "name": "x", "bar_type": "N10", "layer": "sides",
            "start": { "hook_type": "H135", "orientation": "right" },
            "end":   { "hook_type": "H90",  "orientation": "right" } } ]
```

- One single-leg tie is laid across the section at every intermediate bar of the named layer. A
  left/right layer is tied across the width; a top/bottom layer across the depth, skipping its two
  corner bars.
- The tie runs from one stirrup leg's centreline to the other, beside the stirrup: half a stirrup
  and half a tie further along the member.
- The ties follow the stirrups' zones, so confinement zones apply to the ties too.
- Hooks are declared. On the perimeter, where there is no intermediate bar, a tie is refused.
- **Measured (Revit 2024 and 2026):**
  - A two-point tie declared `left` is drawn `right` and moved half a diameter, and the apply
    reports it unverified. Declared `right`, it is built as declared.
  - Its as-declared form carries a 135 degree hooked end on along the bar by half its diameter
    (4.748 mm in 10M).
  - The audit accepts exactly that, as the info finding `hook_end_extension`: only at a hooked
    end, only along the bar, and within one bar diameter.

**Bayoneta** (`crank` on a layer: `{ at_mm, run_mm, inward_mm }`):

- From `at_mm` along the host, the bar moves `inward_mm` toward the inside of its face over
  `run_mm`, and carries on. Every number is the caller's.
- Usually it runs `into` the smaller column above.
- Refused:
  - a crank outside the bar (`crank_outside_the_bar`);
  - a crank together with laps (`crank_with_laps_not_implemented`);
  - a crank on a perimeter layer, which is four faces.

**Circular sections:**

- A host whose solid is a circular cylinder about its axis is read as circular: equal extents
  across, and every tessellated vertex at the same radius.
- `face: "ring"`, `count` ≥ 3, lays the bars evenly round, the first on +V. They sit at the
  radius less the cover, the hoop and half a bar. Each bar is its own single-bar rule
  (`...#ring#b1`...), because a set cannot march round a circle.
- Face layers on a circular section are `section_not_rectangular`. A ring on a rectangular
  section is `ring_needs_a_circular_section`.
- **A closed chain with an arc is not drawn from curves.**
  - Measured on Revit 2024 (L2): `Rebar.CreateFromCurves` fails with an internal error on a
    closed circle of two or four arcs, in either style.
  - Such rules are refused in the plan as `arc_chain_not_buildable_from_curves`: a circular
    section's stirrups without `shape_placement`, and any CLOSED `curve_mm` chain with an arc.
  - **An open chain goes to Revit** (etapa 1b). The hoop a plan details as "circle closed by a
    lap, 135 degree hooks" is declared open, running once round and on over its lap: three arcs,
    0 to 180, 180 to 360 and the lap, with `start` and `end` hooks. Measured on Revit 2024
    in an anonymized regression case: built, every set verified, audit `agrees`. What Revit does
    with any other open chain is its answer, re-read after the commit like every bar.
  - A spiral, or a hoop without hooks, can still be **placed from a rebar shape** (L7, below).

### Hoops and spirals placed from a shape — `shape_placement` (L7)

On a circular section rule's `stirrups`:

```json
"stirrups": {
  "bar_type": "N10",
  "cover": { "source": "host" },
  "shape_placement": { "shape": "M_T6", "kind": "lapped_circle" },
  "zones": [ { "name": "ends", "length_mm": 600, "layout": { "rule": "maximum_spacing", "spacing_mm": 100, "include_last_bar": false } },
             { "name": "middle", "layout": { "rule": "maximum_spacing", "spacing_mm": 200 } } ]
}
```

```json
"stirrups": {
  "bar_type": "N10",
  "start_offset_mm": 50, "end_offset_mm": 50,
  "shape_placement": { "shape": "M_SP", "kind": "spiral", "pitch_mm": 75,
                       "base_finishing_turns": 1, "top_finishing_turns": 1 },
  "zones": [ { "name": "all", "layout": { "rule": "single" } } ]
}
```

- **Keys:** `shape`, `kind` (`lapped_circle` or `spiral`), and for a spiral `pitch_mm`,
  `base_finishing_turns` and `top_finishing_turns`. Nothing is defaulted.
  - A spiral without its pitch or either turn count is refused (`required_field_missing`); a
    pitch that is not a positive number, or turns that are not whole numbers of zero or more,
    are refused by name.
  - Those three on a `lapped_circle` are refused: a value that would not be used.
  - Beside `shape_placement`, the stirrups' own `shape` or `allow_new_shape` is refused (two
    instructions about one shape), and so is a declared hook: hooks on a placed shape are not
    implemented. A hook type with `none: true` is accepted.
  - A spiral is **one zone laid out `single`**: its pitch is the placement's, so a zone spacing
    would be a second statement of it, and two spirals meeting in one plane would stack their
    finishing turns.
  - On a section that is not circular: `shape_placement_needs_a_circular_section`.
- **The shape is judged by its definition, never by its name.** A template carries
  `M_00..M_26`, `M_T1..M_T9`, `M_S1..M_S6` and `M_SP`, and nothing in a name says which is a
  lapped circle. The resolver reads `RebarShape.GetRebarShapeDefinition()`: a
  `RebarShapeDefinitionByArc` of type `LappedCircle` for `lapped_circle`, of type `Spiral` for
  `spiral`. Anything else is refused as `shape_is_not_a_circle` / `shape_is_not_a_spiral`,
  naming what the definition is (an open arc, straight segments...); a definition that cannot
  be read is `shape_definition_unreadable`, never agreement. The shape must still be allowed for
  the bar type (`shape_not_allowed_for_this_bar_type`).
- **What it becomes.**
  - A lapped circle: **one placed hoop per station** that has a bar - the zones are laid out as
    for any stirrup zone rule, and each station becomes its own single-bar rule,
    `...#stirrups#<zone>#s1`, `#s2`... A suppressed end bar leaves its station empty.
  - A spiral: **one placed spiral over its zone**, `...#stirrups#<zone>`, rising from the zone's
    start over the zone's length (with `cover`, the usable span; with offsets, inside them).
  - The hoop's centre is on the member's axis at the station, its plane square to the axis, the
    shape's x and y on the section's V and W, its CENTRELINE radius half a hoop diameter inside
    the cover - the same circle the curves version would have drawn.
  - The style is the shape's own, read from it and published (the section rule never declared one
    for it).
  - Containment, fit and anchorage measure the circle (or the helix, sampled at 36 segments a
    turn with its flat finishing turns) exactly as for any other bar.
- **How it is built** (the apply):
  - `Rebar.CreateFromRebarShape` at the corner of the hoop's box, x and y in the section plane;
    both hooks then cleared (`SetHookTypeId`), because Revit assigns hooks to a placed shape
    "arbitrarily".
  - A hoop is fitted with `RebarShapeDrivenAccessor.ScaleToBox`, whose documented box INCLUDES the
    bar: the box is the centreline diameter plus one bar.
  - A spiral is fitted with `ScaleToBoxFor3D` over the declared height, then
    `REBAR_SHAPE_SPIRAL_PITCH`, `..._BASE_FINISHING_TURNS`, `..._TOP_FINISHING_TURNS` and
    `..._HEIGHT` are set from the declaration. A parameter the bar does not carry, or Revit
    refuses, rolls the batch back by name.
  - Each placed bar is laid out `single`.
- **What the apply re-reads**, besides every check any bar gets (host, type, shape, layout,
  hooks, containment on the tessellated drawn centreline, provenance):
  - `placement_centreline`: the centreline Revit drew (`GetCenterlineCurves`, tessellated) -
    every point on the centreline radius from the axis; a hoop's in its plane at its station and
    all the way round (no gap wider than 90 degrees); a spiral's from its base to its top.
  - `placement_size_parameter`: when the shape's definition carries a diameter or radius
    constraint (`RebarShapeConstraintDiameter` / `...Radius`), that parameter read off the bar,
    against the value predicted from how it measures (centreline, external or internal). A shape
    with no such constraint is `not applicable`, and the centreline is the measurement.
  - `spiral_parameters`: pitch, height and both turn counts read back. Unread is not a pass.
- **The audit** compares a placed bar by its placement (`placed_shape_differs`), not point by
  point: a lapped hoop is longer than its circle by its lap, and its drawn points are not the
  two the expansion declares, so neither the length, the polyline nor a single bar's set normal
  is compared for it. Where round the axis the lap or the first turn starts, and the lap's
  length, are the shape's (`placed_shape_start_angle`, `placed_shape_lap_length` in
  `not_checked`).
- **Measured live:** `verify-rebar-placed` P1-P2 (lapped hoop and spiral placed from their
  shapes) pass in Revit 2023-2027; the re-reads above are what judge them.

**Arcs in `curve_mm`.** `{ "through": [x, y, z] }` between two points declares a three-point arc.

- A trailing through point closes a closed bar with an arc.
- Through points first, repeated, or on the chord are refused.
- Every measurement uses the arc sampled to a 0.05 mm sagitta. Revit is asked for the true arc.
- **A single arc between two points builds** - measured on Revit 2024, created and read back - and
  nothing else is needed for it. A CLOSED chain with an arc in it is refused; an open one is built (see above).

**Measured along the way, and corrected:**

- **The audit's length check.**
  - Revit's `TotalLength` is rounded per bar by the bar type's rounding rule (measured: 3 × 3770
    against 3 × 3767.85), and the API reports that rule in display units.
  - The audit now compares the exact centreline length (`measured.bar_centreline_length_mm` ×
    quantity).
  - A bent bar's prediction subtracts the arc Revit puts in each corner, R(2·tan(θ/2) − θ). R is
    the inside bend radius plus half the bar: an L of 500 + 1890 mm in 16M measures 2366.2 mm.

## Walls - boundary elements

A boundary element is a column-like cage inside a structural wall, at one of its ends,
up its whole height: "EB-1: 8 bars and closed stirrups in the last 600 mm of the wall".
It is declared in `wall_boundary_rules`, and it is a section rule whose section is the
end region of ONE wall.

```json
"wall_boundary_rules": [
  {
    "id": "EB1", "host": { "element_ids": [ 7 ] },
    "end": "start", "length_mm": 600,
    "cover": { "distance_mm": 40 },
    "layers": [
      { "name": "face-a", "face": "top",    "count": 4, "bar_type": "N16", "extend": "host_length" },
      { "name": "face-b", "face": "bottom", "count": 4, "bar_type": "N16", "extend": "host_length" }
    ],
    "stirrups": { "bar_type": "N10", "start": { "hook_type": "H135" }, "end": { "hook_type": "H135" },
                  "zones": [ { "name": "all", "layout": { "rule": "maximum_spacing", "spacing_mm": 100 } } ] }
  }
]
```

- **Keys.** `id`, `host`, `end`, `length_mm`, `cover`, `layers`, `stirrups`, `ties`, `mark`,
  `required`. `cover`, `layers`, `stirrups` and `ties` are exactly those of a section rule
  (cover in its three forms; layers with `extend`, `laps`, `into`, `anchorage`, `crank`).
  `axis` is not admitted.
- **`host`** is `{ "element_ids": [ one wall ] }` and nothing else. Any other selector, or
  more than one id, is refused as `value_not_in_vocabulary` at `host.element_ids`: the
  region is measured from ONE wall's end. Write one rule per wall end.
- **`end`** is `start` or `end`: the end of the wall's location line. Missing is
  `required_field_missing`; anything else `value_not_in_vocabulary`.
- **`length_mm`** is how far the region runs along the wall from that end. It is declared,
  never derived. Missing is `required_field_missing`, not a number `value_is_not_a_number`,
  zero or less `geometry_not_usable`.
- **The frame.** The wall is measured as any member with a straight location line is
  (along the line, up world Z, extents off its solid). The region's section frame is then:
  - U up the wall, from the wall's bottom to its top: layers run up it, `extend` and laps
    are measured from the wall's bottom, and the `end` cover is at the bottom and the top;
  - V along the wall, over `length_mm` from the declared end: `left` is toward the wall's
    start, `right` toward its end;
  - W across the thickness: `top` and `bottom` are the wall's two faces (`top` is the face
    to the left of the location line, looking from its start to its end).
- **What it becomes.** Exactly what a section rule becomes, with the wall as host:
  `<id>#h<wall id>#<layer>` and `<id>#h<wall id>#stirrups`, the stirrups closed round the
  region and marching up the wall.
- **Refused, by name:**
  - a region longer than the wall: `wall_boundary_longer_than_the_wall`;
  - a region shorter than its two covers along the wall, two stirrup legs and its largest
    bar: `wall_boundary_too_short_for_its_bars`, with the arithmetic;
  - a host that is not a system wall: `wall_boundary_host_is_not_a_wall`;
  - a wall whose solid is not a box along its own axes (an opening, a cut or mitred end):
    `section_not_rectangular`, as for any section rule;
  - the resolver reports each as `wall_boundary_rule_refused` with the reason.

**The row at the hooks' corner** (any section rule with a hooked stirrup) may seat 0 or 3.964 mm off
the plan: which leg the hooks' second pass lies along is Revit's tie-break (measured: identical beams in
one model came out either way, by position), and the corner bar sits in that pass's bend. That row - the
top row - carries `hook_corner_seat_mm` (half a stirrup); within it the audit says `hook_corner_seat`
(info), past it `geometry_differs`. The same holds for a hooked top row (a column bar with a 90 at
its start). A top row a `crossing_order` moved further than its seat has left the corner and carries
no seat. No other row is excused.

**Where the hooks of the stirrup go.** The stirrup starts - and Revit lays its two hooks - at the
top corner against the wall's END face, at either end. Measured on Revit 2024 and 2026: with the
hooks' corner inside the wall (one leg free of any face) Revit moved the hooks' second pass half a
stirrup along the free leg, and the corner bars with it, 3.96 mm off the plan.

**A mat through the boundary's stirrups is now caught.** Measured: a mat's horizontals laid
through the boundary stirrups were pushed out by Revit until they touched them (the array 12.5 mm
short). Alone on the wall, the same side cover was honoured to the millimetre. The plan now refuses
such a mat and the stirrups it crosses as `bars_intersect`, and the audit reports
`overlapping_bar`. See "Bars of different rules" below. Keep the mat clear of the boundary's
stirrups, or declare `clearances` to hold it further off.

**The body of the wall** takes no new schema:

- **Two faces** - a retaining wall, a tank wall: one `mat_rules` entry per face, with
  `face_normal` the wall's own normal and its opposite. Each mat's layers are offset from
  its own face, so the two meshes stand apart across the thickness.
- **Starter bars from the footing** are `reinforcement_rules` (or section layers) with
  `into: [footing id]` and `anchorage`, as for a column (see below).
- **Hooks at the ends of a mat's bars**: `start_hook` and `end_hook` on a mat component,
  each `{ hook_type, orientation }` with a declared hook type, as on any rule. Only an end
  at the host's edge carries the hook; a bar an opening trims keeps the hooks on its
  outer ends, and the ends at the opening are straight. A horizontal bar's hook turns in
  the bar's own plane, across the thickness, so a 180 degree standard hook returns it
  toward the other face. A return that runs the full thickness to the other face (a
  U-bar, a hairpin) is not a hook: declare it as `reinforcement_rules` with its curve.

## Stairs (L6)

A stair is reinforced through the elements it is made of:

- **The waist** is a structural floor sloped with `horizun_slab_shape`. A mat under its soffit
  declares `face_normal` along the slope and its components run along and across it.
- **Each landing** is a structural floor with its own mat.
- **A mat is measured on its face.** It is laid in the plane of its face, and its extents are
  taken over that face whenever the face is the host's only plane facing that way. Measured on
  Revit 2024: on a waist with vertical end cuts the solid reaches past the face along the slope,
  and the first bar was laid outside. A host with a step keeps the whole solid, so containment
  refuses a bar over the lower step.
- **A stair element as a host follows Revit.** Measured on Revit 2024 and 2026: Revit accepts a
  cast-in-place stair, and a bar inside its solid is planned and contained like any other. A
  stair Revit refuses is `stair_not_a_rebar_host`, with the way out above.

## Holding the ends where the plan put them — `hold_ends` (stage 1b)

```json
{ "schema": "horizun.structural-requirements/1", "hold_ends": true, "reinforcement_rules": [ { "id": "U-top", "hold_ends": false, ... } ] }
```

- **What Revit does.** A bar that runs into another element is constrained, handle by handle, to the
  cover of the elements around it, and Revit pulls its ends there - measured on a real model up to
  15 mm on beam bars anchored in columns. The bar is then not where the plan put it.
- **What `hold_ends` does.** Off unless asked. `hold_ends: true` at the root applies to every rule;
  a rule's own `hold_ends` overrides the set's. For each bar it holds, before the commit, the apply:
  - measures the drawn centreline against the planned one, vertex by vertex
    (`HoldEndsRules.Deviation`; a bar with another number of vertices cannot be compared and is
    not verified);
  - leaves alone a bar already within 1 mm (`HoldEndsRules.ToleranceMm`);
  - otherwise re-pins every handle to a `FixedDistanceToHostFace` constraint and steps each
    distance (e, -e, e/2, -e/2, 2e, -2e; up to 8 rounds) while the summed deviation falls.
  - Closed bars, coupled pieces and placed shapes are not held (`applicable: false`): their ends
    are not free.
  - A bar with arcs in its centreline (an open lapped hoop, etapa 1b) is drawn tessellated, so its
    vertices have no one-to-one counterpart and it would not verify. The anonymized regression case declares its hoops
    `hold_ends: false` under a root `hold_ends: true`; they are judged by containment and the audit.
- **What it verifies.** The apply re-reads every held bar AFTER the commit and reports
  `checks.hold_ends`: `before_mm`, `after_mm`, `after_commit_mm`, `handles_switched_to_fixed`,
  `distance_steps`, `verified`. A bar more than 1 mm off at its worst vertex fails the apply's
  verification; nothing is assumed.
- **Constraint API.** Revit 2023 and 2024 use `GetConstraintCandidatesForHandle(handle)` and
  `SetPreferredConstraintForHandle`; Revit 2025 onward the target-element overload and
  `SetPreferredConstraint` (the old pair is obsolete in 2025).
- **When Revit moves a bar (stage 1c, measured on Revit 2024 and read typed).** Revit pins every
  handle of a new bar to a face of its host OR of an element joined to it (the elements a rule runs
  `into`). A handle declared INSIDE the cover zone of such a face - between the face and its cover
  plane - gets a `ToCover` constraint and is moved onto the cover plane. A handle declared anywhere
  else gets `FixedDistanceToHostFace` and stays. The face is often one the joined element buries, so
  the steel is in the concrete and the plan's containment does not refuse it. In an anonymized regression case:
  - a `COLUMN-A` bar (hosted in a round column) has its vertical edge inside the cover zone of
    the roof slab's edge face (the slab runs into the column). Revit pins that edge `ToCover` on the
    slab and moves the bar in its plane;
  - a `BEAM-B` bar has its legs inside the cover zone of the round columns it enters and
    its tips inside the cover zone of the slab's underside. Revit pins the legs `ToCover` on the columns
    and the tips `ToCover` on the slab, and moves the bar.

  `hold_ends` brings them back to 0.000 and 0.090 mm. The audit then `agrees`, and every handle is
  `FixedDistanceToHostFace`. In stage 1b these bars were refused earlier by `overlapping_bar` (they
  cross other steel), so nothing was built that could be moved. Built alone, they move as they did in
  stage 1. A bar declared short of every cover plane is never moved, which is why the stage-1b synthetic
  fixture (ends 6 mm short of the column's cover) never reproduced it.
- **Reading the constraints.** `horizun_query_structure` mode=rebar with `include_constraints: true`
  gives, per handle, the current and the preferred constraint: its type, target element, face kind, face
  plane and the distance it owns. A summary names the handles pinned to an element other than the host.
- **With or without `hold_ends`, the apply compares the drawn bar with its plan (stage 1c).** Every built
  bar is judged by `checks.geometry_matches_the_plan`, with the audit's own rule (see *What the apply
  verifies about a bar's geometry* below). A bar Revit moved off its plan is NOT verified: the transaction
  commits, the reply is a failure, and the row names the move. When Revit holds handles of that bar to a
  cover or to another element and the set did not ask for `hold_ends`, the `why` says so and
  `evidence.handles_to_cover` counts them.
- **Measured:** `verify-rebar-holdends`. A beam U-bar has its legs declared 6 mm inside the cover zone of
  the face of the column the beam enters, with the cover read from the host.
  - E1: without `hold_ends`, Revit moves it 6 mm and its four legs are `ToCover` on the columns. The
    apply reports both U-bars not verified (`geometry_matches_the_plan` false, naming `hold_ends`) and
    the control bar verified.
  - E4: the same bar 6 mm short of the cover plane is not moved, and no leg is pinned to a cover.
  - E2/E3: with `hold_ends`, before 6 mm, after the commit 0 mm, and the audit agrees.

  Live on Revit 2024 at stage 1c (the five-year matrix: CHANGELOG). Stage 1b: E2/E3 in all five years,
  acceptance B1 in 2025 and 2027 (0.000 mm).

### What the apply verifies about a bar's geometry (stage 1c)

After the commit, the apply re-reads each bar and reports `checks.geometry_matches_the_plan`. The comparison is
the audit's, from one function (`RebarAuditRules.CompareBarGeometry`), on the same reading of the drawn bar
(`RebarFacts`) and with the same tolerance:

- **Coupled ends:** a coupled end Revit seats short of its station, inside the coupler's body, is moved there
  (`coupled_end_seated`).
- **Total length:** for a bar with no hooks, the length of the drawn centreline times the quantity, against the
  declared length less R(2 tan(θ/2) − θ) for each bend Revit puts in a corner the plan draws sharp. R is the
  centreline radius: half the bar type's bend diameter plus half the bar.
- **Centreline:** the drawn centreline against the planned polyline, point by point and both ways, with the bend
  allowance in each corner. A reversed bar is `geometry_reversed`, and a bar in another plane is `plane_differs`.

`verified` is true only when nothing differs, nothing was unreadable, and every note is one the audit also accepts
as info: `coupled_end_seated`, `hook_end_extension`, `hook_corner_opening` or `hook_corner_seat`. An unknown note
is not a pass. Each check also carries:

- `worst_mm`: the worst distance between the two centrelines;
- `tolerance_mm` and `allowance_mm`;
- `notes_accepted` and `findings`;
- `why`;
- `evidence` (`handles_to_cover`, `handles_on_other_elements`, `other_elements`): how Revit holds the bar's
  handles, read from its constraints.

A placed hoop or spiral is judged by `placement_centreline`, as in the audit. Before stage 1c the apply asserted
length only on straight bars with no hooks, and compared no centreline. A bar Revit moved off its plan
(anonymized regression case COLUMN-A, Revit 2024) came back `verified`, and only the audit reported it.

## Steel that crosses elements — `into` and `anchorage`

A dowel runs from the footing into the column; a beam bar is anchored in the column;
a column bar is lapped into the storey above. Each is hosted in ONE element and runs
into others. `into` names those others, by element id, and `anchorage` says how far
the bars must run inside each one. Both are declared, never searched for: which element
a bar is embedded in, and how deep, is somebody's design decision.

```json
"section_rules": [
  {
    "id": "C1", "host": { "element_ids": [ 7 ] }, "cover": { "distance_mm": 40 },
    "layers": [
      { "name": "main", "face": "perimeter", "count": 4, "bar_type": "N5",
        "extend": { "from_mm": -500, "to_mm": 3700 },
        "into": [ 50, 8 ],
        "laps": [ { "at_mm": 300, "length_mm": 600 } ],
        "anchorage": [ { "element_id": 50, "min_length_mm": 450, "end": "start" },
                       { "element_id": 8,  "min_length_mm": 650, "end": "end" } ] }
    ]
  }
]
```

Here column 7 stands on footing 50 under column 8. The layer runs 500 mm down into
the footing and 700 mm up into the column above. The lap at 300 mm splits it into a
dowel, `#p1` (from −500 to 600), and the column bar, `#p2` (from 0 to 3700), which sits
one diameter in. The lap is measured as any other lap is (`lap_insufficient`), so the
dowel is coordinated with the column bar by construction.

- **`into: [ids]`** is accepted on `reinforcement_rules` and on section layers.
  - Ids only, at least one, none repeated, never the host itself (`into_names_the_host`).
  - An id the document does not have is `into_element_not_found`.
  - A rule whose host selector matches more than one element is refused as
    `into_needs_a_single_host`, because the ids name one host's neighbours. Write one
    rule per host.
- **What it changes: containment is measured against the union.** The bars are
  measured against the host AND the declared elements together.
  - A face two of them share is concrete on both sides, so it is not a boundary: a dowel
    crossing the top of the footing is inside.
  - A face two of them have in the same plane facing the same way (a beam flush with a
    column face) IS a boundary, because there is air on that side of both.
  - The cover held is the smallest cover any of the elements declares.
    - A section rule declares its host's cover itself (`cover`), and that counts like a cover rule: a
      column at 40 mm running into a footing whose cover rule says 75 is held to 40, not 75. It lowers
      what the cover rules declare; with no cover rule for any of the elements, the bars are held to no
      cover, as a host measured alone is.
  - The plan, the apply's post-commit check and the audit all measure the same union and
    say so in `measured_against` (host first).
  - **Joined elements need not be convex.** In a real model the elements are joined: a
    beam cut by a round column, a footing notched where a foundation beam sits. Such an
    element used to be refused as `into_union_not_measurable`; it is now measured.
    - Inside-ness is each element's winding number, convex or not.
    - The face two elements share is clipped out against the convex ones, exactly as
      before. A union of convex elements is measured exactly as it always was.
    - Across a joint with a non-convex element, the distance to air is measured three
      ways, cheapest first. The nearest face point, when the point 0.001 mm beyond it is
      air: exact. Otherwise the nearest face point that IS beside air: exact where the
      air is an edge two elements make. And 2562 rays (an icosphere subdivided four
      times, turned off the axes), each followed through every element's triangles to
      where it leaves the union, for air nearer than that. A ray's exit is multiplied by
      the cosine of the set's covering angle, 2.73 degrees, so a clearance from the
      rays is never more than the true one and short of it by at most 0.11 % of itself:
      0.06 mm at 50 mm, 0.11 mm at 100 mm, 0.17 mm at 150 mm. That is twelve times under
      the 2 mm length tolerance at a 150 mm cover. The bound holds where the air beyond
      the nearest point is not narrower than the rays are apart there.
    - Revit triangulates each element on its own, so two curved faces that coincide in
      the model become two polygons that do not, with slivers between them no thicker
      than the chord tolerance. A gap between two elements whose middle lies within the
      chord tolerance of both, with the two on opposite sides of it, is a **seam**, not
      air. Elements with no curved face get 0.01 mm. The width used is published.
    - The answer says so: `how_measured` names the method and the numbers, and the set's
      `containment.joints` carries `rays`, `ray_covering_angle_deg`,
      `clearance_bound_mm`, `seam_width_mm` and how many samples came from the rays.
    - An element whose boundary is not closed is still refused by name, and the refusal
      now carries the element id and its diagnosis (usable triangles, zero-area
      triangles, edges not used once in each direction).
- **Extents that leave the host.** A section layer that declares `into` may have
  `from_mm` below 0 and `to_mm` past the host's measured length. Without `into`, both
  are refused as before.
- **`anchorage: [{ element_id, min_length_mm }]`.** On a section layer it also takes
  `end: start | end`.
  - The element must be one named in `into`.
  - The length is the length of the bar's centreline inside that element's solid,
    clipped exactly: against its face planes when the element is convex, and otherwise
    cut at every crossing of its triangles with each piece judged by the winding number
    at its middle. The set is anchored as well as its least-anchored bar.
  - On a lapped layer only the piece at the declared `end` carries the check.
  - **Plan:** measures the declared centreline, which has no hooks yet.
    - Short is refused as `anchorage_short`.
    - Short with a hook declared is `decided_after_commit`, because the length Revit
      gives a hook is only known once the bar is drawn.
  - **Apply and audit:** measure the bar Revit drew, hooks included.
    - The apply reports `checks.anchorage` per set.
    - The audit reports `anchorage_short`, with the measured and declared lengths.
  - An anchorage that cannot be measured is `anchorage_not_measured` and is never `met`.

## Marks and the bending schedule

**One mark, one bar.** A schedule line is a mark, and several sets carrying one
mark is how a schedule is meant to work: Revit numbers identical bars with the same
Rebar Number itself. So the audit no longer reports a shared mark as such. It
groups every bar set in the audited hosts by schedule mark and compares each set
with the first one of its mark:

- **bar type** (by id) and **shape** (by id) - exact;
- **one bar's length** - `measured.bar_centreline_length_mm`, or Revit's
  `total_length_mm / quantity` when the centreline length was not published -
  within the set's `tolerances.length_mm`.

The same bar under one mark is not a finding; those marks are listed in the
audit's `summary.marks_shared`. A different bar under one mark is
**`bar_mark_conflict`** (error), with `differences` naming every property that
differs, the two sets and both values, e.g. `set 2 differs from set 1 in
bar_type: '12M' (id 300) against '16M' (id 301)`. When a property could not be
read on one of the sets and nothing was proved different, the finding is
`unreadable` (about `bar_mark_conflict`, severity `unknown`) - never "the same
bar". Blank marks are not compared. `bar_mark_conflict` replaced
`bar_mark_duplicate`, which reported every shared mark as an error.

**The bending schedule (cartilla), derived from the model.**
`horizun_query_structure mode=bending_schedule` reads every rebar set **and every
bar of an area or path reinforcement system** in scope (`element_ids`, else the
document; then `host_id` - see "Steel in scope" below) and returns:

```json
{
  "mode": "bending_schedule",
  "derived_from_model": true,
  "rows": [{
    "mark": "A1", "mark_missing": false,
    "bar_type": { "id": 300, "name": "12M" }, "nominal_diameter_mm": 12.0,
    "shape": { "id": 400, "name": "M_11" },
    "segments_mm": [1000.0, 2000.0], "segments_consistent": true,
    "hooks": { "start": null, "end": "Standard - 90 deg." },
    "bars": 10, "one_bar_length_mm": 3000.0, "one_bar_length_source": "bar_centreline_length_mm",
    "total_length_m": 30.0, "total_kg": 26.69,
    "sets": 2, "rebar_ids": [1, 2], "host_ids": [5001, 5002],
    "mark_conflict": false, "systems": [], "complete": true
  }],
  "mark_conflicts": [],
  "totals_by_diameter": [{ "nominal_diameter_mm": 12.0, "sets": 2, "bars": 10, "total_length_m": 30.0, "total_kg": 26.69 }],
  "totals_by_host": [{ "host_id": 5001, "sets": 1, "bars": 5, "total_length_m": 15.0, "total_kg": 13.345 }],
  "totals": { "sets": 2, "bars": 10, "total_length_m": 30.0, "total_kg": 26.69 },
  "fabric": { "rows": [], "totals": { "sheets": 0, "wires": 0, "total_kg": 0.0, "complete": true } },
  "totals_with_fabric": { "total_kg": 26.69, "complete": true },
  "density": { "kg_per_m3": 7850, "source": "..." },
  "not_included": [{ "what": "linked_models", "why": "..." }],
  "scope": { "bar_sets_in_scope": 2, "system_bars_in_scope": 0, "system_ids_expanded": [],
             "fabric_in_scope": 0, "fabric_area_ids_expanded": [], "ids_not_read": [],
             "element_ids_given": 0, "host_id": null },
  "coverage": { "coverage": "complete", "reasons": [] }
}
```

- **`density_kg_per_m3` and `density_source` are required**: without them the
  call is refused as `density_required` (or `density_not_usable`,
  `density_without_a_source`) before a bar is read. There is no default steel.
- **`length_tolerance_mm`** (optional, default 0 = exact at the 0.001 mm the
  reader publishes) is how far one bar's length may differ between sets that share
  a mark and still be one line. Bar type and shape are always exact.
- **Segments** are the legs of the bar, read off
  `geometry.centreline_points_as_declared_mm` (hooks and bend radii suppressed),
  with consecutive collinear points merged. Hooks are named per end, not
  dimensioned.
- **Kilograms** are Revit's measured volume times the declared density - the same
  volume `mode=quantities` reports - not a nominal mass per metre.
- **A mark over different bars is split** into one line per bar, each with
  `mark_conflict: true`, and named in `mark_conflicts` - never averaged into one
  line. Bars with no mark are a line of their own with `mark_missing: true`.
- **A value the model would not report is never a zero**: the total is null, the
  `_counted` number beside it is what could be read, and `complete` is false.
- **Area and path bars are scheduled.** A line whose bars came from a system
  names it in `systems` (`[{ "kind": "area" | "path", "id": 900 }]`, empty for a
  line of rebar sets only). They sit under their own mark, or on the
  `mark_missing` line when Revit gave them none.
- **Fabric is scheduled by sheet type**, in its own `fabric` block (a sheet is not a bent
  bar with legs): one line per sheet type with its fabric numbers, sheets, wires, the
  wires' length and steel, `total_kg` at the declared density and Revit's
  `revit_cut_sheet_mass_kg` beside it; `totals_with_fabric.total_kg` is the bars' plus the
  fabric's. A sheet that would not read makes the schedule incomplete.
- **`not_included`** names what the schedule leaves out: linked models, hook
  dimensions, fabrication cut lengths, and the per-bar lengths of a variable set
  (one bar's length is the first bar's).

**Steel in scope - `mode=quantities`, `mode=reconcile`, `mode=bending_schedule`.**
The three modes read ONE scope (`RebarScope`, Core) and aggregate the same rows, so
they cannot disagree about which steel they counted:

- **Rebar sets** (`Rebar`), described by `RebarFacts`, with `"system": null`.
- **The bars of area and path reinforcement** (`RebarInSystem`), described in the
  same row shape - bar type with both diameters, shape, quantity, Revit's own total
  length and volume, one bar's centreline, schedule mark, host - plus
  `"system": { "kind": "area" | "path", "id": <system element id> }`. Before this
  they were silently left out of all three modes while the coverage said
  `complete`. The takeoff counts them per group and in total as
  `sets_from_systems`.
- **`element_ids`** may name sets, system bars, or an **area or path reinforcement
  element**, which scopes to its bars (`scope.system_ids_expanded`); a bar named
  both ways is read once. An id that is not reinforcement (a floor, a beam) or not in
  the document is not read and is named in `scope.ids_not_read` and in
  `coverage.reasons` (`element_ids`) - the coverage is `partial`. To take off what a
  host carries, pass `host_id`.
- **`host_id`** keeps the sets and system bars whose host is that element. A bar
  whose host would not read is a `host` reason, not silently dropped.
- **Fabric sheets** (stage 1c) are READ, one row per sheet, marked
  `"system": { "kind": "fabric", "id": <fabric area id, or null for a sheet placed alone> }`.
  A sheet is welded wire, so its bars are its WIRES as placed: `measured.quantity` is the
  number of wire centrelines Revit returns for the sheet (`FabricSheet.GetWireCenterlines`,
  each direction, already cut by the host and the area), `total_length_mm` their summed
  length, and `volume_m3` their steel - each wire's length times pi d^2 / 4 of its wire
  type's diameter. The declared density weighs it as it weighs every bar.
  - The row's `fabric` block carries the sheet type, the fabric number (its
    `schedule_mark`), each direction's wire type, diameter, wires, length and volume,
    the cut dimensions, and **Revit's own masses** - `revit_cut_sheet_mass_kg`
    (`FabricSheet.CutSheetMass`), `revit_type_sheet_mass_kg` and
    `revit_type_mass_per_area_kg_m2`. They are published to check the takeoff against,
    never summed into it: they are Revit's arithmetic, with Revit's density.
  - `bar_type` is the sheet type; its `nominal_diameter_mm` is the wire diameter only
    when both directions share one, else null with `diameter_why`.
  - A **fabric area** in `element_ids` scopes to its sheets
    (`scope.fabric_area_ids_expanded`); a sheet named both ways is read once.
  - The takeoff counts fabric in `fabric_sheets` (not in `sets_from_systems`), and each
    group or total holding fabric carries `fabric: { sheets, wires, volume_m3_counted,
    mass_kg_counted, revit_cut_sheet_mass_kg }`.
  - Coverage: fabric in scope is no longer a reason by itself. A sheet whose wires,
    wire diameter or type would not read is named (`fabric`) and the coverage is
    `partial`; a fabric area that would not list its sheets likewise. Read in full, the
    coverage is `complete`.
- `mode=reconcile` compares the takeoff above with the schedule passed in, so system
  bars count under their mark (a mark only the model carries is
  `not_in_schedule`); its `model_scope` and `model_coverage` are the takeoff's.

## Area reinforcement, path reinforcement and fabric — `area_rules`, `path_rules`, `fabric_rules`

Three Revit systems that take an outline or a path on ONE face of ONE floor or wall and a
handful of numbers, and then lay the bars (or sheets) themselves. Every number is declared;
nothing is defaulted. **Offline so far: built against the API of all five years, tested at a
desk, not yet measured in a live Revit.**

```json
"area_rules": [ {
  "id": "S1-area", "host": { "element_ids": [ 501 ] },
  "boundary_mm": [[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,200]],
  "major_direction": [1,0,0],
  "area_type_name": "Area Reinforcement 1",
  "layers": {
    "top_major":    { "bar_type": "T10", "spacing_mm": 200 },
    "bottom_major": { "bar_type": "T12", "spacing_mm": 150 }
  },
  "mark": "AR1"
} ],
"path_rules": [ {
  "id": "S1-edge", "host": { "element_ids": [ 501 ] },
  "path_mm": [[0,0,200],[4000,0,200]],
  "path_type_name": "Path Reinforcement 1", "flip": false,
  "primary": { "bar_type": "T12", "spacing_mm": 200, "length_mm": 1200 }
} ],
"fabric_rules": [ {
  "id": "S1-mesh", "host": { "element_ids": [ 501 ] },
  "boundary_mm": [[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,200]],
  "major_direction": [1,0,0], "major_direction_origin_mm": [0,0,200],
  "fabric_area_type_name": "Fabric Area 1", "fabric_sheet_type_name": "A193",
  "cover_offset_mm": 10
} ]
```

**What the parser holds every rule to** (a refusal names the field, as everywhere else):

- Keys are strict, at every level: the rule, `layers`, each layer and `primary`.
- `host` names exactly ONE element by id. The outline is model coordinates on that host;
  the same outline on another host would be somewhere else on it, or off it.
- Rule ids share one namespace with every other rule of the set (provenance records the
  id alone).
- `boundary_mm`: at least three points, no point repeated (closure is implied — do not
  write the first point again), every point within `tolerances.length_mm` of one plane,
  enclosing an area, and no edge crossing another.
- `path_mm`: at least two points, open, no point repeated, in one plane.
- `major_direction` lies in the plane of the outline (within `tolerances.angle_degrees`).
  `major_direction_origin_mm` (fabric) lies in that plane (within `length_mm`). The
  origin is declared, not taken from the outline: the sheets are laid out from it.
- `layers` names at least one layer. A floor's are `top_major`, `top_minor`,
  `bottom_major`, `bottom_minor`; a wall's are `exterior_major`, `exterior_minor`,
  `interior_major`, `interior_minor`. One rule does not mix the two families: which face
  of a wall is its "top" is not translated. Each layer is `{ bar_type, spacing_mm }`, the
  bar type an id from `bar_types`, the spacing greater than zero.
- `flip` is a boolean, declared: which side of the path the bars go is not in the polyline.
- `primary` is `{ bar_type, spacing_mm, length_mm }`, all declared.
- `cover_offset_mm` (fabric) is optional and not below zero. Absent, Revit's own value
  stands and is reported, not compared.
- `required` is a boolean when given (default true, as for every rule); `mark` optional.

**What the plan resolves** (rows in the plan's new `area`, `path` and `fabric` arrays,
beside `reinforcement`; codes published in `refusal_codes`):

- The host exists (`host_not_found`), is a floor or a wall
  (`system_host_not_a_floor_or_wall` — a Part is a valid Revit host and is not resolved
  here), and Revit accepts it as a reinforcement host (`host_ineligible`,
  `host_eligibility_unreadable`).
- The layer words match the host: floor words on a floor, wall words on a wall
  (`area_layer_not_on_this_host`).
- Every type name resolves to exactly one type: `area_reinforcement_type_*`,
  `path_reinforcement_type_*`, `fabric_area_type_*`, `fabric_sheet_type_*`, `bar_type_*`,
  each `_not_found` or `_ambiguous`. Two types with one name are refused, never picked.
- The outline's corners (or the path's points) lie on ONE face of the host's own boundary:
  in its plane and inside it, within the length tolerance
  (`boundary_not_on_a_host_face`, `boundary_outside_the_host_face`). The host's
  triangulated boundary is the one containment already uses (`host_extent_not_measured`
  when Revit will not give it). The major direction lies in that face
  (`major_direction_not_in_the_host_face`).
- A rule that already built a system on that host is refused on the write path
  (`this_rule_already_built_a_set_in_this_host`).

**What the apply builds and proves.** In the same single transaction, after the rebar sets:

- Area: `AreaReinforcement.Create` over the outline, then every layer of the host's family —
  the declared ones active with their bar type (`REBAR_SYSTEM_BAR_TYPE_*`) and spacing
  (`REBAR_SYSTEM_SPACING_*`), the others INACTIVE. No hooks: this schema declares none, and
  the system is created with `InvalidElementId`, Revit's "no hooks".
- Path: `PathReinforcement.Create` with the declared `flip`, then `PATH_REIN_TYPE_1`,
  `PATH_REIN_SPACING`, `PATH_REIN_LENGTH_1`, and `PATH_REIN_ALTERNATING` = 0 (only primary
  bars were declared). No hooks.
- Fabric: `FabricArea.Create` with the declared direction, origin and types, and
  `CoverOffset` when declared (`IsCoverOffsetValid` first).
- `mark` into the element's Mark.
- Provenance on the system element, by the same store as bars (`layout_rule` names the
  kind; `expected_quantity` is 0, because the count is Revit's).
- A parameter Revit will not take, or a creation that throws, rolls the whole batch back.

After the commit every system is re-read (`system_verification`): host, type, every layer
slot (active or not, bar type, spacing, hook), the major direction (as an axis), the
path's bar type, spacing, length, hooks and alternating flag, the fabric's sheet type,
origin and cover offset, the mark — and that the system produced **at least one
RebarInSystem** (fabric: at least one sheet). `verified` is true only when every check is;
otherwise the apply reports failure and claims nothing.

**Their steel is in the takeoff.** The bars an area or path system lays are read by
`horizun_query_structure mode=quantities`, `mode=bending_schedule` and `mode=reconcile`
like rebar sets, marked `system: { kind, id }`; the system's id in `element_ids` scopes to
its bars. Fabric sheets are read too - their wires counted, measured and weighed with the
declared density, Revit's own sheet mass beside them - and a fabric area's id scopes to
its sheets. See "Steel in scope" under the bending schedule.

**What the audit compares.** Systems are matched by provenance (rule id and host), and
every parameter above is read back and compared, as `system_parameter_differs` (or
`unreadable` when Revit will not say). A rule with no system is `rule_built_nothing`; two
systems recording one rule are reported, the lowest id audited. System rule ids are known
to the orphan check.

**What is NOT compared** — named in the audit's `not_checked`:

- `area_reinforcement_geometry`, `path_reinforcement_geometry`: where Revit laid each bar,
  whether it stays inside the host and keeps its cover, and where the outline's EDGES run
  (only its corners are held to the face, so an edge across a notch or an opening is not
  caught).
- `fabric_sheet_layout`: the sheets a fabric area cuts, and their positions.
- `path_reinforcement_flip`: the side the bars went to is sent as declared and not read back.
- `area_reinforcement_layout_rule`: read and reported (`layout_rule_read`), not set or
  compared; the spacing read back is compared whatever the rule is.

**Not in the schema yet:** hooks on area and path layers, alternating path bars, openings
inside a fabric outline (Revit takes inner loops; the schema has one outline), bent
fabric sheets, single placed `FabricSheet`s, and Part hosts.

## Cover on one face — `face: { normal }` (L7)

A cover rule's `face` is `"common"` (or absent) or an object naming the faces by their
**outward normal**:

```json
"cover_rules": [
  { "id": "footing-bottom", "host": { "category": "OST_StructuralFoundation" },
    "face": { "normal": [0, 0, -1] }, "cover_type_name": "Cast against earth", "distance_mm": 75 }
]
```

- **Every exposed planar face of the host** whose outward normal is within
  `tolerances.angle_degrees` of the declared one gets the cover type - read through
  `RebarHostData.GetExposedFaces`, set with `SetCoverType`. A face is never named by a word
  like `top` (a slab has two faces, a sloped one is neither) and never by a Revit reference,
  which does not survive a re-issue; the normal does, and it is how mats already name their face.
- A host with no such face is refused as `cover_face_not_found`, listing the normals of the
  exposed planar faces it has. A curved exposed face has no single normal and never matches.
- The faces are found again inside the transaction by the same normal; a different count than
  the rehearsal found rolls the batch back.
- **Re-read:** every matched face's `GetCoverType` is the declared type. The plan shows
  `face.faces_matched` and the current cover of those faces; the audit reports `cover_differs`
  when not every face carries it.
- A per-face rule is not the host's one number: containment, which holds a bar to a single
  cover, does not take it (a common cover rule on the same host still does), and
  `cover: { source: host }` still reads only the common cover.
- Measured live: `verify-rebar-placed` (per-face cover) in Revit 2023-2027.

## Large sets in pieces — `chunk`

A building of tens of thousands of bars is one requirement set and should not be
one call that holds Revit for an hour. `horizun_plan_reinforcement`,
`horizun_apply_reinforcement` and `horizun_audit_reinforcement` accept

```json
"chunk": { "by": "level", "hosts_per_piece": 20, "piece_index": 0, "partition_sha256": "…" }
```

and then run ONE piece of the set. The command reads the hosts every selector of
the set reaches — with the resolver's own host selection, so the cut sees exactly
what a whole-set run would — groups them by `level` (the level name) or by
`host_type` (BuiltInCategory and type name), at most `hosts_per_piece` per piece,
numbered from 0 in a stable order, and narrows every rule to that piece's hosts
(`RequirementSetChunks.Split`; each rule keeps its selector as `source_selector`).
A call without `chunk` means exactly what it meant before.

- **The cut has a digest.** Every reply carries `chunk.partition_sha256`: the set's
  sha256, the dimension, the piece size and every piece's hosts. A host added, a Mark
  or level edited, or the set changed moves hosts between pieces, and a loop that kept
  counting would build some twice and others never — so a call quoting a digest that
  no longer matches is refused as `chunk_partition_changed`.
- **A write must quote it.** `horizun_apply_reinforcement` with `chunk` and no
  `partition_sha256` is refused as `chunk_partition_required` (the refusal does not
  hand the digest back). The plan and the audit accept it optionally.
- **Other refusals:** `chunk_bad_request` (a key missing, unknown or out of range,
  or `chunk` together with `host_ids`), `chunk_piece_out_of_range` (with
  `piece_count`), and the cut's own `chunk_host_without_key`, `chunk_no_hosts`.
- **The reply's `chunk` block** names `piece_index`, `piece_count`,
  `next_piece_index` (null on the last), the piece's `key`, `element_ids` and
  `requirement_set_sha256`, a summary of every piece, `rules_matching_nothing` and
  `unknown_element_ids`.
- **Provenance keeps the WHOLE set's sha256**, so auditing the whole set later does
  not call every piece's bars stale. The confirmation token binds `chunk`.
- **The audit of a piece** echoes each rule's `source_selector` and judges orphans
  only in the piece's hosts: a bar of another piece is that piece's business.

**The recommended loop:**

1. `horizun_plan_reinforcement` with `chunk: { by, hosts_per_piece, piece_index: 0 }`.
   Read `chunk.piece_count` and `chunk.partition_sha256`; check `rules_matching_nothing`.
2. For each `piece_index` from 0 to `piece_count - 1`, with the same `requirement_set`
   and `chunk` (now carrying `partition_sha256`):
   - `horizun_submit_job` → `horizun_apply_reinforcement` with `dry_run: true`; poll
     `horizun_job_status` until `finished`, take `result.confirmation_token`;
   - `horizun_submit_job` → the same arguments with `dry_run: false`, the token and a
     new `idempotency_key`; poll until `finished`; read `result`;
   - optionally `horizun_submit_job` → `horizun_audit_reinforcement` with the same `chunk`.
3. A `chunk_partition_changed` anywhere means the model or the set moved under the
   loop: stop, plan again, and audit what was already built.

`scripts/live/verify-rebar-xl.ps1` runs that loop over a synthetic fixture of more
than 20 000 bars and records the seconds per piece. **It has not been run yet.**

## Reading schedule notation and comparing bars

- **`horizun_query_structure mode=parse_notation`** with `lines: ["4#5 L=6.20",
  "E#3 1@0.05 10@0.10 R@0.20", …]` reads each schedule cell with `RebarNotation`
  into bars (count, `bar_number` or `diameter_mm`, size), a length and a spacing or
  stirrup zones in millimetres — or refuses it with its code
  (`notation_unrecognized`, `notation_bad_number`, `notation_rest_not_last`, …).
  One refused line never stops the rest; a request that is not a list of 1–5000
  strings is refused whole (`notation_lines_not_usable`). It reads no model, and which
  bar type a `#5` is in the model is not decided here.
- **`horizun_query_structure mode=compare_bars`** compares two sets of bars of the
  active model BAR BY BAR: side A is `element_ids_a` (rebar sets) or `host_id_a`
  (every set in that host), side B likewise. A bar is one position of a set that has a
  bar: position 0's centreline as Revit draws it, offset by that position's
  `GetBarPositionTransform`. `bar_tolerances: { identical_mm, near_mm, length_mm }` is
  required, with no defaults.
  - The distance between two bars is the mean distance from each centreline to the
    other (33 samples evenly along each, point to segment in space), both ways, the
    larger kept; the length difference is judged separately.
  - **identical**: distance ≤ `identical_mm` and length difference ≤ `length_mm`;
    **near**: distance ≤ `near_mm`; **unmatched**: no free same-diameter bar within
    `near_mm` — the row then names the nearest same-diameter bar of B however far,
    and which bar of A took it.
  - Only bars of the same NOMINAL diameter are compared; type names are reported,
    never compared.
  - Pairing is one-to-one and greedy — identical pairs first, then by distance — and
    deterministic; it is not a global optimum and says so.
  - The verdict is `identical` only when every bar of A was paired as identical, no
    bar of B is left over (`unmatched_b`) and every bar on both sides was read;
    `incomplete` when nothing disagreed and something could not be read.
  - Bars of ANOTHER model are not compared: this reads the active document only.

## Reading schedule cards

`horizun_read_schedule` reads the sheets a reinforcement schedule is drawn on and,
when asked, proposes the beam cards as a set of this schema. It is answered in the
server and writes nothing; the caller plans the proposal with
`horizun_plan_reinforcement`, which resolves each mark to its hosts and refuses what
does not fit.

**One reader, three sources.** The reading is `ScheduleCards` (Core, pure): it works
on generic primitives - line segments with a colour and a layer, positioned texts
with a height and a direction - in DRAWING MILLIMETRES with y up. Exactly one source
per call:

- `file_path`: a vector PDF. The server extracts the primitives with PdfPig: stroked
  lines and the text layer, letters joined into texts in the order the page draws
  them. No OCR: a page with no text layer is `sheet_has_no_text`. Bezier pieces
  (axis bubbles, arcs) and filled paths are counted in `sources[].ignored_by_kind`
  and left out. Positions are taken as the page is displayed: measured, PdfPig 0.1.13
  already reports a page under `/Rotate 90` in its displayed frame.
- `cad_entities` + `cad_text_anchor`: CAD IR entities (`CadIrEntity.ToJson`) in
  millimetres that CARRY TEXT - the bridge's accoreconsole reading of a DWG. Measured:
  `horizun_cad_extract` over a Revit CAD link declares text `unavailable` (Revit's import
  drops it), so its entities read as `sheet_has_no_text`. A DWG text carries one point and
  no extent, so the caller declares whether that point is the `start` or the `middle` of
  the baseline (`cad_text_anchor_not_declared` otherwise). With `start`, the axis labels'
  centres are not known: the span check does not run and nothing is proposed
  (`axis_positions_uncertain`). A text carrying MTEXT formatting codes is refused
  (`text_formatting_not_read`), never cleaned on a guess; a text without a height is
  `text_height_unknown`. An entity inside a block definition (`block_path`) carries the
  block's coordinates, not the sheet's: it is counted (`in_block_definition`) and left out.
  An arc is read as its two chords start-middle-end (the IR arc carries `start`, `middle`
  and `end`); an arc without them is counted (`arc_without_ends`).
- `primitives`: `{ segments, polylines, texts }` produced by any other extractor.

**Declared, never inferred.** `scale` is the drawing scale's denominator (50 for
1:50): one drawing millimetre is that many real millimetres. `tolerances` is
`{ length_mm, zone_span_mm }`, both required. Without them nothing is read
(`scale_not_declared`, `tolerances_not_declared`). Optional: `kinds`,
`title_pattern` (default `^[A-Z]{1,3}-?\d{1,4}[A-Z]?$`), `bar_colors` / `bar_layers`
(only those lines can be bars; a PDF colour is `#rrggbb`, a DWG one `aci:n`),
`stirrup_bar_label` and `tolerances.support_gap_mm` (both below).

**The conventions read.** Every geometric window is a multiple of the callout's own
text height, so a sheet in points, a sheet in millimetres and a DWG drawn full size
are read by the same rules.

- *A card* is a title at least 1.6 times the sheet's median text height inside a
  frame: the smallest rectangle around it that holds a reinforcement callout and no
  other title. A frame with rounded corners reaches a PDF as its top and bottom lines
  only (the arcs are curves); a pair of those with the same ends, at least ten title
  heights wide, is a frame too, and the card says `frame_kind: top_and_bottom_lines`.
  A closed frame wins over such a pair: the pair is the fallback for a card no closed
  frame holds (measured: two stacked title boxes made a smaller "open frame" inside a
  card's closed one and cut its first column off).
- *A title that is no card*: a title whose smallest callout-holding frame is a frame
  around other cards (the sheet's number in its border, a region heading) is listed in
  `titles_not_cards`, not refused; every callout in that frame that lies in no card's
  frame is refused as `callout_outside_cards`.
- *A beam card* has level bar callouts or stirrup zones. Axis labels are read in the
  band above the frame; stations are millimetres from the first axis. `B=25 H=40` is
  returned as written (`section_as_written`), with no unit assumed. `Son 2 Vigas`
  gives `members_stated`; `SON 36` or `Cantidad 36` alone is the stirrup total.
- *A bar* is a callout `n#k L=x.xx` (an optional `(note)` after it) and the line
  just below it, parallel to the text, under its middle, within three text heights.
  Its ends are walked: lines of its colour joined end to end are first the same bar
  in pieces (collinear), then its hook - a square tick, or a bend drawn as short
  chords and a leg. The hook's angle is measured and read as 90, 135 or 180 degrees
  within 10. Its face is `top` above the stirrup zone line, `bottom` below it, and
  unknown when the bars are not on both sides of one. A bend drawn in chords shorter
  than the end window is walked as one chain: a line that only comes NEAR a bar's end
  is followed only when it is joined, end to end, to the line that ends there; any other
  near line is a fork (`hook_ambiguous`).
- *A stirrup zone* is `60e#3` with `c/.30` written just under it, `11@.08`, or
  `14#3@.07`, over its dimension line. A zone that states no bar takes the card's
  stirrup bar only when the card states exactly one, and says so (`bar_from: card`).
  One text holding several zones (`6.#3@.06 2.#3@.25`) is split by its characters;
  with no extent (a DWG text) it is `zone_group_not_placed`. A PDF that writes two
  callouts side by side with a space's width between them and no space character
  (measured) reaches the reader with a space put back: a gap wider than 0.15 of the
  letter size between two letters of one run is a word break. When the count is written
  over the line and the spacing under it (`40e#3` / line / `c/.25`), the one line
  between the two texts - under the count's middle, below its baseline, above the
  spacing's capitals - is the zone's line; two there are `callout_two_lines_equally_close`.
  A zone's line may be as short as a quarter of its text height (two stirrups at .08 m
  are 1.6 mm at 1:50); the zone count check holds it. The stirrup type may follow the bar
  against it (`12e#3T-4`): it starts with a letter, so the bar size is not in doubt.
- *The card's stirrup bar label* (`stirrup_bar_label`, declared): a regular expression
  with a named group `bar` for the card text that states its stirrup bar (for example
  `^T\d+\s*(?<bar>[#Ø]\s*\d+)$`). Zones that state no bar take it, `bar_from:
  card_label`; two different bars on one card refuse every zone that states none
  (`zone_bar_not_stated`). Without the declaration nothing is taken from a label.
- *A wall card* writes its bars turned beside them. Turned bar callouts are read the
  same way (the line "below" a turned text is beside it). Levels are `+2.50` texts
  with the level line nearest the middle of the text, above or below it. Horizontal
  bars are `n#k` with `c/.20` written just below it in its own direction, level or
  turned. A wall card is read and checked; it is not proposed.
- *A level callout written across turned bars* (measured on real wall cards: `4 #4 L=2.70`
  at mid-height of a storey's three vertical bars, crossing one of them): when no level
  line runs under it, the bars it names are the bar lines, upright, at least three text
  heights long, that run through the whole height of the text, inside the card, not
  along its frame's sides and not taken by a turned callout. It is read only when their
  number is the callout's n (`bar_group_count_differs` otherwise) and no other callout
  reaches the same lines (`line_claimed_by_two_callouts`). Each line is walked for its
  hooks; the length check holds the line furthest from the L (`read_as:
  level_callout_across_turned_bars`, `drawn_developed_mm_each`). A card with only level
  bar callouts is a wall card when it also carries no stirrup zone, two level values and
  a turned horizontal-bar label.
- *A bending table* is read only with `table.columns`: the header text of `mark`,
  `bar`, `count`, `length` (and optionally `total`, `shape`, `notation`) as the sheet
  writes them - no header dictionary is compiled in. Rows are the texts under the
  header row, columns split halfway between header centres, until the `total_label`
  row or a gap of three row pitches. A bar cell must say `#n` or `Øn`; a bare number
  is read only with `bar_prefix` declared.

Every callout goes through `RebarNotation` (F3): a note it refuses is
`callout_notation_refused`, with `notation_code` beside it.

**Refused, by name, never guessed:** `callout_without_line`,
`callout_two_lines_equally_close` (gaps within 0.15 text heights),
`line_claimed_by_two_callouts` (every claimant refused), `callout_notation_refused`,
`hook_ambiguous` (two lines leave a bar's end, or what continues it is longer than a
hook), `zone_spacing_missing`, `zone_spacing_ambiguous`, `zone_bar_not_stated`,
`horizontal_bar_spacing_missing`, `horizontal_bar_spacing_ambiguous`,
`level_without_line`, `card_frame_not_found`, `card_frame_shared`,
`card_kind_ambiguous` (turned bars and stirrup zones in one frame),
`card_has_no_callouts`, `sheet_has_no_text`, `table_header_incomplete`,
`table_header_ambiguous`, `table_cell_has_two_texts`, `table_cell_missing`,
`table_cell_not_a_number`, `table_bar_unrecognized`, `callout_not_recognised` (a text in
a card that writes a bar with a length, a spacing or a stirrup count in a shape no
convention reads - never dropped in silence), `callout_outside_cards`,
`zone_group_not_placed`, `bar_group_count_differs`. A refused row never stops the
others.

**Self-checks**, each a named result - `pass`, `fail` or `not_run` with the reason -
never a silent fix:

| check | compares |
| --- | --- |
| `bar_length_matches_callout` | the drawn line plus its hook legs, at the declared scale, against the callout's L, within `length_mm` |
| `zone_count_matches_its_length` | n stirrups at spacing s measure (n-1) x s; against the zone's dimension line, within `length_mm` |
| `stirrups_sum_to_stated_total` | the zones' counts against `SON n` / `Cantidad n`; not run when a zone was refused |
| `zones_fill_axis_span` | the zones' lengths against the span between the first and last axis, within `zone_span_mm` |
| `levels_agree_with_scale` | the levels' stated differences against the distances between their lines at the scale |
| `table_row_total_matches` | count x length against the row's total column, within `length_mm` |
| `table_rows_sum_to_total` | the rows' totals against the totals row; not run when a row was refused |
| `cards_match_stated_count` | a sheet's `Son n Vigas` outside every card against the counts its cards state with the same word |
| `zones_fill_span_between_supports` | with `tolerances.support_gap_mm` declared, in place of `zones_fill_axis_span`: every gap between consecutive zones, and between the outer axes and their zones, is a hand-over (no wider than the larger spacing beside it, within `length_mm`) or a support (an axis inside it or at its end, no wider than `support_gap_mm`); no zone past the outer axes, none overlapping |

`summary.verdict` is `checks_failed` when any check failed, else
`read_with_refusals` when anything was refused, else `read_clean`.

**The proposal from the card's axes** (`requirement_set.extents: "axes"`, the default since
stage 1c). Every bar group (one size, one run, one hook at each end) becomes its own layer
with `extend: { axis: <first axis>, toward: <last axis>, from_mm, to_mm }` exactly as drawn,
so bastones, one-end hooks and asymmetric zones need no orientation declared. Stirrups:
each zone lays its `n` stirrups with `number_with_spacing` from its own first stirrup and
runs on to the next zone's first one, so the gap the drawing leaves between zones is
kept; the runs an interior support separates become rules of their own (`<mark>-s2`...)
on the same host mark. Refused by name: `face_bars_overlap` (two groups on one face over
the same stretch - a lap, a baston between continuous bars: where each stands across the
face is not on the card), `zones_not_consecutive` (a zone reaching the next zone's first
stirrup), `axis_names_repeated`, `hook_shape_not_read` (a bend at no angle within 10 degrees
of 90, 135 or 180), `bar_end_continues_parallel` (the bar bends and runs on beside itself:
a bayoneta, or the offset a drawing gives one bar of a lap - the card says neither which nor
the offset, so it is not read as a `crank`; measured on the real sheets below, the drawn
offsets are 250 to 319 mm along the slope at the card's scale, many bar diameters, and 36
of 75 step toward the outside of the section, where no crank can go).

**How the face is laid - `requirement_set.face_slots`** (declared by the caller, never
assumed). A card draws two groups on one face and never says where each stands across it.
`face_slots: { laps?: "inside" | "outside", additional?: "between" }` declares the drawing
set's convention, and only what it resolves beyond doubt is proposed:

- `laps`: two groups of one bar and one count, the second starting inside the first and
  ending past it, neither hooked at the end that laps, are ONE layer from the first's start
  to the second's end with a lap `{ at_mm, length_mm, toward }` at the middle of the overlap
  and as long as it, from the card's first axis; chains of three or more are read the same.
  `toward` is the declared value: the side the next piece sits on.
- `additional: "between"`: a group lying within exactly one other (unlapped) group's
  stretch, with one bar fewer than it, is a layer `between` that group: one bar in each gap.
  Its ends within the reader's `length_mm` of the holder's ARE the holder's (as two bars'
  ends within it are one run). Two such groups over one stretch are not resolved.

Everything else stays `face_bars_overlap`, and with face_slots declared its reason says the
declaration did not resolve it: groups of equal count one within the other (a second row,
or bundled bars - not on the card), different bars lapped, a third group over a lap.
`face_slots` with `extents: "host"` is refused: that proposal has one layer per face. The member's mark selects the host
and is NOT written on its bars (measured live: stamped on every set it was
`bar_mark_conflict`). Measured live on Revit 2023 (`scripts/live/verify-rebar-card-axes.ps1`,
a synthetic two-card sheet over grids Y7..Y9, one member as one beam and one as two):
read, plan, apply and audit 5/5 - 19 sets built and read back, each span's steel in its
own beam, an unknown axis refused as `axis_not_in_model` before anything was built.

**The proposal from the host** (`extents: "host"`: `requirement_set: { bar_type_names,
hook_type_names?, cover?, stirrup_hook_type_name?, card_left_is_host_start?, set_id? }`). Each beam card becomes
one `section_rules` entry with `host: { category: OST_StructuralFraming, mark: <title> }`,
`cover` as given or `{ source: host }`, one `host_length` layer per face and the
stirrup zones with their measured lengths and spacings (`include_last_bar: false`
before each boundary). Bar types are named by the caller (`{ "#5": "<RebarBarType>" }`);
hooks by their read shape (`{ "90": "<RebarHookType>" }`). It is ALL OR NOTHING - half a
set builds half the steel - and every reason a card is not proposed is listed in
`requirement_set_not_proposed`: `card_has_refusals`, `card_checks_failed`,
`card_title_repeated`, `axes_missing`, `axis_positions_uncertain`,
`bar_face_not_determinable`, `extent_not_expressible` (a bar that does not run axis to
axis - the host's start face is not on the card), `face_has_mixed_continuous_bars`,
`hook_end_not_mappable` and `zones_orientation_unknown` (a one-end hook or asymmetric
zones, unless `card_left_is_host_start` is declared), `hook_shape_not_read`,
`hook_type_not_declared`, `bar_type_not_declared`, `stirrup_bars_differ`,
`zones_across_supports` (the zones stop at an interior support and start again past it: a
section rule's zones run on along one host, so the gap is not expressible). The members a
card states are in `cards[].members_stated`: compare them with the hosts the plan
selects by mark.

**Measured on real sheets (stage 1c, 2026-10-12).** The reader was run, read-only, over
the 121 pages of 67 real schedule and plan PDFs of one project, at `scale` 50 and
`length_mm` 50 (counts only; nothing from them is in this repository). Pages are typed by
what they write, before reading: beam cards (two or more zone callouts), wall cards (two
or more turned bar callouts), bars only, other (plans). A callout is a text that writes a
bar with a length (`n#k L=`) or a stirrup zone; a *row* is one of them read; a card is
*clean* with no refusal and no failed check, *complete* when clean and (a beam card)
proposable; callouts outside every card are counted apart and never block a card; a page
is *complete* when every callout on it is read, nothing is refused, no check fails and -
on a beam-card page - a set is proposed. The declarations were the caller's, per drawing
set: `bar_colors` red on the sets that draw bars red, `stirrup_bar_label`
`^T\d+\s*(?<bar>[#Ø]\s*\d+)$`, `support_gap_mm` 500, and (second column) a second
`title_pattern` for the wall sets' detail boxes.

| | release 3.0.0 | stage 1c, lot 1 | stage 1c, lot 1b | lot 1b + `face_slots` |
|---|---|---|---|---|
| beam-card pages (18): rows read / callouts | 838 / 1849 | 1682 / 1878 | 1746 / 1878 | 1746 / 1878 |
| beam cards clean / found | 5 / 300 | 147 / 265 | 159 / 289 | 159 / 289 |
| beam cards complete (proposable) / found | 5 / 300 | 42 / 265 | 94 / 289 | 105 / 289 (undeclared: 94) |
| wall-card pages (24): rows read / callouts | 220 / 1128 | 943 / 1128 | 943 / 1128 | 943 / 1128 |
| wall cards complete / found | 0 / 147 | 103 / 127 | 103 / 278 (151 of them detail boxes, none complete; undeclared: 103 / 147) | 103 / 278 |
| callouts outside every card | (not counted) | 584 | 243 (undeclared: 560) | 243 |
| callouts neither read nor refused (all pages) | 383 | 6 | 6 | 6 |
| failed checks on beam-card pages | 147 | 105 | 109 | 109 |
| pages complete | 0 / 121 | 0 / 121 | 0 / 121 | 0 / 121 |

With `face_slots: { laps: "inside", additional: "between" }` the beam cards refused as
`face_bars_overlap` went from 78 to 59 (the reasons from 250 to 115); of the 19 resolved,
11 are complete and 8 carry a failed check. Of the 59, 35 are refused for that alone: groups
of equal count one within the other, different bars over one stretch, a third group over a
lap - the declaration does not say where those stand.

No page is complete. Beam pages: a set is all or nothing, and every real beam page carries
cards with two bars overlapping on one face that no declaration resolves
(`face_bars_overlap`), or a bend that continues parallel rather than hooking
(`bar_end_continues_parallel`, 71; `hook_shape_not_read`, 4): where each bar stands across
the face is not on the card.
Wall pages: the detail boxes, once declared as cards, are bar sketches whose callouts sit
beside the bar and are drawn at a scale the box does not state (`callout_without_line`,
`callout_not_recognised`); undeclared they stay `callout_outside_cards`. Both are
refusals, not silences. Tables of the mark/bar/count/length/total shape were not found in
that set and are measured on the synthetic sheets only.

**The DWG route, measured on the real schedule DWGs (4 card drawings, the same sheets as
four of the PDFs; 8 plan drawings).** The accoreconsole reader left the source folder
unchanged (every file's size and time identical).

- *Fixed in lot 1b: the extractor now carries what places a text and how a line is drawn.*
  TEXT rows carry their justification (72/73) and alignment point, MTEXT rows their
  attachment (71) and box (42/43), LINE/ARC/LWPOLYLINE rows their own colour (62, and 420)
  and linetype (6), resolved against the layer for BYLAYER. A text carrying `text_type` is
  placed by it - no `cad_text_anchor` is needed - and a DWG height (AutoCAD's: the height of
  the capitals) is read as height / 0.7, the em every window here is measured in (the same
  sheet both ways: capitals against em 0.72). Placed this way, 135 of 154 DWG texts start
  within 0.3 mm of the same text on the PDF (62 before). Every text on the real card
  drawings is an MTEXT attached top-left; every line is BYLAYER on one detail layer, the
  bars and the dimension lines alike - no colour separates them, and dashed outlines only
  by linetype (`bar_linetypes`). Measured after the fix: the beam drawings read 56 of 56
  callouts with nothing refused, 5 checks failing, 6 of 14 cards proposable; the wall
  drawings 78 of 114, the 36 others refused as `line_claimed_by_two_callouts`: a storey
  dimension line, drawn like a bar, runs through every storey's callout (in the PDF it is
  black and the bars red).
- *Where the text point sits (before the fix).* Fitted against the same texts on the PDF (scale 1:50, no
  turn, found by the fit) on the 154 texts written once in both: along the baseline the
  point is the text's START (133 within a fifth of the advance from it; 36 of 69 and 26 of
  85 within 0.2 mm under the start hypothesis against 8 and 11 under the middle one).
  Across the baseline it is NOT always on it: 65 are, 67 sit 0.2 to 0.8 of their size
  above it - the MTEXT insertion point, at its top or middle attachment. The
  IR does not say which entity was TEXT and which MTEXT, nor the attachment, so a reader
  of these entities cannot put such a text back on its baseline. On each of the two wall
  drawings 78 rows are refused that the PDF of the same sheet reads (42
  `horizontal_bar_spacing_missing`, 36 `line_claimed_by_two_callouts`); the cause was not
  isolated, and this offset is the difference measured between the two readings' texts.
  Declare `cad_text_anchor: start`; the remedy is the extractor carrying each text's entity
  type and justification, which it does not yet.
- *MTEXT formatting codes.* 122 texts carry them, all `\L` (underline) or `\P`
  (paragraph), none on a reinforcement callout. They stay refused
  (`text_formatting_not_read`).
- *Colours and layers.* No entity carries a colour in the IR (the extractor reads the
  layer's, not the entity's), and the bars share a generic detail layer with every other
  line, so neither `bar_colors` nor `bar_layers` can isolate them; read without either.
- *Blocks and arcs.* Title blocks, axis bubbles and symbols are blocks: 689 of 2946
  entities of one card drawing are in block definitions, at block coordinates - now left
  out and counted. Every hook bend is an ARC: the IR arc now carries its start, middle and
  end, and the reader follows the bend - on the two beam-card drawings the failed checks
  went from 16 to 2 each once the bends were followed, with every callout read and nothing refused. The axis labels sit
  in the bubble blocks, so the span checks do not run on a DWG reading (`not_run`).

The proposal is held to `StructuralRequirementSet.Load` offline; planning and applying a
proposed set against real beams is the live step.

## Bars of different rules — `clearances` and `overlapping_bar`

Every other check here looks at one set against its host. This one looks at two sets against
each other. MEASURED on Revit 2024 and 2026: a wall's horizontal bars laid through a boundary
element's stirrups came out pushed by Revit until they touched them, with the array 12.5 mm short,
and nothing reported it. **Revit moves a bar that crosses another, and says nothing.** So the
check runs twice. The plan and the apply run it on the declared centrelines, before anything is
written. The audit runs it on the bars the model carries.

```json
"clearances": {
  "between_rules_mm": 25,
  "crossing_mm": 0,
  "pairs": [ { "rules": ["dowels-c1", "C1"], "min_clear_mm": 0 } ]
}
```

- **The clearance is declared, never compiled in.** `between_rules_mm` is the minimum clear
  distance between the SURFACES of bars of different rules that run alongside each other
  (segments within 45 degrees of each other). `crossing_mm` is the same for bars that cross; it
  is 0 unless declared, because crossing bars are tied in contact (a mat's two layers are laid
  that way).
- **`pairs` overrides `between_rules_mm` for two declared rules**, by the id the set declares
  (a section, wall boundary, mat or stirrup zone rule, or a reinforcement rule). Name the same id
  twice for two sets of one rule. Every id must be declared, or the set is refused as
  `reference_not_declared`. A piece of a chunked set keeps only the pairs whose two rules it
  still carries.
- Each value is a number from 0 to 500 mm. Anything else is refused by name.
- **Nothing declared: only intersections are checked.** These are surfaces that interpenetrate
  (clear distance below zero, beyond `tolerances.length_mm`), and Revit would move such a bar.
  The reply's `checked` says `intersections_only`, and its `checked_means` says bars close
  together were not judged.

**What is measured.** Each bar is its centreline as a polyline (arcs sampled), at every position
its set builds, with the radius of the MODEL diameter.

- **The plan measures a declared corner as Revit draws it** (etapa 1c): a bend whose centreline radius is
  half the bar type's bend diameter (stirrup/tie or standard, by the row's style) plus half the model
  diameter. MEASURED, Revit 2024 (anonymized regression case): corner bars declared in a stirrup's sharp corner passed the
  plan, and the audit, on the drawn bars, found each 10.29 mm into the stirrup's bend. A row with arcs
  keeps its sampled arcs; a row without its bend diameters is measured sharp. Two segments are measured exactly
(closest distance between segments). The clear distance is that distance less both radii.

- The plan uses the declared centrelines at their signed positions. A suppressed first or last
  bar is not there.
- The audit uses the centreline Revit drew (its bends as drawn), offset by
  `GetBarPositionTransform`, at every position the model says exists.

A finding is anything closer than the clearance that applies, less `tolerances.length_mm`. Bars
that pass THROUGH each other are always a finding, whatever is declared.

**What is not a finding — the excuses, each with a test** (`RebarOverlapTests`):

| Excused | Why | Counted in |
|---|---|---|
| Two bars of ONE set, or of one layout (the zones of one stirrup zone rule, the pieces of one mat component around openings) | Their spacing is the layout's; this check is between rules | never compared |
| The two pieces of a **declared lap** (a section layer's `laps`) | Laid one diameter across, in contact (D4); `lap_insufficient` measures them | `excused.declared_lap` |
| The two pieces of a **declared coupler** | They meet end to end at the station; `missing_coupler` and `coupled_end_seated` measure them | `excused.declared_coupler` |
| A **cage's stirrups or ties against its own longitudinal bars**, and its stirrups against its own ties (same section or wall boundary rule, same host) | The stirrup is laid against the bars, Revit seats the corner bars in its bends (L10), and a tie is declared through the centre of the bar it encloses | `excused.cage_ties_its_own_bars` |
| A **hook** | The plan's centrelines carry none. The audit cuts the hook off the drawn bar where the hook-free centreline ends (`centreline_points_as_declared_mm`): on an open bar, at the first drawn point on the hook-free line, so no part of the hook's bend is measured (etapa 1c). Where Revit bends a hook round another bar is its drawing of a declared termination | `excused.hooks_not_measured` (hooked ends) |

Not excused: two longitudinal bars of one cage, two ties of one cage, a bar of ANOTHER cage
(a beam-column joint, a mat against a boundary element), starter bars declared as their own rule
against the column bars (declare a pair with `min_clear_mm: 0` if they are meant to touch), and
crossing layers of different mats.

**The plan refuses both sets of a clash.** Either set alone would build; together they cannot be
built as declared. Which of the two passes over is declared with `crossing_order` (below); without one, nothing is
displaced.

- `bars_intersect`: the bars pass through each other.
- `bars_closer_than_declared_clearance`: the bars keep less than the declared clear distance.

Each refused row carries `overlaps`: the other rule and host, the worst clear distance, the point,
`alongside` or `crossing`, and how many bar pairs clash. A set the check cannot measure (no model
diameter) is refused as `overlap_not_evaluable`. The apply runs the same check, so the rehearsal
a person approves is what runs. A refusal is the honest answer here: a warning would let the apply
build steel that Revit then moves, or that breaks the declared clearance. The plan's and the
apply's `overlap_check` publishes the following:

- what was checked, and the clearances;
- the counts and the broad phase;
- the excuses that came into play;
- every pair, up to 200.

**The audit reports `overlapping_bar`** (error), one per pair of sets: `rule_id` and
`other_rule_id`, `rebar_id` and `other_rebar_id`, `clear_mm`, `required_clear_mm`, `point_mm`,
`kind`, `relation` and `bar_pairs`. A set it cannot measure is `unreadable` about
`overlapping_bar`. Examples: no positions; a hooked bar whose hook-free centreline will not read.
Unknown is never a pass. The reply's `overlaps` block mirrors the plan's `overlap_check`.

**The same bar built twice — `duplicate_bar`** (stage 1c). Two bars of different rules, or
of two sets, on ONE centreline are not two bars crossing: they are one bar built twice, and
every takeoff counts that steel twice. A pair of bars is a duplicate when every point of each
centreline - its vertices, and points no more than 50 mm apart along every segment - lies
within `tolerances.length_mm` of the other centreline (both ways, so a bar that is only part
of a longer one is not a duplicate), and their model diameters agree within 0.05 mm.

- It is the limiting case of an intersection, so it is found FIRST and its bar pairs are taken
  out of the intersection test: a duplicate is reported **once, as a duplicate**, never also as
  `overlapping_bar`. Other bars of the same two sets that cross are still intersections.
- **Nothing excuses it.** A lap, a coupler or a cage is a contact between two different bars;
  one bar built twice is wrong whatever was declared. Two bars of ONE layout are still never
  compared.
- **The plan and the apply refuse both rules** as `duplicate_bar`, each naming the other: the
  row's `overlaps` entry has `kind: "duplicate"`, `bar_pairs`, `max_deviation_mm` and
  `position_pairs` (this rule's position / the other's, up to 20).
- **The audit reports `duplicate_bar`** (error): `rule_id` and `other_rule_id`, `rebar_id` and
  `other_rebar_id`, `position` and `other_position`, `bar_pairs`, `position_pairs`,
  `max_deviation_mm` and `point_mm`. It looks against **every bar in the audited hosts**, not
  only the ones a rule matched: a set no rule of the requirement set built (another set,
  hand modelling, an area or path system's bar) laid on a rule's bar is found too - its
  `other_rule_id` is null and the text says no rule of the set built it. Those bars are
  compared for duplicates ONLY (`overlaps.duplicate_only_sets` counts them); a set of them
  that will not read is `unreadable` about `duplicate_bar`.

**Not compared** (in `not_checked`):

- `overlap_with_unattributed_bar`: whether bars no rule of this set built (another set's, hand
  modelling, an area or path system's) CROSS the set's bars, or come closer than declared -
  the audit compares them for `duplicate_bar` only. Fabric is never compared, and in a
  chunked run neither are the bars of other pieces. `mode=steel_review` (below) judges every
  bar of a model against every other.

**A whole model, with no requirement set — `horizun_query_structure mode=steel_review`.** A
model generated elsewhere, or built by hand, has no requirement set for its steel - and that
is where steel ordered twice and bars Revit will push go unseen. `steel_review` runs the same
two checks, with the same broad phase and the same exact geometry, over every bar in scope:

```json
{ "mode": "steel_review", "tolerance_mm": 2, "max_rows": 200 }
{ "mode": "steel_review", "tolerance_mm": 2, "host_id": 501, "clearances": { "between_rules_mm": 25 } }
```

- **Scope** as `mode=quantities`: `element_ids` (a set, a system bar, or an area or path system,
  which scopes to its bars), else the whole document; then `host_id`. Every rebar set and every
  bar of an area or path system is read at every position the model says holds a bar
  (`GetTransformedCenterlineCurves`, hooks suppressed and counted in `hooks_not_measured`,
  bends kept). Fabric sheets in scope are counted (`fabric_sheets_not_compared`), not compared.
- **What is compared with what.** Each rebar set is its own layout; every bar of one area or
  path SYSTEM is one layout (the system lays its layers crossing). Two sets a **Revit coupler
  in the model** joins meet end to end at it by construction: they are not judged as an
  intersection (`counts.coupled_in_the_model`) - MEASURED on the acceptance building (Revit
  2026), the B2 pieces overlap 3.9 mm at their couplers. Every other pair is compared - there
  are no cage or lap excuses, because nothing is declared; a duplicate is a duplicate whatever
  joins it.
- **`tolerance_mm` is required** (`tolerance_required` otherwise; nothing is reviewed): how far
  two centrelines may stand apart and still be one bar, and how deep two surfaces must
  interpenetrate to be an intersection. **`clearances`** (optional, 0-500 mm) takes
  `between_rules_mm` and `crossing_mm` only - rule pairs mean nothing without a set - and makes
  bars of different sets closer than that a finding too.
- **The reply**: `verdict` - `clean` (every bar in scope read, nothing found), `findings`, or
  `incomplete` (nothing found among what was read, and something could not be read; never
  clean); `counts` (sets, system bars, systems, bars compared, unreadable sets, hooks not
  measured, and `duplicates`, `intersections`, `closer_than_declared_clearance`, each as
  `set_pairs` and `bar_pairs`); `by_host_category` (the same counts per host category; a pair
  across two categories is counted under both); `intersections_by_styles` (by the two sets'
  styles, and whether they share a host); `pairs` worst first - duplicates, then the deepest
  intersections, then the shortest clearances - up to `max_rows` (default 50, at most 500;
  `pairs_not_listed` says how many more), each with both bar ids, the systems, the host
  categories, the styles, `code` (`duplicate_bar` or `overlapping_bar`), the clear distance,
  the point and the bar pairs; `unreadable` by name; `coverage`; `read_ms` and `elapsed_ms`.
  Read-only.
- **Bars a `crossing_order` displaced.** A model has no declaration to read, so the review judges the
  bars where the model has them: a displacement that left them in contact (the `diameter` offset) is not an
  intersection, one that left them still passing through each other is counted, and a `crossing_mm` given
  to the review judges the contact too. Whether a bar stands where its order put it is the audit's question
  (`crossing_displacement_differs`), not this one's.
- **Read the intersections with the styles.** MEASURED (L10): Revit seats a cage's corner bars
  in its stirrup's bends, 3.53 mm into the bend for a 16 mm bar in a 10 mm stirrup. A
  requirement set's audit excuses that as a declared cage; this review, with no declaration,
  reports it as an intersection when `tolerance_mm` is tighter - it is under
  `standard+stirrup_tie (one host)`.
- **Speed.** Measured at a desk (`RebarSteelReviewTests`): 21,924 bars in 2,003 sets of a
  ten-storey building, with two planted duplicates and a stray bar, reviewed in 0.9 s with the
  counts exact. Live (Revit 2026): the acceptance building - 238 sets and 3 area and path
  bars, 1,313 bars - read in 46 ms and judged in 3 ms, `clean`.

**Speed.** The broad phase is a grid. Every segment is cut into pieces no longer than a cell
(500 mm, or more for large radii and clearances). Each piece is filed under the cells its box
touches; the box is grown by the bar's radius and half the largest clearance. A pair of pieces is
measured once, in the lowest cell their boxes share. MEASURED at a desk (`RebarOverlapTests`): ten
storeys of 30 x 30 m slabs with four mat layers and 49 caged columns. That is 21,721 bars in 2,001
sets and 51,121 segments. The check took 1,281,368 exact tests (all pairs would be about 1.3
billion) in 0.85 s.

## Which bar passes over — `crossing_order` (etapa 1c)

Without it, two sets whose bars pass through each other are both refused (`bars_intersect`, see above):
Revit pushes a crossing bar until it touches the other, and says nothing. That is right, and it leaves
real buildings unbuildable. In an anonymized Revit 2024 regression case, bars were refused for
crossings that a detailer resolves by deciding which bar passes over — two beams' bottom bars crossing
at one level at a joint, a slab's mat running through beam bars, the legs of a beam's top and bottom U
bars standing in one plane at one station.

Which bar passes over is a detailing decision, so it is **declared**. The bridge compiles no order of
its own: without a `crossing_order` nothing is displaced.

```json
"crossing_order": {
  "offset": "diameter",
  "orders": [
    { "id": "x-over-y", "stays": ["K10*"], "moves": ["K20*"],
      "move": "translate", "direction": "toward_host_core" },
    { "id": "mat-over-band", "stays": ["BAND"], "moves": ["M1#bottom_y"],
      "move": "translate", "direction": [0, 0, 1], "offset": { "mm": 16 } },
    { "id": "u-legs", "stays": ["U-SUP"], "moves": ["U-INF"], "move": "legs", "legs": "both" }
  ]
}
```

- **`offset`** (required): how far a moving set goes. `"diameter"` is its own model diameter.
  `"contact"` is the two bars' radii, so the bars end up touching. `{ "mm": n }` is a declared
  distance (0 < n ≤ 500). An order may carry its own `offset`.
- **`orders`** (required, not empty): each one has an `id`, the sets that **stay**, the sets that
  **move**, and how they move. A ref is a declared rule id (every row that rule expands to) or a row's
  own id after its `#`: `V1#h*#bottom` is one layer of a section rule in any host, and `M1#bottom_*`
  is one mat component. `*` and `?` are wildcards, and everything else is literal. The part before
  the first `#` must match a declared rule, or the set is refused (`reference_not_declared`). A ref
  in both `stays` and `moves` of one order is refused.
- **`move: "translate"`** moves the whole set. `direction` is a vector, or `"toward_host_core"`: the
  common perpendicular of the two crossing bars where they meet, to the side of the moving set's host
  centre (bounding box). For two beams' bottom bars that is up, and for their top bars it is down.
- **`move: "legs"`** moves the end legs of an open bar inward along its run (`legs`: `both`, `start`
  or `end`). The bottom U then nests inside the top U, and its run is shorter by what the legs moved.
  A closed bar, a bar with an arc, a bar with too few segments, or a run shorter than the move is
  refused by name (`crossing_displacement_not_applicable`).

**What the plan does.** It measures the declared centrelines against each other (the overlap check).
For every clashing pair one order covers (one set in `stays`, the other in `moves`), it displaces the
moving set once, from its declared position. Then it resolves that set again, so every check runs on
the displaced bars: the fit, containment and **cover**, and the anchorage. Last, the overlap check runs
on the **result**. Each displaced row carries `crossing` with the following fields:

- `mode`, `moved_by_mm`, `direction` (or `legs`);
- `against` (the sets it gave way to, with the clash found before);
- `order_ids` and `reason`;
- `declared_curve_mm` beside the displaced `curve_mm`.

The plan's and the apply's `crossing_order` block lists every displacement and what became of its
row. What the apply builds and verifies is the displaced centreline.

**Never squeezed, never guessed.** Each of these is a refusal by name:

| Code | When |
|---|---|
| `crossing_displacement_breaks_cover` | The displaced bars no longer fit their host or its cover. The cover is the one the set declares (`cover_rules`), or, where it declares none, the cover the host carries: the bridge moved the bar, so it proves where it now stands (measured on Revit 2025: without this, a mat layer sent 12.7 mm down out of a 20 mm slab cover planned as inside). The why names the original check |
| `crossing_order_ambiguous` | Two orders disagree on which of two sets moves (both sets refused). Or `toward_host_core` cannot be read one way: the bars run alongside each other (no crossing axis), the host centre lies on the plane they cross in (within the length tolerance), or the centre could not be read |
| `crossing_order_conflict` | One set is asked to move two different ways. Displacements are not added up. Two pairs that move a set the same way (within 1 degree) move it once, by the larger offset |
| `crossing_displacement_not_applicable` | The set cannot be moved that way: legs (above), or a hoop or spiral placed from a shape |
| `bars_intersect` / `bars_closer_than_declared_clearance` | The displaced set still clashes (the offset is not enough, or a third set crosses it). The why says it was displaced |

A pair no order covers is refused by the overlap check as before. **One bar built twice is not a crossing**:
an order naming two rules that lay the same bar moves neither and refuses neither as a crossing - the pair
stays `duplicate_bar`, by name, in the check on the result (lot 4; `RebarCrossingTests`). Two components of ONE mat declared at one depth are still refused by the mat itself (`two_layers_occupy_the_same_plane`, measured on Revit 2025): a mat's own layers are spaced by its own offsets. A two-way mat laid at one depth is two mat rules, and the order says which passes over. Displacements are computed once,
from the declared positions. A set that must clear two others stacked one above the other is declared
with an explicit `offset`, not by chaining orders. A set refused by an order does not make the set it
stays for unbuildable: that one was declared to stay.

**What the audit does.** It resolves the same orders the same way and expects the displaced bars. It
compares them to the model as it compares any bar, and `overlapping_bar` runs on what the model
carries. `crossing_displacements` lists every displaced set the model carries, measured: its
hook-free ends against the **declared** ends, along the displacement (`measured_start_mm`,
`measured_end_mm`, `off_the_displacement_mm`, `agrees`). A bar that is not where the displacement put
it is `crossing_displacement_differs` (error). A bar whose centreline would not read is `unreadable`.
This is the list an acceptance holds apart: declare a position tolerance for every bar and an
additional diameter based allowance for bars displaced by a crossing.

**In a chunked run** a piece keeps every ref that still names one of its rules. An order left with no
`stays` or no `moves` is dropped. Pairs split across two pieces are not compared in either piece (see
"Bars of different rules").

Tests: `RebarCrossingTests` (red first). Live: `scripts/live/verify-rebar-crossing.ps1` covers the
following:

- a joint of two beams crossing at a column (N1, N2);
- nested U legs (U1, U2);
- a mat laid at one depth with a band bar through it, two orders moving it once by the larger offset,
  and a displacement that breaks cover (P1–P3);
- C1 in `verify-rebar-acceptance.ps1`, judged against the harness's own calculation of every displaced
  bar.

## What is not implemented

Recorded here rather than left to be discovered:

- **Per-face cover.** `face` accepts only `common`. Setting cover on one face
  needs a stable way to name that face across a re-issue, and this bridge does
  not have one. Per-face cover is READ by `horizun_query_structure mode=hosts`
  and is not written.
- **Free-form bars.** The schema has no way to ask for them. Area reinforcement, path
  reinforcement and fabric are in the schema (see above), with the gaps listed there.
- **Couplers, and laps nobody declared.** Laps a section rule declares are laid and

- **Free-form bars.** A centreline that is not planar is refused as `curve_not_planar`, whose
  message says so. `Rebar.CreateFreeForm` builds a bar with no shape and no layout (or one that
  needs an API constraint server), which nothing here can lay out, re-read as a set or audit.
- **Hooks on a placed hoop or spiral, and a spiral whose pitch changes along the member.** Both
  are refused rather than approximated (see `shape_placement`).
- **Laps nobody declared.** Laps and couplers a section rule declares are laid and
  audited: `lap_insufficient` when the two pieces overlap less than declared, less
  the length tolerance, or stand further apart across the bar than that tolerance,
  and each lap is listed in the reply's `laps` with the overlap measured. A splice
  somebody modelled by hand, or two `reinforcement_rules` that happen to overlap, is
  not paired — `undeclared_lap` is in `not_checked`. Continuity
  between consecutive spans and between storeys is laid as a layer that runs `into`
  the next element and is lapped there (see above). A bar of one rule is never paired
  with a bar of ANOTHER rule as a LAP: a dowel declared as its own `reinforcement_rules`
  entry is measured for containment and anchorage, and its lap with the column bars is
  not. It IS measured against them for overlap (see "Bars of different rules").
  Stirrup ZONES are in the schema.
- **Elements that are not convex as `into` targets.** See `into_union_not_measurable`.
- **A boundary element in a wall that is not a box.** The wall's whole solid is measured,
  so a wall with an opening, or whose end is mitred or extended by a join, is refused as
  `section_not_rectangular` even where its end region is whole. Corner bars of L and T
  walls are not laid by any rule; declare them as `reinforcement_rules`.
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
  refused. The clamp is applied equally at both ends of the run. A per-face cover
  rule sets faces; it does not feed this.
- **A zone profile declared relative to the host section, outside a section
  rule.** `profile_mm` in `stirrup_zone_rules` is model coordinates; a section
  rule's `stirrups` derive the outline from the host instead.
- **A host whose section changes along its length.** A zone profile is one
  outline moved along the run; a haunched beam needs a rule per section.

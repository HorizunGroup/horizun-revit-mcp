# Planimetry, audited from the model

`horizun_query_planimetry` and `horizun_audit_planimetry` read and judge the
DOCUMENTATION surface of a Revit model — sheets, views, viewports, schedule
placements, dimensions, tags, text, 2D detail and the references between views —
directly from the database. `horizun_fix_planimetry` then applies typed
corrections to what the audit found. No PDF is exported, opened or looked at
anywhere in any of the three.

The two READ tools are **read-only by construction**: neither opens a
`Transaction`, `SubTransaction` or `TransactionGroup`, and a source-level test
(`PlanimetryContractTests`) fails the build if one ever appears on that path.
They share ONE collector (`PlanimetryInventory`), so the query and the audit can
never disagree about what is on a sheet — and the fix recomputes findings
through that same collector and the same rules, so it cannot disagree with the
auditor about what a finding is either.

## Why the model and not the PDF

A PDF answers "what does the sheet look like"; it cannot answer "what IS on the
sheet". It has no element ids, no crop states, no template assignments, no way to
tell an empty viewport from a viewport whose view has nothing visible, and no way
to point a person at the exact element to fix. The database has all of that, and
these tools read it with:

- **real ElementIds and UniqueIds** on every row, so a finding is navigable
  (`horizun_navigate` can select or frame the element);
- **coordinates that declare their frame** — `sheet` for paper geometry,
  `view_plane` for annotations — and their units;
- **explicit coverage**: a reply says whether the whole model was available to be
  read, and an empty finding list under incomplete coverage says so instead of
  reading as a pass.

`horizun_capture_view` can still produce an image as *optional evidence for a
human* — for example, to attach beside a finding — but no rule in this phase
reads an image, and an image never decides whether a check passed.

## The query: six modes

One answer for the whole surface would be enormous, so `horizun_query_planimetry`
takes an explicit `mode`. Every mode is deterministic, paginated, and reports the
exact total whether or not the page was truncated.

| Mode | Population | The facts each row carries |
| --- | --- | --- |
| `inventory` | the census | totals for sheets, views, templates, viewports, schedule placements, title blocks, dimensions, tags, text notes, detail curves, filled regions, detail components, generic annotations, revision clouds, sections, elevations, drafting views, legends, schedules and view references. A total that could not be computed is **absent and named in `checks_failed`**, never written as zero. |
| `sheets` | ViewSheets | number, name, placeholder state, every title-block instance with type and family, the title-block extent AND the sheet outline (with `extent_source` naming which one the auditor uses), placed views, viewports, schedule placements, revisions, guide grid, requested parameters. |
| `views` | Views (not sheets) | type, template id/name, scale, discipline, sub-discipline, detail level, phase, phase filter, level, crop active/visible with the crop **as geometry**, annotation crop, scope box, plan view range, underlay, parent/dependent views, filters, the sheets it is placed on, printability, and the view plane so a caller can convert coordinates. |
| `placements` | Viewports AND ScheduleSheetInstances | class discriminator, sheet, target view/schedule with existence, box outline, label outline, their union as `extent`, centre, rotation, viewport type, pinned, title, detail number — all in **sheet coordinates**. |
| `annotations` | dimensions, tags, text notes, detail curves, filled regions, detail components, generic annotations, revision clouds | every row carries `kind`, owner view (with existence), type/family, a **view-plane bounding box** projected by all eight corners, pinned, group. Dimensions add reference counts split into broken / linked / unreadable, overrides and segments; tags add targets, orphan state, leader and head position; text adds the text, width, alignment and an empty/whitespace flag. |
| `references` | elevation markers, reference callouts, reference viewers, section heads | owner view, target view — resolved, missing, or an **explicit `unknown` with the reason**. A relation the API does not expose is never inferred from a name. |

Narrowing arrives as `sheet_ids` / `view_ids` / `element_ids` (ids that matched
nothing come back in `unmatched_ids`), `categories` (annotations mode only —
refused elsewhere rather than ignored), and `include_parameters` +
`parameter_names` (both or neither).

### Pagination and cursors

`max_rows` caps a page at 500. `next_cursor` is bound to **both** the query
arguments and a fingerprint of the whole result set: a cursor used with different
arguments is refused, and a cursor from before the model changed is refused as
stale instead of silently paging a different list. `matched_total` is exact on
every page.

### The states of a field

A field is never silently null. The row's `unreadable_fields` list names every
field Revit would not surrender, with the reason; `not_applicable` (a schedule
has no view plane) is simply absent without a note; and the coverage block at the
bottom of every reply aggregates what could not be read, because **an unreadable
fact can never become a pass**.

## The audit: findings, not prose

`horizun_audit_planimetry` evaluates the same snapshot and returns findings —
each one `blocking`, `advisory` or `unknown`, each citing the requirement set
(id, version, SHA-256) that produced it, each carrying `observed` vs `expected`
and, where geometry decided it, a location with its coordinate system and units.

There is **no 0–100 score**. A single number invites the reader to stop reading;
the findings are the deliverable.

### Universal checks

The built-in catalog (`horizun-universal-planimetry`, version 1.0.0 — published
in every reply) contains only rules that are true without a company standard:

- **Sheets / layout**: a non-placeholder sheet with zero title blocks; more than
  one title block; two viewports (or a viewport and a schedule, or two schedules)
  that share area beyond an explicit tolerance — *touching edges are NOT an
  overlap*; a placement wholly outside the sheet's extent; a viewport or schedule
  placement whose target no longer exists.
- **Views**: a view held by more than one viewport; a broken parent/callout
  relation. A view without a template and a view on no sheet are **advisory** —
  working views legitimately have neither, and a requirement set is where they
  become blocking.
- **Dimensions**: `AreReferencesAvailable=false`; a genuinely broken reference —
  references into RVT links are labelled linked and **never counted broken**; a
  value override is **advisory by default** (`forbid_numeric_override` makes it
  blocking); a non-view-specific Dimension is reported as a model *constraint*,
  not a defect.
- **Tags**: orphaned; duplicated (same type, same view, same complete target
  set — a multi-reference tag is keyed by its whole set); no owner view; a
  linked target is `unknown` ("not inspected" is not "broken").
- **Text**: empty or whitespace-only; no owner view.
- **2D detail**: no or missing owner view; a degenerate curve; an incompletely
  read region.
- **References**: a target view that is gone; a target the API cannot identify
  (`unknown`, with the reason); a referenced view on no sheet (advisory).
- **Crop**: an annotation or detail element demonstrably outside an ACTIVE crop.
  "Demonstrably" is load-bearing: the finding exists only when the crop is
  active AND its shape was read AND the element's box was read. Missing any of
  the three, the rule stays silent for that element and the element is reported
  `unknown` — never guessed either way.

Every check reports its population and status: `passed` requires a non-empty
population and zero unknowns; a check that examined nothing is `not_applicable`,
and a check with unknowns is `unknown` — **never passed**.

### Requirement sets: the standard as an argument

Everything with a number or a name in it — margins, gaps, allowed scales,
templates and types, naming patterns, required parameters, which categories must
be tagged — is a standard, and a standard arrives as the **inline**
`requirement_set` argument. There is no file-path variant: a read-only auditor
that opens arbitrary paths on the machine is a file reader wearing an auditor's
name.

```json
{
  "requirement_set": { "id": "acme-planimetry", "version": "2.1.0", "title": "Acme sheet standard" },
  "rules": [
    { "id": "sheet-number-format", "entity": "sheet", "severity": "blocking",
      "selector": { "applies_to_all": true },
      "assertion": { "field": "sheet_number", "operator": "matches", "value": "^A-[0-9]{3}$" } },
    { "id": "plan-templates", "entity": "view", "severity": "blocking",
      "selector": { "view_type": "FloorPlan" },
      "assertion": { "operator": "allowed_template", "value": ["ARQ-PLANTA-50", "ARQ-PLANTA-100"] } },
    { "id": "viewport-margin", "entity": "viewport", "severity": "advisory",
      "selector": { "applies_to_all": true },
      "assertion": { "operator": "inside_extent", "value": 10 } },
    { "id": "doors-tagged", "entity": "view", "severity": "blocking",
      "selector": { "view_type": "FloorPlan" },
      "assertion": { "operator": "requires_tag", "value": [
        { "category": "OST_Doors", "exclude_type_matches": "^TMP-",
          "exclude_when_parameter_set": "NO_TAG" } ] } }
  ]
}
```

Entities: `sheet`, `view`, `viewport`, `schedule_placement`, `dimension`, `tag`,
`text_note`, `detail_2d`, `view_reference`. Selectors: `field` (equals),
`field_matches` (regex), `field_in` (list), `applies_to` (explicit ids), and
`applies_to_all: true` for the deliberate match-everything — an *empty* selector
is refused, because a rule left empty by an edit is indistinguishable from one
that meant to match everything. Field operators: `matches`, `not_matches`,
`equals`, `not_equals`, `in_list`, `not_in_list`, `required`, `not_empty`,
`greater_than`, `less_than`, `between`. Whole-entity operators: `minimum_gap`,
`inside_extent` (both in the call's units), `allowed_type`, `allowed_template`,
`allowed_scale`, `required_parameter`, `forbid_numeric_override`,
`requires_tag`. `parameter:<name>` reaches project parameters on sheets and
views.

A malformed set is **refused whole** with a sentence its author can act on — an
unknown operator, a field the entity does not have, a duplicated rule id, a regex
that will not compile, an oversized document. Every regex runs under a 250 ms
match timeout; a timeout is `unknown` for that element, never a pass and never a
crash. The set's SHA-256 is computed canonically (key order does not matter) and
stamped on the reply and on every finding.

`requires_tag` counts only elements **visible in the view** — where "visible"
is decided by substance, not by Revit's view-scoped collector: the element is
not hidden in the view, yields a bounding box in it, and (when the crop is
active) that box intersects the crop. Measured twice on Revit 2023, the
view-scoped collector omits elements that are demonstrably in the view until its
graphics regenerate, and a tag rule built on it would fabricate "nothing to
tag". The check separates host elements from linked ones (a linked element is not this model's to tag and is
reported separately, never blamed), names the exact untagged ElementId, honours
exclusions by type, family, type-regex and parameter, and declares itself
incomplete beyond 2,000 enumerated elements — the findings are then a lower
bound, and the rule says so. It walks each selected view's visible set, so on a
large model scope it with `view_ids`.

### Coverage, and why `unknown` never passes

Every reply carries `coverage_complete` and the reasons it is false: collection
passes that died (`checks_failed`), elements or fields Revit would not read,
closed worksets (elements not in the document at all), unloaded links. When
coverage is incomplete the reply's `note` says, in words, that the absence of a
finding is not a pass — and the live gate proves that behaviour by unloading a
link on purpose and asserting the degradation is surfaced.

`unknown` findings exist so that "could not be measured" is visible per element:
a viewport whose outline would not read is in **no** overlap answer, and the
check that examined it reports `unknown` rather than `passed`.

### Navigating to a finding

Every finding carries `element_ids`, and usually `sheet_id`/`sheet_number` and
`view_id`. `horizun_navigate` can select those ids or open the view; a geometric
finding also carries a `location` point in its declared frame. `recommended_tool`
names the typed command a person could use (`horizun_edit_dimensions` for an
override, `horizun_manage_views` for a broken placement,
`horizun_delete_verified` for an orphan).

## The fix: findings become typed corrections

`horizun_fix_planimetry` is the write half. It never decides anything: every
final value — the template, the scale, the name, the number, the type, the point,
the crop — arrives explicit in the request, and a missing instruction is a
refusal rather than a choice.

### The nine operations

| Operation | Target | What is re-read after the commit |
| --- | --- | --- |
| `set_view_template` | `view_id` + `template_id` | `View.ViewTemplateId` |
| `set_view_scale` | `view_id` + `scale` (1..24000) | `View.Scale` |
| `rename_view` | `view_id` + `new_name` | `View.Name` |
| `rename_sheet` | `sheet_id` + `new_number` and/or `new_name` | BOTH `SheetNumber` and `Name` — the field that was not renamed is re-read against its before-value, so a fix that quietly moved it fails |
| `place_title_block` | `sheet_id` + `title_block_type_id` | the instance, its owner sheet, its symbol, its category, and a title-block count that must be exactly 1 |
| `move_viewport` | `viewport_id` + `point` (sheet coordinates) | `Viewport.GetBoxCenter()` within the declared tolerance, plus whether the placement still intersects the sheet extent |
| `move_schedule` | `schedule_instance_id` + `point` | `ScheduleSheetInstance.Point` within the declared tolerance |
| `clear_element_override` | `view_id` + `element_id` | the element's `OverrideGraphicSettings` back to defaults, AND proof that the CATEGORY override and the view template did not move |
| `set_crop` | `view_id` + rectangular `crop` (view-plane) | crop active, crop visibility unchanged, and the committed shape within tolerance |

Scale, template and title-block types are always ElementIds; nothing is resolved
from a name, because two elements may share one.

### The finding is the only licence to write

Every action carries the finding it corrects, copied from the audit reply:

```json
{
  "operation": "set_view_template",
  "view_id": 501, "template_id": 733,
  "finding": {
    "rule_id": "view.no-template",
    "requirement_set": "horizun-universal-planimetry",
    "requirement_set_version": "1.0.0",
    "entity_kind": "view",
    "view_id": 501, "element_ids": [501],
    "observed": { "template_id": null, "view_type": "FloorPlan" }
  }
}
```

Before any transaction opens, the command recomputes the whole audit and refuses
the action when:

- **the finding is gone** — no current finding shares its identity (rule, set,
  sheet, view, element set). It was already fixed, the elements changed, or the
  block was mis-copied. `STALE FINDING`.
- **the observation moved** — the finding still exists but no longer shows the
  state the caller approved a fix for. `STALE OBSERVATION`, and the refusal
  prints both the cited and the current block rather than sending somebody to
  diff JSON by hand.
- **the finding is `unknown`** — something could not be measured. That is the
  absence of a fact, not a defect, and correcting on top of it would write over
  a state nobody read.
- **the operation does not address the rule** — universal checks name their own
  remedies (an overlap is corrected by moving a placement, never by renaming a
  sheet); a requirement-set rule is judged by its `entity_kind`.
- **the requirement set was modified** — a finding from an inline set requires
  that set inline, and its canonical SHA-256 must equal the one the finding
  cites. A fix judged by different rules than the audit is not a fix.

Checks whose severity is `unknown` can never be cited by any operation at all —
that is a property of the catalog, not a runtime test.

### Rehearsal, atomicity, and what a re-read proves

`dry_run` defaults to true. The rehearsal **materialises the whole batch inside a
transaction**, measures every postcondition in that provisional state, and rolls
back; a rollback Revit does not confirm withholds the token and reports the call
as `uncertain`, because the provisional elements may still be there. The token
binds the request AND the resolved elements' before-state, so a model that moved
between rehearsal and apply refuses as `stale_plan`.

The apply commits ONE `TransactionGroup`. While that group is still open, any
action that throws, any postcondition that does not match and any check that
could not be measured rolls the **entire batch** back.

There is exactly one state that is not a rollback, and it is worth being precise
about because it is the limit of the atomicity claim: after the group has been
assimilated, the same checks run again over the settled model. If that re-read
contradicts the reversible-state check, there is nothing left to roll back. The
reply is then `uncertain` — never "partly applied" — because two measurements of
one fact in contradiction are the absence of knowledge, not half of it, and the
message says to inspect the model before any retry.

### Resolution is the auditor's verdict

After the commit the **full audit runs again**. A partial re-evaluation of only
the affected checks is not demonstrably equivalent — overlap, containment and
coverage are cross-entity — so the complete universal catalog, and the
requirement set when one was given, are re-run.

The reply then separates:

- **`resolved`** — the finding's own rule no longer produces it. A verified
  postcondition does *not* by itself make a finding resolved.
- **`persistent`** — the rule still fires over those elements, even if the typed
  write landed exactly as requested.
- **`new_findings`** — findings that existed in neither the before set nor the
  selection. Resolving one finding must never hide that another appeared.
- **`undetermined`** — selected findings the re-audit could not decide about,
  because a collection pass **died** and left its population empty. The
  inventory does not throw when a pass dies; it records the failure and returns
  that population empty, so classifying purely on absence would read a dead
  views pass as "every view finding resolved" and zero the new list at the same
  time. Absence from an uncollected population is not absence of defect, and
  `new_findings_complete` says when the new list is a lower bound.

`coverage_before` and `coverage_after` travel with them, because a finding that
disappeared into incomplete coverage has not been fixed.

If the re-audit itself fails, **nothing is declared resolved** and the reply says
so.

### What the fix deliberately will not do

Published in `not_covered` on every reply, so their absence is a decision:
automatic sheet packing, auto-tagging, dimensioning by intent, revision
generation, visual judgement, and any implicit choice of type, position, name or
standard.

## Examples

The census:

```json
{ "mode": "inventory" }
```

Everything on two sheets, in millimetres:

```json
{ "mode": "placements", "sheet_ids": [123101, 123102], "units": "mm" }
```

The audit a delivery review starts from:

```json
{ "scope": "model", "include_advisory": true, "max_findings": 200 }
```

A response, abbreviated to one finding:

```json
{
  "blocking_total": 1, "advisory_total": 3, "unknown_total": 0,
  "coverage_complete": true,
  "findings": [
    {
      "rule_id": "sheet.viewport-overlap",
      "requirement_set": "horizun-universal-planimetry",
      "requirement_set_version": "1.0.0",
      "severity": "blocking", "status": "failed",
      "entity_kind": "viewport",
      "sheet_id": 123101, "sheet_number": "A-201",
      "element_ids": [4567, 4568],
      "observed": { "overlap_x": 12.0, "overlap_y": 8.0, "overlap_area": 96.0, "units": "mm" },
      "expected": { "overlap_area": 0, "tolerance": 0.1 },
      "location": { "coordinate_system": "sheet", "units": "mm", "point": [420.0, 185.0] },
      "fixable": false, "recommended_tool": null, "coverage_complete": true
    }
  ]
}
```

## Limitations — deliberate, and published in every reply

The audit's `not_covered` block names the judgements this phase does not make,
so their absence is never read as "the model is fine in these respects":

- **which elements should be dimensioned** — design intent, not a database fact;
- **whether a dimension chain is architecturally correct** — a discipline
  judgement;
- **whether a sheet is visually balanced** — needs visual analysis;
  `horizun_capture_view` can hand the image to a human or a later phase;
- **whether a note is technically correct or a detail complete** — domain
  review;
- **applying any correction** — `horizun_audit_planimetry` itself is read-only
  and `fixable` stays false on every finding it returns. Correction is a
  separate, explicitly confirmed call: `horizun_fix_planimetry` above, which
  covers the nine operations listed there and refuses everything else by name.

Two measured behaviours worth knowing: Revit may return **no bounding box** for
an element hidden by an active crop, in which case the element is reported
`unknown` (bounds unreadable) rather than "outside the crop" — the geometric
finding appears only when the box was actually read; and the `GuideGrid` class
does not exist in any supported Revit API (2023–2027, checked by metadata), so
the guide grid is read through `BuiltInParameter.SHEET_GUIDE_GRID` and resolved
as an ordinary element.

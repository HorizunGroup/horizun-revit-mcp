# Deliverable production

These additions extend the existing typed tools. They do not deploy a build or
constitute live Revit acceptance.

## Annotation production

- `horizun_plan_annotations.distance_space`: `model` (compatible default) or
  `paper`. Scales layout distances only, never model/probe coordinates.
- `auto_tags` emits `avoid_collisions=true` and `require_tag_text=true`.
  The writer provisionally creates the real tag, measures native extents,
  searches inside `layout_max_displacement`, and verifies view, type, head
  position, orientation, leader presence, orphan state, text and clearance.
  Unknown obstacle bounds refuse instead of disappearing.
- Collision boxes include native leaders and are deliberately conservative.
  This can refuse a visually acceptable placement. It is not exact glyph
  intersection, leader routing or an aesthetic approval.
- `intent_dimension.reference_targets` allows distinct selectors for the same
  element, e.g. exterior/interior wall faces for thickness. It cannot be mixed
  with `element_ids`. Each selector must produce one compatible reference.
- `dimension_set` composes named roles such as general, partial, thickness and
  openings using explicit types, sides, offsets and native reference intent.
  Partial coverage or duplicate reference sets refuse the complete proposal.
  Native dimension rehearsal still verifies values/references; global text
  spacing is reviewed on the capture, not claimed by the planner.

## Paper composition

`horizun_pack_sheets` accepts `usable_rect:[minX,minY,maxX,maxY]` and
`reserved_zones:[[minX,minY,maxX,maxY],...]`, in paper coordinates and the
requested units. Text, tags and generic annotation symbols on the sheet are
also measured as obstacles. Titleblock artwork is represented by explicit
reserved zones rather than its usually whole-sheet bounding box.

Use either `sheet_id` for existing single-sheet behavior or `sheets` for
ordered candidate sheets. Multi-sheet distribution accepts up to 100 unplaced
views/schedules, tries first-fit at unchanged scales, and applies the complete
assignment through an atomic plan. It never invents sheets, reduces scale or
claims the first-fit search proves that no other arrangement is possible.
Unknown bounds or an unconfirmed rollback stop the run.
Capacity refusals carry a structured `packing_no_fit` code; unreadable geometry
is not interpreted as a reason to silently move on to another candidate.

## Staged delivery profile

Call `horizun_plan_views` with `operation:deliverable_set` and a
`delivery_profile`. This is an executable-by-client stage plan, **not an
autonomous executor or a blanket approval**. Create new room views with the
existing room workflow first; resolve their real IDs before requesting delivery.

Example profile (all IDs and paths are illustrative):

```json
{
  "id": "architectural-delivery",
  "version": "1",
  "units": "mm",
  "views": [{
    "view_id": 101,
    "dimension_sets": [{
      "role": "thickness",
      "operation": "intent_dimension",
      "reference_targets": [
        {"element_id": 501, "selector": "exterior_face"},
        {"element_id": 501, "selector": "interior_face"}
      ],
      "offset": 10,
      "side": "positive",
      "dimension_type_id": 301
    }],
    "tags": {"element_ids": [501], "tag_type_id": 401,
             "clearance": 3, "max_displacement": 20}
  }],
  "packing": {
    "sheets": [
      {"sheet_id": 201, "usable_rect": [10, 10, 700, 500]},
      {"sheet_id": 202, "usable_rect": [10, 10, 700, 500]}
    ],
    "items": [{"key": "plan", "view_id": 101}],
    "margin": 5, "gap": 10
  },
  "publication": {
    "format": "pdf", "view_ids": [201, 202],
    "output_path": "C:/ApprovedOutput/delivery.pdf",
    "pdf_combine": false, "overwrite": false
  },
  "requirement_set": {
    "requirement_set": {"id": "sheet-standard", "version": "1"},
    "rules": [{
      "id": "number", "entity": "sheet",
      "selector": {"applies_to_all": true},
      "assertion": {"field": "sheet_number", "operator": "matches", "value": "^A"}
    }]
  }
}
```

The stage plan activates each annotation view, plans dimensions, replans tags,
captures the view, packs after annotation changes, audits the published sheets,
requests visual approval, and prepares publication. Every writer needs a fresh
rehearsal/confirmation against the preceding stage's result. Use stage keys and
receipts to resume without blindly replaying completed writes. Failures do not
roll back already completed stages; publication is external I/O.

## PDF acceptance

`pdf_combine=false` produces deterministic `stem-001-viewId.pdf` names via
single-view exports. Combined mode produces the requested single PDF.
Exported PDFs are reopened with pinned
[PdfPig 0.1.13](https://www.nuget.org/packages/PdfPig/0.1.13); expected file/page
counts and page geometry are inspected. `emit_manifest=true` writes and rereads
`output_path.manifest.json` with SHA256, source views and revision IDs.

The source/page association records the export invocation, not independent
semantic recognition of the printed page. File/page verification does not
certify legibility or drawing completeness. Export/verification failures report
that files may exist and that there is no transaction rollback for external files.

## Stabilization block (2026-09-08)

These close the release requirements the 2026-09-07 review listed. Each is
verified live in Revit 2026 on an isolated candidate; see
[STABILIZATION-LOG-2026-09-08.md](STABILIZATION-LOG-2026-09-08.md).

- **Annotation coverage.** `horizun_plan_annotations auto_tags` and every
  `horizun_annotate` tag with `avoid_collisions` report `annotation_coverage`:
  one verdict per annotation (`measured`, `excluded_element_hidden`,
  `excluded_category_hidden`, `excluded_annotation_categories_hidden`,
  `excluded_not_visible_in_view`, `excluded_invalid_element`,
  `accepted_unmeasurable`, `unknown_unreadable_extent`,
  `unknown_visibility_undecidable`), the bounds source (`annotation_crop`,
  `model_crop`, `none`) and `clearance_scope` (`complete`, `partial`,
  `undecided`). A readable extent is always an obstacle; an unreadable one is
  excluded only when the element is explicitly hidden or when Revit's own
  view-scoped collector does not list it; anything else blocks by id. The
  caller may accept exact ids with `accept_unmeasurable` /
  `layout_accept_unmeasurable`; the clearance is then `partial`, never
  collision-free. Dependent views are surveyed through their primary.
- **Print policy.** `horizun_export pdf_print` is a closed set of 20 options
  (the 21 `PDFExportOptions` properties, identical in 2023-2027; `export_in_background`
  is 2025+ and refused earlier). Each option is reported as
  `requested`/`defaulted`, `applied` (read back from the option object) and
  `verified`/`verified_mismatch`/`requested_unverifiable`; only
  `paper_format` and `orientation` are proved from the produced page. With
  `paper_format=Default` a page larger than the titleblock's declared
  `SHEET_WIDTH`/`SHEET_HEIGHT` is a composition defect (something lies outside
  the titleblock) and fails the export by name - inside the paper is not
  inside the frame. Measured on 2026 and built in: `placement` is
  `center|lower_left` (Revit's Margins is the same enum value as LowerLeft);
  an option the exporter reads back different from what was set is
  `applied_mismatch` and fails the export; `export_in_background=true` is
  refused on every year because the export returns before the file exists;
  and `origin_offset_x/y` require `zoom='zoom'` with an explicit
  `zoom_percentage`, because under `fit_to_page` Revit fits the sheet to the
  whole paper and the offset pushes it off the page (rendered and looked at:
  10/20 mm offsets clipped 20 mm at the top and 6 mm at the right of an A3).
  The bridge cannot read that clipping from the file, so the combination that
  produces it is refused instead of reported as unverifiable.
- **`view_scale`** on every operation whose result has a drawing scale, checked
  with `View.IsValidViewScale`, refused when a template controls View Scale or
  the result is a sheet/schedule/perspective, and re-read per row.
- **`fits_titleblock_cell`** (requirement-set operator on `sheet`): the
  caller's cell geometry decides whether `sheet_number`/`name` fits; an
  estimate, declared as such; an overflowing value is a finding, never
  trimmed. Put it in the delivery profile's `requirement_set` so that the
  audit stage - and therefore the publish gate - catches overflowing numbers
  on named paper formats, where page geometry cannot.
- **Preflight.** `deliverable_set`/`delivery_open` validate every stage's
  arguments statically and against the document (views, dimension/tag types,
  elements, sheets with exactly one titleblock, `Viewport.CanAddViewToSheet`,
  placed schedules, output directory writability and overwrite conflicts) and
  withhold the whole plan on any error, listing all of them by stage and field
  plus what only a rehearsal or the export can decide.
- **Delivery ledger.** `horizun_plan_views delivery_open / delivery_status /
  delivery_record / delivery_approve / delivery_invalidate` keep one
  append-only event file per delivery under the data root. Stages carry
  explicit statuses and dependencies; a completed write records its
  `idempotency_key` and the `VersionGuid` of every element it declared; status
  re-reads those and invalidates changed or deleted scopes in cascade;
  approvals bind a named identity to the sheet and its placements; the publish
  gate opens only on a complete, audited (`no_blocking_findings=true`),
  currently-approved run. Re-opening an existing `delivery_id` answers
  "already exists - resume it with `delivery_status`" BEFORE the plan is
  preflighted (measured on 2026: the model has usually changed by the time a
  run is resumed, and a preflight finding would hide the resume path). `horizun_export delivery_id` refuses while the gate
  is closed and records the publish stage with hashes when it runs. Atomicity
  is per typed write; navigation, captures and files share no transaction; a
  passed audit authorises nothing beyond what it measured. A stage left
  `in_progress` by a crash is reported as `in_doubt` on resume: it is neither
  offered again nor assumed done, and only an explicit `completed` (with ids
  the host re-reads) or `failed` resolves it.
- **Text positions and leaders (8B).** The design decision - where a text or
  leader goes - is an explicit argument, never a built-in standard.
  `horizun_edit_dimensions`: `text_position` (absolute) or `text_offset`
  (`[dx,dy]` along the owner view's axes, `distance_space` model|paper - a
  qualifier accepted only beside a `text_offset`, refused by name on its own),
  `leader`, `leader_end`, and the same per segment (`segments[].text_position`
  / `text_offset` / `reset_text_position` / `leader_end`). A dimension that
  reports `IsTextPositionAdjustable()=false` refuses; every position is re-read
  within 1e-5 ft. `horizun_transform_elements`: `move_tag_head` (`point` for
  one tag or `vector` for many) and `set_tag_leader` (`has_leader`,
  `leader_end_condition` attached|free, `leader_end` for a free end,
  `leader_elbow`, `leader_visible`), refusing what
  `CanLeaderEndConditionBeAssigned` denies, a free end on an attached leader, a
  leader edit on a tag without one, a pinned tag and a tag with more than one
  tagged reference. Measured limits, not guesses: room/space/area tags are not
  covered by these operations; the Revit API offers no way to create a label
  inside an annotation family (`FamilyItemFactory` has `NewModelText` only), so
  a labelled tag family must come from a person or a library RFA; legibility
  remains a visual review, not a claim of the bridge.

## Required live acceptance

Use disposable models with Revit 2023–2027 and matched server/add-in binaries.
Test:

1. Long/short tag text, leaders, orphan/empty tags, unreadable obstacles, crowded
   layouts and views at 1:50 / 1:100 / 1:200.
2. Wall thickness from opposite faces, general/partial/opening sets, ambiguous
   references, repeat requests and reference failures.
3. Titleblock bands and sheet notes, viewport titles, schedule extents, overflow
   to the next candidate and failure with no partial committed placement.
4. PDF combined/separate modes, exact page counts, revisions, missing outputs,
   overwrite refusal and partial export failure.
5. The complete staged flow, with interrupted runs resumed from receipts and
   publication withheld until audit and visual approval.

Unit tests and API compilation are necessary, but none replaces these checks.

`scripts/live/verify-deliverable-visual.ps1` builds the page item 1 and item 4
need a person for: a plan at 1:100 in a disposable fixture carrying a dimension
whose text was displaced by an explicit paper offset and a tag with a leader,
cropped, placed through the packer and exported as one verified PDF page. It
writes `visual-request.json` — what was asked for beside what was re-read — and
declares its own last probe `not_covered`, because legibility is a judgement and
a harness that scored it would be scoring its own request. Rendering the page is
what closes it; `artifacts/.../visual-review.md` records one such review. The
harness reports `fixture_missing` by name when the document has no tag family
that can tag its host: the API cannot create a label inside an annotation family,
so a labelled tag family still has to come from a person or a library RFA.

## Verification recorded — 2026-09-07

- Core: 3,798 passed, zero failed or skipped. Includes paper-unit normalization,
  bounded-layout inputs, capacity vs. unreadable geometry, actual PDF parsing,
  stage order, published tool fields and withholding publication approval.
- Server: 477 passed, zero failed or skipped.
- Revit API builds: 2023, 2024, 2025, 2026 and 2027, each with zero warnings/errors.
- Public consistency: passed under Windows PowerShell 5.1 and PowerShell 7.
- `git diff --check`: passed; Git only reports line-ending normalization notices
  for restored lock files.

Local test receipts are under `artifacts/deliverable-production-2026-09-07/`
(`core.trx`, `server.trx`); artifacts are not published source documentation.
PdfPig assemblies were present in build output and the existing payload staging
helper copies all output DLLs. No install, client registration, release, model
write or live Revit acceptance was performed. Version remains 1.2.1 in this
working tree; a passing build is not evidence that the running add-in changed.

### Subsequent live campaign

The later isolated Revit 2026 campaign found and corrected PDF filename and
manifest roundtrip defects, and an explicitly-hidden annotation obstacle bug.
The final focused run passed 13 checks but still failed its native-tag case;
visual production acceptance and the full supported-year live matrix remain
open. Final offline counts are 3,800 Core and 477 Server tests. The detailed
[candidate review](RELEASE-CANDIDATE-REVIEW-2026-09-07.md) separates build hashes,
failed attempts, fixture limitations and remaining release requirements.
These results supersede neither the clean-candidate nor installer release gate.

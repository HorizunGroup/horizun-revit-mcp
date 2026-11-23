# PDF QA — reading the paper back

`horizun_export` with `pdf_qa` (on a `format=pdf` export, or on its own with
`format=pdf_qa` and an existing `path`) reads every PDF back with PdfPig 0.1.13,
the reader the add-in already ships. Nothing is rendered to a picture: PdfPig
reads the drawing operators, it does not draw them. Findings are reported, never
fatal.

## Verdict

- `findings`: something is wrong on paper (blank page, text over text, text
  outside the frame, a missing or substituted font, a note not found or printed
  on another number of lines than `expected_lines`).
- `partial`: nothing wrong was found, but something that was asked could not be
  measured. `incomplete` lists each reason: a sheet that could not be mapped onto
  its page (and why), an approximate mapping, a page whose sheet number is not
  among its words (`page_order_unverified`), a viewport with no ink assigned, a
  region that could not be measured or whose gray could not, a note printed more
  than once (`ambiguous`), a `compare_to` that could not be read.
- `clean`: everything asked was measured and nothing is wrong.

On a `format=pdf` export, the reply's `qa` block counts `clean`, `partial` and
`with_findings` files.

## What it measures

| Option | What comes back |
|---|---|
| (always) | fonts used and substituted, blank pages, text over text, text outside the frame |
| `expected_fonts` | missing fonts; each substituted font carries `installed` (`user`, `machine`, `both`, `none`, `unknown`) and `advice`; `install_advice` lists the fonts installed for this Windows user only, with the advice to install them **for all users** (right-click the font file > *Install for all users*, needs administrator rights) and export again |
| `viewports: true` | per viewport: `box_mm` (Revit's box on the sheet), `ink_mm` (ink assigned to it), `ink_minus_box_mm` {left, bottom, right, top}, `max_difference_mm`, `center_offset_mm`, `ink_from_box_center_mm` (null when the mapping is approximate), the title's `label_ink_mm`, its `gray` and `flags` (`empty`, `off_box`: ink centre 25 mm or more from the box centre, `overflows_box`: ink 5 mm or more past a side, `touches_page_edge` (ink within 0.5 mm of the page box edge), `no_box` (Revit reported no box); `off_box`/`overflows_box` are not computed on an approximate page (`flags_basis: "approximate"`); leads, not a verdict; listed in `viewports.flagged`); `ink_found: false` with `why` when nothing was assigned. Per page (`viewports.pages`): `scale_pt_per_mm`, `aspect_sheet`, `aspect_page`, `mapping` (`exact`, `approximate`, `unmapped` with `why`), `sheet_number_on_page`, `page_order_unverified` |
| `regions` | each region by `rect_mm` (page mm, origin bottom-left) **or** `viewport_id` (its box), optionally narrowed to one `element_id` inside that viewport, on `page`; returns the ink box and `gray` |
| `notes` | per note (`text` or `note_id`, optional `page` and `expected_lines`): `scope`, `found`, `ambiguous`, `page`, `lines`, `line_texts`, `box_page_mm`, `matches_expected`; `ambiguous: true` with `candidates` and no line count when the text is printed more than once in the searched area |
| `compare_to` | an earlier `.pdf`: per page `words` (removed/added), `fonts` (removed/added), path, segment and image counts before/after, `letters` before/after and the deltas (`letters_delta`, `paths_delta`, `segments_delta`), `ink_box_mm` and `ink_box_shift_mm`; `verdict` `same` or `changed` |

With `format=pdf_qa`, `viewports`, viewport regions and element regions need
`view_ids`: the sheets, in page order. On a `format=pdf` export the sheets are
the exported ones, in their export order.

## How ink is assigned to a viewport

1. The sheet outline is fitted to the page, centred, with one uniform scale.
   The mapping is `exact` only when that scale is 72/25.4 pt per mm and the
   page's aspect ratio is the sheet's, both within 0.5 % (`paper_format`
   Default). Anything else (a zoom, another paper size, a rotation) is
   `approximate`: Revit is assumed to have centred and scaled the sheet, the
   verdict is at most `partial`, and `ink_from_box_center_mm` is not offered.
   The sheet number is looked for among the page's words (one to three
   consecutive words, punctuation ignored). It confirms the page only inside
   the title block's box, or when it is printed exactly once on the page (a
   "1" that is also a detail number proves nothing); otherwise the page order
   is unverified.
2. Each drawn item (stroke, fill, image, letter) goes first to the viewport
   whose **title** box contains it; otherwise to the viewport whose box, grown by
   10 mm, contains the item's centre and is not smaller than the item; the
   nearest box wins. Title-block lines and other items that fit no box stay
   unassigned.
   A stroked path can contribute several boxes, one for each subpath, including
   when the operator also fills the path. The viewport survey keeps up to 20,000
   marks per page. If it omits marks, `ink_items_truncated` flags the partial
   measurement, `incomplete` explains the limit, and `ink_from_box_center_mm`
   is withheld.
3. `ink_from_box_center_mm` is the ink box relative to the box centre. It does
   not change when the viewport moves, so it is what `horizun_sheet_layout`
   `move_viewport` takes as `ink_box` with `anchor: "ink"` to place a viewport by
   its printed ink. The move does not measure the ink again (`ink_remeasured:
   false`): export again and run `pdf_qa` to confirm.

## How a note is found

- With `note_id` and the sheets known (`view_ids` on `format=pdf_qa`, or the
  exported sheets), the search covers only the page of the sheet that shows the
  note (the sheet itself, or the sheet holding a viewport of the note's view)
  and only the note's box carried to that sheet, grown by 2 mm (`scope:
  note_box`). When the view is on several printed sheets (a legend), `page`
  says which one; without it the note is not searched. A note whose view this
  file does not show, or not on the page asked, is reported with
  `not_measured: true` and makes the verdict `partial`, not `findings`.
- By `text`, or `note_id` without sheets, the search covers the page asked or
  every page (`scope: page` / `all_pages`).
- A match that the same line goes on after (the next word of the same note -
  same font and size - on the same baseline, no farther than two letter
  heights) belongs to a longer text and is not counted. A dimension or a label
  in another font or size beside it does not void the match. Two or more
  matches answer `ambiguous`.
- Region names must be unique (the reply and its reasons are keyed by them).

## How gray is measured

A coarse software raster of the vector drawing, in painter's order: opaque fills
(non-zero and even-odd), strokes by their width (a stroke thinner than a cell
covers it in proportion), white paints over. Cells default to 0.1 mm and grow so
that a region never has more than 250,000 cells; past 30 million cell visits
the result is `truncated`. `mean_darkness` is 0 (white) to 1 (black);
`mean_gray_255` is the same as a gray level; `histogram` has 10 bins of
darkness. Cells under text and under images are left out of the tone
(`text_fraction`, `image_fraction` say how much of the region they cover).

## Limits — say them when you report

- No picture: cells under text are left out of gray and images are not
  decoded. For a picture use `horizun_capture_view` on the sheet.
- Line widths are taken as written in the content stream; a width scaled by the
  current transformation matrix is not rescaled.
- Clipping paths are not applied to what they clip; items outside a viewport's
  clip still count where they were drawn.
- Transparency and blend modes are treated as opaque.
- A viewport's ink depends on the 10 mm margin: two viewports closer than that
  may share items, and the nearest box takes them.
- Note lines are grouped by baseline; a note rotated or split across columns can
  read as more lines.
- The comparison is per page index; inserting a page shifts every later page.

## Related writes (5.2)

- `horizun_manage_views` `place_view` with `detail_number` refuses a number
  already used on the sheet (naming the viewport that holds it, compared without
  case) or twice in the batch, before `Viewport.Create`, and re-reads the new
  viewport and its number after. A viewport placed earlier in the same batch
  without a number, which Revit auto-numbered with the number asked later, is
  moved to the next free number and reported (`detail_number_renumbered` on its
  row, `detail_numbers_moved` in the reply: id, number before, number after).
  `"detail_number": null` is refused.
- `horizun_sheet_layout` `set_title` with `label_offset` [x, y] mm sets
  `Viewport.LabelOffset` explicitly, re-reads it, checks the title moved with it
  and returns `title_check` (`outside_mm`, `over_datum`: the title off the sheet
  or over a grid or level head). The title's movement is compared by its
  lower-left corner (so `line_length` does not turn it red) and only where the
  title is shown before and after.
- `horizun_view_crop` crops an orthographic 3D view along the camera's right
  and up axes. `set_crop_from_elements` also sets and verifies an active
  section box. The confirmation token covers the planned bounds and frame.
  `read` returns Revit's view rectangle in `crop` and reports an active
  `section_box` separately. `effective_crop` gives the bounds of the section
  box clipped by the active view rectangle along the viewing direction.
  `effective_crop.depth_mm` measures depth along that direction. An empty
  intersection returns `effective_crop: null` and
  `effective_crop_status: "empty"`. This command refuses perspective views.
  `set_annotation_crop_offsets` applies to 2D views.
- `horizun_planimetry_ops` `datum_extents` and the bubble stagger report
  `label_offset_rewritten` when Revit rewrote a viewport's `LabelOffset` while a
  datum's 2D extent changed.

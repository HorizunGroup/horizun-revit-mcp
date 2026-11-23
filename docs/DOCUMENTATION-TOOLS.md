# Documentation tools in Revit

## Sheet lists with Project Information parameters

`horizun_manage_schedules` accepts `field_type` on each `add_fields` entry. A
sheet list can expose the same shared parameter twice with the same parameter
id: once as a sheet instance field and once as `ProjectInfo`. Choose the latter
for a value stored in Project Information. The command re-reads the added
field's type after the transaction and includes it in the definition fingerprint.

```json
{
  "target_document": "MyProject",
  "actions": [
    {"operation": "add_fields", "schedule_id": 123,
     "fields": [{"name": "HZ_Resp_Director", "field_type": "ProjectInfo"}]}
  ]
}
```

After applying that action, rehearse and apply the sorting action:

```json
{
  "target_document": "MyProject",
  "actions": [
    {"operation": "set_sorting", "schedule_id": 123,
     "sorting": [{"field": "HZ_Resp_Director", "direction": "ascending"}]}
  ]
}
```

`set_sorting.field` names a field on the schedule at application time. If a name
identifies several existing fields, use `parameter_id`; if that also collides,
edit the schedule so the choice is unambiguous.

## Schedule appearance, split schedules and colour schemes

Three groups of operations that were missing from the documentation surface. Every
write rehearses (`dry_run` is the default), issues a `confirmation_token`, applies,
and re-reads the committed model. Nothing here was measured in a live Revit when it
was written; the rehearsal is the real call, so a refusal arrives with Revit's own
sentence and nothing committed.

### Appearance: `horizun_manage_schedules` `set_appearance`

| argument | what it does |
|---|---|
| `scope` | The region: `title`, `headers` (the column heading rows), `body`, or `field` (one column, named with `parameter_id`, `name` or `field_index`, like `set_field`). |
| `text_type` | Name or id of a text note type for the whole region (`title`, `headers`, `body`): Revit's own *Title / Header / Body text*. |
| `style` | Cell-level overrides, only the attributes you name: `font`, `size_pt`, `bold`, `italic`, `underline`, `color`, `fill` (`#RRGGBB`), `h_align`, `v_align`, `border` (`top`, `bottom`, `left`, `right`: `none`, a line style id or a line style name). |
| `show_grid`, `show_title` | The schedule's grid lines and title row. |

A `TableCellStyle` carries a value for every attribute and an override flag per
attribute; a value written without its flag is accepted and ignored. The command
flags exactly the attributes you name and re-reads exactly those, so "bold did
nothing" is a failed verification that rolls the batch back. Column width,
alignment and number format were already `set_field` (`width` in mm, `alignment`,
`format`).

The token binds the whole definition fingerprint, and that fingerprint now includes
an `appearance` section (grid, title, segments, region styles, column styles), so a
colleague restyling the table between rehearsal and apply refuses as `stale_plan`,
and the reply's `changed_sections` names `appearance`.

**Conditional formatting has no Revit API** (colour a cell by its value, rules,
icons). Neither this tool nor `horizun_execute_python` can create it, and the
command refuses `conditional_format` with the reason. Alternatives that work:
`set_filters` to split a table into one per state, a per-column style for a fixed
look, and `horizun_manage_views` `color_by_value` for colour by value in a view.
Conditional formatting already in a schedule is left untouched.

### Split schedules: `split`, `unsplit` and `place_schedule` with `segment_index`

* `split` with `parts` (2 to 50 equal segments) or `heights_mm` (the height limit of
  every segment except the last; Revit decides the last from the whole table).
  Refused when the schedule is already split or is a title block revision schedule.
* `unsplit` merges every segment into the first. Merging deletes the merged
  segments' instances on sheets, so the rehearsal reports `sheet_instances_removed`.
* `horizun_manage_views` `place_schedule` takes `segment_index` (0 is the first
  segment) to place ONE segment of a split schedule on a sheet. The index is checked
  against the segments the schedule really has, and the placed instance's
  `SegmentIndex` is re-read. Split in one call, place in the next: the schedule has to
  be split when the placement is rehearsed.

### Colour fill schemes: `horizun_manage_styles`

| operation | what it does |
|---|---|
| `list_color_schemes` | Every scheme (or one `category`: `rooms`, `areas`, `spaces`; or one `scheme_id`, which also lists the parameters it can colour by): parameter, by-range flag, legend title, entries with colour, visibility, `in_use`. |
| `create_color_scheme` | `name` plus `scheme_id` (the scheme to copy) or `category` (copies the first scheme of that category). Revit has no empty-scheme constructor, so a new scheme is a copy; it may take `parameter`, `entries`, `title` in the same call. |
| `edit_color_scheme` | `scheme_id` and any of `parameter` (name or id; changing it clears the entries), `by_range`, `include_links`, `title` (the legend title), `entries`. |
| `apply_color_scheme` | `view_id` + `scheme_id`: applies the scheme to a plan, area plan or legend view (Revit's `CanApplyColorFillScheme` decides; an area plan only takes a scheme of its own area scheme). With only `category` instead of `scheme_id` it REMOVES the view's scheme for that category. |
| `place_color_legend` | `view_id`, `point` (`[x, y]` in `units`), and `scheme_id` or `category`. Applies the scheme first when the view does not already have it; refused when the view has no scheme or already has a legend for that category. |

`entries` is a list of `{ value, color, caption?, visible?, pattern? }`. A new entry
takes a solid drafting pattern unless `pattern` names another, because an entry
without a pattern colours nothing. Numeric values are Revit **internal units**
(feet, square feet). Revit keeps an entry that elements still use when it is left
out of `entries`: the reply lists those as `extra_entries_kept` instead of
failing. By-range schemes take numeric values (where each colour starts) and
`caption`.

Not covered by `horizun_manage_styles`, because it is a tool-level choice: the
`documentation` permission profile does not include this tool.

## Titleblocks from a base family

Authoring a titleblock family from a base family (`horizun_create_family`) is in the Pro edition.

## Project Browser organization

Revit 2023–2027 exposes `BrowserOrganization` for reading the current scheme,
its folder items, sorting order and sorting parameter. The installed public API
has no method to create a scheme, edit its grouping/filter rules, or activate a
different scheme. Setting up grouping by HZ_Uso, HZ_Disciplina and HZ_Grupo for
views, sheets and schedules therefore requires Revit's Project Browser UI or a
template in which those three schemes were already configured. The bridge must
not report this as a verified typed write.

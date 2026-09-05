# Quantities and the budget baseline

Two tools, one join. `horizun_quantities` in mode `takeoff` measures the
quantities **you** name, per element, and tags each with the budget code **you**
name. `horizun_budget_compare` joins that takeoff to a budget baseline read from
an `.xlsx`, and reports per code what changed - quantity, classification and
price kept apart - then writes the result to Excel and/or Power BI, each
destination reported on its own.

Nothing about any organisation's budget is compiled in: not the parameter
that carries the code, not the unit a line is billed in, not a price, not a
catalogue. Everything is an argument.

## What `horizun_quantities` mode `takeoff` measures

```json
{
  "mode": "takeoff",
  "category": "OST_Walls",
  "classification_parameter": "BUDGET_CODE",
  "quantities": [
    { "name": "volume", "source": "geometry_volume", "unit": "m3" },
    { "name": "area",   "source": "parameter", "parameter": "Area", "unit": "m2" },
    { "name": "length", "source": "length", "unit": "m" },
    { "name": "units",  "source": "count", "unit": "un" }
  ],
  "include_links": true,
  "top": 5000
}
```

| `source` | What is read | Unit |
| --- | --- | --- |
| `parameter` | The named parameter, instance first then type. A Length / Area / Volume spec is converted from Revit's internal feet to **m / m2 / m3** and `unit` must say so; any other spec is returned raw in the unit you declared. Text is `invalid`, never parsed. | yours, checked |
| `geometry_volume` | The solids at `detail_level` (Fine by default). | m3 |
| `geometry_area` | The **total face area** of those solids - both faces and the edges of a wall, not the schedule's Area parameter. Read that one with `source: parameter` when it is the quantity you bill. | m2 |
| `length` | The location curve - walls, beams, ducts, pipes. Point-located elements have none. | m |
| `count` | 1 per element. | yours |

Every reading carries one of five states, and only the first is a number:

| state | means |
| --- | --- |
| `measured` | a number, **including a real zero** |
| `absent` | no such parameter on the instance or its type; no solids; no location curve |
| `empty` | the parameter exists and holds no value |
| `unreadable` | the read threw - the element is missing from every total it touches |
| `invalid` | a value that is not a number, or a dimensioned parameter whose unit is not the declared one |

The classification code has the same discipline: the value, or one of three
non-values kept apart - `(no such parameter)`, `(empty)`, `(unreadable)`.

The reply carries per-element `rows` (capped by `top`; `truncated` says so and
`by_code` stays exact regardless), a `by_code` rollup that states for every
quantity how many elements each sum actually covers, `documents` with a
visibility-coverage block each, and `links_not_loaded`. `coverage_complete` is
true only when every workset was open, every link loaded, and no reading was
unreadable or invalid.

With `include_links: true` every loaded `RevitLinkInstance` is swept for the
same category, and each row carries `element_id`, `document`, `document_path`,
`link_instance_id` and its `placement` number; the `documents` block adds the
link's transform. Volumes, areas, lengths and counts are invariant under the
link's placement, so the transform is recorded, not applied. `include_links`
needs a `category`, not `element_ids`: an id is only unique inside one document.

**A linked file placed twice is two placements, not one.** Revit holds one
`RevitLinkType` per path and hands every instance of it the SAME `Document`, so
`documents` carries one entry per PLACEMENT - each with its own
`link_instance_id`, transform and `placement` number - and the elements are
measured once per placement, which is what the model says. Because that doubles
a total, it is declared rather than left to be discovered: `repeated_link_documents`
names every file placed more than once, how many placements it has and all of
their instance ids, so each copy can be traced back to the placement it came
through. A row's identity is `(document, link_instance_id, element_id)`; the
same element id arriving twice under two different instances is two rows, and a
linked element that happens to wear a host element's id is never confused with
it.

To place a link a second time, use `horizun_manage_links` with
`operation: "add_instance"` and the `link_type_id` from `operation: "list"`.

The takeoff-only keys are **refused** in the default `volume` mode rather than
ignored, so a reply never looks like a takeoff that did not happen.

## What `horizun_budget_compare` does

```json
{
  "model_rows_path": "C:\\work\\takeoff.json",
  "baseline": {
    "file_path": "C:\\work\\presupuesto.xlsx",
    "sheet": "Presupuesto",
    "header_row": 1,
    "columns": { "code": "Codigo", "description": "Descripcion", "unit": "Und",
                 "quantity": "Cantidad", "unit_price": "Precio", "currency": "Moneda" }
  },
  "mapping": {
    "unit_conversions": [ { "from": "m3", "to": "ft3", "factor": 35.3147 } ],
    "tolerances": { "quantity_pct": 2 }
  },
  "outputs": {
    "excel": { "file_path": "C:\\work\\comparacion.xlsx", "overwrite_policy": "refuse" },
    "power_bi": { "dataset_id": "<guid>", "table": "BudgetComparison", "dry_run": true }
  },
  "idempotency_key": "a-new-uuid-per-run"
}
```

`model_rows` is either the takeoff reply itself or its `rows`; a reply whose
rows were truncated is refused (re-run with `top >= rows_matching`). The
baseline is read through `horizun_excel_read_rows` - same OPC reader, same
`sha256` in the reply - and every row below `header_row` with a non-blank code
is a line; blank-code rows (subtotals) are skipped **and counted**. Columns are
named by header (case-insensitive) or by 1-based index; a header that matches
nothing is refused with the headers that exist.

### Per code, one status

| status | when |
| --- | --- |
| `added` | model elements carry the code, the baseline has no line for it |
| `removed` | the baseline has the line, no model element carries the code |
| `unchanged` | both, comparable, and the quantity delta is inside the tolerance |
| `modified` | both, comparable, and it is not |
| `not_comparable` | both present, but no honest subtraction exists - `reason` says why |

The `not_comparable` reasons: `unit_incompatible` (no declared factor from the
model unit to the baseline unit), `ambiguous_quantity` (two takeoff quantities
could feed the line - pin one with `mapping.quantity_field`),
`incomplete_read` (an element under the code was unreadable: the sum is a lower
bound and is reported as one), `model_invalid`, `model_absent` (no element
carries the quantity - not a zero), `partial_coverage` (some elements carry it
and some do not; opt in with `mapping.rules.compare_partial_coverage`),
`baseline_absent`, `baseline_invalid`, `baseline_ambiguous_unit` (the same code
in two units).

### Three deltas, kept apart

- **quantity** - `abs` and `pct` against the baseline, `within_tolerance`,
  `coverage_complete`. A baseline of zero has no percentage and says so.
- **classification** - `in_model`, `in_baseline`, and against an optional
  `mapping.catalogue` (`{version, codes: {code: is_leaf}}`, the same shape
  `horizun_audit_model`'s delivery readiness takes) `catalogue_status` of
  `leaf` / `group_not_terminal` / `not_in_catalogue`. Without a catalogue the
  status is `catalogue_not_supplied` and `is_leaf` is null - never guessed.
  Elements whose code is a non-value are pooled in `unclassified` by kind and
  never become `added` codes: nobody can price `(empty)`.
- **price** - only when the baseline line carries `unit_price`. The model amount
  is the model quantity **at the baseline unit price**, so `amount_delta`
  isolates quantity drift at the agreed rate. No price is ever invented: a
  line without one reports `state: not_available`; a `removed` priced line
  reports `baseline_only` with its budget and a null model amount; baseline
  rows that disagree on the price for one code produce no rate.

### Every line keeps its trace

`trace.element_ids`, `trace.documents`, `trace.link_instance_ids` and
`trace.baseline_rows` (1-based positions among the rows the sheet stores -
equal to the Excel row number when the sheet stores no empty rows, which the
reply says rather than assumes).

## What it refuses

- Unknown keys anywhere - top level, `baseline`, `columns`, `mapping`,
  `outputs` - because an argument nobody reads is an instruction you believe
  was followed.
- A unit pair with no declared factor, in the declared direction only: `m3 ->
  ft3` does not imply `ft3 -> m3`.
- A truncated takeoff, a baseline sheet longer than `max_rows`, an empty
  baseline (every model code would read `added`, which is not a finding about
  the budget).
- Writing without an `idempotency_key`, and the same key with different
  arguments.

## Destinations: each on its own

The structured comparison **always** comes back. Then each declared
destination is written and reported separately, in `destinations`:

| status | means |
| --- | --- |
| `written` | done, with evidence |
| `replayed` | this key already wrote it; nothing was written now |
| `skipped` | deliberately not written: an existing file under `overwrite_policy: refuse`, or a Power BI `dry_run` |
| `failed` | definitively not written; the error is in the entry |
| `in_doubt` | Power BI only: the HTTP answer was lost, Microsoft may have the rows |

There is **no global transaction**. A failed Excel write does not undo a
Power BI push, a refused push does not remove the workbook, and the reply says
so in `destinations_note`. Read every entry.

**Excel** creates a **new** workbook - one sheet (`Comparison` by default), a
header row, one row per code: `status, code, description, unit,
baseline_quantity, model_quantity, quantity_delta, quantity_delta_pct,
unit_price, baseline_amount, model_amount, amount_delta, elements, reason,
trace`. A number that does not exist is a blank cell, never a zero. The rows go
through `horizun_excel_write_rows`'s own path - exclusive lock, in-memory and
on-disk re-read verification, its backup, its ledger under `<key>/excel` - and
the evidence (`rows_written`, `sha256`, `bytes`, `verified`) is that writer's.
An existing file is `skipped` unless `overwrite_policy: replace`, and then it
is copied to `<file>.<stamp>.horizunbak` before being replaced.

**Power BI** pushes one row per code (plus `run_id`) through
`horizun_power_bi_push` under `<key>/powerbi`, with `dry_run` defaulting to
true. Its design decision stands unchanged here: **no automatic network
retry**. A definitive HTTP failure is `failed` and a retry with the same key
replays that failure; a lost answer is `in_doubt` with the ledger key, and
nothing is re-sent until a person inspects the table and chooses a new key.

## Profiles and packs

`horizun_budget_compare` is classified `ExternalSideEffectOnRequest`, and that
is two guarantees rather than one. **Admission**: every `permission_profile`
admits the tool, because a comparison with no `outputs` reads a workbook,
computes arithmetic and writes nothing at all - a read_only machine is entitled
to it. **The destination**: declaring `outputs.excel` or `outputs.power_bi` -
a dry-run push included, because its reply reports whether this machine has
Power BI credentials - needs `permission_profile: full_write` or `unsafe_code`,
and under a lower profile the whole call is refused BY NAME before the baseline
is read. The MCP annotations still describe the worst case (`openWorldHint`,
`destructiveHint`): a client deciding whether to ask a person is told what the
tool can do, not what one call happens to do.

It used to be classified `ExternalSideEffect`, which hid the whole tool from
`read_only` and `safe_write` - so a machine allowed to read a budget was refused
the reading because the same surface can also write one. It rides on the
`powerbi`, `coordination` and `interoperability` packs; the takeoff mode is part
of `horizun_quantities` and follows it.

## Reproducing with disposable files

No Revit is needed for the comparison half. The server tests do exactly this
(`tests/Horizun.Server.Tests/BudgetCompareTests.cs`): build a baseline
workbook with `ExcelWriteRows.MinimalWorkbook` plus `horizun_excel_write_rows`,
write a takeoff reply as JSON by hand, run the compare, and read the report
back with `horizun_excel_read_rows`. From an MCP client:

1. `horizun_excel_write_rows` onto a fresh `.xlsx` (or any workbook you do not
   mind touching) with a header `Codigo, Descripcion, Und, Cantidad, Precio`
   and a few lines.
2. Save a takeoff reply to a temp `.json` - a real one from `horizun_quantities`
   mode `takeoff`, or a hand-written `{"mode":"takeoff","truncated":false,
   "rows":[...]}` in the shape above.
3. `horizun_budget_compare` with `model_rows_path`, that baseline, and
   `outputs.excel` pointing at a temp path. `power_bi` with `dry_run: true`
   validates the rows without a token.
4. `horizun_excel_read_rows` on the output: the header is row 1 and each code is
   a row, with blanks where no number exists.

The Revit half - mode `takeoff` against a live model, and `include_links` - is
verified by `scripts/live/verify-quantities-budget.ps1`, not by these tests. It
builds its own disposable state in the document it is given (codes written into
a parameter, a copy of a model linked once and then placed a second time, then
unloaded), measures the five reading states, the units, both provenance cases
and the comparison end to end, and asserts the permission rule in both
directions. It never saves the model, never edits the machine's settings, and
runs Power BI as a dry run only.

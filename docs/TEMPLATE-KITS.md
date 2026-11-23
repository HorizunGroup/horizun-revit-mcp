# Template kit compatibility and audit coverage

The manifest schema remains `horizun.template-kit/1`. New fields are optional;
loading an older kit does not require migration.

`find` compares `dims` and `near` filters with an entry's `nominal` dimensions
when present, otherwise with `dims`. Values are millimetres, with an exact-match
tolerance of 0.5 mm **per dimension**. Hits retain both source fields and label
them in `dimensions.nominal_mm` and `dimensions.envelope_mm`.

`policy` defaults to `existing_first`, which ranks exact existing sizes first.
`existing_only` excludes sizes outside tolerance and returns `status: no_match`
without creation advice when none remain. `allow_create` can return a supplied
`new_size` recipe. Neither policy invents an executable resize recipe. Entries
with `usable: false` are excluded unless `include_unusable: true`; placed entries
are reported by the audit's `legacy` finding.

`load` echoes `conventions.selection_policy` and reports `unavailable_tools`
with their 4.0 group. It uses `mcp.tools_used` together with tools discovered in
recipes, and applies the actual session permission and visibility filters.
An inactive group concerns discovery; activating it does not grant permissions.

`validate` separates `kit_valid` (structure), `recipes_valid` (call schemas) and
`contract_match` (API hash). `valid` requires both structure and recipes to pass.
Playbook/check calls, `placement.recipe` and `new_size` calls are inspected without
execution. A placement object without `tool` is checked as one element in
`horizun_create_elements`, with a model marker and mm units as request context.
`<...>` markers count as present values; unknown
arguments, missing required arguments and concrete invalid enums still fail.

`list`, `load` and `validate` share `contract_match`:
`{match, kit_hash, server_hash, note}`. `match: null` means the kit did not record
its contract. `template_hash_match` independently hashes an available RTE named
by `model_path` or `template.file` (relative to the kit). An RVT cannot establish
the original template's byte identity, and yields an unknown match with a reason.

The model audit checks **both** `HZ_AI_Source` and `HZ_AI_Status` on every judged
element, distinguishing `absent`, `empty`, `not_allowed` and `unreadable`. Optional
allowed values are declared as, for example:

```json
{"conventions":{"traceability":{
  "HZ_AI_Source":{"allowed_values":["drawing","instruction"]},
  "HZ_AI_Status":{"allowed_values":["pending","checked"]}
}}}
```

Each parameter may also directly contain the allowed-values array. Without an
allowed-values declaration any nonblank value passes that parameter's value rule.

`conventions.foreign_names.forbidden_regex` accepts a string or array of bounded
regular expressions; `forbidden` (or `fragments`) contains literal fragments.
The audit reads the types actually used, checking family/type names, Manufacturer,
Model, Description, Type Comments and the optional manufacturer/reference fields.
Unreadable types or invalid expressions leave the check `not_executed` with a reason.

`conventions.titleblock_text` accepts `{brand: "Sample", must_not_overlap: true}`
or the existing structured brand `{canonical: "Sample", variants: [...]}`.
Placed titleblock families are read through `audit_titleblock_text`; bounding-box
intersections are candidates, not proof of glyph collisions. No placed titleblocks
means `not_applicable`. An unavailable mode or incomplete read means `not_executed`.

`checks_coverage` contains every kit check, keyed by its `id` or otherwise its
`name`, with `status: executed | executed_with_declared_evidence | not_executed | not_applicable` and a reason when
it was not executed. Built-in checks use their measured inputs; arbitrary kit
recipes and prose pass conditions are never treated as executed measurements.
Unknown/manual checks are explicitly `not_executed`. Checks are blocking unless
`blocking: false` or `severity: information | optional` is supplied. Any missing
blocking check prevents `usable_today` and appears in
`blocking_checks_not_executed`. The correction verdict is
`usable_with_corrections`; blocking findings still produce `not_yet`.

Drawing checks (`cobertura_contra_plano`, `visual_evidence`, `quantities_by_code`,
`induccion_completa`, `compare_drawings`) accept external evidence **per check**.
Call `horizun_template_kit operation=evidence_schema` to obtain its JSON Schema
without Revit or a kit. Pass an absolute `declared_evidence_path` to `audit`:

```json
{
  "schema": "horizun.template-check-evidence/1",
  "target_document": "Synthetic model",
  "checks": [{
    "check_id": "cobertura_contra_plano",
    "source": {
      "origin": "Independent drawing review, revision 2",
      "path": "D:\\Reviews\\drawing-coverage.json",
      "sha256": "<64 hexadecimal characters: SHA-256 of the source file bytes>"
    },
    "coverage": [{
      "drawing_ref": "A-01 revision 2 / region A",
      "model_element_ids": [101, 102]
    }],
    "result": "passed",
    "summary": "Reviewer declares geometry and counts match the named region."
  }]
}
```

The server validates the strict schema, exact audit document name, per-check
assignment, unique check entry, source readability and SHA-256, nonempty drawing
references and model IDs against the complete model census. `source.path` must be
absolute. The source can be a drawing, a coverage report or another review artifact;
the server checks its bytes, not the semantic truth of the review. The example hash
placeholder must be replaced with an actual digest. Evidence for one check cannot
satisfy another. A missing/invalid declaration leaves that check `not_executed`,
with `required_evidence` and validation errors when applicable.

Accepted evidence yields `executed_with_declared_evidence`, `passed: null`,
`declared_passed: true | false`, `measured: false`, and `host_verified: false`.
The result retains the declaration, verified source hash and manifest hash/path.
A declared failure creates a finding; a declared pass never becomes a measured
pass. Any accepted declaration keeps the aggregate verdict below `usable_today`
(`usable_with_corrections` when no blocking finding exists). Read
`checks_with_declared_evidence` separately from measured checks and
`blocking_checks_not_executed`.

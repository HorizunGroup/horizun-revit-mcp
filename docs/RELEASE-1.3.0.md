# Horizun Revit MCP 1.3.0

This release strengthens geometry verification and execution contracts while
retaining the production tools from the 1.2.1 development line.

- BIM production workflows are available through MCP resources and prompts,
  with owner-local mode, pause, receipt history and workshared protection controls.
- Deliverable production gains annotation and sheet-placement checks, PDF page
  verification and a delivery ledger. Optional enterprise policies and receipt
  forwarding remain explicit configuration, with no automatic data upload.
- Creation checks requested geometry after commit: elevations, offsets, location,
  orientation, profiles and roof slopes. Geometry mismatches roll back the batch.
- Point placements declare `coordinate_mode`: `absolute` or `level_offset`.
  Replies include the measured absolute elevation and level offset. Previously
  ambiguous placement requests now receive an explicit refusal.
- Typed creation adds wall profiles, stairs with explicit runs and landings,
  per-edge roof slopes and displacement sets. Existing structural and MEP
  creation and verification remain available.
- Type duplication accepts architectural types and family symbols, checks
  requested values and confirms that the source type remains unchanged.
- Save operations respect `dry_run`; document operations have separate argument
  contracts. Unsupported arguments fail before writing.
- Python snapshots include source and includes in the idempotency identity.
  Helpers, compact output, bounded output and host observations improve evidence;
  Python results remain self-reported and Python remains disabled by default.
- Transport errors retain structured failure details. Request progress exposes
  queue and execution state; health reports runtime warm-up state.
- Captures support temporary framing and view options. Eligible plan and section
  captures can return a measured world-to-pixel transformation, with independent
  holdout checks and restoration of the original view state.
- Optional source references persist per element and compare source dimensions
  with measured model values.
- Installation instructions for LLMs now prefer the published Windows installer,
  which includes the server runtime and requires no development SDK.

`horizun_transform_elements` uses `join_end` (0 or 1) for `wall_join`, preserving
the existing `end` point argument used by `set_curve`. Horizontal slab profiles
use contours → XYZ points → coordinates; beam-system boundaries retain XY points.

Validation of the integrated release is in progress. Passing results from an
earlier development build are not evidence for the final installer. Stable
publication requires the packaged build's live matrix for Revit 2023–2027.

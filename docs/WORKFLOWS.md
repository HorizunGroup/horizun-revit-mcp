# BIM Production Workflows

These are task-oriented entry points over the typed tool surface. They do not
introduce a second execution path: every workflow uses existing permissions,
dry runs and verification rules.

![Workflow lifecycle: identify, audit, rehearse, apply and retain evidence](assets/bim-production-workflow.svg)

| Workflow | Mode | Output | Suggested request |
| --- | --- | --- | --- |
| Model Health Audit | `read_only` | Findings, coverage and optional correction proposals | `Audit the active model without writes. Run model scan and pre-delivery audit; report findings and unknown coverage separately.` |
| Sheet QA/QC | `read_only` | Sheet/view/annotation findings | `Audit planimetry for the active document. Do not write. Report blocking, advisory and unknown findings with their cited evidence.` |
| Family QA/QC | `read_only` | In-place, naming and model-health facts | `Inspect family health in the active model. Include in-place families, heavy or unused candidates where measurable, and state what is not covered.` |
| Parameter Compliance | `read_only` → `safe_write` | Measured compliance; optional verified updates | `First audit the explicit parameter requirements for the named categories. Propose typed corrections only; do not apply them.` |
| Room / Area Audit | `read_only` | Unplaced, unenclosed, duplicate or incomplete room facts | `Audit rooms and areas in the active model without writes. Separate unplaced, unenclosed, blank and duplicate conditions.` |
| Architecture vs Structure | `read_only` | Scoped coordination evidence | `Compare the explicitly named architectural and structural links for grids, levels and requested elements. State coverage and unresolved ambiguity; do not modify either model.` |
| Quantity / Power BI Pack | `read_only` → `full_write` | Takeoff first; optional external receipt | `Produce a read-only quantity takeoff for the named categories and parameters. Do not export or push data until I approve the destination.` |
| DWG to BIM | `read_only` → `safe_write` | Requirement-bound plan then verified application | `Read the selected DWG and requirement set. Return a plan only, with omissions and ambiguities; do not build anything.` |
| Planimetry Review | `read_only` → `safe_write` | Findings, correction rehearsal and visual capture | `Audit the named sheets, propose only cited typed corrections, and rehearse them without applying.` |
| Safe Batch Parameter Update | `safe_write` | Dry-run receipt then verified per-target values | `Rehearse updates for these explicit element ids and parameter values. Refuse unresolved scope; do not apply.` |

## Workflow contract

Every workflow must say what model it targets, what it measures, what it cannot
measure, whether it can write, and what evidence proves its result. A workflow
does not use a broad request such as “fix everything” as permission to select
elements or values.

Enterprise and project-specific workflows belong in Horizun Hub or a separately
versioned package. The public bridge remains capable of executing them only
through explicit, reviewable inputs.

For a report layout that intentionally contains no fabricated model result, see
the [QA/QC Excel template](../examples/qaqc-report-template.xlsx).

Use the `qaqc-report-export` MCP prompt only after an audit has completed. It
requires the approved workbook path and the exact audit reply or durable receipt
that supplies each row. The local writer creates a `.horizunbak` backup and
re-reads appended cells; it never turns incomplete audit coverage into a pass.

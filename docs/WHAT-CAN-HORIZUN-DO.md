# What can Horizun Revit MCP do today?

Horizun Revit MCP provides safe, verified BIM automation for Autodesk Revit.
Typed writes are re-read from the model after commit; a tool does not report an
unverified change as completed. Python is an explicitly owner-approved fallback
and is always labelled self-reported.

| BIM task | Typical tools | Model effect | Evidence returned |
| --- | --- | --- | --- |
| Model health and pre-delivery | `horizun_model_scan`, `horizun_audit_model` | Read-only | Measured counts, findings, coverage and optional correction proposals |
| Sheets, views and documentation | `horizun_query_planimetry`, `horizun_audit_planimetry`, `horizun_fix_planimetry` | Audit is read-only; fixes are typed writes | Cited findings, rehearsal, after-state and re-audit |
| Families and types | `horizun_family_apply`, `horizun_create_family`, `horizun_manage_system_types` | Typed writes | Resolved type/family identity and post-commit readback |
| Parameters and classification | `horizun_query_model`, `horizun_write_params_verified`, `horizun_set_keynote` | Query or typed writes | Per-target resolution and verified values |
| Rooms, areas and schedules | `horizun_query_model`, `horizun_create_schedule`, `horizun_get_schedule_data` | Query or typed writes | Explicit coverage, schedule fields and displayed cells |
| Coordination and links | `horizun_manage_links`, `horizun_clash`, `horizun_coordination` | Mostly read-only; link changes are typed | Link state, scope and measured clashes |
| Quantities and Power BI | `horizun_quantities`, `horizun_budget_compare`, `horizun_power_bi_push` | Takeoff is read-only; outputs require approval | Provenance, units, comparison states and destination receipts |
| DWG to BIM | `horizun_query_cad`, `horizun_plan_from_cad`, `horizun_apply_cad_plan`, `horizun_audit_cad_model` | Plan/audit read-only; application typed | Requirement-set hash, plan binding, post-commit verification |
| Structural and MEP production | `horizun_plan_structure`, `horizun_plan_mep`, reinforcement tools | Plan/audit read-only; application typed | Inputs, unresolved ambiguity and re-read geometry |

## How to compare capabilities responsibly

Tool count is not a production metric. Compare a workflow by its scope,
permission requirement, dry-run behavior, handling of unreadable data, and the
evidence it returns after a write. A clean-looking result with incomplete
coverage is not a clean model in Horizun.

See [WORKFLOWS.md](WORKFLOWS.md) for task-oriented entry points and
[security-model.md](security-model.md) for the permission contract.

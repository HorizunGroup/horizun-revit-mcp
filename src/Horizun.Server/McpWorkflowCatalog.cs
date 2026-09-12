// Horizun MCP — product-facing workflows, derived from the installed surface.
// This is deliberately a catalog of safe entry points, not a second executor.
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpWorkflowCatalog
    {
        public static JObject Document()
        {
            return new JObject
            {
                ["schema"] = "horizun.workflow-catalog/1",
                ["title"] = "Horizun BIM Production Workflows",
                ["principle"] = "A workflow is an entry point over typed tools; it does not bypass permissions, dry runs or verification.",
                ["workflows"] = new JArray
                {
                    Flow("deliverable-production", "Deliverable Production", "read_only_to_full_write", "Plan staged annotation, paper layout, audit, visual approval and verified PDF publication from explicit project rules.", "horizun_plan_views", "horizun_plan_annotations", "horizun_annotate", "horizun_pack_sheets", "horizun_audit_planimetry", "horizun_capture_view", "horizun_export"),
                    Flow("room-documentation", "Room Documentation", "safe_write", "Plan explicit room views, then rehearse the atomic document_rooms workflow with approved types, templates, scale and placements.", "horizun_plan_views", "horizun_execute_plan", "horizun_query_planimetry"),
                    Flow("family-recipe", "Family Recipe", "full_write", "Compile an explicit fixed-footprint, height-parametric recipe; inspect flex and PNG before acceptance.", "horizun_create_family", "horizun_query_model"),
                    Flow("review-correct-verify", "Review, Correct, Verify", "read_only_to_safe_write", "Select audit findings, rehearse typed corrections, then inspect per-element re-audit evidence.", "horizun_audit_model", "horizun_apply_corrections"),
                    Flow("model-health-audit", "Model Health Audit", "read_only", "Measure health, hygiene, links and readiness.", "horizun_model_scan", "horizun_audit_model"),
                    Flow("sheet-qaqc", "Sheet QA/QC", "read_only", "Audit sheets, views and annotation evidence.", "horizun_query_planimetry", "horizun_audit_planimetry"),
                    Flow("family-qaqc", "Family QA/QC", "read_only", "Inspect measurable family hygiene and in-place content.", "horizun_model_scan", "horizun_query_model"),
                    Flow("parameter-compliance", "Parameter Compliance", "read_only_to_safe_write", "Measure an explicit standard; rehearse typed corrections only after review.", "horizun_query_model", "horizun_write_params_verified"),
                    Flow("room-area-audit", "Room / Area Audit", "read_only", "Inspect placement, enclosure and data completeness.", "horizun_query_model", "horizun_audit_model"),
                    Flow("quantity-export-pack", "Quantity / Power BI Pack", "read_only_to_full_write", "Take off first; export or push only after destination approval.", "horizun_quantities", "horizun_budget_compare", "horizun_power_bi_push"),
                    Flow("dwg-to-bim-review", "DWG to BIM", "read_only_to_safe_write", "Plan against an explicit requirement set before building.", "horizun_query_cad", "horizun_plan_from_cad", "horizun_apply_cad_plan", "horizun_audit_cad_model"),
                    Flow("planimetry-review", "Planimetry Review", "read_only_to_safe_write", "Inspect actual sheets; corrections are cited, rehearsed and verified.", "horizun_audit_planimetry", "horizun_capture_view", "horizun_fix_planimetry"),
                    Flow("safe-batch-parameter-update", "Safe Batch Parameter Update", "safe_write", "Resolve explicit targets, dry run, then verify every committed value.", "horizun_write_params_verified"),
                    Flow("architecture-structure-coordination", "Architecture / Structure Coordination", "read_only", "Measure an explicit coordination scope and preserve unresolved or incomplete coverage.", "horizun_query_structure", "horizun_query_model", "horizun_clash"),
                    Flow("mep-coordination-review", "MEP Coordination Review", "read_only", "Inspect systems and clashes; route planning remains a non-writing proposal.", "horizun_query_model", "horizun_clash", "horizun_plan_mep"),
                    Flow("western-forms-concrete-review", "Western Forms / Concrete Review", "read_only", "Audit declared concrete and reinforcement requirements without applying reinforcement.", "horizun_query_structure", "horizun_audit_reinforcement"),
                    Flow("qaqc-report-export", "QA/QC Report Export", "full_write", "Append measured findings to an approved local workbook and re-read every written cell.", "horizun_excel_write_rows", "horizun_excel_read_rows")
                }
            };
        }

        private static JObject Flow(string id, string title, string permission, string outcome, params string[] tools)
        {
            return new JObject
            {
                ["id"] = id,
                ["title"] = title,
                ["minimum_permission"] = permission,
                ["expected_outcome"] = outcome,
                ["prompt"] = id,
                ["tools"] = new JArray(tools),
                ["evidence"] = "Tool replies must state measured findings, coverage and verification; unknown coverage is never a pass."
            };
        }
    }
}

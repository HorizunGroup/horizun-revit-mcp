// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// The MCP face of the Horizun handlers.
//
// This file exists because of a gap that only running the thing revealed: the
// nine Horizun handlers lived in the Revit plugin and worked — verified against
// a live model — while the MCP server exposed 223 tools, none of them Horizun's.
// The handlers were reachable by named pipe from a test script and by nothing
// else. For the person the tool was built for, they did not exist.
//
// A handler in the plugin is only half a tool. This is the other half.
//
// Descriptions here are load-bearing: they are what the model reads to decide
// whether to reach for a tool at all. So each one leads with what makes it
// different from the generic wrapper it replaces, because otherwise the model
// picks whichever name it saw first.
// -----------------------------------------------------------------------------
using System;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    public class HorizunTools
    {
        // ---- SCAN -------------------------------------------------------------

        [McpServerTool(Name = "horizun_model_scan", ReadOnly = true),
         System.ComponentModel.Description(
            "Deep read-only scan of the ACTIVE Revit model: cleanliness (imported vs linked CAD, IMPORT-* patterns, " +
            "unused templates/filters/group types, in-place families, stray lines), naming inputs (RAW view/sheet/level " +
            "names — it judges nothing, validate them yourself), documentation (views off sheets, sheets missing " +
            "titleblocks), project info, warnings, links, worksets, design options and type/keynote inventory. " +
            "PREFER THIS over get_model_overview/analyze_model_statistics: every bucket reports exact total vs returned " +
            "vs truncated, every section reports ok|failed(reason) so a check that threw can never read as a clean zero, " +
            "and 'could not read' is always a separate count from 'nothing there'. " +
            "target_document_title is REQUIRED and must match the active document — with several Revit versions running, " +
            "scanning the wrong model is a clean bill of health for a file nobody looked at. " +
            "sections: JSON array to limit the scan, e.g. '[\"cleanliness\",\"health\"]'. Omit for all 12.")]
        public static async Task<string> ModelScan(
            string target_document_title,
            string sections = null,
            int? top = null,
            string target_parameter = null)
        {
            try
            {
                var p = new JObject { ["target_document_title"] = target_document_title };
                if (!string.IsNullOrWhiteSpace(sections)) p["sections"] = JArray.Parse(sections);
                if (top.HasValue) p["top"] = top.Value;
                if (!string.IsNullOrWhiteSpace(target_parameter)) p["target_parameter"] = target_parameter;
                var result = await ToolGateway.SendToRevit("horizun_model_scan", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // ---- WRITE ------------------------------------------------------------

        [McpServerTool(Name = "horizun_write_params_verified"),
         System.ComponentModel.Description(
            "Write parameters to a batch of elements or TYPES in ONE transaction (one undo step), then re-read every " +
            "value from the model and compare it against what you passed. " +
            "PREFER THIS over set_element_parameter_values/set_type_parameter_values: those report success from the " +
            "call not throwing — Parameter.Set() can decline a write and return false without throwing, and Revit can " +
            "roll a whole transaction back and return RolledBack silently. " +
            "Writing to a TYPE re-codes every instance of it: the response tells you the blast radius, including the " +
            "elements you did not name. " +
            "writes: JSON array of {target_id, parameter, value}, or {target:\"project_info\", parameter, value}. " +
            "'parameter' accepts a BuiltInParameter name, a shared-parameter GUID, or the UI name. " +
            "on_failure: 'atomic' (default — roll the whole batch back; a half-coded model is worse than an uncoded " +
            "one) or 'best_effort'. Use dry_run=true first to see the blast radius. " +
            "Outcomes are three-way: confirmed / not_written / unknown. 'unknown' means the model could not be re-read " +
            "— it is NOT the same as absent.")]
        public static async Task<string> WriteParamsVerified(
            string writes,
            bool? dry_run = null,
            string on_failure = null,
            string transaction_name = null,
            string target_document_title = null,
            bool? allow_vary_between_groups = null)
        {
            try
            {
                var p = new JObject { ["writes"] = JArray.Parse(writes) };
                if (dry_run.HasValue) p["dry_run"] = dry_run.Value;
                if (!string.IsNullOrWhiteSpace(on_failure)) p["on_failure"] = on_failure;
                if (!string.IsNullOrWhiteSpace(transaction_name)) p["transaction_name"] = transaction_name;
                if (!string.IsNullOrWhiteSpace(target_document_title)) p["target_document_title"] = target_document_title;
                if (allow_vary_between_groups.HasValue) p["allow_vary_between_groups"] = allow_vary_between_groups.Value;
                var result = await ToolGateway.SendToRevit("horizun_write_params_verified", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "horizun_set_keynote"),
         System.ComponentModel.Description(
            "Set the Keynote code on elements, reporting exactly what it touched. In Revit the Keynote parameter " +
            "normally lives on the TYPE, so coding one element re-codes every sibling instance — we measured a request " +
            "to code ONE beam re-code 51. This resolves the target first, states the collateral BEFORE writing, writes " +
            "each type once, and re-reads to confirm. " +
            "scope: 'auto' (write wherever it lives), 'instance' (refuse rather than spill onto siblings), 'type'. " +
            "For anything other than keynotes use horizun_write_params_verified.")]
        public static async Task<string> SetKeynote(
            string element_ids,
            string keynote,
            string scope = null,
            bool? dry_run = null)
        {
            try
            {
                var p = new JObject { ["element_ids"] = JArray.Parse(element_ids), ["keynote"] = keynote };
                if (!string.IsNullOrWhiteSpace(scope)) p["scope"] = scope;
                if (dry_run.HasValue) p["dry_run"] = dry_run.Value;
                var result = await ToolGateway.SendToRevit("horizun_set_keynote", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // ---- DELETE -----------------------------------------------------------

        [McpServerTool(Name = "horizun_delete_verified"),
         System.ComponentModel.Description(
            "Delete elements by id, or purge unused, and prove it. DESTRUCTIVE. " +
            "PREFER THIS over purge_unused/delete_element: a purge once reported '758 types purged' having purged " +
            "nothing — Revit rolled the transaction back and returned RolledBack without throwing, and the script " +
            "counted its own Delete() calls. Here every id is RE-RESOLVED against the document after the commit, so " +
            "'deleted' means the model says it is gone. Cascades (elements taken along that you never named) are " +
            "reported as three numbers: confirmed gone / confirmed surviving / could not look. " +
            "mode: 'ids' (needs ids) or 'purge_unused'. dry_run defaults TRUE for purge — on a dry run deleted_total " +
            "is null and would_delete_total carries the rehearsal. protect_ids: ids the purge must never touch.")]
        public static async Task<string> DeleteVerified(
            string mode = null,
            string ids = null,
            string protect_ids = null,
            bool? dry_run = null,
            int? max_passes = null,
            string transaction_name = null)
        {
            try
            {
                var p = new JObject();
                if (!string.IsNullOrWhiteSpace(mode)) p["mode"] = mode;
                if (!string.IsNullOrWhiteSpace(ids)) p["ids"] = JArray.Parse(ids);
                if (!string.IsNullOrWhiteSpace(protect_ids)) p["protect_ids"] = JArray.Parse(protect_ids);
                if (dry_run.HasValue) p["dry_run"] = dry_run.Value;
                if (max_passes.HasValue) p["max_passes"] = max_passes.Value;
                if (!string.IsNullOrWhiteSpace(transaction_name)) p["transaction_name"] = transaction_name;
                var result = await ToolGateway.SendToRevit("horizun_delete_verified", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // ---- PERSIST ----------------------------------------------------------

        [McpServerTool(Name = "horizun_document_session"),
         System.ComponentModel.Description(
            "Open, save, save-as, close or inspect a Revit document, with the upgrade gate in front. " +
            "USE THIS TO OPEN ANY FILE. Opening a .rvt/.rfa on the wrong Revit version UPGRADES IT IRREVERSIBLY — " +
            "there is no downgrade, and with several Revit versions running at once that is one wrong port away. " +
            "Before opening, it reads the file's own Revit year off disk (BasicFileInfo, WITHOUT opening it), reads the " +
            "host's version, and refuses unless both agree with the REQUIRED expected_version. An upgrade must be opted " +
            "into with allow_upgrade=true and is reported as irreversible. " +
            "operation: 'inspect' (read the version off disk, opens nothing), 'open', 'save', 'save_as', 'close'. " +
            "It never syncs to central, and refuses to save a workshared document unless force_workshared=true. " +
            "'save' supports audit+compact. A save is only reported as saved when the file on disk proves it.")]
        public static async Task<string> DocumentSession(
            string operation,
            string file_path = null,
            string expected_version = null,
            bool? allow_upgrade = null,
            bool? audit = null,
            bool? detach = null,
            string save_as_path = null,
            bool? compact = null,
            bool? overwrite = null,
            bool? force_workshared = null,
            bool? save_on_close = null)
        {
            try
            {
                var p = new JObject { ["operation"] = operation };
                if (!string.IsNullOrWhiteSpace(file_path)) p["file_path"] = file_path;
                if (!string.IsNullOrWhiteSpace(expected_version)) p["expected_version"] = expected_version;
                if (allow_upgrade.HasValue) p["allow_upgrade"] = allow_upgrade.Value;
                if (audit.HasValue) p["audit"] = audit.Value;
                if (detach.HasValue) p["detach"] = detach.Value;
                if (!string.IsNullOrWhiteSpace(save_as_path)) p["save_as_path"] = save_as_path;
                if (compact.HasValue) p["compact"] = compact.Value;
                if (overwrite.HasValue) p["overwrite"] = overwrite.Value;
                if (force_workshared.HasValue) p["force_workshared"] = force_workshared.Value;
                if (save_on_close.HasValue) p["save_on_close"] = save_on_close.Value;
                var result = await ToolGateway.SendToRevit("horizun_document_session", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // ---- MEASURE ----------------------------------------------------------

        [McpServerTool(Name = "horizun_quantities", ReadOnly = true),
         System.ComponentModel.Description(
            "Volume takeoff in m3 from ALL THREE sources Revit offers — the Volume parameter, the real solid geometry, " +
            "and the material takeoff — side by side, with the disagreement measured. " +
            "PREFER THIS over compute_element_volume/get_material_quantities: they read ONE source (usually the cheap " +
            "cached parameter) and call it 'the volume'. On a real model we measured 51 of 51 beams disagreeing by " +
            "42.7%: the parameter said 23.1 m3, the geometry and the material takeoff both said 40.4 m3. Billing that " +
            "off the parameter bills 57% of the concrete poured. " +
            "This reports all three and refuses to choose — which source is right depends on your measurement criteria, " +
            "and whoever signs the takeoff is the one who gets to decide. " +
            "Pass element_ids (JSON int array) or category (e.g. 'OST_StructuralFraming').")]
        public static async Task<string> Quantities(
            string element_ids = null,
            string category = null,
            string detail_level = null,
            double? tolerance_pct = null,
            bool? only_disagreements = null)
        {
            try
            {
                var p = new JObject();
                if (!string.IsNullOrWhiteSpace(element_ids)) p["element_ids"] = JArray.Parse(element_ids);
                if (!string.IsNullOrWhiteSpace(category)) p["category"] = category;
                if (!string.IsNullOrWhiteSpace(detail_level)) p["detail_level"] = detail_level;
                if (tolerance_pct.HasValue) p["tolerance_pct"] = tolerance_pct.Value;
                if (only_disagreements.HasValue) p["only_disagreements"] = only_disagreements.Value;
                var result = await ToolGateway.SendToRevit("horizun_quantities", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "horizun_clash", ReadOnly = true),
         System.ComponentModel.Description(
            "Clash detection between two category sets, across the host model AND loaded Revit links. " +
            "PREFER THIS over check_clashes/clash_detection: those only ever read the HOST document and never say so. " +
            "On a federated project the other discipline lives in a link — that is what links are for — so asking the " +
            "old tool for walls vs ducts returns '0 clashes' about ducts it never opened. We reproduced it: a duct " +
            "physically through a beam, old tool clashes:[] warnings:[] success:true. " +
            "Link geometry is transformed into host coordinates and every clash names the source model of both " +
            "elements. If a link was excluded or unloaded, or geometry failed, the result is labelled PARTIAL rather " +
            "than clean — a zero from this tool means zero, or it says why not. " +
            "categories_a / categories_b: JSON string arrays of BuiltInCategory names.")]
        public static async Task<string> Clash(
            string categories_a,
            string categories_b,
            bool? include_links = null,
            double? tolerance_mm = null,
            int? max_results = null)
        {
            try
            {
                var p = new JObject
                {
                    ["categories_a"] = JArray.Parse(categories_a),
                    ["categories_b"] = JArray.Parse(categories_b)
                };
                if (include_links.HasValue) p["include_links"] = include_links.Value;
                if (tolerance_mm.HasValue) p["tolerance_mm"] = tolerance_mm.Value;
                if (max_results.HasValue) p["max_results"] = max_results.Value;
                var result = await ToolGateway.SendToRevit("horizun_clash", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "horizun_audit_model", ReadOnly = true),
         System.ComponentModel.Description(
            "Opinionated pre-delivery audit: findings with is_issue flags, ready to read to a human. Answers 'what in " +
            "here will embarrass us?'. Counts orphan GROUP TYPES (zero instances, full geometry in the file, invisible " +
            "in every view — the usual reason a model is inexplicably large), in-place families, imported CAD, views " +
            "off sheets, unplaced rooms, unloaded links. No 0-100 score on purpose: one number invites you to stop " +
            "reading. " +
            "For raw facts to compute your own verdict against your own standard, use horizun_model_scan instead — " +
            "this is its opinionated subset.")]
        public static async Task<string> AuditModel(int? top = null)
        {
            try
            {
                var p = new JObject();
                if (top.HasValue) p["top"] = top.Value;
                var result = await ToolGateway.SendToRevit("horizun_audit_model", p);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // ---- ESCAPE HATCH -----------------------------------------------------

        [McpServerTool(Name = "horizun_execute_python"),
         System.ComponentModel.Description(
            "Run Python directly against the Revit API on the UI thread. Pre-injected: doc, uidoc, uiapp, app. " +
            "Return data by assigning __output__ or with print(). " +
            "THE PYTHON STANDARD LIBRARY IS AVAILABLE — import json, re, csv, datetime, math all work. (Other " +
            "IronPython bridges ship without it, which forces hand-rolled JSON built with string joins.) " +
            "A script that throws inside a Transaction is rolled back for you, so a failure cannot poison later " +
            "commands with 'Modification of the document is forbidden'. Python tracebacks come back with line numbers. " +
            "Use this when no typed tool fits — it reaches the whole API, and typed tools will never cover all of it. " +
            "Prefer a typed horizun_* tool when one exists: they verify their work, this does exactly what you write.")]
        public static async Task<string> ExecutePython(string code)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("horizun_execute_python", new JObject { ["code"] = code });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}

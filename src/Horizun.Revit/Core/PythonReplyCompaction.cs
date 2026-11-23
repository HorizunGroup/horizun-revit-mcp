// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// horizun_execute_python response_mode=compact, made compact for real.
//
// Field report (cloud workshared model, Revit 2024): replies of ~127,000 characters
// for a script whose __output__ was a few lines, with max_output_chars low and
// response_mode=compact. The bulk was warning_delta: the model carried more than a
// thousand persistent warnings and the reply listed every signature TWICE (before and
// after), plus the fixed contract text on every call. Compact mode now carries what a
// caller acts on - counts, and the warnings this script ADDED or RESOLVED, bounded and
// with their totals - and refers to the contract instead of repeating it. Full mode
// is unchanged: it is the explicit request for everything.
// -----------------------------------------------------------------------------
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PythonReplyCompaction
    {
        /// <summary>At most this many new and this many resolved signatures are listed in compact mode.</summary>
        public const int MaxListedWarnings = 20;

        /// <summary>Fixed explanatory text a compact reply refers to instead of repeating.</summary>
        public static readonly string[] FixedTextFields =
        {
            "evidence_contract", "host_verification_note", "transaction_policy",
            "revit_raised_note", "observation_note"
        };

        public const string ContractRef = "horizun://contract/tools/horizun_execute_python";

        // Keep a single output field so scalar, array and null values survive the MCP
        // object envelope. Never make a withheld oversize result look like null output.
        public static JObject OutputOnly(JObject reply)
        {
            var result = new JObject { ["output"] = reply["output"]?.DeepClone() };
            if (reply.Value<string>("output_kind") == "text_only")
            {
                result["output_kind"] = "text_only";
                result["output_note"] = reply["output_note"]?.DeepClone();
            }
            if (reply.Value<string>("output_kind") == "paged")
            {
                // The first page is TEXT, not the data: say so beside it, with the way to the rest.
                result["output_kind"] = "paged";
                result["output_paging"] = reply["output_paging"]?.DeepClone();
                result["output_note"] = reply["output_note"]?.DeepClone();
                result["evidence_status"] = reply["evidence_status"]?.DeepClone();
            }
            if (reply["persistence_error"] != null)
            {
                // The output came back whole although it could not be saved, and the page size asked for was ignored: say so.
                result["persistence_error"] = reply["persistence_error"].DeepClone();
                result["output_note"] = reply["output_note"]?.DeepClone();
            }
            if (reply.Value<bool?>("output_truncated") == true)
            {
                result["output_truncated"] = true;
                result["output_original_chars"] = reply["output_original_chars"]?.DeepClone();
                result["output_full_path"] = reply["output_full_path"]?.DeepClone();
                result["output_note"] = reply["output_note"]?.DeepClone();
            }
            return result;
        }

        /// <summary>
        /// The compact form of PythonHostObservations.Delta: counts before and after, and the
        /// new/resolved signatures bounded to <see cref="MaxListedWarnings"/> each with their
        /// totals. Never the full before/after lists. A comparison that could not be made
        /// stays null (unknown), never an empty list.
        /// </summary>
        public static JObject CompactWarningDelta(JObject full, int maxListed = MaxListedWarnings)
        {
            if (full == null) return null;
            JObject before = full["before"] as JObject;
            JObject after = full["after"] as JObject;
            bool complete = full.Value<bool?>("persistent_warning_comparison_complete") == true;
            JArray added = full["new"] as JArray;
            JArray resolved = full["resolved"] as JArray;
            var compact = new JObject
            {
                ["shape"] = "compact",
                ["observed"] = before?.Value<bool?>("observed") == true && after?.Value<bool?>("observed") == true,
                ["count_before"] = before?["count"]?.DeepClone(),
                ["count_after"] = after?["count"]?.DeepClone(),
                ["persistent_warning_comparison_complete"] = complete,
                ["new_count"] = complete && added != null ? (JToken)added.Count : JValue.CreateNull(),
                ["resolved_count"] = complete && resolved != null ? (JToken)resolved.Count : JValue.CreateNull(),
                ["new"] = complete && added != null ? new JArray(added.Take(maxListed).Select(t => t.DeepClone())) : null,
                ["resolved"] = complete && resolved != null ? new JArray(resolved.Take(maxListed).Select(t => t.DeepClone())) : null,
                ["new_truncated"] = complete && added != null && added.Count > maxListed,
                ["resolved_truncated"] = complete && resolved != null && resolved.Count > maxListed,
                ["suppressed_warning_coverage"] = full["suppressed_warning_coverage"]?.DeepClone(),
                ["full_lists"] = "response_mode=full"
            };
            string error = before?.Value<string>("error") ?? after?.Value<string>("error");
            if (error != null) compact["observation_error"] = error;
            return compact;
        }

        /// <summary>Rows listed per observation in compact mode; the totals are never cut.</summary>
        public const int MaxListedObservationRows = 50;

        /// <summary>
        /// host_observations.rows has one row per id the script reported (up to 2,000): in compact mode it is bounded,
        /// with the totals kept and the cut said, so a paged reply does not grow back through it. Idempotent.
        /// </summary>
        public static void CompactHostObservations(JObject reply)
        {
            if (!(reply?["host_observations"] is JObject obs) || !(obs["rows"] is JArray rows) || rows.Count <= MaxListedObservationRows) return;
            int total = rows.Count;
            obs["rows"] = new JArray(rows.Take(MaxListedObservationRows).Select(t => t.DeepClone()));
            obs["rows_total"] = total;
            obs["rows_listed"] = MaxListedObservationRows;
            obs["rows_omitted_in_compact"] = total - MaxListedObservationRows;
            obs["full_rows"] = "response_mode=full";
        }

        /// <summary>
        /// Applies compact mode to a reply (success payload or failure detail) in place:
        /// the warning delta in its compact form, the fixed texts removed and referred to,
        /// the source's fixed note dropped, host_observations bounded. Idempotent.
        /// </summary>
        public static JObject Compact(JObject reply)
        {
            if (reply == null) return null;
            if (reply["warning_delta"] is JObject delta && delta.Value<string>("shape") != "compact")
                reply["warning_delta"] = CompactWarningDelta(delta);
            CompactHostObservations(reply);
            bool removed = false;
            foreach (string field in FixedTextFields)
                if (reply.Remove(field)) removed = true;
            if (reply["source"] is JObject source && source.Remove("note")) removed = true;
            if (removed || reply["contract_ref"] == null) reply["contract_ref"] = ContractRef;
            return reply;
        }
    }
}

// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// response_mode=summary for the two WRITE/COORDINATION replies that outgrew a client.
//
// Course dry run 2026-09-30 (defect #17): horizun_create_elements of 75 walls answered
// 262-276 kB - ten re-read postconditions per wall, each with requested / found /
// tolerance - and horizun_clash (Pro edition) with 165 interferences 58 kB. Both went over the
// client's limit and were truncated to a file, which in Claude Desktop breaks the flow.
//
// This is the repository's existing convention, not a new one: response_mode with
// full as the DEFAULT (nothing changes for a caller that does not ask), the measured
// verdict and coverage never touched, and every shortened array NAMED in
// response_omissions with how many rows it held and how many are shown - the same
// fields ProgressiveResponse gives model_scan. Presentation only.
//
//   create_elements: every row that is not a clean verified row stays IN FULL - a
//     row that did not verify, a postcondition that did not all verify, a source
//     comparison that did not match, or an element the spatial check named. The rest
//     collapse to counts by status and kind plus their element ids: their per-property
//     detail is dropped from THIS reply only, because those rows passed every check a
//     full row shows. Re-read any of them with horizun_query_model element_ids.
//   clash: totals by category pair (and source models), the top N interferences by
//     intersection volume IN FULL with their original clash_index, and where the rest
//     live: the coordination ledger (record_findings=true -> horizun_coordination (Pro edition) list
//     or export) or a re-run with response_mode=full (clash is read-only).
//
// No `using Autodesk.*`: it shapes JSON the commands already built.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ResponseSummaryRules
    {
        public const string Full = "full";
        public const string Summary = "summary";

        /// <summary>The requested mode, or null with <paramref name="error"/> set. Absent is full.</summary>
        public static string ParseMode(JObject request, out string error)
        {
            error = null;
            JToken t = request?["response_mode"];
            if (t == null || t.Type == JTokenType.Null) return Full;
            string mode = t.Type == JTokenType.String ? (string)t : null;
            if (mode == Full || mode == Summary) return mode;
            error = "response_mode must be full or summary. Nothing ran.";
            return null;
        }

        // ------------------------------------------------------------------ create_elements

        /// <summary>
        /// Shape a create_elements reply in place: rows (the apply) and
        /// api_rehearsal.provisional_verification (a revit_rollback rehearsal). Element ids
        /// named by spatial_check findings keep their rows in full.
        /// </summary>
        public static JObject CreateElements(JObject data)
        {
            if (data == null) return null;
            var named = SpatialFindingIds(data["spatial_check"] as JObject);
            var omissions = new JArray();
            var summaries = new JObject();

            if (data["rows"] is JArray rows)
                summaries["rows"] = CollapseRows(rows, named, "/rows", omissions);
            if (data["api_rehearsal"] is JObject rehearsal && rehearsal["provisional_verification"] is JArray provisional)
                summaries["api_rehearsal.provisional_verification"] =
                    CollapseRows(provisional, named, "/api_rehearsal/provisional_verification", omissions);

            Stamp(data, omissions);
            data["rows_summary"] = summaries;
            data["expand"] = new JObject
            {
                ["tool"] = "horizun_query_model",
                ["arguments"] = new JObject { ["element_ids"] = new JArray() },
                ["note"] = "A collapsed row passed every check a full row shows (verified, every postcondition re-read " +
                           "and matched, not named by the spatial check); only its per-property detail is left out of " +
                           "this reply. Put any of collapsed_element_ids in element_ids to re-read those elements. Rows " +
                           "that did not verify cleanly are never collapsed."
            };
            return data;
        }

        /// <summary>Is this row a clean, verified one that may be collapsed to its id?</summary>
        public static bool IsCleanRow(JObject row, ISet<long> namedBySpatialCheck)
        {
            if (row == null) return false;
            if (row.Value<bool?>("verified") != true) return false;
            if (row["present_after_commit"] != null && row.Value<bool?>("present_after_commit") != true) return false;
            if (row["postconditions"] is JObject pc && pc.Value<bool?>("all_verified") != true) return false;
            if (row["production_postconditions"] is JObject ppc &&
                ppc["all_verified"] != null && ppc.Value<bool?>("all_verified") != true) return false;
            if (row["source_comparison"] is JObject sc && sc["matches"] != null && sc.Value<bool?>("matches") != true) return false;
            if (row["warnings"] is JArray w && w.Count > 0) return false;
            if (row["error"] != null && row["error"].Type != JTokenType.Null) return false;
            long? id = ReadId(row["element_id"]);
            if (id == null) return false;   // a row with no id cannot be pointed at again: keep it whole
            if (namedBySpatialCheck != null && namedBySpatialCheck.Contains(id.Value)) return false;
            return true;
        }

        private static JObject CollapseRows(JArray rows, ISet<long> named, string pointer, JArray omissions)
        {
            int total = rows.Count;
            var kept = new JArray();
            var collapsedIds = new JArray();
            var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int clean = 0, notVerified = 0, verifiedWithFindings = 0;
            foreach (JToken t in rows)
            {
                var row = t as JObject;
                string kind = row?.Value<string>("kind") ?? "(unknown)";
                byKind[kind] = byKind.TryGetValue(kind, out int n) ? n + 1 : 1;
                if (IsCleanRow(row, named))
                {
                    clean++;
                    collapsedIds.Add(ReadId(row["element_id"]).Value);
                    continue;
                }
                if (row?.Value<bool?>("verified") == true) verifiedWithFindings++; else notVerified++;
                kept.Add(t.DeepClone());
            }
            rows.RemoveAll();
            foreach (JToken k in kept) rows.Add(k);
            if (collapsedIds.Count > 0)
                omissions.Add(new JObject
                {
                    ["json_pointer"] = pointer,
                    ["returned_items_before_summary"] = total,
                    ["shown"] = kept.Count,
                    ["omitted"] = collapsedIds.Count
                });
            return new JObject
            {
                ["total"] = total,
                ["by_status"] = new JObject
                {
                    ["verified_clean"] = clean,
                    ["verified_with_findings"] = verifiedWithFindings,
                    ["not_verified"] = notVerified
                },
                ["by_kind"] = JObject.FromObject(byKind),
                ["shown_in_full"] = kept.Count,
                ["collapsed"] = collapsedIds.Count,
                ["collapsed_element_ids"] = collapsedIds
            };
        }

        private static HashSet<long> SpatialFindingIds(JObject spatial)
        {
            var ids = new HashSet<long>();
            if (spatial?["findings"] is JArray findings)
                foreach (JObject f in findings.OfType<JObject>())
                    foreach (string side in new[] { "a", "b" })
                    {
                        long? id = ReadId((f[side] as JObject)?["id"]);
                        if (id != null) ids.Add(id.Value);
                    }
            return ids;
        }

        // ------------------------------------------------------------------ the hook

        /// <summary>
        /// Called by the dispatcher AFTER SpatialAfterWrite.Attach, so a create_elements row
        /// the spatial check named is kept whole. A request without response_mode=summary,
        /// a failure, or another tool is left exactly as it was. The commands themselves
        /// refuse an invalid response_mode before running (ParseMode), so this never has to.
        /// </summary>
        public static void ApplyToResult(string tool, string paramsJson, CommandResult result)
        {
            if (result == null || !result.Success) return;
            if (tool != "horizun_create_elements") return;
            if (paramsJson == null || paramsJson.IndexOf("response_mode", StringComparison.Ordinal) < 0) return;
            JObject request;
            try { request = JObject.Parse(paramsJson); } catch { return; }
            if (ParseMode(request, out _) != Summary) return;
            JObject data = result.Data as JObject ?? (result.Data == null ? null : JObject.FromObject(result.Data));
            if (data == null) return;
            CreateElements(data);
            result.ReplaceData(data);
        }

        // ------------------------------------------------------------------ shared

        private static void Stamp(JObject data, JArray omissions)
        {
            data["response_mode"] = Summary;
            data["response_omissions"] = omissions;
            data["response_detail_complete"] = omissions.Count == 0;
        }

        private static long? ReadId(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return (long)t;
            return long.TryParse(t.ToString(), out long v) ? v : (long?)null;
        }
    }
}

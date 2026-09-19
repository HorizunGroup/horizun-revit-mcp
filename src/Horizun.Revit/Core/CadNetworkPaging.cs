// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// A NETWORK READING THAT CANNOT BE RETURNED IS NOT A NETWORK READING.
//
// MEASURED (campaign 6): horizun_cad_networks on a real mechanical plan with no
// layer filter produced a 96 MB answer and the transport refused it - the work
// was done, nobody could see it. Filtering the layer let the route continue; it
// did not make the tool usable.
//
// So the ANALYSIS always runs over the whole declared scope, and the SUMMARY is
// always computed from all of it; what is bounded is the LISTING. Each array
// (runs, junctions, connections, crossings, gaps, components) is returned as a
// page: its total, the offset and limit applied, the next offset, and a
// truncated flag. Nothing is dropped from the analysis to make the answer fit,
// and a page never claims to be the whole list. The order within each array is
// the network's own, which is deterministic for one drawing and one set of
// options, so page two follows page one and both agree with the summary.
//
// Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadNetworkPaging
    {
        public static readonly string[] Lists = { "runs", "junctions", "connections", "crossings", "gaps", "components", "dropped_short_runs" };
        public const int DefaultLimit = 500;
        public const int MaxLimit = 5000;

        /// <summary>
        /// Replace every list of <paramref name="full"/> by one page of it and say so. <paramref name="only"/>
        /// (may be null) names the lists the caller wants listed; the others are summarised by count.
        /// </summary>
        public static JObject Page(JObject full, IList<string> only, int offset, int limit)
        {
            if (full == null) return null;
            offset = Math.Max(0, offset);
            limit = Math.Max(0, Math.Min(MaxLimit, limit));
            var paging = new JObject();
            bool anyTruncated = false;
            full["analysis_fingerprint"] = Fingerprint(full);
            foreach (string name in Lists)
            {
                var arr = full[name] as JArray;
                if (arr == null) continue;
                int total = arr.Count;
                bool wanted = only == null || only.Count == 0 || only.Contains(name, StringComparer.Ordinal);
                int take = wanted ? Math.Max(0, Math.Min(limit, total - offset)) : 0;
                var page = new JArray(arr.Skip(wanted ? offset : total).Take(take));
                full[name] = page;
                bool truncated = wanted ? offset + take < total || offset > 0 : total > 0;
                anyTruncated |= truncated;
                paging[name] = new JObject
                {
                    ["total"] = total,
                    ["listed"] = wanted,
                    ["offset"] = wanted ? offset : 0,
                    ["returned"] = take,
                    ["next_offset"] = wanted && offset + take < total ? (JToken)(offset + take) : JValue.CreateNull(),
                    ["truncated"] = truncated
                };
            }
            full["listing"] = paging;
            full["listing_complete"] = !anyTruncated;
            full["analysis_complete"] = true;
            full["listing_means"] =
                "the ANALYSIS covered every run of the declared scope and the summary counts all of it; only the " +
                "LISTING is paged. listing_complete=false means some list above is a page - its total and " +
                "next_offset say how to ask for the rest with the same arguments and offset. The order is the " +
                "network's own, so pages do not overlap and together are exactly the totals.";
            return full;
        }

        /// <summary>The biggest layers of a reading, as a scope a caller can send back.</summary>
        /// <summary>
        /// A hash of every list of the FULL analysis, before paging. The same arguments over the same source give
        /// the same fingerprint; a caller paging through sends the first page's fingerprint back and a later page
        /// read from a changed source is refused instead of being stitched onto pages of another reading.
        /// </summary>
        public static string Fingerprint(JObject full)
        {
            var o = new JObject();
            foreach (string name in Lists) if (full[name] != null) o[name] = full[name];
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(o.ToString(Newtonsoft.Json.Formatting.None)));
                return "net:" + BitConverter.ToString(h, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }

        public static JObject ScopeProposal(IEnumerable<CadSegment> segments, int top = 12)
        {
            var byLayer = segments.GroupBy(s => s.Layer ?? "", StringComparer.OrdinalIgnoreCase)
                                  .Select(g => new { layer = g.Key, n = g.Count() })
                                  .OrderByDescending(x => x.n).ThenBy(x => x.layer, StringComparer.Ordinal).ToList();
            return new JObject
            {
                ["layers_by_size"] = new JArray(byLayer.Take(top).Select(x => (JToken)new JObject { ["layer"] = x.layer, ["segments"] = x.n })),
                ["layers_total"] = byLayer.Count,
                ["means"] = "no layer filter was given, so every layer of the drawing was read as a network. Send " +
                            "layers (or a requirement_set whose rules name them) to read only the services you mean."
            };
        }
    }
}

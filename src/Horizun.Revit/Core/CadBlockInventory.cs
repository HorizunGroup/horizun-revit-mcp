// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// EVERY SYMBOL, NOT THE FIFTY MOST FREQUENT NAMES.
//
// MEASURED on a second apartment: the plan listed the fifty busiest unclaimed
// block names and nineteen instances were in none of them. A bounded summary is
// fine for a reply; it is not an inventory, and a classification built on it
// cannot say what it left out. This is the inventory: one row per PLACEMENT the
// reading found, in an order that does not change between readings of the same
// file, paged, with totals that must add up and say so when they do not.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What became of one placement of one block.</summary>
    public static class CadInventoryOutcome
    {
        public const string Claimed = "claimed";
        public const string Unclaimed = "unclaimed";
        public const string Tie = "tie";
        public const string Duplicate = "coincident_duplicate";
        public const string OutsideExtent = "outside_extent";
        public const string PaperSpace = "paper_space";
        public const string SpaceUnknown = "space_unknown";
        public const string NotClassified = "not_classified";

        public static readonly string[] All =
            { Claimed, Unclaimed, Tie, Duplicate, OutsideExtent, PaperSpace, SpaceUnknown, NotClassified };
    }

    public sealed class CadInventoryRow
    {
        public string Key;
        public string BlockName;
        public string Layer;
        public string Space;
        public List<string> Path = new List<string>();
        public List<string> HandleChain = new List<string>();
        public CadPoint DrawingAt;
        public CadPoint? ModelAt;
        public double RotationRadians;
        public bool Mirrored;
        public Dictionary<string, string> Attributes;
        public string Outcome = CadInventoryOutcome.NotClassified;
        public string RuleId;
        public string CandidateId;
        public string DuplicateOf;
        public List<string> TieRules;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["key"] = Key,
                ["block_name"] = BlockName,
                ["bare_name"] = CadBlockRules.BareName(BlockName),
                ["layer"] = Layer,
                ["space"] = Space,
                ["outcome"] = Outcome,
                ["path"] = new JArray(Path),
                ["handle_chain"] = new JArray(HandleChain),
                ["xref"] = XrefOf(Layer, BlockName),
                ["drawing_at"] = new JArray(R(DrawingAt.X), R(DrawingAt.Y), R(DrawingAt.Z)),
                ["model_at_mm"] = ModelAt.HasValue
                    ? (JToken)new JArray(R(ModelAt.Value.X), R(ModelAt.Value.Y), R(ModelAt.Value.Z))
                    : JValue.CreateNull(),
                ["rotation_degrees"] = Math.Round(RotationRadians * 180.0 / Math.PI, 4),
                ["mirrored"] = Mirrored
            };
            if (RuleId != null) o["rule_id"] = RuleId;
            if (CandidateId != null) o["candidate_id"] = CandidateId;
            if (DuplicateOf != null) o["duplicate_of"] = DuplicateOf;
            if (TieRules != null) o["tie_rules"] = new JArray(TieRules);
            if (Attributes != null && Attributes.Count > 0)
                o["attributes"] = new JObject(Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new JProperty(kv.Key, kv.Value)));
            return o;
        }

        /// <summary>
        /// The external reference a layer or block name belongs to - "XREF|LAYER" -
        /// or null for the drawing's own content. AutoCAD writes the prefix; it is
        /// read, not guessed.
        /// </summary>
        public static string XrefOf(string layer, string blockName)
        {
            foreach (string s in new[] { layer, blockName })
            {
                if (string.IsNullOrEmpty(s)) continue;
                int bar = s.IndexOf('|');
                if (bar > 0) return s.Substring(0, bar);
            }
            return null;
        }

        private static double R(double v) => Math.Round(v, 3);
    }

    public static class CadBlockInventory
    {
        /// <summary>The rows in the one order every reading of the same file gives.</summary>
        public static List<CadInventoryRow> Ordered(IEnumerable<CadInventoryRow> rows) =>
            (rows ?? Enumerable.Empty<CadInventoryRow>())
                .Where(r => r != null)
                .OrderBy(r => r.Key ?? "", StringComparer.Ordinal)
                .ThenBy(r => r.Outcome ?? "", StringComparer.Ordinal)
                .ToList();

        /// <summary>A fingerprint over what the rows ARE and what became of them.</summary>
        public static string Fingerprint(IEnumerable<CadInventoryRow> rows)
        {
            var sb = new StringBuilder();
            foreach (CadInventoryRow r in rows)
                sb.Append(r.Key).Append('|').Append(r.BlockName).Append('|').Append(r.Layer).Append('|')
                  .Append(r.Outcome).Append('|').Append(r.RuleId).Append('|').Append(r.CandidateId).Append('\n');
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return "cadinv:" + BitConverter.ToString(h, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// One page, the totals it belongs to, and the checks that the totals add
        /// up. <paramref name="filtered"/> is what the caller asked to see;
        /// <paramref name="all"/> is the whole reading, which the totals describe.
        /// </summary>
        public static JObject Page(List<CadInventoryRow> all, List<CadInventoryRow> filtered, int offset, int limit,
                                   int? instancesConsidered, int? candidates)
        {
            all = Ordered(all);
            filtered = Ordered(filtered ?? all);
            offset = Math.Max(0, offset);
            limit = Math.Max(1, limit);
            List<CadInventoryRow> page = filtered.Skip(offset).Take(limit).ToList();

            var byOutcome = new JObject();
            foreach (string o in CadInventoryOutcome.All)
                byOutcome[o] = all.Count(r => r.Outcome == o);

            int model = all.Count(r => r.Space == "model");
            int kept = all.Count(r => r.Space == "model" && r.Outcome != CadInventoryOutcome.Duplicate);
            int inExtent = all.Count(r => r.Outcome == CadInventoryOutcome.Claimed ||
                                          r.Outcome == CadInventoryOutcome.Unclaimed ||
                                          r.Outcome == CadInventoryOutcome.Tie);
            int claimed = all.Count(r => r.Outcome == CadInventoryOutcome.Claimed);
            var checks = new JObject
            {
                ["every_row_has_one_outcome"] =
                    all.Count == CadInventoryOutcome.All.Sum(o => all.Count(r => r.Outcome == o)),
                ["keys_are_unique"] = all.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() == all.Count,
                ["model_is_kept_plus_duplicates"] =
                    model == kept + all.Count(r => r.Outcome == CadInventoryOutcome.Duplicate),
                ["kept_is_in_zone_plus_outside"] =
                    kept == inExtent + all.Count(r => r.Outcome == CadInventoryOutcome.OutsideExtent) +
                            all.Count(r => r.Space == "model" && r.Outcome == CadInventoryOutcome.NotClassified),
                ["in_zone_matches_the_reading"] = !instancesConsidered.HasValue || instancesConsidered.Value == inExtent,
                ["claimed_matches_the_candidates"] = !candidates.HasValue || candidates.Value == claimed
            };
            bool all_ok = checks.Properties().All(p => p.Value.Type == JTokenType.Boolean && (bool)p.Value);

            var names = all.Where(r => r.Outcome == CadInventoryOutcome.Unclaimed)
                .GroupBy(r => r.BlockName ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new JObject { ["block_name"] = g.Key, ["count"] = g.Count() })
                .OrderByDescending(o => (int)o["count"]).ThenBy(o => (string)o["block_name"], StringComparer.Ordinal);

            return new JObject
            {
                ["total_rows"] = all.Count,
                ["rows_matching_filter"] = filtered.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["next_offset"] = offset + page.Count < filtered.Count ? (JToken)(offset + page.Count) : JValue.CreateNull(),
                ["rows"] = new JArray(page.Select(r => r.ToJson())),
                ["by_outcome"] = byOutcome,
                ["unclaimed_names"] = new JArray(names),
                ["inventory_fingerprint"] = Fingerprint(all),
                ["filtered_fingerprint"] = Fingerprint(filtered),
                ["page_fingerprint"] = Fingerprint(page),
                ["reconciliation"] = checks,
                ["reconciles"] = all_ok,
                ["means"] = "one row per placement of a block, in an order that is the same for every reading of the " +
                            "same file (the insertion handles, outermost first). Page with offset until next_offset " +
                            "is null; the pages concatenated reproduce filtered_fingerprint. reconciles is false when " +
                            "any total fails to add up, and then the reading is not an inventory."
            };
        }
    }
}

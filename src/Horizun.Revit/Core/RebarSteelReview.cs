// -----------------------------------------------------------------------------
// Horizun Revit MCP - every bar of a model against every other, with no requirement set.
// Original Horizun code. No Revit types: the Revit side reads each rebar set and each
// bar of an area or path system into ReviewSetFacts (hook-free centreline at every
// position that exists, model diameter, host and its category) and this judges them.
//
// WHY THIS EXISTS. overlapping_bar and duplicate_bar judge the bars a REQUIREMENT
// SET's rules built. A model generated elsewhere, or built over months by hand, has
// no set for its steel - and those are exactly the models where two bars on one
// centreline (steel ordered twice) or a bar through another (a bar Revit will push)
// go unseen. horizun_query_structure mode=steel_review runs the same two checks -
// RebarOverlap, the same broad phase, the same exact geometry - over every bar in
// scope: each rebar set is its own layout, each area or path system is one layout
// (its layers are laid crossing each other by the system), and every other pair is
// compared.
//
// WHAT IT SAYS. Counts in total and by host category; the pairs, worst first, up to a
// bound the caller sets (the counts are always whole); and what it could not read,
// by name. `clean` is said only when nothing was found AND every bar in scope was
// measured - a model half read is `incomplete`, never clean.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One rebar set, or one bar of an area or path system, as the Revit side read it for the review.</summary>
    public sealed class ReviewSetFacts
    {
        /// <summary>The Rebar or RebarInSystem element.</summary>
        public long Id = -1;
        /// <summary><see cref="RebarSteelReview.LabelSet"/>, <see cref="RebarSteelReview.LabelArea"/> or <see cref="RebarSteelReview.LabelPath"/>.</summary>
        public string Label = RebarSteelReview.LabelSet;
        /// <summary>The area or path system a system bar belongs to, or -1.</summary>
        public long SystemId = -1;
        public long HostId = -1;
        public string HostCategory;
        /// <summary>standard or stirrup_tie (StructuralStyle), or null when it would not read.</summary>
        public string Style;
        public double? ModelDiameterMm;
        /// <summary>The hook-free centreline at every position the model says holds a bar, in model millimetres.</summary>
        public List<OverlapBar> Bars = new List<OverlapBar>();
        /// <summary>Hooked ends cut off before measuring. Published, never silent.</summary>
        public int HooksNotMeasured;
        /// <summary>Why it could not be read. Unknown is never a pass.</summary>
        public string UnreadableWhy;
        /// <summary>The bar sets a coupler in the model joins to this one, read off its ends.</summary>
        public List<long> CoupledTo = new List<long>();
    }

    public static class RebarSteelReview
    {
        public const string Mode = "steel_review";
        public const string LabelSet = "rebar_set";
        public const string LabelArea = "area";
        public const string LabelPath = "path";

        public const string VerdictClean = "clean";
        public const string VerdictFindings = "findings";
        public const string VerdictIncomplete = "incomplete";

        public const int DefaultMaxPairs = 50;
        public const int MaxPairs = 500;

        public const string CodeToleranceRequired = "tolerance_required";
        public const string CodeClearancesNotUsable = "clearances_not_usable";

        /// <summary>
        /// The member the overlap check measures. A rebar set is a layout of its own; every bar of one area or
        /// path SYSTEM shares the system's layout - the system lays its layers crossing each other, so its own bars
        /// are never compared with each other.
        /// </summary>
        public static OverlapMember Member(ReviewSetFacts f)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            bool system = (f.Label == LabelArea || f.Label == LabelPath) && f.SystemId > 0;
            var m = new OverlapMember
            {
                RebarId = f.Id,
                HostId = f.HostId,
                Label = f.Label,
                SystemId = f.SystemId,
                HostCategory = f.HostCategory,
                Style = f.Style,
                Layout = system ? "system:" + f.SystemId.ToString(CultureInfo.InvariantCulture)
                                : "element:" + f.Id.ToString(CultureInfo.InvariantCulture),
                HooksNotMeasured = f.HooksNotMeasured
            };
            foreach (long c in f.CoupledTo ?? new List<long>()) if (c > 0 && c != f.Id) m.CoupledTo.Add(c);
            if (f.UnreadableWhy != null) { m.UnreadableWhy = f.UnreadableWhy; return m; }
            if (!f.ModelDiameterMm.HasValue || f.ModelDiameterMm.Value <= 0 || double.IsNaN(f.ModelDiameterMm.Value))
            {
                m.UnreadableWhy = "the bar type would not report a MODEL diameter, and a clear distance is measured between bar surfaces.";
                return m;
            }
            m.RadiusMm = f.ModelDiameterMm.Value / 2;
            foreach (OverlapBar b in f.Bars ?? new List<OverlapBar>())
                if (b != null && b.Points != null && b.Points.Count >= 2) m.Bars.Add(b);
            if (m.Bars.Count == 0)
                m.UnreadableWhy = "no position of this set returned a centreline, so where its bars stand is not known.";
            return m;
        }

        /// <summary>
        /// The optional clearances of a review: between_rules_mm (bars of different sets running alongside) and
        /// crossing_mm, each 0 to 500. Pairs by rule id mean nothing without a requirement set and are refused.
        /// Null with no error when none were given.
        /// </summary>
        public static StructuralClearances ReadClearances(JToken t, out string error)
        {
            error = null;
            if (t == null || t.Type == JTokenType.Null) return new StructuralClearances();
            var o = t as JObject;
            if (o == null) { error = CodeClearancesNotUsable + ": clearances must be an object."; return null; }
            var c = new StructuralClearances();
            foreach (JProperty p in o.Properties())
            {
                if (p.Name != "between_rules_mm" && p.Name != "crossing_mm")
                {
                    error = CodeClearancesNotUsable + ": clearances." + p.Name + " is not read here - a steel review " +
                            "takes between_rules_mm and crossing_mm; pairs name rules, and a model with no requirement " +
                            "set has none.";
                    return null;
                }
                double v;
                if (p.Value.Type != JTokenType.Integer && p.Value.Type != JTokenType.Float) v = double.NaN;
                else v = p.Value.Value<double>();
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > 500)
                {
                    error = CodeClearancesNotUsable + ": clearances." + p.Name + " must be a number from 0 to 500 mm.";
                    return null;
                }
                if (p.Name == "between_rules_mm") c.BetweenRulesMm = v;
                else { c.CrossingMm = v; c.CrossingDeclared = true; }
            }
            return c;
        }

        /// <summary>
        /// The review: duplicate_bar and overlapping_bar over every set, counted in total and by host category,
        /// the pairs worst first up to `maxPairs`, the unreadable named. `scopeReasons` are what the reader could
        /// not put in scope (each one makes the coverage partial); `fabricNotCompared` is how many fabric sheets
        /// were in scope and are not compared (welded wire, no bar centreline here).
        /// </summary>
        public static JObject Review(IList<ReviewSetFacts> sets, StructuralClearances clearances, double tolMm, int maxPairs,
                                     JArray scopeReasons, int fabricNotCompared, double cellMm = 0)
        {
            clearances = clearances ?? new StructuralClearances();
            if (maxPairs < 1) maxPairs = DefaultMaxPairs;
            var members = new List<OverlapMember>();
            foreach (ReviewSetFacts f in sets ?? new List<ReviewSetFacts>())
                if (f != null) members.Add(Member(f));
            OverlapReport r = RebarOverlap.Check(members, clearances, tolMm, cellMm);

            List<OverlapPair> dups = r.Pairs.Where(p => p.IsDuplicate).ToList();
            List<OverlapPair> hits = r.Pairs.Where(p => p.Kind == RebarOverlap.KindIntersection).ToList();
            List<OverlapPair> close = r.Pairs.Where(p => p.Kind == RebarOverlap.KindTooClose).ToList();

            // ------------------------------------------------------------ counts by host category
            var cats = new SortedDictionary<string, Tally>(StringComparer.Ordinal);
            Func<OverlapMember, Tally> tallyOf = m =>
            {
                string k = string.IsNullOrWhiteSpace(m.HostCategory) ? "(unknown)" : m.HostCategory;
                Tally t;
                if (!cats.TryGetValue(k, out t)) { t = new Tally(); cats[k] = t; }
                return t;
            };
            foreach (OverlapMember m in members)
            {
                Tally t = tallyOf(m);
                t.Sets++;
                if (m.Label == LabelArea || m.Label == LabelPath) t.SystemBars++;
                t.Bars += m.Bars.Count;
                if (m.UnreadableWhy != null) t.Unreadable++;
            }
            foreach (OverlapPair p in r.Pairs)
            {
                Tally a = tallyOf(p.A), b = tallyOf(p.B);
                foreach (Tally t in a == b ? new[] { a } : new[] { a, b })
                {
                    if (p.IsDuplicate) { t.DupSets++; t.DupBars += p.BarPairs; }
                    else if (p.Kind == RebarOverlap.KindIntersection) { t.HitSets++; t.HitBars += p.BarPairs; }
                    else { t.CloseSets++; t.CloseBars += p.BarPairs; }
                }
            }
            var byCat = new JArray();
            foreach (var kv in cats)
                byCat.Add(new JObject
                {
                    ["host_category"] = kv.Key,
                    ["sets"] = kv.Value.Sets,
                    ["system_bars"] = kv.Value.SystemBars,
                    ["bars"] = kv.Value.Bars,
                    ["unreadable_sets"] = kv.Value.Unreadable,
                    ["duplicates"] = Count(kv.Value.DupSets, kv.Value.DupBars),
                    ["intersections"] = Count(kv.Value.HitSets, kv.Value.HitBars),
                    ["closer_than_declared_clearance"] = Count(kv.Value.CloseSets, kv.Value.CloseBars)
                });

            // ------------------------------------------------------------ the pairs, worst first
            var ordered = dups.OrderByDescending(p => p.BarPairs).ThenBy(p => p.A.RebarId).ThenBy(p => p.B.RebarId)
                .Concat(hits.OrderBy(p => p.ClearMm).ThenBy(p => p.A.RebarId).ThenBy(p => p.B.RebarId))
                .Concat(close.OrderBy(p => p.ClearMm - p.RequiredClearMm).ThenBy(p => p.A.RebarId).ThenBy(p => p.B.RebarId))
                .ToList();
            var pairs = new JArray();
            foreach (OverlapPair p in ordered.Take(maxPairs))
            {
                JObject j = p.ToJson();
                j.Remove("rule_id"); j.Remove("other_rule_id");
                j["style"] = p.A.Style;
                j["other_style"] = p.B.Style;
                j["host_category"] = p.A.HostCategory;
                j["other_host_category"] = p.B.HostCategory;
                j["code"] = p.IsDuplicate ? RebarFinding.DuplicateBar : RebarFinding.OverlappingBar;
                pairs.Add(j);
            }

            // ------------------------------------------------------------ what could not be read
            var reasons = new JArray();
            foreach (JToken t in scopeReasons ?? new JArray()) reasons.Add(t.DeepClone());
            var unreadable = new JArray();
            foreach (OverlapMember m in r.Unreadable)
            {
                if (unreadable.Count < maxPairs)
                    unreadable.Add(new JObject
                    {
                        ["rebar_id"] = m.RebarId, ["what"] = m.Label, ["system_id"] = m.SystemId > 0 ? (JToken)m.SystemId : JValue.CreateNull(),
                        ["host_id"] = m.HostId, ["host_category"] = m.HostCategory, ["why"] = m.UnreadableWhy
                    });
            }
            if (r.Unreadable.Count > 0)
                reasons.Add(StructuralCoverage.Reason("bars",
                    r.Unreadable.Count + " set(s) or system bar(s) in scope could not be measured (listed in unreadable, up to " +
                    maxPairs + "), so they were compared with nothing: a duplicate or a crossing involving them is unknown."));
            JObject coverage = StructuralCoverage.Declare(
                reasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial,
                members.Count - r.Unreadable.Count, r.Unreadable.Count, reasons);

            string verdict = r.Pairs.Count > 0 ? VerdictFindings : reasons.Count > 0 ? VerdictIncomplete : VerdictClean;
            JObject overlapJson = r.ToJson();
            var result = new JObject
            {
                ["mode"] = Mode,
                ["verdict"] = verdict,
                ["verdict_means"] =
                    "clean: every bar in scope was measured and no two bars of different sets lie on one centreline, pass " +
                    "through each other, or come closer than a declared clearance. findings: at least one pair was found " +
                    "(counts and pairs below). incomplete: nothing was found among what could be read, and something in " +
                    "scope could not be - that is not clean.",
                ["tolerance_mm"] = r.ToleranceMm,
                ["tolerance_means"] =
                    "declared by the caller: a duplicate is a bar every point of which lies within this of another bar of " +
                    "the same model diameter (and the other way round); an intersection is surfaces interpenetrating by " +
                    "more than this.",
                ["same_diameter_within_mm"] = RebarOverlap.SameDiameterMm,
                ["checked"] = r.Checked,
                ["checked_means"] = overlapJson["checked_means"],
                ["clearances"] = clearances.ToJson(),
                ["alongside_within_degrees"] = RebarOverlap.AlongsideDegrees,
                ["counts"] = new JObject
                {
                    ["sets"] = members.Count(m => m.Label != LabelArea && m.Label != LabelPath),
                    ["system_bars"] = members.Count(m => m.Label == LabelArea || m.Label == LabelPath),
                    ["systems"] = members.Where(m => m.SystemId > 0).Select(m => m.SystemId).Distinct().Count(),
                    ["bars_compared"] = r.BarsCompared,
                    ["unreadable_sets"] = r.Unreadable.Count,
                    ["hooks_not_measured"] = r.Excused[RebarOverlap.ExcuseHook],
                    ["duplicates"] = Count(dups.Count, dups.Sum(p => p.BarPairs)),
                    ["intersections"] = Count(hits.Count, hits.Sum(p => p.BarPairs)),
                    ["closer_than_declared_clearance"] = Count(close.Count, close.Sum(p => p.BarPairs)),
                    ["fabric_sheets_not_compared"] = fabricNotCompared,
                    ["coupled_in_the_model"] = r.Excused[RebarOverlap.ExcuseCoupler]
                },
                ["intersections_by_styles"] = ByStyles(hits),
                ["intersections_by_styles_means"] =
                    "the intersections counted by the styles of their two sets (standard, stirrup_tie). MEASURED (L10): " +
                    "Revit seats a cage's corner bars in its stirrup's bends, 3.53 mm into the bend for a 16 mm bar in a " +
                    "10 mm stirrup - deeper than a tight tolerance - so standard+stirrup_tie in one host may be that seat, " +
                    "which a requirement set's audit excuses as a declared cage and this review, with no declaration, cannot.",
                ["counts_means"] =
                    "set_pairs: pairs of sets (or system bars) with at least one such bar pair; bar_pairs: the bar " +
                    "positions involved. A duplicate is counted ONLY as a duplicate - never also as an intersection. " +
                    "coupled_in_the_model: pairs of sets a Revit coupler joins, which meet end to end at it by construction " +
                    "and are not intersections (a coupler is a fact of the model, not a declaration).",
                ["by_host_category"] = byCat,
                ["by_host_category_means"] =
                    "the same counts per category of the bars' host, as Revit names it. A pair whose two sets stand in " +
                    "hosts of different categories is counted under both, so the categories do not add up to the total.",
                ["pairs"] = pairs,
                ["pairs_order"] = "duplicates (most bar pairs first), then intersections (deepest first), then bars closer than declared.",
                ["unreadable"] = unreadable,
                ["coverage"] = coverage,
                ["not_compared"] =
                    "two bars of one set; two bars of one area or path system (the system lays its own layers crossing); " +
                    "two sets a coupler in the model joins (counted in coupled_in_the_model; a duplicate is still a duplicate); " +
                    "fabric sheets (welded wire - counted in fabric_sheets_not_compared); bars in linked models. Hooks are " +
                    "cut off each bar before measuring (hooks_not_measured).",
                ["broad_phase"] = overlapJson["broad_phase"],
                ["segments"] = r.Segments,
                ["elapsed_ms"] = r.ElapsedMs,
                ["writes_nothing"] = true
            };
            if (ordered.Count > maxPairs) result["pairs_not_listed"] = ordered.Count - maxPairs;
            if (r.Unreadable.Count > maxPairs) result["unreadable_not_listed"] = r.Unreadable.Count - maxPairs;
            return result;
        }

        private static JObject ByStyles(IEnumerable<OverlapPair> pairs)
        {
            var d = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
            foreach (OverlapPair p in pairs)
            {
                string[] two = { p.A.Style ?? "unknown", p.B.Style ?? "unknown" };
                Array.Sort(two, StringComparer.Ordinal);
                string k = two[0] + "+" + two[1] + (p.A.HostId == p.B.HostId ? " (one host)" : " (two hosts)");
                int[] c;
                if (!d.TryGetValue(k, out c)) { c = new int[2]; d[k] = c; }
                c[0]++; c[1] += p.BarPairs;
            }
            var o = new JObject();
            foreach (var kv in d) o[kv.Key] = Count(kv.Value[0], kv.Value[1]);
            return o;
        }

        private static JObject Count(int sets, int bars) => new JObject { ["set_pairs"] = sets, ["bar_pairs"] = bars };

        private sealed class Tally
        {
            public int Sets, SystemBars, Bars, Unreadable, DupSets, DupBars, HitSets, HitBars, CloseSets, CloseBars;
        }
    }
}

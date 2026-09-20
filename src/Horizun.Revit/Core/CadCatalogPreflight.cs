// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// THE CATALOGUE, CHECKED BEFORE ANYTHING IS WRITTEN.
//
// A wall conversion chooses a wall type per interpreted thickness, or withdraws the
// wall. Read one wall at a time that is correct and unreadable: the question a person
// has is "which thicknesses does this drawing ask for, which types answer them, what
// is left out, and what else is lost with it". So the choices and withdrawals are
// grouped by thickness, alternatives the caller names (test types, say) are measured
// against each group WITHOUT being used, and a symbol that was withdrawn because the
// wall it is drawn against is not in the model is tied to the withdrawn wall it stands
// on - the device a missing type costs, by name.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadCatalogPreflight
    {
        /// <summary>Thicknesses closer than this are one group (drawings round in 1/16 in).</summary>
        public const double GroupMm = 0.5;

        /// <summary>
        /// chosen: the plan's wall_types_chosen rows; withdrawn: every withdrawn row (walls are the
        /// ones with kind 'wall'); alternatives: (name, width mm) of types the caller would consider,
        /// never used here.
        /// </summary>
        public static JObject Summarize(IEnumerable<JObject> chosen, IEnumerable<JObject> withdrawn,
                                        IEnumerable<Tuple<string, double>> alternatives, double toleranceMm)
        {
            var rows = new List<Tuple<double, JObject, bool>>();
            foreach (JObject c in chosen ?? Enumerable.Empty<JObject>())
            {
                double? t = c.Value<double?>("thickness_mm");
                if (t.HasValue) rows.Add(Tuple.Create(t.Value, c, false));
            }
            var walls = (withdrawn ?? Enumerable.Empty<JObject>()).Where(w => (string)w["kind"] == "wall").ToList();
            foreach (JObject w in walls)
            {
                double? t = w.Value<double?>("thickness_mm");
                if (t.HasValue) rows.Add(Tuple.Create(t.Value, w, true));
            }
            var alts = (alternatives ?? Enumerable.Empty<Tuple<string, double>>()).ToList();
            var groups = new JArray();
            foreach (var g in Group(rows))
            {
                double t = Math.Round(g.Average(r => r.Item1), 1);
                var types = g.Where(r => !r.Item3).GroupBy(r => (string)r.Item2["chosen"] ?? "(none)")
                             .Select(x => new JObject
                             {
                                 ["type"] = x.Key, ["walls"] = x.Count(),
                                 ["off_by_mm"] = x.First().Item2["off_by_mm"]
                             });
                var reasons = g.Where(r => r.Item3).GroupBy(r => (string)r.Item2["reason"] ?? "(unstated)")
                               .Select(x => new JObject { ["reason"] = x.Key, ["walls"] = x.Count() });
                JObject sample = g.First().Item2;
                int withdrawnCount = g.Count(r => r.Item3);
                groups.Add(new JObject
                {
                    ["thickness_mm"] = t,
                    ["walls"] = g.Count,
                    ["length_mm"] = Math.Round(g.Sum(r => Length(r.Item2)), 0),
                    ["built_as"] = new JArray(types),
                    ["withdrawn"] = withdrawnCount,
                    ["withdrawn_because"] = new JArray(reasons),
                    ["listed_candidates"] = sample["candidates"],
                    ["verdict"] = withdrawnCount == 0 ? "answered_by_the_catalogue"
                                : withdrawnCount == g.Count ? "no_type_in_the_catalogue" : "partly_answered",
                    // MEASURED, NOT USED: an alternative is a proposal for a person.
                    ["alternatives_that_would_fit"] = new JArray(alts
                        .Where(a => Math.Abs(a.Item2 - t) <= toleranceMm + 1e-6)
                        .OrderBy(a => Math.Abs(a.Item2 - t)).ThenBy(a => a.Item1, StringComparer.Ordinal)
                        .Select(a => new JObject
                        {
                            ["type"] = a.Item1, ["width_mm"] = Math.Round(a.Item2, 1),
                            ["off_by_mm"] = Math.Round(a.Item2 - t, 1)
                        }))
                });
            }
            return new JObject
            {
                ["thickness_groups"] = groups,
                ["walls_considered"] = rows.Count,
                ["walls_withdrawn"] = walls.Count,
                ["fallback_to_a_generic_type"] = 0,
                ["means"] = "every interpreted wall thickness, what the listed types answer and what is withdrawn. " +
                            "alternatives_that_would_fit are measured and NEVER used: choosing one is a decision. " +
                            "There is no silent fallback - a wall no listed type fits is withdrawn by name, or built " +
                            "as its rule's family_type only where the rule says so (built_as names it)."
            };
        }

        /// <summary>
        /// The withdrawn symbols that stand against a withdrawn wall: the drawn wall line they are
        /// against lies on that wall's band (parallel, within half its thickness plus the tolerance,
        /// and overlapping it along its length).
        /// </summary>
        public static JArray AffectedSymbols(IEnumerable<JObject> withdrawnWalls, IEnumerable<JObject> withdrawnSymbols,
                                             double toleranceMm, double angleDegrees = 2.0)
        {
            var out_ = new JArray();
            var walls = (withdrawnWalls ?? Enumerable.Empty<JObject>()).Where(w => (string)w["kind"] == "wall").ToList();
            foreach (JObject s in withdrawnSymbols ?? Enumerable.Empty<JObject>())
            {
                if ((string)s["reason"] != "nearer_drawn_wall_is_not_in_the_model") continue;
                var line = s["other_wall_line"] as JObject;
                double[] a = Pt(line?["from_mm"]), b = Pt(line?["to_mm"]);
                if (a == null || b == null) continue;
                var hits = new List<JObject>();
                foreach (JObject w in walls)
                {
                    double[] p = Pt(w["from_mm"]), q = Pt(w["to_mm"]);
                    double? t = w.Value<double?>("thickness_mm");
                    if (p == null || q == null || !t.HasValue) continue;
                    if (OnBand(p, q, t.Value / 2.0 + toleranceMm, a, b, angleDegrees)) hits.Add(w);
                }
                out_.Add(new JObject
                {
                    ["source_row"] = s["source_row"],
                    ["kind"] = s["kind"],
                    ["at_mm"] = s["at_mm"],
                    ["stands_against"] = hits.Count == 1
                        ? new JObject
                        {
                            ["wall_source_row"] = hits[0]["source_row"],
                            ["thickness_mm"] = hits[0]["thickness_mm"],
                            ["withdrawn_because"] = hits[0]["reason"]
                        }
                        : null,
                    ["verdict"] = hits.Count == 1 ? "lost_with_that_wall"
                                : hits.Count == 0 ? "its_wall_is_not_a_withdrawn_one" : "more_than_one_withdrawn_wall"
                });
            }
            return out_;
        }

        private static List<List<Tuple<double, JObject, bool>>> Group(List<Tuple<double, JObject, bool>> rows)
        {
            var groups = new List<List<Tuple<double, JObject, bool>>>();
            foreach (var r in rows.OrderBy(r => r.Item1))
            {
                var last = groups.LastOrDefault();
                if (last != null && r.Item1 - last[0].Item1 <= GroupMm) last.Add(r);
                else groups.Add(new List<Tuple<double, JObject, bool>> { r });
            }
            return groups;
        }

        private static double Length(JObject row)
        {
            double[] a = Pt(row["from_mm"]), b = Pt(row["to_mm"]);
            return a == null || b == null ? 0 : Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
        }

        private static double[] Pt(JToken t)
        {
            var arr = t as JArray;
            if (arr == null || arr.Count < 2) return null;
            try
            {
                return new[] { Convert.ToDouble(((JValue)arr[0]).Value, CultureInfo.InvariantCulture),
                               Convert.ToDouble(((JValue)arr[1]).Value, CultureInfo.InvariantCulture) };
            }
            catch { return null; }
        }

        private static bool OnBand(double[] p, double[] q, double halfBand, double[] a, double[] b, double angleDegrees)
        {
            double ux = q[0] - p[0], uy = q[1] - p[1];
            double len = Math.Sqrt(ux * ux + uy * uy);
            if (len < 1e-6) return false;
            ux /= len; uy /= len;
            double vx = b[0] - a[0], vy = b[1] - a[1];
            double vl = Math.Sqrt(vx * vx + vy * vy);
            if (vl < 1e-6) return false;
            double cross = Math.Abs(ux * vy / vl - uy * vx / vl);
            if (cross > Math.Sin(angleDegrees * Math.PI / 180.0)) return false;
            double dA = Math.Abs(-(a[0] - p[0]) * uy + (a[1] - p[1]) * ux);
            double dB = Math.Abs(-(b[0] - p[0]) * uy + (b[1] - p[1]) * ux);
            if (Math.Max(dA, dB) > halfBand) return false;
            double sa = (a[0] - p[0]) * ux + (a[1] - p[1]) * uy, sb = (b[0] - p[0]) * ux + (b[1] - p[1]) * uy;
            return Math.Max(sa, sb) > 0 && Math.Min(sa, sb) < len;
        }
    }
}

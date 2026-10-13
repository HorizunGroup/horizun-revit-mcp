// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// THE MODEL AGAINST THE SCHEDULE IT WAS BUILT FROM, MARK BY MARK.
//
// A detail schedule lists, per bar mark, how many bars, how long each one is and -
// often - what the line weighs. horizun_query_structure mode=quantities reads what
// the model carries; this compares the two (refuerzo desde cartilla, F1.3). The
// tolerances are the caller's and are required: whether 0.5 % of length is a
// difference is a decision, not a constant. Weight is compared only when a density
// and its source are declared - this bridge carries no density of its own.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RebarReconcile
    {
        public const string Agrees = "agrees";
        public const string Differs = "differs";
        public const string MissingInModel = "missing_in_model";
        public const string NotInSchedule = "not_in_schedule";
        public const string Incomplete = "incomplete";

        public const string CodeNoRows = "schedule_has_no_rows";
        public const string CodeRow = "schedule_row_not_usable";
        public const string CodeTolerance = "tolerances_not_declared";

        /// <summary>One schedule line: a mark, its bar type or diameter, how many bars, how long each, and optionally the line's weight.</summary>
        public sealed class Line
        {
            public string Mark;
            public string BarType;
            public double? DiameterMm;
            public int Count;
            public double LengthEachMm;
            public double? WeightKg;
        }

        public static readonly HashSet<string> RowKeys =
            new HashSet<string>(new[] { "mark", "bar_type", "diameter_mm", "count", "length_mm", "weight_kg" }, StringComparer.Ordinal);

        /// <summary>Read the schedule rows. Null and an error when one is not usable; nothing is guessed.</summary>
        public static List<Line> ReadSchedule(JArray rows, out string code, out string error)
        {
            code = null; error = null;
            var lines = new List<Line>();
            if (rows == null || rows.Count == 0) { code = CodeNoRows; error = "schedule must list at least one row."; return null; }
            for (int i = 0; i < rows.Count; i++)
            {
                var o = rows[i] as JObject;
                string at = "schedule[" + i + "]";
                if (o == null) { code = CodeRow; error = at + " must be an object."; return null; }
                foreach (JProperty p in o.Properties())
                    if (!RowKeys.Contains(p.Name)) { code = CodeRow; error = at + "." + p.Name + " is not a known key (" + string.Join(", ", RowKeys) + ")."; return null; }
                var l = new Line { Mark = o.Value<string>("mark"), BarType = o.Value<string>("bar_type") };
                if (string.IsNullOrWhiteSpace(l.Mark)) { code = CodeRow; error = at + " needs mark: the bar mark the schedule and the model share."; return null; }
                if (o["diameter_mm"] != null)
                {
                    if (!Number(o["diameter_mm"], out double d) || d <= 0) { code = CodeRow; error = at + ".diameter_mm must be a positive number."; return null; }
                    l.DiameterMm = d;
                }
                if (l.BarType == null && !l.DiameterMm.HasValue) { code = CodeRow; error = at + " needs bar_type or diameter_mm: a count of bars of no stated size reconciles nothing."; return null; }
                JToken c = o["count"];
                if (c == null || c.Type != JTokenType.Integer || c.Value<int>() < 1) { code = CodeRow; error = at + ".count must be a whole number of bars, one or more."; return null; }
                l.Count = c.Value<int>();
                if (!Number(o["length_mm"], out double len) || len <= 0) { code = CodeRow; error = at + ".length_mm must be the positive length of EACH bar, in mm."; return null; }
                l.LengthEachMm = len;
                if (o["weight_kg"] != null)
                {
                    if (!Number(o["weight_kg"], out double w) || w < 0) { code = CodeRow; error = at + ".weight_kg must be a number, zero or more."; return null; }
                    l.WeightKg = w;
                }
                lines.Add(l);
            }
            return lines;
        }

        /// <summary>
        /// Compare the schedule with a quantities takeoff grouped by mark and bar type
        /// (RebarTakeoff.Group output). `lengthTolerancePct` and `massTolerancePct` are the
        /// caller's; a count must match exactly. Returns { rows, summary, verdict }.
        /// </summary>
        public static JObject Compare(List<Line> schedule, JObject takeoff, double lengthTolerancePct, double? massTolerancePct)
        {
            // THE SCHEDULE, per mark: counts and total lengths add up across lines of one mark.
            var byMark = new Dictionary<string, List<Line>>(StringComparer.Ordinal);
            foreach (Line l in schedule)
            {
                List<Line> b;
                if (!byMark.TryGetValue(l.Mark, out b)) byMark[l.Mark] = b = new List<Line>();
                b.Add(l);
            }

            // THE MODEL, per mark.
            var model = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
            foreach (JObject g in (takeoff?["groups"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string mark = (string)g["group"]?["mark"];
                if (mark == null) mark = "";
                List<JObject> b;
                if (!model.TryGetValue(mark, out b)) model[mark] = b = new List<JObject>();
                b.Add(g);
            }

            var rows = new JArray();
            int agrees = 0, differs = 0, missing = 0, extra = 0, incomplete = 0;
            foreach (string mark in byMark.Keys.Concat(model.Keys).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal))
            {
                List<Line> s; List<JObject> m;
                byMark.TryGetValue(mark, out s);
                model.TryGetValue(mark, out m);
                var row = new JObject { ["mark"] = mark };
                if (s == null)
                {
                    row["verdict"] = NotInSchedule;
                    row["model"] = ModelSide(m);
                    row["means"] = "the model carries bars under this mark and the schedule lists none.";
                    extra++; rows.Add(row); continue;
                }
                int sCount = s.Sum(x => x.Count);
                double sLengthM = s.Sum(x => x.Count * x.LengthEachMm) / 1000.0;
                double? sWeight = s.All(x => x.WeightKg.HasValue) ? s.Sum(x => x.WeightKg.Value) : (double?)null;
                var schedSide = new JObject
                {
                    ["count"] = sCount, ["total_length_m"] = Math.Round(sLengthM, 4),
                    ["weight_kg"] = sWeight.HasValue ? (JToken)Math.Round(sWeight.Value, 3) : JValue.CreateNull(),
                    ["bar_types"] = new JArray(s.Where(x => x.BarType != null).Select(x => x.BarType).Distinct()),
                    ["diameters_mm"] = new JArray(s.Where(x => x.DiameterMm.HasValue).Select(x => x.DiameterMm.Value).Distinct())
                };
                row["schedule"] = schedSide;
                if (m == null)
                {
                    row["verdict"] = MissingInModel;
                    row["means"] = "the schedule lists this mark and the model carries no bar under it.";
                    missing++; rows.Add(row); continue;
                }
                JObject modelSide = ModelSide(m);
                row["model"] = modelSide;
                bool complete = m.All(g => (bool?)g["complete"] == true);
                var findings = new JArray();

                // THE BAR TYPE: every type the model carries under this mark must be one the schedule names.
                var sTypes = new HashSet<string>(s.Where(x => x.BarType != null).Select(x => x.BarType), StringComparer.Ordinal);
                var sDias = s.Where(x => x.DiameterMm.HasValue).Select(x => x.DiameterMm.Value).ToList();
                foreach (JObject g in m)
                {
                    string bt = (string)g["group"]?["bar_type"];
                    if (sTypes.Count > 0 && bt != null && !sTypes.Contains(bt))
                        findings.Add(new JObject { ["field"] = "bar_type", ["schedule"] = new JArray(sTypes), ["model"] = bt });
                    double? nd = (double?)g["group"]?["nominal_diameter_mm"];
                    if (sTypes.Count == 0 && sDias.Count > 0 && nd.HasValue && !sDias.Any(d => Math.Abs(d - nd.Value) <= 0.05))
                        findings.Add(new JObject { ["field"] = "diameter_mm", ["schedule"] = new JArray(sDias), ["model"] = nd.Value });
                }

                int? mBars = Sum(m, "bars");
                double? mLength = SumD(m, "total_length_m");
                if (!mBars.HasValue || !mLength.HasValue) complete = false;
                if (mBars.HasValue && mBars.Value != sCount)
                    findings.Add(new JObject { ["field"] = "count", ["schedule"] = sCount, ["model"] = mBars.Value, ["tolerance"] = "exact" });
                if (mLength.HasValue && Math.Abs(mLength.Value - sLengthM) > sLengthM * lengthTolerancePct / 100.0)
                    findings.Add(new JObject
                    {
                        ["field"] = "total_length_m", ["schedule"] = Math.Round(sLengthM, 4), ["model"] = Math.Round(mLength.Value, 4),
                        ["difference_pct"] = Math.Round((mLength.Value - sLengthM) / sLengthM * 100.0, 3),
                        ["tolerance_pct"] = lengthTolerancePct
                    });
                double? mMass = SumD(m, "mass_kg");
                if (sWeight.HasValue && massTolerancePct.HasValue)
                {
                    if (!mMass.HasValue)
                        row["weight_not_compared"] = "the model's weight was not computed: declare density_kg_per_m3 and density_source.";
                    else if (Math.Abs(mMass.Value - sWeight.Value) > sWeight.Value * massTolerancePct.Value / 100.0)
                        findings.Add(new JObject
                        {
                            ["field"] = "weight_kg", ["schedule"] = Math.Round(sWeight.Value, 3), ["model"] = Math.Round(mMass.Value, 3),
                            ["difference_pct"] = sWeight.Value > 0 ? (JToken)Math.Round((mMass.Value - sWeight.Value) / sWeight.Value * 100.0, 3) : JValue.CreateNull(),
                            ["tolerance_pct"] = massTolerancePct.Value
                        });
                }
                else if (sWeight.HasValue)
                    row["weight_not_compared"] = "no weight tolerance was declared (tolerances.weight_pct).";
                else
                {
                    // PART OF A MARK WEIGHED IS NOT ITS WEIGHT (backlog #57): the sum of the lines that
                    // state one would read as the mark's, and dropping it silently read as "no weight".
                    int weighed = s.Count(x => x.WeightKg.HasValue);
                    if (weighed > 0)
                        row["weight_not_compared"] = "only " + weighed + " of " + s.Count + " schedule lines for this mark state " +
                                                     "weight_kg, so the mark has no weight to compare; a sum of part of its lines " +
                                                     "would read as the whole.";
                }

                row["differences"] = findings;
                if (findings.Count > 0) { row["verdict"] = Differs; differs++; }
                else if (!complete) { row["verdict"] = Incomplete; incomplete++; row["means"] = "part of what the model carries under this mark could not be read, so agreement is not claimed."; }
                else { row["verdict"] = Agrees; agrees++; }
                rows.Add(row);
            }

            string verdict = differs + missing + extra > 0 ? "differences_found" : incomplete > 0 ? Incomplete : Agrees;
            return new JObject
            {
                ["verdict"] = verdict,
                ["summary"] = new JObject
                {
                    ["marks"] = rows.Count, ["agrees"] = agrees, ["differs"] = differs,
                    ["missing_in_model"] = missing, ["not_in_schedule"] = extra, ["incomplete"] = incomplete
                },
                ["tolerances"] = new JObject
                {
                    ["count"] = "exact", ["length_pct"] = lengthTolerancePct,
                    ["weight_pct"] = massTolerancePct.HasValue ? (JToken)massTolerancePct.Value : JValue.CreateNull()
                },
                ["rows"] = rows,
                ["length_means"] = "the schedule's length is count x length_mm of each bar; the model's is read off each set (Rebar total length), hooks and bends included as Revit measures them."
            };
        }

        private static JObject ModelSide(List<JObject> m)
        {
            if (m == null) return null;
            return new JObject
            {
                ["sets"] = m.Sum(g => (int?)g["sets"] ?? 0),
                ["bars"] = Sum(m, "bars").HasValue ? (JToken)Sum(m, "bars").Value : JValue.CreateNull(),
                ["total_length_m"] = SumD(m, "total_length_m").HasValue ? (JToken)Math.Round(SumD(m, "total_length_m").Value, 4) : JValue.CreateNull(),
                ["mass_kg"] = SumD(m, "mass_kg").HasValue ? (JToken)Math.Round(SumD(m, "mass_kg").Value, 3) : JValue.CreateNull(),
                ["bar_types"] = new JArray(m.Select(g => (string)g["group"]?["bar_type"]).Where(x => x != null).Distinct())
            };
        }

        private static int? Sum(List<JObject> m, string key)
        {
            int total = 0;
            foreach (JObject g in m) { JToken t = g[key]; if (t == null || t.Type == JTokenType.Null) return null; total += t.Value<int>(); }
            return total;
        }

        private static double? SumD(List<JObject> m, string key)
        {
            double total = 0;
            foreach (JObject g in m) { JToken t = g[key]; if (t == null || t.Type == JTokenType.Null) return null; total += t.Value<double>(); }
            return total;
        }

        private static bool Number(JToken t, out double v)
        {
            v = 0;
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)) return false;
            v = t.Value<double>();
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}

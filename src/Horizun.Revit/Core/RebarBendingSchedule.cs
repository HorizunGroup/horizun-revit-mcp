// -----------------------------------------------------------------------------
// Horizun Revit MCP - the bending schedule (cartilla), derived from the model.
// Original Horizun code. No Revit types: the rows are what RebarFacts emits.
//
// ONE MARK, ONE BAR. A schedule line is a mark: so many bars of this type, bent to
// this shape, with these legs, each this long. Several sets carrying one mark is
// how a schedule is MEANT to work - Revit numbers identical bars with the same
// Rebar Number itself - and it is wrong only when the sets under that mark are not
// the same bar. RebarMarkIdentity is the one place that decides "the same bar";
// the audit's bar_mark_conflict and this schedule's split lines both ask it, so
// the two can never disagree about what a mark covers.
//
// DERIVED, NOT DECLARED. Every number here is read off the bars in the model
// (counts, Revit's own total length and volume, the centreline as declared). It is
// what the model carries, which is the thing to check a drawn cartilla against -
// not a replacement for the engineer's.
//
// No default density. A weight needs the density and its source, both declared by
// the caller, or there is no schedule at all: a cartilla without kilograms is not
// what anybody asked for, and kilograms from an invented density are worse.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Whether two RebarFacts rows are the same bar: bar type, shape and one bar's length.</summary>
    public static class RebarMarkIdentity
    {
        public const string PropBarType = "bar_type";
        public const string PropShape = "shape";
        public const string PropLength = "one_bar_length_mm";

        public const string SourceCentreline = "bar_centreline_length_mm";
        public const string SourceTotalOverQuantity = "total_length_mm/quantity";

        /// <summary>
        /// One bar's length: measured.bar_centreline_length_mm (exact, off the curves) when the
        /// model published it, else measured.total_length_mm / measured.quantity (Revit's rounded
        /// total shared out). Null when neither can be read.
        /// </summary>
        public static double? OneBarLengthMm(JObject row, out string source)
        {
            source = null;
            double? each = Num(row, "measured", "bar_centreline_length_mm");
            if (each.HasValue && each.Value > 0) { source = SourceCentreline; return each; }
            double? total = Num(row, "measured", "total_length_mm");
            double? q = Num(row, "measured", "quantity");
            if (total.HasValue && q.HasValue && q.Value > 0)
            {
                source = SourceTotalOverQuantity;
                return total.Value / q.Value;
            }
            return null;
        }

        /// <summary>
        /// Every property in which `row` differs from `reference`, each with both values, and the
        /// properties that could not be read on either side (`unreadable`). A property that could
        /// not be read is never counted as agreeing.
        /// </summary>
        public static List<JObject> Differences(JObject reference, JObject row, double lengthTolMm,
                                                out List<string> unreadable)
        {
            unreadable = new List<string>();
            var diffs = new List<JObject>();

            long? ta = TypeId(reference), tb = TypeId(row);
            if (!ta.HasValue || !tb.HasValue) unreadable.Add(PropBarType);
            else if (ta.Value != tb.Value)
                diffs.Add(Diff(PropBarType, reference, row, TypeValue(reference), TypeValue(row)));

            long? sa = ShapeId(reference), sb = ShapeId(row);
            if (!sa.HasValue || !sb.HasValue) unreadable.Add(PropShape);
            else if (sa.Value != sb.Value)
                diffs.Add(Diff(PropShape, reference, row, ShapeValue(reference), ShapeValue(row)));

            string srcA, srcB;
            double? la = OneBarLengthMm(reference, out srcA), lb = OneBarLengthMm(row, out srcB);
            if (!la.HasValue || !lb.HasValue) unreadable.Add(PropLength);
            else if (Math.Abs(la.Value - lb.Value) > lengthTolMm + 1e-9)
            {
                JObject d = Diff(PropLength, reference, row, Math.Round(la.Value, 3), Math.Round(lb.Value, 3));
                d["reference_value_source"] = srcA;
                d["value_source"] = srcB;
                d["difference_mm"] = Math.Round(Math.Abs(la.Value - lb.Value), 3);
                diffs.Add(d);
            }
            return diffs;
        }

        /// <summary>One line of prose per difference, both values in it.</summary>
        public static string Describe(JObject d)
        {
            return "set " + d["rebar_id"] + " differs from set " + d["reference_rebar_id"] + " in " +
                   (string)d["property"] + ": " + Show(d["reference_value"]) + " against " + Show(d["value"]);
        }

        public static string Mark(JObject row)
        {
            JToken t = Path(row, "measured", "schedule_mark");
            string m = t == null || t.Type == JTokenType.Null ? null : t.ToString();
            return string.IsNullOrWhiteSpace(m) ? null : m;
        }

        public static long Id(JObject row)
        {
            return row.Value<long?>("id") ?? -1;
        }

        private static JObject Diff(string prop, JObject a, JObject b, JToken va, JToken vb)
        {
            return new JObject
            {
                ["property"] = prop,
                ["reference_rebar_id"] = Id(a),
                ["rebar_id"] = Id(b),
                ["reference_value"] = va,
                ["value"] = vb
            };
        }

        private static string Show(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "(none)";
            var o = t as JObject;
            if (o != null)
                return "'" + ((string)o["name"] ?? "?") + "' (id " + o["id"] + ")";
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                return t.Value<double>().ToString("0.###", CultureInfo.InvariantCulture) + " mm";
            return t.ToString();
        }

        private static long? TypeId(JObject row)
        {
            JToken resolved = Path(row, "bar_type", "resolved");
            if (resolved != null && resolved.Type == JTokenType.Boolean && !(bool)resolved) return null;
            JToken id = Path(row, "bar_type", "id");
            return id != null && id.Type == JTokenType.Integer ? (long?)id.Value<long>() : null;
        }

        private static long? ShapeId(JObject row)
        {
            // -1 is a published value - a bar with no shape - and two shapeless bars agree on it.
            JToken id = Path(row, "shape", "id");
            return id != null && id.Type == JTokenType.Integer ? (long?)id.Value<long>() : null;
        }

        internal static JObject TypeValue(JObject row)
        {
            return new JObject { ["id"] = Path(row, "bar_type", "id"), ["name"] = Path(row, "bar_type", "name") };
        }

        internal static JObject ShapeValue(JObject row)
        {
            return new JObject { ["id"] = Path(row, "shape", "id"), ["name"] = Path(row, "shape", "name") };
        }

        internal static JToken Path(JObject o, params string[] path)
        {
            JToken t = o;
            foreach (string p in path)
            {
                if (t == null || t.Type != JTokenType.Object) return null;
                t = t[p];
            }
            return t == null || t.Type == JTokenType.Null ? null : t;
        }

        internal static double? Num(JObject o, params string[] path)
        {
            JToken t = Path(o, path);
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) return null;
            double v = t.Value<double>();
            return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
        }
    }

    public static class RebarBendingSchedule
    {
        public const string CodeDensityRequired = "density_required";
        public const string CodeToleranceNotUsable = "length_tolerance_not_usable";

        /// <summary>
        /// The schedule over RebarFacts rows. `densityKgPerM3` and `densitySource` are REQUIRED;
        /// `lengthTolMm` is how far one bar's length may differ between sets that share a mark and
        /// still be one line (0 = exact at the 0.001 mm the reader publishes). Null and a code when
        /// something is not usable; nothing is guessed.
        /// </summary>
        public static JObject Build(JArray rows, double? densityKgPerM3, string densitySource, double lengthTolMm,
                                    out string code, out string error)
        {
            code = null; error = null;
            if (!densityKgPerM3.HasValue)
            {
                code = CodeDensityRequired;
                error = "a bending schedule weighs every line, and this bridge carries no density of its own: " +
                        "declare density_kg_per_m3 and density_source (the standard, the supplier or the project " +
                        "document it came from). Nothing was scheduled.";
                return null;
            }
            double density = densityKgPerM3.Value;
            if (double.IsNaN(density) || double.IsInfinity(density) || density <= 0)
            {
                code = RebarTakeoff.CodeDensityNotUsable;
                error = "the density must be a positive finite number of kilograms per cubic metre. Nothing was scheduled.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(densitySource))
            {
                code = RebarTakeoff.CodeDensityWithoutSource;
                error = "a density needs a source - the standard, the supplier, or the project document it came " +
                        "from. A weight nobody can trace is a weight nobody should order from. Nothing was scheduled.";
                return null;
            }
            if (double.IsNaN(lengthTolMm) || double.IsInfinity(lengthTolMm) || lengthTolMm < 0)
            {
                code = CodeToleranceNotUsable;
                error = "length_tolerance_mm must be zero or more. Nothing was scheduled.";
                return null;
            }

            var all = (rows ?? new JArray()).OfType<JObject>().ToList();
            // FABRIC SHEETS are scheduled by sheet type, in their own block: a sheet is not a bent bar with legs.
            var fabric = all.Where(RebarSystemBars.IsFabric).ToList();
            var bars = all.Where(b => !RebarSystemBars.IsFabric(b)).ToList();

            // ------------------------------------------------ lines: mark, then identity
            var marks = new List<string>();
            var byMark = new Dictionary<string, List<Line>>(StringComparer.Ordinal);
            const string NoMark = "\0";
            foreach (JObject b in bars)
            {
                string m = RebarMarkIdentity.Mark(b) ?? NoMark;
                List<Line> lines;
                if (!byMark.TryGetValue(m, out lines)) { lines = new List<Line>(); byMark[m] = lines; marks.Add(m); }
                Line home = null;
                foreach (Line l in lines)
                {
                    List<string> unread;
                    List<JObject> d = RebarMarkIdentity.Differences(l.Sets[0], b, lengthTolMm, out unread);
                    if (d.Count == 0 && unread.Count == 0) { home = l; break; }
                }
                if (home == null)
                {
                    home = new Line();
                    lines.Add(home);
                }
                home.Sets.Add(b);
            }

            var outRows = new JArray();
            var conflicts = new JArray();
            bool everyComplete = true;
            foreach (string m in marks.Where(x => x != NoMark).OrderBy(x => x, StringComparer.Ordinal)
                                       .Concat(marks.Where(x => x == NoMark)))
            {
                List<Line> lines = byMark[m];
                bool conflict = m != NoMark && lines.Count > 1;
                if (conflict)
                {
                    var diffs = new JArray();
                    var unreadAll = new SortedSet<string>(StringComparer.Ordinal);
                    for (int i = 1; i < lines.Count; i++)
                    {
                        List<string> unread;
                        foreach (JObject d in RebarMarkIdentity.Differences(lines[0].Sets[0], lines[i].Sets[0],
                                                                           lengthTolMm, out unread))
                            diffs.Add(d);
                        foreach (string u in unread) unreadAll.Add(u);
                    }
                    var c = new JObject
                    {
                        ["mark"] = m,
                        ["rows"] = lines.Count,
                        ["differences"] = diffs,
                        ["why"] = "one mark covers bars that are not the same bar, so it is split into one line " +
                                  "per bar rather than averaged into one. The audit reports this as bar_mark_conflict."
                    };
                    if (unreadAll.Count > 0) c["unreadable_properties"] = new JArray(unreadAll.ToArray());
                    conflicts.Add(c);
                }
                foreach (Line l in lines)
                {
                    JObject row = LineRow(m == NoMark ? null : m, l, conflict, density, lengthTolMm);
                    if (!(bool)row["complete"]) everyComplete = false;
                    outRows.Add(row);
                }
            }
            if (conflicts.Count > 0) everyComplete = false;
            JObject fabricBlock = FabricSchedule(fabric, density);
            if (!(bool)fabricBlock["totals"]["complete"]) everyComplete = false;
            JObject barTotals = Sum(bars, density);

            var result = new JObject
            {
                ["derived_from_model"] = true,
                ["derived_from_model_means"] =
                    "every number is read off the bars in the model: counts, Revit's own total length and " +
                    "volume, and the centreline as declared. It is what the model carries - the thing to check " +
                    "a drawn schedule against - not a schedule anybody declared.",
                ["rows"] = outRows,
                ["row_count"] = outRows.Count,
                ["mark_conflicts"] = conflicts,
                ["totals_by_diameter"] = TotalsBy(bars, "nominal_diameter_mm",
                    b => RebarMarkIdentity.Path(b, "bar_type", "nominal_diameter_mm"), density),
                ["totals_by_host"] = TotalsBy(bars, "host_id",
                    b => RebarMarkIdentity.Path(b, "host", "id"), density),
                ["totals"] = barTotals,
                ["fabric"] = fabricBlock,
                ["totals_with_fabric"] = WithFabric(barTotals, (JObject)fabricBlock["totals"]),
                ["length_tolerance_mm"] = lengthTolMm,
                ["length_tolerance_means"] =
                    "how far one bar's length may differ between sets that share a mark and still be one line. " +
                    "0 compares exactly, at the 0.001 mm the reader publishes. Bar type and shape are always exact.",
                ["density"] = new JObject
                {
                    ["kg_per_m3"] = density,
                    ["source"] = densitySource,
                    ["means"] = "declared by the caller. This bridge carries no density of its own."
                },
                ["mass_means"] =
                    "kilograms are Revit's measured bar volume times the declared density, summed per line - the " +
                    "same volume mode=quantities reports. They are not recomputed from a nominal mass per metre.",
                ["sets_read"] = all.Count,
                ["complete"] = everyComplete,
                ["not_included"] = NotIncluded()
            };
            return result;
        }

        /// <summary>Named so a reader can see the edges of this schedule rather than assume it has none.</summary>
        public static JArray NotIncluded()
        {
            return new JArray(
                Gap("linked_models", "only the rebar of the active document; bars in linked models are not read."),
                Gap("hook_dimensions", "segments are read off the centreline AS DECLARED - hooks and bend radii " +
                    "suppressed - so a hook is named per end, not dimensioned, and bend allowances are not added."),
                Gap("cut_length", "one_bar_length_mm is the centreline as Revit draws it (or Revit's total over " +
                    "the quantity), not a fabrication cut length under any bending standard."),
                Gap("variable_length_sets", "a set whose bars are not all one length (a tapered or varying set) " +
                    "is scheduled with ONE bar's length, the first bar's; its total length is Revit's own total."));
        }

        /// <summary>
        /// The fabric of the schedule: one line per sheet type - the sheets, their fabric numbers, their wires as
        /// placed, the wires' steel weighed with the declared density, and Revit's own cut-sheet mass beside it.
        /// </summary>
        private static JObject FabricSchedule(List<JObject> sheets, double density)
        {
            var order = new List<string>();
            var byType = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
            foreach (JObject f in sheets)
            {
                JToken id = RebarMarkIdentity.Path(f, "bar_type", "id");
                string k = id == null ? "\0" : id.ToString(Newtonsoft.Json.Formatting.None);
                if (!byType.ContainsKey(k)) { byType[k] = new List<JObject>(); order.Add(k); }
                byType[k].Add(f);
            }
            var lines = new JArray();
            foreach (string k in order)
            {
                List<JObject> l = byType[k];
                JObject sum = FabricSum(l, density);
                var line = new JObject
                {
                    ["sheet_type"] = RebarMarkIdentity.TypeValue(l[0]),
                    ["fabric_numbers"] = new JArray(l.Select(RebarMarkIdentity.Mark).Where(m => m != null).Distinct()
                                                     .OrderBy(m => m, StringComparer.Ordinal).ToArray()),
                    ["sheet_ids"] = new JArray(l.Select(x => (object)RebarMarkIdentity.Id(x)).ToArray()),
                    ["host_ids"] = new JArray(l.Select(x => RebarMarkIdentity.Path(x, "host", "id")).Where(t => t != null)
                                               .Select(t => t.Value<long>()).Distinct().OrderBy(v => v).Select(v => (object)v).ToArray()),
                    ["fabric_area_ids"] = new JArray(l.Select(x => RebarMarkIdentity.Path(x, "system", "id")).Where(t => t != null)
                                                      .Select(t => t.Value<long>()).Distinct().OrderBy(v => v).Select(v => (object)v).ToArray())
                };
                foreach (JProperty p in sum.Properties()) line[p.Name] = p.Value;
                lines.Add(line);
            }
            return new JObject
            {
                ["rows"] = lines,
                ["totals"] = FabricSum(sheets, density),
                ["means"] =
                    "fabric sheets (welded wire mesh), one line per sheet type. wires are the wires Revit returns for each " +
                    "sheet as placed; total_kg is their steel (length x pi x d^2 / 4 per wire type) times the declared " +
                    "density, as every bar is weighed. revit_cut_sheet_mass_kg is Revit's own mass for the same sheets, " +
                    "published to check against and never summed in."
            };
        }

        private static JObject FabricSum(IEnumerable<JObject> sheets, double density)
        {
            int n = 0, wires = 0, wUnread = 0, lUnread = 0, vUnread = 0, rUnread = 0;
            double lengthM = 0, volume = 0, revit = 0;
            foreach (JObject f in sheets)
            {
                n++;
                double? q = RebarMarkIdentity.Num(f, "measured", "quantity");
                if (q.HasValue) wires += (int)q.Value; else wUnread++;
                double? l = RebarMarkIdentity.Num(f, "measured", "total_length_m");
                if (l.HasValue) lengthM += l.Value; else lUnread++;
                double? v = RebarMarkIdentity.Num(f, "measured", "volume_m3");
                if (v.HasValue) volume += v.Value; else vUnread++;
                double? r = RebarMarkIdentity.Num(f, "fabric", "revit_cut_sheet_mass_kg");
                if (r.HasValue) revit += r.Value; else rUnread++;
            }
            int unread = wUnread + lUnread + vUnread;
            var o = new JObject
            {
                ["sheets"] = n,
                ["wires"] = wUnread > 0 ? (JToken)JValue.CreateNull() : wires,
                ["wires_counted"] = wires,
                ["total_wire_length_m"] = lUnread > 0 ? (JToken)JValue.CreateNull() : Math.Round(lengthM, 4),
                ["total_wire_length_m_counted"] = Math.Round(lengthM, 4),
                ["volume_m3"] = vUnread > 0 ? (JToken)JValue.CreateNull() : Math.Round(volume, 6),
                ["total_kg"] = vUnread > 0 ? (JToken)JValue.CreateNull() : Math.Round(volume * density, 3),
                ["total_kg_counted"] = Math.Round(volume * density, 3),
                ["revit_cut_sheet_mass_kg"] = rUnread > 0 ? (JToken)JValue.CreateNull() : Math.Round(revit, 3),
                ["complete"] = unread == 0
            };
            if (unread > 0)
                o["why_partial"] = unread + " value(s) of these sheets could not be read, so the totals they feed are null " +
                                   "rather than a sum with a hole in it; the _counted numbers are what could be read.";
            return o;
        }

        private static JObject WithFabric(JObject bars, JObject fabric)
        {
            JToken bk = bars["total_kg"], fk = fabric["total_kg"];
            bool complete = (bool)bars["complete"] && (bool)fabric["complete"];
            return new JObject
            {
                ["total_kg"] = bk == null || bk.Type == JTokenType.Null || fk == null || fk.Type == JTokenType.Null
                    ? (JToken)JValue.CreateNull() : Math.Round(bk.Value<double>() + fk.Value<double>(), 3),
                ["total_kg_counted"] = Math.Round(bars.Value<double>("total_kg_counted") + fabric.Value<double>("total_kg_counted"), 3),
                ["complete"] = complete,
                ["means"] = "the bars' kilograms (totals) plus the fabric's (fabric.totals), both weighed with the declared density."
            };
        }

        private static JObject Gap(string what, string why)
        {
            return new JObject { ["what"] = what, ["why"] = why };
        }

        private static JObject LineRow(string mark, Line l, bool conflict, double density, double tolMm)
        {
            JObject first = l.Sets[0];
            string lengthSource;
            double? each = RebarMarkIdentity.OneBarLengthMm(first, out lengthSource);

            List<double> segments = Segments(first);
            bool segmentsConsistent = true;
            foreach (JObject other in l.Sets.Skip(1))
            {
                List<double> s = Segments(other);
                if (segments == null || s == null || s.Count != segments.Count ||
                    s.Where((v, i) => Math.Abs(v - segments[i]) > Math.Max(tolMm, 0.001)).Any())
                    segmentsConsistent = false;
            }

            JObject sum = Sum(l.Sets, density);
            var row = new JObject
            {
                ["mark"] = mark == null ? JValue.CreateNull() : (JToken)mark,
                ["mark_missing"] = mark == null,
                ["bar_type"] = RebarMarkIdentity.TypeValue(first),
                ["nominal_diameter_mm"] = RebarMarkIdentity.Path(first, "bar_type", "nominal_diameter_mm"),
                ["shape"] = RebarMarkIdentity.ShapeValue(first),
                ["segments_mm"] = segments == null ? (JToken)JValue.CreateNull()
                                                   : new JArray(segments.Select(v => (object)Math.Round(v, 3)).ToArray()),
                ["segments_source"] = "geometry.centreline_points_as_declared_mm",
                ["segments_consistent"] = segments != null && segmentsConsistent,
                ["hooks"] = new JObject { ["start"] = Hook(first, 0), ["end"] = Hook(first, 1) },
                ["bars"] = sum["bars"],
                ["bars_counted"] = sum["bars_counted"],
                ["one_bar_length_mm"] = each.HasValue ? (JToken)Math.Round(each.Value, 3) : JValue.CreateNull(),
                ["one_bar_length_source"] = lengthSource,
                ["total_length_m"] = sum["total_length_m"],
                ["total_length_m_counted"] = sum["total_length_m_counted"],
                ["total_kg"] = sum["total_kg"],
                ["total_kg_counted"] = sum["total_kg_counted"],
                ["sets"] = l.Sets.Count,
                ["rebar_ids"] = new JArray(l.Sets.Select(s => (object)RebarMarkIdentity.Id(s)).ToArray()),
                ["host_ids"] = new JArray(l.Sets.Select(s => RebarMarkIdentity.Path(s, "host", "id"))
                                                .Where(t => t != null).Select(t => t.Value<long>()).Distinct()
                                                .OrderBy(v => v).Select(v => (object)v).ToArray()),
                ["mark_conflict"] = conflict,
                // THE BARS OF AREA AND PATH SYSTEMS ARE SCHEDULED (RebarInSystem rows): a line names the
                // systems its bars came from, empty when every set on it is a rebar set.
                ["systems"] = Systems(l.Sets)
            };
            if (segments == null)
                row["segments_why"] = "the bar did not publish its centreline as declared, so its legs were not " +
                                      "measured. The line is kept; its shape dimensions are unknown.";
            else if (!segmentsConsistent)
                row["segments_why"] = "the sets on this line share type, shape and length but not the same legs; " +
                                      "the segments shown are the first set's.";
            bool complete = (bool)sum["complete"] && each.HasValue && segments != null && segmentsConsistent;
            row["complete"] = complete;
            if (sum["why_partial"] != null) row["why_partial"] = sum["why_partial"];
            return row;
        }

        private static JArray Systems(IEnumerable<JObject> sets)
        {
            var a = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject s in sets)
            {
                JObject sys = RebarSystemBars.SystemOf(s);
                if (sys == null) continue;
                string k = sys.ToString(Newtonsoft.Json.Formatting.None);
                if (!seen.Add(k)) continue;
                a.Add(new JObject { ["kind"] = sys["kind"], ["id"] = sys["id"] });
            }
            return a;
        }

        /// <summary>
        /// The legs of the bar, in mm: distances between the declared centreline's vertices, with
        /// consecutive collinear pieces merged (the tessellation of a straight leg splits it).
        /// </summary>
        public static List<double> Segments(JObject row)
        {
            var pts = RebarMarkIdentity.Path(row, "geometry", "centreline_points_as_declared_mm") as JArray;
            if (pts == null || pts.Count < 2) return null;
            var p = new List<double[]>();
            foreach (JToken t in pts)
            {
                var a = t as JArray;
                if (a == null || a.Count != 3) return null;
                try { p.Add(new[] { a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>() }); }
                catch { return null; }
            }
            var segs = new List<double>();
            double[] dir = null;
            for (int i = 1; i < p.Count; i++)
            {
                double[] d = { p[i][0] - p[i - 1][0], p[i][1] - p[i - 1][1], p[i][2] - p[i - 1][2] };
                double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                if (len < 1e-6) continue;
                double[] u = { d[0] / len, d[1] / len, d[2] / len };
                if (dir != null && u[0] * dir[0] + u[1] * dir[1] + u[2] * dir[2] > 1 - 1e-9)
                    segs[segs.Count - 1] += len;
                else segs.Add(len);
                dir = u;
            }
            return segs.Count == 0 ? null : segs;
        }

        private static JToken Hook(JObject row, int end)
        {
            var terms = row["terminations"] as JArray;
            if (terms == null) return JValue.CreateNull();
            foreach (JObject t in terms.OfType<JObject>())
                if (t.Value<int?>("end") == end)
                {
                    JToken n = t["hook_type_name"];
                    return n == null ? JValue.CreateNull() : n.DeepClone();
                }
            return JValue.CreateNull();
        }

        private static JArray TotalsBy(List<JObject> bars, string keyName, Func<JObject, JToken> key, double density)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
            var values = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (JObject b in bars)
            {
                JToken v = key(b);
                string k = v == null ? "\0" : v.ToString(Newtonsoft.Json.Formatting.None);
                if (!groups.ContainsKey(k)) { groups[k] = new List<JObject>(); values[k] = v; order.Add(k); }
                groups[k].Add(b);
            }
            var a = new JArray();
            foreach (string k in order)
            {
                JObject s = Sum(groups[k], density);
                var o = new JObject { [keyName] = values[k] == null ? JValue.CreateNull() : values[k].DeepClone() };
                foreach (JProperty p in s.Properties()) o[p.Name] = p.Value;
                a.Add(o);
            }
            return a;
        }

        private static JObject Sum(IEnumerable<JObject> sets, double density)
        {
            int n = 0, bars = 0, qUnread = 0, lUnread = 0, vUnread = 0;
            double lengthM = 0, volume = 0;
            foreach (JObject s in sets)
            {
                n++;
                double? q = RebarMarkIdentity.Num(s, "measured", "quantity");
                if (q.HasValue) bars += (int)q.Value; else qUnread++;
                double? l = RebarMarkIdentity.Num(s, "measured", "total_length_m");
                if (l.HasValue) lengthM += l.Value; else lUnread++;
                double? v = RebarMarkIdentity.Num(s, "measured", "volume_m3");
                if (v.HasValue) volume += v.Value; else vUnread++;
            }
            int unread = qUnread + lUnread + vUnread;
            var o = new JObject
            {
                ["sets"] = n,
                ["bars"] = qUnread > 0 ? (JToken)JValue.CreateNull() : bars,
                ["bars_counted"] = bars,
                ["total_length_m"] = lUnread > 0 ? (JToken)JValue.CreateNull() : Math.Round(lengthM, 4),
                ["total_length_m_counted"] = Math.Round(lengthM, 4),
                ["total_kg"] = vUnread > 0 ? (JToken)JValue.CreateNull() : Math.Round(volume * density, 3),
                ["total_kg_counted"] = Math.Round(volume * density, 3),
                ["complete"] = unread == 0
            };
            if (unread > 0)
            {
                o["unreadable"] = new JObject { ["quantity"] = qUnread, ["length"] = lUnread, ["volume"] = vUnread };
                o["why_partial"] = unread + " value(s) could not be read, so the totals they feed are null rather " +
                                   "than a sum with a hole in it; the _counted numbers are what could be read.";
            }
            return o;
        }

        private sealed class Line
        {
            public readonly List<JObject> Sets = new List<JObject>();
        }
    }
}

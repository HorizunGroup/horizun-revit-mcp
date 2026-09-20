// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// A DUCT'S SECTION, RUN BY RUN, FROM THE DRAWING'S OWN LABELS.
//
// A plan draws a supply main as a line and writes its size beside it: "16X8",
// "12X8", "6X6". A line carries no width; the label does. MEASURED on a real
// corridor supply plan (campaign 6): 12 main polylines, 26 texts on the duct text
// layer - sizes, airflows "(630 CFM)", damper tags "FSD" / "FD" - and runs whose
// size changes along the corridor at short transition pieces.
//
// WHAT IT REFUSES TO GUESS.
//
//   A NUMBER IS NOT A SIZE. Only "W x H" (or "H x W", as the rule declares) is a
//   section; airflow, damper tags and anything else are reported as not a size,
//   by name, and never read as one.
//
//   UNITS ARE DECLARED, never deduced: "16X8" is inches on a US plan and
//   millimetres on another; the rule says which.
//
//   NEAREST IS NOT ENOUGH. A leader that ends at a label and points at a run is
//   the drawing saying which run; without one, a label is associated with a run
//   only when it lies beside the run's interior, and a label about as close to
//   two different runs is AMBIGUOUS and kept as such. A label written parallel to
//   one run and across another prefers the parallel one - that is how a size is
//   written along a duct - and the preference is recorded.
//
//   TWO SIZES ON ONE RUN is a contradiction: the run has no size.
//
//   PROPAGATION STOPS. A size travels from a labelled run to an unlabelled one
//   only through a node where exactly two runs meet (an elbow or a straight
//   continuation). It stops at a tee or cross (a branch's size is its own), at a
//   run that has its own label (a different size there is a transition), and
//   when two different sizes would arrive at one run.
//
// Every run gets a row saying which of those happened, with the entities, the
// text, the value as written, the units, the interpretation and the reason.
//
// Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The rule's declaration of where a duct's section is written.</summary>
    public sealed class CadSectionRule
    {
        public List<string> LabelLayers = new List<string>();
        /// <summary>"inch" or "mm": what the numbers in the label are.</summary>
        public string LabelUnits;
        /// <summary>"width_x_height" or "height_x_width": which number is the horizontal side.</summary>
        public string LabelOrder;
        public double MaxDistanceMm;
        public double LeaderToleranceMm;
        public double AmbiguityRatio = 1.5;
        public double ParallelToleranceDegrees = 10.0;
        public bool PropagateThroughElbows = true;
        /// <summary>
        /// Cut a run whose labels name more than one size into pieces, at the only drawn event that
        /// separates them (a branch tapping its interior) - or, with none, keep the stretch between the
        /// labels as one piece whose change is not located. Declared, never assumed.
        /// </summary>
        public bool SplitAtSectionChanges;
        /// <summary>How close a branch's end must be to a run's interior to count as tapping it (mm).</summary>
        public double? TapToleranceMm;
        /// <summary>
        /// A label that no run passes beside may name the run whose FREE end it lies beyond, within this many
        /// mm of that end - declared, never assumed. MEASURED (M106): three branch stubs whose "4X4" is written
        /// past the stub's open end, 436-637 mm away, beside the damper or grille that ends the branch.
        /// </summary>
        public double? FreeEndMaxDistanceMm;

        public double MmPerUnit => LabelUnits == "inch" ? 25.4 : 1.0;

        public JObject ToJson() => new JObject
        {
            ["from"] = "labels",
            ["label_layers"] = new JArray(LabelLayers),
            ["split_at_section_changes"] = SplitAtSectionChanges,
            ["tap_tolerance_mm"] = TapToleranceMm.HasValue ? (JToken)TapToleranceMm.Value : JValue.CreateNull(),
            ["free_end_max_distance_mm"] = FreeEndMaxDistanceMm.HasValue ? (JToken)FreeEndMaxDistanceMm.Value : JValue.CreateNull(),
            ["label_units"] = LabelUnits,
            ["label_order"] = LabelOrder,
            ["max_distance_mm"] = MaxDistanceMm,
            ["leader_tolerance_mm"] = LeaderToleranceMm,
            ["ambiguity_ratio"] = AmbiguityRatio,
            ["parallel_tolerance_degrees"] = ParallelToleranceDegrees,
            ["propagate_through_elbows"] = PropagateThroughElbows
        };
    }

    /// <summary>A text in model millimetres.</summary>
    public sealed class CadLabel
    {
        public string Id;
        public string Text;
        public string Layer;
        public CadPoint At;
        public double? RotationRadians;
    }

    /// <summary>A leader in model millimetres; Points[0] is the arrowhead.</summary>
    public sealed class CadLeaderLine
    {
        public string Id;
        public string Layer;
        public List<CadPoint> Points = new List<CadPoint>();
    }

    /// <summary>One run's section decision.</summary>
    public sealed class CadRunSection
    {
        public string RunId;
        public string SemanticId;
        public CadPoint Start, End;
        public List<string> SourceEntities = new List<string>();
        /// <summary>documented | propagated | contradictory | ambiguous | missing</summary>
        public string State = "missing";
        public double? WidthMm, HeightMm;
        public List<JObject> Labels = new List<JObject>();
        public string PropagatedFrom;
        public string Reason;
        /// <summary>A piece: its parent run and its anchor key (see CadCandidate.PieceKey).</summary>
        public string ParentRunId, PieceKey;
        /// <summary>Where along the parent the piece lies (0..1), and why it was cut there.</summary>
        public double? ParentFrom, ParentTo;
        public string CutReason;
        /// <summary>A transition piece with a settled size at both ends: the two neighbours and their sizes.</summary>
        public JObject TransitionEnds;

        public JObject ToJson() => new JObject
        {
            ["run"] = RunId,
            ["semantic_id"] = SemanticId,
            ["from_mm"] = new JArray(Math.Round(Start.X, 1), Math.Round(Start.Y, 1)),
            ["to_mm"] = new JArray(Math.Round(End.X, 1), Math.Round(End.Y, 1)),
            ["source_entities"] = new JArray(SourceEntities),
            ["state"] = State,
            ["width_mm"] = WidthMm.HasValue ? (JToken)Math.Round(WidthMm.Value, 3) : JValue.CreateNull(),
            ["height_mm"] = HeightMm.HasValue ? (JToken)Math.Round(HeightMm.Value, 3) : JValue.CreateNull(),
            ["orientation"] = WidthMm.HasValue ? "width horizontal (the label's plan-side number), height vertical" : null,
            ["labels"] = new JArray(Labels),
            ["propagated_from"] = PropagatedFrom,
            ["reason"] = Reason,
            ["piece_of"] = ParentRunId,
            ["piece_key"] = PieceKey,
            ["piece_along_parent"] = ParentFrom.HasValue ? new JArray(Math.Round(ParentFrom.Value, 4), Math.Round(ParentTo.Value, 4)) : null,
            ["cut_reason"] = CutReason,
            ["transition_ends"] = TransitionEnds
        };
    }

    public sealed class CadSectionReading
    {
        public List<CadRunSection> Runs = new List<CadRunSection>();
        public List<JObject> SizeLabels = new List<JObject>();
        public List<JObject> OtherTexts = new List<JObject>();

        public JObject ToJson()
        {
            var byState = Runs.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
            var o = new JObject();
            foreach (var kv in byState.OrderBy(k => k.Key, StringComparer.Ordinal)) o[kv.Key] = kv.Value;
            return new JObject
            {
                ["runs"] = Runs.Count,
                ["by_state"] = o,
                ["size_labels"] = SizeLabels.Count,
                ["labels_used"] = SizeLabels.Count(l => l.Value<string>("outcome") == "associated"),
                ["labels_not_used"] = new JArray(SizeLabels.Where(l => l.Value<string>("outcome") != "associated")),
                ["other_texts"] = new JArray(OtherTexts),
                ["rows"] = new JArray(Runs.Select(r => (JToken)r.ToJson())),
                ["means"] = "documented: a label of the drawing names this run's size. propagated: an unlabelled run " +
                            "took the size of its only neighbour through an elbow or a straight continuation. " +
                            "transition: an unlabelled run between two different (or unsettled) sizes - a transition " +
                            "piece, not a straight duct of either size. contradictory / ambiguous / missing / transition: " +
                            "no size - the run is NOT planned and is listed for a person, with the reason."
            };
        }
    }

    public static class CadDuctSections
    {
        /// <summary>
        /// The rule's "section" object, validated: every way it could be read two ways is a
        /// refusal of the whole set.
        /// </summary>
        public static CadSectionRule Parse(string ruleId, JObject s, string produces, double? diameterMm)
        {
            Func<string, CadRequirementSetException> bad = m => new CadRequirementSetException("rule '" + ruleId + "': section " + m);
            if (s == null) throw bad("must be an object.");
            if (produces != "duct") throw bad("only means something for a rule that produces ducts.");
            if (diameterMm.HasValue) throw bad("and diameter_mm are two sizes for one run; declare one.");
            var known = new HashSet<string>(StringComparer.Ordinal)
            { "from", "label_layers", "label_units", "label_order", "max_distance_mm", "leader_tolerance_mm",
              "ambiguity_ratio", "parallel_tolerance_degrees", "propagate_through_elbows",
              "split_at_section_changes", "tap_tolerance_mm", "free_end_max_distance_mm" };
            foreach (JProperty p in s.Properties())
                if (!known.Contains(p.Name)) throw bad("has no key '" + p.Name + "'.");
            if (s.Value<string>("from") != "labels") throw bad("from must be \"labels\".");
            var rule = new CadSectionRule();
            var layers = s["label_layers"] as JArray;
            if (layers == null || layers.Count == 0) throw bad("label_layers must list the layers the sizes are written on.");
            foreach (JToken t in layers) if (!string.IsNullOrWhiteSpace((string)t)) rule.LabelLayers.Add(((string)t).Trim());
            rule.LabelUnits = s.Value<string>("label_units");
            if (rule.LabelUnits != "inch" && rule.LabelUnits != "mm")
                throw bad("label_units must be \"inch\" or \"mm\" - the drawing's numbers are not guessed.");
            rule.LabelOrder = s.Value<string>("label_order");
            if (rule.LabelOrder != "width_x_height" && rule.LabelOrder != "height_x_width")
                throw bad("label_order must be \"width_x_height\" or \"height_x_width\".");
            double? maxd = s.Value<double?>("max_distance_mm");
            if (!maxd.HasValue || maxd.Value <= 0) throw bad("max_distance_mm must be positive.");
            rule.MaxDistanceMm = maxd.Value;
            rule.LeaderToleranceMm = s.Value<double?>("leader_tolerance_mm") ?? maxd.Value / 2;
            if (rule.LeaderToleranceMm <= 0) throw bad("leader_tolerance_mm must be positive.");
            rule.AmbiguityRatio = s.Value<double?>("ambiguity_ratio") ?? 1.5;
            if (rule.AmbiguityRatio < 1) throw bad("ambiguity_ratio must be at least 1.");
            rule.ParallelToleranceDegrees = s.Value<double?>("parallel_tolerance_degrees") ?? 10.0;
            rule.PropagateThroughElbows = s.Value<bool?>("propagate_through_elbows") ?? true;
            rule.SplitAtSectionChanges = s.Value<bool?>("split_at_section_changes") ?? false;
            rule.TapToleranceMm = s.Value<double?>("tap_tolerance_mm");
            if (rule.TapToleranceMm.HasValue && rule.TapToleranceMm.Value <= 0) throw bad("tap_tolerance_mm must be positive.");
            rule.FreeEndMaxDistanceMm = s.Value<double?>("free_end_max_distance_mm");
            if (rule.FreeEndMaxDistanceMm.HasValue && (rule.FreeEndMaxDistanceMm.Value <= 0 || rule.FreeEndMaxDistanceMm.Value > 5000))
                throw bad("free_end_max_distance_mm must be positive and at most 5000 - a label farther than that from a run's end names something else.");
            return rule;
        }

        private static readonly Regex Size = new Regex(
            @"^\s*(\d+(?:\.\d+)?)\s*(?:""|in|mm)?\s*[xX×]\s*(\d+(?:\.\d+)?)\s*(?:""|in|mm)?\s*$", RegexOptions.CultureInvariant);

        /// <summary>MTEXT formatting codes and braces removed; nothing else changed.</summary>
        public static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = Regex.Replace(text, @"\\[A-Za-z][^;\\]*;", "");
            s = s.Replace("\\P", " ").Replace("{", "").Replace("}", "");
            return s.Trim();
        }

        /// <summary>(first, second) numbers of a "W x H" label, or null.</summary>
        public static Tuple<double, double> ParseSize(string text)
        {
            Match m = Size.Match(Plain(text));
            if (!m.Success) return null;
            return Tuple.Create(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        /// <summary>Why a text is not a size - said by name, not guessed at.</summary>
        public static string NotASize(string text)
        {
            string p = Plain(text).ToUpperInvariant();
            if (p.Contains("CFM") || p.Contains("L/S") || p.Contains("M3/H")) return "airflow";
            if (Regex.IsMatch(p, @"^\(?[A-Z]{1,4}\)?$")) return "tag";
            return "not_a_section";
        }

        private static double Dist(CadPoint a, CadPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>Distance from p to segment ab, and the parameter of the foot (0..1 inside).</summary>
        private static double ToSegment(CadPoint p, CadPoint a, CadPoint b, out double t)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            t = l2 <= 0 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2;
            double tc = Math.Max(0, Math.Min(1, t));
            var q = new CadPoint(a.X + tc * dx, a.Y + tc * dy);
            return Dist(p, q);
        }

        private static bool Parallel(double? textRotation, CadPoint a, CadPoint b, double tolDeg)
        {
            if (!textRotation.HasValue) return false;
            double run = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
            double txt = textRotation.Value * 180 / Math.PI;
            double d = Math.Abs(((run - txt) % 180 + 180) % 180);
            return Math.Min(d, 180 - d) <= tolDeg;
        }

        /// <summary>
        /// Decide each run's section from the labels and leaders. <paramref name="runs"/> are
        /// (id, semantic id, start, end, source entities); coordinates in model millimetres.
        /// </summary>
        public static CadSectionReading Assign(IList<CadRunSection> runs, IList<CadLabel> labels,
                                               IList<CadLeaderLine> leaders, CadSectionRule rule, double pointToleranceMm)
        {
            var reading = new CadSectionReading();
            reading.Runs.AddRange(runs);
            var byRun = runs.ToDictionary(r => r.RunId, StringComparer.Ordinal);
            var unreached = new List<Tuple<CadLabel, JObject>>();

            foreach (CadLabel label in labels.OrderBy(l => l.Id, StringComparer.Ordinal))
            {
                Tuple<double, double> size = ParseSize(label.Text);
                var row = new JObject
                {
                    ["label"] = label.Id, ["text"] = label.Text, ["layer"] = label.Layer,
                    ["at_mm"] = new JArray(Math.Round(label.At.X, 1), Math.Round(label.At.Y, 1))
                };
                if (size == null)
                {
                    row["not_a_size"] = NotASize(label.Text);
                    reading.OtherTexts.Add(row);
                    continue;
                }
                double first = size.Item1 * rule.MmPerUnit, second = size.Item2 * rule.MmPerUnit;
                double width = rule.LabelOrder == "height_x_width" ? second : first;
                double height = rule.LabelOrder == "height_x_width" ? first : second;
                row["value_as_written"] = Plain(label.Text);
                row["units"] = rule.LabelUnits;
                row["width_mm"] = Math.Round(width, 3);
                row["height_mm"] = Math.Round(height, 3);
                reading.SizeLabels.Add(row);

                // 1. A LEADER that ends at this label and points at a run.
                CadRunSection byLeader = null;
                double leaderGap = double.MaxValue;
                string leaderId = null;
                foreach (CadLeaderLine ld in leaders)
                {
                    if (ld.Points.Count < 2) continue;
                    double tail = Dist(ld.Points[ld.Points.Count - 1], label.At);
                    if (tail > rule.LeaderToleranceMm) continue;
                    foreach (CadRunSection r in runs)
                    {
                        double t;
                        double d = ToSegment(ld.Points[0], r.Start, r.End, out t);
                        if (d <= Math.Max(pointToleranceMm, rule.LeaderToleranceMm / 4) && d < leaderGap)
                        { leaderGap = d; byLeader = r; leaderId = ld.Id; }
                    }
                }
                if (byLeader != null)
                {
                    row["outcome"] = "associated";
                    row["run"] = byLeader.RunId;
                    row["by"] = "leader " + leaderId + " (arrowhead " + Math.Round(leaderGap, 1) + " mm from the run)";
                    double tl;
                    ToSegment(leaders.First(x => x.Id == leaderId).Points[0], byLeader.Start, byLeader.End, out tl);
                    row["along"] = Math.Round(tl, 6);
                    byLeader.Labels.Add(row);
                    continue;
                }

                // 2. BESIDE A RUN'S INTERIOR, parallel preferred, ambiguity kept.
                var near = new List<Tuple<CadRunSection, double, bool>>();
                foreach (CadRunSection r in runs)
                {
                    double t;
                    double d = ToSegment(label.At, r.Start, r.End, out t);
                    if (t < -0.02 || t > 1.02 || d > rule.MaxDistanceMm) continue;
                    near.Add(Tuple.Create(r, d, Parallel(label.RotationRadians, r.Start, r.End, rule.ParallelToleranceDegrees)));
                }
                if (near.Count == 0)
                {
                    row["outcome"] = "no_run_within_reach";
                    row["means"] = "no run passes beside this label within max_distance_mm; it names nothing this reading can see.";
                    unreached.Add(Tuple.Create(label, row));
                    continue;
                }
                var pool = near.Any(x => x.Item3) ? near.Where(x => x.Item3).ToList() : near;
                pool = pool.OrderBy(x => x.Item2).ToList();
                var best = pool[0];
                var second2 = pool.Skip(1).FirstOrDefault(x => x.Item1 != best.Item1);
                if (second2 != null && second2.Item2 <= best.Item2 * rule.AmbiguityRatio + 1e-6)
                {
                    row["outcome"] = "ambiguous";
                    row["candidates"] = new JArray(pool.Take(3).Select(x => (JToken)new JObject
                    {
                        ["run"] = x.Item1.RunId, ["distance_mm"] = Math.Round(x.Item2, 1), ["parallel"] = x.Item3
                    }));
                    foreach (var x in pool.Where(x => x.Item2 <= best.Item2 * rule.AmbiguityRatio + 1e-6))
                        x.Item1.Labels.Add(row);
                    continue;
                }
                row["outcome"] = "associated";
                row["run"] = best.Item1.RunId;
                double tb;
                ToSegment(label.At, best.Item1.Start, best.Item1.End, out tb);
                row["along"] = Math.Round(tb, 6);
                row["by"] = "beside the run's interior at " + Math.Round(best.Item2, 1) + " mm" +
                            (best.Item3 ? ", written parallel to it" : "") +
                            (second2 == null ? "; no other run within reach"
                                             : "; next run " + second2.Item1.RunId + " at " + Math.Round(second2.Item2, 1) + " mm");
                best.Item1.Labels.Add(row);
            }

            // 3. BEYOND A FREE END, only when the rule declares how far - and only for a label nothing else
            //    claimed. The run whose open end is nearest names it; a run that already carries its own label
            //    does not take a second one from past its end, and two ends about as near is ambiguity.
            if (rule.FreeEndMaxDistanceMm.HasValue)
                foreach (var u in unreached)
                    FreeEnd(u.Item1, u.Item2, runs, rule, pointToleranceMm);

            // Each run: one size, contradiction, ambiguity or nothing.
            foreach (CadRunSection r in runs)
            {
                var assoc = r.Labels.Where(l => l.Value<string>("outcome") == "associated" && l.Value<string>("run") == r.RunId).ToList();
                var sizes = assoc.Select(l => Tuple.Create(l.Value<double>("width_mm"), l.Value<double>("height_mm"))).Distinct().ToList();
                if (sizes.Count == 1)
                {
                    r.State = "documented"; r.WidthMm = sizes[0].Item1; r.HeightMm = sizes[0].Item2;
                    r.Reason = assoc.Count + " label(s) of the drawing name this size" +
                               (assoc.Any(l => l.Value<bool?>("beyond_free_end") == true)
                                   ? ", written beyond its free end (free_end_max_distance_mm, declared)" : "");
                }
                else if (sizes.Count > 1)
                {
                    r.State = "contradictory";
                    r.Reason = "labels name " + string.Join(" and ", sizes.Select(s => s.Item1 + "x" + s.Item2 + " mm")) +
                               " for this one run; it gets no size";
                }
                else if (r.Labels.Any(l => l.Value<string>("outcome") == "ambiguous"))
                {
                    r.State = "ambiguous";
                    r.Reason = "a size label lies about as close to another run; which one it names is not said";
                }
            }

            if (rule.SplitAtSectionChanges) Segment(reading, rule, pointToleranceMm);
            if (rule.PropagateThroughElbows) Propagate(reading.Runs, pointToleranceMm);
            foreach (CadRunSection r in reading.Runs.Where(x => x.State == "missing" && x.Reason == null))
                r.Reason = "no label names this run and none could reach it through an elbow or a straight continuation";
            return reading;
        }

        /// <summary>
        /// The declared free-end reading of ONE label no run passes beside. A free end is an end no other run
        /// meets - not at its own ends and not through its interior (a tap) - and the label must lie past it,
        /// on the outward side. The row says which run, how far, and what was next.
        /// </summary>
        private static void FreeEnd(CadLabel label, JObject row, IList<CadRunSection> runs, CadSectionRule rule, double tol)
        {
            double reach = rule.FreeEndMaxDistanceMm.Value;
            var ends = new List<Tuple<CadRunSection, double, bool>>();
            foreach (CadRunSection r in runs)
                foreach (bool atStart in new[] { true, false })
                {
                    CadPoint end = atStart ? r.Start : r.End, other = atStart ? r.End : r.Start;
                    double len = Dist(end, other);
                    if (len <= 0) continue;
                    bool met = false;
                    foreach (CadRunSection o in runs)
                    {
                        if (ReferenceEquals(o, r)) continue;
                        double t;
                        if (ToSegment(end, o.Start, o.End, out t) <= tol) { met = true; break; }
                    }
                    if (met) continue;
                    double along = ((label.At.X - end.X) * (end.X - other.X) + (label.At.Y - end.Y) * (end.Y - other.Y)) / len;
                    double d = Dist(label.At, end);
                    if (along <= 0 || d > reach) continue;
                    ends.Add(Tuple.Create(r, d, atStart));
                }
            if (ends.Count == 0)
            {
                row["means"] = "no run passes beside this label within max_distance_mm, and no run's free end lies within " +
                               "free_end_max_distance_mm (" + reach.ToString("0.#", CultureInfo.InvariantCulture) +
                               " mm) of it on its outward side; it names nothing this reading can see.";
                return;
            }
            ends = ends.OrderBy(x => x.Item2).ToList();
            var best = ends[0];
            var next = ends.Skip(1).FirstOrDefault(x => x.Item1 != best.Item1);
            if (next != null && next.Item2 <= best.Item2 * rule.AmbiguityRatio + 1e-6)
            {
                row["outcome"] = "ambiguous";
                row["beyond_free_end"] = true;
                row["candidates"] = new JArray(ends.Take(3).Select(x => (JToken)new JObject
                {
                    ["run"] = x.Item1.RunId, ["distance_from_free_end_mm"] = Math.Round(x.Item2, 1)
                }));
                row["means"] = "two runs end about as near this label; which one it names is not said";
                foreach (var x in ends.Where(x => x.Item2 <= best.Item2 * rule.AmbiguityRatio + 1e-6))
                    if (!x.Item1.Labels.Contains(row)) x.Item1.Labels.Add(row);
                return;
            }
            if (best.Item1.Labels.Any(l => l.Value<string>("outcome") == "associated" || l.Value<string>("outcome") == "ambiguous"))
            {
                row["outcome"] = "free_end_of_a_labelled_run";
                row["run"] = best.Item1.RunId;
                row["means"] = "the nearest free end belongs to a run that already carries its own label; a second size " +
                               "from past its end is not taken";
                return;
            }
            row["outcome"] = "associated";
            row["run"] = best.Item1.RunId;
            row["along"] = best.Item3 ? 0.0 : 1.0;
            row["beyond_free_end"] = true;
            row["by"] = "beyond the run's free end at " + Math.Round(best.Item2, 1) + " mm (free_end_max_distance_mm " +
                        reach.ToString("0.#", CultureInfo.InvariantCulture) + ", declared)" +
                        (next == null ? "; no other free end within reach"
                                      : "; next free end " + next.Item1.RunId + " at " + Math.Round(next.Item2, 1) + " mm");
            row.Remove("means");
            best.Item1.Labels.Add(row);
        }

        private static CadPoint Lerp(CadPoint a, CadPoint b, double t) =>
            new CadPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        /// <summary>
        /// A RUN WHOSE LABELS NAME MORE THAN ONE SIZE IS CUT - never at a point nobody drew.
        ///
        /// MEASURED on a real plan: three straight runs of 5-9 m each carried two sizes (8X8 then 8X6) and
        /// were left whole and unsized, which also left the drawn transitions at their ends without a size on
        /// one side. The labels settle more than "contradiction": the section at a label is that label's, and
        /// nothing is drawn between a run's end and its nearest label, so each END is determined. What is
        /// not determined is where, BETWEEN the last label of one size and the first of the next, the change
        /// happens - unless the drawing puts exactly one event there (a branch tapping the run), in which
        /// case the cut is there. Never the midpoint between two texts.
        ///
        /// Pieces are keyed by what anchors them - the parent's end ("end:lo" / "end:hi", by coordinates,
        /// so reversing the drawn direction changes nothing), the first label of an interior block, or the two
        /// labels an unlocated change lies between - not by their order.
        /// </summary>
        private static void Segment(CadSectionReading reading, CadSectionRule rule, double tol)
        {
            double tapTol = rule.TapToleranceMm ?? Math.Max(tol, 1.0);
            var original = reading.Runs.ToList();
            var result = new List<CadRunSection>();
            foreach (CadRunSection r in original)
            {
                if (r.State != "contradictory" || r.ParentRunId != null) { result.Add(r); continue; }
                var assoc = r.Labels.Where(l => l.Value<string>("outcome") == "associated" && l.Value<string>("run") == r.RunId &&
                                                l["along"] != null)
                                    .OrderBy(l => l.Value<double>("along")).ThenBy(l => l.Value<string>("label"), StringComparer.Ordinal).ToList();
                var blocks = new List<List<JObject>>();
                foreach (JObject l in assoc)
                {
                    var last = blocks.LastOrDefault();
                    if (last != null && Math.Abs(last[0].Value<double>("width_mm") - l.Value<double>("width_mm")) < 0.01 &&
                        Math.Abs(last[0].Value<double>("height_mm") - l.Value<double>("height_mm")) < 0.01) last.Add(l);
                    else blocks.Add(new List<JObject> { l });
                }
                Func<JObject, double> at = l => Math.Max(0.0, Math.Min(1.0, l.Value<double>("along")));
                if (blocks.Count < 2 || Enumerable.Range(0, blocks.Count - 1).Any(i => at(blocks[i].Last()) >= at(blocks[i + 1][0])))
                {
                    r.Reason += "; its labels cannot be ordered along it into separate stretches, so it is not cut";
                    result.Add(r);
                    continue;
                }
                var taps = new List<Tuple<double, string>>();
                foreach (CadRunSection q in original)
                {
                    if (q == r) continue;
                    foreach (CadPoint e in new[] { q.Start, q.End })
                    {
                        double t;
                        double d = ToSegment(e, r.Start, r.End, out t);
                        if (d <= tapTol && Dist(e, r.Start) > tol && Dist(e, r.End) > tol && t > 0 && t < 1)
                            taps.Add(Tuple.Create(t, q.RunId));
                    }
                }
                bool lo = r.Start.X < r.End.X - 1e-9 || (Math.Abs(r.Start.X - r.End.X) <= 1e-9 && r.Start.Y <= r.End.Y);
                string startKey = lo ? "end:lo" : "end:hi", endKey = lo ? "end:hi" : "end:lo";
                var pieces = new List<CadRunSection>();
                double cursor = 0.0;
                string cursorWhy = "the run's own end";
                for (int i = 0; i < blocks.Count; i++)
                {
                    List<JObject> b = blocks[i];
                    bool final = i == blocks.Count - 1;
                    string key = i == 0 ? startKey : final ? endKey
                               : "block:" + b.Select(x => x.Value<string>("label")).OrderBy(x => x, StringComparer.Ordinal).First();
                    double to;
                    string toWhy, nextWhy = null;
                    CadRunSection gap = null;
                    if (final) { to = 1.0; toWhy = "the run's own end"; }
                    else
                    {
                        List<JObject> n = blocks[i + 1];
                        double a0 = at(b.Last()), a1 = at(n[0]);
                        var inside = taps.Where(x => x.Item1 > a0 && x.Item1 < a1).ToList();
                        string between = b.Last().Value<string>("label") + " (" + b.Last().Value<string>("text") + ") and " +
                                         n[0].Value<string>("label") + " (" + n[0].Value<string>("text") + ")";
                        if (inside.Count == 1)
                        {
                            to = inside[0].Item1;
                            toWhy = "the branch " + inside[0].Item2 + " tapping the run - the only drawn event between the labels " + between;
                            nextWhy = toWhy;
                        }
                        else
                        {
                            to = a0;
                            toWhy = "the last label of this size, " + b.Last().Value<string>("label");
                            // named by the two labels in a fixed order, so the drawn direction does not rename it
                            var pair = new[] { b.Last().Value<string>("label"), n[0].Value<string>("label") }
                                           .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                            string gk = "between:" + pair[0] + "|" + pair[1];
                            gap = new CadRunSection
                            {
                                RunId = r.RunId + "|" + gk, ParentRunId = r.RunId, PieceKey = gk,
                                SemanticId = r.SemanticId, SourceEntities = new List<string>(r.SourceEntities),
                                Start = Lerp(r.Start, r.End, a0), End = Lerp(r.Start, r.End, a1), ParentFrom = a0, ParentTo = a1,
                                State = "change_unlocated",
                                Reason = "the drawing names " + b[0].Value<string>("text") + " up to label " + b.Last().Value<string>("label") +
                                         " and " + n[0].Value<string>("text") + " from label " + n[0].Value<string>("label") + ", and " +
                                         (inside.Count == 0 ? "draws nothing between them" : inside.Count + " branches tap between them") +
                                         " that places the change; this stretch gets no size",
                                CutReason = "bounded by the labels " + between
                            };
                            nextWhy = "the first label of the next size, " + n[0].Value<string>("label");
                        }
                    }
                    var piece = new CadRunSection
                    {
                        RunId = r.RunId + "|" + key, ParentRunId = r.RunId, PieceKey = key, SemanticId = r.SemanticId,
                        SourceEntities = new List<string>(r.SourceEntities),
                        Start = Lerp(r.Start, r.End, cursor), End = Lerp(r.Start, r.End, to), ParentFrom = cursor, ParentTo = to,
                        State = "documented", WidthMm = b[0].Value<double>("width_mm"), HeightMm = b[0].Value<double>("height_mm"),
                        Labels = new List<JObject>(b),
                        Reason = b.Count + " label(s) of the drawing name this size, and nothing drawn between them and the cut changes it",
                        CutReason = "from " + cursorWhy + " to " + toWhy
                    };
                    if (Dist(piece.Start, piece.End) > tol) pieces.Add(piece);
                    if (gap != null) { if (Dist(gap.Start, gap.End) > tol) pieces.Add(gap); cursor = gap.ParentTo.Value; }
                    else cursor = to;
                    cursorWhy = nextWhy ?? cursorWhy;
                }
                result.AddRange(pieces);
            }
            reading.Runs.Clear();
            reading.Runs.AddRange(result);
        }

        private static string NodeKey(CadPoint p, double tol) =>
            Math.Round(p.X / tol).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(p.Y / tol).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// UNLABELLED RUNS TAKE A SIZE CHAIN BY CHAIN, never in whatever order the loop meets them.
        ///
        /// MEASURED (campaign 7): with runs cut at their section changes, a drawn 243 mm transition sat
        /// between an 8X6 piece and an unlabelled run that led, through an elbow, to a 6X6 label. The
        /// first version propagated from whichever side it reached first and gave the transition 8X6.
        /// Now every chain of unlabelled runs joined end to end (degree-2 joints only; a branch or a free
        /// end stops it) is settled as a whole from what bounds it:
        ///   one size and nothing else  -> every run of the chain takes it (through elbows and straights);
        ///   the same size on both ends -> the same;
        ///   two sizes, or one and an unsettled run -> the size changes inside the chain. The place is the
        ///     one run that is straight at BOTH its ends (a drawn transition piece); with none, the one
        ///     straight joint (an elbow does not change section); anything else is not decided - the
        ///     whole chain stays unsized and says how many places could carry the change.
        /// </summary>
        private static void Propagate(List<CadRunSection> runs, double tol)
        {
            tol = Math.Max(tol, 1.0);
            Func<CadPoint, List<CadRunSection>> at = p =>
                runs.Where(r => Dist(r.Start, p) <= tol || Dist(r.End, p) <= tol).ToList();
            Func<CadRunSection, CadPoint, CadPoint> other = (r, p) => Dist(r.Start, p) <= tol ? r.End : r.Start;
            Func<CadRunSection, CadRunSection, bool> straight = (a, b) =>
            {
                double ax = a.End.X - a.Start.X, ay = a.End.Y - a.Start.Y, bx = b.End.X - b.Start.X, by = b.End.Y - b.Start.Y;
                double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                if (la < 1e-9 || lb < 1e-9) return false;
                return Math.Abs(ax * by - ay * bx) / (la * lb) <= Math.Sin(2.0 * Math.PI / 180);
            };
            var done = new HashSet<CadRunSection>();
            foreach (CadRunSection seed in runs.Where(r => r.State == "missing").ToList())
            {
                if (done.Contains(seed)) continue;
                // the chain, in order, with what bounds it at each end
                var chain = new LinkedList<CadRunSection>();
                chain.AddFirst(seed);
                CadRunSection[] bound = new CadRunSection[2];
                for (int side = 0; side < 2; side++)
                {
                    CadRunSection cur = seed;
                    CadPoint node = side == 0 ? seed.Start : seed.End;
                    while (true)
                    {
                        List<CadRunSection> meeting = at(node);
                        if (meeting.Count != 2) break;                           // a branch or a free end
                        CadRunSection next = meeting.First(m => m != cur);
                        if (next.State == "missing" && !chain.Contains(next))
                        {
                            if (side == 0) chain.AddFirst(next); else chain.AddLast(next);
                            node = other(next, node);
                            cur = next;
                            continue;
                        }
                        if (!chain.Contains(next)) bound[side] = next;
                        break;
                    }
                }
                var list = chain.ToList();
                foreach (CadRunSection r in list) done.Add(r);
                CadRunSection A = bound[0], B = bound[1];
                bool aSized = A != null && A.WidthMm.HasValue, bSized = B != null && B.WidthMm.HasValue;
                bool aUnsettled = A != null && !aSized, bUnsettled = B != null && !bSized;
                Action<CadRunSection, CadRunSection> give = (r, from) =>
                {
                    r.State = "propagated"; r.WidthMm = from.WidthMm; r.HeightMm = from.HeightMm; r.PropagatedFrom = from.RunId;
                    r.Reason = "no label of its own; takes " + from.RunId + "'s size through an elbow or straight continuation";
                };
                bool same = aSized && bSized && Math.Abs(A.WidthMm.Value - B.WidthMm.Value) <= 0.01 &&
                            Math.Abs(A.HeightMm.Value - B.HeightMm.Value) <= 0.01;
                if ((aSized && B == null) || same) { foreach (var r in list) give(r, A); continue; }
                if (bSized && A == null) { foreach (var r in list) give(r, B); continue; }
                if (!(aSized || bSized) || (A == null || B == null)) continue;       // nothing settles it
                // the size changes inside the chain: find the one place
                var seq = new List<CadRunSection> { A };
                seq.AddRange(list);
                seq.Add(B);
                var pieces = new List<int>();                                        // chain index of straight-both-ends runs
                for (int i = 1; i < seq.Count - 1; i++)
                    if (straight(seq[i - 1], seq[i]) && straight(seq[i], seq[i + 1])) pieces.Add(i);
                var joints = new List<int>();                                        // joint between seq[i] and seq[i+1]
                for (int i = 0; i < seq.Count - 1; i++) if (straight(seq[i], seq[i + 1])) joints.Add(i);
                string sizeA = aSized ? A.WidthMm + "x" + A.HeightMm + " mm" : A.State;
                string sizeB = bSized ? B.WidthMm + "x" + B.HeightMm + " mm" : B.State;
                if (pieces.Count == 1)
                {
                    int k = pieces[0];
                    CadRunSection t = seq[k];
                    for (int i = 1; i < k; i++) if (aSized) give(seq[i], A);
                    for (int i = k + 1; i < seq.Count - 1; i++) if (bSized) give(seq[i], B);
                    CadRunSection left = seq[k - 1], right = seq[k + 1];
                    t.State = "transition";
                    t.Reason = "a transition between " + left.RunId + " (" + (left.WidthMm.HasValue ? left.WidthMm + "x" + left.HeightMm + " mm" : left.State) +
                               ") and " + right.RunId + " (" + (right.WidthMm.HasValue ? right.WidthMm + "x" + right.HeightMm + " mm" : right.State) +
                               "): the one unlabelled piece straight at both ends between " + sizeA + " and " + sizeB + "; no size is carried into it";
                    if (left.WidthMm.HasValue && right.WidthMm.HasValue)
                    {
                        bool startTouchesLeft = Dist(t.Start, left.Start) <= tol || Dist(t.Start, left.End) <= tol;
                        CadPoint na = startTouchesLeft ? t.Start : t.End;
                        CadPoint nb = startTouchesLeft ? t.End : t.Start;
                        t.TransitionEnds = new JObject
                        {
                            ["a"] = new JObject { ["run"] = left.RunId, ["at_mm"] = new JArray(Math.Round(na.X, 1), Math.Round(na.Y, 1)),
                                                  ["width_mm"] = left.WidthMm, ["height_mm"] = left.HeightMm },
                            ["b"] = new JObject { ["run"] = right.RunId, ["at_mm"] = new JArray(Math.Round(nb.X, 1), Math.Round(nb.Y, 1)),
                                                  ["width_mm"] = right.WidthMm, ["height_mm"] = right.HeightMm },
                            ["drawn_length_mm"] = Math.Round(Dist(t.Start, t.End), 1)
                        };
                    }
                    continue;
                }
                if (pieces.Count == 0 && joints.Count == 1 && aSized && bSized)
                {
                    int j = joints[0];
                    for (int i = 1; i <= j; i++) give(seq[i], A);
                    for (int i = j + 1; i < seq.Count - 1; i++) give(seq[i], B);
                    foreach (CadRunSection r in list)
                        r.Reason += "; the size changes at the one straight joint of this chain (no transition is drawn; an elbow does not change section)";
                    continue;
                }
                foreach (CadRunSection r in list)
                    r.Reason = "the size changes somewhere along these " + list.Count + " unlabelled run(s) between " + sizeA + " and " + sizeB +
                               ", and " + (pieces.Count > 1 ? pieces.Count + " straight pieces" : joints.Count + " straight joints") +
                               " could carry it; nothing drawn says which, so none gets a size";
            }
        }
    }
}

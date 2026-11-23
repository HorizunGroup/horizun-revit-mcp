// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE LOCAL MEP GRAPH OF ONE BATCH, judged before Revit is asked anything.
//
// A batch of horizun_create_elements (or the request horizun_plan_mep (Pro edition) route_run
// returns) names pipes, ducts, conduits and cable trays by their two end points and
// joins them with fittings through batch_index. Everything needed to know whether a
// fitting can exist is already in the request: where the curves end, which way each
// one leaves the joint, how long each one is. Until now the rehearsal said "valid"
// for those rows and the commit refused them, one at a time, inside the transaction:
//
//   * a "tee" over three curves on ONE axis (the branch lying along the run) - the
//     third port is not perpendicular to the pair that passes straight through, and
//     Revit has no tee to give;
//   * an "elbow" over two curves that leave the joint on the same side - a bend of
//     180 degrees on itself, a return the fitting families do not have;
//   * an elbow or tee over curves that do not meet, found only when the pair
//     selection ran inside the transaction;
//   * a short curve squeezed between two fittings, which fails at commit as "too
//     short" after an automatic transition has eaten the room it needed.
//
// Field cycle MEP (2026-11-12/14): every one of those was met by hand in Python.
//
// WHAT THIS FILE DOES NOT KNOW: the real insertion length of a fitting family
// (Revit reads it from the family, which is opened only inside Revit). So the room a
// fitting takes is either GIVEN (fitting_insertion_mm) and then a short curve is a
// refusal, or ESTIMATED from the curve's own size and then it is only a warning that
// says "estimate". Refusing on a guess would block runs that Revit accepts.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One straight curve of the batch, in feet, in the order the batch lists it.</summary>
    public sealed class MepGraphCurve
    {
        public int Index;
        public string Kind;
        public double[] Start, End;
        /// <summary>Round diameter, or the larger side of a rectangular section; null when the row gives none.</summary>
        public double? SizeFeet;
        /// <summary>The row's type_id, when it gives one (the Revit half reads that type's routing preferences).</summary>
        public long? TypeId;
    }

    /// <summary>One fitting row: its subtype and the batch rows it joins, in Revit's argument order.</summary>
    public sealed class MepGraphFitting
    {
        public int Index;
        public string Subtype;
        public List<int> Members = new List<int>();
    }

    public sealed class MepGraphIssue
    {
        public int Index;
        public string Code;
        public string Message;
        /// <summary>True: the row cannot be built and must be refused. False: a warning the reply carries.</summary>
        public bool Refusal;

        public JObject ToJson() => new JObject
        {
            ["index"] = Index, ["code"] = Code, ["severity"] = Refusal ? "refusal" : "warning", ["message"] = Message
        };
    }

    public static class MepGraphRules
    {
        public const string CodeNotMeeting = "fitting_curves_do_not_meet";
        public const string CodeHairpin = "elbow_returns_on_itself";
        public const string CodeCollinear = "elbow_over_collinear_curves";
        public const string CodeTeeCollinear = "tee_with_collinear_axes";
        public const string CodeTeeHairpin = "tee_run_returns_on_itself";
        public const string CodeJointTurn = "fitting_turn_mismatch";
        public const string CodeShortBetween = "segment_too_short_for_fittings";
        public const string CodeTightBetween = "segment_tight_between_fittings";
        public const string CodeMixedSizesAtElbow = "elbow_between_different_sizes";

        /// <summary>Two curves meet when their ends agree within this (1 mm: Revit's own connector tolerance here).</summary>
        public const double MeetToleranceFeet = 1.0 / 304.8;

        /// <summary>A turn at or past this is a return on itself, not a bend. Curves leaving within 5 degrees of each other.</summary>
        public const double HairpinTurnDegrees = 175.0;

        /// <summary>A turn at or under this is straight: that is a union, not an elbow.</summary>
        public const double StraightTurnDegrees = 1.0;

        /// <summary>A branch within this many degrees of the run's axis (either way) lies along it.</summary>
        public const double CollinearBranchDegrees = 5.0;

        /// <summary>
        /// Estimated room one fitting takes at an end, in diameters. An ESTIMATE, labelled as one wherever it
        /// is used: real families run from about 0.5 to 1.5 diameters for elbows and tees of the common series.
        /// </summary>
        public const double EstimatedInsertionDiameters = 1.0;

        /// <summary>Largest ratio between two sizes that one elbow should be allowed to turn without a transition piece.</summary>
        public const double SingleSizeRatio = 1.4;

        // ---- reading a batch -------------------------------------------------------------

        private static readonly string[] CurveKinds = { "pipe", "duct", "conduit", "cable_tray" };

        /// <summary>
        /// Reads the curve and fitting rows of a create_elements batch. `scaleToFeet` converts the batch's
        /// units; rows that are not MEP curves or fittings are ignored. A fitting that names an element_id
        /// member is kept with that member's slot set to -1: its geometry is not in the batch.
        /// </summary>
        public static void ReadBatch(JArray rows, double scaleToFeet, out List<MepGraphCurve> curves, out List<MepGraphFitting> fittings)
        {
            curves = new List<MepGraphCurve>();
            fittings = new List<MepGraphFitting>();
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i] as JObject;
                if (row == null) continue;
                string kind = (row.Value<string>("kind") ?? "").ToLowerInvariant();
                if (CurveKinds.Contains(kind))
                {
                    double[] a = Point(row["start"], scaleToFeet), b = Point(row["end"], scaleToFeet);
                    if (a == null || b == null) continue;
                    curves.Add(new MepGraphCurve { Index = i, Kind = kind, Start = a, End = b, SizeFeet = SizeOf(row, scaleToFeet),
                        TypeId = row["type_id"]?.Type == JTokenType.Integer ? row["type_id"].Value<long?>() : null });
                }
                else if (kind == "fitting")
                {
                    string sub = (row.Value<string>("fitting") ?? "").ToLowerInvariant();
                    if (sub != "elbow" && sub != "union" && sub != "transition" && sub != "tee") continue;
                    var f = new MepGraphFitting { Index = i, Subtype = sub };
                    if (row["elements"] is JArray members)
                        foreach (JToken m in members)
                        {
                            int? bi = (m as JObject)?["batch_index"]?.Type == JTokenType.Integer ? m["batch_index"].Value<int?>() : null;
                            f.Members.Add(bi ?? -1);
                        }
                    fittings.Add(f);
                }
            }
        }

        private static double[] Point(JToken t, double scale)
        {
            var a = t as JArray;
            if (a == null || a.Count != 3) return null;
            var p = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (a[i].Type != JTokenType.Integer && a[i].Type != JTokenType.Float) return null;
                p[i] = a[i].Value<double>() * scale;
                if (double.IsNaN(p[i]) || double.IsInfinity(p[i])) return null;
            }
            return p;
        }

        private static double? SizeOf(JObject row, double scale)
        {
            double? d = Number(row["diameter"]);
            if (d.HasValue && d.Value > 0) return d.Value * scale;
            double? w = Number(row["width"]), h = Number(row["height"]);
            if (w.HasValue && h.HasValue && w.Value > 0 && h.Value > 0) return Math.Max(w.Value, h.Value) * scale;
            return null;
        }

        private static double? Number(JToken t) =>
            t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? t.Value<double>() : (double?)null;

        // ---- the check -------------------------------------------------------------------

        /// <summary>
        /// Every issue of the local graph. `insertionFeet` is the room ONE fitting takes at one end when the
        /// caller knows it (then a curve too short for its two ends is a refusal); null estimates it from the
        /// curve's size and only warns.
        /// </summary>
        public static List<MepGraphIssue> Check(IList<MepGraphCurve> curves, IList<MepGraphFitting> fittings, double? insertionFeet)
        {
            var issues = new List<MepGraphIssue>();
            var byIndex = new Dictionary<int, MepGraphCurve>();
            foreach (MepGraphCurve c in curves ?? new List<MepGraphCurve>()) byIndex[c.Index] = c;
            // curve index -> ends already taken by a fitting: end 0 = Start, 1 = End
            var taken = new Dictionary<int, List<int>>();
            var takenSubtype = new Dictionary<int, Dictionary<int, string>>();

            foreach (MepGraphFitting f in fittings ?? new List<MepGraphFitting>())
            {
                if (f.Members.Any(m => m < 0 || !byIndex.ContainsKey(m))) continue; // geometry not in the batch: nothing to judge here
                if (f.Subtype == "tee") { CheckTee(f, byIndex, issues, taken, takenSubtype); continue; }
                if (f.Members.Count != 2) continue;
                MepGraphCurve a = byIndex[f.Members[0]], b = byIndex[f.Members[1]];
                int ea, eb; double gap;
                Nearest(a, b, out ea, out eb, out gap);
                if (gap > MeetToleranceFeet)
                {
                    issues.Add(Refuse(f.Index, CodeNotMeeting, "the " + f.Subtype + " joins rows " + a.Index + " and " + b.Index +
                        ", whose nearest ends are " + Mm(gap) + " apart; a fitting joins ends that MEET (within " + Mm(MeetToleranceFeet) +
                        "). Move the ends so they coincide."));
                    continue;
                }
                double angle = AngleBetween(Leg(a, ea), Leg(b, eb));      // between the two legs leaving the joint
                double turn = 180.0 - angle;                               // 0 = straight on, 180 = back on itself
                string problem = ElbowProblem(f.Subtype, turn);
                if (problem != null)
                {
                    string code = f.Subtype != "elbow" ? CodeJointTurn : turn >= HairpinTurnDegrees ? CodeHairpin : CodeCollinear;
                    issues.Add(Refuse(f.Index, code, "rows " + a.Index + " and " + b.Index + ": " + problem));
                    continue;
                }
                Take(taken, takenSubtype, a.Index, ea, f.Subtype);
                Take(taken, takenSubtype, b.Index, eb, f.Subtype);
                if (f.Subtype == "elbow" && a.SizeFeet.HasValue && b.SizeFeet.HasValue)
                {
                    double big = Math.Max(a.SizeFeet.Value, b.SizeFeet.Value), small = Math.Min(a.SizeFeet.Value, b.SizeFeet.Value);
                    string single = SingleSizeAdvice(a.SizeFeet.Value, b.SizeFeet.Value);
                    if (single != null)
                        issues.Add(Warn(f.Index, CodeMixedSizesAtElbow, "rows " + a.Index + " and " + b.Index + " have sizes " +
                            Mm(a.SizeFeet.Value) + " and " + Mm(b.SizeFeet.Value) + " (" + (big / small).ToString("0.00", CultureInfo.InvariantCulture) +
                            "x): an elbow between them is built as elbow + reducer + a stub of a few centimetres. " + single));
                }
            }

            // Room between fittings: a curve that carries a fitting at BOTH ends.
            foreach (KeyValuePair<int, List<int>> kv in taken)
            {
                if (!kv.Value.Contains(0) || !kv.Value.Contains(1)) continue;
                MepGraphCurve c = byIndex[kv.Key];
                double length = Distance(c.Start, c.End);
                if (insertionFeet.HasValue)
                {
                    double needed = 2 * insertionFeet.Value;
                    if (length + 1e-9 < needed)
                        issues.Add(Refuse(c.Index, CodeShortBetween, "row " + c.Index + " is " + Mm(length) + " long and carries a fitting at both ends, " +
                            "which take " + Mm(needed) + " together (fitting_insertion_mm " + Mm(insertionFeet.Value) + " each). " +
                            "Lengthen it, or drop one of the fittings."));
                }
                else if (c.SizeFeet.HasValue)
                {
                    double estimate = 2 * EstimatedInsertionDiameters * c.SizeFeet.Value;
                    if (length + 1e-9 < estimate)
                        issues.Add(Warn(c.Index, CodeTightBetween, "row " + c.Index + " is " + Mm(length) + " long with a fitting at both ends; " +
                            "ESTIMATING " + Mm(EstimatedInsertionDiameters * c.SizeFeet.Value) + " per fitting (1 diameter, not read from the family) they need about " +
                            Mm(estimate) + ". Revit may refuse the commit as 'too short'. Give fitting_insertion_mm to turn this into a hard check."));
                }
            }
            return issues.OrderBy(i => i.Index).ThenBy(i => i.Code, StringComparer.Ordinal).ToList();
        }

        private static void CheckTee(MepGraphFitting f, Dictionary<int, MepGraphCurve> byIndex, List<MepGraphIssue> issues,
                                     Dictionary<int, List<int>> taken, Dictionary<int, Dictionary<int, string>> takenSubtype)
        {
            if (f.Members.Count != 3) return;
            MepGraphCurve r1 = byIndex[f.Members[0]], r2 = byIndex[f.Members[1]], br = byIndex[f.Members[2]];
            int e1, e2, eb; double gap;
            Nearest(r1, r2, out e1, out e2, out gap);
            if (gap > MeetToleranceFeet)
            {
                issues.Add(Refuse(f.Index, CodeNotMeeting, "the tee's two run curves (rows " + r1.Index + " and " + r2.Index +
                    ") end " + Mm(gap) + " apart; the through run of a tee must meet at the junction (within " + Mm(MeetToleranceFeet) + ")."));
                return;
            }
            double[] p = EndOf(r1, e1);
            eb = DistanceToEnd(br, p, out double branchGap);
            if (branchGap > MeetToleranceFeet)
            {
                issues.Add(Refuse(f.Index, CodeNotMeeting, "the tee's branch (row " + br.Index + ") ends " + Mm(branchGap) + " from the junction of rows " +
                    r1.Index + " and " + r2.Index + "; it must end ON that point (within " + Mm(MeetToleranceFeet) + ")."));
                return;
            }
            double[] a1 = Leg(r1, e1), a2 = Leg(r2, e2), ab = Leg(br, eb);
            double runAngle = AngleBetween(a1, a2);
            string problem = TeeProblem(runAngle, AngleBetween(ab, a1), AngleBetween(ab, a2), out bool hairpin);
            if (problem != null)
            {
                issues.Add(Refuse(f.Index, hairpin ? CodeTeeHairpin : CodeTeeCollinear, "rows " + r1.Index + ", " + r2.Index + ", " + br.Index + ": " + problem));
                return;
            }
            Take(taken, takenSubtype, r1.Index, e1, "tee");
            Take(taken, takenSubtype, r2.Index, e2, "tee");
            Take(taken, takenSubtype, br.Index, eb, "tee");
        }

        // ---- the joint judged from angles (shared with the existing-element path) -------------

        /// <summary>
        /// Why a fitting cannot join curves that turn `turnDegrees` (0 = straight on, 180 = back on itself); null when it can.
        /// An elbow needs a corner and not a return; a union or transition needs a straight joint.
        /// </summary>
        public static string ElbowProblem(string subtype, double turnDegrees)
        {
            string turnText = turnDegrees.ToString("0.0", CultureInfo.InvariantCulture);
            if (subtype == "elbow")
            {
                if (turnDegrees >= HairpinTurnDegrees)
                    return "both curves leave the joint on the same side (a turn of " + turnText + " degrees): that is a return of 180 degrees on itself, which no elbow family has. " +
                           "Route it with two elbows and a leg between them.";
                if (turnDegrees <= StraightTurnDegrees)
                    return "the curves are collinear (turn " + turnText + " degrees); an elbow needs a corner - use a union (same size) or a transition.";
                return null;
            }
            if ((subtype == "union" || subtype == "transition") && turnDegrees > StraightTurnDegrees)
                return "the curves turn " + turnText + " degrees; a " + subtype + " joins collinear curves - use an elbow.";
            return null;
        }

        /// <summary>
        /// Why three legs cannot be one tee; null when they can. `runDegrees` is the angle between the two run
        /// legs (180 = straight through); the other two are the branch's angles to each run leg.
        /// </summary>
        public static string TeeProblem(double runDegrees, double branchToRun1, double branchToRun2, out bool hairpin)
        {
            hairpin = false;
            if (runDegrees <= CollinearBranchDegrees)
            {
                hairpin = true;
                return "the two run curves leave the junction on the same side (" + runDegrees.ToString("0.0", CultureInfo.InvariantCulture) +
                       " degrees apart): the 'run' returns on itself, so there is no through pair for a tee.";
            }
            double nearest = Math.Min(branchToRun1, branchToRun2);
            if (nearest <= CollinearBranchDegrees)
                return "the branch lies along a run curve (" + nearest.ToString("0.0", CultureInfo.InvariantCulture) + " degrees): three ports on one axis. " +
                       "The third port must leave perpendicular (or at a drawn angle) to the pair that passes straight through.";
            return null;
        }

        /// <summary>
        /// The same judgement from connectors read in the model: outward directions (each connector's BasisZ,
        /// pointing OUT of its element). `branch` is null for a two-port joint.
        /// </summary>
        public static string ExistingJointProblem(string subtype, double[] outA, double[] outB, double[] outBranch)
        {
            // legs leaving the joint are the opposite of the connectors' outward bases
            double[] la = Neg(outA), lb = Neg(outB);
            if (subtype == "tee" && outBranch != null)
            {
                double[] lc = Neg(outBranch);
                return TeeProblem(AngleBetween(la, lb), AngleBetween(lc, la), AngleBetween(lc, lb), out _);
            }
            return ElbowProblem(subtype, 180.0 - AngleBetween(la, lb));
        }

        /// <summary>
        /// When two sizes differ by less than SingleSizeRatio the run that turns should keep ONE size - the larger -
        /// so the elbow does not drag a reducer and a stub with it. Null when the sizes are equal, differ more, or are unknown.
        /// </summary>
        public static string SingleSizeAdvice(double sizeAFeet, double sizeBFeet)
        {
            if (!(sizeAFeet > 0) || !(sizeBFeet > 0)) return null;
            double big = Math.Max(sizeAFeet, sizeBFeet), small = Math.Min(sizeAFeet, sizeBFeet);
            if (big / small >= SingleSizeRatio) return null;
            if (big - small < 0.5 / 304.8) return null; // under half a millimetre apart: the same size
            return "Use one size, " + Mm(big) + ", for both curves that turn here (they differ by less than " +
                   SingleSizeRatio.ToString("0.0", CultureInfo.InvariantCulture) + "x) and reduce at the tee instead.";
        }

        // ---- routing preferences -------------------------------------------------------------

        public const string CodeNoRoutingPreference = "curve_type_without_routing_preference";

        /// <summary>The routing-preference rule group that supplies the family for a fitting subtype; null when it has none.</summary>
        public static string RoutingGroupFor(string subtype)
        {
            switch (subtype)
            {
                case "elbow": return "Elbows";
                case "tee": case "takeoff": return "Junctions";
                case "cross": return "Crosses";
                case "transition": return "Transitions";
                case "union": return "Unions";
                default: return null;
            }
        }

        /// <summary>
        /// The warning for a pipe or duct type whose routing preferences hold NO rule for the fitting the batch asks for
        /// (the 'Default' pipe type ships empty): Revit places no elbow or tee there on its own and the fitting creation
        /// can refuse. Null when the group has a rule, or when the family has no such group (ducts have no unions).
        /// </summary>
        public static string RoutingPreferenceWarning(string curveKind, string typeName, string subtype, int rulesInGroup)
        {
            string group = RoutingGroupFor(subtype);
            if (group == null || rulesInGroup > 0) return null;
            if (group == "Unions" && curveKind == "duct") return null;
            return CodeNoRoutingPreference + ": the " + curveKind + " type '" + typeName + "' has no routing-preference rule for " + group.ToLowerInvariant() +
                   ", so Revit has no " + subtype + " family to put there. In Routing Preferences of that type add a rule for " + group.ToLowerInvariant() +
                   " (a type with preferences, or a duplicate of one that has them), then rehearse again.";
        }

        // ---- geometry --------------------------------------------------------------------

        private static double[] EndOf(MepGraphCurve c, int end) => end == 0 ? c.Start : c.End;
        private static double[] OtherEnd(MepGraphCurve c, int end) => end == 0 ? c.End : c.Start;

        /// <summary>Unit vector along the curve, leaving the joint at `end`.</summary>
        private static double[] Leg(MepGraphCurve c, int end)
        {
            double[] from = EndOf(c, end), to = OtherEnd(c, end);
            double dx = to[0] - from[0], dy = to[1] - from[1], dz = to[2] - from[2];
            double n = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return n < 1e-12 ? new[] { 0.0, 0.0, 0.0 } : new[] { dx / n, dy / n, dz / n };
        }

        private static double[] Neg(double[] v)
        {
            double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return n < 1e-12 ? new[] { 0.0, 0.0, 0.0 } : new[] { -v[0] / n, -v[1] / n, -v[2] / n };
        }

        private static double AngleBetween(double[] a, double[] b)
        {
            double na = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            double nb = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
            if (na < 1e-12 || nb < 1e-12) return 0;
            double dot = (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) / (na * nb);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot))) * 180.0 / Math.PI;
        }

        private static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static void Nearest(MepGraphCurve a, MepGraphCurve b, out int endA, out int endB, out double gap)
        {
            endA = 0; endB = 0; gap = double.MaxValue;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    double d = Distance(EndOf(a, i), EndOf(b, j));
                    if (d < gap) { gap = d; endA = i; endB = j; }
                }
        }

        private static int DistanceToEnd(MepGraphCurve c, double[] p, out double gap)
        {
            double d0 = Distance(c.Start, p), d1 = Distance(c.End, p);
            gap = Math.Min(d0, d1);
            return d0 <= d1 ? 0 : 1;
        }

        private static void Take(Dictionary<int, List<int>> taken, Dictionary<int, Dictionary<int, string>> sub, int curve, int end, string subtype)
        {
            if (!taken.TryGetValue(curve, out List<int> ends)) taken[curve] = ends = new List<int>();
            if (!ends.Contains(end)) ends.Add(end);
            if (!sub.TryGetValue(curve, out Dictionary<int, string> bySubtype)) sub[curve] = bySubtype = new Dictionary<int, string>();
            bySubtype[end] = subtype;
        }

        private static MepGraphIssue Refuse(int index, string code, string message) =>
            new MepGraphIssue { Index = index, Code = code, Message = message, Refusal = true };

        private static MepGraphIssue Warn(int index, string code, string message) =>
            new MepGraphIssue { Index = index, Code = code, Message = message, Refusal = false };

        private static string Mm(double feet) => (feet * 304.8).ToString("0.0", CultureInfo.InvariantCulture) + " mm";
    }
}

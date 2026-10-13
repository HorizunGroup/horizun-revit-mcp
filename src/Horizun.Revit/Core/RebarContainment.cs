// -----------------------------------------------------------------------------
// Horizun Revit MCP - one containment answer for a whole rebar SET.
// Original Horizun code. No Revit types.
//
// SolidContainment answers the question for one bar. A set is an array of bars,
// and the plan, the apply and the audit each have a version of the same
// question - "is this set in the concrete" - which they used to answer three
// slightly different ways. This is the single definition all three call:
//
//   the plan       hands it the centreline it is ABOUT TO ask for, and the
//                  offsets its own arithmetic predicts
//   the apply      hands it the centreline Revit DREW and the offsets Revit
//                  computed, both read back after the commit
//   the audit      hands it the same, read from a model nobody is writing to
//
// Same code, same tolerance, same five words. A plan that says a set fits and an
// audit that later says it does not are then disagreeing about the MODEL rather
// than about arithmetic.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class SetContainment
    {
        public string Word = SolidContainment.NotEvaluable;
        public bool Evaluated;
        public string Why;

        public int PositionsTested;
        public int PositionsInside;
        public double SampleStepMm;

        /// <summary>The position that came off worst, and its verdict.</summary>
        public int WorstPosition = -1;
        public ContainmentVerdict Worst;

        /// <summary>Every position whose answer was not <c>inside</c>.</summary>
        public List<int> NotInsidePositions = new List<int>();

        public double WorstOutsideMm;
        public double WorstCoverShortfallMm;
        public double MinSurfaceClearanceMm;

        public bool CurvedBoundaryApproximated;
        public double ChordToleranceMm;

        /// <summary>The radius every surface number here was computed with.</summary>
        public double BarRadiusMm;
        /// <summary>True when at least one position produced numbers, even if the set as a whole did not.</summary>
        public bool Measured;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["containment"] = Word,
                ["evaluated"] = Evaluated,
                ["positions_tested"] = PositionsTested,
                ["positions_inside"] = PositionsInside,
                ["sample_step_mm"] = Math.Round(SampleStepMm, 4),
                ["bar_radius_mm"] = Math.Round(BarRadiusMm, 4),
                ["why"] = Why,
                ["how_measured"] = Worst != null
                    ? Worst.HowMeasured
                    : "every sampled point of every bar position, against the host's own boundary."
            };
            if (NotInsidePositions.Count > 0)
                o["positions_not_inside"] = new JArray(NotInsidePositions.ToArray());
            if (WorstPosition >= 0) o["worst_position"] = WorstPosition;
            if (Measured)
            {
                o["min_surface_clearance_mm"] = Math.Round(MinSurfaceClearanceMm, 3);
                o["worst_outside_mm"] = Math.Round(WorstOutsideMm, 3);
                o["worst_cover_shortfall_mm"] = Math.Round(WorstCoverShortfallMm, 3);
                if (!Evaluated)
                    o["numbers_are_partial"] =
                        "these come from the positions that COULD be measured. The set as a whole is " +
                        "not_evaluable, and a position nobody measured could be worse.";
            }
            if (Worst != null && Worst.WorstPointMm != null)
                o["worst_point_mm"] = new JArray(
                    Math.Round(Worst.WorstPointMm[0], 3),
                    Math.Round(Worst.WorstPointMm[1], 3),
                    Math.Round(Worst.WorstPointMm[2], 3));
            if (Worst != null && Worst.SeamWidthMm > 0)
                o["joints"] = new JObject
                {
                    ["elements_not_convex"] = true,
                    ["ray_samples_in_worst_position"] = Worst.RaySamples,
                    ["rays"] = SolidContainment.RayCount,
                    ["ray_covering_angle_deg"] = Math.Round(SolidContainment.RayCoveringAngleDeg, 4),
                    ["clearance_bound_mm"] = Math.Round(Worst.ClearanceBoundMm, 4),
                    ["seam_width_mm"] = Math.Round(Worst.SeamWidthMm, 4),
                    ["means"] =
                        "the elements are joined and at least one is not convex. Near a joint the distance to air is " +
                        "the nearest face point confirmed to be beside air (exact), or else the nearest exit of a fixed " +
                        "set of rays times the cosine of their covering angle: never more than the true clearance, and " +
                        "short of it by at most clearance_bound_mm. A gap between two elements no wider than the seam " +
                        "width is tessellation, not air."
                };
            if (CurvedBoundaryApproximated)
            {
                o["boundary_is_approximated"] = true;
                o["chord_tolerance_mm"] = Math.Round(ChordToleranceMm, 3);
                o["approximation_means"] =
                    "the host has at least one curved face, so its boundary here is a many-sided prism " +
                    "sitting slightly INSIDE the real surface. A bar close to that surface is reported " +
                    "marginally worse than it is, never better.";
            }
            return o;
        }
    }

    /// <summary>One declared anchorage against what was measured.</summary>
    public sealed class AnchorageResult
    {
        public long ElementId;
        public double RequiredMm;
        /// <summary>The shortest length any bar of the set has inside the element; null when not measured.</summary>
        public double? MeasuredMm;
        public string Verdict;
        public string Why;

        public JObject ToJson() => new JObject
        {
            ["element_id"] = ElementId,
            ["min_length_mm"] = Math.Round(RequiredMm, 3),
            ["measured_mm"] = MeasuredMm.HasValue ? (JToken)Math.Round(MeasuredMm.Value, 3) : JValue.CreateNull(),
            ["verdict"] = Verdict,
            ["why"] = Why
        };
    }

    /// <summary>
    /// The anchored length a rule declared in an element it runs into (acero 3.0, L1), against
    /// the length its bars have inside that element's solid - the shortest bar of the set
    /// decides. The plan measures the declared centreline, which has no hooks yet; when that is
    /// short and a hook is declared, the answer waits for the centreline Revit draws.
    /// </summary>
    public static class AnchorageCheck
    {
        public const string Met = "met";
        public const string Short = "short";
        public const string AfterCommit = "decided_after_commit";
        public const string NotMeasured = "not_measured";

        public static List<AnchorageResult> Evaluate(HostMesh union, IList<long> partIds,
            IList<StructuralAnchorage> wanted, IList<double[]> centrelineMm, IList<double> signedOffsetsMm,
            double[] normal, double toleranceMm, bool hooksDrawn, bool hookDeclared)
        {
            var outp = new List<AnchorageResult>();
            if (wanted == null) return outp;
            double[] lengths = union == null ? null : RebarContainment.AnchoredLengths(union, centrelineMm, signedOffsetsMm, normal);
            foreach (StructuralAnchorage a in wanted)
            {
                var r = new AnchorageResult { ElementId = a.ElementId, RequiredMm = a.MinLengthMm };
                int k = partIds == null ? -1 : IndexOf(partIds, a.ElementId);
                if (lengths == null || k < 0 || k >= lengths.Length || !SolidContainment.IsFinite(lengths[k]))
                {
                    r.Verdict = NotMeasured;
                    r.Why = lengths == null
                        ? "the union of the elements could not be measured, so the anchored length in element " +
                          a.ElementId + " is UNKNOWN. Unknown is not met."
                        : "element " + a.ElementId + " is not one the length could be measured in.";
                    outp.Add(r);
                    continue;
                }
                r.MeasuredMm = lengths[k];
                string got = lengths[k].ToString("0.###", CultureInfo.InvariantCulture);
                string want = a.MinLengthMm.ToString("0.###", CultureInfo.InvariantCulture);
                if (lengths[k] >= a.MinLengthMm - toleranceMm)
                {
                    r.Verdict = Met;
                    r.Why = "the shortest bar runs " + got + " mm inside element " + a.ElementId + "; " + want + " mm was declared.";
                }
                else if (!hooksDrawn && hookDeclared)
                {
                    r.Verdict = AfterCommit;
                    r.Why = "the declared centreline runs " + got + " mm inside element " + a.ElementId + " and " + want +
                            " mm was declared. A hook is declared, and the length Revit gives it is only known once the " +
                            "bar is drawn, so the anchorage is measured on the drawn bar after the commit.";
                }
                else
                {
                    r.Verdict = Short;
                    r.Why = "the shortest bar runs " + got + " mm inside element " + a.ElementId + " and " + want +
                            " mm was declared" + (hooksDrawn ? ", measured on the bar as drawn, hooks included." : ".");
                }
                outp.Add(r);
            }
            return outp;
        }

        private static int IndexOf(IList<long> ids, long id)
        {
            for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
            return -1;
        }
    }

    public static class RebarContainment
    {
        /// <summary>How far apart the samples along each bar are, unless told otherwise.</summary>
        public const double DefaultSampleStepMm = 25.0;

        /// <summary>
        /// The answer for a whole set: every bar position, each sampled along its
        /// length, against the host's own boundary. The weakest answer wins, because
        /// a set with one bar in the air is not a set that fits.
        /// </summary>
        public static SetContainment Check(HostMesh mesh, IList<double[]> centrelineMm,
            IList<double> signedOffsetsMm, double[] normal, double barRadiusMm,
            double? requiredCoverMm, double toleranceMm, double sampleStepMm)
        {
            return Check(mesh, centrelineMm, false, signedOffsetsMm, normal, barRadiusMm,
                         requiredCoverMm, toleranceMm, sampleStepMm);
        }

        /// <summary>The same, told whether the bar closes. See SolidContainment.Classify.</summary>
        public static SetContainment Check(HostMesh mesh, IList<double[]> centrelineMm, bool closed,
            IList<double> signedOffsetsMm, double[] normal, double barRadiusMm,
            double? requiredCoverMm, double toleranceMm, double sampleStepMm)
        {
            return Check(mesh, centrelineMm, closed, signedOffsetsMm, normal, barRadiusMm,
                         requiredCoverMm, toleranceMm, sampleStepMm, null);
        }

        /// <summary>
        /// The same, told WHY the boundary is missing when it is. MEASURED on a real model: a bar
        /// declared into five elements was refused with only "the host boundary was not available",
        /// and nothing said which of the five, or what was wrong with it. The reason the caller
        /// already holds - the element and its diagnosis - is the reason given.
        /// </summary>
        public static SetContainment Check(HostMesh mesh, IList<double[]> centrelineMm, bool closed,
            IList<double> signedOffsetsMm, double[] normal, double barRadiusMm,
            double? requiredCoverMm, double toleranceMm, double sampleStepMm, string boundaryWhy)
        {
            var r = new SetContainment
            {
                CurvedBoundaryApproximated = mesh != null && mesh.AnyCurvedFace,
                ChordToleranceMm = mesh == null ? 0 : mesh.ChordToleranceMm,
                BarRadiusMm = barRadiusMm
            };

            if (mesh == null)
            {
                r.Why = string.IsNullOrWhiteSpace(boundaryWhy)
                    ? "the host boundary was not available, so containment was not measured. Unknown is not a pass."
                    : boundaryWhy.TrimEnd() + (boundaryWhy.Contains("Unknown is not a pass") ? "" : " Unknown is not a pass.");
                return r;
            }
            if (centrelineMm == null || centrelineMm.Count == 0)
            {
                r.Why = "no centreline was available for this set.";
                return r;
            }

            // ANY NONZERO OFFSET needs a direction to be an offset ALONG. The guard
            // used to ask how MANY offsets there were, so a single bar at 900 mm
            // with an unusable normal was measured, unmoved, at zero - and answered
            // confidently about a place the bar is not.
            double[] unit = Unit(normal);
            if (unit == null && signedOffsetsMm != null)
                foreach (double off in signedOffsetsMm)
                    if (!SolidContainment.IsFinite(off) || Math.Abs(off) > 1e-9)
                    {
                        r.Why = "the distribution direction was not a usable vector, so a bar offset by " +
                                off.ToString("0.###", CultureInfo.InvariantCulture) +
                                " mm could not be placed for the test.";
                        return r;
                    }

            var offsets = new List<double>();
            if (signedOffsetsMm == null || signedOffsetsMm.Count == 0) offsets.Add(0);
            else offsets.AddRange(signedOffsetsMm);

            double step = sampleStepMm > 0 ? sampleStepMm : DefaultSampleStepMm;
            var words = new List<string>();
            double worstOut = 0, worstCover = 0, minClear = double.MaxValue;
            int worstPos = -1, unmeasuredPos = -1;
            ContainmentVerdict worstVerdict = null, unmeasuredVerdict = null;

            for (int i = 0; i < offsets.Count; i++)
            {
                double d = offsets[i];
                if (!SolidContainment.IsFinite(d))
                {
                    r.Why = "bar position " + i + " was not a finite offset.";
                    r.WorstPosition = i;
                    return r;
                }

                var moved = new List<double[]>(centrelineMm.Count);
                foreach (double[] p in centrelineMm)
                {
                    if (p == null || p.Length < 3)
                    {
                        r.Why = "the centreline contained a point that was not three numbers.";
                        return r;
                    }
                    moved.Add(unit == null
                        ? new[] { p[0], p[1], p[2] }
                        : new[] { p[0] + unit[0] * d, p[1] + unit[1] * d, p[2] + unit[2] * d });
                }

                ContainmentVerdict v = SolidContainment.Classify(
                    mesh, moved, closed, barRadiusMm, requiredCoverMm, toleranceMm, step);

                words.Add(v.Word);
                r.PositionsTested++;
                r.SampleStepMm = v.SampleStepMm;
                if (v.Word == SolidContainment.Inside) r.PositionsInside++;
                else r.NotInsidePositions.Add(i);

                if (v.WorstOutsideMm > worstOut) worstOut = v.WorstOutsideMm;
                if (v.CoverShortfallMm > worstCover) worstCover = v.CoverShortfallMm;

                // TWO DIFFERENT WORST CASES, tracked separately. They used to share
                // one slot, and an unmeasurable position after a measurable one
                // never took it - so a set reported not_evaluable while naming a
                // GOOD bar as its worst position, and quoting that good bar's
                // measurement as the reason nothing could be measured.
                if (v.Evaluated)
                {
                    if (v.MinSurfaceClearanceMm < minClear)
                    {
                        minClear = v.MinSurfaceClearanceMm;
                        worstPos = i;
                        worstVerdict = v;
                    }
                }
                else if (unmeasuredVerdict == null)
                {
                    unmeasuredPos = i;
                    unmeasuredVerdict = v;
                }
            }

            r.Word = SolidContainment.Weakest(words);
            r.WorstOutsideMm = worstOut;
            r.WorstCoverShortfallMm = worstCover;
            r.MinSurfaceClearanceMm = minClear == double.MaxValue ? 0 : minClear;

            // The numbers that WERE measured stay published even when a sibling bar
            // could not be. A cover violation somebody can act on used to be
            // suppressed because another position in the same set was unreadable.
            r.Measured = minClear != double.MaxValue;
            r.Evaluated = r.Word != SolidContainment.NotEvaluable;

            if (r.Word == SolidContainment.NotEvaluable && unmeasuredVerdict != null)
            {
                r.Worst = unmeasuredVerdict;
                r.WorstPosition = unmeasuredPos;
                r.Why = "bar position " + unmeasuredPos + " of " + r.PositionsTested + " could not be " +
                        "measured: " + unmeasuredVerdict.Why +
                        (r.Measured
                            ? " The other positions were measured and their numbers are published, but the " +
                              "set as a whole has not been established."
                            : "");
            }
            else
            {
                r.Worst = worstVerdict;
                r.WorstPosition = worstPos;
                if (r.Word == SolidContainment.Inside)
                    r.Why = "all " + r.PositionsTested + " bar position(s) are inside the host" +
                            (requiredCoverMm.HasValue ? " and meet the declared cover." : ".");
                else if (worstVerdict != null)
                    r.Why = "bar position " + worstPos + " of " + r.PositionsTested + ": " + worstVerdict.Why;
                else
                    r.Why = "no bar position could be measured.";
            }

            return r;
        }

        /// <summary>
        /// The anchored length of a SET in each part of a union (acero 3.0, L1): for every part,
        /// the shortest length any one bar position has inside it. A set is anchored as well as
        /// its least-anchored bar. Null when the mesh is not a union or a position cannot be placed;
        /// NaN for a part whose length could not be measured.
        /// </summary>
        public static double[] AnchoredLengths(HostMesh union, IList<double[]> centrelineMm,
            IList<double> signedOffsetsMm, double[] normal)
        {
            if (union == null || union.Parts == null || centrelineMm == null || centrelineMm.Count < 2) return null;
            double[] unit = Unit(normal);
            var offsets = new List<double>();
            if (signedOffsetsMm == null || signedOffsetsMm.Count == 0) offsets.Add(0);
            else offsets.AddRange(signedOffsetsMm);
            if (unit == null && offsets.Exists(o => Math.Abs(o) > 1e-9)) return null;

            var outp = new double[union.Parts.Count];
            for (int k = 0; k < outp.Length; k++) outp[k] = double.MaxValue;
            foreach (double d in offsets)
            {
                if (!SolidContainment.IsFinite(d)) return null;
                var moved = new List<double[]>(centrelineMm.Count);
                foreach (double[] p in centrelineMm)
                    moved.Add(unit == null ? new[] { p[0], p[1], p[2] }
                                           : new[] { p[0] + unit[0] * d, p[1] + unit[1] * d, p[2] + unit[2] * d });
                for (int k = 0; k < outp.Length; k++)
                {
                    double len = SolidContainment.LengthInside(union.Parts[k], moved);
                    if (!SolidContainment.IsFinite(len)) outp[k] = double.NaN;
                    else if (!double.IsNaN(outp[k]) && len < outp[k]) outp[k] = len;
                }
            }
            return outp;
        }

        /// <summary>A unit vector, or null when the input is not one.</summary>
        public static double[] Unit(double[] v)
        {
            if (v == null || v.Length < 3) return null;
            for (int i = 0; i < 3; i++) if (!SolidContainment.IsFinite(v[i])) return null;
            double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (!SolidContainment.IsFinite(n) || n < 1e-12) return null;
            return new[] { v[0] / n, v[1] / n, v[2] / n };
        }

        /// <summary>
        /// The audit's version of the question: what should a reader be told when a
        /// set is not fully inside its host. Returns null when there is nothing to
        /// say - which is the only case where silence is correct.
        /// </summary>
        public static string FindingCodeFor(string containmentWord)
        {
            switch (containmentWord)
            {
                case SolidContainment.Inside: return null;
                case SolidContainment.InsideCoverViolated: return RebarFinding.CoverViolated;
                case SolidContainment.PartiallyOutside: return RebarFinding.BarPartiallyOutsideHost;
                case SolidContainment.CompletelyOutside: return RebarFinding.BarOutsideHost;
                case SolidContainment.NotEvaluable: return RebarFinding.ContainmentNotEvaluable;
                default:
                    throw new ArgumentException("unknown containment word '" + containmentWord + "'");
            }
        }

        /// <summary>How bad a containment answer is, for the verdict.</summary>
        public static string SeverityFor(string containmentWord)
        {
            switch (containmentWord)
            {
                case SolidContainment.Inside: return null;
                case SolidContainment.InsideCoverViolated: return "error";
                case SolidContainment.PartiallyOutside: return "error";
                case SolidContainment.CompletelyOutside: return "error";
                case SolidContainment.NotEvaluable: return "unknown";
                default:
                    throw new ArgumentException("unknown containment word '" + containmentWord + "'");
            }
        }

        /// <summary>
        /// The cover a bar declared into other elements is held to (L1): the SMALLEST cover any of the
        /// elements declares. `coverRuleCovers` is what cover_rules declare for each element of the union
        /// (null: none); `sectionCoverMm` is what the bar's own section declares for its host. MEASURED in
        /// the second steel duel (2026-10-12): the section's 40 mm was never counted, so a footing's 75 mm
        /// "cast against earth" held every column bar - the column above's too - to 75 and refused them all.
        /// The section's cover lowers what the cover rules declare; it never holds a union to a cover where
        /// the cover rules declare none, because a host measured alone is not held to its section's cover
        /// either. Null: no cover at all, as before.
        /// </summary>
        public static double? UnionCoverMm(IEnumerable<double?> coverRuleCovers, double? sectionCoverMm)
        {
            double? cover = null;
            foreach (double? c in coverRuleCovers ?? Enumerable.Empty<double?>())
                if (c.HasValue) cover = cover.HasValue ? Math.Min(cover.Value, c.Value) : c;
            if (cover.HasValue && sectionCoverMm.HasValue) cover = Math.Min(cover.Value, sectionCoverMm.Value);
            return cover;
        }

        /// <summary>
        /// Why the union a bar runs through was not built: one of its elements had no usable
        /// boundary. Names the element; the reason it carries is the diagnosis of that element.
        /// </summary>
        public static string UnionElementUnreadableWhy(long elementId, string why)
        {
            return "the boundary of element " + elementId + ", one of the elements the bars run through, could " +
                   "not be read (" + (string.IsNullOrWhiteSpace(why) ? "no reason was given" : why.TrimEnd()) +
                   "), so the union was not built and containment was not measured. Unknown is not a pass.";
        }

        /// <summary>
        /// The explanation and the reason, each said ONCE. Explain already quotes the reason for an
        /// answer that could not be measured; appending it again printed the same sentence twice.
        /// </summary>
        public static string Describe(SetContainment c)
        {
            if (c == null) return Explain(null);
            if (c.Word == SolidContainment.NotEvaluable || string.IsNullOrWhiteSpace(c.Why)) return Explain(c);
            return Explain(c) + " " + c.Why;
        }

        /// <summary>A sentence a person can act on, for the plan and the apply.</summary>
        public static string Explain(SetContainment c)
        {
            if (c == null) return "containment was not measured.";
            switch (c.Word)
            {
                case SolidContainment.Inside:
                    return "every bar is inside the host.";
                case SolidContainment.InsideCoverViolated:
                    return "the steel is in the concrete but " +
                           c.WorstCoverShortfallMm.ToString("0.###", CultureInfo.InvariantCulture) +
                           " mm short of the declared cover at its worst point.";
                case SolidContainment.PartiallyOutside:
                    return c.WorstOutsideMm.ToString("0.###", CultureInfo.InvariantCulture) +
                           " mm of steel is outside the host at its worst point.";
                case SolidContainment.CompletelyOutside:
                    return "at least one bar is entirely outside the host.";
                default:
                    return "containment could not be measured, which is not a pass: " + c.Why;
            }
        }
    }
}

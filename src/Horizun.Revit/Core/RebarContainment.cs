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
            var r = new SetContainment
            {
                CurvedBoundaryApproximated = mesh != null && mesh.AnyCurvedFace,
                ChordToleranceMm = mesh == null ? 0 : mesh.ChordToleranceMm,
                BarRadiusMm = barRadiusMm
            };

            if (mesh == null)
            {
                r.Why = "the host boundary was not available, so containment was not measured. " +
                        "Unknown is not a pass.";
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

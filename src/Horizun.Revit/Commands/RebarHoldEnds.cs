using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>
    /// hold_ends (acero 3.0, stage 1b). MEASURED on Revit 2024 (anonymized regression case): Revit constrains a bar's handles to
    /// the cover of the elements it runs into and pulls its ends there - up to 12 mm on a beam bar anchored in
    /// a column. Asked for, each handle is re-pinned to a FIXED DISTANCE from its host face, and that distance
    /// is stepped until the drawn bar is back on the plan (HoldEndsRules.ToleranceMm at every vertex) - the way
    /// an external pipeline restored its bars. The result is measured, never assumed: a bar that cannot be held
    /// says so, and the apply's verification fails on it.
    /// </summary>
    internal static class RebarHoldEnds
    {
        private const double FtToMm = 304.8;

        internal static JObject Hold(Document doc, Rebar bar, IList<double[]> plannedMm)
        {
            var o = new JObject { ["requested"] = true, ["tolerance_mm"] = HoldEndsRules.ToleranceMm };
            double worst, total;
            if (!Measure(bar, plannedMm, out worst, out total))
            {
                o["verified"] = false;
                o["why"] = "the drawn bar has another number of vertices than the plan, so its ends cannot be compared one to one.";
                return o;
            }
            o["before_mm"] = Math.Round(worst, 3);
            if (HoldEndsRules.Held(worst))
            {
                o["after_mm"] = Math.Round(worst, 3); o["handles_changed"] = 0; o["verified"] = true;
                o["why"] = "already where the plan put it; nothing was changed.";
                return o;
            }
            RebarConstraintsManager cm = bar.GetRebarConstraintsManager();
            IList<RebarConstrainedHandle> handles = cm.GetAllHandles();
            int switched = 0, steps = 0;
            // 1. EVERY HANDLE TO A FIXED DISTANCE FROM A HOST FACE: a constraint to a cover follows the cover.
            foreach (RebarConstrainedHandle h in handles)
            {
                RebarConstraint c = cm.GetCurrentConstraintOnHandle(h);
                if (c != null && c.GetConstraintType() == RebarConstraintType.FixedDistanceToHostFace) continue;
                RebarConstraint fixedOne = Candidates(cm, h, bar)
                    .FirstOrDefault(k => k.GetConstraintType() == RebarConstraintType.FixedDistanceToHostFace);
                if (fixedOne == null) continue;
                Prefer(cm, h, fixedOne);
                switched++;
            }
            doc.Regenerate();
            Measure(bar, plannedMm, out worst, out total);
            // 2. STEP EACH DISTANCE toward the plan while the summed deviation falls.
            for (int round = 0; round < 8 && !HoldEndsRules.Held(worst); round++)
            {
                bool improved = false;
                foreach (RebarConstrainedHandle h in cm.GetAllHandles())
                {
                    RebarConstraint c = cm.GetCurrentConstraintOnHandle(h);
                    if (c == null || c.GetConstraintType() != RebarConstraintType.FixedDistanceToHostFace) continue;
                    double baseFt = c.GetDistanceToTargetHostFace();
                    double e = Math.Max(worst, 0.2) / FtToMm;
                    foreach (double step in new[] { e, -e, e / 2, -e / 2, 2 * e, -2 * e })
                    {
                        c.SetDistanceToTargetHostFace(baseFt + step);
                        Prefer(cm, h, c);
                        doc.Regenerate(); steps++;
                        double nw, nt;
                        if (Measure(bar, plannedMm, out nw, out nt) && nt < total - 0.01)
                        {
                            worst = nw; total = nt; baseFt += step; improved = true;
                            break;
                        }
                        c.SetDistanceToTargetHostFace(baseFt);
                        Prefer(cm, h, c);
                        doc.Regenerate();
                    }
                }
                if (!improved) break;
            }
            o["after_mm"] = double.IsNaN(worst) ? (JToken)JValue.CreateNull() : Math.Round(worst, 3);
            o["handles_switched_to_fixed"] = switched;
            o["distance_steps"] = steps;
            o["verified"] = HoldEndsRules.Held(worst);
            if (!HoldEndsRules.Held(worst))
                o["why"] = "the bar's handles were re-pinned and stepped and it is still " +
                           Math.Round(worst, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           " mm off the plan at its worst vertex - Revit would not hold it there.";
            return o;
        }

        // THE CONSTRAINT API CHANGED IN 2025 (the old pair is obsolete there, removed later): candidates are asked for against a target element, and the
        // preferred constraint is set without its handle (read off the 2026 and 2027 RevitAPI.dll).
        private static IList<RebarConstraint> Candidates(RebarConstraintsManager cm, RebarConstrainedHandle h, Rebar bar)
        {
#if REVIT2025 || REVIT2026 || REVIT2027
            return cm.GetConstraintCandidatesForHandle(h, bar.GetHostId());
#else
            return cm.GetConstraintCandidatesForHandle(h);
#endif
        }

        private static void Prefer(RebarConstraintsManager cm, RebarConstrainedHandle h, RebarConstraint c)
        {
#if REVIT2025 || REVIT2026 || REVIT2027
            cm.SetPreferredConstraint(c);
#else
            cm.SetPreferredConstraintForHandle(h, c);
#endif
        }

        private static bool Measure(Rebar bar, IList<double[]> plannedMm, out double worst, out double total)
        {
            List<double[]> drawn = RebarFacts.CentrelinePointsMm(bar, true);
            return HoldEndsRules.Deviation(plannedMm, drawn, out worst, out total);
        }
    }
}

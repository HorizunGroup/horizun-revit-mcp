using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// ACERO 3.0, stage 1c: the constraints Revit holds a bar's handles with, typed (horizun_query_structure
    /// mode=rebar include_constraints). MEASURED on the anonymized regression case (Revit 2024): a column bar declared 6.7 mm inside
    /// the cover of the beam it runs into came back 11.45 mm off its plan, its edge pinned to that beam's cover.
    /// The evidence is which element each handle is pinned to, so the summary names every handle whose
    /// constraint targets an element other than the bar's host. Revit-free: the add-in fills the rows.
    /// </summary>
    public static class RebarConstraintRules
    {
        private const double FtToMm = 304.8;

        /// <summary>One handle as the add-in read it. A null ConstraintType means Revit holds it with none.</summary>
        public sealed class Handle
        {
            public string Name;
            public string ConstraintType;
            public long? TargetElementId;
            public bool ToCover;
            public double? DistanceToHostFaceMm;
            public double? DistanceToCoverMm;
        }

        public sealed class Summary
        {
            public int Handles;
            public int Constrained;
            public int ToCover;
            public List<string> Unconstrained = new List<string>();
            public List<long> OtherTargets = new List<long>();
            public List<string> HandlesOnOtherTargets = new List<string>();
        }

        public static Summary Summarise(long hostId, IList<Handle> handles)
        {
            var s = new Summary();
            foreach (Handle h in handles ?? new List<Handle>())
            {
                s.Handles++;
                if (string.IsNullOrEmpty(h.ConstraintType)) { s.Unconstrained.Add(h.Name); continue; }
                s.Constrained++;
                if (h.ToCover) s.ToCover++;
                if (h.TargetElementId.HasValue && h.TargetElementId.Value != hostId)
                {
                    s.HandlesOnOtherTargets.Add(h.Name);
                    if (!s.OtherTargets.Contains(h.TargetElementId.Value)) s.OtherTargets.Add(h.TargetElementId.Value);
                }
            }
            s.OtherTargets = s.OtherTargets.OrderBy(x => x).ToList();
            return s;
        }

        /// <summary>The distance the constraint itself owns: to the cover for a cover constraint, to the face for a
        /// fixed-distance one, none otherwise (a distance the constraint does not hold is not a fact about it).</summary>
        public static double? Distance(Handle h)
        {
            if (h == null || string.IsNullOrEmpty(h.ConstraintType)) return null;
            if (h.ToCover) return h.DistanceToCoverMm;
            if (h.ConstraintType == "FixedDistanceToHostFace") return h.DistanceToHostFaceMm;
            return null;
        }

        public static double? FeetToMm(double? feet) => feet.HasValue ? Math.Round(feet.Value * FtToMm, 3) : (double?)null;
    }
}

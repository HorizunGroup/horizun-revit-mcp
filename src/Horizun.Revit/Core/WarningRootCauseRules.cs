// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHY two elements triggered an overlap/duplicate-instance warning, not just
// THAT they did. Field session 2026-09-25 on one model: 7 warnings were one
// element wholly inside another, 21 were a wall whose edited profile had been
// left behind by a move (see WallSketchDriftRules), and 18 were a genuine
// vertical overlap of a measured depth - three different repairs, reported by
// Revit as one undifferentiated "elements overlap" list.
//
// Revit-free: given each failing element's own bounding box (mm), category,
// type and whether it is independently known to carry a stranded profile,
// this decides which of the four causes the pair most likely is. It never
// claims more precision than the boxes support - an axis-aligned box is not
// the element's true solid, so "contained" and "vertical_overlap" are read
// from it as a bound, not a proof, and the finding says so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ElementBoxFact
    {
        public long Id;
        public string Category;
        public long TypeId;
        /// <summary>Axis-aligned bounding box, millimetres, model space.</summary>
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        /// <summary>True when this element is independently known (WallSketchDriftRules) to carry an
        /// elevation profile left behind by a move - the usual real cause behind an overlap that
        /// otherwise reads as a coincidence of two ordinary elements.</summary>
        public bool StrandedProfile;
    }

    public static class WarningRootCauseCauses
    {
        public const string ExactDuplicate = "exact_duplicate";
        public const string Contained = "contained";
        public const string VerticalOverlap = "vertical_overlap";
        public const string StrandedProfile = "stranded_profile";
        public const string Unclassified = "unclassified";
    }

    public sealed class WarningRootCause
    {
        public string Cause;
        /// <summary>vertical_overlap only: how many millimetres the two boxes overlap along Z.</summary>
        public double? OverlapMm;
        public string Detail;
    }

    public static class WarningRootCauseRules
    {
        public const double DefaultToleranceMm = 1.0;

        public static readonly string BoxCaveat =
            "read from each element's axis-aligned bounding box, not its true solid: 'contained' and " +
            "'vertical_overlap' are bounds on the real geometry, not a proof of it, and a rotated or " +
            "sloped element can read more generously contained/overlapping than it truly is.";

        /// <summary>
        /// Classifies one warning's failing elements. A stranded profile, when present, is reported as
        /// the cause regardless of how the boxes relate - it is the more actionable, upstream fact:
        /// fixing the drift removes the geometric coincidence that produced the warning in the first
        /// place. Classification by box shape runs only for exactly two elements; a warning naming one
        /// or three-or-more elements is reported unclassified rather than guessed at.
        /// </summary>
        public static WarningRootCause Classify(IList<ElementBoxFact> elements, double toleranceMm = DefaultToleranceMm)
        {
            if (elements == null || elements.Count == 0)
                return new WarningRootCause { Cause = WarningRootCauseCauses.Unclassified, Detail = "no failing elements to classify." };

            ElementBoxFact stranded = elements.FirstOrDefault(e => e.StrandedProfile);
            if (stranded != null)
                return new WarningRootCause
                {
                    Cause = WarningRootCauseCauses.StrandedProfile,
                    Detail = "element " + stranded.Id + " carries an edited profile that a move left behind " +
                             "(see the audit's wall_sketch_drift finding); correcting the drift is expected " +
                             "to resolve this warning."
                };

            if (elements.Count != 2)
                return new WarningRootCause
                {
                    Cause = WarningRootCauseCauses.Unclassified,
                    Detail = elements.Count + " elements are named; box-shape classification covers pairs only."
                };

            ElementBoxFact a = elements[0], b = elements[1];

            if (string.Equals(a.Category, b.Category, StringComparison.Ordinal) && a.TypeId == b.TypeId &&
                BoxesCoincide(a, b, toleranceMm))
                return new WarningRootCause
                {
                    Cause = WarningRootCauseCauses.ExactDuplicate,
                    Detail = "same category, same type, and their bounding boxes coincide within " +
                             toleranceMm.ToString("0.#") + " mm on every axis: two instances standing on top of each other."
                };

            if (Contains(a, b, toleranceMm) || Contains(b, a, toleranceMm))
                return new WarningRootCause
                {
                    Cause = WarningRootCauseCauses.Contained,
                    Detail = "one element's bounding box lies entirely within the other's."
                };

            double xyOverlap = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            double yOverlap = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            double zOverlap = Math.Min(a.MaxZ, b.MaxZ) - Math.Max(a.MinZ, b.MinZ);
            if (xyOverlap > toleranceMm && yOverlap > toleranceMm && zOverlap > toleranceMm)
                return new WarningRootCause
                {
                    Cause = WarningRootCauseCauses.VerticalOverlap,
                    OverlapMm = zOverlap,
                    Detail = "their footprints intersect and their elevation ranges overlap by " +
                             zOverlap.ToString("0.#") + " mm."
                };

            return new WarningRootCause
            {
                Cause = WarningRootCauseCauses.Unclassified,
                Detail = "neither a duplicate, a containment nor a measurable vertical overlap of their " +
                         "bounding boxes; Revit's own warning stands without a box-level explanation."
            };
        }

        private static bool BoxesCoincide(ElementBoxFact a, ElementBoxFact b, double tol) =>
            Math.Abs(a.MinX - b.MinX) <= tol && Math.Abs(a.MaxX - b.MaxX) <= tol &&
            Math.Abs(a.MinY - b.MinY) <= tol && Math.Abs(a.MaxY - b.MaxY) <= tol &&
            Math.Abs(a.MinZ - b.MinZ) <= tol && Math.Abs(a.MaxZ - b.MaxZ) <= tol;

        /// <summary>True when outer's box entirely contains inner's, within tolerance.</summary>
        private static bool Contains(ElementBoxFact outer, ElementBoxFact inner, double tol) =>
            outer.MinX - tol <= inner.MinX && inner.MaxX <= outer.MaxX + tol &&
            outer.MinY - tol <= inner.MinY && inner.MaxY <= outer.MaxY + tol &&
            outer.MinZ - tol <= inner.MinZ && inner.MaxZ <= outer.MaxZ + tol &&
            // A box does not "contain" a near-identical copy of itself - that is the duplicate case,
            // classified earlier so this is reached only when they differ - but guard it here too so
            // this function is correct when called on its own.
            !BoxesCoincide(outer, inner, tol);
    }
}

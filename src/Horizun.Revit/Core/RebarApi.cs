// -----------------------------------------------------------------------------
// Horizun Revit MCP - the ONE file that knows which Revit year this is.
// Original Horizun code.
//
// MEASURED, in docs/STRUCTURAL-API-MATRIX.json, by reflection over the five
// installed RevitAPI.dll: there is no overload of Rebar.CreateFromCurves or
// Rebar.CreateFromCurvesAndShape that exists in all five supported years.
//
//   2023, 2024, 2025 : RebarHookType + RebarHookOrientation only
//   2026             : BOTH - and the hook forms carry [Obsolete]
//   2027             : BarTerminationsData only; RebarHookOrientation is GONE
//
// 2026 is the only year where both exist, and 2026 is the machine this is
// developed on. That is the trap: whichever form is chosen, everything works
// here and breaks on somebody else's Revit. The repository already has one file
// that absorbs a version difference so no other file has to - Rid.cs, for the
// 32-to-64-bit ElementId change - and this is the second.
//
// THE RULE FOR THIS FILE: it contains no decisions. No defaults, no fallbacks,
// no inference, no "if the caller did not say, assume". It spells one call two
// ways and maps one vocabulary. Anything that could be WRONG rather than merely
// version-specific belongs in RebarLayoutRules or RebarPlanRules, where it can
// be proved at a desk. If this file is ever tempted to branch on something other
// than the compilation constant, that branch is in the wrong file.
//
// ADR-002 records the decision and what was rejected.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace Horizun.Revit.Core
{
    public static class RebarApi
    {
        /// <summary>
        /// Horizun's own words for which side of the bar a hook turns to. Revit
        /// spells this RebarHookOrientation up to 2026 and RebarTerminationOrientation
        /// from 2026, and publishing Revit's spelling would mean a requirement set
        /// written by an engineer stopped being portable between two Revits for a
        /// reason that has nothing to do with the building.
        /// </summary>
        public const string OrientationLeft = "left";
        public const string OrientationRight = "right";
        public static readonly string[] Orientations = { OrientationLeft, OrientationRight };

        public static bool IsKnownOrientation(string s)
        {
            return string.Equals(s, OrientationLeft, StringComparison.Ordinal)
                || string.Equals(s, OrientationRight, StringComparison.Ordinal);
        }

        /// <summary>
        /// True where this Revit exposes the terminations API. Published so a reply
        /// can say WHICH half of the matrix answered, rather than leaving a caller
        /// to infer it from a version number.
        /// </summary>
        public static bool HasTerminationsApi
        {
            get
            {
#if REVIT2026 || REVIT2027
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>The API generation this add-in was compiled against, for the reply.</summary>
        public static string ApiGeneration
        {
            get { return HasTerminationsApi ? "bar_terminations_data" : "hook_type_and_orientation"; }
        }

        // ------------------------------------------------------------- create

        /// <summary>
        /// A bar of an EXPLICIT shape. Both hook type ids may be
        /// <see cref="ElementId.InvalidElementId"/>, which means no hook - the two
        /// APIs spell that differently (a null object, an invalid id) and the
        /// caller should not have to know which.
        /// </summary>
        public static Rebar CreateFromCurvesAndShape(
            Document doc, RebarShape shape, RebarBarType barType, Element host, XYZ norm,
            IList<Curve> curves, ElementId startHookTypeId, ElementId endHookTypeId,
            string startOrientation, string endOrientation)
        {
            RequireOrientation(startOrientation, "start");
            RequireOrientation(endOrientation, "end");
#if REVIT2026 || REVIT2027
            using (var t = new BarTerminationsData(doc))
            {
                t.HookTypeIdAtStart = startHookTypeId ?? ElementId.InvalidElementId;
                t.HookTypeIdAtEnd = endHookTypeId ?? ElementId.InvalidElementId;
                t.TerminationOrientationAtStart = Termination(startOrientation);
                t.TerminationOrientationAtEnd = Termination(endOrientation);
                return Rebar.CreateFromCurvesAndShape(doc, shape, barType, host, norm, curves, t);
            }
#else
            return Rebar.CreateFromCurvesAndShape(
                doc, shape, barType,
                HookType(doc, startHookTypeId), HookType(doc, endHookTypeId),
                host, norm, curves,
                Legacy(startOrientation), Legacy(endOrientation));
#endif
        }

        /// <summary>
        /// A bar whose shape Revit is asked to match or create. `createNewShape` is
        /// passed straight through and is never defaulted here: creating a shape
        /// family behind somebody's back is a decision, and decisions do not live
        /// in this file.
        /// </summary>
        public static Rebar CreateFromCurves(
            Document doc, RebarStyle style, RebarBarType barType, Element host, XYZ norm,
            IList<Curve> curves, ElementId startHookTypeId, ElementId endHookTypeId,
            string startOrientation, string endOrientation,
            bool useExistingShapeIfPossible, bool createNewShape)
        {
            RequireOrientation(startOrientation, "start");
            RequireOrientation(endOrientation, "end");
#if REVIT2026 || REVIT2027
            using (var t = new BarTerminationsData(doc))
            {
                t.HookTypeIdAtStart = startHookTypeId ?? ElementId.InvalidElementId;
                t.HookTypeIdAtEnd = endHookTypeId ?? ElementId.InvalidElementId;
                t.TerminationOrientationAtStart = Termination(startOrientation);
                t.TerminationOrientationAtEnd = Termination(endOrientation);
                return Rebar.CreateFromCurves(doc, style, barType, host, norm, curves, t,
                                              useExistingShapeIfPossible, createNewShape);
            }
#else
            return Rebar.CreateFromCurves(
                doc, style, barType,
                HookType(doc, startHookTypeId), HookType(doc, endHookTypeId),
                host, norm, curves,
                Legacy(startOrientation), Legacy(endOrientation),
                useExistingShapeIfPossible, createNewShape);
#endif
        }

        // ------------------------------------------------------- read it back

        /// <summary>
        /// Which way the termination at this end turns, in Horizun's words. Null
        /// when the model would not answer - never a default, because "left" and
        /// "could not read" are different findings.
        /// </summary>
        public static string ReadOrientation(Rebar bar, int end)
        {
            if (bar == null) return null;
            try
            {
#if REVIT2026 || REVIT2027
                RebarTerminationOrientation o = bar.GetTerminationOrientation(end);
                return o == RebarTerminationOrientation.Left ? OrientationLeft : OrientationRight;
#else
                RebarHookOrientation o = bar.GetHookOrientation(end);
                return o == RebarHookOrientation.Left ? OrientationLeft : OrientationRight;
#endif
            }
            catch { return null; }
        }

        /// <summary>Set the termination orientation at one end. Throws on an unknown word rather than picking one.</summary>
        public static void WriteOrientation(Rebar bar, int end, string orientation)
        {
            if (bar == null) throw new ArgumentNullException("bar");
            if (!IsKnownOrientation(orientation))
                throw new ArgumentException("orientation must be 'left' or 'right' - got '" + orientation + "'.");
#if REVIT2026 || REVIT2027
            bar.SetTerminationOrientation(end, Termination(orientation));
#else
            bar.SetHookOrientation(end, Legacy(orientation));
#endif
        }

        // ------------------------------------------------------------- flip

        /// <summary>
        /// Flip the SET about its distribution path. Measured: FlipRebarSet does
        /// not exist in 2023. 2023 has FlipRebar, which flips the BAR - a different
        /// operation - so this refuses there rather than doing the other thing.
        /// </summary>
        public static bool TryFlipSet(RebarShapeDrivenAccessor accessor, out string why)
        {
#if REVIT2023
            why = "flipping a rebar SET needs RebarShapeDrivenAccessor.FlipRebarSet, which Revit 2023 does not " +
                  "have. 2023 has FlipRebar, which flips the bar rather than the set - a different operation, " +
                  "so it is not substituted here. Declare the normal you want instead.";
            return false;
#else
            if (accessor == null) { why = "no shape-driven accessor on this bar."; return false; }
            accessor.FlipRebarSet();
            why = null;
            return true;
#endif
        }

        /// <summary>
        /// A word that is not in the vocabulary is refused, not defaulted.
        ///
        /// Both mappers below read "right" and treat everything else as "left",
        /// which for a null or a typo means a hook silently turning the wrong way -
        /// in a file whose whole charter is that it carries no defaults. The write
        /// path validated and threw; the CREATE path did not, and the create path is
        /// the one that puts steel in a building.
        /// </summary>
        private static void RequireOrientation(string s, string which)
        {
            if (!IsKnownOrientation(s))
                throw new ArgumentException(
                    "the " + which + " termination orientation must be '" + OrientationLeft + "' or '" +
                    OrientationRight + "' - got " + (s == null ? "null" : "'" + s + "'") +
                    ". This is not defaulted: a hook turning the wrong way is not a detail.");
        }

        // ------------------------------------------------------------ mapping

#if REVIT2026 || REVIT2027
        private static RebarTerminationOrientation Termination(string s)
        {
            // Measured in every probed year: Left = 1, Right = -1, in BOTH enums.
            return string.Equals(s, OrientationRight, StringComparison.Ordinal)
                ? RebarTerminationOrientation.Right
                : RebarTerminationOrientation.Left;
        }
#else
        private static RebarHookOrientation Legacy(string s)
        {
            return string.Equals(s, OrientationRight, StringComparison.Ordinal)
                ? RebarHookOrientation.Right
                : RebarHookOrientation.Left;
        }

        private static RebarHookType HookType(Document doc, ElementId id)
        {
            // No hook is a null object in this generation of the API and an invalid
            // id in the other one.
            if (doc == null || id == null || id == ElementId.InvalidElementId) return null;
            return doc.GetElement(id) as RebarHookType;
        }
#endif
    }
}

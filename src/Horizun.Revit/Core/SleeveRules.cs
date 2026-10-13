// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// SLEEVES AND STRUCTURAL OPENINGS - the pure, Revit-free half of
// horizun_resolve_clash's propose_opening / apply_opening. Used when a clash
// between an MEP run and a wall/floor/roof/framing/column cannot be resolved by
// moving the run (ClashResolveRules already covers the move path): instead of
// relocating anything, the run keeps its line and the HOST gets an opening (or,
// for framing/columns, a caller-supplied sleeve family only - by scope, see
// RouteSleeveOnly).
//
//   * THE CROSSING. The command layer intersects the run's centreline with the
//     host's SOLIDS (Solid.IntersectWithCurve, segments inside); the host's
//     bounding box is only a prefilter here (LineBoxIntersect), never the
//     crossing itself - a rotated wall's box is not the wall.
//   * THE SIZE. The run's OUTER section (outside diameter + insulation, read by
//     the command layer) projected through the host along the run's direction:
//     FootprintHalfExtent gives, per opening axis, how far the run's material
//     reaches from the crossing across the host's full thickness - a skewed
//     crossing is wider than the pipe (section / cos + thickness * tan). The
//     opening is twice that plus clearance_mm.
//   * THE ROUTE. Which Revit API call the command layer uses is a property of
//     the HOST's kind: a wall gets a rectangular NewOpening(wall, pt1, pt2); a
//     floor/roof/ceiling gets a boundary-loop NewOpening(host, CurveArray,
//     false) - a VERTICAL cut, so a vertical run through a sloped roof gets a
//     vertical hole; framing and columns get a sleeve family only.
//   * THE CHECK. The command layer verifies the clearance on solids after the
//     commit (a clearance envelope around the run must not meet the host or the
//     sleeve); nothing in this file claims a clearance it did not size.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SleeveRules
    {
        /// <summary>
        /// Categories whose elements are structural by what they ARE, not by a flag: a
        /// framing member, a column, a foundation. A footing cut for a sleeve without the
        /// structural opt-in was the gap (audit 2026-10-08, 6.14).
        /// </summary>
        public static bool IsStructuralCategory(string builtInCategory) =>
            builtInCategory == "OST_StructuralFraming" || builtInCategory == "OST_StructuralColumns" ||
            builtInCategory == "OST_StructuralFoundation";

        public const string ShapeRound = "round";
        public const string ShapeRect = "rect";

        public const string HostWall = "wall";
        public const string HostFloor = "floor";
        public const string HostRoof = "roof";
        public const string HostCeiling = "ceiling";
        public const string HostFramingOrColumn = "framing_or_column";
        public const string HostUnsupported = "unsupported";

        /// <summary>doc.Create.NewOpening(Wall, XYZ, XYZ) - a rectangular cut through a wall's full thickness.</summary>
        public const string RouteWallOpening = "wall_opening";
        /// <summary>doc.Create.NewOpening(HostObject, CurveArray, false) - a vertical boundary-loop cut through a floor/roof/ceiling.</summary>
        public const string RouteFloorOpening = "floor_opening";
        /// <summary>
        /// Framing/columns: only a caller-supplied sleeve family instance. The API CAN cut a
        /// beam, brace or column (Creation.Document.NewOpening(Element, CurveArray, eRefFace));
        /// this operation deliberately does not offer that cut - cutting a structural member is
        /// an engineer's sized penetration, not a clash fix.
        /// </summary>
        public const string RouteSleeveOnly = "sleeve_only";
        /// <summary>Neither a cut nor a documented sleeve route exists for this host kind.</summary>
        public const string RouteRefused = "refused";

        public const string CodeNoCrossing = "run_does_not_cross_host";
        public const string CodeParallel = "run_parallel_to_host_face";
        public const string CodeNoProfile = "run_has_no_profile";
        public const string CodeNoHostBox = "host_has_no_bounding_box";
        public const string CodeHostSolidUnreadable = "host_solid_unreadable";
        public const string CodeHostUnsupported = "host_kind_not_supported";
        public const string CodeCurvedWall = "curved_wall_not_supported";
        public const string CodeTooSteepForWall = "run_too_steep_for_wall_opening";
        public const string CodeTooFlatForFloor = "run_too_flat_for_floor_opening";
        public const string CodeNothingToCut = "nothing_to_cut";
        public const string CodeGeometryChanged = "geometry_changed_since_propose";
        /// <summary>Framing/columns: a cut is not offered by this operation (scope, not an API limit), so it is refused by name.</summary>
        public const string CodeCutRefused = "member_cut_not_offered";

        /// <summary>A floor/roof/ceiling opening is planned only for a run at least this steep (|unit z|): 30 degrees above horizontal.</summary>
        public const double MinVerticalComponentForFloorOpening = 0.5;
        /// <summary>Below this |cos| between the run and the host normal the skewed footprint explodes; refused as parallel.</summary>
        public const double MinNormalComponent = 0.25;
        /// <summary>The clearance envelope is shrunk by this much so an exact-fit cut passes and anything short by more fails.</summary>
        public const double EnvelopeToleranceMm = 0.5;
        /// <summary>A proposal's bound geometry may drift this much (mm) before apply refuses it as changed.</summary>
        public const double GeometryDriftToleranceMm = 1.0;

        /// <summary>The host kind that decides the route.</summary>
        public static string HostKindOf(string builtInCategory)
        {
            switch (builtInCategory)
            {
                case "OST_Walls": return HostWall;
                case "OST_Floors": return HostFloor;
                case "OST_Roofs": return HostRoof;
                case "OST_Ceilings": return HostCeiling;
                case "OST_StructuralFraming":
                case "OST_StructuralColumns":
                case "OST_Columns":
                    return HostFramingOrColumn;
                default: return HostUnsupported;
            }
        }

        /// <summary>The Revit route for a host kind - never guesses; an unmapped kind is refused.</summary>
        public static string RouteFor(string hostKind)
        {
            switch (hostKind)
            {
                case HostWall: return RouteWallOpening;
                case HostFloor:
                case HostRoof:
                case HostCeiling:
                    return RouteFloorOpening;
                case HostFramingOrColumn: return RouteSleeveOnly;
                default: return RouteRefused;
            }
        }

        /// <summary>
        /// Direction gate per route, on the run's unit direction: a wall opening only for a run
        /// no steeper than PenetrationRules.MaxVerticalComponentForWallOpening, a floor/roof/
        /// ceiling opening only for a run at least MinVerticalComponentForFloorOpening steep.
        /// Null when the route accepts the direction, otherwise the refusal code.
        /// </summary>
        public static string DirectionRefusal(string route, double[] runDirection)
        {
            double[] u = Unit(runDirection);
            if (u == null) return CodeParallel;
            if (route == RouteWallOpening && Math.Abs(u[2]) > PenetrationRules.MaxVerticalComponentForWallOpening) return CodeTooSteepForWall;
            if (route == RouteFloorOpening && Math.Abs(u[2]) < MinVerticalComponentForFloorOpening) return CodeTooFlatForFloor;
            return null;
        }

        /// <summary>
        /// A 3D segment (mm) clipped against an axis-aligned box (Liang-Barsky slab method,
        /// clipped to the segment's own [0,1] as well). Used ONLY as a cheap prefilter before the
        /// solid intersection: a false return means the run cannot reach the host at all.
        /// </summary>
        public static bool LineBoxIntersect(double[] startMm, double[] endMm, ResolveBox box,
                                            out double[] entryMm, out double[] exitMm, out string code)
        {
            entryMm = null; exitMm = null; code = null;
            if (startMm == null || endMm == null || box == null) { code = CodeNoHostBox; return false; }
            double[] d = { endMm[0] - startMm[0], endMm[1] - startMm[1], endMm[2] - startMm[2] };
            double[] bmin = { box.MinX, box.MinY, box.MinZ };
            double[] bmax = { box.MaxX, box.MaxY, box.MaxZ };
            double tmin = 0, tmax = 1;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(d[i]) < 1e-9)
                {
                    if (startMm[i] < bmin[i] - 1e-6 || startMm[i] > bmax[i] + 1e-6) { code = CodeParallel; return false; }
                    continue;
                }
                double t1 = (bmin[i] - startMm[i]) / d[i];
                double t2 = (bmax[i] - startMm[i]) / d[i];
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tmin) tmin = t1;
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) { code = CodeNoCrossing; return false; }
            }
            entryMm = new[] { startMm[0] + tmin * d[0], startMm[1] + tmin * d[1], startMm[2] + tmin * d[2] };
            exitMm = new[] { startMm[0] + tmax * d[0], startMm[1] + tmax * d[1], startMm[2] + tmax * d[2] };
            return true;
        }

        /// <summary>Midpoint of two mm points - the crossing point a sleeve/opening centres on.</summary>
        public static double[] Midpoint(double[] a, double[] b) =>
            new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 };

        /// <summary>
        /// Opening/sleeve size for a run crossing its host square-on: the run's own outer
        /// cross-section plus clearance_mm. Kept for the perpendicular case; a skewed crossing is
        /// sized by FootprintHalfExtent.
        /// </summary>
        public static void OpeningSize(double runWidthMm, double runHeightMm, double clearanceMm, string runShape,
                                       out double openingWidthMm, out double openingHeightMm, out string shape)
        {
            bool round = runShape == "round";
            openingWidthMm = Math.Max(0, runWidthMm) + Math.Max(0, clearanceMm);
            openingHeightMm = round ? openingWidthMm : Math.Max(0, runHeightMm) + Math.Max(0, clearanceMm);
            shape = round ? ShapeRound : ShapeRect;
        }

        /// <summary>
        /// How far (mm) the run's material reaches from the crossing along the opening axis `a`
        /// (unit, in the host's plane, perpendicular to the host normal `n`), over the host's full
        /// `thicknessMm` measured along `n`. The run is `u`; its section is a circle of radius
        /// `sectionHalfMm` when `round`, otherwise a square of half-side `sectionHalfMm` on the
        /// section axes `e1`/`e2` (null axes: the circle circumscribing that square - an
        /// unknown section rotation is sized for every rotation). Derivation: the section
        /// projected along u onto the host plane has half-extent max over the section of x.v with
        /// v = a - n (u.a)/(u.n) (v is perpendicular to u), i.e. r|v| = r sqrt(1 + k^2) for a circle, k = (u.a)/(u.n);
        /// across the thickness the centre drifts t|k|, half of it on each side of the crossing.
        /// NaN when the run is (nearly) parallel to the host face.
        /// </summary>
        public static double FootprintHalfExtent(double[] u, double[] n, double[] a, double[] e1, double[] e2,
                                                 bool round, double sectionHalfMm, double thicknessMm)
        {
            u = Unit(u); n = Unit(n); a = Unit(a);
            if (u == null || n == null || a == null) return double.NaN;
            double un = Dot(u, n);
            if (Math.Abs(un) < MinNormalComponent) return double.NaN;
            double k = Dot(u, a) / un;
            double[] v = { a[0] - n[0] * k, a[1] - n[1] * k, a[2] - n[2] * k };
            double[] x1 = Unit(e1), x2 = Unit(e2);
            double section = round ? sectionHalfMm * Norm(v)
                           : (x1 == null || x2 == null) ? sectionHalfMm * Math.Sqrt(2) * Norm(v)
                           : sectionHalfMm * (Math.Abs(Dot(v, x1)) + Math.Abs(Dot(v, x2)));
            return section + Math.Max(0, thicknessMm) * Math.Abs(k) / 2;
        }

        /// <summary>
        /// Default section axes for a run: e1 horizontal and perpendicular to the run (plan X for a
        /// vertical run), e2 = u x e1. Used when the connector's own coordinate system is unreadable.
        /// </summary>
        public static void DefaultSectionAxes(double[] runDirection, out double[] e1, out double[] e2)
        {
            double[] u = Unit(runDirection) ?? new double[] { 1, 0, 0 };
            e1 = Math.Abs(u[2]) > 0.99 ? new double[] { 1, 0, 0 } : Unit(Cross(u, new double[] { 0, 0, 1 }));
            e2 = Unit(Cross(u, e1));
        }

        /// <summary>
        /// Does a proposal's bound geometry still match the live re-derivation? Crossing point and
        /// both opening sizes within GeometryDriftToleranceMm, same shape.
        /// </summary>
        public static bool SameGeometry(double[] boundCrossing, double boundWidth, double boundHeight, string boundShape,
                                        double[] liveCrossing, double liveWidth, double liveHeight, string liveShape)
        {
            if (boundCrossing == null || liveCrossing == null || boundCrossing.Length < 3 || liveCrossing.Length < 3) return false;
            if (!string.Equals(boundShape, liveShape, StringComparison.Ordinal)) return false;
            for (int i = 0; i < 3; i++) if (Math.Abs(boundCrossing[i] - liveCrossing[i]) > GeometryDriftToleranceMm) return false;
            return Math.Abs(boundWidth - liveWidth) <= GeometryDriftToleranceMm && Math.Abs(boundHeight - liveHeight) <= GeometryDriftToleranceMm;
        }

        /// <summary>
        /// New clash pairs after an opening/sleeve, minus the one that is the design itself: a
        /// sleeve sitting inside its host (created~host) is the point of a sleeve, not a new
        /// clash. Anything else new - the sleeve against the run, against a third element, or
        /// the run against anything - stays in the list and fails the apply.
        /// </summary>
        public static List<string> UnintendedNewPairs(IEnumerable<string> before, IEnumerable<string> after, long createdId, long hostId)
        {
            string intended = ClashResolveRules.PairKey(createdId, hostId);
            return ClashResolveRules.NewPairs(before, after).Where(p => p != intended).ToList();
        }

        public static double[] Unit(double[] d)
        {
            if (d == null || d.Length < 3) return null;
            double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            return len < 1e-9 ? null : new[] { d[0] / len, d[1] / len, d[2] / len };
        }

        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        public static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double Norm(double[] v) => Math.Sqrt(Dot(v, v));

        public static string Describe(string hostKind, double widthMm, double heightMm, string shape) =>
            shape + " opening " + widthMm.ToString("0.#", CultureInfo.InvariantCulture) + "x" +
            heightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm on " + hostKind;
    }
}

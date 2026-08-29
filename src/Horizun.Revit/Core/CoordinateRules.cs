// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHERE THE MODEL THINKS IT IS. Revit has three points that people routinely
// confuse, and the confusion is expensive:
//
//   INTERNAL ORIGIN   Revit's own (0,0,0). Not movable, not a user decision, and
//                     the thing everything else is measured from. Geometry far
//                     from IT is the accuracy problem.
//   PROJECT BASE POINT  where the project's coordinate system starts.
//   SURVEY POINT      where the site's real-world coordinate system starts.
//
// THE MOST COMMON FALSE POSITIVE IN THIS WHOLE AREA is reading a survey point
// ten kilometres away and reporting the model as "far from origin". A survey
// point at the national grid coordinate of the site is CORRECT - that is what it
// is for. What matters is where the GEOMETRY is, measured from the internal
// origin, and these rules keep the two apart by construction: the distance
// findings take element positions and never a control point.
//
// Everything here is Revit-free so the decisions can be proved at a desk. The
// reading lives in the command; the judgement lives here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>One control point as the model reports it, or why it could not be read.</summary>
    public sealed class PointFact
    {
        public string Name;
        public bool Readable;
        public string Why;
        public double XMm, YMm, ZMm;
        /// <summary>Null when the model does not answer. A clipped point does not move with shared coordinates.</summary>
        public bool? Clipped;

        public double DistanceFromInternalOriginMm
        {
            get { return Math.Sqrt(XMm * XMm + YMm * YMm + ZMm * ZMm); }
        }
    }

    /// <summary>An element that sits a long way from the internal origin.</summary>
    public sealed class OutlierFact
    {
        public long ElementId;
        public string Category;
        public string Name;
        public double DistanceMm;
    }

    /// <summary>A link, and how its placement relates to the host's.</summary>
    public sealed class LinkPlacementFact
    {
        public long InstanceId;
        public string Name;
        public bool TransformReadable;
        public string Why;
        public bool HasRotation;
        public bool HasReflection;
        public double OriginOffsetMm;
        /// <summary>Null when the link would not say. True means it and the host agree about shared coordinates.</summary>
        public bool? SharedPositionMatchesHost;
    }

    /// <summary>Everything the command read. Nothing here is judged; that happens below.</summary>
    public sealed class CoordinateFacts
    {
        public PointFact InternalOrigin;
        public PointFact ProjectBasePoint;
        public PointFact SurveyPoint;

        // bool?, NOT bool. Three states, and collapsing them loses the one that
        // matters: null is "nobody asked", false is "asked and the model would not
        // say", true is "read". A fresh facts object with bool fields answers
        // "false" to everything, which reads as a model missing its true north when
        // in fact nothing has looked yet.
        public bool? LocationReadable;
        public string LocationWhy;
        public string ActiveLocationName;
        public bool? TrueNorthReadable;
        public double? TrueNorthDegrees;

        /// <summary>How many named project locations the document holds. One is ordinary.</summary>
        public int? NamedLocationCount;

        public string LengthUnitName;
        public bool? UnitsReadable;

        public long ElementsMeasured;
        public long ElementsUnreadable;
        public double? FarthestElementMm;
        public List<OutlierFact> Outliers = new List<OutlierFact>();
        public List<LinkPlacementFact> Links = new List<LinkPlacementFact>();
    }

    /// <summary>The names this area publishes. One list, so the gate and the audit cannot drift.</summary>
    public static class CoordinateCheckParts
    {
        public const string ControlPoints = "control_points";
        public const string ElementsFarFromOrigin = "elements_far_from_origin";
        public const string LinksReflected = "links_reflected";
        public const string LinksRotated = "links_rotated";
        public const string LinksNotSharingPosition = "links_not_sharing_position";
    }

    public static class CoordinateRules
    {
        /// <summary>
        /// The default radius beyond which geometry is far enough from the internal
        /// origin to cost accuracy. Ten miles is Revit's own documented limit and is
        /// where its arithmetic degrades; this is deliberately well inside it,
        /// because a model that has drifted that far has usually drifted by mistake.
        /// It is a DEFAULT, overridable by the caller - no standard is compiled in.
        /// </summary>
        public const double DefaultFarRadiusMm = 1000.0 * 1000.0;   // 1 km

        public const string ToleranceFarRadius = "origin_distance_mm";
        public const string ToleranceLinkOriginOffset = "link_origin_offset_mm";

        /// <summary>
        /// The per-item answers for a `require_coordinate_facts` requirement. Each
        /// is "was this readable", NOT "is this value good" - a base point at an
        /// unusual place is a decision somebody made, and this bridge does not
        /// grade decisions it was not given a standard for.
        /// </summary>
        public static Dictionary<string, GateItemMeasurement> ReadabilityItems(CoordinateFacts f)
        {
            var items = new Dictionary<string, GateItemMeasurement>(StringComparer.Ordinal);
            AddPoint(items, "internal_origin", f == null ? null : f.InternalOrigin);
            AddPoint(items, "project_base_point", f == null ? null : f.ProjectBasePoint);
            AddPoint(items, "survey_point", f == null ? null : f.SurveyPoint);

            items["project_location"] = new GateItemMeasurement
            {
                Name = "project_location",
                Satisfied = f == null ? (bool?)null : f.LocationReadable,
                Detail = f == null ? "not collected"
                       : f.LocationReadable == null ? "not collected"
                       : f.LocationReadable.Value ? ("active location '" + (f.ActiveLocationName ?? "(unnamed)") + "'")
                       : (f.LocationWhy ?? "the document would not report a project location")
            };
            items["true_north"] = new GateItemMeasurement
            {
                Name = "true_north",
                Satisfied = f == null ? (bool?)null : f.TrueNorthReadable,
                Detail = f == null || f.TrueNorthReadable == null ? "not collected"
                       : f.TrueNorthDegrees.HasValue ? (Fmt(f.TrueNorthDegrees.Value) + " degrees")
                       : "the document would not report an angle to true north"
            };
            items["length_units"] = new GateItemMeasurement
            {
                Name = "length_units",
                Satisfied = f == null ? (bool?)null : f.UnitsReadable,
                Detail = f == null || f.UnitsReadable == null ? "not collected"
                       : f.UnitsReadable.Value ? f.LengthUnitName
                       : "the document would not report its length unit"
            };
            return items;
        }

        private static void AddPoint(Dictionary<string, GateItemMeasurement> into, string name, PointFact p)
        {
            into[name] = new GateItemMeasurement
            {
                Name = name,
                Satisfied = p == null ? (bool?)null : p.Readable,
                Detail = p == null ? "not collected"
                       : p.Readable ? (Fmt(p.XMm) + ", " + Fmt(p.YMm) + ", " + Fmt(p.ZMm) + " mm from the internal origin")
                       : p.Why
            };
        }

        /// <summary>
        /// How far the model's GEOMETRY sits from the internal origin, and which
        /// elements are the outliers.
        ///
        /// This deliberately takes element positions and nothing else. Feeding it a
        /// survey point would reproduce the single most common false positive in
        /// this area: a survey point at a national grid coordinate is correct, and a
        /// tool that calls it "geometry 10 km from origin" has misread the model
        /// rather than found a problem.
        /// </summary>
        public static long CountBeyond(IEnumerable<OutlierFact> elements, double radiusMm, out double? farthestMm)
        {
            farthestMm = null;
            if (elements == null) return 0;
            long n = 0;
            foreach (OutlierFact e in elements)
            {
                if (e == null || double.IsNaN(e.DistanceMm) || double.IsInfinity(e.DistanceMm)) continue;
                if (!farthestMm.HasValue || e.DistanceMm > farthestMm.Value) farthestMm = e.DistanceMm;
                if (e.DistanceMm > radiusMm) n++;
            }
            return n;
        }

        /// <summary>
        /// The sentence that keeps a reader from the false positive, published beside
        /// the numbers rather than left in documentation nobody opens.
        /// </summary>
        public const string DistanceMeans =
            "measured from the INTERNAL ORIGIN to each element, never from a control point. A survey point far " +
            "from the internal origin is normal and is what a survey point is for; geometry far from it is the " +
            "accuracy problem. These two are different questions and this number answers only the second.";

        public static string OriginNote(long beyond, double radiusMm, long measured, long unreadable)
        {
            if (measured == 0)
                return "no element position could be measured, so nothing is known about where the geometry sits.";
            string s = beyond == 0
                ? ("every one of " + measured + " measured element(s) is within " + Fmt(radiusMm) +
                   " mm of the internal origin.")
                : (beyond + " of " + measured + " measured element(s) sit further than " + Fmt(radiusMm) +
                   " mm from the internal origin.");
            if (unreadable > 0)
                s += " " + unreadable + " element(s) would not report a position, so this count is a LOWER BOUND.";
            return s;
        }

        /// <summary>
        /// Links whose placement differs from the host's in a way that changes what
        /// a reader sees. A rotation or a reflection is reported separately from an
        /// offset because they mean different things: an offset is usually a
        /// placement decision, a REFLECTION is almost never intentional and turns
        /// every text in the link backwards.
        /// </summary>
        public static void TallyLinks(IEnumerable<LinkPlacementFact> links, double originOffsetToleranceMm,
                                      out long reflected, out long rotated, out long offset,
                                      out long notSharingPosition, out long unreadable)
        {
            reflected = 0; rotated = 0; offset = 0; notSharingPosition = 0; unreadable = 0;
            if (links == null) return;
            foreach (LinkPlacementFact l in links)
            {
                if (l == null) continue;
                if (!l.TransformReadable) { unreadable++; continue; }
                if (l.HasReflection) reflected++;
                if (l.HasRotation) rotated++;
                if (l.OriginOffsetMm > originOffsetToleranceMm) offset++;
                // NULL IS NOT FALSE. A link that would not say whether it shares the
                // host's position has not told us it does not.
                if (l.SharedPositionMatchesHost.HasValue && !l.SharedPositionMatchesHost.Value) notSharingPosition++;
            }
        }

        internal static string Fmt(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

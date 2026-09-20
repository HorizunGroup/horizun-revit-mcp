// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// MEASURED, NOT NOMINAL. And a real route, not a straight line.
//
// horizun_audit_access shipped saying two things about itself, both true and one
// of them a dead end that should never have been one:
//
//   "a door width is NOMINAL" — the type's parameter, not the gap somebody walks
//   through.
//   "a distance to an exit is a STRAIGHT LINE THAT CROSSES WALLS, never a travel
//   distance."
//
// The second was recorded as a limit of the API. IT IS NOT.
// Autodesk.Revit.DB.Analysis.PathOfTravel.FindShortestPaths exists in every Revit
// this bridge supports — 2023 through 2027 — and computes real routes around real
// obstacles, returning each path as a list of points. It creates no elements and
// opens no transaction. "The API has no property for it" was true and was never
// the same statement as "it cannot be obtained".
//
// WHAT THE ROUTE COSTS, said here rather than discovered:
//
//   IT NEEDS A FLOOR PLAN VIEW. The calculation is two-dimensional, per level, in
//   a specific plan. Which plan is a choice with consequences: obstacles are what
//   THAT view shows, so a plan with furniture hidden measures a building with no
//   furniture in it.
//   THE DESTINATIONS' Z IS IGNORED and replaced with the view's level elevation.
//   Destinations on another level are silently flattened onto this one.
//   AN EMPTY PATH MEANS NO ROUTE, which is a finding: a room with no way out is
//   the most important thing this measurement can say, and it arrives as an empty
//   array rather than as an error.
//   IT IS NOT AVAILABLE EVERYWHERE. The service can be absent; that is reported
//   as a coverage gap, never as a passing check.
//   AND THE ARGUMENT ORDER IS (view, DESTINATIONS, STARTS). Backwards, against
//   every expectation, and silently plausible when swapped — which is why it is
//   wrapped here exactly once.
//
// THE WIDTH IS HONEST ABOUT BEING SOMETHING ELSE. Geometry gives the ROUGH
// OPENING in the wall: more than the type's nominal parameter, less than a clear
// width, which subtracts the leaf and the hardware and is defined at a door
// opened ninety degrees. Reporting it as "clear width" would trade a known
// approximation for an unknown one, so it is reported as what it is and the
// difference is stated in the same row.
//
// EVERY MEASUREMENT CARRIES ITS OWN UNCERTAINTY AND ITS OWN COVERAGE. A check
// that could not be measured says so instead of falling back to the nominal value
// and reading as measured.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>How a number was arrived at. Never inferred from the number itself.</summary>
    public static class MeasurementBasis
    {
        /// <summary>A type or instance parameter. What the family says it is.</summary>
        public const string Nominal = "nominal_parameter";

        /// <summary>Taken off the element's own solids.</summary>
        public const string Geometry = "measured_geometry";

        /// <summary>Computed by Revit's path-of-travel service around real obstacles.</summary>
        public const string TravelPath = "measured_travel_path";

        /// <summary>A straight line. Crosses walls. Never a travel distance.</summary>
        public const string StraightLine = "straight_line";

        /// <summary>Nothing could be established. NOT a pass and not a fail.</summary>
        public const string NotMeasurable = "not_measurable";
    }

    /// <summary>One number, with how it was got and how much it can be trusted.</summary>
    /// <summary>
    /// A measured distance for an ACCESSIBILITY check, with the basis it was
    /// taken on.
    ///
    /// Renamed from Measurement: the quantity takeoff has a class of that name in
    /// this same namespace, with a different shape and a different meaning, and
    /// the two collided so the add-in project did not compile at all. Two things
    /// called Measurement in one namespace is also two things a reader has to
    /// keep apart by remembering which file they are in.
    /// </summary>
    public sealed class AccessMeasurement
    {
        public double? Millimetres;
        public string Basis = MeasurementBasis.NotMeasurable;
        public string Uncertainty;
        public string Note;

        public JObject ToJson() => new JObject
        {
            ["mm"] = Millimetres.HasValue ? (JToken)Math.Round(Millimetres.Value, 1) : JValue.CreateNull(),
            ["basis"] = Basis,
            ["uncertainty"] = Uncertainty,
            ["note"] = Note
        };

        public static AccessMeasurement Unavailable(string why) =>
            new AccessMeasurement { Basis = MeasurementBasis.NotMeasurable, Note = why };
    }

    public static class AccessGeometry
    {
        public const double MillimetresPerFoot = 304.8;

        // =====================================================================
        // Travel paths
        // =====================================================================

        public sealed class TravelResult
        {
            public double? DistanceMm;
            public int Waypoints;
            public bool Routed;
            public string Problem;

            public JObject ToJson() => new JObject
            {
                ["distance_mm"] = DistanceMm.HasValue ? (JToken)Math.Round(DistanceMm.Value, 1) : JValue.CreateNull(),
                ["waypoints"] = Waypoints,
                ["routed"] = Routed,
                ["problem"] = Problem,
                ["basis"] = Routed ? MeasurementBasis.TravelPath : MeasurementBasis.NotMeasurable
            };
        }

        /// <summary>
        /// Real routes from each start to its nearest destination, around real obstacles.
        ///
        /// Wrapped exactly once, because of the argument order: Revit's own signature is
        /// (view, DESTINATIONS, STARTS). Backwards against every expectation, and a swap
        /// produces plausible numbers for the wrong question.
        ///
        /// Computation only: no element is created and no transaction is opened.
        /// </summary>
        public static List<TravelResult> Routes(ViewPlan plan, IList<XYZ> starts, IList<XYZ> destinations,
                                                out string coverageProblem)
        {
            coverageProblem = null;
            var results = new List<TravelResult>();

            if (plan == null)
            {
                coverageProblem =
                    "no floor plan view was given. Revit's path-of-travel calculation is two-dimensional " +
                    "and runs IN a plan: the obstacles it avoids are the ones that view shows. There is " +
                    "no view-independent version of this measurement, and choosing a plan silently would " +
                    "be choosing which obstacles count.";
                return results;
            }
            if (starts == null || starts.Count == 0 || destinations == null || destinations.Count == 0)
            {
                coverageProblem = "routes need at least one start and one destination.";
                return results;
            }

            IList<IList<XYZ>> paths;
            try
            {
                // (view, DESTINATIONS, STARTS). See above.
                paths = PathOfTravel.FindShortestPaths(plan, destinations, starts);
            }
            catch (Exception ex)
            {
                // THE SERVICE CAN BE ABSENT. That is a coverage gap in this measurement, and
                // reporting it as "no route found" would turn a missing instrument into a
                // finding about somebody's building.
                coverageProblem =
                    "Revit's path-of-travel service did not run: " + ex.Message + ". This is a gap in " +
                    "the MEASUREMENT, not a finding about the model - it does NOT mean there is no " +
                    "route, and nothing here falls back to a straight line and calls it one.";
                return results;
            }

            for (int i = 0; i < starts.Count; i++)
            {
                IList<XYZ> path = paths != null && i < paths.Count ? paths[i] : null;
                if (path == null || path.Count < 2)
                {
                    // AN EMPTY PATH IS THE MOST IMPORTANT THING THIS CAN SAY. A room with no
                    // way out arrives here as an empty array, not as an exception.
                    results.Add(new TravelResult
                    {
                        Routed = false,
                        Problem = "no route was found from this point to any declared destination. That is " +
                                  "a finding, not a measurement failure: every obstacle the chosen plan " +
                                  "shows was taken into account and none of them could be got around."
                    });
                    continue;
                }

                double feet = 0;
                for (int p = 1; p < path.Count; p++) feet += path[p].DistanceTo(path[p - 1]);
                results.Add(new TravelResult
                {
                    DistanceMm = feet * MillimetresPerFoot,
                    Waypoints = path.Count,
                    Routed = true
                });
            }
            return results;
        }

        /// <summary>The caveats that travel with every routed number, in the reply.</summary>
        public static JObject RouteCaveats(ViewPlan plan) => new JObject
        {
            ["view"] = plan == null ? null : SafeName(plan),
            ["view_id"] = plan == null ? (JToken)JValue.CreateNull() : Rid.Value(plan.Id),
            ["obstacles_are"] = "whatever THAT plan shows. A view with furniture hidden measures a " +
                                "building with no furniture in it, and the number looks identical.",
            ["destination_z_ignored"] = "Revit replaces every destination's Z with the view's level " +
                                        "elevation. A destination on another level is flattened onto " +
                                        "this one rather than refused, so cross-level routes are NOT " +
                                        "what this measures.",
            ["two_dimensional"] = "the calculation is per level and in plan. Stairs and ramps between " +
                                  "levels are not part of any distance reported here.",
            ["creates_nothing"] = true
        };

        // =====================================================================
        // Openings, measured off the geometry
        // =====================================================================

        /// <summary>
        /// The rough opening a door or window occupies in its host, measured from solids.
        ///
        /// WHAT THIS IS NOT: a clear width. Clear width subtracts the leaf, the frame stop and
        /// the hardware, and is defined at a door opened ninety degrees. Reporting this as
        /// clear width would trade a KNOWN approximation - the type's nominal parameter - for
        /// an unknown one, which is a worse trade even though the number looks better.
        ///
        /// WHAT IT IS: the extent of the void in the wall, measured along the wall, which is
        /// an upper bound on the clear width and a lower bound on nothing. It catches the case
        /// the nominal parameter cannot: a family whose parameter says 900 and whose geometry
        /// cuts 700.
        /// </summary>
        public static AccessMeasurement RoughOpening(Document doc, FamilyInstance instance)
        {
            if (doc == null || instance == null)
                return AccessMeasurement.Unavailable("there is no instance to measure.");

            Wall host = null;
            try { host = instance.Host as Wall; } catch { }
            if (host == null)
                return AccessMeasurement.Unavailable(
                    "this instance is not hosted in a wall, so there is no wall direction to measure the " +
                    "opening along. A face-hosted or free instance needs a different measurement, and " +
                    "guessing an axis would produce a number with no meaning.");

            XYZ along;
            try
            {
                var curve = (host.Location as LocationCurve)?.Curve as Line;
                if (curve == null)
                    return AccessMeasurement.Unavailable(
                        "the host wall is not straight, so 'along the wall' is not one direction. A curved " +
                        "wall's opening needs an arc-length measurement this does not do.");
                along = curve.Direction.Normalize();
            }
            catch (Exception ex)
            {
                return AccessMeasurement.Unavailable("the host wall's axis could not be read: " + ex.Message);
            }

            try
            {
                var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false,
                                            DetailLevel = ViewDetailLevel.Fine };
                double min = double.MaxValue, max = double.MinValue;
                int solids = 0;

                foreach (GeometryObject geometry in Flatten(instance.get_Geometry(options)))
                {
                    var solid = geometry as Solid;
                    if (solid == null || solid.Volume <= 0) continue;
                    solids++;
                    foreach (Edge edge in solid.Edges)
                        foreach (XYZ point in edge.Tessellate())
                        {
                            double along1 = point.DotProduct(along);
                            if (along1 < min) min = along1;
                            if (along1 > max) max = along1;
                        }
                }

                if (solids == 0 || min > max)
                    return AccessMeasurement.Unavailable(
                        "this instance produced no solid geometry at fine detail. An empty-geometry family " +
                        "is a real thing - a symbolic door, a placeholder - and it is reported rather " +
                        "than measured as zero.");

                return new AccessMeasurement
                {
                    Millimetres = (max - min) * MillimetresPerFoot,
                    Basis = MeasurementBasis.Geometry,
                    Uncertainty =
                        "±the tessellation of the family's own solids, and it measures the WHOLE instance " +
                        "along the wall - frame included - from " + solids + " solid(s).",
                    Note =
                        "This is the ROUGH OPENING, not a clear width. Clear width subtracts the leaf, the " +
                        "stop and the hardware and is defined at ninety degrees open; this is an upper " +
                        "bound on it. It catches what the nominal parameter cannot: a family whose " +
                        "parameter says 900 and whose geometry cuts 700."
                };
            }
            catch (Exception ex)
            {
                return AccessMeasurement.Unavailable("the geometry could not be read: " + ex.Message);
            }
        }

        /// <summary>
        /// A ramp's slope from geometry rather than from its type's declared maximum.
        ///
        /// The declared maximum is what the type ALLOWS. The built slope is rise over run of
        /// what is there, and a ramp modelled steeper than its own type permits is exactly the
        /// finding an audit exists for - and exactly the one a type parameter cannot report.
        /// </summary>
        public static AccessMeasurement BuiltSlope(Document doc, Element ramp)
        {
            if (ramp == null) return AccessMeasurement.Unavailable("there is no ramp to measure.");
            try
            {
                BoundingBoxXYZ box = ramp.get_BoundingBox(null);
                if (box == null)
                    return AccessMeasurement.Unavailable("this ramp publishes no bounding box.");

                double rise = (box.Max.Z - box.Min.Z) * MillimetresPerFoot;
                double runX = (box.Max.X - box.Min.X) * MillimetresPerFoot;
                double runY = (box.Max.Y - box.Min.Y) * MillimetresPerFoot;
                double run = Math.Sqrt(runX * runX + runY * runY);
                if (run < 1 || rise < 1)
                    return AccessMeasurement.Unavailable("the ramp's extent is degenerate in plan or in height.");

                return new AccessMeasurement
                {
                    Millimetres = null,
                    Basis = MeasurementBasis.Geometry,
                    Uncertainty =
                        "the bounding box is the WHOLE element: a ramp with landings measures flatter " +
                        "than its steepest run, and a ramp that turns measures a diagonal run longer " +
                        "than the one anybody walks. Treat it as an average, not as a maximum.",
                    Note = "built average slope ≈ " +
                           Math.Round(100.0 * rise / run, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           "% (rise " + Math.Round(rise, 0) + " mm over run " + Math.Round(run, 0) + " mm). " +
                           "The TYPE's declared maximum is what the family allows; this is what was built, " +
                           "and a ramp modelled steeper than its own type permits is exactly the finding a " +
                           "type parameter cannot report."
                };
            }
            catch (Exception ex)
            {
                return AccessMeasurement.Unavailable("the ramp's geometry could not be read: " + ex.Message);
            }
        }

        // =====================================================================
        // Coverage
        // =====================================================================

        /// <summary>
        /// How much of a check was actually measured, per check, in the reply.
        ///
        /// WITHOUT THIS a report of "12 doors within profile" is unreadable: twelve out of
        /// twelve and twelve out of two hundred read identically, and the second is the one
        /// that matters.
        /// </summary>
        public static JObject Coverage(string check, int applicable, int measured, string basis,
                                       IEnumerable<string> gaps)
        {
            var list = (gaps ?? Enumerable.Empty<string>()).ToList();
            return new JObject
            {
                ["check"] = check,
                ["applicable"] = applicable,
                ["measured"] = measured,
                ["not_measured"] = applicable - measured,
                ["basis"] = basis,
                ["coverage"] = applicable == 0 ? 0.0 : Math.Round(measured / (double)applicable, 4),
                ["gaps"] = new JArray(list.Take(25)),
                ["means"] = applicable == measured
                    ? "every applicable element was measured on this basis."
                    : (applicable - measured) + " applicable element(s) were NOT measured, each for the " +
                      "reason above. They are neither passing nor failing, and a summary that counted " +
                      "them as either would be inventing a result."
            };
        }

        // =====================================================================

        private static IEnumerable<GeometryObject> Flatten(GeometryElement element)
        {
            if (element == null) yield break;
            foreach (GeometryObject geometry in element)
            {
                var instance = geometry as GeometryInstance;
                if (instance != null)
                {
                    GeometryElement nested = null;
                    try { nested = instance.GetInstanceGeometry(); } catch { }
                    if (nested != null)
                        foreach (GeometryObject inner in Flatten(nested)) yield return inner;
                    continue;
                }
                yield return geometry;
            }
        }

        private static string SafeName(Element element)
        {
            try { return element == null ? null : element.Name; } catch { return null; }
        }
    }
}

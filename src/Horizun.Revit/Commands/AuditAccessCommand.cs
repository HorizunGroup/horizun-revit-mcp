// -----------------------------------------------------------------------------
// Horizun Revit MCP - measuring a model for accessibility and egress.
// Original Horizun code. READ-ONLY.
//
// G22 of the 2026-09-14 competitive inventory. The rule ARITHMETIC and the whole
// argument about what may and may not be claimed live in Core/AccessRules.cs,
// which knows no building code and never will. This file does the measuring.
//
// WHAT IT MEASURES, AND HOW HONEST EACH NUMBER IS - stated per row, because the
// three are not equally trustworthy and presenting them identically would be the
// defect:
//
//   DOOR CLEAR WIDTH. Read from the instance's own width parameter, falling back
//   to the type's. It is the NOMINAL leaf width, and the row says so: real clear
//   width subtracts the leaf thickness and the stop, and a door that opens 90
//   degrees is not the same as one that opens 180. A nominal width is the right
//   number to triage on and the wrong number to certify with.
//
//   STAIR GEOMETRY. Riser and tread are Revit's own ACTUAL values, and width is
//   the narrowest of the stair's runs - a stair is as wide as its pinch point.
//   These are the most trustworthy rows here.
//
//   RAMP SLOPE. The TYPE'S DECLARED MAXIMUM, and nothing better is available:
//   checked against the installed RevitAPI.dll, a Revit ramp exposes no as-built
//   slope, height or width parameter. A ramp drawn steeper than its type permits
//   is therefore invisible to this row, and the row says so rather than passing
//   the type's constraint off as a measurement.
//
//   DISTANCE TO THE NEAREST EXIT. A STRAIGHT LINE from the room's location point
//   to the nearest door marked as an exit. It goes through walls. Every row says
//   so, and the value is labelled straight_line_mm rather than travel_distance.
//   SINCE 2026-09-15 IT IS NO LONGER THE ONLY NUMBER: pass route_view_id and a REAL
//   travel distance is computed with Revit's own path-of-travel service, around the
//   obstacles that plan shows. "The API exposes no property for it" was true and was
//   never the same statement as "it cannot be obtained",
//   because the second word would be a lie that reads as a measurement.
//
// WHICH DOORS ARE EXITS IS THE CALLER'S TO SAY. Nothing in a Revit model reliably
// marks an exit; a project might use a parameter, a mark prefix, or a family
// name. So the caller declares the rule, and if they declare none the command
// REFUSES the egress half rather than guessing - a guessed exit set produces
// egress distances that are confidently wrong.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class AuditAccessCommand : ICommand
    {
        public string Name => "horizun_audit_access";

        public string Description =>
            "Measure doors, ramps, stairs and straight-line distance to exits against a rule profile the " +
            "caller supplies. Read-only, and never a statement of code compliance.";

        private const double FeetToMm = 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            string profileError;
            AccessProfile profile = AccessRules.ReadProfile(request["profile"] as JObject, out profileError);
            if (profile == null) return CommandResult.Fail(profileError);

            var checks = new HashSet<string>(
                (request["checks"] as JArray)?.Select(t => (t.Value<string>() ?? "").ToLowerInvariant())
                ?? new[] { "doors", "ramps", "stairs", "egress" },
                StringComparer.Ordinal);

            var result = new JObject
            {
                ["document"] = doc.Title,
                ["profile"] = AccessRules.Preamble(profile),
                ["unapplied_thresholds"] = new JArray(AccessRules.Unapplied(profile)),
                ["unapplied_means"] =
                    "thresholds this profile declares that this command cannot apply. They are listed rather " +
                    "than dropped: a threshold somebody wrote and nothing checked is worse than one nobody wrote."
            };

            int findings = 0;
            if (checks.Contains("doors")) findings += Section(result, "doors", Doors(doc, profile));
            if (checks.Contains("ramps")) findings += Section(result, "ramps", Ramps(doc, profile));
            if (checks.Contains("stairs")) findings += Section(result, "stairs", Stairs(doc, profile));

            if (checks.Contains("egress"))
            {
                JArray egress = Egress(doc, request, profile, out string egressRefusal);
                if (egress == null)
                {
                    result["egress"] = new JObject
                    {
                        ["not_covered"] = true,
                        ["reason"] = egressRefusal,
                        ["means"] = "NOT COVERED is not a clean result. Nothing was measured for egress."
                    };
                }
                else findings += Section(result, "egress", egress);
            }

            result["findings_outside_profile"] = findings;
            result["coverage"] =
                "Every check that ran reports a row per element, including the ones inside the profile. A " +
                "check that could not read what it needed appears as not_covered with its reason - which is " +
                "not the same as a clean result and must never be rendered as one.";
            return CommandResult.Ok(result);
        }

        private static int Section(JObject result, string name, JArray rows)
        {
            result[name] = rows;
            int outside = 0;
            foreach (JToken row in rows)
                foreach (JToken evaluation in row["evaluations"] as JArray ?? new JArray())
                    if (evaluation["within_profile"]?.Value<bool>() == false) outside++;
            return outside;
        }

        // =====================================================================
        // Doors
        // =====================================================================

        private static JArray Doors(Document doc, AccessProfile profile)
        {
            var rows = new JArray();
            foreach (FamilyInstance door in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
                         .Cast<FamilyInstance>())
            {
                double? widthMm = LengthMm(door, BuiltInParameter.DOOR_WIDTH)
                                  ?? LengthMm(door, BuiltInParameter.GENERIC_WIDTH)
                                  ?? TypeLengthMm(doc, door, BuiltInParameter.DOOR_WIDTH)
                                  ?? TypeLengthMm(doc, door, BuiltInParameter.GENERIC_WIDTH);

                var row = new JObject
                {
                    ["element_id"] = Rid.Value(door.Id),
                    ["name"] = SafeName(door),
                    ["level"] = LevelName(doc, door),
                    ["nominal_width_mm"] = widthMm == null ? (JToken)JValue.CreateNull() : Math.Round(widthMm.Value, 1),
                    ["measurement_quality"] =
                        "NOMINAL leaf width from the door's own parameter. Real clear width subtracts the leaf " +
                        "thickness and the stop, and depends on how far the door opens. Right number to triage " +
                        "on; wrong number to certify with.",
                    ["evaluations"] = new JArray()
                };

                if (widthMm == null)
                    row["not_measured"] = "this door reports no width parameter, so nothing was measured for it.";
                else
                {
                    JObject evaluation = AccessRules.Evaluate(profile, "minimum_clear_width_mm", widthMm.Value, "mm");
                    if (evaluation != null) ((JArray)row["evaluations"]).Add(evaluation);
                }
                rows.Add(row);
            }
            return rows;
        }

        // =====================================================================
        // Ramps
        // =====================================================================

        /// <summary>
        /// Ramps, and an honest admission about what is readable.
        ///
        /// THE AS-BUILT SLOPE IS NOT A PARAMETER. The parameters that exist on a Revit
        /// ramp are its TYPE's constraint - RAMP_ATTR_MIN_INV_SLOPE, the "1/x" maximum -
        /// not the slope the ramp was actually drawn at. Checked against the installed
        /// RevitAPI.dll: there is no RAMP_ATTR_MIN_INV_HEIGHT and no RAMP_ATTR_WIDTH, so
        /// reading a height and a run off the element is not available either.
        ///
        /// So this reports the type's declared maximum and SAYS that is what it is. A
        /// ramp whose type permits 8.33% may still have been drawn steeper by overriding
        /// its geometry, and this command cannot see that. Reporting the type constraint
        /// as though it were the built slope would be the most comfortable lie available
        /// here, which is exactly why it is named on every row.
        /// </summary>
        private static JArray Ramps(Document doc, AccessProfile profile)
        {
            var rows = new JArray();
            foreach (Element ramp in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Ramps).WhereElementIsNotElementType())
            {
                double? inverseSlope = Raw(ramp, BuiltInParameter.RAMP_ATTR_MIN_INV_SLOPE)
                                       ?? TypeRaw(doc, ramp, BuiltInParameter.RAMP_ATTR_MIN_INV_SLOPE);
                double? slopePercent = inverseSlope != null && Math.Abs(inverseSlope.Value) > 1e-9
                    ? 100.0 / inverseSlope.Value
                    : (double?)null;

                var row = new JObject
                {
                    ["element_id"] = Rid.Value(ramp.Id),
                    ["name"] = SafeName(ramp),
                    ["level"] = LevelName(doc, ramp),
                    ["type_inverse_slope"] = inverseSlope == null
                        ? (JToken)JValue.CreateNull() : Math.Round(inverseSlope.Value, 3),
                    ["type_maximum_slope_percent"] = slopePercent == null
                        ? (JToken)JValue.CreateNull() : Math.Round(slopePercent.Value, 2),
                    ["measurement_quality"] =
                        "THE TYPE'S DECLARED MAXIMUM, not the as-built slope. Revit exposes no as-built slope " +
                        "parameter on a ramp - checked against the installed API, not remembered - so a ramp " +
                        "drawn steeper than its type permits is invisible to this row. Treat it as a screening " +
                        "value and walk the ones that matter.",
                    ["evaluations"] = new JArray()
                };

                if (slopePercent == null)
                    row["not_measured"] = "neither the ramp nor its type reports a maximum inverse slope.";
                else
                    Add(row, Evaluate(profile, "maximum_ramp_slope_percent", slopePercent, "%"));

                rows.Add(row);
            }
            return rows;
        }

        // =====================================================================
        // Stairs
        // =====================================================================

        private static JArray Stairs(Document doc, AccessProfile profile)
        {
            var rows = new JArray();
            foreach (Element stair in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Stairs).WhereElementIsNotElementType())
            {
                double? riserMm = LengthMm(stair, BuiltInParameter.STAIRS_ACTUAL_RISER_HEIGHT);
                double? treadMm = LengthMm(stair, BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH);
                // WIDTH IS ON THE RUN, NOT ON THE STAIR. There is no STAIRS_ATTR_MIN_WIDTH
                // in the API; STAIRS_RUN_ACTUAL_RUN_WIDTH is on each StairsRun. A stair
                // with runs of different widths therefore has a narrowest one, and that
                // is the number worth reporting - an average would hide the pinch point,
                // which is the only part of a stair this check exists to find.
                double? widthMm = NarrowestRunMm(doc, stair);

                var row = new JObject
                {
                    ["element_id"] = Rid.Value(stair.Id),
                    ["name"] = SafeName(stair),
                    ["level"] = LevelName(doc, stair),
                    ["actual_riser_mm"] = riserMm == null ? (JToken)JValue.CreateNull() : Math.Round(riserMm.Value, 1),
                    ["actual_tread_mm"] = treadMm == null ? (JToken)JValue.CreateNull() : Math.Round(treadMm.Value, 1),
                    ["minimum_width_mm"] = widthMm == null ? (JToken)JValue.CreateNull() : Math.Round(widthMm.Value, 1),
                    ["measurement_quality"] =
                        "riser and tread are Revit's own ACTUAL values for the stair, which is the most " +
                        "trustworthy measurement in this command. Width is the NARROWEST of the stair's " +
                        "runs and does not account for handrail intrusion.",
                    ["evaluations"] = new JArray()
                };

                Add(row, Evaluate(profile, "maximum_stair_riser_mm", riserMm, "mm"));
                Add(row, Evaluate(profile, "minimum_stair_tread_mm", treadMm, "mm"));
                Add(row, Evaluate(profile, "minimum_stair_width_mm", widthMm, "mm"));

                if (riserMm == null && treadMm == null && widthMm == null)
                    row["not_measured"] = "this stair reports none of riser, tread or width.";
                rows.Add(row);
            }
            return rows;
        }

        // =====================================================================
        // Egress - straight line, and saying so
        // =====================================================================

        private static JArray Egress(Document doc, JObject request, AccessProfile profile, out string refusal)
        {
            refusal = null;

            JObject exitRule = request["exit_rule"] as JObject;
            if (exitRule == null)
            {
                refusal =
                    "exit_rule is required for the egress check. NOTHING IN A REVIT MODEL RELIABLY MARKS AN " +
                    "EXIT - a project might use a parameter, a mark prefix, or a family name - so the rule is " +
                    "the caller's to state. Guessing it would produce egress distances that are confidently " +
                    "wrong, which is worse than none. Give either {\"parameter\":\"<name>\",\"value\":\"<v>\"} " +
                    "or {\"mark_prefix\":\"<p>\"} or {\"element_ids\":[...]}.";
                return null;
            }

            List<FamilyInstance> exits = ResolveExits(doc, exitRule, out refusal);
            if (exits == null) return null;
            if (exits.Count == 0)
            {
                refusal = "the exit rule matched no door in this document, so there is nothing to measure a " +
                          "distance to. Nothing was measured.";
                return null;
            }

            var exitPoints = new List<KeyValuePair<long, XYZ>>();
            foreach (FamilyInstance exit in exits)
            {
                XYZ point = Origin(exit);
                if (point != null) exitPoints.Add(new KeyValuePair<long, XYZ>(Rid.Value(exit.Id), point));
            }
            if (exitPoints.Count == 0)
            {
                refusal = "every matched exit reports no location point, so no distance could be measured.";
                return null;
            }

            // THE ROUTE VIEW, when the caller named one. Revit's path-of-travel calculation
            // runs IN a floor plan and avoids the obstacles THAT view shows, so the choice is
            // the caller's: a plan with furniture hidden measures a building with no furniture
            // in it, and the number looks identical either way.
            ViewPlan routePlan = null;
            string routeProblem = null;
            long routeViewId = request.Value<long?>("route_view_id") ?? -1;
            if (routeViewId >= 0 && Rid.CanRepresent(routeViewId))
            {
                routePlan = doc.GetElement(Rid.Make(routeViewId)) as ViewPlan;
                if (routePlan == null)
                    routeProblem = "route_view_id " + routeViewId + " is not a floor plan view in this " +
                                   "document, so no route was computed. The straight line below is " +
                                   "unaffected and is still a straight line.";
            }

            var roomOrder = new List<Room>();
            var roomPoints = new List<XYZ>();
            var rows = new JArray();
            foreach (Room room in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                         .OfType<Room>())
            {
                // An unplaced room has no geometry at all. It is reported as its own
                // thing, because "no distance" and "a long distance" are different
                // problems with different fixes.
                if (room.Area <= 0)
                {
                    rows.Add(new JObject
                    {
                        ["room_id"] = Rid.Value(room.Id),
                        ["name"] = SafeName(room),
                        ["not_measured"] = "the room is not placed, so it has no location to measure from.",
                        ["evaluations"] = new JArray()
                    });
                    continue;
                }

                XYZ from = (room.Location as LocationPoint)?.Point;
                if (from == null)
                {
                    rows.Add(new JObject
                    {
                        ["room_id"] = Rid.Value(room.Id),
                        ["name"] = SafeName(room),
                        ["not_measured"] = "the room reports no location point.",
                        ["evaluations"] = new JArray()
                    });
                    continue;
                }

                long nearestId = 0;
                double nearestMm = double.MaxValue;
                foreach (KeyValuePair<long, XYZ> exit in exitPoints)
                {
                    double distanceMm = from.DistanceTo(exit.Value) * FeetToMm;
                    if (distanceMm >= nearestMm) continue;
                    nearestMm = distanceMm;
                    nearestId = exit.Key;
                }

                var row = new JObject
                {
                    ["room_id"] = Rid.Value(room.Id),
                    ["name"] = SafeName(room),
                    ["level"] = LevelName(doc, room),
                    ["nearest_exit_element_id"] = nearestId,
                    ["straight_line_mm"] = Math.Round(nearestMm, 1),
                    ["measurement_quality"] =
                        "A STRAIGHT LINE, not a travel distance. It passes through walls, furniture and other " +
                        "rooms; the real path is longer, always. This is a screening number for finding the " +
                        "rooms worth walking, and it is not an egress calculation.",
                    ["evaluations"] = new JArray()
                };
                Add(row, AccessRules.Evaluate(profile, "maximum_straight_line_to_exit_mm", nearestMm, "mm"));
                rows.Add(row);
                roomOrder.Add(room);
                roomPoints.Add(from);
            }

            // ONE batched route call for every measurable room. Per-room calls would rebuild
            // the same obstacle map once per room, which on a real floor is the difference
            // between a second and a minute of held UI thread.
            if (routePlan != null && roomPoints.Count > 0)
            {
                string coverageProblem;
                List<AccessGeometry.TravelResult> routes = AccessGeometry.Routes(
                    routePlan, roomPoints, exitPoints.Select(e => e.Value).ToList(), out coverageProblem);

                int measured = 0;
                var gaps = new List<string>();
                for (int i = 0; i < roomOrder.Count; i++)
                {
                    long roomId = Rid.Value(roomOrder[i].Id);
                    JObject row = rows.OfType<JObject>()
                        .FirstOrDefault(r => r.Value<long?>("room_id") == roomId);
                    if (row == null) continue;

                    if (coverageProblem != null || i >= routes.Count)
                    {
                        row["travel_distance"] = new JObject
                        {
                            ["routed"] = false,
                            ["problem"] = coverageProblem ?? "no route was returned for this room.",
                            ["basis"] = MeasurementBasis.NotMeasurable
                        };
                        gaps.Add("room " + roomId + ": " + (coverageProblem ?? "no route returned"));
                        continue;
                    }

                    AccessGeometry.TravelResult route = routes[i];
                    row["travel_distance"] = route.ToJson();
                    if (route.Routed)
                    {
                        measured++;
                        // EVALUATED UNDER ITS OWN NAME. Feeding a travel distance into a rule
                        // written for a straight line would compare a number against a
                        // threshold nobody set for it.
                        Add(row, AccessRules.Evaluate(profile, "maximum_travel_distance_to_exit_mm",
                                                      route.DistanceMm ?? 0, "mm"));
                    }
                    else
                    {
                        gaps.Add("room " + roomId + ": " + route.Problem);
                    }
                }

                rows.Add(new JObject
                {
                    ["measurement_summary"] = new JObject
                    {
                        ["route_caveats"] = AccessGeometry.RouteCaveats(routePlan),
                        ["coverage"] = AccessGeometry.Coverage("egress.travel_distance", roomOrder.Count,
                                                               measured, MeasurementBasis.TravelPath, gaps)
                    }
                });
            }
            else if (routeProblem != null || routeViewId >= 0)
            {
                rows.Add(new JObject
                {
                    ["measurement_summary"] = new JObject
                    {
                        ["coverage"] = AccessGeometry.Coverage(
                            "egress.travel_distance", roomOrder.Count, 0, MeasurementBasis.NotMeasurable,
                            new[] { routeProblem ?? "no measurable room to route from" })
                    }
                });
            }

            return rows;
        }

        private static List<FamilyInstance> ResolveExits(Document doc, JObject rule, out string refusal)
        {
            refusal = null;
            List<FamilyInstance> doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
                .OfType<FamilyInstance>().ToList();

            JArray explicitIds = rule["element_ids"] as JArray;
            if (explicitIds != null)
            {
                var wanted = new HashSet<long>(explicitIds.Select(t => t.Value<long?>() ?? -1));
                return doors.Where(d => wanted.Contains(Rid.Value(d.Id))).ToList();
            }

            string prefix = rule.Value<string>("mark_prefix");
            if (!string.IsNullOrWhiteSpace(prefix))
                return doors.Where(d =>
                {
                    string mark = ParameterText(d, BuiltInParameter.ALL_MODEL_MARK);
                    return mark != null && mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                }).ToList();

            string parameter = rule.Value<string>("parameter");
            if (!string.IsNullOrWhiteSpace(parameter))
            {
                string value = rule.Value<string>("value");
                return doors.Where(d =>
                {
                    string actual = ParameterByName(d, parameter);
                    if (actual == null) return false;
                    return string.IsNullOrWhiteSpace(value)
                        ? !string.IsNullOrWhiteSpace(actual)
                        : string.Equals(actual.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            refusal = "exit_rule must carry element_ids, mark_prefix, or parameter (with an optional value).";
            return null;
        }

        // =====================================================================
        // Readers
        // =====================================================================

        private static void Add(JObject row, JObject evaluation)
        {
            if (evaluation != null) ((JArray)row["evaluations"]).Add(evaluation);
        }

        private static JObject Evaluate(AccessProfile profile, string key, double? measured, string units)
            => measured == null ? null : AccessRules.Evaluate(profile, key, measured.Value, units);

        /// <summary>
        /// The narrowest run of a stair, in millimetres, or null when it has no run this
        /// can read. The NARROWEST, because a stair is as wide as its pinch point.
        /// </summary>
        private static double? NarrowestRunMm(Document doc, Element stair)
        {
            double? narrowest = null;
            try
            {
                foreach (Element run in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_StairsRuns).WhereElementIsNotElementType())
                {
                    // A run whose parent cannot be read is SKIPPED, never attributed to
                    // this stair: a width taken from somebody else's stair is worse than
                    // a missing one, because it looks like a measurement.
                    if (ParentStairId(run) != Rid.Value(stair.Id)) continue;
                    double? width = LengthMm(run, BuiltInParameter.STAIRS_RUN_ACTUAL_RUN_WIDTH);
                    if (width == null) continue;
                    if (narrowest == null || width.Value < narrowest.Value) narrowest = width;
                }
            }
            catch { return narrowest; }
            return narrowest;
        }

        /// <summary>The stair a run belongs to, or 0 when it cannot be read.</summary>
        private static long ParentStairId(Element run)
        {
            try
            {
                var stairsRun = run as Autodesk.Revit.DB.Architecture.StairsRun;
                return stairsRun == null ? 0 : Rid.Value(stairsRun.GetStairs().Id);
            }
            catch { return 0; }
        }

        /// <summary>A raw double parameter, in Revit's own units. Used where the value is not a length.</summary>
        private static double? Raw(Element element, BuiltInParameter builtIn)
        {
            try
            {
                Parameter p = element.get_Parameter(builtIn);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                double value = p.AsDouble();
                return Math.Abs(value) < 1e-9 ? (double?)null : value;
            }
            catch { return null; }
        }

        private static double? TypeRaw(Document doc, Element element, BuiltInParameter builtIn)
        {
            try
            {
                Element type = doc.GetElement(element.GetTypeId());
                return type == null ? null : Raw(type, builtIn);
            }
            catch { return null; }
        }

        private static double? LengthMm(Element element, BuiltInParameter builtIn)
        {
            try
            {
                Parameter p = element.get_Parameter(builtIn);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                double value = p.AsDouble();
                return Math.Abs(value) < 1e-9 ? (double?)null : value * FeetToMm;
            }
            catch { return null; }
        }

        private static double? TypeLengthMm(Document doc, Element element, BuiltInParameter builtIn)
        {
            try
            {
                Element type = doc.GetElement(element.GetTypeId());
                return type == null ? null : LengthMm(type, builtIn);
            }
            catch { return null; }
        }

        private static string ParameterText(Element element, BuiltInParameter builtIn)
        {
            try
            {
                Parameter p = element.get_Parameter(builtIn);
                return p == null || !p.HasValue ? null : p.AsString();
            }
            catch { return null; }
        }

        private static string ParameterByName(Element element, string name)
        {
            try
            {
                foreach (Parameter p in element.Parameters)
                {
                    string parameterName;
                    try { parameterName = p.Definition?.Name; } catch { continue; }
                    if (!string.Equals(parameterName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.HasValue) continue;
                    switch (p.StorageType)
                    {
                        case StorageType.String: return p.AsString();
                        case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                        case StorageType.Double: return p.AsValueString();
                        case StorageType.ElementId: return p.AsValueString();
                    }
                }
            }
            catch { }
            return null;
        }

        private static XYZ Origin(Element element)
        {
            try { return (element.Location as LocationPoint)?.Point; }
            catch { return null; }
        }

        private static string LevelName(Document doc, Element element)
        {
            try
            {
                Element level = doc.GetElement(element.LevelId);
                return level == null ? null : SafeName(level);
            }
            catch { return null; }
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; } catch { return null; }
        }
    }
}

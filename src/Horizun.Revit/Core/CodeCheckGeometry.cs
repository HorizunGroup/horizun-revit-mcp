// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CODE CHECKS: arithmetic the Revit side feeds with facts.
//
//   * EXITS PER LEVEL. Occupant load = sum over the level's PLACED rooms of the
//     declared occupant parameter, or area / area_per_person_m2 when the set gives
//     a factor, or area / the factor area_per_person_by_group assigns to the group
//     each room declares in occupancy_parameter. Required exits come from the set's own table. Exits are the doors
//     the set's exit_door rule selects - nothing in a model marks an exit, so no
//     rule means not_decidable, never a guess.
//
//   * RAMP GEOMETRY. From the ramp's own planar top faces, not from its type's
//     declared maximum (which horizun_audit_access reports and names as such): the
//     steepest sloped face gives the slope, its extent along the gradient the run,
//     across it the width; flat top faces are landings.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One planar face: unit normal and vertices, in millimetres.</summary>
    public sealed class PlanarFaceFact
    {
        public double Nx, Ny, Nz;
        public List<double[]> Points = new List<double[]>();
    }

    public static partial class CodeCheckRules
    {
        /// <summary>Slopes flatter than this (0.5 %) are landings; steeper than 100 % are sides.</summary>
        public const double FlatTangent = 0.005;
        public const double SideTangent = 1.0;

        /// <summary>exit_count_minus_required for one level, or not_decidable with the missing piece named.</summary>
        public static MeasuredValue ExitsVsRequired(JObject config, CheckedElement level)
        {
            if (config == null) return MeasuredValue.None("the rule carries no config (occupant load, exit_door, required_exits).");
            JArray table = config["required_exits"] as JArray;
            if (table == null || table.Count == 0)
                return MeasuredValue.None("config.required_exits is empty: the set does not state how many exits a load requires.");
            JObject exitRule = config["exit_door"] as JObject;
            if (exitRule == null)
                return MeasuredValue.None("config.exit_door is missing: nothing in a model marks an exit, so the set must say which doors are exits.");
            string loadParam = config.Value<string>("occupant_load_parameter");
            double? perPerson = config.Value<double?>("area_per_person_m2");
            // Occupancy-group selection: a norm's factor table keyed by the group each
            // room declares (NSR-10 Tabla K.3.3-1). A room whose group is absent or not in
            // the table makes the load incomplete - never a default factor.
            string groupParam = config.Value<string>("occupancy_parameter");
            JObject byGroup = config["area_per_person_by_group"] as JObject;
            if ((groupParam == null) != (byGroup == null))
                return MeasuredValue.None("config.occupancy_parameter and config.area_per_person_by_group go together: one without the other cannot assign a factor to a room.");
            if (loadParam == null && byGroup == null && (perPerson == null || perPerson <= 0))
                return MeasuredValue.None("config needs occupant_load_parameter, occupancy_parameter with area_per_person_by_group, or a positive area_per_person_m2.");

            List<CheckedElement> rooms = (level.Rooms ?? new List<CheckedElement>()).Where(r => r.AreaM2 > 0).ToList();
            if (rooms.Count == 0)
                return MeasuredValue.None("no placed room on this level: without rooms there is no occupant load to compute.");

            double load = 0;
            foreach (CheckedElement room in rooms)
            {
                if (loadParam != null)
                {
                    if (!room.Params.TryGetValue(loadParam, out ParamFact p) || !p.Exists || p.Number == null)
                        return MeasuredValue.None("room " + room.Id + " carries no numeric '" + loadParam + "': the occupant load is incomplete.");
                    load += p.Number.Value;
                }
                else if (byGroup != null)
                {
                    if (!room.Params.TryGetValue(groupParam, out ParamFact g) || !g.Exists || string.IsNullOrWhiteSpace(g.Text))
                        return MeasuredValue.None("room " + room.Id + " declares no '" + groupParam + "': its occupancy group, and so the occupant load, is unknown.");
                    string group = g.Text.Trim();
                    JProperty row = byGroup.Properties().FirstOrDefault(pr => string.Equals(pr.Name.Trim(), group, StringComparison.OrdinalIgnoreCase));
                    double? factor = row?.Value.Type == JTokenType.Integer || row?.Value.Type == JTokenType.Float ? row.Value.Value<double>() : (double?)null;
                    if (factor == null || factor <= 0)
                        return MeasuredValue.None("room " + room.Id + " declares group '" + group + "', which config.area_per_person_by_group gives no positive factor for: the occupant load is incomplete.");
                    load += room.AreaM2 / factor.Value;
                }
                else load += room.AreaM2 / perPerson.Value;
            }
            load = Math.Ceiling(load - 1e-9);

            int? required = null;
            foreach (JObject row in table.OfType<JObject>().OrderBy(r => r.Value<double?>("max_load") ?? double.MaxValue))
            {
                double max = row.Value<double?>("max_load") ?? double.MaxValue;
                if (load <= max) { required = row.Value<int?>("exits"); break; }
            }
            if (required == null)
                return MeasuredValue.None("config.required_exits has no row for an occupant load of " + Fmt(load) + ".");

            int exits = (level.Doors ?? new List<CheckedElement>()).Count(d => IsExit(exitRule, d));
            return new MeasuredValue
            {
                Value = exits - required.Value,
                Basis = "exit doors on the level minus required exits; occupant load from " +
                        (loadParam != null ? "'" + loadParam + "'"
                         : byGroup != null ? "area / the factor of each room's '" + groupParam + "'"
                         : "area / " + Fmt(perPerson.Value) + " m2 per person") + ".",
                Detail = new JObject { ["occupant_load"] = load, ["required_exits"] = required.Value, ["exit_doors"] = exits, ["rooms"] = rooms.Count }
            };
        }

        public static bool IsExit(JObject rule, CheckedElement door)
        {
            string prefix = rule.Value<string>("mark_prefix");
            if (!string.IsNullOrWhiteSpace(prefix))
                return door.Mark != null && door.Mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            string parameter = rule.Value<string>("parameter");
            if (string.IsNullOrWhiteSpace(parameter)) return false;
            if (!door.Params.TryGetValue(parameter, out ParamFact p) || !p.Exists) return false;
            string value = rule["value"]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? !string.IsNullOrWhiteSpace(p.Text)
                : string.Equals((p.Text ?? "").Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Ramp measures from its planar faces (millimetres). Every key is set, some as None.</summary>
        public static Dictionary<string, MeasuredValue> RampMeasures(IList<PlanarFaceFact> faces)
        {
            var result = new Dictionary<string, MeasuredValue>(StringComparer.Ordinal);
            var sloped = new List<(PlanarFaceFact f, double tan, double gx, double gy)>();
            var flat = new List<PlanarFaceFact>();
            foreach (PlanarFaceFact f in faces ?? new List<PlanarFaceFact>())
            {
                if (f.Nz <= 1e-6 || f.Points.Count < 3) continue;           // downward or vertical
                double h = Math.Sqrt(f.Nx * f.Nx + f.Ny * f.Ny);
                double tan = h / f.Nz;
                if (tan <= FlatTangent) flat.Add(f);
                else if (tan < SideTangent) sloped.Add((f, tan, f.Nx / h, f.Ny / h));
            }
            if (sloped.Count == 0)
            {
                string why = "no upward sloped face between 0.5 % and 100 % in the ramp's geometry.";
                foreach (string k in new[] { "ramp_slope_percent", "ramp_run_length_mm", "ramp_width_mm", "ramp_landing_length_mm" })
                    result[k] = MeasuredValue.None(why);
                return result;
            }
            result["ramp_slope_percent"] = MeasuredValue.Exact(sloped.Max(s => s.tan) * 100.0,
                "steepest upward sloped face of the ramp's own geometry.");
            result["ramp_run_length_mm"] = MeasuredValue.Exact(sloped.Max(s => Extent(s.f, s.gx, s.gy)),
                "longest horizontal extent of one sloped face along its gradient.");
            result["ramp_width_mm"] = MeasuredValue.Exact(sloped.Min(s => Extent(s.f, -s.gy, s.gx)),
                "narrowest sloped face across its gradient; handrails not deducted.");
            if (flat.Count == 0)
                result["ramp_landing_length_mm"] = MeasuredValue.None("no flat top face: the ramp has no modelled landing.");
            else
            {
                double shortest = double.MaxValue;
                foreach (PlanarFaceFact l in flat)
                {
                    double[] c = Centroid(l);
                    var near = sloped.OrderBy(s => Dist2(Centroid(s.f), c)).First();
                    shortest = Math.Min(shortest, Extent(l, near.gx, near.gy));
                }
                result["ramp_landing_length_mm"] = MeasuredValue.Exact(shortest,
                    "shortest flat top face measured along the nearest flight's gradient.");
            }
            return result;
        }

        private static double Extent(PlanarFaceFact f, double dx, double dy)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (double[] p in f.Points) { double t = p[0] * dx + p[1] * dy; min = Math.Min(min, t); max = Math.Max(max, t); }
            return max - min;
        }

        private static double[] Centroid(PlanarFaceFact f)
            => new[] { f.Points.Average(p => p[0]), f.Points.Average(p => p[1]), f.Points.Average(p => p[2]) };

        private static double Dist2(double[] a, double[] b)
            => (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]);
    }
}

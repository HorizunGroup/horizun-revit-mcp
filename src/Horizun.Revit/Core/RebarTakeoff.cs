// -----------------------------------------------------------------------------
// Horizun Revit MCP - the quantities somebody orders steel from.
// Original Horizun code. No Revit types.
//
// Length, volume and count are already measured per SET. What is missing is the
// grouping: nobody orders "set 419", they order "so many metres of 12 mm at
// mark E1". This groups the rows a reader already has, by keys they declare.
//
// The failure this file is built to avoid is the one every takeoff has: a value
// the model would not report becomes a zero, the zero sums silently, and the
// total is short by exactly the bars nobody could measure. So a group counts
// what it could NOT read and says so, every total that is missing something is
// labelled `partial`, and the totals never quietly include a zero that was
// really an absence.
//
// Weight is not computed from a density this file knows, because it does not
// know one. A caller that wants kilograms declares the density and the source it
// came from, and both are published beside the number.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RebarTakeoffKey
    {
        public const string Mark = "mark";
        public const string BarType = "bar_type";
        public const string NominalDiameter = "nominal_diameter_mm";
        public const string ModelDiameter = "model_diameter_mm";
        public const string Shape = "shape";
        public const string Host = "host";
        public const string HostCategory = "host_category";
        public const string Style = "style";
        public const string Layout = "layout";
        /// <summary>The provenance rule id. For a stirrup zone that is parent#zone, so this groups BY ZONE.</summary>
        public const string Rule = "rule";

        public static readonly string[] All =
        {
            Mark, BarType, NominalDiameter, ModelDiameter, Shape, Host, HostCategory, Style, Layout, Rule
        };

        public static bool IsKnown(string k)
        {
            return Array.IndexOf(All, k) >= 0;
        }
    }

    public static class RebarTakeoff
    {
        public const string CodeUnknownKey = "unknown_group_key";
        public const string CodeNoKeys = "no_group_keys";
        public const string CodeDensityNotUsable = "density_not_usable";
        public const string CodeDensityWithoutSource = "density_without_a_source";

        /// <summary>
        /// Group the rows and total them.
        ///
        /// `rows` are RebarFacts-shaped objects - deliberately, so this cannot drift
        /// from the reader. `densityKgPerM3` and `densitySource` travel together:
        /// a number with no source is refused, because a weight nobody can trace is
        /// a weight nobody should order from.
        /// </summary>
        public static JObject Group(JArray rows, IList<string> groupBy,
            double? densityKgPerM3, string densitySource, out string error)
        {
            error = null;

            if (groupBy == null || groupBy.Count == 0)
            {
                error = CodeNoKeys + ": name at least one of " + string.Join(", ", RebarTakeoffKey.All) +
                        ". A takeoff with no grouping is the list of bars you already have.";
                return null;
            }
            foreach (string k in groupBy)
                if (!RebarTakeoffKey.IsKnown(k))
                {
                    error = CodeUnknownKey + ": '" + k + "' is not one of " +
                            string.Join(", ", RebarTakeoffKey.All) + ". The vocabulary is closed so that a " +
                            "misspelled key is a refusal rather than a group of one.";
                    return null;
                }

            if (densityKgPerM3.HasValue)
            {
                double v = densityKgPerM3.Value;
                if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
                {
                    error = CodeDensityNotUsable + ": the density must be a positive finite number of " +
                            "kilograms per cubic metre.";
                    return null;
                }
                if (string.IsNullOrWhiteSpace(densitySource))
                {
                    error = CodeDensityWithoutSource + ": a density needs a source - the standard, the " +
                            "supplier, or the project document it came from. This bridge does not carry one, " +
                            "and a weight nobody can trace is a weight nobody should order from.";
                    return null;
                }
            }

            var groups = new Dictionary<string, Group0>(StringComparer.Ordinal);
            var order = new List<string>();
            int rowsSeen = 0, systemRows = 0, fabricRows = 0;

            foreach (JToken t in rows ?? new JArray())
            {
                var row = t as JObject;
                if (row == null) continue;
                rowsSeen++;

                var values = new JObject();
                var keyParts = new List<string>();
                foreach (string k in groupBy)
                {
                    JToken v = ValueFor(row, k);
                    values[k] = v ?? JValue.CreateNull();
                    keyParts.Add(v == null || v.Type == JTokenType.Null
                        ? "\0"   // a value the model would not report is its OWN group, not a blank one
                        : v.ToString(Newtonsoft.Json.Formatting.None));
                }
                string key = string.Join("\u001f", keyParts);

                Group0 g;
                if (!groups.TryGetValue(key, out g))
                {
                    g = new Group0 { Values = values };
                    groups[key] = g;
                    order.Add(key);
                }

                g.Sets++;
                // A system bar (RebarInSystem of an area or path system) is counted as a set is; this says how many.
                // A FABRIC SHEET is counted too - its bars are its wires - and says so on its own.
                bool fabric = RebarSystemBars.IsFabric(row);
                if (fabric) { g.FabricSheets++; fabricRows++; }
                else if (row["system"] is JObject) { g.FromSystems++; systemRows++; }

                int? quantity = Int(row, "measured", "quantity");
                if (quantity.HasValue) g.Bars += quantity.Value; else g.QuantityUnreadable++;
                if (fabric)
                {
                    if (quantity.HasValue) g.FabricWires += quantity.Value;
                    double? fv = Num(row, "measured", "volume_m3");
                    if (fv.HasValue) g.FabricVolumeM3 += fv.Value;
                    double? revit = Num(row, "fabric", "revit_cut_sheet_mass_kg");
                    if (revit.HasValue) g.FabricRevitKg += revit.Value; else g.FabricRevitUnread++;
                }

                double? lengthM = Num(row, "measured", "total_length_m");
                if (lengthM.HasValue) g.LengthM += lengthM.Value; else g.LengthUnreadable++;

                double? volume = Num(row, "measured", "volume_m3");
                if (volume.HasValue) g.VolumeM3 += volume.Value; else g.VolumeUnreadable++;

                double? each = Num(row, "geometry", "centreline_length_mm");
                if (each.HasValue)
                {
                    if (!g.EachMm.HasValue) g.EachMm = each;
                    else if (Math.Abs(g.EachMm.Value - each.Value) > 0.5) g.EachVaries = true;
                }
                else g.EachUnreadable++;
            }

            var outGroups = new JArray();
            foreach (string key in order)
            {
                Group0 g = groups[key];
                var o = new JObject { ["group"] = g.Values, ["sets"] = g.Sets, ["sets_from_systems"] = g.FromSystems };
                if (g.FabricSheets > 0) Fabric(o, g.FabricSheets, g.FabricWires, g.FabricVolumeM3, g.FabricRevitKg, g.FabricRevitUnread, densityKgPerM3);

                o["bars"] = g.QuantityUnreadable > 0 ? (JToken)JValue.CreateNull() : g.Bars;
                o["bars_counted"] = g.Bars;
                o["total_length_m"] = g.LengthUnreadable > 0
                    ? (JToken)JValue.CreateNull() : Math.Round(g.LengthM, 4);
                o["total_length_m_counted"] = Math.Round(g.LengthM, 4);
                o["volume_m3"] = g.VolumeUnreadable > 0
                    ? (JToken)JValue.CreateNull() : Math.Round(g.VolumeM3, 6);
                o["volume_m3_counted"] = Math.Round(g.VolumeM3, 6);

                o["bar_length_each_mm"] = g.EachVaries || !g.EachMm.HasValue
                    ? (JToken)JValue.CreateNull() : Math.Round(g.EachMm.Value, 3);
                if (g.EachVaries)
                    o["bar_length_each_why"] =
                        "the sets in this group are not all the same length, so there is no single bar length " +
                        "for it. Add a key that separates them.";

                if (densityKgPerM3.HasValue)
                {
                    o["mass_kg"] = g.VolumeUnreadable > 0
                        ? (JToken)JValue.CreateNull() : Math.Round(g.VolumeM3 * densityKgPerM3.Value, 3);
                    o["mass_kg_counted"] = Math.Round(g.VolumeM3 * densityKgPerM3.Value, 3);
                }

                int unread = g.QuantityUnreadable + g.LengthUnreadable + g.VolumeUnreadable;
                o["complete"] = unread == 0;
                if (unread > 0)
                {
                    o["unreadable"] = new JObject
                    {
                        ["quantity"] = g.QuantityUnreadable,
                        ["length"] = g.LengthUnreadable,
                        ["volume"] = g.VolumeUnreadable
                    };
                    o["why_partial"] =
                        "the model would not report every value in this group. The totals above are null " +
                        "rather than a sum with a hole in it; the _counted numbers are what could be read, " +
                        "and they are SHORT by the sets named in unreadable. A takeoff that turns an " +
                        "unreadable value into a zero is short by exactly the bars nobody could measure.";
                }
                outGroups.Add(o);
            }

            var result = new JObject
            {
                ["grouped_by"] = new JArray(groupBy.ToArray()),
                ["sets_read"] = rowsSeen,
                ["sets_from_systems"] = systemRows,
                ["sets_from_systems_means"] =
                    "how many of the rows read are bars of area or path reinforcement (RebarInSystem, marked " +
                    "system on their rows). They are measured and summed exactly as rebar sets are.",
                ["fabric_sheets"] = fabricRows,
                ["fabric_sheets_means"] =
                    "how many of the rows read are FABRIC SHEETS (system.kind fabric). A sheet's bars are its wires as " +
                    "placed: their count is in bars, their summed length in total_length_m, their steel (length x pi x " +
                    "d^2 / 4 per wire type) in volume_m3, weighed with the declared density like every bar. Where fabric " +
                    "is counted, fabric.revit_cut_sheet_mass_kg is Revit's own mass for the same sheets, to check against.",
                ["groups"] = outGroups,
                ["group_count"] = outGroups.Count,
                ["totals"] = Totals(groups.Values, densityKgPerM3),
                ["how_measured"] =
                    "length, volume and count come from the rebar element itself (a set, or a bar of an area or " +
                    "path system) - Revit's own totals - and " +
                    "are summed per group. A fabric sheet's come from its wires as placed (see fabric_sheets_means). " +
                    "Nothing here is derived from a declared centreline.",
                ["group_keys_available"] = new JArray(RebarTakeoffKey.All)
            };
            if (densityKgPerM3.HasValue)
                result["density"] = new JObject
                {
                    ["kg_per_m3"] = densityKgPerM3.Value,
                    ["source"] = densitySource,
                    ["means"] = "declared by the caller. This bridge carries no density of its own: steel is " +
                                "not one number, and a weight with an invented density is a weight nobody can " +
                                "check."
                };
            else
                result["mass"] = new JObject
                {
                    ["reported"] = false,
                    ["why"] = "no density was declared, so no weight is reported. Declare density_kg_per_m3 " +
                              "and density_source together to get one."
                };
            return result;
        }

        private static JObject Totals(IEnumerable<Group0> groups, double? density)
        {
            int sets = 0, bars = 0, unread = 0, fromSystems = 0, sheets = 0, wires = 0, revitUnread = 0;
            double lengthM = 0, volume = 0, fabricVolume = 0, revitKg = 0;
            foreach (Group0 g in groups)
            {
                sets += g.Sets;
                fromSystems += g.FromSystems;
                sheets += g.FabricSheets;
                wires += g.FabricWires;
                fabricVolume += g.FabricVolumeM3;
                revitKg += g.FabricRevitKg;
                revitUnread += g.FabricRevitUnread;
                bars += g.Bars;
                lengthM += g.LengthM;
                volume += g.VolumeM3;
                unread += g.QuantityUnreadable + g.LengthUnreadable + g.VolumeUnreadable;
            }
            var o = new JObject
            {
                ["sets"] = sets,
                ["sets_from_systems"] = fromSystems,
                ["bars"] = unread == 0 ? (JToken)bars : JValue.CreateNull(),
                ["bars_counted"] = bars,
                ["total_length_m"] = unread == 0 ? (JToken)Math.Round(lengthM, 4) : JValue.CreateNull(),
                ["total_length_m_counted"] = Math.Round(lengthM, 4),
                ["volume_m3"] = unread == 0 ? (JToken)Math.Round(volume, 6) : JValue.CreateNull(),
                ["volume_m3_counted"] = Math.Round(volume, 6),
                ["complete"] = unread == 0
            };
            if (density.HasValue)
            {
                o["mass_kg"] = unread == 0
                    ? (JToken)Math.Round(volume * density.Value, 3) : JValue.CreateNull();
                o["mass_kg_counted"] = Math.Round(volume * density.Value, 3);
            }
            if (unread > 0)
                o["why_partial"] = unread + " value(s) could not be read, so the totals are null rather than " +
                                   "a sum that looks complete and is not.";
            o["fabric_sheets"] = sheets;
            if (sheets > 0) Fabric(o, sheets, wires, fabricVolume, revitKg, revitUnread, density);
            return o;
        }

        /// <summary>The fabric of a group or of the totals: its sheets, wires and steel, and Revit's own mass beside it.</summary>
        private static void Fabric(JObject o, int sheets, int wires, double volumeM3, double revitKg, int revitUnread, double? density)
        {
            var f = new JObject
            {
                ["sheets"] = sheets,
                ["wires"] = wires,
                ["volume_m3_counted"] = Math.Round(volumeM3, 6),
                ["revit_cut_sheet_mass_kg"] = revitUnread > 0 ? (JToken)JValue.CreateNull() : Math.Round(revitKg, 3),
                ["revit_cut_sheet_mass_kg_counted"] = Math.Round(revitKg, 3)
            };
            if (density.HasValue) f["mass_kg_counted"] = Math.Round(volumeM3 * density.Value, 3);
            f["means"] = "the fabric sheets in this total (already inside bars, total_length_m, volume_m3 and mass_kg): " +
                         "their wires, the wires' steel, and Revit's own cut-sheet mass for the same sheets - Revit's " +
                         "arithmetic with Revit's density, published to check against, never summed in.";
            o["fabric"] = f;
        }

        private static JToken ValueFor(JObject row, string key)
        {
            switch (key)
            {
                case RebarTakeoffKey.Mark: return Path(row, "measured", "schedule_mark");
                case RebarTakeoffKey.BarType: return Path(row, "bar_type", "name");
                case RebarTakeoffKey.NominalDiameter: return Path(row, "bar_type", "nominal_diameter_mm");
                case RebarTakeoffKey.ModelDiameter: return Path(row, "bar_type", "model_diameter_mm");
                case RebarTakeoffKey.Shape: return Path(row, "shape", "name");
                case RebarTakeoffKey.Host: return Path(row, "host", "id");
                case RebarTakeoffKey.HostCategory: return Path(row, "host", "category");
                case RebarTakeoffKey.Style: return row["style_horizun"];
                case RebarTakeoffKey.Layout: return Path(row, "layout", "rule");
                case RebarTakeoffKey.Rule: return Path(row, "provenance", "rule_id");
                default: throw new ArgumentException("unknown takeoff key '" + key + "'");
            }
        }

        private static JToken Path(JObject o, params string[] path)
        {
            JToken t = o;
            foreach (string p in path)
            {
                if (t == null) return null;
                t = t[p];
            }
            return t == null || t.Type == JTokenType.Null ? null : t;
        }

        private static double? Num(JObject o, params string[] path)
        {
            JToken t = Path(o, path);
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) return null;
            double v = t.Value<double>();
            return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
        }

        private static int? Int(JObject o, params string[] path)
        {
            JToken t = Path(o, path);
            if (t == null || t.Type != JTokenType.Integer) return null;
            return t.Value<int>();
        }

        private sealed class Group0
        {
            public JObject Values;
            public int Sets;
            public int FromSystems;
            public int FabricSheets;
            public int FabricWires;
            public double FabricVolumeM3;
            public double FabricRevitKg;
            public int FabricRevitUnread;
            public int Bars;
            public double LengthM;
            public double VolumeM3;
            public int QuantityUnreadable;
            public int LengthUnreadable;
            public int VolumeUnreadable;
            public int EachUnreadable;
            public double? EachMm;
            public bool EachVaries;
        }
    }
}

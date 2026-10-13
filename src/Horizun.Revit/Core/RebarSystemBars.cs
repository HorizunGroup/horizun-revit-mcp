// -----------------------------------------------------------------------------
// Horizun Revit MCP - the bars of area and path reinforcement, in the takeoff.
// Original Horizun code. No Revit types: the Revit side reads one RebarInSystem
// into SystemBarFacts and this turns it into a row.
//
// WHY THIS EXISTS. An area or path reinforcement system lays its bars as
// RebarInSystem elements, not as rebar sets. mode=quantities, mode=reconcile and
// mode=bending_schedule collected rebar sets only, so a slab's area steel was
// silently missing from the takeoff - and the takeoff still said `complete`.
// Steel left out of an order under the one word that promises nothing is
// missing is the failure this whole bridge is built against.
//
// THE SAME ROW. A system bar becomes a row in the shape RebarFacts gives a set
// (measured, bar_type, host, shape, geometry), so RebarTakeoff, RebarBendingSchedule
// and RebarReconcile aggregate it with no second path - plus `system: {kind, id}`,
// which says where it came from.
//
// THE SCOPE. element_ids may name sets, system bars, or the SYSTEM itself (an area
// or path reinforcement element), which scopes to its bars.
//
// FABRIC (stage 1c). A fabric sheet is welded wire, not a bar set, and it used to
// be named as a reason and left out of the totals (coverage partial). It is now
// READ: one row per sheet (FabricRow), whose bars are its wires as placed and whose
// steel is the wires' volume, weighed with the declared density like every bar -
// Revit's own sheet masses published beside it to check against. A fabric AREA
// named in element_ids scopes to its sheets. A sheet that would not read is a
// named reason, and the coverage partial; one that read is complete. An id that is
// no reinforcement at all is named rather than dropped.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What the Revit side read off ONE RebarInSystem: millimetres and cubic metres, as read, nothing derived.</summary>
    public sealed class SystemBarFacts
    {
        public long Id;
        public string UniqueId;
        public string Name;
        /// <summary><see cref="RebarSystemBars.KindArea"/>, <see cref="RebarSystemBars.KindPath"/>, or null when the system did not resolve.</summary>
        public string SystemKind;
        public long? SystemId;
        public long? HostId;
        public bool HostResolved;
        public string HostCategory;
        public string HostName;
        public long? BarTypeId;
        public string BarTypeName;
        public double? NominalDiameterMm;
        public double? ModelDiameterMm;
        public long? ShapeId;
        public string ShapeName;
        /// <summary>StructuralStyle word, or null.</summary>
        public string Style;
        /// <summary>Revit's enum name (MaximumSpacing ...).</summary>
        public string LayoutRule;
        public int? NumberOfBarPositions;
        public int? Quantity;
        public double? MaxSpacingMm;
        public double? ArrayLengthMm;
        public long? HookTypeId0;
        public long? HookTypeId1;
        public string HookTypeName0;
        public string HookTypeName1;
        public bool HookReadable0 = true;
        public bool HookReadable1 = true;
        /// <summary>Revit's own TotalLength (rounded by the bar type's rule).</summary>
        public double? TotalLengthMm;
        public double? VolumeM3;
        public string ScheduleMark;
        /// <summary>One bar's centreline as drawn, hooks and bends included, unrounded.</summary>
        public double? OneBarCentrelineMm;
        public List<double[]> CentrelinePointsMm;
        /// <summary>Hooks and bend radii suppressed - the legs a schedule reads.</summary>
        public List<double[]> CentrelinePointsAsDeclaredMm;
    }

    /// <summary>
    /// What the Revit side read off ONE FabricSheet (welded wire mesh): its type, the wires it carries AS PLACED -
    /// every wire centreline Revit returns for each direction, cut by the host and the fabric area - each wire
    /// type's diameter, and Revit's own masses, published beside the steel volume derived from the wires.
    /// Millimetres and kilograms, as read, nothing derived.
    /// </summary>
    public sealed class FabricSheetFacts
    {
        public long Id;
        public string UniqueId;
        public string Name;
        /// <summary>The fabric area that laid this sheet, or null for a single placed sheet.</summary>
        public long? FabricAreaId;
        public long? HostId;
        public bool HostResolved;
        public string HostCategory;
        public string HostName;
        public long? SheetTypeId;
        public string SheetTypeName;
        /// <summary>The sheet's fabric number (its schedule mark).</summary>
        public string FabricNumber;
        public string MajorWireTypeName;
        public double? MajorWireDiameterMm;
        /// <summary>One length per major wire centreline Revit returned, or null when they would not read.</summary>
        public List<double> MajorWireLengthsMm;
        public string MinorWireTypeName;
        public double? MinorWireDiameterMm;
        public List<double> MinorWireLengthsMm;
        public double? CutOverallLengthMm;
        public double? CutOverallWidthMm;
        public bool? IsBent;
        /// <summary>Revit's FabricSheet.CutSheetMass, in kg.</summary>
        public double? RevitCutSheetMassKg;
        /// <summary>Revit's FabricSheetType.SheetMass (the whole, uncut sheet), in kg.</summary>
        public double? RevitTypeSheetMassKg;
        /// <summary>Revit's FabricSheetType.SheetMassUnit, in kg per square metre.</summary>
        public double? RevitTypeMassPerAreaKgM2;
    }

    /// <summary>What an element id names, as far as a steel takeoff is concerned.</summary>
    public enum ReinforcementKind
    {
        RebarSet, SystemBar, AreaSystem, PathSystem, FabricArea, FabricSheet, NotReinforcement, Missing
    }

    public static class RebarSystemBars
    {
        public const string KindArea = "area";
        public const string KindPath = "path";
        public const string KindFabric = "fabric";

        /// <summary>Whether a takeoff row is a fabric sheet (system.kind = fabric).</summary>
        public static bool IsFabric(JObject row)
        {
            return (string)SystemOf(row)?["kind"] == KindFabric;
        }

        /// <summary>
        /// ONE FABRIC SHEET as a takeoff row, in the shape RebarFacts gives a set, so the takeoff, reconcile and the
        /// schedule aggregate it with no second path. Its "bars" are its WIRES: quantity is how many wire centrelines
        /// Revit returned for the sheet as placed, total length their summed length, and volume the steel of those
        /// wires - each wire's length times the area of its wire type's diameter. The declared density weighs it, as
        /// it weighs every bar. Revit's own masses (cut sheet, and the type's) are published beside it in `fabric`,
        /// never substituted for it: they are Revit's arithmetic, with Revit's density, not the one declared.
        /// </summary>
        public static JObject FabricRow(FabricSheetFacts f)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            var reasons = new JArray();
            var o = new JObject
            {
                ["id"] = f.Id,
                ["unique_id"] = S(f.UniqueId),
                ["name"] = S(f.Name),
                ["system"] = new JObject
                {
                    ["kind"] = KindFabric,
                    ["id"] = f.FabricAreaId.HasValue ? (JToken)f.FabricAreaId.Value : JValue.CreateNull(),
                    ["means"] = "this row is ONE FABRIC SHEET (welded wire mesh), laid by the fabric area named in id (null " +
                                "for a sheet placed on its own). Its bars are its wires: counted, measured and weighed from " +
                                "the wires Revit returns for the sheet as placed."
                }
            };
            o["host"] = f.HostResolved && f.HostId.HasValue
                ? new JObject { ["id"] = f.HostId.Value, ["resolved"] = true, ["category"] = S(f.HostCategory), ["name"] = S(f.HostName) }
                : new JObject
                {
                    ["id"] = f.HostId ?? -1,
                    ["resolved"] = false,
                    ["why"] = f.HostId.HasValue ? "the sheet names a host id that is not in this document." : "the sheet reports no host id."
                };

            bool sameWire = f.MajorWireDiameterMm.HasValue && f.MinorWireDiameterMm.HasValue &&
                            Math.Abs(f.MajorWireDiameterMm.Value - f.MinorWireDiameterMm.Value) < 1e-6;
            var type = new JObject();
            if (!f.SheetTypeId.HasValue)
            {
                type["resolved"] = false;
                reasons.Add(StructuralCoverage.Reason("fabric_sheet_type", "the sheet's type did not resolve to a fabric sheet type.", f.Id));
            }
            else
            {
                type["resolved"] = true;
                type["id"] = f.SheetTypeId.Value;
                type["name"] = S(f.SheetTypeName);
                type["kind"] = "fabric_sheet_type";
                // ONE diameter only when both directions' wires share it; otherwise there is no single one to group by.
                type["nominal_diameter_mm"] = sameWire ? Round(f.MajorWireDiameterMm) : JValue.CreateNull();
                type["model_diameter_mm"] = sameWire ? Round(f.MajorWireDiameterMm) : JValue.CreateNull();
                if (!sameWire)
                    type["diameter_why"] = "the major and minor wires are of different (or unreadable) diameters - see fabric.major and fabric.minor.";
            }
            o["bar_type"] = type;
            o["shape"] = new JObject { ["id"] = -1, ["name"] = null, ["resolved"] = false, ["why"] = "a fabric sheet has no rebar shape." };
            o["style_horizun"] = null;
            o["layout"] = new JObject { ["rule"] = "fabric_sheet", ["accessor"] = "fabric" };
            o["terminations"] = new JArray();

            JObject major = Direction(f.MajorWireTypeName, f.MajorWireDiameterMm, f.MajorWireLengthsMm, "major", f.Id, reasons);
            JObject minor = Direction(f.MinorWireTypeName, f.MinorWireDiameterMm, f.MinorWireLengthsMm, "minor", f.Id, reasons);
            bool wiresRead = f.MajorWireLengthsMm != null && f.MinorWireLengthsMm != null;
            bool volumeRead = wiresRead && f.MajorWireDiameterMm.HasValue && f.MinorWireDiameterMm.HasValue;
            int? wires = wiresRead ? (int?)(f.MajorWireLengthsMm.Count + f.MinorWireLengthsMm.Count) : null;
            double? length = wiresRead ? (double?)(f.MajorWireLengthsMm.Sum() + f.MinorWireLengthsMm.Sum()) : null;
            double? volume = volumeRead
                ? (double?)(WireVolumeM3(f.MajorWireDiameterMm.Value, f.MajorWireLengthsMm) + WireVolumeM3(f.MinorWireDiameterMm.Value, f.MinorWireLengthsMm))
                : null;
            if (wiresRead && wires == 0)
                reasons.Add(StructuralCoverage.Reason("fabric_wires", "Revit returned no wire for this sheet, so its steel is not known.", f.Id));

            o["measured"] = new JObject
            {
                ["total_length_mm"] = Round(length),
                ["total_length_m"] = Round(length.HasValue ? length.Value / 1000.0 : (double?)null, 4),
                ["volume_m3"] = Round(volume, 6),
                ["quantity"] = wires,
                ["schedule_mark"] = S(f.FabricNumber),
                ["bar_centreline_length_mm"] = null,
                ["source"] = "the wires Revit returns for the sheet as placed (FabricSheet.GetWireCenterlines, each " +
                             "direction) and each wire type's diameter: quantity = wires, total length = their summed " +
                             "length, volume = sum of length x pi x diameter^2 / 4"
            };
            o["fabric"] = new JObject
            {
                ["sheet_type"] = new JObject { ["id"] = f.SheetTypeId.HasValue ? (JToken)f.SheetTypeId.Value : JValue.CreateNull(), ["name"] = S(f.SheetTypeName) },
                ["fabric_number"] = S(f.FabricNumber),
                ["major"] = major,
                ["minor"] = minor,
                ["cut_overall_length_mm"] = Round(f.CutOverallLengthMm),
                ["cut_overall_width_mm"] = Round(f.CutOverallWidthMm),
                ["bent"] = f.IsBent.HasValue ? (JToken)f.IsBent.Value : JValue.CreateNull(),
                ["revit_cut_sheet_mass_kg"] = Round(f.RevitCutSheetMassKg),
                ["revit_type_sheet_mass_kg"] = Round(f.RevitTypeSheetMassKg),
                ["revit_type_mass_per_area_kg_m2"] = Round(f.RevitTypeMassPerAreaKgM2),
                ["revit_mass_means"] =
                    "Revit's own numbers: the CUT sheet's mass, the whole sheet type's mass and its mass per square metre. " +
                    "They are published to check the takeoff against, not summed into it: the takeoff weighs the wires' " +
                    "volume with the density YOU declared, as it weighs every bar."
            };
            o["geometry"] = new JObject
            {
                ["centreline"] = "not_applicable",
                ["centreline_why"] = "a fabric sheet is many wires, not one bar: its wires are summed in measured, one direction at a time in fabric."
            };
            o["coverage"] = StructuralCoverage.Declare(
                reasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial,
                1, reasons.Count == 0 ? 0 : 1, reasons);
            return o;
        }

        /// <summary>The steel of wires of one diameter: sum of length x pi x d^2 / 4, in cubic metres.</summary>
        public static double WireVolumeM3(double diameterMm, IEnumerable<double> lengthsMm)
        {
            double area = Math.PI * diameterMm * diameterMm / 4.0;
            return (lengthsMm ?? new double[0]).Sum() * area / 1e9;
        }

        private static JObject Direction(string typeName, double? dia, List<double> lengths, string which, long id, JArray reasons)
        {
            if (lengths == null)
                reasons.Add(StructuralCoverage.Reason("fabric_wires", "the sheet's " + which + " wires would not return their centrelines, so its steel is not known.", id));
            if (!dia.HasValue)
                reasons.Add(StructuralCoverage.Reason("fabric_wire_type", "the " + which + " wire type would not report its diameter, so the volume of its wires is not known.", id));
            return new JObject
            {
                ["wire_type"] = S(typeName),
                ["diameter_mm"] = Round(dia),
                ["wires"] = lengths == null ? (JToken)JValue.CreateNull() : lengths.Count,
                ["length_mm"] = lengths == null ? JValue.CreateNull() : Round(lengths.Sum()),
                ["volume_m3"] = lengths == null || !dia.HasValue ? JValue.CreateNull() : Round(WireVolumeM3(dia.Value, lengths), 9)
            };
        }

        /// <summary>The system word for a system element kind, or null for anything that is not area or path.</summary>
        public static string KindOf(ReinforcementKind k)
        {
            return k == ReinforcementKind.AreaSystem ? KindArea : k == ReinforcementKind.PathSystem ? KindPath : null;
        }

        /// <summary>One system bar as a RebarFacts-shaped row, with `system` and its own coverage.</summary>
        public static JObject Row(SystemBarFacts f)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            var reasons = new JArray();
            var o = new JObject
            {
                ["id"] = f.Id,
                ["unique_id"] = S(f.UniqueId),
                ["name"] = S(f.Name),
                ["system"] = new JObject
                {
                    ["kind"] = S(f.SystemKind),
                    ["id"] = f.SystemId.HasValue ? (JToken)f.SystemId.Value : JValue.CreateNull(),
                    ["means"] = "this bar is a RebarInSystem laid by an area or path reinforcement system, not a rebar " +
                                "set. It is counted, measured and weighed exactly as a set is."
                }
            };
            if (f.SystemKind != KindArea && f.SystemKind != KindPath)
                reasons.Add(StructuralCoverage.Reason("reinforcement_system",
                    "the bar's system did not resolve to an area or path reinforcement element.", f.Id));

            o["host"] = f.HostResolved && f.HostId.HasValue
                ? new JObject
                {
                    ["id"] = f.HostId.Value,
                    ["resolved"] = true,
                    ["category"] = S(f.HostCategory),
                    ["name"] = S(f.HostName)
                }
                : new JObject
                {
                    ["id"] = f.HostId ?? -1,
                    ["resolved"] = false,
                    ["why"] = f.HostId.HasValue ? "the bar names a host id that is not in this document."
                                                : "the bar reports no host id."
                };

            var type = new JObject();
            if (!f.BarTypeId.HasValue)
            {
                type["resolved"] = false;
                reasons.Add(StructuralCoverage.Reason("bar_type", "the bar type did not resolve to a RebarBarType.", f.Id));
            }
            else
            {
                type["resolved"] = true;
                type["id"] = f.BarTypeId.Value;
                type["name"] = S(f.BarTypeName);
                type["nominal_diameter_mm"] = Round(f.NominalDiameterMm);
                type["model_diameter_mm"] = Round(f.ModelDiameterMm);
            }
            o["bar_type"] = type;

            o["shape"] = new JObject
            {
                ["id"] = f.ShapeId ?? -1,
                ["name"] = S(f.ShapeName),
                ["resolved"] = f.ShapeId.HasValue
            };
            o["style_horizun"] = S(f.Style);

            o["layout"] = new JObject
            {
                ["rule"] = S(f.LayoutRule),
                ["number_of_bar_positions"] = f.NumberOfBarPositions,
                ["quantity"] = f.Quantity,
                ["max_spacing_mm"] = Round(f.MaxSpacingMm),
                ["array_length_mm"] = Round(f.ArrayLengthMm),
                ["accessor"] = "system",
                ["means"] = "the layout of a system bar is the system's: its spacing and extent come from the area " +
                            "or path element and are read off the bar as Revit laid it."
            };

            o["terminations"] = new JArray(
                Termination(0, f.HookTypeId0, f.HookTypeName0, f.HookReadable0),
                Termination(1, f.HookTypeId1, f.HookTypeName1, f.HookReadable1));

            var measured = new JObject
            {
                ["total_length_mm"] = Round(f.TotalLengthMm),
                ["total_length_m"] = Round(f.TotalLengthMm.HasValue ? f.TotalLengthMm.Value / 1000.0 : (double?)null, 4),
                ["volume_m3"] = Round(f.VolumeM3, 6),
                ["quantity"] = f.Quantity,
                ["schedule_mark"] = S(f.ScheduleMark),
                ["bar_centreline_length_mm"] = Round(f.OneBarCentrelineMm),
                ["source"] = "read from the element, not derived from the plan"
            };
            o["measured"] = measured;
            if (!f.TotalLengthMm.HasValue)
                reasons.Add(StructuralCoverage.Reason("total_length", "the bar would not report its total length.", f.Id));
            if (!f.VolumeM3.HasValue)
                reasons.Add(StructuralCoverage.Reason("volume", "the bar would not report its volume.", f.Id));
            if (!f.Quantity.HasValue)
                reasons.Add(StructuralCoverage.Reason("quantity", "the bar would not report how many bars it stands for.", f.Id));

            var g = new JObject();
            if (f.CentrelinePointsMm == null && !f.OneBarCentrelineMm.HasValue)
            {
                g["centreline"] = "unreadable";
                reasons.Add(StructuralCoverage.Reason("centreline", "the bar would not return its centreline curves.", f.Id));
            }
            else
            {
                g["centreline"] = "read";
                if (f.CentrelinePointsMm != null) g["centreline_points_mm"] = Points(f.CentrelinePointsMm);
                if (f.CentrelinePointsAsDeclaredMm != null)
                    g["centreline_points_as_declared_mm"] = Points(f.CentrelinePointsAsDeclaredMm);
                g["centreline_length_mm"] = Round(f.OneBarCentrelineMm);
                g["centreline_length_means"] =
                    "the length of ONE bar of this system bar as Revit draws it, hooks included.";
            }
            o["geometry"] = g;

            o["coverage"] = StructuralCoverage.Declare(
                reasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial,
                1, reasons.Count == 0 ? 0 : 1, reasons);
            return o;
        }

        /// <summary>The `system` block of a row, or null for a rebar set.</summary>
        public static JObject SystemOf(JObject row)
        {
            return row == null ? null : row["system"] as JObject;
        }

        private static JObject Termination(int end, long? hookId, string hookName, bool readable)
        {
            return new JObject
            {
                ["end"] = end,
                ["hook_readable"] = readable,
                ["hook_type_id"] = hookId ?? -1,
                ["hook_type_name"] = S(hookName),
                ["has_hook"] = hookId.HasValue,
                // The orientation of a system bar's hook is the system's, not the bar's, and
                // Revit publishes no per-end orientation on a RebarInSystem.
                ["orientation"] = null,
                ["coupler_id"] = -1,
                ["coupler_readable"] = true
            };
        }

        private static JToken S(string v)
        {
            return v == null ? JValue.CreateNull() : new JValue(v);
        }

        private static JArray Points(List<double[]> pts)
        {
            var a = new JArray();
            foreach (double[] p in pts)
                a.Add(new JArray(Math.Round(p[0], 3), Math.Round(p[1], 3), Math.Round(p[2], 3)));
            return a;
        }

        private static JToken Round(double? v, int digits = 3)
        {
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return JValue.CreateNull();
            return new JValue(Math.Round(v.Value, digits));
        }
    }

    /// <summary>
    /// Which reinforcement a takeoff reads: rebar sets, system bars (an area or path system named by
    /// id scopes to its bars), fabric sheets (a fabric area named by id scopes to its sheets), and the
    /// stray ids it cannot read - each named as a reason.
    /// </summary>
    public sealed class RebarScope
    {
        public List<long> SetIds = new List<long>();
        public List<long> SystemBarIds = new List<long>();
        /// <summary>Fabric SHEETS in scope: each is read as a row of its own.</summary>
        public List<long> FabricIds = new List<long>();
        /// <summary>Area and path system ids that were named and replaced by their bars.</summary>
        public List<long> SystemIdsExpanded = new List<long>();
        /// <summary>Fabric area ids that were named and replaced by their sheets.</summary>
        public List<long> FabricAreaIdsExpanded = new List<long>();
        /// <summary>Ids that are no reinforcement, or not in the document.</summary>
        public List<long> IgnoredIds = new List<long>();
        public JArray Reasons = new JArray();

        /// <summary>
        /// The scope of `ids`, each classified by `kindOf`. An area or path system scopes to the bars
        /// `membersOf` lists, a fabric area to its sheets (null = they could not be listed, which is a reason).
        /// Every id once, in id order. For the whole document, pass every rebar set, system bar and fabric element.
        /// </summary>
        public static RebarScope FromIds(IEnumerable<long> ids, Func<long, ReinforcementKind> kindOf,
                                         Func<long, IList<long>> membersOf)
        {
            var s = new RebarScope();
            var sets = new SortedSet<long>();
            var bars = new SortedSet<long>();
            var fabric = new SortedSet<long>();
            var systems = new SortedSet<long>();
            var fabricAreas = new SortedSet<long>();
            var ignored = new SortedSet<long>();
            foreach (long id in (ids ?? new long[0]).Distinct().OrderBy(v => v))
            {
                ReinforcementKind k = kindOf(id);
                switch (k)
                {
                    case ReinforcementKind.RebarSet: sets.Add(id); break;
                    case ReinforcementKind.SystemBar: bars.Add(id); break;
                    case ReinforcementKind.AreaSystem:
                    case ReinforcementKind.PathSystem:
                        systems.Add(id);
                        IList<long> members = null;
                        try { members = membersOf(id); } catch { members = null; }
                        if (members == null)
                            s.Reasons.Add(StructuralCoverage.Reason("reinforcement_system",
                                "the " + RebarSystemBars.KindOf(k) + " reinforcement system would not list its bars, " +
                                "so they may be missing from a total.", id));
                        else foreach (long m in members) bars.Add(m);
                        break;
                    case ReinforcementKind.FabricArea:
                        fabricAreas.Add(id);
                        IList<long> sheets = null;
                        try { sheets = membersOf(id); } catch { sheets = null; }
                        if (sheets == null)
                            s.Reasons.Add(StructuralCoverage.Reason("fabric",
                                "the fabric area would not list its sheets, so their steel may be missing from a total.", id));
                        else foreach (long m in sheets) fabric.Add(m);
                        break;
                    case ReinforcementKind.FabricSheet:
                        fabric.Add(id);
                        break;
                    default:
                        ignored.Add(id);
                        s.Reasons.Add(StructuralCoverage.Reason("element_ids",
                            k == ReinforcementKind.Missing
                                ? "this id is not an element of this document, so nothing under it was read."
                                : "this id is not reinforcement (not a rebar set, a system bar, an area or path system, " +
                                  "or fabric), so it carries no bars to take off. To take off the bars a host carries, " +
                                  "pass host_id.", id));
                        break;
                }
            }
            s.SetIds = sets.ToList();
            s.SystemBarIds = bars.ToList();
            s.FabricIds = fabric.ToList();
            s.SystemIdsExpanded = systems.ToList();
            s.FabricAreaIdsExpanded = fabricAreas.ToList();
            s.IgnoredIds = ignored.ToList();
            return s;
        }

        /// <summary>
        /// Keep only what `host` carries - sets, system bars and fabric sheets alike. One whose host would not
        /// read is not silently dropped: it is a reason, because whether it belongs to this host is unknown.
        /// </summary>
        public void FilterByHost(long host, Func<long, long?> hostOf)
        {
            var kept = new List<long>();
            var keptBars = new List<long>();
            var keptFabric = new List<long>();
            var unknown = new List<long>();
            foreach (long id in SetIds) Keep(id, host, hostOf, kept, unknown);
            foreach (long id in SystemBarIds) Keep(id, host, hostOf, keptBars, unknown);
            foreach (long id in FabricIds) Keep(id, host, hostOf, keptFabric, unknown);

            var reasons = new JArray();
            foreach (JObject r in Reasons.OfType<JObject>()) reasons.Add(r);
            foreach (long id in unknown)
                reasons.Add(StructuralCoverage.Reason("host",
                    "the host of this bar or sheet would not read, so whether it belongs to host_id is unknown; it is " +
                    "not in the totals.", id));
            SetIds = kept;
            SystemBarIds = keptBars;
            FabricIds = keptFabric;
            Reasons = reasons;
        }

        private static void Keep(long id, long host, Func<long, long?> hostOf, List<long> kept, List<long> unknown)
        {
            long? h = null;
            try { h = hostOf(id); } catch { h = null; }
            if (h.HasValue) { if (h.Value == host) kept.Add(id); }
            else unknown.Add(id);
        }

        /// <summary>
        /// The coverage of a takeoff over `rows` (what was read): complete only when every row is complete
        /// and nothing in scope was set aside. Every reason is named.
        /// </summary>
        public JObject Coverage(JArray rows)
        {
            var reasons = new JArray();
            foreach (JToken t in Reasons) reasons.Add(t.DeepClone());
            int measured = 0;
            foreach (JObject row in (rows ?? new JArray()).OfType<JObject>())
            {
                measured++;
                string word = (string)(row["coverage"] == null ? null : row["coverage"]["coverage"]);
                if (word != null && word != StructuralCoverage.Complete)
                {
                    long id = row.Value<long?>("id") ?? -1;
                    bool fabric = RebarSystemBars.IsFabric(row);
                    bool system = !fabric && RebarSystemBars.SystemOf(row) != null;
                    reasons.Add(StructuralCoverage.Reason(fabric ? "fabric" : system ? "rebar_in_system" : "rebar",
                        (fabric ? "fabric sheet " : system ? "system bar " : "set ") + id + " could not be described " +
                        "completely, so it may be missing from a total.", id));
                }
            }
            return StructuralCoverage.Declare(
                reasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial,
                measured, reasons.Count, reasons);
        }

        /// <summary>The scope block published beside a takeoff or schedule.</summary>
        public JObject Describe(int idsGiven, long hostFilter)
        {
            return new JObject
            {
                ["bar_sets_in_scope"] = SetIds.Count,
                ["system_bars_in_scope"] = SystemBarIds.Count,
                ["system_ids_expanded"] = new JArray(SystemIdsExpanded.Select(v => (object)v).ToArray()),
                ["fabric_in_scope"] = FabricIds.Count,
                ["fabric_area_ids_expanded"] = new JArray(FabricAreaIdsExpanded.Select(v => (object)v).ToArray()),
                ["ids_not_read"] = new JArray(IgnoredIds.Select(v => (object)v).ToArray()),
                ["element_ids_given"] = idsGiven,
                ["host_id"] = hostFilter >= 0 ? (JToken)hostFilter : JValue.CreateNull(),
                ["system_bars_means"] =
                    "the bars of area and path reinforcement are RebarInSystem elements, read here exactly as rebar " +
                    "sets are and marked system: {kind, id} on their rows. An area or path system id in element_ids " +
                    "scopes to its bars. fabric_in_scope counts fabric SHEETS: each is a row marked system: {kind: " +
                    "fabric, id: its fabric area}, whose bars are its wires; a fabric area id scopes to its sheets."
            };
        }
    }
}

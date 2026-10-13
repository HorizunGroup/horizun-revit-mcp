// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// FABRIC IN THE TAKEOFF (acero 3.0, stage 1c). mode=quantities, mode=reconcile and
// mode=bending_schedule used to name fabric as a reason and leave its steel out, so
// any slab with mesh had a takeoff that was partial by construction. A fabric sheet
// is now ONE ROW: its bars are its wires as placed, its steel is the wires' volume
// (length x pi x d^2 / 4 per wire type), weighed with the declared density like
// every bar, and Revit's own cut-sheet mass travels beside it to check against.
//
// The facts are what the Revit side reads off one FabricSheet: wire centreline
// lengths per direction (FabricSheet.GetWireCenterlines), each wire type's diameter,
// CutSheetMass and the type's SheetMass / SheetMassUnit - never a shape invented here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarFabricTakeoffTests
    {
        private const double Density = 7850;

        /// <summary>A sheet 2000 x 1500 cut by its area: 8 major wires of 2000 (6 mm) and 11 minor of 1500 (5 mm).</summary>
        private static FabricSheetFacts Sheet(long id = 801, long? area = 800, string number = "1")
        {
            return new FabricSheetFacts
            {
                Id = id, UniqueId = "u-" + id, Name = "Fabric Sheet",
                FabricAreaId = area,
                HostId = 400, HostResolved = true, HostCategory = "Floors", HostName = "Floor 200",
                SheetTypeId = 90, SheetTypeName = "W11/4",
                FabricNumber = number,
                MajorWireTypeName = "W6", MajorWireDiameterMm = 6, MajorWireLengthsMm = Enumerable.Repeat(2000.0, 8).ToList(),
                MinorWireTypeName = "W5", MinorWireDiameterMm = 5, MinorWireLengthsMm = Enumerable.Repeat(1500.0, 11).ToList(),
                CutOverallLengthMm = 2000, CutOverallWidthMm = 1500, IsBent = false,
                RevitCutSheetMassKg = 4.83, RevitTypeSheetMassKg = 20.1, RevitTypeMassPerAreaKgM2 = 1.61
            };
        }

        private static double Volume(FabricSheetFacts f) =>
            (Math.PI * 36 / 4 * f.MajorWireLengthsMm.Sum() + Math.PI * 25 / 4 * f.MinorWireLengthsMm.Sum()) / 1e9;

        private static JObject SetRow(long id, string mark, int quantity, double lengthM, double volume)
        {
            return new JObject
            {
                ["id"] = id,
                ["system"] = null,
                ["measured"] = new JObject
                {
                    ["schedule_mark"] = mark, ["quantity"] = quantity, ["total_length_m"] = lengthM,
                    ["total_length_mm"] = lengthM * 1000, ["volume_m3"] = volume, ["bar_centreline_length_mm"] = lengthM * 1000 / quantity
                },
                ["bar_type"] = new JObject { ["resolved"] = true, ["id"] = 77, ["name"] = "10M", ["nominal_diameter_mm"] = 10 },
                ["host"] = new JObject { ["id"] = 400, ["category"] = "Floors" },
                ["shape"] = new JObject { ["id"] = 88, ["name"] = "M_00" },
                ["geometry"] = new JObject
                {
                    ["centreline_length_mm"] = lengthM * 1000 / quantity,
                    ["centreline_points_as_declared_mm"] = new JArray(new JArray(0.0, 0.0, 0.0), new JArray(lengthM * 1000 / quantity, 0.0, 0.0))
                },
                ["coverage"] = new JObject { ["coverage"] = "complete" }
            };
        }

        [Fact]
        public void A_fabric_sheet_is_one_row_whose_bars_are_its_wires_and_whose_steel_is_their_volume()
        {
            FabricSheetFacts f = Sheet();
            JObject r = RebarSystemBars.FabricRow(f);
            Assert.Equal(801, (long)r["id"]);
            Assert.Equal("fabric", (string)r["system"]["kind"]);
            Assert.Equal(800, (long)r["system"]["id"]);
            Assert.True(RebarSystemBars.IsFabric(r));
            Assert.Equal("W11/4", (string)r["bar_type"]["name"]);
            Assert.Equal(90, (long)r["bar_type"]["id"]);
            // Two wire diameters: no single diameter to group by, and it says why.
            Assert.Equal(JTokenType.Null, r["bar_type"]["nominal_diameter_mm"].Type);
            Assert.NotNull(r["bar_type"]["diameter_why"]);
            Assert.Equal(19, (int)r["measured"]["quantity"]);
            Assert.Equal(8 * 2000 + 11 * 1500, (double)r["measured"]["total_length_mm"], 6);
            Assert.Equal(Math.Round(Volume(f), 6), (double)r["measured"]["volume_m3"], 9);
            Assert.Equal("1", (string)r["measured"]["schedule_mark"]);
            Assert.Equal(8, (int)r["fabric"]["major"]["wires"]);
            Assert.Equal(5.0, (double)r["fabric"]["minor"]["diameter_mm"]);
            Assert.Equal(4.83, (double)r["fabric"]["revit_cut_sheet_mass_kg"], 6);
            Assert.Equal(1.61, (double)r["fabric"]["revit_type_mass_per_area_kg_m2"], 6);
            Assert.Equal("complete", (string)r["coverage"]["coverage"]);
            // A sheet placed on its own has no fabric area.
            Assert.Equal(JTokenType.Null, RebarSystemBars.FabricRow(Sheet(area: null))["system"]["id"].Type);
            // One wire diameter both ways: that is the sheet's diameter.
            FabricSheetFacts same = Sheet(); same.MinorWireDiameterMm = 6;
            Assert.Equal(6.0, (double)RebarSystemBars.FabricRow(same)["bar_type"]["nominal_diameter_mm"]);
        }

        [Fact]
        public void A_sheet_whose_wires_or_wire_diameter_would_not_read_is_partial_by_name_and_never_a_zero()
        {
            FabricSheetFacts noWires = Sheet(); noWires.MinorWireLengthsMm = null;
            JObject r = RebarSystemBars.FabricRow(noWires);
            Assert.Equal("partial", (string)r["coverage"]["coverage"]);
            Assert.Equal(JTokenType.Null, r["measured"]["volume_m3"].Type);
            Assert.Equal(JTokenType.Null, r["measured"]["quantity"].Type);
            Assert.Contains(((JArray)r["coverage"]["reasons"]).OfType<JObject>(), x => (string)x["what"] == "fabric_wires");

            FabricSheetFacts noDia = Sheet(); noDia.MajorWireDiameterMm = null;
            JObject r2 = RebarSystemBars.FabricRow(noDia);
            Assert.Equal(JTokenType.Null, r2["measured"]["volume_m3"].Type);
            Assert.Equal(19, (int)r2["measured"]["quantity"]);
            Assert.Contains(((JArray)r2["coverage"]["reasons"]).OfType<JObject>(), x => (string)x["what"] == "fabric_wire_type");

            // In the scope's coverage the sheet is named as fabric, and the takeoff is partial.
            RebarScope s = RebarScope.FromIds(new long[] { 801 }, id => ReinforcementKind.FabricSheet, id => null);
            JObject c = s.Coverage(new JArray(r));
            Assert.Equal("partial", (string)c["coverage"]);
            Assert.Contains(((JArray)c["reasons"]).OfType<JObject>(), x => (string)x["what"] == "fabric" && (long)x["element_id"] == 801);
            // Read in full, the same scope is complete.
            Assert.Equal("complete", (string)s.Coverage(new JArray(RebarSystemBars.FabricRow(Sheet())))["coverage"]);
        }

        [Fact]
        public void The_takeoff_sums_the_fabric_wires_into_its_totals_weighs_them_and_publishes_revit_s_mass_beside()
        {
            FabricSheetFacts a = Sheet(801), b = Sheet(802);
            var rows = new JArray(SetRow(10, "TX1", 15, 90, 0.00706), RebarSystemBars.FabricRow(a), RebarSystemBars.FabricRow(b));
            string error;
            JObject t = RebarTakeoff.Group(rows, new[] { RebarTakeoffKey.Mark }, Density, "test", out error);
            Assert.Null(error);
            JObject totals = (JObject)t["totals"];
            Assert.Equal(15 + 19 + 19, (int)totals["bars"]);
            Assert.Equal(Math.Round(90 + 2 * 32.5, 4), (double)totals["total_length_m"], 4);
            double volume = 0.00706 + 2 * Math.Round(Volume(a), 6);
            Assert.Equal(Math.Round(volume, 6), (double)totals["volume_m3"], 6);
            Assert.Equal(Math.Round(volume * Density, 3), (double)totals["mass_kg"], 3);
            Assert.True((bool)totals["complete"]);
            // Fabric is counted on its own - not as a system bar - and Revit's mass for it rides beside.
            Assert.Equal(0, (int)totals["sets_from_systems"]);
            Assert.Equal(2, (int)totals["fabric_sheets"]);
            Assert.Equal(2, (int)t["fabric_sheets"]);
            Assert.Equal(38, (int)totals["fabric"]["wires"]);
            Assert.Equal(9.66, (double)totals["fabric"]["revit_cut_sheet_mass_kg"], 6);
            Assert.Equal(Math.Round(2 * Math.Round(Volume(a), 6) * Density, 3), (double)totals["fabric"]["mass_kg_counted"], 3);
            // Grouped by mark, the sheets sit under their fabric number.
            JObject g = ((JArray)t["groups"]).OfType<JObject>().Single(x => (string)x["group"]["mark"] == "1");
            Assert.Equal(2, (int)g["fabric"]["sheets"]);
            Assert.Equal(38, (int)g["bars"]);
            // A sheet Revit gave no mass for leaves Revit's number null, never a short sum; the takeoff still weighs it.
            FabricSheetFacts noMass = Sheet(803); noMass.RevitCutSheetMassKg = null;
            JObject t2 = RebarTakeoff.Group(new JArray(RebarSystemBars.FabricRow(a), RebarSystemBars.FabricRow(noMass)),
                                            new[] { RebarTakeoffKey.Mark }, Density, "test", out error);
            Assert.Equal(JTokenType.Null, t2["totals"]["fabric"]["revit_cut_sheet_mass_kg"].Type);
            Assert.True((bool)t2["totals"]["complete"]);
        }

        [Fact]
        public void The_bending_schedule_lists_fabric_by_sheet_type_beside_the_bars_and_weighs_both()
        {
            FabricSheetFacts a = Sheet(801, 800, "1"), b = Sheet(802, 800, "2");
            var rows = new JArray(SetRow(10, "TX1", 15, 90, 0.00706), RebarSystemBars.FabricRow(a), RebarSystemBars.FabricRow(b));
            string code, error;
            JObject r = RebarBendingSchedule.Build(rows, Density, "test", 0, out code, out error);
            Assert.Null(code);
            // The bar lines carry the bars only; a sheet is not a bent bar with legs.
            JObject line = Assert.Single(((JArray)r["rows"]).OfType<JObject>());
            Assert.Equal("TX1", (string)line["mark"]);
            Assert.Equal(15, (int)r["totals"]["bars"]);
            JObject fab = (JObject)r["fabric"];
            JObject ft = Assert.Single(((JArray)fab["rows"]).OfType<JObject>());
            Assert.Equal("W11/4", (string)ft["sheet_type"]["name"]);
            Assert.Equal(2, (int)ft["sheets"]);
            Assert.Equal(38, (int)ft["wires"]);
            Assert.Equal(new[] { "1", "2" }, ((JArray)ft["fabric_numbers"]).Select(x => (string)x).ToArray());
            Assert.Equal(new long[] { 800 }, ((JArray)ft["fabric_area_ids"]).Select(x => (long)x).ToArray());
            double fkg = Math.Round(2 * Math.Round(Volume(a), 6) * Density, 3);
            Assert.Equal(fkg, (double)fab["totals"]["total_kg"], 3);
            Assert.Equal(9.66, (double)fab["totals"]["revit_cut_sheet_mass_kg"], 6);
            Assert.Equal(Math.Round((double)r["totals"]["total_kg"] + fkg, 3), (double)r["totals_with_fabric"]["total_kg"], 3);
            Assert.True((bool)r["complete"]);
            Assert.DoesNotContain("fabric", ((JArray)r["not_included"]).Select(x => (string)x["what"]));
            // A sheet that would not read makes the schedule incomplete.
            FabricSheetFacts bad = Sheet(803); bad.MajorWireLengthsMm = null;
            JObject r2 = RebarBendingSchedule.Build(new JArray(RebarSystemBars.FabricRow(bad)), Density, "test", 0, out code, out error);
            Assert.False((bool)r2["complete"]);
            Assert.Equal(JTokenType.Null, r2["fabric"]["totals"]["total_kg"].Type);
        }

        [Fact]
        public void Wire_volume_is_length_times_the_area_of_the_wire_diameter()
        {
            // 1 m of 10 mm wire: pi x 25 mm2 x 1000 mm = 78,539.8 mm3.
            Assert.Equal(Math.PI * 25 * 1000 / 1e9, RebarSystemBars.WireVolumeM3(10, new[] { 400.0, 600.0 }), 12);
            Assert.Equal(0.0, RebarSystemBars.WireVolumeM3(10, new double[0]));
        }
    }
}

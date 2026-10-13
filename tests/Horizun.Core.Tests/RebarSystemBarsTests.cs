// -----------------------------------------------------------------------------
// The bars of an area or path reinforcement system are RebarInSystem elements,
// not rebar sets. A takeoff that read only rebar sets left them out and still
// said `complete`: steel missing from an order under the one word that promises
// nothing is missing. These pin the row a system bar becomes (the same shape the
// takeoff and the schedule aggregate, with where it came from), the scope an id
// resolves to, and the coverage. Fabric (stage 1c) is READ now - see
// RebarFabricTakeoffTests - and only a sheet that would not read is a reason.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarSystemBarsTests
    {
        // The shape the Revit side fills from one RebarInSystem: values in mm and m3, as read.
        private static SystemBarFacts AreaBar(long id = 501, long system = 500, int quantity = 16,
                                              double lengthEachMm = 3000, string mark = null)
        {
            return new SystemBarFacts
            {
                Id = id, UniqueId = "u-" + id, Name = "Rebar Bar",
                SystemKind = RebarSystemBars.KindArea, SystemId = system,
                HostId = 400, HostResolved = true, HostCategory = "Floors", HostName = "Floor 200",
                BarTypeId = 77, BarTypeName = "16M", NominalDiameterMm = 16, ModelDiameterMm = 19.5,
                ShapeId = 88, ShapeName = "M_00", Style = StructuralStyle.Standard,
                LayoutRule = "MaximumSpacing", NumberOfBarPositions = quantity, Quantity = quantity,
                MaxSpacingMm = 200, ArrayLengthMm = (quantity - 1) * 200.0,
                TotalLengthMm = quantity * lengthEachMm, VolumeM3 = quantity * lengthEachMm / 1000.0 * 0.000201,
                ScheduleMark = mark, OneBarCentrelineMm = lengthEachMm,
                CentrelinePointsMm = new List<double[]> { new[] { 0.0, 0, 0 }, new[] { lengthEachMm, 0, 0 } },
                CentrelinePointsAsDeclaredMm = new List<double[]> { new[] { 0.0, 0, 0 }, new[] { lengthEachMm, 0, 0 } }
            };
        }

        private static JObject SetRow(long id, string mark, string type, double dia, int quantity, double lengthM, double volume)
        {
            return new JObject
            {
                ["id"] = id,
                ["measured"] = new JObject
                {
                    ["schedule_mark"] = mark, ["quantity"] = quantity, ["total_length_m"] = lengthM,
                    ["total_length_mm"] = lengthM * 1000, ["volume_m3"] = volume,
                    ["bar_centreline_length_mm"] = lengthM * 1000 / quantity
                },
                ["bar_type"] = new JObject { ["resolved"] = true, ["id"] = 77, ["name"] = type, ["nominal_diameter_mm"] = dia },
                ["host"] = new JObject { ["id"] = 400, ["category"] = "Floors" },
                ["shape"] = new JObject { ["id"] = 88, ["name"] = "M_00" },
                ["geometry"] = new JObject { ["centreline_length_mm"] = lengthM * 1000 / quantity },
                ["coverage"] = new JObject { ["coverage"] = "complete" }
            };
        }

        // ------------------------------------------------------------ the row

        [Fact]
        public void A_system_bar_becomes_a_row_in_the_shape_the_takeoff_reads_and_says_where_it_came_from()
        {
            JObject r = RebarSystemBars.Row(AreaBar(mark: "A1"));

            Assert.Equal(501, (long)r["id"]);
            Assert.Equal(16, (int)r["measured"]["quantity"]);
            Assert.Equal(48000, (double)r["measured"]["total_length_mm"], 3);
            Assert.Equal(48.0, (double)r["measured"]["total_length_m"], 4);
            Assert.Equal(16 * 3.0 * 0.000201, (double)r["measured"]["volume_m3"], 6);
            Assert.Equal("A1", (string)r["measured"]["schedule_mark"]);
            Assert.Equal(3000, (double)r["measured"]["bar_centreline_length_mm"], 3);
            Assert.Equal("16M", (string)r["bar_type"]["name"]);
            Assert.Equal(16, (double)r["bar_type"]["nominal_diameter_mm"], 3);
            Assert.True((bool)r["bar_type"]["resolved"]);
            Assert.Equal(400, (long)r["host"]["id"]);
            Assert.Equal("Floors", (string)r["host"]["category"]);
            Assert.Equal(88, (long)r["shape"]["id"]);
            Assert.Equal(3000, (double)r["geometry"]["centreline_length_mm"], 3);
            Assert.Equal(2, ((JArray)r["geometry"]["centreline_points_as_declared_mm"]).Count);
            Assert.Equal("area", (string)r["system"]["kind"]);
            Assert.Equal(500, (long)r["system"]["id"]);
            Assert.Equal("complete", (string)r["coverage"]["coverage"]);
        }

        [Fact]
        public void A_system_bar_whose_length_would_not_read_is_partial_and_not_a_zero()
        {
            SystemBarFacts f = AreaBar();
            f.TotalLengthMm = null;
            JObject r = RebarSystemBars.Row(f);

            Assert.Equal(JTokenType.Null, r["measured"]["total_length_mm"].Type);
            Assert.Equal(JTokenType.Null, r["measured"]["total_length_m"].Type);
            Assert.Equal("partial", (string)r["coverage"]["coverage"]);
            Assert.Contains(((JArray)r["coverage"]["reasons"]).Select(t => (string)t["what"]), w => w == "total_length");
        }

        [Fact]
        public void A_system_bar_whose_system_is_not_area_or_path_is_partial()
        {
            SystemBarFacts f = AreaBar();
            f.SystemKind = null;
            JObject r = RebarSystemBars.Row(f);

            Assert.Equal(JTokenType.Null, r["system"]["kind"].Type);
            Assert.Equal("partial", (string)r["coverage"]["coverage"]);
            Assert.Contains(((JArray)r["coverage"]["reasons"]).Select(t => (string)t["what"]), w => w == "reinforcement_system");
        }

        [Fact]
        public void A_system_bar_with_no_bar_type_is_partial()
        {
            SystemBarFacts f = AreaBar();
            f.BarTypeId = null; f.BarTypeName = null; f.NominalDiameterMm = null;
            JObject r = RebarSystemBars.Row(f);
            Assert.False((bool)r["bar_type"]["resolved"]);
            Assert.Equal("partial", (string)r["coverage"]["coverage"]);
        }

        // ---------------------------------------------------------- the takeoff

        [Fact]
        public void The_takeoff_totals_system_bars_beside_rebar_sets_and_counts_them()
        {
            var rows = new JArray(
                SetRow(10, "A1", "16M", 16, 10, 30.0, 0.006),
                RebarSystemBars.Row(AreaBar(501, 500, 16, 3000, "A1")),
                RebarSystemBars.Row(AreaBar(502, 500, 12, 2000, "A1")));
            string err;
            JObject t = RebarTakeoff.Group(rows, new[] { RebarTakeoffKey.Mark }, 7850, "test", out err);
            Assert.Null(err);
            JObject g = (JObject)((JArray)t["groups"])[0];
            Assert.Equal(3, (int)g["sets"]);
            Assert.Equal(2, (int)g["sets_from_systems"]);
            Assert.Equal(10 + 16 + 12, (int)g["bars"]);
            Assert.Equal(30.0 + 48.0 + 24.0, (double)g["total_length_m"], 4);
            Assert.Equal(2, (int)t["sets_from_systems"]);
            Assert.Equal(2, (int)t["totals"]["sets_from_systems"]);
        }

        // ------------------------------------------------------------- the scope

        private static readonly Dictionary<long, ReinforcementKind> Kinds = new Dictionary<long, ReinforcementKind>
        {
            [10] = ReinforcementKind.RebarSet,
            [11] = ReinforcementKind.RebarSet,
            [500] = ReinforcementKind.AreaSystem,
            [501] = ReinforcementKind.SystemBar,
            [502] = ReinforcementKind.SystemBar,
            [600] = ReinforcementKind.PathSystem,
            [601] = ReinforcementKind.SystemBar,
            [700] = ReinforcementKind.FabricArea,
            [701] = ReinforcementKind.FabricSheet,
            [400] = ReinforcementKind.NotReinforcement
        };

        private static ReinforcementKind KindOf(long id)
        {
            ReinforcementKind k;
            return Kinds.TryGetValue(id, out k) ? k : ReinforcementKind.Missing;
        }

        private static IList<long> MembersOf(long id)
        {
            if (id == 500) return new List<long> { 501, 502 };
            if (id == 600) return new List<long> { 601 };
            if (id == 700) return new List<long> { 701, 702 };
            return null;
        }

        [Fact]
        public void An_area_system_id_scopes_to_its_bars_once_even_when_a_bar_is_named_too()
        {
            RebarScope s = RebarScope.FromIds(new long[] { 500, 501, 10 }, KindOf, MembersOf);
            Assert.Equal(new long[] { 10 }, s.SetIds);
            Assert.Equal(new long[] { 501, 502 }, s.SystemBarIds);
            Assert.Equal(new long[] { 500 }, s.SystemIdsExpanded);
            Assert.Empty(s.Reasons);
        }

        [Fact]
        public void A_path_system_id_scopes_to_its_bars()
        {
            RebarScope s = RebarScope.FromIds(new long[] { 600 }, KindOf, MembersOf);
            Assert.Empty(s.SetIds);
            Assert.Equal(new long[] { 601 }, s.SystemBarIds);
        }

        [Fact]
        public void A_fabric_area_scopes_to_its_sheets_once_and_fabric_that_reads_is_no_reason()
        {
            // Stage 1c: fabric is READ (one row per sheet), so a fabric area named by id is its sheets - read once
            // even when a sheet is named too - and fabric in scope is no longer a reason by itself.
            RebarScope s = RebarScope.FromIds(new long[] { 10, 700, 701 }, KindOf, MembersOf);
            Assert.Equal(new long[] { 701, 702 }, s.FabricIds);
            Assert.Empty(s.Reasons);
            Assert.Equal("complete", (string)s.Coverage(new JArray(SetRow(10, "A", "16M", 16, 1, 1, 0.0001)))["coverage"]);
            Assert.Equal(new long[] { 700 }, ((JArray)s.Describe(3, -1)["fabric_area_ids_expanded"]).Select(t => (long)t).ToArray());
            // A fabric area that would not list its sheets is a reason, by name.
            RebarScope bad = RebarScope.FromIds(new long[] { 700 }, KindOf, id => null);
            Assert.Contains(bad.Reasons.OfType<JObject>(), r => (string)r["what"] == "fabric" && (long)r["element_id"] == 700);
        }

        [Fact]
        public void An_id_that_is_no_reinforcement_or_not_in_the_document_is_named_not_dropped()
        {
            RebarScope s = RebarScope.FromIds(new long[] { 400, 999 }, KindOf, MembersOf);
            Assert.Equal(new long[] { 400, 999 }, s.IgnoredIds);
            Assert.Equal(2, s.Reasons.Count);
            Assert.Equal("partial", (string)s.Coverage(new JArray())["coverage"]);
        }

        [Fact]
        public void A_system_whose_bars_would_not_list_is_a_reason()
        {
            RebarScope s = RebarScope.FromIds(new long[] { 500 }, KindOf, id => null);
            Assert.Empty(s.SystemBarIds);
            Assert.Contains(s.Reasons.OfType<JObject>(), r => (string)r["what"] == "reinforcement_system" && (long)r["element_id"] == 500);
        }

        [Fact]
        public void The_host_filter_keeps_its_bars_and_its_fabric_and_names_a_bar_whose_host_would_not_read()
        {
            RebarScope s = RebarScope.FromIds(new long[] { 10, 11, 501, 502, 701 }, KindOf, MembersOf);
            var hosts = new Dictionary<long, long?> { [10] = 400, [11] = 401, [501] = 400, [502] = null, [701] = 401 };
            s.FilterByHost(400, id => hosts[id]);
            Assert.Equal(new long[] { 10 }, s.SetIds);
            Assert.Equal(new long[] { 501 }, s.SystemBarIds);
            Assert.Empty(s.FabricIds);   // on another host: not this takeoff's business
            Assert.Contains(s.Reasons.OfType<JObject>(), r => (string)r["what"] == "host" && (long)r["element_id"] == 502);
            Assert.DoesNotContain(s.Reasons.OfType<JObject>(), r => (string)r["what"] == "fabric");
            // A sheet whose host would not read is a host reason, like a bar's - not kept, not dropped silently.
            RebarScope f = RebarScope.FromIds(new long[] { 701 }, KindOf, MembersOf);
            f.FilterByHost(400, id => null);
            Assert.Empty(f.FabricIds);
            Assert.Contains(f.Reasons.OfType<JObject>(), r => (string)r["what"] == "host" && (long)r["element_id"] == 701);
        }

        [Fact]
        public void Coverage_is_complete_only_when_every_row_is_and_nothing_was_set_aside()
        {
            RebarScope s = RebarScope.FromIds(new long[] { 10, 500 }, KindOf, MembersOf);
            var rows = new JArray(SetRow(10, "A", "16M", 16, 1, 1, 0.0001),
                                  RebarSystemBars.Row(AreaBar(501)), RebarSystemBars.Row(AreaBar(502)));
            JObject c = s.Coverage(rows);
            Assert.Equal("complete", (string)c["coverage"]);
            Assert.Equal(3, (int)c["measured"]);

            SystemBarFacts bad = AreaBar(502); bad.VolumeM3 = null;
            rows[2] = RebarSystemBars.Row(bad);
            c = s.Coverage(rows);
            Assert.Equal("partial", (string)c["coverage"]);
            Assert.Contains(((JArray)c["reasons"]).OfType<JObject>(), r => (long)r["element_id"] == 502);
        }

        [Fact]
        public void The_scope_block_counts_sets_system_bars_systems_and_fabric()
        {
            RebarScope s = RebarScope.FromIds(new long[] { 10, 600, 701 }, KindOf, MembersOf);
            JObject b = s.Describe(3, -1);
            Assert.Equal(1, (int)b["bar_sets_in_scope"]);
            Assert.Equal(1, (int)b["system_bars_in_scope"]);
            Assert.Equal(new long[] { 600 }, ((JArray)b["system_ids_expanded"]).Select(t => (long)t).ToArray());
            Assert.Equal(1, (int)b["fabric_in_scope"]);
            Assert.Equal(3, (int)b["element_ids_given"]);
            Assert.Equal(JTokenType.Null, b["host_id"].Type);
        }

        // ------------------------------------------------- the bending schedule

        [Fact]
        public void The_schedule_reads_system_bars_and_names_the_systems_on_their_line()
        {
            var rows = new JArray(RebarSystemBars.Row(AreaBar(501, 500, 16, 3000, "A1")),
                                  RebarSystemBars.Row(AreaBar(502, 500, 16, 3000, "A1")));
            string code, error;
            JObject r = RebarBendingSchedule.Build(rows, 7850, "test", 0, out code, out error);
            Assert.Null(code);
            JObject line = (JObject)((JArray)r["rows"])[0];
            Assert.Equal(32, (int)line["bars"]);
            Assert.Equal(new long[] { 500 }, ((JArray)line["systems"]).Select(t => (long)t["id"]).ToArray());
            Assert.Equal("area", (string)((JArray)line["systems"])[0]["kind"]);
            var gaps = ((JArray)r["not_included"]).Select(t => (string)t["what"]).ToList();
            Assert.DoesNotContain("area_reinforcement", gaps);
            Assert.DoesNotContain("path_reinforcement", gaps);
            Assert.DoesNotContain("fabric", gaps);
        }
    }
}

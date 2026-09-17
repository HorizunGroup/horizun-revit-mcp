// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE INVENTORY, AND THE PLACEMENTS IT IS BUILT FROM.
//
// MEASURED: a plan listed the fifty busiest unclaimed names and nineteen
// instances were in none of them. These pin the full inventory - one row per
// placement, a key no two placements share, a stable order, pages that add up -
// and two placement defects found while writing it: a negative Y scale read as a
// reflection with the raw angle, and two negative scales read as no turn at all.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadBlockInventoryTests
    {
        private static CadIrEntity Insert(string name, string handle, double x, double y, double rotDeg = 0,
                                          double sx = 1, double sy = 1, string layer = "E-P", params string[] path)
        {
            var e = new CadIrEntity
            {
                Id = "e" + handle, Handle = handle, Kind = CadEntityKind.BlockInstance, Layer = layer,
                BlockName = name, RotationRadians = rotDeg * Math.PI / 180.0, ScaleX = sx, ScaleY = sy,
                Space = path.Length == 0 ? "model" : null
            };
            e.Points.Add(new CadPoint(x, y, 0));
            foreach (string p in path) e.BlockPath.Add(p);
            return e;
        }

        [Fact]
        public void A_block_placed_twice_inside_one_parent_gives_two_distinct_keys()
        {
            var reading = CadBlockPlacement.Place(new List<CadIrEntity>
            {
                Insert("UNIT", "R1", 0, 0),
                Insert("B", "H1", 100, 0, 0, 1, 1, "E-P", "UNIT"),
                Insert("B", "H2", 500, 0, 0, 1, 1, "E-P", "UNIT"),
                Insert("OUT2", "HC", 10, 0, 0, 1, 1, "E-P", "B")
            });
            List<CadPlacedBlock> outlets = reading.Placed.Where(p => p.BlockName == "OUT2").ToList();
            Assert.Equal(2, outlets.Count);
            Assert.Equal(new[] { "R1/H1/HC", "R1/H2/HC" }, outlets.Select(p => p.Key).OrderBy(k => k).ToArray());
            // The names alone are the same for both - the reason the key is the handles.
            Assert.Equal(outlets[0].Path, outlets[1].Path);
            Assert.Equal(reading.Placed.Count, reading.Placed.Select(p => p.Key).Distinct().Count());
        }

        [Fact]
        public void A_negative_y_scale_is_a_reflection_plus_half_a_turn()
        {
            var reading = CadBlockPlacement.Place(new List<CadIrEntity> { Insert("OUT2", "A", 0, 0, 0, 1, -1) });
            CadPlacedBlock p = reading.Placed.Single();
            Assert.True(p.Mirrored);
            Assert.Equal(Math.PI, p.RotationRadians, 9);
            // The device facing the block's +x: raw transform diag(1,-1) keeps +x.
            CadIrEntity e = p.AsEntity();
            CadVector f = CadDeviceSide.InPlan(new CadVector(1, 0), e.RotationRadians, e.ScaleX, e.ScaleY).Value;
            Assert.Equal(1, f.X, 9);
            Assert.Equal(0, f.Y, 9);
        }

        [Fact]
        public void Two_negative_scales_are_half_a_turn_and_no_reflection()
        {
            var reading = CadBlockPlacement.Place(new List<CadIrEntity> { Insert("OUT2", "A", 0, 0, 0, -1, -1) });
            CadPlacedBlock p = reading.Placed.Single();
            Assert.False(p.Mirrored);
            Assert.Equal(Math.PI, p.RotationRadians, 9);
            CadIrEntity e = p.AsEntity();
            CadVector f = CadDeviceSide.InPlan(new CadVector(1, 0), e.RotationRadians, e.ScaleX, e.ScaleY).Value;
            Assert.Equal(-1, f.X, 9);
        }

        [Fact]
        public void A_child_of_a_y_mirrored_parent_lands_where_the_parent_puts_it()
        {
            var reading = CadBlockPlacement.Place(new List<CadIrEntity>
            {
                Insert("UNIT", "R", 1000, 2000, 30, 1, -1),
                Insert("OUT2", "C", 10, 5, 0, 1, 1, "E-P", "UNIT")
            });
            CadPlacedBlock child = reading.Placed.Single(p => p.BlockName == "OUT2");
            // Raw: R(30)·diag(1,-1)·(10,5) + (1000,2000)
            double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
            Assert.Equal(1000 + 10 * c - (-5) * s, child.At.X, 9);
            Assert.Equal(2000 + 10 * s + (-5) * c, child.At.Y, 9);
            Assert.True(child.Mirrored);
            // Its facing (+x in its own frame) is the parent's reflected +x, turned 30 degrees.
            CadIrEntity e = child.AsEntity();
            CadVector f = CadDeviceSide.InPlan(new CadVector(1, 0), e.RotationRadians, e.ScaleX, e.ScaleY).Value;
            Assert.Equal(c, f.X, 9);
            Assert.Equal(s, f.Y, 9);
        }

        private static CadInventoryRow Row(string key, string outcome, string name = "OUT2") =>
            new CadInventoryRow { Key = key, BlockName = name, Layer = "E-P", Space = "model", Outcome = outcome };

        [Fact]
        public void Pages_are_stable_complete_and_add_up()
        {
            var rows = new List<CadInventoryRow>();
            for (int i = 0; i < 23; i++)
                rows.Add(Row("R" + (100 - i).ToString("000"), i % 3 == 0 ? CadInventoryOutcome.Unclaimed
                                                        : i % 3 == 1 ? CadInventoryOutcome.Claimed
                                                        : CadInventoryOutcome.OutsideExtent));
            int considered = rows.Count(r => r.Outcome != CadInventoryOutcome.OutsideExtent);
            int claimed = rows.Count(r => r.Outcome == CadInventoryOutcome.Claimed);

            var seen = new List<string>();
            int? offset = 0;
            string whole = null;
            while (offset.HasValue)
            {
                JObject page = CadBlockInventory.Page(rows, null, offset.Value, 5, considered, claimed);
                Assert.True((bool)page["reconciles"], page["reconciliation"].ToString());
                whole = (string)page["inventory_fingerprint"];
                seen.AddRange(((JArray)page["rows"]).Select(r => (string)r["key"]));
                offset = page["next_offset"].Type == JTokenType.Null ? (int?)null : (int)page["next_offset"];
            }
            Assert.Equal(23, seen.Count);
            Assert.Equal(seen.OrderBy(k => k, StringComparer.Ordinal), seen);
            // Shuffled input, same inventory.
            var shuffled = rows.OrderBy(r => r.Key.GetHashCode()).ToList();
            Assert.Equal(whole, (string)CadBlockInventory.Page(shuffled, null, 0, 1, considered, claimed)["inventory_fingerprint"]);
        }

        [Fact]
        public void A_total_that_does_not_add_up_is_said()
        {
            var rows = new List<CadInventoryRow> { Row("A", CadInventoryOutcome.Claimed), Row("B", CadInventoryOutcome.Unclaimed) };
            JObject bad = CadBlockInventory.Page(rows, null, 0, 10, 3, 1);
            Assert.False((bool)bad["reconciles"]);
            Assert.False((bool)bad["reconciliation"]["in_zone_matches_the_reading"]);
            JObject dup = CadBlockInventory.Page(new List<CadInventoryRow> { Row("A", "claimed"), Row("A", "unclaimed") },
                                                 null, 0, 10, null, null);
            Assert.False((bool)dup["reconciliation"]["keys_are_unique"]);
        }

        [Fact]
        public void A_filter_narrows_the_page_and_never_the_totals()
        {
            var rows = new List<CadInventoryRow>
            {
                Row("A", CadInventoryOutcome.Claimed), Row("B", CadInventoryOutcome.Unclaimed, "TV-1"),
                Row("C", CadInventoryOutcome.Unclaimed, "DISC"), Row("D", CadInventoryOutcome.Unclaimed, "DISC")
            };
            JObject page = CadBlockInventory.Page(rows, rows.Where(r => r.BlockName == "DISC").ToList(), 0, 10, 4, 1);
            Assert.Equal(4, (int)page["total_rows"]);
            Assert.Equal(2, (int)page["rows_matching_filter"]);
            Assert.Equal(3, (int)page["by_outcome"]["unclaimed"]);
            JObject first = (JObject)((JArray)page["unclaimed_names"]).First;
            Assert.Equal("DISC", (string)first["block_name"]);
            Assert.Equal(2, (int)first["count"]);
        }

        [Fact]
        public void The_external_reference_is_read_from_the_prefix()
        {
            Assert.Equal("X-FEI-L9-15", CadInventoryRow.XrefOf("X-FEI-L9-15|E-PNL", "PNL"));
            Assert.Equal("A-109", CadInventoryRow.XrefOf("0", "A-109|FUR_BED"));
            Assert.Null(CadInventoryRow.XrefOf("E-P", "OUT2"));
        }

        [Fact]
        public void Every_instance_a_reading_considers_has_an_outcome()
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'e', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1, 'gap_mm': 25, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [
                { 'id': 'a', 'precedence': 10, 'layers': ['E-P'], 'produces': 'electrical_fixture',
                  'family_type': 'F: T', 'level': 'L', 'geometry': { 'from': 'blocks', 'blocks': ['OUT2'] } },
                { 'id': 'b', 'precedence': 10, 'layers': ['E-P'], 'produces': 'electrical_fixture',
                  'family_type': 'F: U', 'level': 'L', 'geometry': { 'from': 'blocks', 'blocks': ['J-B'] } },
                { 'id': 'c', 'precedence': 10, 'layers': ['E-P'], 'produces': 'electrical_fixture',
                  'family_type': 'F: V', 'level': 'L', 'geometry': { 'from': 'blocks', 'blocks': ['J-B'] } } ]
            }".Replace('\'', '"');
            CadRequirementSet set = CadRequirementSet.Load(JObject.Parse(doc));
            var a = Insert("OUT2", "1", 0, 0);
            var b = Insert("TV-1", "2", 100, 0);
            var c = Insert("J-B", "3", 200, 0);
            CadBlockReading r = CadBlockRules.Interpret(new List<CadIrEntity> { a, b, c }, set, "sha");
            Assert.Equal(CadInventoryOutcome.Claimed, r.Outcomes[a].Outcome);
            Assert.Equal("a", r.Outcomes[a].RuleId);
            Assert.Same(r.Candidates.Single(), r.Outcomes[a].Candidate);
            Assert.Equal(CadInventoryOutcome.Unclaimed, r.Outcomes[b].Outcome);
            Assert.Equal(CadInventoryOutcome.Tie, r.Outcomes[c].Outcome);
            Assert.Equal(new[] { "b", "c" }, r.Outcomes[c].TieRules);
            Assert.Equal(r.InstancesConsidered, r.Outcomes.Count);
        }

        [Fact]
        public void The_query_serves_the_whole_inventory_and_the_plan_points_to_it()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            Func<string, string> read = rel => System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, rel));
            string query = read(@"src\Horizun.Revit\Commands\QueryCadCommand.cs");
            string source = read(@"src\Horizun.Revit\Commands\CadBlockSource.cs");
            string contract = read(@"src\Horizun.Contracts\Contract.cs");
            Assert.Contains("if (mode == \"blocks\") return Blocks(doc, element, facts, harvest, request, visibility);", query);
            Assert.Contains("CadBlockInventory.Page(rows, filtered, offset, limit,", query);
            Assert.Contains("int readTimeoutSeconds, List<CadInventoryRow> inventory = null)", source);
            Assert.Contains("report[\"inventory\"] = ", source);
            Assert.Contains("\"\"profile\"\", \"\"blocks\"\"], \"\"default\"\": \"\"instances\"\"", contract);
        }

        [Fact]
        public void The_model_query_can_return_placement_as_revit_stores_it()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            Func<string, string> read = rel => System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, rel));
            string query = read(@"src\Horizun.Revit\Commands\QueryModelCommand.cs");
            string contract = read(@"src\Horizun.Contracts\Contract.cs");
            Assert.Contains("request.Value<bool?>(\"include_orientation\") == true", query);
            Assert.Contains("o[\"facing\"] = V(fi.FacingOrientation);", query);
            Assert.Contains("[\"basis_z\"] = V(t.BasisZ)", query);
            Assert.Contains("o[\"host_face\"] = face.ConvertToStableRepresentation(element.Document);", query);
            Assert.Contains("o[\"exterior_normal\"] = V(wall.Orientation);", query);
            Assert.Contains("\"\"include_orientation\"\": {", contract);
        }
    }
}

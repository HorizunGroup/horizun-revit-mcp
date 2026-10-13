// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE CARTILLA, FROM THE MODEL. One line per mark: what bar, what shape, the
// lengths of its legs, how many, how long each, how long in all, how heavy. The
// weight needs a density the caller declares with its source - there is no
// default steel. A mark that covers different bars is split and named, never
// averaged into one line. A value the model would not report is never a zero.
//
// The rows are the shape RebarFacts.Describe emits.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarBendingScheduleTests
    {
        private const double Density = 7850.0;
        private const string Source = "project specification 03 20 00";

        private static JObject Bar(long id, string mark, long host = 5001, long typeId = 300, string typeName = "12M",
                                   double dia = 12.0, long shapeId = 400, string shapeName = "M_11",
                                   int? quantity = 5, double? eachMm = 3000.0, double? totalMm = 15000.0,
                                   double? volume = 0.0017, JArray declared = null)
        {
            var o = new JObject
            {
                ["id"] = id,
                ["host"] = new JObject { ["id"] = host, ["resolved"] = true, ["category"] = "Structural Framing" },
                ["bar_type"] = new JObject
                {
                    ["resolved"] = true, ["id"] = typeId, ["name"] = typeName,
                    ["nominal_diameter_mm"] = dia, ["model_diameter_mm"] = dia + 1.5
                },
                ["shape"] = new JObject { ["id"] = shapeId, ["name"] = shapeName, ["resolved"] = true },
                ["terminations"] = new JArray(
                    new JObject { ["end"] = 0, ["hook_type_name"] = null, ["has_hook"] = false },
                    new JObject { ["end"] = 1, ["hook_type_name"] = "Standard - 90 deg.", ["has_hook"] = true }),
                ["measured"] = new JObject
                {
                    ["total_length_mm"] = totalMm,
                    ["total_length_m"] = totalMm.HasValue ? (JToken)(totalMm.Value / 1000.0) : JValue.CreateNull(),
                    ["volume_m3"] = volume,
                    ["quantity"] = quantity,
                    ["schedule_mark"] = mark,
                    ["bar_centreline_length_mm"] = eachMm
                },
                ["geometry"] = new JObject
                {
                    ["centreline"] = "read",
                    // An L: 1000 up (tessellated in two), then 2000 along.
                    ["centreline_points_as_declared_mm"] = declared ?? new JArray(
                        new JArray(0.0, 0.0, 0.0), new JArray(0.0, 0.0, 500.0), new JArray(0.0, 0.0, 1000.0),
                        new JArray(2000.0, 0.0, 1000.0))
                }
            };
            return o;
        }

        private static JObject Build(JArray rows, double? density = Density, string source = Source, double tol = 0)
        {
            string code, error;
            JObject r = RebarBendingSchedule.Build(rows, density, source, tol, out code, out error);
            Assert.True(r != null, code + ": " + error);
            return r;
        }

        private static JObject Row(JObject r, int i)
        {
            return (JObject)((JArray)r["rows"])[i];
        }

        // ---------------------------------------------------------- density

        [Fact]
        public void Without_a_density_it_REFUSES_there_is_no_default_steel()
        {
            string code, error;
            Assert.Null(RebarBendingSchedule.Build(new JArray(Bar(1, "A1")), null, null, 0, out code, out error));
            Assert.Equal(RebarBendingSchedule.CodeDensityRequired, code);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void A_density_without_its_source_is_refused()
        {
            string code, error;
            Assert.Null(RebarBendingSchedule.Build(new JArray(Bar(1, "A1")), Density, " ", 0, out code, out error));
            Assert.Equal(RebarTakeoff.CodeDensityWithoutSource, code);
        }

        [Fact]
        public void A_density_that_is_not_positive_is_refused()
        {
            string code, error;
            Assert.Null(RebarBendingSchedule.Build(new JArray(Bar(1, "A1")), 0, Source, 0, out code, out error));
            Assert.Equal(RebarTakeoff.CodeDensityNotUsable, code);
        }

        [Fact]
        public void A_negative_length_tolerance_is_refused()
        {
            string code, error;
            Assert.Null(RebarBendingSchedule.Build(new JArray(Bar(1, "A1")), Density, Source, -1, out code, out error));
            Assert.Equal(RebarBendingSchedule.CodeToleranceNotUsable, code);
        }

        // ------------------------------------------------------------ a line

        [Fact]
        public void Two_identical_sets_with_one_mark_are_ONE_line_with_their_bars_added()
        {
            JObject r = Build(new JArray(Bar(1, "A1", host: 5001), Bar(2, "A1", host: 5002)));
            Assert.Equal(1, (int)r["row_count"]);
            JObject a = Row(r, 0);
            Assert.Equal("A1", (string)a["mark"]);
            Assert.Equal("12M", (string)a["bar_type"]["name"]);
            Assert.Equal(12.0, (double)a["nominal_diameter_mm"], 3);
            Assert.Equal("M_11", (string)a["shape"]["name"]);
            Assert.Equal(10, (int)a["bars"]);
            Assert.Equal(3000.0, (double)a["one_bar_length_mm"], 3);
            Assert.Equal("bar_centreline_length_mm", (string)a["one_bar_length_source"]);
            Assert.Equal(30.0, (double)a["total_length_m"], 6);
            Assert.Equal(0.0034 * Density, (double)a["total_kg"], 3);
            Assert.Equal(2, (int)a["sets"]);
            Assert.Equal(new[] { 1L, 2L }, ((JArray)a["rebar_ids"]).Select(t => (long)t));
            Assert.Equal(new[] { 5001L, 5002L }, ((JArray)a["host_ids"]).Select(t => (long)t));
            Assert.False((bool)a["mark_conflict"]);
            Assert.True((bool)a["complete"]);
        }

        [Fact]
        public void The_segment_dimensions_are_read_off_the_bar_with_collinear_points_merged()
        {
            JObject a = Row(Build(new JArray(Bar(1, "A1"))), 0);
            Assert.Equal(new[] { 1000.0, 2000.0 }, ((JArray)a["segments_mm"]).Select(t => (double)t));
            Assert.Equal("geometry.centreline_points_as_declared_mm", (string)a["segments_source"]);
            Assert.True((bool)a["segments_consistent"]);
        }

        [Fact]
        public void Hooks_are_named_per_end()
        {
            JObject a = Row(Build(new JArray(Bar(1, "A1"))), 0);
            Assert.Equal(JTokenType.Null, a["hooks"]["start"].Type);
            Assert.Equal("Standard - 90 deg.", (string)a["hooks"]["end"]);
        }

        [Fact]
        public void A_bar_whose_declared_centreline_is_missing_has_no_segments_and_says_so()
        {
            JObject b = Bar(1, "A1");
            ((JObject)b["geometry"]).Remove("centreline_points_as_declared_mm");
            JObject a = Row(Build(new JArray(b)), 0);
            Assert.Equal(JTokenType.Null, a["segments_mm"].Type);
            Assert.False(string.IsNullOrWhiteSpace((string)a["segments_why"]));
        }

        [Fact]
        public void Without_a_centreline_length_one_bar_is_the_total_over_the_quantity()
        {
            JObject a = Row(Build(new JArray(Bar(1, "A1", eachMm: null, totalMm: 15500.0, quantity: 5))), 0);
            Assert.Equal(3100.0, (double)a["one_bar_length_mm"], 3);
            Assert.Equal("total_length_mm/quantity", (string)a["one_bar_length_source"]);
        }

        // --------------------------------------------------- one mark, two bars

        [Fact]
        public void One_mark_on_DIFFERENT_bars_is_split_and_named_never_averaged()
        {
            JObject r = Build(new JArray(
                Bar(1, "A1"),
                Bar(2, "A1", typeId: 301, typeName: "16M", dia: 16.0)));
            Assert.Equal(2, (int)r["row_count"]);
            Assert.True((bool)Row(r, 0)["mark_conflict"]);
            Assert.True((bool)Row(r, 1)["mark_conflict"]);
            JObject c = ((JArray)r["mark_conflicts"]).OfType<JObject>().Single();
            Assert.Equal("A1", (string)c["mark"]);
            Assert.Equal(2, (int)c["rows"]);
            Assert.Contains("bar_type", ((JArray)c["differences"]).Select(t => (string)t["property"]));
            Assert.False((bool)r["complete"]);
        }

        [Fact]
        public void A_length_inside_the_declared_tolerance_stays_on_one_line()
        {
            JObject r = Build(new JArray(Bar(1, "A1", eachMm: 3000.0), Bar(2, "A1", eachMm: 3001.0)), tol: 2.0);
            Assert.Equal(1, (int)r["row_count"]);
            Assert.Equal(2.0, (double)r["length_tolerance_mm"], 3);
        }

        [Fact]
        public void With_no_tolerance_declared_lengths_are_compared_exactly()
        {
            JObject r = Build(new JArray(Bar(1, "A1", eachMm: 3000.0), Bar(2, "A1", eachMm: 3001.0)));
            Assert.Equal(2, (int)r["row_count"]);
        }

        [Fact]
        public void Bars_without_a_mark_are_a_line_of_their_own_and_are_flagged()
        {
            JObject r = Build(new JArray(Bar(1, ""), Bar(2, "A1")));
            JObject blank = ((JArray)r["rows"]).OfType<JObject>().Single(o => (bool)o["mark_missing"]);
            Assert.Equal(JTokenType.Null, blank["mark"].Type);
        }

        // ---------------------------------------------------------- totals

        [Fact]
        public void Totals_per_diameter_and_per_host_add_up()
        {
            JObject r = Build(new JArray(
                Bar(1, "A1", host: 5001),
                Bar(2, "A1", host: 5002),
                Bar(3, "B1", host: 5001, typeId: 301, typeName: "16M", dia: 16.0, shapeId: 401, shapeName: "M_00",
                    quantity: 2, eachMm: 4000.0, totalMm: 8000.0, volume: 0.0016)));
            var byDia = ((JArray)r["totals_by_diameter"]).OfType<JObject>().ToList();
            Assert.Equal(2, byDia.Count);
            JObject d12 = byDia.Single(o => (double)o["nominal_diameter_mm"] == 12.0);
            Assert.Equal(10, (int)d12["bars"]);
            Assert.Equal(30.0, (double)d12["total_length_m"], 6);
            Assert.Equal(0.0034 * Density, (double)d12["total_kg"], 3);

            var byHost = ((JArray)r["totals_by_host"]).OfType<JObject>().ToList();
            JObject h1 = byHost.Single(o => (long)o["host_id"] == 5001);
            Assert.Equal(2, (int)h1["sets"]);
            Assert.Equal(7, (int)h1["bars"]);
            Assert.Equal(23.0, (double)h1["total_length_m"], 6);
            Assert.Equal((0.0017 + 0.0016) * Density, (double)h1["total_kg"], 3);

            Assert.Equal(12, (int)r["totals"]["bars"]);
            Assert.Equal(38.0, (double)r["totals"]["total_length_m"], 6);
            Assert.True((bool)r["complete"]);
        }

        [Fact]
        public void An_unreadable_volume_is_a_null_weight_never_a_zero()
        {
            JObject r = Build(new JArray(Bar(1, "A1"), Bar(2, "A1", volume: null)));
            JObject a = Row(r, 0);
            Assert.Equal(JTokenType.Null, a["total_kg"].Type);
            Assert.Equal(0.0017 * Density, (double)a["total_kg_counted"], 3);
            Assert.False((bool)a["complete"]);
            Assert.Equal(JTokenType.Null, r["totals"]["total_kg"].Type);
            Assert.False((bool)r["complete"]);
        }

        // ------------------------------------------------- what it says it is

        [Fact]
        public void It_is_marked_derived_from_the_model_and_publishes_what_it_leaves_out()
        {
            JObject r = Build(new JArray(Bar(1, "A1")));
            Assert.True((bool)r["derived_from_model"]);
            var missing = ((JArray)r["not_included"]).Select(t => (string)t["what"]).ToList();
            // Area and path bars are READ (RebarInSystem rows, see RebarSystemBarsTests), and fabric sheets too,
            // by sheet type in their own block (stage 1c, RebarFabricTakeoffTests).
            Assert.DoesNotContain("area_reinforcement", missing);
            Assert.DoesNotContain("path_reinforcement", missing);
            Assert.DoesNotContain("fabric", missing);
            Assert.Equal(Density, (double)r["density"]["kg_per_m3"], 3);
            Assert.Equal(Source, (string)r["density"]["source"]);
        }
    }
}

// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Comité de obra 2026-10-01, horizun_query_model:
//   - include_bounding_box answered null for every grid and level (datums have no
//     model box), so grids could not be compared between links;
//   - there was no filter by source model: 92 rows of one link cost 987 rows;
//   - compact returned Volume in raw cubic feet under a key that reads like m3.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class QueryModelComiteObraTests
    {
        // ---- datums ----

        [Fact]
        public void A_grid_carries_its_line_in_the_callers_units_and_a_direction_that_ignores_drawing_order()
        {
            // 10 ft -> 3048 mm along +Y, drawn top to bottom.
            JObject g = DatumGeometryRules.Grid(new[] { 1.0, 10.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }, false, null, 304.8, "mm");
            Assert.Equal("grid", (string)g["kind"]);
            Assert.Equal(304.8, (double)g["start"][0], 6);
            Assert.Equal(3048.0, (double)g["start"][1], 6);
            Assert.Equal(90.0, (double)g["plan_angle_degrees"], 6);
            Assert.Equal(3048.0, (double)g["length"], 6);
            JObject reversed = DatumGeometryRules.Grid(new[] { 1.0, 0.0, 0.0 }, new[] { 1.0, 10.0, 0.0 }, false, null, 304.8, "mm");
            Assert.Equal((double)g["plan_angle_degrees"], (double)reversed["plan_angle_degrees"], 6);
        }

        [Theory]
        [InlineData(-0.0)]
        [InlineData(-1e-12)]
        public void A_grid_along_X_reads_zero_degrees_never_negative_zero(double dy)
        {
            // Measured live on CO-Mirador-redes: grid A printed plan_angle_degrees -0.0.
            JObject g = DatumGeometryRules.Grid(new[] { 0.0, 5.0, 0.0 }, new[] { 10.0, 5.0 + dy, 0.0 }, false, null, 1, "feet");
            double angle = (double)g["plan_angle_degrees"];
            Assert.Equal(0.0, angle);
            Assert.False(double.IsNegative(angle));
            Assert.DoesNotContain("-0", g["plan_angle_degrees"].ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void A_curved_grid_lists_its_points_and_claims_no_straight_direction()
        {
            var pts = new List<double[]> { new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 0 }, new[] { 2.0, 0, 0 } };
            JObject g = DatumGeometryRules.Grid(pts[0], pts[2], true, pts, 1, "feet");
            Assert.True((bool)g["is_curved"]);
            Assert.Null(g["plan_angle_degrees"]);
            Assert.Equal(3, ((JArray)g["points"]).Count);
        }

        [Fact]
        public void A_grid_box_is_the_box_of_its_curve_and_a_level_gets_an_elevation_never_a_box()
        {
            double[][] box = DatumGeometryRules.BoxOf(new[] { new[] { 5.0, 2, 1 }, new[] { -1.0, 7, 1 } });
            Assert.Equal(new[] { -1.0, 2, 1 }, box[0]);
            Assert.Equal(new[] { 5.0, 7, 1 }, box[1]);
            Assert.Null(DatumGeometryRules.BoxOf(new double[0][]));

            JObject l = DatumGeometryRules.Level(10.0, 9.0, 0.3048, "m");
            Assert.Equal(3.048, (double)l["elevation"], 6);
            Assert.Equal(2.743, (double)l["elevation_in_own_model"], 3);
            Assert.Null(l["bounding_box"]);
        }

        // ---- source filter ----

        private static readonly List<QuerySourceFilter.Source> Available = new List<QuerySourceFilter.Source>
        {
            new QuerySourceFilter.Source { Kind = "host", Title = "CO-Mirador-arquitectura" },
            new QuerySourceFilter.Source { Kind = "link", Title = "LNK-Mirador-estructura", LinkInstanceId = 501 },
            new QuerySourceFilter.Source { Kind = "link", Title = "LNK-Mirador-redes", LinkInstanceId = 502 },
            new QuerySourceFilter.Source { Kind = "link", Title = "LNK-Mirador-redes", LinkInstanceId = 503 }
        };

        private static QuerySourceFilter Filter(string json)
        {
            QuerySourceFilter f = QuerySourceFilter.Parse(JObject.Parse(json), out string error);
            Assert.Null(error);
            return f;
        }

        [Fact]
        public void No_filter_reads_everything_as_before()
        {
            QuerySourceFilter f = Filter("{}");
            Assert.False(f.Active);
            Assert.All(Available, s => Assert.True(f.Admits(s)));
        }

        [Fact]
        public void A_title_reads_that_link_alone_and_never_the_host()
        {
            QuerySourceFilter f = Filter("{\"source_models\":[\"lnk-mirador-ESTRUCTURA\"]}");
            Assert.Null(f.Unmatched(Available));
            Assert.False(f.Admits(Available[0]));
            Assert.True(f.Admits(Available[1]));
            Assert.False(f.Admits(Available[2]));
            Assert.Single((JArray)f.ToJson(Available)["sources_read"]);
            Assert.Equal(3, (int)f.ToJson(Available)["sources_skipped"]);
        }

        [Fact]
        public void An_instance_id_picks_one_of_two_placements_and_host_joins_by_keyword()
        {
            QuerySourceFilter f = Filter("{\"link_instance_ids\":[503],\"source_models\":[\"host\"]}");
            Assert.True(f.Admits(Available[0]));
            Assert.False(f.Admits(Available[2]));
            Assert.True(f.Admits(Available[3]));
        }

        [Fact]
        public void A_name_that_matches_nothing_refuses_and_lists_what_exists()
        {
            QuerySourceFilter f = Filter("{\"source_models\":[\"LNK-Mirador-estuctura\"],\"link_instance_ids\":[999]}");
            string refusal = f.Unmatched(Available);
            Assert.Contains("'LNK-Mirador-estuctura'", refusal);
            Assert.Contains("link_instance_ids 999", refusal);
            Assert.Contains("LNK-Mirador-redes' (link_instance_id 502)", refusal);
            Assert.Contains("Nothing was read", refusal);
        }

        [Theory]
        [InlineData("{\"source_models\":\"host\"}")]
        [InlineData("{\"source_models\":[\"\"]}")]
        [InlineData("{\"link_instance_ids\":[\"501\"]}")]
        public void Malformed_filters_refuse(string json)
        {
            Assert.Null(QuerySourceFilter.Parse(JObject.Parse(json), out string error));
            Assert.NotNull(error);
        }

        // ---- compact units ----

        private static DisplayUnitFact CubicMeters => new DisplayUnitFact
        { UnitTypeId = "autodesk.unit.unit:cubicMeters-1.0.1", Label = "Cubic meters", Factor = 0.028316846592, Offset = 0 };

        [Fact]
        public void The_measured_trap_cubic_feet_come_back_in_the_projects_cubic_meters_and_say_so()
        {
            var tally = new CompactUnitTally();
            JToken v = tally.Value("Volume", new JValue(15.53), "autodesk.spec.aec:volume-2.0.0", SumUnitRules.Volume, CubicMeters);
            Assert.Equal(0.43976, (double)v, 4);
            JObject units = tally.ToJson();
            Assert.Equal("Cubic meters", (string)units["Volume"]["unit"]);
            Assert.True((bool)units["Volume"]["converted"]);
            Assert.Equal(1, (int)units["Volume"]["values"]);
        }

        [Fact]
        public void Text_ids_and_unitless_numbers_pass_through_and_offset_units_stay_raw_by_name()
        {
            var tally = new CompactUnitTally();
            Assert.Equal("W-01", (string)tally.Value("Mark", new JValue("W-01"), "", SumUnitRules.Unknown, null));
            Assert.Equal(42L, (long)tally.Value("Type Id", new JValue(42L), "", SumUnitRules.Identifier, null));
            Assert.Equal(3L, (long)tally.Value("Count", new JValue(3L), "autodesk.spec:spec.int64-2.0.0", SumUnitRules.Unitless, null));
            var celsius = new DisplayUnitFact { Label = "Celsius", Factor = 0.5556, Offset = -273.15 };
            Assert.Equal(300.0, (double)tally.Value("Temp", new JValue(300.0), "autodesk.spec:temp", SumUnitRules.Measurable, celsius));
            JObject u = tally.ToJson();
            Assert.Null(u["Mark"]);
            Assert.Null(u["Type Id"]);
            Assert.Equal("unitless", (string)u["Count"]["unit"]);
            Assert.False((bool)u["Temp"]["converted"]);
            Assert.Contains("offset", (string)u["Temp"]["unit"]);
        }

        [Fact]
        public void One_key_under_two_specs_is_reported_mixed()
        {
            var tally = new CompactUnitTally();
            tally.Value("Size", new JValue(1.0), "len", SumUnitRules.Length, new DisplayUnitFact { Label = "Millimeters", Factor = 304.8 });
            tally.Value("Size", new JValue(1.0), "area", SumUnitRules.Area, new DisplayUnitFact { Label = "Square meters", Factor = 0.092903 });
            JObject u = tally.ToJson();
            Assert.True((bool)u["Size"]["mixed"]);
            Assert.Equal(2, ((JArray)u["Size"]["units"]).Count);
        }

        // ---- the Revit half wires them ----

        private static string Source()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "QueryModelCommand.cs"));
        }

        [Fact]
        public void The_query_skips_unnamed_sources_before_collecting_and_names_datums_and_units()
        {
            string s = Source();
            int unmatched = s.IndexOf("sourceFilter.Unmatched(availableSources)", StringComparison.Ordinal);
            int hostCollect = s.IndexOf("if (sourceFilter.Admits(availableSources[0]))", StringComparison.Ordinal);
            int linkSkip = s.IndexOf("if (!sourceFilter.Admits(new QuerySourceFilter.Source { Kind = \"link\"", StringComparison.Ordinal);
            int linkCollect = s.IndexOf("Collect(linked, \"link\"", StringComparison.Ordinal);
            Assert.True(unmatched > 0 && hostCollect > unmatched && linkSkip > hostCollect && linkCollect > linkSkip,
                "an unmatched name refuses before any read, and unnamed documents are never collected");
            Assert.Contains("if (b == null && element is Grid grid) return GridBox(grid, transform);", s);
            Assert.Contains("json[\"datum\"] = datum;", s);
            Assert.Contains("units.Value(prop.Name, cell[\"raw\"], spec.Key, spec.Quantity", s);
            Assert.Contains("queryResult[\"parameter_units\"] = compactUnits.ToJson();", s);
        }
    }
}

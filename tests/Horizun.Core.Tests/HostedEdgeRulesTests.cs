// horizun_wall_sweep operation=place_edge: slab edges, fascias and gutters, without Revit.
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HostedEdgeRulesTests
    {
        private static JObject Place() => new JObject
        {
            ["target_document"] = "Casa", ["operation"] = "place_edge", ["kind"] = "fascia", ["type_id"] = 90, ["host_id"] = 12,
            ["edge_references"] = new JArray("abc:1:SURFACE", "abc:2:SURFACE")
        };

        [Fact]
        public void A_well_formed_place_edge_passes_with_edges_or_faces_or_both()
        {
            Assert.Null(WallSweepRules.Validate(Place()));
            var r = Place(); r.Remove("edge_references"); r["face_references"] = new JArray("abc:3:SURFACE");
            Assert.Null(WallSweepRules.Validate(r));
            r["edge_references"] = new JArray("abc:1:SURFACE");
            Assert.Null(WallSweepRules.Validate(r));
        }

        [Fact]
        public void The_kind_is_said_not_guessed()
        {
            var r = Place(); r.Remove("kind");
            Assert.Contains("kind", WallSweepRules.Validate(r));
            r["kind"] = "soffit";
            Assert.Contains("slab_edge, fascia, gutter", WallSweepRules.Validate(r));
        }

        [Fact]
        public void A_type_a_host_and_at_least_one_reference_are_required()
        {
            var r = Place(); r.Remove("type_id");
            Assert.Contains("FasciaType", WallSweepRules.Validate(r));
            r = Place(); r.Remove("host_id");
            Assert.Contains("host_id", WallSweepRules.Validate(r));
            r = Place(); r.Remove("edge_references");
            Assert.Contains("name the edges", WallSweepRules.Validate(r));
        }

        [Fact]
        public void References_are_non_empty_strings_without_repeats_within_the_limits()
        {
            var r = Place(); r["edge_references"] = new JArray();
            Assert.Contains("edge_references", WallSweepRules.Validate(r));
            r = Place(); r["edge_references"] = new JArray("a", "a");
            Assert.Contains("twice", WallSweepRules.Validate(r));
            r = Place(); r["edge_references"] = new JArray("a", 5);
            Assert.Contains("edge_references", WallSweepRules.Validate(r));
            r = Place(); r["edge_references"] = new JArray(" ");
            Assert.Contains("edge_references", WallSweepRules.Validate(r));
            var many = new JArray();
            for (int i = 0; i <= HostedEdgeRules.MaxEdgeReferences; i++) many.Add("e" + i);
            r = Place(); r["edge_references"] = many;
            Assert.Contains("edge_references", WallSweepRules.Validate(r));
        }

        [Fact]
        public void A_field_of_a_wall_sweep_place_is_refused_by_name_on_place_edge()
        {
            var r = Place(); r["wall_ids"] = new JArray(1);
            Assert.Contains("wall_ids", WallSweepRules.Validate(r));
            r = Place(); r["side"] = "exterior";
            Assert.Contains("side", WallSweepRules.Validate(r));
        }

        [Fact]
        public void And_the_edge_fields_are_refused_on_the_wall_operations()
        {
            var place = new JObject
            {
                ["operation"] = "place", ["type_id"] = 12, ["wall_ids"] = new JArray(1), ["height"] = 0, ["side"] = "interior",
                ["kind"] = "fascia"
            };
            Assert.Contains("kind", WallSweepRules.Validate(place));
        }

        [Fact]
        public void Read_takes_a_wall_or_a_host_not_both()
        {
            var host = new JObject { ["operation"] = "read", ["host_id"] = 12 };
            Assert.Null(WallSweepRules.Validate(host));
            host["element_id"] = 4;
            Assert.Contains("not both", WallSweepRules.Validate(host));
            Assert.Contains("host_id", WallSweepRules.Validate(new JObject { ["operation"] = "read", ["host_id"] = "roof" }));
            Assert.Null(WallSweepRules.Validate(new JObject { ["operation"] = "read", ["element_id"] = 4 }));
        }

        [Theory]
        [InlineData("slab_edge", "SlabEdge", "SlabEdgeType")]
        [InlineData("fascia", "Fascia", "FasciaType")]
        [InlineData("gutter", "Gutter", "GutterType")]
        public void Each_kind_names_the_class_it_creates_and_the_type_it_needs(string kind, string created, string type)
        {
            Assert.Equal(created, HostedEdgeRules.CreatedClass(kind));
            Assert.Equal(type, HostedEdgeRules.TypeClass(kind));
        }

        [Fact]
        public void Floors_host_slab_edges_and_roofs_host_fascias_and_gutters()
        {
            var floor = new List<string> { "Floor", "CeilingAndFloor", "HostObject", "Element" };
            var roof = new List<string> { "FootPrintRoof", "RoofBase", "HostObject", "Element" };
            var wall = new List<string> { "Wall", "HostObject", "Element" };
            Assert.True(HostedEdgeRules.HostFits("slab_edge", floor));
            Assert.False(HostedEdgeRules.HostFits("slab_edge", roof));
            Assert.True(HostedEdgeRules.HostFits("fascia", roof));
            Assert.True(HostedEdgeRules.HostFits("gutter", roof));
            Assert.False(HostedEdgeRules.HostFits("fascia", floor));
            Assert.False(HostedEdgeRules.HostFits("gutter", wall));
            Assert.False(HostedEdgeRules.HostFits("fascia", null));
        }

        [Fact]
        public void A_sweep_on_a_roof_edge_touches_the_roof_and_one_far_away_does_not()
        {
            double[] roofMin = { 0, 0, 10 }, roofMax = { 30, 20, 16 };
            Assert.True(HostedEdgeRules.BoxesTouch(new double[] { -0.5, -0.5, 9.5 }, new double[] { 30.5, 0.2, 10.5 }, roofMin, roofMax, 0.33));
            Assert.False(HostedEdgeRules.BoxesTouch(new double[] { 100, 0, 10 }, new double[] { 101, 1, 11 }, roofMin, roofMax, 0.33));
            Assert.False(HostedEdgeRules.BoxesTouch(null, new double[] { 1, 1, 1 }, roofMin, roofMax, 0.33));
        }

        [Theory]
        [InlineData(0, 0, 1, "top")]
        [InlineData(0, 0, -1, "bottom")]
        [InlineData(1, 0, 0, "side")]
        [InlineData(0, 0.7071, 0.7071, "sloped_up")]
        [InlineData(0, 0.7071, -0.7071, "sloped_down")]
        [InlineData(0, 0, 0, "unknown")]
        public void A_face_is_named_by_its_normal(double x, double y, double z, string role)
            => Assert.Equal(role, HostedEdgeRules.FaceRole(x, y, z));
    }
}

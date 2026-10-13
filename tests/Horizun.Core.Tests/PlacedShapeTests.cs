// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L7: the circular hoop and the spiral, built from a PLACED rebar shape
// because Rebar.CreateFromCurves fails on every chain with an arc in it (measured on
// Revit 2024). The shape is named by the caller and its definition is READ before
// anything is built; the kind, the pitch and the finishing turns are declared.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlacedShapeTests
    {
        // A cylinder of radius 250 and height 3000 on the origin, 64 sides.
        private static HostMesh Cylinder(double r = 250, int n = 64)
        {
            var m = new HostMesh { AnyCurvedFace = true, ChordToleranceMm = r * (1 - Math.Cos(Math.PI / n)) };
            int c0 = m.AddVertex(0, 0, 0), c1 = m.AddVertex(0, 0, 3000);
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                m.AddVertex(r * Math.Cos(a), r * Math.Sin(a), 0);
                m.AddVertex(r * Math.Cos(a), r * Math.Sin(a), 3000);
            }
            for (int i = 0; i < n; i++)
            {
                int b0 = 2 + 2 * i, t0 = b0 + 1, b1 = 2 + 2 * ((i + 1) % n), t1 = b1 + 1;
                m.AddTriangle(c0, b1, b0); m.AddTriangle(c1, t0, t1);
                m.AddTriangle(b0, b1, t1); m.AddTriangle(b0, t1, t0);
            }
            return m;
        }

        private static SectionHostFrame Round() =>
            SectionFrames.Measure(9, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }, Cylinder());

        private static SectionHostFrame Square() =>
            SectionFrames.Measure(9, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 },
                HostMesh.Box(new double[] { -250, -250, 0 }, new double[] { 250, 250, 3000 }));

        private const string Hoop = "{ 'shape': 'M_T6', 'kind': 'lapped_circle' }";
        private const string Spiral = "{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': 75, 'base_finishing_turns': 1, 'top_finishing_turns': 2 }";

        private static StructuralRequirementSet Set(string placement, string zones = null, string stirrupsExtra = "", string layers = null)
        {
            zones = zones ?? "[ { 'name': 'all', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 150 } } ]";
            layers = layers ?? "[ { 'name': 'ring', 'face': 'ring', 'count': 6, 'bar_type': 'N16', 'extend': 'host_length' } ]";
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'placed', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' }, { 'id': 'N10', 'type_name': '10M' } ],
  'hook_types': [ { 'id': 'H135', 'type_name': 'Stirrup/Tie - 135 deg.' }, { 'id': 'NONE', 'none': true } ],
  'section_rules': [ { 'id': 'CC', 'host': { 'element_ids': [ 9 ] }, 'cover': { 'distance_mm': 40 },
    'layers': " + layers + @",
    'stirrups': { 'bar_type': 'N10', " + stirrupsExtra + @" 'shape_placement': " + placement + @", 'zones': " + zones + @" } } ] }"));
        }

        private static List<StructuralRebarRule> ExpandHoops(StructuralRequirementSet s, out StirrupZoneResult plan)
        {
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.ExpandAll(s.SectionRules.Single(), Round(), id => id == "N10" ? 10 : 16, null, null,
                out List<StructuralRebarRule> _, out List<StructuralStirrupZoneRule> zones);
            Assert.True(r.Ok, r.Why);
            StructuralStirrupZoneRule z = zones.Single();
            plan = StirrupZoneRules.Expand(z, z.SpanMm.Value, 10, null, out List<StructuralRebarRule> expanded);
            return expanded;
        }

        // ------------------------------------------------------------- parsing

        [Fact]
        public void A_lapped_circle_placement_is_read_with_its_shape_and_kind()
        {
            StructuralRequirementSet s = Set(Hoop);
            Assert.True(s.Ok, s.Error);
            StructuralShapePlacement p = s.SectionRules.Single().Stirrups.ShapePlacement;
            Assert.Equal("M_T6", p.Shape);
            Assert.Equal(StructuralShapePlacement.KindLappedCircle, p.Kind);
            Assert.Null(p.PitchMm);
        }

        [Fact]
        public void A_spiral_placement_carries_every_number_it_was_given()
        {
            StructuralRequirementSet s = Set(Spiral, "[ { 'name': 'all', 'layout': { 'rule': 'single' } } ]");
            Assert.True(s.Ok, s.Error);
            StructuralShapePlacement p = s.SectionRules.Single().Stirrups.ShapePlacement;
            Assert.Equal(75, p.PitchMm.Value, 9);
            Assert.Equal(1, p.BaseFinishingTurns.Value);
            Assert.Equal(2, p.TopFinishingTurns.Value);
        }

        [Theory]
        [InlineData("{ 'shape': 'M_SP', 'kind': 'spiral', 'base_finishing_turns': 1, 'top_finishing_turns': 1 }")]
        [InlineData("{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': 75, 'top_finishing_turns': 1 }")]
        [InlineData("{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': 75, 'base_finishing_turns': 1 }")]
        [InlineData("{ 'kind': 'lapped_circle' }")]
        [InlineData("{ 'shape': 'M_T6' }")]
        public void Nothing_a_spiral_or_a_hoop_needs_is_defaulted(string placement)
        {
            StructuralRequirementSet s = Set(placement, "[ { 'name': 'all', 'layout': { 'rule': 'single' } } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.StartsWith("section_rules['CC'].stirrups.shape_placement", s.Path);
        }

        [Theory]
        [InlineData("{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': 0, 'base_finishing_turns': 1, 'top_finishing_turns': 1 }")]
        [InlineData("{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': 75, 'base_finishing_turns': 1.5, 'top_finishing_turns': 1 }")]
        [InlineData("{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': 75, 'base_finishing_turns': -1, 'top_finishing_turns': 1 }")]
        [InlineData("{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': '75', 'base_finishing_turns': 1, 'top_finishing_turns': 1 }")]
        public void A_pitch_that_is_not_a_length_or_turns_that_are_not_whole_are_refused(string placement)
        {
            StructuralRequirementSet s = Set(placement, "[ { 'name': 'all', 'layout': { 'rule': 'single' } } ]");
            Assert.False(s.Ok);
            Assert.StartsWith("section_rules['CC'].stirrups.shape_placement", s.Path);
        }

        [Fact]
        public void A_value_a_hoop_would_not_use_is_refused_not_ignored()
        {
            StructuralRequirementSet s = Set("{ 'shape': 'M_T6', 'kind': 'lapped_circle', 'pitch_mm': 75 }");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("section_rules['CC'].stirrups.shape_placement.pitch_mm", s.Path);
        }

        [Fact]
        public void An_unknown_kind_and_an_unknown_key_are_refused_by_name()
        {
            StructuralRequirementSet kind = Set("{ 'shape': 'M_T6', 'kind': 'circle' }");
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, kind.Code);
            Assert.Contains("lapped_circle", kind.Error);
            StructuralRequirementSet key = Set("{ 'shape': 'M_T6', 'kind': 'lapped_circle', 'diameter_mm': 410 }");
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, key.Code);
        }

        [Fact]
        public void A_placement_beside_a_stirrup_shape_or_a_new_shape_is_two_instructions()
        {
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, Set(Hoop, null, "'shape': 'M_T1',").Code);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, Set(Hoop, null, "'allow_new_shape': true,").Code);
        }

        [Fact]
        public void A_placed_shape_with_a_declared_hook_is_refused_and_an_explicit_none_is_not()
        {
            StructuralRequirementSet hooked = Set(Hoop, null, "'start': { 'hook_type': 'H135' },");
            Assert.False(hooked.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, hooked.Code);
            Assert.Contains("hook", hooked.Error);
            Assert.True(Set(Hoop, null, "'start': { 'hook_type': 'NONE' },").Ok);
        }

        [Fact]
        public void A_spiral_is_one_zone_laid_out_single()
        {
            StructuralRequirementSet two = Set(Spiral,
                "[ { 'name': 'a', 'length_mm': 600, 'layout': { 'rule': 'single' } }, { 'name': 'b', 'layout': { 'rule': 'single' } } ]");
            Assert.False(two.Ok);
            Assert.Contains("one zone", two.Error);
            StructuralRequirementSet spaced = Set(Spiral);
            Assert.False(spaced.Ok);
            Assert.Contains("single", spaced.Error);
        }

        // ----------------------------------------------------------- expansion

        [Fact]
        public void A_placement_on_a_rectangular_section_is_refused_by_name()
        {
            StructuralRequirementSet s = Set(Hoop, null, "", "[ { 'face': 'top', 'count': 2, 'bar_type': 'N16', 'extend': 'host_length' } ]");
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.ExpandAll(s.SectionRules.Single(), Square(), id => 16, null, null, out _, out _);
            Assert.Equal(SectionRules.CodePlacementNeedsCircle, r.Code);
        }

        [Fact]
        public void A_circular_hoop_zone_becomes_one_placed_hoop_per_station()
        {
            List<StructuralRebarRule> hoops = ExpandHoops(Set(Hoop), out StirrupZoneResult plan);
            Assert.True(plan.Ok, plan.Why);
            // 3000 mm at most 150 apart: 21 stations, 0 to 3000.
            Assert.Equal(21, hoops.Count);
            Assert.Equal("CC#h9#stirrups#all#s1", hoops[0].Id);
            Assert.Equal("CC#h9#stirrups#all#s21", hoops[20].Id);
            Assert.Equal(21, hoops.Select(h => h.Id).Distinct().Count());
            for (int k = 0; k < hoops.Count; k++)
            {
                StructuralRebarRule h = hoops[k];
                Assert.Equal(RebarLayout.Single, h.Layout.Layout);
                Assert.Equal("M_T6", h.ShapeName);
                Assert.True(h.Closed);
                Assert.NotNull(h.Placement);
                Assert.Equal(StructuralShapePlacement.KindLappedCircle, h.Placement.Kind);
                Assert.Equal(205, h.Placement.CentrelineRadiusMm, 6);
                Assert.Equal(150.0 * k, h.Placement.CentreMm[2], 6);
                Assert.Equal(0, h.Placement.CentreMm[0], 6); Assert.Equal(0, h.Placement.CentreMm[1], 6);
                Assert.All(h.CurvesMm, p => Assert.Equal(150.0 * k, p[2], 6));
                Assert.All(h.CurvesMm, p => Assert.Equal(205, Math.Sqrt(p[0] * p[0] + p[1] * p[1]), 6));
                Assert.Equal(2, h.ArcThrough.Count);
                // The axis the hoop's plane is square to is the member's own.
                Assert.Equal(1, Math.Abs(h.Placement.AxisMm[2]), 9);
                Assert.Null(h.CoverPrediction);
            }
        }

        [Fact]
        public void A_suppressed_end_station_puts_no_hoop_there()
        {
            List<StructuralRebarRule> hoops = ExpandHoops(Set(Hoop,
                "[ { 'name': 'all', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 150, 'include_last_bar': false } } ]"), out StirrupZoneResult plan);
            Assert.True(plan.Ok, plan.Why);
            Assert.Equal(20, hoops.Count);
            Assert.Equal(2850, hoops.Last().Placement.CentreMm[2], 6);
        }

        [Fact]
        public void A_spiral_runs_from_the_zone_start_over_the_zone_length()
        {
            List<StructuralRebarRule> one = ExpandHoops(Set(Spiral, "[ { 'name': 'all', 'layout': { 'rule': 'single' } } ]",
                "'start_offset_mm': 50, 'end_offset_mm': 50,"), out StirrupZoneResult plan);
            Assert.True(plan.Ok, plan.Why);
            StructuralRebarRule sp = one.Single();
            Assert.Equal("CC#h9#stirrups#all", sp.Id);
            Assert.Equal("M_SP", sp.ShapeName);
            Assert.False(sp.Closed);
            Assert.Empty(sp.ArcThrough);
            Assert.Equal(RebarLayout.Single, sp.Layout.Layout);
            PlacedShapeGeometry g = sp.Placement;
            Assert.Equal(StructuralShapePlacement.KindSpiral, g.Kind);
            Assert.Equal(50, g.CentreMm[2], 6);
            Assert.Equal(2900, g.HeightMm.Value, 6);
            Assert.Equal(75, g.PitchMm.Value, 6);
            Assert.Equal(1, g.BaseFinishingTurns.Value);
            Assert.Equal(2, g.TopFinishingTurns.Value);
            // The measured path is the helix itself: every point on the radius, base to top.
            Assert.All(sp.CurvesMm, p => Assert.Equal(205, Math.Sqrt(p[0] * p[0] + p[1] * p[1]), 6));
            Assert.Equal(50, sp.CurvesMm.Min(p => p[2]), 6);
            Assert.Equal(2950, sp.CurvesMm.Max(p => p[2]), 6);
            Assert.Equal(50, sp.CurvesMm.First()[2], 6);
            Assert.Equal(2950, sp.CurvesMm.Last()[2], 6);
        }

        [Fact]
        public void The_helix_has_the_declared_turns_and_flat_finishing_turns()
        {
            var g = new PlacedShapeGeometry
            {
                Kind = StructuralShapePlacement.KindSpiral, CentreMm = new[] { 0.0, 0, 0 }, AxisMm = new[] { 0.0, 0, 1 },
                XVec = new[] { 1.0, 0, 0 }, YVec = new[] { 0.0, 1, 0 }, CentrelineRadiusMm = 100, HeightMm = 300, PitchMm = 100,
                BaseFinishingTurns = 1, TopFinishingTurns = 1
            };
            List<double[]> path = PlacedShapes.HelixPath(g, 36);
            // 1 flat + 3 rising + 1 flat turns, 36 segments each, one point more than segments.
            Assert.Equal(5 * 36 + 1, path.Count);
            Assert.All(path.Take(37), p => Assert.Equal(0, p[2], 9));
            Assert.All(path.Skip(36 * 4), p => Assert.Equal(300, p[2], 9));
            Assert.Equal(150, path[36 + 54][2], 6);
        }

        // ---------------------------------------------------------- definition

        [Theory]
        [InlineData("lapped_circle", "lapped_circle", null)]
        [InlineData("spiral", "spiral", null)]
        [InlineData("lapped_circle", "spiral", "shape_is_not_a_circle")]
        [InlineData("lapped_circle", "arc", "shape_is_not_a_circle")]
        [InlineData("lapped_circle", "segments", "shape_is_not_a_circle")]
        [InlineData("spiral", "lapped_circle", "shape_is_not_a_spiral")]
        [InlineData("spiral", "segments", "shape_is_not_a_spiral")]
        [InlineData("spiral", "unreadable", "shape_definition_unreadable")]
        [InlineData("lapped_circle", null, "shape_definition_unreadable")]
        public void The_shape_is_judged_by_its_definition_never_by_its_name(string wanted, string read, string code)
        {
            string[] refusal = PlacedShapes.DefinitionRefusal(wanted, read, "M_T6");
            if (code == null) Assert.Null(refusal);
            else
            {
                Assert.Equal(code, refusal[0]);
                Assert.Contains("M_T6", refusal[1]);
            }
        }

        [Fact]
        public void The_box_a_hoop_is_scaled_to_includes_the_bar_thickness()
        {
            var g = new PlacedShapeGeometry
            {
                Kind = StructuralShapePlacement.KindLappedCircle, CentreMm = new[] { 1000.0, 2000, 300 }, AxisMm = new[] { 0.0, 0, 1 },
                XVec = new[] { 1.0, 0, 0 }, YVec = new[] { 0.0, 1, 0 }, CentrelineRadiusMm = 205
            };
            PlacedShapes.Box(g, 10, out double[] origin, out double[] x, out double[] y);
            Assert.Equal(new[] { 1000 - 210.0, 2000 - 210.0, 300 }, origin);
            Assert.Equal(new[] { 420.0, 0, 0 }, x);
            Assert.Equal(new[] { 0.0, 420, 0 }, y);
        }

        [Theory]
        [InlineData("diameter", "centerline", 410.0)]
        [InlineData("diameter", "external", 420.0)]
        [InlineData("diameter", "internal", 400.0)]
        [InlineData("radius", "centerline", 205.0)]
        [InlineData("radius", "external", 210.0)]
        [InlineData("radius", "internal", 200.0)]
        public void The_size_parameter_is_predicted_by_how_the_shape_measures_it(string constraint, string reference, double mm) =>
            Assert.Equal(mm, PlacedShapes.ExpectedSizeMm(constraint, reference, 205, 10).Value, 9);

        [Fact]
        public void A_size_measured_some_other_way_is_not_predicted() =>
            Assert.Null(PlacedShapes.ExpectedSizeMm("diameter", "sideways", 205, 10));

        // ---------------------------------------------------------- re-reading

        private static PlacedShapeGeometry HoopAt(double z) => new PlacedShapeGeometry
        {
            Kind = StructuralShapePlacement.KindLappedCircle, CentreMm = new[] { 0.0, 0, z }, AxisMm = new[] { 0.0, 0, 1 },
            XVec = new[] { 1.0, 0, 0 }, YVec = new[] { 0.0, 1, 0 }, CentrelineRadiusMm = 205
        };

        private static List<double[]> Circle(double r, double z, double fromDeg = 0, double toDeg = 380, int n = 80)
        {
            var pts = new List<double[]>();
            for (int i = 0; i <= n; i++)
            {
                double a = (fromDeg + (toDeg - fromDeg) * i / n) * Math.PI / 180;
                pts.Add(new[] { r * Math.Cos(a), r * Math.Sin(a), z });
            }
            return pts;
        }

        [Fact]
        public void A_lapped_hoop_drawn_where_it_was_placed_verifies()
        {
            PlacedShapeCheck c = PlacedShapes.CheckCentreline(HoopAt(600), Circle(205, 600), 2.0);
            Assert.True(c.Evaluated);
            Assert.True(c.Verified, c.Why);
        }

        [Fact]
        public void A_hoop_of_another_size_or_at_another_station_does_not()
        {
            Assert.False(PlacedShapes.CheckCentreline(HoopAt(600), Circle(210, 600), 2.0).Verified);
            Assert.False(PlacedShapes.CheckCentreline(HoopAt(600), Circle(205, 610), 2.0).Verified);
        }

        [Fact]
        public void Half_a_hoop_on_the_right_radius_does_not()
        {
            PlacedShapeCheck c = PlacedShapes.CheckCentreline(HoopAt(600), Circle(205, 600, 0, 180), 2.0);
            Assert.False(c.Verified);
            Assert.True(c.LargestGapDegrees > 90);
        }

        [Fact]
        public void No_drawn_centreline_is_unknown_and_never_a_pass()
        {
            PlacedShapeCheck c = PlacedShapes.CheckCentreline(HoopAt(600), null, 2.0);
            Assert.False(c.Evaluated);
            Assert.False(c.Verified);
            Assert.False((bool)c.ToJson()["verified"]);
        }

        [Fact]
        public void A_spiral_drawn_over_its_height_verifies_and_a_short_one_does_not()
        {
            var g = new PlacedShapeGeometry
            {
                Kind = StructuralShapePlacement.KindSpiral, CentreMm = new[] { 0.0, 0, 50 }, AxisMm = new[] { 0.0, 0, 1 },
                XVec = new[] { 1.0, 0, 0 }, YVec = new[] { 0.0, 1, 0 }, CentrelineRadiusMm = 205, HeightMm = 2900, PitchMm = 75,
                BaseFinishingTurns = 1, TopFinishingTurns = 1
            };
            Assert.True(PlacedShapes.CheckCentreline(g, PlacedShapes.HelixPath(g, 36), 2.0).Verified);
            var shortG = new PlacedShapeGeometry
            {
                Kind = g.Kind, CentreMm = g.CentreMm, AxisMm = g.AxisMm, XVec = g.XVec, YVec = g.YVec, CentrelineRadiusMm = 205,
                HeightMm = 2000, PitchMm = 75, BaseFinishingTurns = 1, TopFinishingTurns = 1
            };
            Assert.False(PlacedShapes.CheckCentreline(g, PlacedShapes.HelixPath(shortG, 36), 2.0).Verified);
        }

        [Fact]
        public void The_spiral_parameters_are_held_to_the_declaration_and_unread_is_not_a_pass()
        {
            var g = new PlacedShapeGeometry
            {
                Kind = StructuralShapePlacement.KindSpiral, CentrelineRadiusMm = 205, HeightMm = 2900, PitchMm = 75,
                BaseFinishingTurns = 1, TopFinishingTurns = 2
            };
            Assert.True((bool)PlacedShapes.CompareSpiralParameters(g, 75, 2900, 1, 2, 2.0)["verified"]);
            Assert.False((bool)PlacedShapes.CompareSpiralParameters(g, 100, 2900, 1, 2, 2.0)["verified"]);
            Assert.False((bool)PlacedShapes.CompareSpiralParameters(g, 75, 2900, 1, 1, 2.0)["verified"]);
            Assert.False((bool)PlacedShapes.CompareSpiralParameters(g, null, 2900, 1, 2, 2.0)["verified"]);
        }

        // --------------------------------------------------------------- audit

        private static JObject ExpectedPlaced()
        {
            return new JObject
            {
                ["rule_id"] = "CC#h9#stirrups#all#s3",
                ["host_id"] = 9,
                ["bar_type"] = new JObject { ["id"] = 300, ["nominal_diameter_mm"] = 10.0 },
                ["shape"] = new JObject { ["declared"] = true, ["id"] = 400 },
                ["style"] = "standard",
                ["closed"] = true,
                ["curve_mm"] = new JArray(new JArray(205, 0, 300), new JArray(-205, 0, 300)),
                ["normal"] = new JObject { ["x"] = 0.0, ["y"] = 0.0, ["z"] = 1.0 },
                ["expected_total_steel_length_mm"] = 1288.05,
                ["layout"] = new JObject { ["rule"] = "single", ["number_of_bar_positions"] = 1, ["quantity"] = 1 },
                ["terminations"] = new JObject
                {
                    ["start"] = new JObject { ["hook_type_id"] = -1, ["orientation"] = "left" },
                    ["end"] = new JObject { ["hook_type_id"] = -1, ["orientation"] = "left" }
                },
                ["placement"] = HoopAt(300).ToJson()
            };
        }

        private static JObject ObservedPlaced()
        {
            return new JObject
            {
                ["id"] = 9001,
                ["host"] = new JObject { ["id"] = 9, ["resolved"] = true },
                ["bar_type"] = new JObject { ["resolved"] = true, ["id"] = 300, ["nominal_diameter_mm"] = 10.0 },
                ["shape"] = new JObject { ["id"] = 400, ["resolved"] = true },
                ["style_horizun"] = "standard",
                ["layout"] = new JObject { ["rule_horizun"] = "single", ["number_of_bar_positions"] = 1, ["quantity"] = 1,
                                           ["normal"] = new JObject { ["x"] = 1.0, ["y"] = 0.0, ["z"] = 0.0 } },
                ["terminations"] = new JArray(
                    new JObject { ["end"] = 0, ["hook_type_id"] = -1, ["hook_readable"] = true, ["orientation"] = "right" },
                    new JObject { ["end"] = 1, ["hook_type_id"] = -1, ["hook_readable"] = true, ["orientation"] = "right" }),
                ["geometry"] = new JObject { ["centreline_points_mm"] = new JArray(Circle(205, 300).Select(p => new JArray(p[0], p[1], p[2]))) },
                ["measured"] = new JObject { ["total_length_mm"] = 1500.0, ["bar_centreline_length_mm"] = 1500.0 }
            };
        }

        [Fact]
        public void The_audit_compares_a_placed_shape_by_its_placement_not_by_polyline_or_length()
        {
            // The lapped hoop is longer than its circle by the lap, its drawn points are not the
            // declared two, and a single placed bar's set normal is not a fact about the hoop:
            // none of the three may become a finding. The placement check is the comparison.
            JArray f = RebarAuditRules.CompareBar(ExpectedPlaced(), ObservedPlaced(), new StructuralTolerances());
            Assert.Empty(f);
        }

        [Fact]
        public void A_placed_hoop_in_the_wrong_place_is_a_finding_with_its_evidence()
        {
            JArray f = PlacedShapes.AuditFindings("CC#h9#stirrups#all#s3", 9001, (JObject)ExpectedPlaced()["placement"],
                Circle(205, 320), null, new StructuralTolerances());
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.PlacedShapeDiffers, (string)one["code"]);
            Assert.Equal(RebarSeverity.Error, (string)one["severity"]);
            Assert.Empty(PlacedShapes.AuditFindings("CC#h9#stirrups#all#s3", 9001, (JObject)ExpectedPlaced()["placement"],
                Circle(205, 300), null, new StructuralTolerances()));
        }

        [Fact]
        public void A_placed_hoop_whose_centreline_could_not_be_read_is_unknown()
        {
            JArray f = PlacedShapes.AuditFindings("r", 1, (JObject)ExpectedPlaced()["placement"], null, null, new StructuralTolerances());
            Assert.Equal(RebarSeverity.Unknown, (string)f.OfType<JObject>().Single()["severity"]);
        }

        [Fact]
        public void The_placement_survives_its_own_json()
        {
            PlacedShapeGeometry back = PlacedShapeGeometry.FromJson(HoopAt(300).ToJson());
            Assert.Equal(205, back.CentrelineRadiusMm, 9);
            Assert.Equal(300, back.CentreMm[2], 9);
            Assert.Equal(StructuralShapePlacement.KindLappedCircle, back.Kind);
            Assert.Contains(RebarFinding.PlacedShapeDiffers, RebarFinding.All);
        }
    }
}

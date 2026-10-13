// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L2: arcs in a bar's centreline ({ "through": [x, y, z] } between two
// points, a three-point arc), and circular columns - read off the solid, with bars
// on a ring and a circular hoop made of two arcs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarArcsAndCircularTests
    {
        // ------------------------------------------------------------------ arcs

        private static StructuralRequirementSet Rule(string curve, bool closed)
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'arcs', 'version': '1' },
  'bar_types': [ { 'id': 'N10', 'type_name': '10M' } ],
  'reinforcement_rules': [ { 'id': 'H', 'host': { 'element_ids': [ 1 ] }, 'bar_type': 'N10', 'style': 'stirrup_tie',
    'curve_mm': " + curve + @", 'closed': " + (closed ? "true" : "false") + @", 'normal': [ 0, 0, 1 ],
    'layout': { 'rule': 'single' }, 'allow_new_shape': true } ] }"));
        }

        private const string Circle = "[ [ 200, 0, 0 ], { 'through': [ 0, 200, 0 ] }, [ -200, 0, 0 ], { 'through': [ 0, -200, 0 ] } ]";

        [Fact]
        public void A_circle_is_two_arcs_and_its_path_is_its_circumference()
        {
            StructuralRequirementSet s = Rule(Circle, true);
            Assert.True(s.Ok, s.Error);
            StructuralRebarRule r = s.RebarRules.Single();
            Assert.Equal(2, r.CurvesMm.Count);
            Assert.Equal(2, r.ArcThrough.Count);
            List<double[]> path = RebarArcs.Path(r.CurvesMm, r.Closed, r.ArcThrough, RebarArcs.DefaultChordMm);
            Assert.Equal(2 * Math.PI * 200, RebarPlanRules.CentrelineLengthMm(path, true), 0);
            Assert.All(path, p => Assert.Equal(200, Math.Sqrt(p[0] * p[0] + p[1] * p[1]), 6));
        }

        [Fact]
        public void A_bar_with_one_arc_between_two_straights_keeps_its_straights()
        {
            // A quarter circle of radius 200 centred on (1000, 200): from (1000, 0) to (1200, 200).
            StructuralRequirementSet s = Rule("[ [ 0, 0, 0 ], [ 1000, 0, 0 ], { 'through': [ 1141.4213562, 58.5786438, 0 ] }, [ 1200, 200, 0 ] ]", false);
            Assert.True(s.Ok, s.Error);
            Assert.Single(s.RebarRules.Single().ArcThrough);
        }

        [Fact]
        public void A_through_point_on_the_chord_is_refused() =>
            Assert.Equal(StructuralRequirementSet.CodeGeometry,
                Rule("[ [ 0, 0, 0 ], { 'through': [ 50, 0, 0 ] }, [ 100, 0, 0 ] ]", false).Code);

        [Fact]
        public void A_through_point_first_or_twice_in_a_row_is_refused()
        {
            Assert.Equal(StructuralRequirementSet.CodeGeometry, Rule("[ { 'through': [ 50, 10, 0 ] }, [ 0, 0, 0 ], [ 100, 0, 0 ] ]", false).Code);
            Assert.Equal(StructuralRequirementSet.CodeGeometry, Rule("[ [ 0, 0, 0 ], { 'through': [ 50, 10, 0 ] }, { 'through': [ 60, 10, 0 ] }, [ 100, 0, 0 ] ]", false).Code);
        }

        [Fact]
        public void A_trailing_through_point_needs_a_closed_bar() =>
            Assert.Equal(StructuralRequirementSet.CodeGeometry, Rule(Circle, false).Code);

        [Fact]
        public void A_straight_bar_has_no_arcs_and_its_path_is_its_points()
        {
            StructuralRequirementSet s = Rule("[ [ 0, 0, 0 ], [ 100, 0, 0 ] ]", false);
            Assert.True(s.Ok, s.Error);
            Assert.Empty(s.RebarRules.Single().ArcThrough);
            Assert.Equal(2, RebarArcs.Path(s.RebarRules.Single().CurvesMm, false, s.RebarRules.Single().ArcThrough, 0.5).Count);
        }

        // ------------------------------------------------------- circular frame

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

        private static SectionHostFrame Frame() =>
            SectionFrames.Measure(9, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }, Cylinder());

        [Fact]
        public void A_cylinder_whose_end_cap_centres_sit_a_hair_off_the_axis_is_still_circular()
        {
            // MEASURED, Revit 2024 and 2026 (acero L7, P1): a revolved column read "vertex radii from 0
            // to 250" and was refused - its end caps' centre vertices lie on the axis to a hair, not to
            // a micron, and were taken for a radius. A vertex within the slack of the axis is on it.
            HostMesh m = Cylinder();
            m.Vertices[0][0] += 0.05; m.Vertices[1][1] -= 0.03;
            SectionHostFrame f = SectionFrames.Measure(9, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }, m);
            Assert.True(f.Circular, f.NotRectangular);
            Assert.Equal(250, f.RadiusMm, 0);
        }

        [Fact]
        public void A_cylinder_is_read_as_a_circular_section()
        {
            SectionHostFrame f = Frame();
            Assert.True(f.Circular);
            Assert.Equal(250, f.RadiusMm, 0);
            Assert.NotNull(f.NotRectangular);
        }

        [Fact]
        public void A_box_is_not_circular() =>
            Assert.False(SectionFrames.Measure(9, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 },
                HostMesh.Box(new double[] { -250, -250, 0 }, new double[] { 250, 250, 3000 })).Circular);

        private static StructuralRequirementSet Ring(string layers, string extra = "")
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'ring', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' }, { 'id': 'N10', 'type_name': '10M' } ],
  'section_rules': [ { 'id': 'CC', 'host': { 'element_ids': [ 9 ] }, 'cover': { 'distance_mm': 40 },
    'layers': " + layers + extra + @" } ] }"));
        }

        [Fact]
        public void A_ring_lays_its_bars_evenly_round_inside_the_hoop()
        {
            StructuralRequirementSet s = Ring("[ { 'name': 'ring', 'face': 'ring', 'count': 6, 'bar_type': 'N16', 'extend': 'host_length' } ]",
                ", 'stirrups': { 'bar_type': 'N10', 'zones': [ { 'name': 'all', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 150 } } ] }");
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.ExpandAll(s.SectionRules.Single(), Frame(), id => id == "N10" ? 10 : 16, null, null,
                out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zones);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(6, bars.Count);
            // 250 - 40 cover - 10 hoop - 8 half bar = 192 from the axis.
            Assert.All(bars, b => Assert.Equal(192, Math.Sqrt(b.CurvesMm[0][0] * b.CurvesMm[0][0] + b.CurvesMm[0][1] * b.CurvesMm[0][1]), 6));
            Assert.All(bars, b => Assert.Equal(RebarLayout.Single, b.Layout.Layout));
            Assert.Equal(6, bars.Select(b => b.Id).Distinct().Count());

            StructuralStirrupZoneRule hoop = zones.Single();
            Assert.True(hoop.Closed);
            Assert.Equal(2, hoop.ProfileMm.Count);
            Assert.Equal(2, hoop.ProfileArcThrough.Count);
            // The hoop's centreline: 250 - 40 - 5.
            Assert.All(hoop.ProfileMm, p => Assert.Equal(205, Math.Sqrt(p[0] * p[0] + p[1] * p[1]), 6));
        }

        [Fact]
        public void A_ring_on_a_rectangular_section_is_refused()
        {
            StructuralRequirementSet s = Ring("[ { 'name': 'ring', 'face': 'ring', 'count': 6, 'bar_type': 'N16', 'extend': 'host_length' } ]");
            SectionResult r = SectionRules.ExpandAll(s.SectionRules.Single(),
                SectionFrames.Measure(9, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 },
                    HostMesh.Box(new double[] { -250, -250, 0 }, new double[] { 250, 250, 3000 })),
                id => 16, null, null, out _, out _);
            Assert.Equal(SectionRules.CodeRingNeedsCircle, r.Code);
        }

        [Fact]
        public void A_face_layer_on_a_circular_section_is_refused_as_not_rectangular()
        {
            StructuralRequirementSet s = Ring("[ { 'face': 'top', 'count': 2, 'bar_type': 'N16', 'extend': 'host_length' } ]");
            SectionResult r = SectionRules.ExpandAll(s.SectionRules.Single(), Frame(), id => 16, null, null, out _, out _);
            Assert.Equal(SectionRules.CodeNotRectangular, r.Code);
        }

        [Fact]
        public void A_ring_of_fewer_than_three_bars_is_refused() =>
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue,
                Ring("[ { 'name': 'ring', 'face': 'ring', 'count': 2, 'bar_type': 'N16', 'extend': 'host_length' } ]").Code);
    }
}

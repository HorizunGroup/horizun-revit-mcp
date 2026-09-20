// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE RIGHT WALL, NOT MERELY A WALL IN RANGE.
//
// MEASURED: a receptacle drawn 60 mm from a wall the reading had not converted
// was hosted on the nearest converted wall, 220 mm away - built, verified and
// matched, on the wrong wall. These cases use that drawing's coordinates, and a
// corner, where a second wall a few millimetres nearer is NOT a different host.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadHostPlausibilityTests
    {
        private static CadSegment Line(double ax, double ay, double bx, double by) =>
            new CadSegment(new CadPoint(ax, ay), new CadPoint(bx, by), "PLAN-A-WALL", CadCurveKind.Line, 0);

        // ---- END-face hosts (campaign 5) ----------------------------------------------
        // Case 159C4 (unit 914), the drawing's own lines: a 254 mm pier closes the west end of the
        // wall the model built from x 34150; the symbol stands 62 mm in front of the PIER's end cap
        // (x 34006) and 206 mm in front of the model wall's end.
        private static readonly List<CadSegment> Pier159C4 = new List<CadSegment>
        {
            Line(34006, 3715, 34006, 3461), Line(34022, 3715, 34022, 3461), Line(34023, 3715, 34023, 3461),
            Line(34022, 3461, 34152, 3461), Line(34152, 3715, 34022, 3715),
            Line(34150, 3499, 36906, 3499), Line(34150, 3683, 36906, 3683)
        };

        [Fact]
        public void A_symbol_on_an_end_the_model_does_not_have_is_not_on_the_model_walls_end()
        {
            CadHostPlausibilityResult r = CadHostPlausibility.CheckEnd(
                new CadPoint(33944.2, 3525.1), new CadPoint(34150, 3591), new CadVector(-1, 0),
                109.55, Pier159C4, 2.0, 1.0);

            Assert.True(r.NearerWallDrawn);
            Assert.Equal(62.0, r.OtherWallMm.Value, 0);
            Assert.False(r.Jamb);
        }

        [Fact]
        public void A_symbol_in_front_of_the_drawn_end_that_the_model_has_is_plausible()
        {
            var lines = new List<CadSegment>
            {
                Line(34150, 3481, 34150, 3700),                        // the drawn cap IS the model's end
                Line(34150, 3481, 36906, 3481), Line(34150, 3700, 36906, 3700)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.CheckEnd(
                new CadPoint(34110, 3591), new CadPoint(34150, 3591), new CadVector(-1, 0), 109.55, lines, 2.0, 1.0);

            Assert.False(r.NearerWallDrawn);
            Assert.False(r.Jamb);
        }

        [Fact]
        public void An_end_where_the_same_wall_resumes_past_the_symbol_is_a_jamb()
        {
            var lines = new List<CadSegment>
            {
                Line(34150, 3481, 34150, 3700),
                Line(34150, 3481, 36906, 3481), Line(34150, 3700, 36906, 3700),
                Line(33250, 3481, 31000, 3481), Line(33250, 3700, 31000, 3700)   // resumes 900 mm on
            };
            CadHostPlausibilityResult r = CadHostPlausibility.CheckEnd(
                new CadPoint(34110, 3591), new CadPoint(34150, 3591), new CadVector(-1, 0), 109.55, lines, 2.0, 1.0);

            Assert.True(r.Jamb);
        }

        [Fact]
        public void A_symbol_drawn_against_an_unconverted_wall_is_not_plausibly_on_the_converted_one()
        {
            // The converted host runs along y = 19007.1; its south face line is at
            // 18905.5. The symbol is 220 mm from that face and 60 mm from the
            // vertical wall line at x = 34259.8, which no model wall stands on.
            var lines = new List<CadSegment>
            {
                Line(33361.3, 18905.5, 34259.8, 18905.5),
                Line(33343.8, 18938.9, 34293.2, 18938.9),
                Line(34259.8, 18026.1, 34259.8, 18905.5)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(34199.4, 18685.4), new CadPoint(33235.9, 19007.1), new CadPoint(34293.2, 19007.1),
                76.2, lines, 2.0, 25.0, 250.0);

            Assert.True(r.NearerWallDrawn);
            Assert.Equal(220.1, r.HostFaceMm.Value, 0);
            Assert.Equal(60.4, r.OtherWallMm.Value, 0);
            Assert.Equal(34259.8, r.OtherWallLine.A.X, 1);
        }

        [Fact]
        public void The_face_of_a_withdrawn_wall_behind_the_host_is_not_the_host_face()
        {
            // Unit 915F, symbol 158A1. Converted host: 101.6 mm on x = 28956.0, faces at
            // 28905.2 and 29006.8. The 119.1 mm wall between 29057.6 and 29176.7 was
            // withdrawn (no type); the symbol is drawn 58.7 mm from its face.
            var lines = new List<CadSegment>
            {
                Line(28905.2, 15335.2, 28905.2, 18700.7),
                Line(29006.8, 15335.2, 29006.8, 18700.7),
                Line(29057.6, 15335.2, 29057.6, 18700.7),
                Line(29176.7, 15335.2, 29176.7, 18700.7)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(29235.4, 18514.9), new CadPoint(28956.0, 15335.2), new CadPoint(28956.0, 18700.7),
                50.8, lines, 2.0, 25.0, 250.0);

            Assert.True(r.NearerWallDrawn);
            Assert.Equal(228.6, r.HostFaceMm.Value, 1);
            Assert.Equal(58.7, r.OtherWallMm.Value, 1);
            Assert.Equal(29176.7, r.OtherWallLine.A.X, 1);
        }

        [Fact]
        public void A_symbol_on_the_lining_of_an_unread_layer_is_not_on_the_core_behind_it()
        {
            // Unit 915F, symbol 158C5: drawn on a lining line (x = 34275.7) of a stud
            // layer the reading did not build; the converted host is the 203.2 mm core
            // between 34445.6 and 34648.8.
            var lines = new List<CadSegment>
            {
                Line(34259.8, 18026.1, 34259.8, 18905.5),
                Line(34275.7, 18010.2, 34275.7, 18921.4),
                Line(34293.2, 18938.9, 34293.2, 18008.6),
                Line(34445.6, 18008.6, 34445.6, 20148.5),
                Line(34648.8, 18008.6, 34648.8, 19840.6)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(34275.6, 18230.0), new CadPoint(34547.2, 18008.6), new CadPoint(34547.2, 19840.6),
                101.6, lines, 2.0, 25.0, 250.0);

            Assert.True(r.NearerWallDrawn);
            Assert.Equal(170.0, r.HostFaceMm.Value, 1);
        }

        [Fact]
        public void The_corner_of_a_neighbouring_wall_past_the_symbol_is_not_a_nearer_wall()
        {
            // Unit 914, symbol 15969: host 161.9 mm on y = 7048.5 (face 7129.5), the
            // symbol 168.6 mm off it. A neighbouring wall steps out to y = 7200.9 from
            // x = 32862.8 on, 91 mm past the symbol: its corner is 133 mm away.
            var lines = new List<CadSegment>
            {
                Line(32202.4, 7129.5, 32878.7, 7129.5),
                Line(32878.7, 7113.6, 32218.3, 7113.6),
                Line(32862.8, 7112.0, 32862.8, 7200.9),
                Line(32862.8, 7200.9, 32878.7, 7200.9),
                Line(33008.9, 7200.9, 32878.7, 7200.9),
                Line(32878.7, 7200.9, 32878.7, 7113.6)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(32771.9, 7298.1), new CadPoint(32218.3, 7048.5), new CadPoint(32880.3, 7048.5),
                80.95, lines, 2.0, 25.0, 250.0);

            Assert.False(r.NearerWallDrawn);
            Assert.Equal(168.6, r.HostFaceMm.Value, 1);
            Assert.Null(r.OtherWallMm);
        }

        [Fact]
        public void The_cap_that_closes_the_hosts_own_end_is_not_a_nearer_wall()
        {
            // Unit 912, symbol 15BB4: host 161.9 mm on x = 41770.3 from y 26114.4; its end is
            // closed by a line across it at y 26114.4; the symbol is drawn inside the wall.
            var lines = new List<CadSegment>
            {
                Line(41705.2, 26114.4, 41835.4, 26114.4),
                Line(41705.2, 26114.4, 41705.2, 26506.5),
                Line(41835.4, 26114.4, 41835.4, 26506.5)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(41757.6, 26121.8), new CadPoint(41770.3, 26114.4), new CadPoint(41770.3, 26508.1),
                80.95, lines, 2.0, 25.0, 250.0);
            Assert.False(r.NearerWallDrawn);
            Assert.Null(r.OtherWallMm);
        }

        [Fact]
        public void A_wall_meeting_the_host_part_way_along_is_still_another_wall()
        {
            // a line across the band but NOT at an end is not a cap
            var lines = new List<CadSegment>
            {
                Line(0, 80, 3000, 80),
                Line(1500, -80, 1500, -600)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(1480, -120), new CadPoint(0, 0), new CadPoint(3000, 0), 80, lines, 2.0, 25.0, 250.0);
            Assert.NotNull(r.OtherWallMm);
        }

        [Fact]
        public void A_finish_line_just_outside_the_host_face_is_still_the_host_face()
        {
            // Unit 915F, symbol 1587E: host 173.0 mm on y = 16136.1 (face 16049.6); the
            // drawn face is the finish line at 16035.3, 14.3 mm further out.
            var lines = new List<CadSegment>
            {
                Line(29905.3, 16035.3, 30395.0, 16035.3),
                Line(29905.3, 16049.6, 30395.0, 16049.6),
                Line(29905.3, 16222.6, 30395.0, 16222.6)
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(30325.9, 16035.9), new CadPoint(29905.3, 16136.1), new CadPoint(30395.0, 16136.1),
                86.5, lines, 2.0, 25.0, 250.0);

            Assert.False(r.NearerWallDrawn);
            Assert.Null(r.OtherWallMm);
        }

        [Fact]
        public void A_symbol_beside_a_corner_is_still_on_its_own_wall()
        {
            // 70 mm from its host face, 55 mm from the face of the wall meeting it:
            // within the tolerance, so it is a corner and not a different host.
            var lines = new List<CadSegment>
            {
                Line(0, 0, 3000, 0),          // the host's south face
                Line(0, 0, 0, -3000)          // the face of the wall that meets it from the south
            };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(55, -70), new CadPoint(0, 75), new CadPoint(3000, 75),
                75, lines, 2.0, 25.0, 250.0);

            Assert.False(r.NearerWallDrawn);
        }

        [Fact]
        public void A_host_whose_faces_are_not_drawn_is_not_judged()
        {
            var lines = new List<CadSegment> { Line(500, -300, 500, 300) };
            CadHostPlausibilityResult r = CadHostPlausibility.Check(
                new CadPoint(480, -100), new CadPoint(0, 0), new CadPoint(3000, 0), 75, lines, 2.0, 25.0, 250.0);

            Assert.False(r.NearerWallDrawn);
            Assert.Null(r.HostFaceMm);
        }

        [Fact]
        public void Host_layers_are_declared_only_with_a_wall_host_and_never_empty()
        {
            string Set(string rule) => (@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'd', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r', 'layers': ['E-P'], 'produces': 'electrical_fixture',
                           'family_type': 'Receptacle: Duplex', 'level': 'Level 1',
                           'geometry': { 'from': 'blocks', 'blocks': ['OUT'] } RULE } ]
            }").Replace("RULE", rule).Replace('\'', '"');

            CadRequirementSet ok = CadRequirementSet.Load(JObject.Parse(
                Set(", 'hosted_on': 'wall', 'host_layers': ['*A-WALL']")));
            Assert.Equal(new[] { "*A-WALL" }, ok.Rules.Single().HostLayers);

            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(JObject.Parse(
                Set(", 'host_layers': ['*A-WALL']"))));
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(JObject.Parse(
                Set(", 'hosted_on': 'wall', 'host_layers': []"))));
        }
    }
}

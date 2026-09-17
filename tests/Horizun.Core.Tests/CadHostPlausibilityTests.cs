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

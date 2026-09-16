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

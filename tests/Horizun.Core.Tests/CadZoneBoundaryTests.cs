// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A UNIT ENDS ON THE CENTRE OF ITS WALLS, AND ITS SHAPE IS NOT ALWAYS A BOX.
//
// MEASURED (units 915F and 914 of the E-300 drawing): each zone was bounded on
// the centreline of its demising walls, facade and corridor wall. The outer face
// line of every one of those walls lay outside the zone and was dropped, so the
// walls were never read and 18 devices drawn on them had no host. And the stair
// core cuts a corner off unit 913, which a rectangle cannot leave out.
//
// These cases pin wall_margin_mm (read the far face, decide ownership on the
// centreline) and the polygon (concave, edges inclusive) with synthetic lines
// in the shape of those walls.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadZoneBoundaryTests
    {
        private static CadRequirementSet Set(string extent)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'zone', 'version': '1' },
              'source': { 'units': 'millimeter', 'extent_mm': EXTENT },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['*A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic', 'level': 'Level 1',
                           'height_mm': 2700, 'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60,
                                         'max_thickness_mm': 400, 'min_overlap_mm': 200 } } ]
            }";
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("EXTENT", extent).Replace('\'', '"')));
        }

        private static CadSegment Line(double ax, double ay, double bx, double by) =>
            new CadSegment(new CadPoint(ax, ay), new CadPoint(bx, by), "A-WALL", CadCurveKind.Line, 0);

        private static List<CadCandidate> Walls(CadInterpretation r) =>
            r.Candidates.Where(c => c.ProposedKind == "wall").ToList();

        /// <summary>
        /// A demising wall 160 mm thick on y = 0 (faces at -80 and +80), the zone
        /// above it (y from 0), a partition of the zone meeting it from above, and
        /// the neighbour's partition meeting it from below.
        /// </summary>
        private static CadSegment[] DemisingWall() => new[]
        {
            Line(0, 80, 4000, 80), Line(0, -80, 4000, -80),                    // the demising wall
            Line(1000, 80, 1000, 2000), Line(1120, 80, 1120, 2000),            // this unit's partition
            Line(2500, -80, 2500, -2000), Line(2620, -80, 2620, -2000)         // the neighbour's partition
        };

        private const string Box = "{ 'min_x': 0, 'min_y': 0, 'max_x': 4000, 'max_y': 3000, 'crossing': 'whole' }";
        private const string BoxWithMargin =
            "{ 'min_x': 0, 'min_y': 0, 'max_x': 4000, 'max_y': 3000, 'crossing': 'whole', 'wall_margin_mm': 300 }";

        [Fact]
        public void A_zone_bounded_on_a_wall_centreline_loses_that_wall_without_a_margin()
        {
            // The failure as it was measured: the far face lies outside and is dropped.
            CadInterpretation r = CadInterpretationRules.Interpret(DemisingWall().ToList(), Set(Box), "h");
            Assert.DoesNotContain(Walls(r), w => System.Math.Abs(w.Geometry[0].Y) < 1 && System.Math.Abs(w.Geometry[1].Y) < 1);
        }

        [Fact]
        public void With_a_margin_the_boundary_wall_is_read_and_the_neighbours_wall_is_named_not_built()
        {
            CadInterpretation r = CadInterpretationRules.Interpret(DemisingWall().ToList(), Set(BoxWithMargin), "h");
            List<CadCandidate> walls = Walls(r);

            Assert.Contains(walls, w => System.Math.Abs(w.Geometry[0].Y) < 1 && System.Math.Abs(w.Geometry[1].Y) < 1);
            Assert.Contains(walls, w => System.Math.Abs(w.Geometry[0].X - 1060) < 1);
            Assert.DoesNotContain(walls, w => System.Math.Abs(w.Geometry[0].X - 2560) < 1);

            Assert.True(r.SegmentsInMargin >= 3);
            JObject named = r.CandidatesOutsideExtent.Cast<JObject>().Single();
            Assert.Equal(2560, (double)named["from_mm"][0], 1);
            Assert.Contains("wall lines read up to 300 mm", r.ExtentDescription);
        }

        [Fact]
        public void The_margin_is_validated()
        {
            Assert.Throws<CadRequirementSetException>(() => Set(
                "{ 'min_x': 0, 'min_y': 0, 'max_x': 10, 'max_y': 10, 'wall_margin_mm': -1 }"));
            Assert.Throws<CadRequirementSetException>(() => Set(
                "{ 'min_x': 0, 'min_y': 0, 'max_x': 10, 'max_y': 10, 'wall_margin_mm': 'wide' }"));
            Assert.Equal(0, Set("{ 'min_x': 0, 'min_y': 0, 'max_x': 10, 'max_y': 10 }").ExtentMm.WallMarginMm);
        }

        // ---- polygons ------------------------------------------------------------

        /// <summary>An L: the stair core takes the lower-left corner (x under 1000, y under 1000).</summary>
        private const string L =
            "{ 'polygon': [[0, 3000], [3000, 3000], [3000, 0], [1000, 0], [1000, 1000], [0, 1000]] }";

        [Fact]
        public void A_concave_polygon_leaves_its_notch_out_and_keeps_its_edges()
        {
            CadExtent z = Set(L).ExtentMm;
            Assert.NotNull(z.Polygon);
            Assert.Equal(0, z.MinX);
            Assert.Equal(3000, z.MaxY);

            Assert.True(z.Contains(new CadPoint(2000, 500)));      // in the leg
            Assert.True(z.Contains(new CadPoint(500, 2000)));      // in the top
            Assert.False(z.Contains(new CadPoint(500, 500)));      // in the core: inside the box, not the unit
            Assert.True(z.Contains(new CadPoint(1000, 500)));      // on the core's wall line: an edge is inside
            Assert.True(z.Contains(new CadPoint(1000, 1000)));     // on the re-entrant vertex
            Assert.False(z.Contains(new CadPoint(3000.5, 500)));   // beyond the facade
            Assert.True(z.ContainsWithin(new CadPoint(900, 500), 150));
            Assert.Contains("polygon of 6 vertices", z.Describe());
        }

        [Fact]
        public void A_line_inside_the_notch_does_not_touch_the_zone_and_one_crossing_it_does()
        {
            CadExtent z = Set(L).ExtentMm;
            Assert.False(z.Touches(new CadPoint(100, 100), new CadPoint(800, 800)));
            Assert.True(z.Touches(new CadPoint(500, 500), new CadPoint(500, 1500)));
            Assert.True(z.Touches(new CadPoint(-100, 2000), new CadPoint(3100, 2000)));
            Assert.True(z.TouchesWithin(new CadPoint(100, 100), new CadPoint(900, 100), 150));
        }

        [Fact]
        public void A_polygon_is_refused_when_it_cannot_mean_a_zone()
        {
            Assert.Throws<CadRequirementSetException>(() => Set("{ 'polygon': [[0,0],[10,0]] }"));
            Assert.Throws<CadRequirementSetException>(() => Set("{ 'polygon': [[0,0],[10,0],[20,0]] }"));
            Assert.Throws<CadRequirementSetException>(() => Set("{ 'polygon': [[0,0],[10,10],[10,0],[0,10]] }"));
            Assert.Throws<CadRequirementSetException>(() => Set("{ 'polygon': [[0,0],[10,0],['a',5]] }"));
            Assert.Throws<CadRequirementSetException>(() => Set(
                "{ 'polygon': [[0,0],[10,0],[10,10]], 'min_x': 0 }"));
        }

        [Fact]
        public void Walls_are_read_against_the_polygon_and_not_its_envelope()
        {
            // A wall inside the core corner is inside the envelope but not the unit.
            var lines = new List<CadSegment>
            {
                Line(100, 300, 800, 300), Line(100, 450, 800, 450),        // in the notch
                Line(1500, 300, 2800, 300), Line(1500, 450, 2800, 450)     // in the leg
            };
            CadInterpretation r = CadInterpretationRules.Interpret(lines, Set(L), "h");
            List<CadCandidate> walls = Walls(r);
            Assert.Single(walls);
            Assert.True(walls[0].Geometry[0].X >= 1500 - 1);
            Assert.Equal(2, r.SegmentsOutsideExtent);
        }
    }
}

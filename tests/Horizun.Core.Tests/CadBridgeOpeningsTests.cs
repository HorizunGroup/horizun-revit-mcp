// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A PLAN DRAWING BREAKS A WALL AT EVERY OPENING.
//
// MEASURED on a DWG this repository exported from Revit 2026: a 12 m wall with
// a door and a window in it arrives as THREE separate pairs of lines, because
// that is what a plan section of a building looks like. A Revit wall is
// continuous and the opening cuts it.
//
// Read literally, that wall becomes three walls with gaps between them. Nothing
// looks wrong in plan; the count is wrong in every schedule, the walls do not
// join, and - which is how this was actually found - the door has no wall to
// live in, so horizun_plan_from_cad correctly refused to host it.
//
// Bridging is opt-in and bounded, because it can be wrong: two separate walls
// in line across a corridor look exactly like one wall with a wide opening, and
// only somebody who knows the building can say which. These tests pin both
// halves - that a declared bridge joins the run, and that an undeclared one
// never does.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadBridgeOpeningsTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static CadRequirementSet Set(double? bridgeMm = null, double pointMm = 1.0)
        {
            string bridge = bridgeMm == null ? ""
                : ", 'bridge_openings_mm': " + bridgeMm.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.0.0', 'title': 'Walls' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': POINT, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'min_overlap_fraction': 0.5BRIDGE } }]
            }".Replace('\'', '"').Replace("BRIDGE", bridge)
              .Replace("POINT", pointMm.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        /// <summary>A stretch of wall as two parallel faces, from x0 to x1 at the given y.</summary>
        private static IEnumerable<CadSegment> Run(double x0, double x1, double y = 0,
                                                   double thickness = 200, string layer = "A-WALL")
        {
            yield return new CadSegment(new CadPoint(x0, y - thickness / 2), new CadPoint(x1, y - thickness / 2), layer);
            yield return new CadSegment(new CadPoint(x0, y + thickness / 2), new CadPoint(x1, y + thickness / 2), layer);
        }

        /// <summary>The wall the fixture actually exported: 12 m, a 900 door, a 1200 window.</summary>
        private static List<CadSegment> WallWithTwoOpenings()
        {
            var segs = new List<CadSegment>();
            segs.AddRange(Run(0, 2550));        // up to the door
            segs.AddRange(Run(3450, 8400));     // between the door and the window
            segs.AddRange(Run(9600, 12000));    // past the window
            return segs;
        }

        [Fact]
        public void Without_a_declared_bridge_every_break_is_the_end_of_a_wall()
        {
            // The default, and it must stay the default: joining is a judgement,
            // and a reading that makes one silently is worse than one that does
            // not make it at all.
            CadInterpretation r = CadInterpretationRules.Interpret(WallWithTwoOpenings(), Set(), Sha);
            Assert.Equal(3, r.Candidates.Count);
        }

        [Fact]
        public void A_declared_bridge_reads_the_run_as_ONE_wall_end_to_end()
        {
            CadInterpretation r = CadInterpretationRules.Interpret(WallWithTwoOpenings(), Set(1500), Sha);
            CadCandidate c = Assert.Single(r.Candidates);
            Assert.Equal(0.0, c.Geometry.Min(p => p.X), 1);
            Assert.Equal(12000.0, c.Geometry.Max(p => p.X), 1);
        }

        [Fact]
        public void And_it_NAMES_every_gap_it_crossed()
        {
            // 900 for the door, 1200 for the window. A reviewer must be able to
            // see what was joined without re-deriving it.
            CadCandidate c = CadInterpretationRules.Interpret(WallWithTwoOpenings(), Set(1500), Sha)
                .Candidates.Single();
            Assert.Contains(c.Assumptions, a => a.Contains("900 mm") && a.Contains("1200 mm"));
            Assert.Contains(c.Assumptions, a => a.Contains("bridge_openings_mm"));
        }

        [Fact]
        public void A_bridge_TOO_NARROW_for_the_opening_leaves_the_wall_in_pieces()
        {
            // 1000 covers the door and not the window. The answer is two walls,
            // not one and not three - the reading does exactly what it was told.
            CadInterpretation r = CadInterpretationRules.Interpret(WallWithTwoOpenings(), Set(1000), Sha);
            Assert.Equal(2, r.Candidates.Count);
        }

        [Fact]
        public void Two_walls_that_are_merely_PARALLEL_are_never_joined()
        {
            // A room apart, same direction, same thickness. Collinearity is what
            // separates a wall from the wall across the corridor.
            var segs = new List<CadSegment>();
            segs.AddRange(Run(0, 5000, 0));
            segs.AddRange(Run(0, 5000, 6000));

            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(9000), Sha);
            Assert.Equal(2, r.Candidates.Count);
        }

        [Fact]
        public void A_wall_of_a_DIFFERENT_THICKNESS_in_line_with_this_one_is_its_own_wall()
        {
            // A 150 partition running on from a 300 wall is two walls, however
            // neatly they meet - and a schedule that says otherwise is wrong.
            var segs = new List<CadSegment>();
            segs.AddRange(Run(0, 5000, 0, 300));
            segs.AddRange(Run(5900, 9000, 0, 150));

            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(1500), Sha);
            Assert.Equal(2, r.Candidates.Count);
        }

        [Fact]
        public void A_gap_WIDER_than_declared_is_left_alone()
        {
            var segs = new List<CadSegment>();
            segs.AddRange(Run(0, 5000));
            segs.AddRange(Run(8000, 12000));   // a 3 m gap

            Assert.Equal(2, CadInterpretationRules.Interpret(segs, Set(1500), Sha).Candidates.Count);
            Assert.Single(CadInterpretationRules.Interpret(segs, Set(3500), Sha).Candidates);
        }

        [Fact]
        public void The_welded_wall_still_answers_to_the_pieces_it_was_made_of()
        {
            // An audit that matched an element to one of the fragments before the
            // bridge was declared must still find it afterwards, or declaring one
            // would look like the building had been redrawn.
            CadInterpretation before = CadInterpretationRules.Interpret(WallWithTwoOpenings(), Set(), Sha);
            CadCandidate after = CadInterpretationRules.Interpret(WallWithTwoOpenings(), Set(1500), Sha)
                .Candidates.Single();

            foreach (CadCandidate piece in before.Candidates)
                Assert.Contains(piece.Id, after.SourceSurrogates);
        }

        [Fact]
        public void bridge_openings_mm_must_be_positive_or_the_set_is_refused()
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.0.0', 'title': 'Walls' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'bridge_openings_mm': 0 } }]
            }".Replace('\'', '"');

            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(JObject.Parse(doc)));
            Assert.Contains("bridge_openings_mm", ex.Message);
        }
    }
}

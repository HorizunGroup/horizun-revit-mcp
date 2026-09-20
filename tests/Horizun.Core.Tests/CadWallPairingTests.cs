// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// ONE WALL, OR TWO?
//
// The double-line reading turns parallel lines into walls, and every way of
// getting it wrong is a defect somebody finds in the model rather than in a
// reply: a wall recognised twice arrives as two coincident walls, and two walls
// merged into one loses a partition somebody has to build.
//
// MEASURED on a real architectural background: ordering candidate pairs by
// THICKNESS chose a pairing whose lines run alongside for 74% of their length,
// consumed one of those lines, and left a 100% pairing of the same thickness
// with nothing to pair with. One wall came out as two, seven millimetres apart.
//
// These cases fix both directions. The positives say what must become one wall;
// the negatives say what must stay two - because a de-duplication that cannot be
// wrong in the second direction is just a merge.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallPairingTests
    {
        private static CadRequirementSet Set(double minThickness = 60, double maxThickness = 400,
                                             double minOverlap = 200)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic - 6\""', 'level': 'Level 1',
                           'height_mm': 2700, 'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': MINT,
                                         'max_thickness_mm': MAXT, 'min_overlap_mm': MINO } } ]
            }";
            doc = doc.Replace("MINT", minThickness.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace("MAXT", maxThickness.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace("MINO", minOverlap.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadSegment Line(double ax, double ay, double bx, double by) =>
            new CadSegment(new CadPoint(ax, ay), new CadPoint(bx, by), "A-WALL", CadCurveKind.Line, 0);

        private static List<CadCandidate> Walls(CadRequirementSet set, params CadSegment[] lines) =>
            CadInterpretationRules.Interpret(lines.ToList(), set, "hash").Candidates
                .Where(c => c.ProposedKind == "wall").ToList();

        [Fact]
        public void Two_faces_of_one_wall_are_one_wall()
        {
            List<CadCandidate> walls = Walls(Set(),
                Line(0, 0, 3000, 0),
                Line(0, 150, 3000, 150));

            Assert.Single(walls);
        }

        /// <summary>
        /// THE CASE THAT PRODUCED THE DUPLICATE. A compound wall exports a line per
        /// material-layer boundary, so one wall arrives as four parallel lines and
        /// several of their pairings are thickness-valid.
        /// </summary>
        [Fact]
        public void A_compound_wall_drawn_as_four_lines_is_still_one_wall()
        {
            List<CadCandidate> walls = Walls(Set(),
                Line(0, 0, 3000, 0),
                Line(0, 30, 3000, 30),
                Line(0, 170, 3000, 170),
                Line(0, 200, 3000, 200));

            Assert.Single(walls);
        }

        /// <summary>
        /// THE ORDERING DEFECT ITSELF, in the shape it was measured in: a partial
        /// pairing of the same thickness as a full one, competing for one line.
        /// Thickness-first took the partial one; the full one must win.
        /// </summary>
        [Fact]
        public void A_fully_matched_pairing_beats_a_partial_one_for_the_same_line()
        {
            // Lines A and B face each other along their whole length. C is a short
            // stub at the same separation from B - a wall meeting it, not facing it.
            List<CadCandidate> walls = Walls(Set(),
                Line(0, 0, 3000, 0),        // A
                Line(0, 150, 3000, 150),    // B
                Line(2200, 300, 3000, 300));// C, same separation from B, 800 long

            CadCandidate wall = Assert.Single(walls);

            // The wall kept is A-B: it runs the whole 3 m, not the 800 mm of the stub.
            double length = wall.Geometry[0].DistanceTo(wall.Geometry[1]);
            Assert.True(length > 2500,
                "the partial pairing won again: the wall is only " + length.ToString("0") + " mm long");
        }

        // ---------------------------------------------------------------------
        // NEGATIVES: what must NOT be merged
        // ---------------------------------------------------------------------

        /// <summary>
        /// Two partitions either side of a corridor. Nothing they contain pairs
        /// across the corridor - the separation is beyond any declared thickness -
        /// and both must survive.
        /// </summary>
        [Fact]
        public void Two_walls_across_a_corridor_stay_two_walls()
        {
            List<CadCandidate> walls = Walls(Set(),
                Line(0, 0, 4000, 0),
                Line(0, 150, 4000, 150),        // wall one
                Line(0, 1200, 4000, 1200),
                Line(0, 1350, 4000, 1350));     // wall two, 1.05 m away

            Assert.Equal(2, walls.Count);
        }

        /// <summary>
        /// TWO THIN WALLS SIDE BY SIDE - a service void between apartments.
        ///
        /// The honest answer here has two halves. With a band that fits the leaves
        /// (60-120 mm) the reading gets it right and both survive. With a LOOSE
        /// band the void between them - 140 mm - is a perfectly good wall as far
        /// as the set is concerned, and no amount of geometry can overrule what
        /// the set declared: the drawing does not say which of the three bands of
        /// parallel lines is material and which is air.
        ///
        /// So the product's answer is the tight band, and the loose band must at
        /// least SAY that it had rivals rather than merging in silence.
        /// </summary>
        [Fact]
        public void Two_thin_walls_with_a_void_survive_a_band_that_fits_them()
        {
            CadRequirementSet set = Set(minThickness: 60, maxThickness: 120);
            List<CadCandidate> walls = Walls(set,
                Line(0, 0, 4000, 0),
                Line(0, 100, 4000, 100),        // 100 mm leaf
                Line(0, 240, 4000, 240),
                Line(0, 340, 4000, 340));       // 100 mm leaf, 140 mm of void between

            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.True(w.ThicknessMm < 120,
                "a leaf came out " + w.ThicknessMm + " mm thick: the void was swallowed"));
        }

        /// <summary>
        /// The same lines under a band that also admits the void. One wall comes
        /// out, and the reading must NAME the pairings it passed over - a merge
        /// nobody can see is the failure; a merge with its rivals listed is a
        /// decision somebody can correct by tightening the band.
        /// </summary>
        [Fact]
        public void A_band_that_admits_the_void_says_what_it_passed_over()
        {
            CadRequirementSet set = Set(minThickness: 60, maxThickness: 200);
            CadInterpretation reading = CadInterpretationRules.Interpret(
                new List<CadSegment>
                {
                    Line(0, 0, 4000, 0),
                    Line(0, 100, 4000, 100),
                    Line(0, 240, 4000, 240),
                    Line(0, 340, 4000, 340)
                }, set, "hash");

            Assert.NotEmpty(reading.DoubleLineReasoning);
            string reasoning = reading.DoubleLineReasoning.ToString();
            Assert.Contains("skipped_line_already_used", reasoning);
            Assert.Contains("mutual_overlap_fraction", reasoning);
        }

        /// <summary>
        /// Collinear neighbours - one wall with a doorway in it, drawn as two
        /// pairs. They are two segments, and merging them would build a wall
        /// through the door.
        /// </summary>
        [Fact]
        public void Collinear_segments_either_side_of_an_opening_stay_separate()
        {
            List<CadCandidate> walls = Walls(Set(),
                Line(0, 0, 1000, 0),
                Line(0, 150, 1000, 150),
                Line(1900, 0, 3000, 0),
                Line(1900, 150, 3000, 150));

            Assert.Equal(2, walls.Count);
        }
    }
}

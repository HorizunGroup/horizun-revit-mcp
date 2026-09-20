// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// ONE WALL, HOWEVER MANY LINES DRAW IT - AND NOT ONE WALL MORE.
//
// MEASURED on one apartment of a real architectural background exported from
// Revit: the line pairing chose sixteen readings for what the drawing shows as
// eight walls. Three of them were one 187 mm wall read at 152, 184 and 187 mm on
// the same centreline; the identity function caught three such collisions and
// let the other five through as distinct walls, which were then built.
//
// The consolidation asks a physical question - do the readings' solids
// intersect? - and these cases pin both directions of it. The positives use the
// drawing's own coordinates. The negatives are the shapes a merge by proximity
// would get wrong: walls face to face closer than the point tolerance, collinear
// segments end to end, two capped walls with a gap between them, a merge that
// would exceed the rule's thickness, and a wall whose width changes along it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallReadingsTests
    {
        private const string Layer = "PLAN-A-WALL";

        private static CadRequirementSet Set(double minThickness = 60, double maxThickness = 400,
                                             double minOverlap = 200, string extent = "")
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1' },
              'source': { 'units': 'millimeter' EXTENT },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['*A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic', 'level': 'Level 1',
                           'height_mm': 2700, 'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': MINT,
                                         'max_thickness_mm': MAXT, 'min_overlap_mm': MINO } } ]
            }";
            doc = doc.Replace("MINT", minThickness.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace("MAXT", maxThickness.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace("MINO", minOverlap.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace("EXTENT", extent)
                     .Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadSegment Line(double ax, double ay, double bx, double by) =>
            new CadSegment(new CadPoint(ax, ay), new CadPoint(bx, by), Layer, CadCurveKind.Line, 0);

        private static CadInterpretation Read(CadRequirementSet set, params CadSegment[] lines) =>
            CadInterpretationRules.Interpret(lines.ToList(), set, "hash");

        private static List<CadCandidate> Walls(CadInterpretation r) =>
            r.Candidates.Where(c => c.ProposedKind == "wall").ToList();

        private static JArray Relations(CadInterpretation r) =>
            (JArray)r.DoubleLineReasoning.Single()["relations"];

        // ---- the drawing's own shapes -----------------------------------------

        /// <summary>
        /// Seven lines of one horizontal wall, as the drawing has them: the board
        /// faces 15.9 mm outside the stud faces, and pieces that stop at different
        /// places. Three disjoint pairs survive the one-line-once selection.
        /// </summary>
        private static CadSegment[] MeasuredThreeReadingWall() => new[]
        {
            Line(33343.8, 18938.9, 34293.2, 18938.9),
            Line(33343.8, 19091.3, 34293.2, 19091.3),
            Line(33359.7, 18921.4, 34275.7, 18921.4),
            Line(33361.3, 19108.7, 34275.7, 19108.7),
            Line(33234.3, 18923.0, 34277.3, 18923.0),
            Line(33235.9, 19107.1, 34277.3, 19107.1),
            // the cap across its left end
            Line(33218.4, 18923.0, 33218.4, 19107.1)
        };

        [Fact]
        public void Three_readings_of_one_measured_wall_are_one_wall_bounded_by_its_outer_faces()
        {
            CadInterpretation r = Read(Set(), MeasuredThreeReadingWall());

            CadCandidate wall = Assert.Single(Walls(r));
            Assert.Equal(187.3, wall.ThicknessMm.Value, 1);
            // It spans every reading, end to end.
            double x0 = wall.Geometry.Min(p => p.X), x1 = wall.Geometry.Max(p => p.X);
            Assert.Equal(33235.9, x0, 1);
            Assert.Equal(34293.2, x1, 1);
            Assert.InRange(wall.Geometry[0].Y, 19015.0, 19015.1);
            // Nothing was dropped: every line it was read from is named.
            Assert.Equal(6, wall.SourceSurrogates.Count(s => s.StartsWith("line:")));
            Assert.Contains(wall.Assumptions, a => a.Contains("3 readings of this wall"));
            Assert.True(wall.EligibleForAutomaticApply, string.Join("; ", wall.IneligibleReasons));

            JObject band = (JObject)((JArray)r.DoubleLineReasoning.Single()["wall_bands"]).Single();
            Assert.Equal(3, (int)band["readings"]);
            Assert.Equal("the caps close this end as one wall", (string)band["ends"]["from_end"]["means"]);
        }

        [Fact]
        public void Readings_that_overlap_only_partly_across_are_one_wall_at_the_outermost_faces()
        {
            // x = 32954.9 .. 33097.8 in the drawing: six vertical lines, and the
            // pairs chosen from them are 108, 127 and 125.4 mm wide, none holding
            // the others. The outermost lines are 142.9 mm apart and the drawing
            // caps the wall's end across exactly that width.
            CadInterpretation r = Read(Set(),
                Line(32954.9, 23036.2, 32954.9, 23749.0),
                Line(32970.8, 23052.1, 32970.8, 23661.7),
                Line(32988.2, 23053.7, 32988.2, 23661.7),
                Line(33080.3, 23053.7, 33080.3, 23661.7),
                Line(33096.2, 23053.7, 33096.2, 23661.7),
                Line(33097.8, 23052.1, 33097.8, 23661.7),
                Line(32954.9, 23036.2, 33097.8, 23036.2));

            CadCandidate wall = Assert.Single(Walls(r));
            Assert.Equal(142.9, wall.ThicknessMm.Value, 1);
            Assert.Equal((32954.9 + 33097.8) / 2, wall.Geometry[0].X, 1);
            Assert.True(wall.EligibleForAutomaticApply, string.Join("; ", wall.IneligibleReasons));
        }

        [Fact]
        public void Layers_that_stop_short_where_a_wall_joins_another_are_still_one_wall()
        {
            // x = 35898.1 .. 36026.7 in the drawing: seven vertical lines whose
            // tops stop at seven different places, because each layer wraps the
            // corner differently. The stud core runs 105 mm past the boards.
            CadInterpretation r = Read(Set(),
                Line(35898.1, 23876.0, 35898.1, 25357.1),
                Line(35914.0, 23877.6, 35914.0, 25373.0),
                Line(35915.6, 23876.0, 35915.6, 25374.6),
                Line(35931.5, 23876.0, 35931.5, 25542.9),
                Line(35995.0, 23876.0, 35995.0, 25479.4),
                Line(36010.8, 23876.0, 36010.8, 25463.5),
                Line(36026.7, 23876.0, 36026.7, 25447.6));

            CadCandidate wall = Assert.Single(Walls(r));
            Assert.True(wall.EligibleForAutomaticApply, string.Join("; ", wall.IneligibleReasons));
            Assert.Equal(128.6, wall.ThicknessMm.Value, 1);
            Assert.Contains(wall.Assumptions, a => a.Contains("stop short of the others"));
            Assert.Contains(wall.Assumptions, a => a.Contains("outer layer"));
        }

        [Fact]
        public void A_face_interrupted_where_another_wall_meets_it_is_still_one_wall()
        {
            // A 200 mm wall whose north layer lines stop for 120 mm where a
            // perpendicular wall arrives from the north; the meeting wall's faces
            // leave the band outwards at both ends of the gap.
            CadInterpretation r = Read(Set(),
                Line(0, 0, 6000, 0),
                Line(0, 40, 6000, 40),
                Line(0, 160, 6000, 160),
                Line(0, 200, 3000, 200), Line(3120, 200, 6000, 200),
                Line(3000, 200, 3000, 2000), Line(3120, 200, 3120, 2000));

            CadCandidate wall = Walls(r).Single(w => w.ThicknessMm > 150);
            Assert.True(wall.EligibleForAutomaticApply, string.Join("; ", wall.IneligibleReasons));
            Assert.Equal(200, wall.ThicknessMm.Value, 1);
            Assert.Contains(wall.Assumptions, a => a.Contains("where another wall meets it"));
        }

        [Fact]
        public void A_niche_is_not_a_join_even_when_it_is_short()
        {
            // The same gap, but its sides run INTO the wall: a recess.
            CadInterpretation r = Read(Set(),
                Line(0, 0, 6000, 0),
                Line(0, 40, 6000, 40),
                Line(0, 160, 6000, 160),
                Line(0, 200, 3000, 200), Line(3120, 200, 6000, 200),
                Line(3000, 100, 3000, 200), Line(3120, 100, 3120, 200));

            Assert.DoesNotContain(Walls(r), w => w.EligibleForAutomaticApply && w.ThicknessMm > 190);
        }

        [Fact]
        public void A_thin_stretch_longer_than_any_wall_is_not_a_join()
        {
            // The same shape with the core running 1.5 m past everything else is
            // a different wall, not a corner.
            CadInterpretation r = Read(Set(),
                Line(0, 0, 0, 3000), Line(150, 0, 150, 3000),
                Line(40, 0, 40, 4500), Line(110, 0, 110, 4500));

            Assert.DoesNotContain(Walls(r), w => w.EligibleForAutomaticApply && w.ThicknessMm > 100);
        }

        [Fact]
        public void A_wall_drawn_the_other_way_round_is_the_same_wall_with_the_same_centreline()
        {
            CadSegment[] forward = { Line(0, 0, 3000, 0), Line(0, 150, 3000, 150) };
            CadSegment[] backward = { Line(3000, 150, 0, 150), Line(3000, 0, 0, 0) };

            CadCandidate a = Assert.Single(Walls(Read(Set(), forward)));
            CadCandidate b = Assert.Single(Walls(Read(Set(), backward)));

            Assert.Equal(a.SemanticId, b.SemanticId);
            Assert.Equal(a.Geometry[0].X, b.Geometry[0].X, 6);
            Assert.Equal(a.Geometry[1].X, b.Geometry[1].X, 6);
            // Normalised: a horizontal wall runs towards +x.
            Assert.True(a.Geometry[1].X > a.Geometry[0].X);
            Assert.Equal(a.SourceSurrogates.Where(s => s.StartsWith("line:")).OrderBy(s => s),
                         b.SourceSurrogates.Where(s => s.StartsWith("line:")).OrderBy(s => s));
        }

        // ---- what must stay two -------------------------------------------------

        [Fact]
        public void Two_walls_face_to_face_closer_than_the_point_tolerance_stay_two()
        {
            // A 10 mm gap is under the 25 mm point tolerance. Proximity would merge
            // them; their solids do not touch, so they are two walls.
            CadInterpretation r = Read(Set(maxThickness: 200),
                Line(0, 0, 4000, 0), Line(0, 150, 4000, 150),
                Line(0, 160, 4000, 160), Line(0, 310, 4000, 310));

            Assert.Equal(2, Walls(r).Count);
            Assert.All(Walls(r), w => Assert.Equal(150, w.ThicknessMm.Value, 1));
            JObject rel = Relations(r).OfType<JObject>().Single(x => (string)x["relation"] == "face_to_face");
            Assert.Equal(10, (double)rel["face_gap_mm"], 1);
        }

        [Fact]
        public void Two_collinear_segments_end_to_end_stay_two_and_are_reported_as_contiguous()
        {
            CadInterpretation r = Read(Set(),
                Line(0, 0, 3000, 0), Line(0, 150, 3000, 150),
                Line(3000, 0, 6000, 0), Line(3000, 150, 6000, 150));

            Assert.Equal(2, Walls(r).Count);
            Assert.Contains(Relations(r).OfType<JObject>(), x => (string)x["relation"] == "contiguous");
        }

        [Fact]
        public void Two_capped_walls_with_a_gap_between_them_are_two_walls_not_one_across_the_gap()
        {
            // 0-150 and 250-400, each capped on its own at both ends. The widest
            // pair spans both walls and the air between them; the caps say so,
            // and the only conflict-free pairing of the four lines is the two walls.
            CadInterpretation r = Read(Set(),
                Line(0, 0, 4000, 0), Line(0, 150, 4000, 150),
                Line(0, 250, 4000, 250), Line(0, 400, 4000, 400),
                Line(0, 0, 0, 150), Line(4000, 0, 4000, 150),
                Line(0, 250, 0, 400), Line(4000, 250, 4000, 400));

            List<CadCandidate> walls = Walls(r);
            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.Equal(150, w.ThicknessMm.Value, 1));
            Assert.All(walls, w => Assert.True(w.EligibleForAutomaticApply, string.Join("; ", w.IneligibleReasons)));
            Assert.Contains(Relations(r).OfType<JObject>(), x => (string)x["relation"] == "re_paired");
            Assert.Contains(Relations(r).OfType<JObject>(),
                x => (string)x["relation"] == "face_to_face" && System.Math.Abs((double)x["face_gap_mm"] - 100) < 0.1);
        }

        [Fact]
        public void Without_caps_the_same_four_lines_are_still_paired_without_a_wall_across_the_gap()
        {
            // No end caps, and the rule allows at most 300 mm: the two 250 mm
            // readings collide, and pairing the lines again gives two walls that
            // explain every line with nothing in the same space.
            CadInterpretation r = Read(Set(maxThickness: 300),
                Line(0, 0, 4000, 0), Line(0, 250, 4000, 250),
                Line(0, 150, 4000, 150), Line(0, 400, 4000, 400));

            List<CadCandidate> walls = Walls(r);
            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.Equal(150, w.ThicknessMm.Value, 1));
        }

        [Fact]
        public void A_collision_no_single_re_pairing_resolves_goes_to_review_whole()
        {
            // The rule only admits 200-300 mm, so the lines pair only as two
            // 250 mm readings that overlap by 100 mm. Together they would be 400
            // mm; apart, each re-pairing explains the same and neither is better.
            CadInterpretation r = Read(Set(minThickness: 200, maxThickness: 300),
                Line(0, 0, 4000, 0), Line(0, 250, 4000, 250),
                Line(0, 150, 4000, 150), Line(0, 400, 4000, 400));

            List<CadCandidate> walls = Walls(r);
            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.False(w.EligibleForAutomaticApply));
            Assert.All(walls, w => Assert.Contains(w.IneligibleReasons, x => x.Contains("at most 300 mm")));
            Assert.Contains(Relations(r).OfType<JObject>(), x => (string)x["relation"] == "overlap_not_merged");
        }

        [Fact]
        public void A_wall_whose_width_changes_along_it_is_not_merged_into_one_width()
        {
            // A 150 mm wall 6 m long with a 300 mm thickening for 1 m of it.
            CadInterpretation r = Read(Set(),
                Line(0, 0, 6000, 0), Line(0, 150, 6000, 150),
                Line(2000, -75, 3000, -75), Line(2000, 225, 3000, 225));

            List<CadCandidate> walls = Walls(r);
            Assert.DoesNotContain(walls, w => w.EligibleForAutomaticApply && w.ThicknessMm > 200);
            Assert.Contains(walls, w => w.IneligibleReasons.Any(x => x.Contains("one straight wall of one width")));
        }

        [Fact]
        public void Perpendicular_walls_meeting_at_a_corner_are_unrelated()
        {
            CadInterpretation r = Read(Set(),
                Line(0, 0, 3000, 0), Line(0, 150, 3000, 150),
                Line(0, 0, 0, 3000), Line(150, 150, 150, 3000));

            Assert.Equal(2, Walls(r).Count);
            Assert.Empty(Relations(r));
        }

        // ---- the zone's edge ----------------------------------------------------

        private const string Zone =
            ", 'extent_mm': { 'min_x': 1000, 'min_y': -1000, 'max_x': 5000, 'max_y': 1000 CROSSING }";

        [Fact]
        public void A_wall_that_runs_past_the_zone_is_excluded_by_default()
        {
            CadInterpretation r = Read(Set(extent: Zone.Replace("CROSSING", "")),
                Line(0, 0, 6000, 0), Line(0, 150, 6000, 150));

            Assert.Empty(Walls(r));
            Assert.Equal(2, r.SegmentsCrossingExtent);
            Assert.Equal(0, r.SegmentsCrossingKept);
        }

        [Fact]
        public void Under_crossing_whole_the_wall_is_read_entire_and_never_clipped()
        {
            CadInterpretation r = Read(Set(extent: Zone.Replace("CROSSING", ", 'crossing': 'whole'")),
                Line(0, 0, 6000, 0), Line(0, 150, 6000, 150),
                Line(20000, 0, 26000, 0));

            CadCandidate wall = Assert.Single(Walls(r));
            Assert.Equal(0, wall.Geometry.Min(p => p.X), 3);
            Assert.Equal(6000, wall.Geometry.Max(p => p.X), 3);
            Assert.Equal(2, r.SegmentsCrossingKept);
            Assert.Equal(1, r.SegmentsOutsideExtent);
        }

        [Fact]
        public void A_line_passing_through_the_zone_with_both_ends_outside_crosses_it()
        {
            CadInterpretation r = Read(Set(extent: Zone.Replace("CROSSING", "")),
                Line(0, 500, 6000, 500));

            Assert.Equal(1, r.SegmentsCrossingExtent);
            Assert.Equal(0, r.SegmentsOutsideExtent);
        }

        [Fact]
        public void An_unknown_crossing_policy_is_refused_whole()
        {
            var ex = Assert.Throws<CadRequirementSetException>(
                () => Set(extent: Zone.Replace("CROSSING", ", 'crossing': 'clip'")));
            Assert.Contains("crossing", ex.Message);
        }
    }
}

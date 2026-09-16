// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// ONE FACE LINE, MANY PIECES OPPOSITE IT.
//
// MEASURED on a second apartment of the drawing scenario A was built from, with
// scenario A's rules unchanged: a corridor wall's outer face is one line 13 m
// long while its inner face is cut wherever a wall meets it, and two more walls
// are drawn the same way with the cut on either face. The selection took each
// line once, so each of those walls was read for one piece and the rest of it was
// left out - 7 m of wall missing from a plan that reported nothing wrong.
//
// A used line may now be paired again only to continue the SAME wall, and two
// readings that share a face line are one wall only when the join between them
// holds. The negatives pin what must not follow from that: a line's other side,
// a stretch already read, another thickness, and a gap nothing explains.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallContinuationTests
    {
        private const string Layer = "PLAN-A-WALL";

        private static CadRequirementSet Set(double maxThickness = 400)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5,
                              'wall_overlap_mm': 1 },
              'rules': [ { 'id': 'r-wall', 'layers': ['*A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic', 'level': 'Level 1',
                           'height_mm': 2700, 'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60,
                                         'max_thickness_mm': MAXT, 'min_overlap_mm': 200 } } ]
            }";
            doc = doc.Replace("MAXT", maxThickness.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadSegment Line(double ax, double ay, double bx, double by) =>
            new CadSegment(new CadPoint(ax, ay), new CadPoint(bx, by), Layer, CadCurveKind.Line, 0);

        private static CadInterpretation Read(CadRequirementSet set, params CadSegment[] lines) =>
            CadInterpretationRules.Interpret(lines.ToList(), set, "hash");

        private static List<CadCandidate> Walls(CadInterpretation r) =>
            r.Candidates.Where(c => c.ProposedKind == "wall").ToList();

        private static List<JObject> Relations(CadInterpretation r) =>
            ((JArray)r.DoubleLineReasoning.Single()["relations"]).OfType<JObject>().ToList();

        private static List<JObject> Pairs(CadInterpretation r) =>
            ((JArray)r.DoubleLineReasoning.Single()["pairs"]).OfType<JObject>().ToList();

        private static bool Vertical(CadCandidate w) =>
            System.Math.Abs(w.Geometry[0].X - w.Geometry[1].X) < 1;

        // The corridor wall of the second apartment, in the drawing's millimetres:
        // the outer face (x = 38709.6) is one line; the inner face (x = 38836.6) is
        // three pieces, and a 127 mm wall arrives from the east between the second
        // and the third.
        private static readonly CadSegment OuterFace = Line(38709.6, 33112.1, 38709.6, 20031.1);
        private static readonly CadSegment[] InnerFace =
        {
            Line(38836.6, 23315.6, 38836.6, 24936.4),
            Line(38836.6, 24936.4, 38836.6, 26508.1),
            Line(38836.6, 26635.1, 38836.6, 28206.7)
        };
        private static readonly CadSegment[] MeetingWall =
        {
            Line(38836.6, 26508.1, 39817.7, 26508.1),
            Line(39817.7, 26635.1, 38836.6, 26635.1)
        };

        [Fact]
        public void A_long_face_opposite_a_face_cut_by_a_meeting_wall_is_one_wall_across_the_join()
        {
            CadInterpretation r = Read(Set(), new[] { OuterFace }.Concat(InnerFace).Concat(MeetingWall).ToArray());

            CadCandidate corridor = Assert.Single(Walls(r), Vertical);
            Assert.True(corridor.EligibleForAutomaticApply, string.Join("; ", corridor.IneligibleReasons));
            Assert.Equal(127, corridor.ThicknessMm.Value, 1);
            Assert.Equal(23315.6, corridor.Geometry.Min(p => p.Y), 1);
            Assert.Equal(28206.7, corridor.Geometry.Max(p => p.Y), 1);
            Assert.Contains(corridor.Assumptions, a => a.Contains("where another wall meets it"));
            // Every piece it was read from is named.
            Assert.Equal(4, corridor.SourceSurrogates.Count(s => s.StartsWith("line:")));

            Assert.Contains(Pairs(r), p => p["continues_lines"] != null);
            Assert.Contains(Relations(r), x => (string)x["relation"] == "joined_along_a_shared_face");
            // The meeting wall is its own wall.
            Assert.Single(Walls(r), w => !Vertical(w));
        }

        [Fact]
        public void The_same_cut_face_with_nothing_meeting_it_is_two_walls_not_one_across_the_gap()
        {
            CadInterpretation r = Read(Set(), new[] { OuterFace }.Concat(InnerFace).ToArray());

            List<CadCandidate> walls = Walls(r);
            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.True(w.EligibleForAutomaticApply, string.Join("; ", w.IneligibleReasons)));
            // The pieces that meet end to end are one; the gap splits the rest.
            Assert.Contains(walls, w => System.Math.Abs(w.Geometry.Max(p => p.Y) - 26508.1) < 0.1);
            Assert.Contains(walls, w => System.Math.Abs(w.Geometry.Min(p => p.Y) - 26635.1) < 0.1);
            JObject refused = Assert.Single(Relations(r), x => (string)x["relation"] == "shared_face_not_joined");
            Assert.Contains("only one face is drawn", (string)refused["reason"]);
            Assert.Equal(127, (double)refused["gap_mm"], 1);
        }

        [Fact]
        public void A_measured_wall_whose_one_face_is_cut_where_the_other_is_not_is_read_whole()
        {
            // y = 16508 .. 16670: three lines along the whole south face, and the
            // north face in two runs that meet at x = 44860 .. 44870.
            CadInterpretation r = Read(Set(),
                Line(46683.6, 16508.4, 42892.7, 16508.4),
                Line(46685.2, 16525.9, 42876.8, 16525.9),
                Line(42876.8, 16524.3, 46683.6, 16524.3),
                Line(44850.1, 16670.3, 42873.6, 16670.3),
                Line(42872.0, 16652.9, 44860.1, 16652.9),
                Line(44859.2, 16654.5, 42873.6, 16654.5),
                Line(46685.2, 16652.9, 44870.0, 16652.9),
                Line(46683.6, 16654.5, 44870.0, 16654.5),
                Line(46667.7, 16670.3, 44870.0, 16670.3));

            CadCandidate wall = Assert.Single(Walls(r));
            Assert.True(wall.EligibleForAutomaticApply, string.Join("; ", wall.IneligibleReasons));
            Assert.InRange(wall.Geometry.Min(p => p.X), 42870.0, 42880.0);
            Assert.InRange(wall.Geometry.Max(p => p.X), 46680.0, 46690.0);
            Assert.Equal(161.9, wall.ThicknessMm.Value, 1);
        }

        [Fact]
        public void A_face_line_is_never_continued_on_its_other_side()
        {
            // Two 150 mm walls 110 mm apart; the lower wall's upper face could pair
            // with the upper wall's lower face as a 110 mm wall of air.
            CadInterpretation r = Read(Set(maxThickness: 300),
                Line(0, 0, 4000, 0), Line(0, 150, 4000, 150),
                Line(0, 260, 2000, 260), Line(0, 410, 2000, 410));

            List<CadCandidate> walls = Walls(r);
            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.Equal(150, w.ThicknessMm.Value, 1));
            Assert.Contains(Pairs(r), p => (string)p["not_a_continuation"] == CadLineUse.OtherSide);
            // Nor over a stretch another reading of the line already covers.
            Assert.Contains(Pairs(r), p => (string)p["not_a_continuation"] == CadLineUse.StretchAlreadyRead);
            Assert.DoesNotContain(Pairs(r), p => (string)p["outcome"] == "chosen" && p["continues_lines"] != null);
        }

        [Fact]
        public void A_used_line_is_not_continued_by_a_wall_of_another_thickness()
        {
            // One long face; a 127 mm wall against its first half, and a line 300 mm
            // away along its second half - another wall's face, or furniture, but
            // not this wall going on.
            CadInterpretation r = Read(Set(),
                Line(0, 0, 6000, 0),
                Line(0, 127, 3000, 127),
                Line(3500, 300, 6000, 300));

            CadCandidate wall = Assert.Single(Walls(r));
            Assert.Equal(127, wall.ThicknessMm.Value, 1);
            Assert.Equal(3000, wall.Geometry.Max(p => p.X), 1);
            Assert.Contains(Pairs(r), p => (string)p["not_a_continuation"] == CadLineUse.OtherThickness);
        }

        [Fact]
        public void A_gap_longer_than_a_wall_can_be_thick_is_not_a_join_even_with_walls_on_both_sides()
        {
            // A 500 mm box against the corridor: its walls leave the inner face at
            // both ends, but 500 mm is more than any wall this rule reads.
            CadInterpretation r = Read(Set(),
                Line(0, 0, 0, 6000),
                Line(127, 0, 127, 2500), Line(127, 3000, 127, 6000),
                Line(127, 2500, 900, 2500), Line(127, 3000, 900, 3000));

            List<CadCandidate> walls = Walls(r).Where(Vertical).ToList();
            Assert.Equal(2, walls.Count);
            Assert.Contains(Relations(r), x => (string)x["relation"] == "shared_face_not_joined");
        }

        [Fact]
        public void The_continuation_test_measures_side_stretch_and_thickness()
        {
            var segments = new List<CadSegment> { Line(0, 0, 6000, 0), Line(0, 127, 3000, 127), Line(3000, 127, 6000, 127),
                                                  Line(0, -127, 6000, -127), Line(3000, 330, 6000, 330) };
            CadLineUse first = CadLineUse.Of(segments, 0, 1, new CadDoubleLine(new CadPoint(0, 63.5), new CadPoint(3000, 63.5),
                127, Layer, 3000, 1, 0, 0, 1, 6000));
            CadLineUse next = CadLineUse.Of(segments, 0, 2, new CadDoubleLine(new CadPoint(3000, 63.5), new CadPoint(6000, 63.5),
                127, Layer, 3000, 1, 0, 0, 2, 6000));
            CadLineUse below = CadLineUse.Of(segments, 0, 3, new CadDoubleLine(new CadPoint(0, -63.5), new CadPoint(6000, -63.5),
                127, Layer, 6000, 1, 0, 0, 3, 6000));
            CadLineUse thick = CadLineUse.Of(segments, 0, 4, new CadDoubleLine(new CadPoint(3000, 165), new CadPoint(6000, 165),
                330, Layer, 3000, 1, 0, 0, 4, 6000));

            Assert.Equal(1, first.Side);
            Assert.Equal(-1, below.Side);
            Assert.Null(next.RefusalToContinue(new[] { first }, 25));
            Assert.Equal(CadLineUse.OtherSide, below.RefusalToContinue(new[] { first }, 25));
            Assert.Equal(CadLineUse.StretchAlreadyRead, first.RefusalToContinue(new[] { next, first }, 25));
            Assert.Equal(CadLineUse.OtherThickness, thick.RefusalToContinue(new[] { first }, 25));
            Assert.Null(first.RefusalToContinue(new CadLineUse[0], 25));
        }

        // ---- a wall is not planned where a wall stands --------------------------

        [Fact]
        public void A_planned_wall_over_a_standing_one_occupies_its_space()
        {
            // MEASURED: the joined reading 29159.2 .. 32970.8 over the piece built
            // as 29159.2 .. 32686.1 under the earlier reading, both 152.4 mm wide.
            double across, along;
            Assert.True(CadWallReadings.SolidsIntersect(
                new CadPoint(29159.2, 23812.5), new CadPoint(32970.8, 23812.5), 152.4,
                new CadPoint(29159.2, 23812.5), new CadPoint(32686.1, 23812.5), 152.4,
                2, 25, 1, out across, out along));
            Assert.Equal(152.4, across, 1);
            Assert.Equal(3526.9, along, 1);
            // Drawn the other way round, it is the same space.
            Assert.True(CadWallReadings.SolidsIntersect(
                new CadPoint(32970.8, 23812.5), new CadPoint(29159.2, 23812.5), 152.4,
                new CadPoint(29159.2, 23812.5), new CadPoint(32686.1, 23812.5), 152.4,
                2, 25, 1, out across, out along));
        }

        [Fact]
        public void Walls_end_to_end_face_to_face_or_crossing_do_not_occupy_each_others_space()
        {
            double across, along;
            // end to end, 6 mm apart
            Assert.False(CadWallReadings.SolidsIntersect(
                new CadPoint(0, 0), new CadPoint(3000, 0), 150, new CadPoint(3006, 0), new CadPoint(6000, 0), 150,
                2, 25, 1, out across, out along));
            // face to face, 10 mm apart
            Assert.False(CadWallReadings.SolidsIntersect(
                new CadPoint(0, 0), new CadPoint(3000, 0), 150, new CadPoint(0, 160), new CadPoint(3000, 160), 150,
                2, 25, 1, out across, out along));
            // a wall meeting it at a corner
            Assert.False(CadWallReadings.SolidsIntersect(
                new CadPoint(0, 0), new CadPoint(3000, 0), 150, new CadPoint(0, 0), new CadPoint(0, 3000), 150,
                2, 25, 1, out across, out along));
            // faces touching within the overlap tolerance
            Assert.False(CadWallReadings.SolidsIntersect(
                new CadPoint(0, 0), new CadPoint(3000, 0), 150, new CadPoint(0, 149.5), new CadPoint(3000, 149.5), 150,
                2, 25, 1, out across, out along));
        }

        [Fact]
        public void Both_routes_withdraw_a_wall_whose_space_is_occupied()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "src", "Horizun.Revit",
                "Commands", "PlanFromCadCommand.cs"));
            // The conversion's own path and ResolveRows, which the update planner uses.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src,
                System.Text.RegularExpressions.Regex.Escape("WithdrawOccupied(doc, creates, set, withdrawn);")).Count);
            Assert.Contains("CadWallReadings.SolidsIntersect(", src);
            Assert.Contains("\"space_already_occupied_by_a_built_wall\"", src);
            // And a reshape the update would make is held when it lands in a standing wall.
            string update = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "src", "Horizun.Revit",
                "Commands", "PlanCadUpdateCommand.cs"));
            Assert.Contains("CadWallReadings.SolidsIntersect(a.Geometry[0], a.Geometry[a.Geometry.Count - 1],", update);
            Assert.Contains("HELD: the new line would put this wall in the space of", update);
            // A withdrawn wall is not counted as a symbol that found no host.
            Assert.Contains("(string)w[\"kind\"] != \"wall\"", src);
        }
    }
}

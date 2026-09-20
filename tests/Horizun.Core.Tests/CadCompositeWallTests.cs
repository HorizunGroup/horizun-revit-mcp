// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// ONE BAND, SEVERAL LEAVES: WHAT THE DRAWING SHOWS, AND WHAT A SET MAKES OF IT.
//
// MEASURED on unit 914 of the E-300 drawing: the wall between a bedroom and the
// demising core is drawn as a 15 mm board (one hatch), a 152 mm stud leaf (a
// second hatch) and a 152 mm concrete leaf (a third), bounded by four lines. The
// line between the stud and the concrete is the face of both. Read with each
// line once, the reading either built the stud leaf alone or - once the far face
// was read - sent the lining and the whole band to review, so neither was built.
//
// Leaves are told apart by hatch ENTITY, never by pattern name, and a boundary
// between two leaves counts only where a line is drawn along it. These cases pin
// the three policies (outer, leaves, composite), a cavity inside a composition,
// and two hatch pieces with no line between them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadCompositeWallTests
    {
        private static CadRequirementSet Set(string policy)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'w', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic', 'level': 'Level 1', 'height_mm': 2700,
                           'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60, 'max_thickness_mm': 400,
                                         'solid_hatch_layers': ['A-WALL-PATT'] POLICY } } ]
            }";
            string p = policy == null ? "" : ", 'composite': '" + policy + "'";
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("POLICY", p).Replace('\'', '"')));
        }

        private static CadSegment Line(double y, double x0 = 0, double x1 = 3000) =>
            new CadSegment(new CadPoint(x0, y), new CadPoint(x1, y), "A-WALL", CadCurveKind.Line, 0);

        private static CadIrEntity Hatch(string pattern, double y0, double y1, double x0 = 0, double x1 = 3000) =>
            new CadIrEntity
            {
                Id = "h" + Guid.NewGuid().ToString("N").Substring(0, 6), Handle = "H" + y0, Layer = "A-WALL-PATT",
                HatchPattern = pattern, Space = "model",
                HatchLoops = new List<List<CadPoint>>
                {
                    new List<CadPoint> { new CadPoint(x0, y0), new CadPoint(x1, y0), new CadPoint(x1, y1), new CadPoint(x0, y1) }
                }
            };

        private static CadSolidHatch Solid(params CadIrEntity[] hatches) =>
            CadSolidHatch.Build(hatches.ToList(), null, new[] { "A-WALL-PATT" }, p => p);

        // board 0-15.5, stud 17.5-170, concrete 170-322.3 (lines at 0, 17.5, 170, 322.3)
        private static List<CadSegment> LiningAndCore() => new List<CadSegment>
        {
            Line(0), Line(17.5), Line(170), Line(322.3)
        };

        private static CadSolidHatch LiningAndCoreHatch() => Solid(
            Hatch("FP_3", 0, 15.5), Hatch("FP_4", 17.5, 170), Hatch("FP_2", 170, 322.3));

        private static List<double[]> Walls(CadInterpretation r) =>
            r.Candidates.Where(c => c.ProposedKind == "wall")
             .Select(c => new[] { Math.Round((c.Geometry[0].Y + c.Geometry[1].Y) / 2, 1), Math.Round(c.ThicknessMm ?? 0, 1) })
             .OrderBy(w => w[0]).ToList();

        private static CadInterpretation Read(string policy, List<CadSegment> lines, CadSolidHatch solid) =>
            CadInterpretationRules.Interpret(lines, Set(policy), "sha", null, null, solid);

        [Fact]
        public void The_composition_is_observed_by_hatch_entity_and_the_board_joins_its_stud()
        {
            CadSolidHatch solid = LiningAndCoreHatch();
            var pair = new CadDoubleLine(new CadPoint(0, 161.15), new CadPoint(3000, 161.15), 322.3, "A-WALL",
                                         3000, 1, 0, 0, 3);
            CadComposition c = solid.Compose(pair, new[] { "A-WALL-PATT" }, false, LiningAndCore(), 60, 6, 2);
            Assert.True(c.IsComposite);
            List<CadLeaf> leaves = c.Leaves.ToList();
            Assert.Equal(2, leaves.Count);
            Assert.Equal(170, leaves[0].ThicknessMm, 0);     // board merged into the stud leaf
            Assert.Equal(1, leaves[0].Finishes);
            Assert.Equal(152.3, leaves[1].ThicknessMm, 0);
            Assert.Equal(2, leaves[0].LineAtHi);              // line 2 (y 170) bounds both
            Assert.Equal(new[] { 2 }, c.SharedBoundaryLines.ToArray());
            Assert.Equal("observed", ((JObject)c.ToJson()).Properties().Select(p => p.Name).First(n => n == "observed"));
        }

        [Fact]
        public void Leaves_policy_builds_the_lining_and_the_core_as_two_walls()
        {
            CadInterpretation r = Read("leaves", LiningAndCore(), LiningAndCoreHatch());
            List<double[]> walls = Walls(r);
            Assert.Equal(2, walls.Count);
            Assert.Equal(85, walls[0][0], 0);        // centre of 0..170
            Assert.Equal(170, walls[0][1], 0);
            Assert.Equal(246.2, walls[1][0], 0);     // centre of 170..322.3
            Assert.Equal(152.3, walls[1][1], 0);
            Assert.All(r.Candidates, c => Assert.True(c.EligibleForAutomaticApply, string.Join(" | ", c.Assumptions)));
            var pairs = (JArray)r.DoubleLineReasoning.Single()["pairs"];
            Assert.Contains(pairs, p => (string)p["outcome"] == "skipped_composite_read_as_leaves");
        }

        [Fact]
        public void Composite_policy_builds_one_wall_of_the_outer_faces_and_names_its_leaves()
        {
            CadInterpretation r = Read("composite", LiningAndCore(), LiningAndCoreHatch());
            List<double[]> walls = Walls(r);
            Assert.Single(walls);
            Assert.Equal(322.3, walls[0][1], 0);
            CadCandidate wall = r.Candidates.Single();
            Assert.Contains(wall.Assumptions, a => a.Contains("2 separate leaves"));
            var band = (JObject)((JArray)r.DoubleLineReasoning.Single()["wall_bands"]).Single();
            Assert.True((bool)band["composition"]["composite"]);
        }

        [Fact]
        public void The_default_is_the_reading_it_always_was()
        {
            CadInterpretation outer = Read(null, LiningAndCore(), LiningAndCoreHatch());
            CadInterpretation same = Read("outer", LiningAndCore(), LiningAndCoreHatch());
            Assert.Equal(Walls(outer).Select(w => w[0] + ":" + w[1]), Walls(same).Select(w => w[0] + ":" + w[1]));
            Assert.DoesNotContain((JArray)outer.DoubleLineReasoning.Single()["pairs"],
                                  p => p["composition"] != null);
        }

        // two 100 mm leaves with a 50 mm unhatched cavity: lines at 0, 100, 150, 250
        private static List<CadSegment> Cavity() => new List<CadSegment> { Line(0), Line(100), Line(150), Line(250) };
        private static CadSolidHatch CavityHatch() => Solid(Hatch("FP_2", 0, 100), Hatch("FP_2", 150, 250));

        [Fact]
        public void A_cavity_inside_a_composition_is_one_wall_under_composite_and_two_leaves_otherwise()
        {
            List<double[]> composite = Walls(Read("composite", Cavity(), CavityHatch()));
            Assert.Single(composite);
            Assert.Equal(250, composite[0][1], 0);

            List<double[]> leaves = Walls(Read("leaves", Cavity(), CavityHatch()));
            Assert.Equal(new[] { 50.0, 200.0 }, leaves.Select(w => w[0]).ToArray());

            // outer keeps the old answer: the band with a gap is not a wall, its leaves are
            List<double[]> outer = Walls(Read("outer", Cavity(), CavityHatch()));
            Assert.Equal(new[] { 50.0, 200.0 }, outer.Select(w => w[0]).ToArray());
        }

        [Fact]
        public void A_composite_whose_leaves_cannot_be_read_is_read_whole_and_held()
        {
            // MEASURED (revision C): a face moved out with only its board's hatch; an
            // unhatched strip was left inside, no leaf pair was solid, and under
            // "leaves" the wall vanished from the reading.
            var lines = new List<CadSegment> { Line(0), Line(15.9), Line(141.3) };
            CadSolidHatch solid = Solid(Hatch("FP_3", 0, 15.9), Hatch("FP_4", 46.8, 141.3));
            CadInterpretation r = Read("leaves", lines, solid);
            List<double[]> walls = Walls(r);
            Assert.Single(walls);
            Assert.Equal(141.3, walls[0][1], 0);
            CadCandidate wall = r.Candidates.Single();
            Assert.False(wall.EligibleForAutomaticApply);
            Assert.Contains(wall.IneligibleReasons, x => x.Contains("policy 'leaves' cannot be applied"));
            var pairs = (JArray)r.DoubleLineReasoning.Single()["pairs"];
            Assert.Contains(pairs, p => p["leaves_unreadable"] != null && (string)p["outcome"] == "chosen");
            Assert.DoesNotContain(pairs, p => (string)p["outcome"] == "skipped_composite_read_as_leaves");
        }

        [Fact]
        public void Two_hatch_pieces_with_no_line_between_them_are_one_leaf()
        {
            // same 170 mm band, hatched in two pieces split at 80 mm, no line drawn at 80
            CadSolidHatch solid = Solid(Hatch("FP_4", 0, 80), Hatch("FP_4", 80, 170));
            var lines = new List<CadSegment> { Line(0), Line(170) };
            var pair = new CadDoubleLine(new CadPoint(0, 85), new CadPoint(3000, 85), 170, "A-WALL", 3000, 1, 0, 0, 1);
            CadComposition c = solid.Compose(pair, new[] { "A-WALL-PATT" }, false, lines, 60, 6, 2);
            Assert.False(c.IsComposite);
            Assert.Single(Walls(Read("leaves", lines, solid)));
        }

        [Fact]
        public void The_policy_is_validated()
        {
            Assert.Throws<CadRequirementSetException>(() => Set("layers"));
            string noSolid = @"{ 'schema': 'horizun.cad-requirements/1', 'requirement_set': { 'id': 'w', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r', 'layers': ['A-WALL'], 'produces': 'wall', 'family_type': 'Basic Wall: G',
                'level': 'Level 1', 'height_mm': 2700,
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60, 'max_thickness_mm': 400,
                              'composite': 'leaves' } } ] }";
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(JObject.Parse(noSolid.Replace('\'', '"'))));
            Assert.Equal(CadCompositePolicy.Leaves, Set("leaves").Rules.Single().Geometry.Composite);
            Assert.Equal(CadCompositePolicy.Outer, Set(null).Rules.Single().Geometry.Composite);
        }
    }
}

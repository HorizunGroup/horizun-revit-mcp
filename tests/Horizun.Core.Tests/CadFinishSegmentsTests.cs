// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// FINISH DRAWN IN STRETCHES IS MODELLED IN STRETCHES.
//
// MEASURED (E-300 units 915F and 914): unhatched finish lines run along part of a
// wall. One width for the whole wall put seven devices 15.8 mm proud of the face
// drawn at them and two 33.3 mm behind it. These cases use a 130 mm wall with a
// 15.8 mm finish on one face that stops for 500 mm, a finish on the other face
// broken for 20 mm where a symbol was drawn over it, and a 33.4 mm double board
// along part of a wall.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadFinishSegmentsTests
    {
        private static CadRequirementSet Set(string finish)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'w', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic', 'level': 'Level 1', 'height_mm': 2700,
                           'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60, 'max_thickness_mm': 400 FINISH } } ]
            }";
            string f = finish == null ? "" : ", 'finish': '" + finish + "'";
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("FINISH", f).Replace('\'', '"')));
        }

        private static CadSegment Line(double y, double x0, double x1) =>
            new CadSegment(new CadPoint(x0, y), new CadPoint(x1, y), "A-WALL", CadCurveKind.Line, 0);

        // core 0..130; north finish at 145.8 over 0..1500 and 2000..3000; south finish at -15.8 broken at 990..1010
        private static List<CadSegment> Wall() => new List<CadSegment>
        {
            Line(0, 0, 3000), Line(130, 0, 3000),
            Line(145.8, 0, 1500), Line(145.8, 2000, 3000),
            Line(-15.8, 0, 990), Line(-15.8, 1010, 3000)
        };

        private static List<double[]> Pieces(CadInterpretation r) =>
            r.Candidates.Where(c => c.ProposedKind == "wall")
             .Select(c => new[]
             {
                 Math.Round(Math.Min(c.Geometry[0].X, c.Geometry[1].X)), Math.Round(Math.Max(c.Geometry[0].X, c.Geometry[1].X)),
                 Math.Round(c.Geometry[0].Y, 1), Math.Round(c.ThicknessMm ?? 0, 1)
             })
             .OrderBy(p => p[0]).ToList();

        [Fact]
        public void Follow_cuts_the_wall_where_a_finish_stops_and_ignores_a_symbol_break()
        {
            List<double[]> p = Pieces(CadInterpretationRules.Interpret(Wall(), Set("follow"), "sha"));
            Assert.Equal(3, p.Count);
            Assert.Equal(new double[] { 0, 1500, 65, 161.6 }, p[0]);      // both finishes: -15.8 .. 145.8
            Assert.Equal(new double[] { 1500, 2000, 57.1, 145.8 }, p[1]); // south finish only: -15.8 .. 130
            Assert.Equal(new double[] { 2000, 3000, 65, 161.6 }, p[2]);
        }

        [Fact]
        public void Widen_is_what_it_was_one_width_for_the_whole_wall()
        {
            List<double[]> p = Pieces(CadInterpretationRules.Interpret(Wall(), Set(null), "sha"));
            Assert.Single(p);
            Assert.Equal(161.6, p[0][3], 1);
        }

        [Fact]
        public void A_double_board_along_part_of_a_wall_is_carried_by_that_part_only()
        {
            // 152.4 stud leaf 0..152.4; two boards to -33.4 over x 0..930 only
            var lines = new List<CadSegment>
            {
                Line(0, 0, 2032), Line(152.4, 0, 2032),
                Line(-15.9, 0, 930), Line(-17.5, 0, 930), Line(-33.4, 0, 930)
            };
            List<double[]> p = Pieces(CadInterpretationRules.Interpret(lines, Set("follow"), "sha"));
            Assert.Equal(2, p.Count);
            Assert.Equal(185.8, p[0][3], 1);
            Assert.Equal(930, p[0][1], 0);
            Assert.Equal(152.4, p[1][3], 1);
        }

        [Fact]
        public void A_core_face_is_never_looked_for_deeper_than_a_finish()
        {
            // a 158.8 mm band whose outer faces cover only part of it, with layer lines at
            // 125 mm and 142 mm inside: the core must not collapse onto those layers
            var lines = new List<CadSegment>
            {
                Line(0, 0, 500), Line(0, 1500, 2000), Line(158.8, 0, 2000),
                Line(125.4, 0, 2000), Line(142.9, 0, 2000)
            };
            List<double[]> p = Pieces(CadInterpretationRules.Interpret(lines, Set("follow"), "sha"));
            Assert.All(p, x => Assert.True(x[3] >= 100, string.Join(",", x)));
        }

        [Fact]
        public void The_key_is_validated()
        {
            Assert.Throws<CadRequirementSetException>(() => Set("partial"));
            Assert.Equal("follow", Set("follow").Rules.Single().Geometry.Finish);
            Assert.Equal("widen", Set(null).Rules.Single().Geometry.Finish);
        }
    }
}

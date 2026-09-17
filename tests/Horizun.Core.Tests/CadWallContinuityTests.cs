// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A WALL DOES NOT END WHERE ONE OF ITS FACES IS BROKEN.
//
// MEASURED (E-300): where a partition meets a wall the drawing breaks that face for
// 10 or 70 mm while the other face runs through; the wall was read as ending at the
// break and five devices beyond it had no host. These cases pin the bridge, the
// merge of two collinear readings across it, and the cross junction, where BOTH
// faces stop and nothing may be bridged.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallContinuityTests
    {
        private static CadRequirementSet Set(double? breaks)
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
                                         'min_overlap_mm': 200 BREAKS } } ]
            }";
            string b = breaks == null ? "" : ", 'face_breaks_mm': " + breaks.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("BREAKS", b).Replace('\'', '"')));
        }

        private static CadSegment H(double y, double x0, double x1) =>
            new CadSegment(new CadPoint(x0, y), new CadPoint(x1, y), "A-WALL", CadCurveKind.Line, 0);

        private static List<double[]> Walls(IList<CadSegment> lines, double? breaks) =>
            CadInterpretationRules.Interpret(lines.ToList(), Set(breaks), "sha").Candidates
                .Where(c => c.ProposedKind == "wall")
                .Select(c => new[] { Math.Round(Math.Min(c.Geometry[0].X, c.Geometry[1].X)),
                                     Math.Round(Math.Max(c.Geometry[0].X, c.Geometry[1].X)),
                                     Math.Round(c.ThicknessMm ?? 0, 1) })
                .OrderBy(w => w[0]).ToList();

        // 146 mm wall along x 0..2000; its north face (y 146) broken at 1850..1860 where a
        // partition meets it from the north; the south face runs through. Past the break
        // only 140 mm remain - under the 200 mm minimum overlap.
        private static List<CadSegment> TeeJunction() => new List<CadSegment>
        {
            H(0, 0, 2000), H(146, 0, 1850), H(146, 1860, 2000)
        };

        [Fact]
        public void Without_the_key_the_wall_ends_at_the_break()
        {
            List<double[]> w = Walls(TeeJunction(), null);
            Assert.Single(w);
            Assert.Equal(1850, w[0][1], 0);
        }

        [Fact]
        public void A_break_in_one_face_is_bridged_while_the_other_face_runs_on()
        {
            List<double[]> w = Walls(TeeJunction(), 150);
            Assert.Single(w);
            Assert.Equal(0, w[0][0], 0);
            Assert.Equal(2000, w[0][1], 0);
            Assert.Equal(146, w[0][2], 1);
        }

        [Fact]
        public void Two_collinear_readings_across_a_bridged_break_are_one_wall()
        {
            // 70 mm break in the south face at 1000..1070, north face continuous
            var lines = new List<CadSegment> { H(0, 0, 1000), H(0, 1070, 2600), H(146, 0, 2600) };
            List<double[]> w = Walls(lines, 150);
            Assert.Single(w);
            Assert.Equal(0, w[0][0], 0);
            Assert.Equal(2600, w[0][1], 0);
        }

        [Fact]
        public void A_cross_junction_is_not_bridged_because_both_faces_stop()
        {
            // both faces broken at 1000..1120 by a wall passing through
            var lines = new List<CadSegment> { H(0, 0, 1000), H(0, 1120, 2600), H(146, 0, 1000), H(146, 1120, 2600) };
            List<double[]> w = Walls(lines, 150);
            Assert.Equal(2, w.Count);
            Assert.Equal(1000, w[0][1], 0);
            Assert.Equal(1120, w[1][0], 0);
        }

        [Fact]
        public void A_reading_is_not_extended_into_space_another_reading_already_holds()
        {
            // a 146 mm wall 0..1850 (north face broken at 1850..1860) and, beyond the break, a
            // wider reading of the same place (finish line at -15.8) from 1860 to 3000
            var lines = new List<CadSegment>
            {
                H(0, 0, 3000), H(146, 0, 1850), H(146, 1860, 3000), H(-15.8, 1860, 3000)
            };
            List<double[]> w = Walls(lines, 150);
            Assert.True(w.Count >= 1);
            // no two walls share any stretch
            for (int i = 0; i < w.Count; i++)
                for (int j = i + 1; j < w.Count; j++)
                    Assert.True(Math.Min(w[i][1], w[j][1]) - Math.Max(w[i][0], w[j][0]) <= 1,
                                string.Join(" | ", w.Select(x => string.Join(",", x))));
        }

        [Fact]
        public void The_key_is_validated()
        {
            Assert.Throws<CadRequirementSetException>(() => Set(0));
            Assert.Throws<CadRequirementSetException>(() => Set(900));
            Assert.Equal(150, Set(150).Rules.Single().Geometry.FaceBreaksMm);
            Assert.Null(Set(null).Rules.Single().Geometry.FaceBreaksMm);
        }
    }
}

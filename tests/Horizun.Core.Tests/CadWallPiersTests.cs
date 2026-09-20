// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A PIER CLOSING A WALL'S END, READ ONLY WHEN ASKED (geometry.end_piers).
//
// MEASURED (case 159C4, unit 914): a 254 mm box 146 mm long closes a 219 mm wall's
// west end; its faces are under the 200 mm minimum overlap, so the wall was built
// 206 mm short of the drawn end and the device on that end had nothing to sit on.
// These cases pin the pier, the report-only mode, and the look-alikes that must NOT
// be read as one: a finish wrap at a jamb, an open box, a box another wall occupies,
// a different wall beside the end, and a box thicker than the rule allows.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallPiersTests
    {
        private static CadRequirementSet Set(string piers, double maxThickness = 400)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'w', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic', 'level': 'Level 1', 'height_mm': 2700,
                           'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60, 'max_thickness_mm': MAXT,
                                         'min_overlap_mm': 200 PIERS } } ]
            }";
            string p = piers == null ? "" : ", 'end_piers': '" + piers + "'";
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("PIERS", p)
                .Replace("MAXT", maxThickness.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace('\'', '"')));
        }

        private static CadSegment H(double y, double x0, double x1) =>
            new CadSegment(new CadPoint(x0, y), new CadPoint(x1, y), "A-WALL", CadCurveKind.Line, 0);

        private static CadSegment V(double x, double y0, double y1) =>
            new CadSegment(new CadPoint(x, y0), new CadPoint(x, y1), "A-WALL", CadCurveKind.Line, 0);

        private static CadInterpretation Read(IList<CadSegment> lines, string piers, double maxThickness = 400) =>
            CadInterpretationRules.Interpret(lines.ToList(), Set(piers, maxThickness), "sha");

        private static List<double[]> Walls(CadInterpretation r) =>
            r.Candidates.Where(c => c.ProposedKind == "wall")
                .Select(c => new[] { Math.Round(Math.Min(c.Geometry[0].X, c.Geometry[1].X)),
                                     Math.Round(Math.Max(c.Geometry[0].X, c.Geometry[1].X)),
                                     Math.Round(c.ThicknessMm ?? 0, 1) })
                .OrderBy(w => w[0]).ToList();

        private static JArray Piers(CadInterpretation r) =>
            (JArray)r.DoubleLineReasoning.OfType<JObject>().Select(j => j["end_piers"]).FirstOrDefault(j => j != null)?["found"];

        // Case 159C4, the drawing's own lines: the pier's faces (y 3461 / 3715, x 34022-34152),
        // its end cap and 16 mm board (x 34006 / 34022 / 34023) and the 219 mm wall from x 34168.
        private static List<CadSegment> Pier159C4() => new List<CadSegment>
        {
            V(34006, 3715, 3461), V(34022, 3715, 3461), V(34023, 3715, 3461),
            H(3461, 34022, 34152), H(3715, 34152, 34022),
            H(3481, 34168, 36906), H(3700, 34168, 36906)
        };

        [Fact]
        public void Without_the_key_the_wall_ends_where_the_pairing_ends_and_nothing_is_reported()
        {
            CadInterpretation r = Read(Pier159C4(), null);
            List<double[]> w = Walls(r);
            Assert.Single(w);
            Assert.Equal(34168, w[0][0], 0);
            Assert.Null(Piers(r));
        }

        [Fact]
        public void Report_lists_the_159C4_pier_and_changes_nothing()
        {
            CadInterpretation r = Read(Pier159C4(), CadWallPiers.Report);
            List<double[]> w = Walls(r);
            Assert.Equal(34168, w[0][0], 0);
            JObject p = (JObject)Assert.Single(Piers(r));
            Assert.Null(p["refused"]);
            Assert.False((bool)p["extended"]);
            Assert.Equal(254.0, (double)p["pier_thickness_mm"], 0);
            Assert.Equal(34006.0, (double)p["cap_along_mm"], 0);
        }

        [Fact]
        public void Extend_carries_the_159C4_wall_to_the_drawn_end_cap_at_its_own_thickness()
        {
            CadInterpretation r = Read(Pier159C4(), CadWallPiers.Extend);
            List<double[]> w = Walls(r);
            Assert.Single(w);
            Assert.Equal(34006, w[0][0], 0);
            Assert.Equal(36906, w[0][1], 0);
            Assert.Equal(219, w[0][2], 0);
            Assert.True((bool)((JObject)Assert.Single(Piers(r)))["extended"]);
            Assert.Contains(r.Candidates.Single(c => c.ProposedKind == "wall").Assumptions,
                            a => a.Contains("pier") && a.Contains("type is not changed"));
        }

        [Fact]
        public void A_pier_at_the_far_end_is_read_the_same_way()
        {
            var lines = new List<CadSegment>
            {
                H(0, 0, 3000), H(219, 0, 3000),
                H(-17.5, 3000, 3130), H(236.5, 3000, 3130), V(3130, -17.5, 236.5)
            };
            List<double[]> w = Walls(Read(lines, CadWallPiers.Extend));
            Assert.Equal(0, w[0][0], 0);
            Assert.Equal(3130, w[0][1], 0);
        }

        [Fact]
        public void A_finish_wrapped_round_a_jamb_is_not_a_pier()
        {
            // an opening at x 0: the 16 mm board returns round the wall's end
            var lines = new List<CadSegment>
            {
                H(0, 0, 3000), H(219, 0, 3000), V(0, 0, 219),
                H(-16, -16, 0), H(235, -16, 0), V(-16, -16, 235)
            };
            CadInterpretation r = Read(lines, CadWallPiers.Extend);
            Assert.Equal(0, Walls(r)[0][0], 0);
            Assert.Equal("finish_wrap_not_a_pier", (string)Assert.Single(Piers(r))["refused"]);
        }

        [Fact]
        public void An_open_box_is_not_a_pier()
        {
            var lines = new List<CadSegment>
            {
                H(0, 150, 3000), H(219, 150, 3000),
                H(-17.5, 16, 146), H(236.5, 16, 146)                  // no end cap
            };
            CadInterpretation r = Read(lines, CadWallPiers.Extend);
            Assert.Equal(150, Walls(r)[0][0], 0);
            Assert.Equal("pier_not_closed_by_an_end_cap", (string)Assert.Single(Piers(r))["refused"]);
        }

        [Fact]
        public void A_box_another_wall_stands_in_is_not_a_pier()
        {
            // a crossing wall runs north-south through where the pier would be
            var lines = new List<CadSegment>
            {
                H(0, 150, 3000), H(219, 150, 3000),
                H(-17.5, 16, 146), H(236.5, 16, 146), V(16, -17.5, 236.5),
                V(40, -2000, 2000), V(130, -2000, 2000)
            };
            CadInterpretation r = Read(lines, CadWallPiers.Extend);
            Assert.Contains(Walls(r), w => w[0] == 150 && w[1] == 3000);
            Assert.Contains(Piers(r), p => (string)p["refused"] == "pier_box_holds_another_wall");
        }

        [Fact]
        public void A_different_wall_beside_the_end_is_not_this_walls_pier()
        {
            // a short pair next to the end but offset: it does not contain this wall's band
            var lines = new List<CadSegment>
            {
                H(0, 150, 3000), H(219, 150, 3000),
                H(100, 16, 146), H(300, 16, 146), V(16, 100, 300)
            };
            CadInterpretation r = Read(lines, CadWallPiers.Extend);
            Assert.Equal(150, Walls(r)[0][0], 0);
            JArray found = Piers(r);
            Assert.True(found == null || found.Count == 0);
        }

        [Fact]
        public void A_box_thicker_than_the_rule_allows_is_not_extended_into()
        {
            var lines = new List<CadSegment>
            {
                H(0, 150, 3000), H(219, 150, 3000),
                H(-150, 16, 146), H(369, 16, 146), V(16, -150, 369)   // 519 mm
            };
            CadInterpretation r = Read(lines, CadWallPiers.Extend);
            Assert.Equal(150, Walls(r)[0][0], 0);
            Assert.Equal("pier_thicker_than_the_rule_allows", (string)Assert.Single(Piers(r))["refused"]);
        }

        [Fact]
        public void The_key_is_refused_without_a_minimum_overlap_and_with_an_unknown_value()
        {
            string doc = @"{ 'schema': 'horizun.cad-requirements/1', 'requirement_set': { 'id': 'w', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'wall', 'family_type': 'Basic Wall: Generic',
                           'level': 'Level 1', 'height_mm': 2700, 'join_rule': 'none',
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60, 'max_thickness_mm': 400 X } } ] }";
            Assert.Throws<CadRequirementSetException>(() =>
                CadRequirementSet.Load(JObject.Parse(doc.Replace("X", ", 'end_piers': 'extend'").Replace('\'', '"'))));
            Assert.Throws<CadRequirementSetException>(() =>
                CadRequirementSet.Load(JObject.Parse(doc.Replace("X", ", 'min_overlap_mm': 200, 'end_piers': 'always'")
                                                        .Replace('\'', '"'))));
        }
    }
}

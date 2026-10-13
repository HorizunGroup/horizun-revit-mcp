// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L5: the boundary element of a structural wall. A schedule says "EB-1:
// 8#5 in the last 600 mm of the wall, E#3 at 100" - a column-like cage inside the
// wall, at one of its ends, running its whole height. It is a section rule whose
// section is the end region of the wall: U up the wall, V along it (restricted to
// length_mm from the declared end), W across the thickness. Every number is the
// caller's; a region longer than the wall, or too short for its covers, stirrup and
// bars, is refused by name.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallBoundaryTests
    {
        private const string Stirrups = @", 'stirrups': { 'bar_type': 'N10', 'allow_new_shape': true,
      'start': { 'hook_type': 'H135' }, 'end': { 'hook_type': 'H135' },
      'zones': [ { 'name': 'all', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 100 } } ] }";

        private const string Layers = @"[ { 'name': 'face-a', 'face': 'top', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true },
      { 'name': 'face-b', 'face': 'bottom', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true } ]";

        private static JObject Doc(string rule)
        {
            return JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'l5', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' }, { 'id': 'N10', 'type_name': '10M' } ],
  'hook_types': [ { 'id': 'H135', 'type_name': 'Stirrup/Tie - 135 deg.' } ],
  'wall_boundary_rules': [ " + rule + @" ] }");
        }

        private static string Rule(string end = "'start'", string length = "600", string host = "{ 'element_ids': [ 7 ] }",
                                   string cover = "{ 'distance_mm': 40 }", string extra = "")
        {
            return "{ 'id': 'B1', 'host': " + host + (end == null ? "" : ", 'end': " + end) +
                   (length == null ? "" : ", 'length_mm': " + length) +
                   (cover == null ? "" : ", 'cover': " + cover) + ", 'layers': " + Layers + Stirrups + extra + " }";
        }

        private static StructuralRequirementSet Load(string rule) => StructuralRequirementSet.Load(Doc(rule));

        // A wall 6000 long along +X, 200 thick (y -100..100), 3000 tall, its location line on y = 0:
        // measured the way the resolver measures it - along its location line, up world Z.
        private static SectionHostFrame Wall()
        {
            return SectionFrames.Measure(7, new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 },
                HostMesh.Box(new double[] { 0, -100, 0 }, new double[] { 6000, 100, 3000 }));
        }

        private static double Dia(string id) => id == "N10" ? 10 : 16;

        private static SectionResult Expand(StructuralRequirementSet s, SectionHostFrame wall, double? hostCover,
                                            out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zones)
        {
            Assert.True(s.Ok, s.Error);
            return WallBoundaryRules.ExpandAll(s.WallBoundaryRules.Single(), wall, Dia, null, hostCover, out bars, out zones);
        }

        // ------------------------------------------------------------------ the hooks' corner

        private static double Winding(List<double[]> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++) { double[] q = p[i], r = p[(i + 1) % p.Count]; a += q[0] * r[1] - r[0] * q[1]; }
            return a;
        }

        [Theory]
        [InlineData("'start'", 45.0)]
        [InlineData("'end'", 5955.0)]
        public void The_stirrup_starts_at_the_corner_against_the_walls_end(string end, double x)
        {
            // MEASURED, Revit 2024 and 2026 (acero L5, W1): with the hooks' corner against the wall's
            // end both legs there lie on the cover, and Revit lays the hooks' second pass along the
            // same leg. At the END region the corner used to fall inside the wall, one leg free, and
            // Revit moved the pass half a stirrup along that leg - which moved the corner bars 3.96 mm
            // off the plan. Both regions now start at the corner on the wall's end face, top side,
            // with the same winding.
            List<StructuralRebarRule> bars; List<StructuralStirrupZoneRule> zones;
            Assert.True(Expand(Load(Rule(end)), Wall(), null, out bars, out zones).Ok);
            StructuralStirrupZoneRule z = zones.First();
            Assert.Equal(x, z.ProfileMm[0][0], 6);
            Assert.Equal(55.0, z.ProfileMm[0][1], 6);
            List<StructuralStirrupZoneRule> startZones;
            Assert.True(Expand(Load(Rule("'start'")), Wall(), null, out bars, out startZones).Ok);
            Assert.Equal(System.Math.Sign(Winding(startZones.First().ProfileMm)), System.Math.Sign(Winding(z.ProfileMm)));
        }

        // ------------------------------------------------------------------ schema

        [Fact]
        public void A_boundary_rule_loads_with_its_end_its_length_and_its_section()
        {
            StructuralRequirementSet s = Load(Rule());
            Assert.True(s.Ok, s.Error);
            StructuralWallBoundaryRule b = s.WallBoundaryRules.Single();
            Assert.Equal("B1", b.Id);
            Assert.Equal(StructuralWallBoundaryRule.EndStart, b.End);
            Assert.Equal(600, b.LengthMm, 9);
            Assert.Equal("B1", b.Section.Id);
            Assert.Equal(new List<long> { 7 }, b.Section.Host.ElementIds);
            Assert.Equal(2, b.Section.Layers.Count);
            Assert.NotNull(b.Section.Stirrups);
            Assert.Equal(40, b.Section.Cover.CommonMm.Value, 9);
            Assert.Empty(s.SectionRules);
        }

        [Fact]
        public void The_end_is_declared_and_refused_by_name_when_missing()
        {
            StructuralRequirementSet s = Load(Rule(end: null));
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Equal("wall_boundary_rules['B1'].end", s.Path);
            Assert.Empty(s.WallBoundaryRules);
        }

        [Fact]
        public void The_end_is_start_or_end()
        {
            StructuralRequirementSet s = Load(Rule(end: "'left'"));
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("wall_boundary_rules['B1'].end", s.Path);
        }

        [Fact]
        public void The_length_is_declared_and_refused_by_name_when_missing()
        {
            StructuralRequirementSet s = Load(Rule(length: null));
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Equal("wall_boundary_rules['B1'].length_mm", s.Path);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-600")]
        public void The_length_is_greater_than_zero(string length)
        {
            StructuralRequirementSet s = Load(Rule(length: length));
            Assert.Equal(StructuralRequirementSet.CodeGeometry, s.Code);
            Assert.Equal("wall_boundary_rules['B1'].length_mm", s.Path);
        }

        [Fact]
        public void The_length_is_a_number()
        {
            StructuralRequirementSet s = Load(Rule(length: "'600'"));
            Assert.Equal(StructuralRequirementSet.CodeNotANumber, s.Code);
            Assert.Equal("wall_boundary_rules['B1'].length_mm", s.Path);
        }

        [Theory]
        [InlineData("{ 'element_ids': [ 7, 8 ] }")]
        [InlineData("{ 'category': 'OST_Walls' }")]
        [InlineData("{ 'element_ids': [ 7 ], 'mark': 'W1' }")]
        public void The_host_is_one_wall_named_by_its_id(string host)
        {
            StructuralRequirementSet s = Load(Rule(host: host));
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("wall_boundary_rules['B1'].host.element_ids", s.Path);
        }

        [Fact]
        public void The_cover_is_declared_as_for_a_section_rule()
        {
            StructuralRequirementSet s = Load(Rule(cover: null));
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Equal("wall_boundary_rules['B1'].cover", s.Path);
        }

        [Fact]
        public void An_unknown_key_is_refused_with_the_admitted_ones()
        {
            StructuralRequirementSet s = Load(Rule(extra: ", 'axis': 'vertical'"));
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Contains("length_mm", s.Allowed);
            Assert.Contains("end", s.Allowed);
            Assert.DoesNotContain("axis", s.Allowed);
        }

        [Fact]
        public void A_boundary_rule_shares_the_id_namespace_with_every_other_rule()
        {
            JObject doc = Doc(Rule());
            doc["section_rules"] = JArray.Parse(@"[ { 'id': 'B1', 'host': { 'element_ids': [ 9 ] }, 'cover': { 'distance_mm': 40 },
                'layers': [ { 'face': 'perimeter', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true } ] } ]");
            Assert.Equal(StructuralRequirementSet.CodeDuplicateId, StructuralRequirementSet.Load(doc).Code);
        }

        [Fact]
        public void A_set_with_only_boundary_rules_asks_for_something()
        {
            Assert.True(Load(Rule()).Ok);
            Assert.Contains("wall_boundary_rules", StructuralRequirementSet.RootKeys);
        }

        // ------------------------------------------------------------------ the frame

        [Fact]
        public void The_boundary_frame_runs_up_the_wall_along_its_end_and_across_its_thickness()
        {
            StructuralRequirementSet s = Load(Rule());
            Assert.True(s.Ok, s.Error);
            SectionHostFrame f;
            SectionResult r = WallBoundaryRules.Frame(s.WallBoundaryRules.Single(), Wall(), out f);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { 0.0, 0, 1 }, f.U);
            Assert.Equal(new[] { 1.0, 0, 0 }, f.V);
            Assert.Equal(new[] { 0.0, 1, 0 }, f.W);
            Assert.Equal(0, f.UMin, 9); Assert.Equal(3000, f.UMax, 9);
            Assert.Equal(0, f.VMin, 9); Assert.Equal(600, f.VMax, 9);
            Assert.Equal(-100, f.WMin, 9); Assert.Equal(100, f.WMax, 9);
            Assert.Equal(7, f.ElementId);
            Assert.Null(f.NotRectangular);
        }

        [Fact]
        public void At_the_end_the_region_is_the_last_length_of_the_wall()
        {
            StructuralRequirementSet s = Load(Rule(end: "'end'"));
            SectionHostFrame f;
            Assert.True(WallBoundaryRules.Frame(s.WallBoundaryRules.Single(), Wall(), out f).Ok);
            Assert.Equal(5400, f.VMin, 9); Assert.Equal(6000, f.VMax, 9);
        }

        [Fact]
        public void A_region_longer_than_the_wall_is_refused()
        {
            StructuralRequirementSet s = Load(Rule(length: "6001"));
            SectionResult r = Expand(s, Wall(), null, out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zones);
            Assert.Equal(WallBoundaryRules.CodeLongerThanWall, r.Code);
            Assert.Contains("6000", r.Why);
            Assert.Empty(bars); Assert.Empty(zones);
        }

        [Fact]
        public void A_region_as_long_as_the_wall_is_laid()
        {
            SectionResult r = Expand(Load(Rule(length: "6000")), Wall(), null, out _, out _);
            Assert.True(r.Ok, r.Why);
        }

        [Fact]
        public void A_region_too_short_for_its_covers_stirrup_and_bars_is_refused()
        {
            // 40 + 40 cover, two 10 mm stirrup legs and one 16 mm bar: 116 mm.
            SectionResult r = Expand(Load(Rule(length: "115")), Wall(), null, out List<StructuralRebarRule> bars, out _);
            Assert.Equal(WallBoundaryRules.CodeTooShort, r.Code);
            Assert.Contains("116", r.Why);
            Assert.Empty(bars);
            Assert.True(Expand(Load(Rule(length: "116")), Wall(), null, out _, out _).Ok);
        }

        [Fact]
        public void The_cover_read_from_the_host_needs_one()
        {
            SectionResult r = Expand(Load(Rule(cover: "{ 'source': 'host' }")), Wall(), null, out _, out _);
            Assert.Equal(SectionRules.CodeNoCover, r.Code);
            Assert.True(Expand(Load(Rule(cover: "{ 'source': 'host' }")), Wall(), 40, out _, out _).Ok);
        }

        [Fact]
        public void A_wall_that_is_not_a_box_is_refused_as_a_section_is()
        {
            SectionHostFrame wall = Wall();
            wall.NotRectangular = "its solid fills 0.9 of the box";
            SectionResult r = Expand(Load(Rule()), wall, null, out List<StructuralRebarRule> bars, out _);
            Assert.Equal(SectionRules.CodeNotRectangular, r.Code);
            Assert.Empty(bars);
        }

        [Fact]
        public void Only_a_wall_hosts_a_boundary_element()
        {
            Assert.Null(WallBoundaryRules.HostRefusal("OST_Walls"));
            Assert.Contains("OST_StructuralColumns", WallBoundaryRules.HostRefusal("OST_StructuralColumns"));
            Assert.NotNull(WallBoundaryRules.HostRefusal(null));
        }

        // ------------------------------------------------------------------ what it lays

        [Fact]
        public void The_bars_run_up_the_wall_inside_the_end_region_on_both_faces()
        {
            SectionResult r = Expand(Load(Rule()), Wall(), null, out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zones);
            Assert.True(r.Ok, r.Why);
            StructuralRebarRule a = bars.Single(b => b.Id == "B1#h7#face-a");
            StructuralRebarRule b2 = bars.Single(b => b.Id == "B1#h7#face-b");
            // Vertical, from the bottom end cover to the top one.
            Assert.Equal(new[] { 58.0, 42, 40 }, a.CurvesMm[0]);
            Assert.Equal(new[] { 58.0, 42, 2960 }, a.CurvesMm[1]);
            Assert.Equal(new[] { 58.0, -42, 40 }, b2.CurvesMm[0]);
            // Spread along the wall over the region less its covers, the stirrup and half a bar.
            Assert.Equal(new[] { 1.0, 0, 0 }, a.NormalMm);
            Assert.Equal(RebarLayout.FixedNumber, a.Layout.Layout);
            Assert.Equal(4, a.Layout.Number.Value);
            Assert.Equal(600 - 2 * 58, a.Layout.ArrayLengthMm.Value, 9);
            Assert.All(bars, x => Assert.Equal(new List<long> { 7 }, x.Host.ElementIds));
        }

        [Fact]
        public void The_stirrups_close_round_the_region_and_march_up_the_wall()
        {
            Expand(Load(Rule()), Wall(), null, out _, out List<StructuralStirrupZoneRule> zones);
            StructuralStirrupZoneRule z = zones.Single();
            Assert.Equal("B1#h7#stirrups", z.Id);
            Assert.True(z.Closed);
            Assert.Equal(new[] { 0.0, 0, 1 }, z.AlongMm);
            Assert.Equal(3000, z.SpanMm.Value, 9);
            // Half a stirrup inside the cover: x 45..555, y -55..55, at the wall's bottom.
            Assert.Equal(4, z.ProfileMm.Count);
            Assert.All(z.ProfileMm, p => Assert.Equal(0, p[2], 9));
            Assert.Equal(45, z.ProfileMm.Min(p => p[0]), 9); Assert.Equal(555, z.ProfileMm.Max(p => p[0]), 9);
            Assert.Equal(-55, z.ProfileMm.Min(p => p[1]), 9); Assert.Equal(55, z.ProfileMm.Max(p => p[1]), 9);
        }

        [Fact]
        public void Boundary_elements_at_both_ends_of_one_wall_do_not_meet()
        {
            JObject doc = Doc(Rule());
            JObject second = (JObject)((JArray)doc["wall_boundary_rules"])[0].DeepClone();
            second["id"] = "B2"; second["end"] = "end";
            ((JArray)doc["wall_boundary_rules"]).Add(second);
            StructuralRequirementSet s = StructuralRequirementSet.Load(doc);
            Assert.True(s.Ok, s.Error);
            var xs = new List<double[]>();
            foreach (StructuralWallBoundaryRule b in s.WallBoundaryRules)
            {
                Assert.True(WallBoundaryRules.ExpandAll(b, Wall(), Dia, null, null, out List<StructuralRebarRule> bars, out _).Ok);
                xs.Add(new[] { bars.Min(x => x.CurvesMm[0][0]), bars.Max(x => x.CurvesMm[0][0] + x.Layout.ArrayLengthMm.Value) });
            }
            Assert.Equal(58, xs[0][0], 9); Assert.Equal(542, xs[0][1], 9);
            Assert.Equal(5458, xs[1][0], 9); Assert.Equal(5942, xs[1][1], 9);
        }

        [Fact]
        public void Laps_and_into_work_up_the_wall_as_in_a_column()
        {
            string lapped = @"{ 'id': 'B1', 'host': { 'element_ids': [ 7 ] }, 'end': 'start', 'length_mm': 600, 'cover': { 'distance_mm': 40 },
              'layers': [ { 'name': 'face-a', 'face': 'top', 'count': 4, 'bar_type': 'N16', 'allow_new_shape': true,
                            'extend': { 'from_mm': -500, 'to_mm': 3700 }, 'into': [ 50, 8 ],
                            'laps': [ { 'at_mm': 300, 'length_mm': 600 } ] } ] }";
            StructuralRequirementSet s = Load(lapped);
            Assert.True(s.Ok, s.Error);
            SectionResult r = WallBoundaryRules.ExpandAll(s.WallBoundaryRules.Single(), Wall(), Dia, null, null,
                out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { "B1#h7#face-a#p1", "B1#h7#face-a#p2" }, bars.Select(b => b.Id).ToArray());
            Assert.Equal(-500, bars[0].CurvesMm[0][2], 9);
            Assert.Equal(3700, bars[1].CurvesMm[1][2], 9);
            Assert.Equal(new List<long> { 50, 8 }, bars[0].Into);
        }

        [Fact]
        public void The_codes_are_published_and_distinct()
        {
            Assert.Contains(WallBoundaryRules.CodeLongerThanWall, WallBoundaryRules.AllCodes);
            Assert.Contains(WallBoundaryRules.CodeTooShort, WallBoundaryRules.AllCodes);
            Assert.Contains(WallBoundaryRules.CodeNotAWall, WallBoundaryRules.AllCodes);
            Assert.Equal(WallBoundaryRules.AllCodes.Length, WallBoundaryRules.AllCodes.Distinct().Count());
            Assert.Empty(WallBoundaryRules.AllCodes.Intersect(SectionRules.AllCodes));
        }
    }
}

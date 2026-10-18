// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Laps (refuerzo desde cartilla, F2.1): a section layer spliced at declared
// stations, each splice a declared overlap - in mm, or in bar diameters, the
// multiplier being the caller's, never a code's.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionLapTests
    {
        private static StructuralRequirementSet Set(string laps, string extend = "'host_length'", string ends = "")
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'hook_types': [ { 'id': 'H90', 'type_name': 'Standard - 90 deg.' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': " + extend + @",
                  'laps': " + laps + ends + @" } ] } ] }"));
        }

        private static SectionHostFrame Frame() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
            UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
        };

        private static List<StructuralRebarRule> Expand(StructuralRequirementSet s, out SectionResult r)
        {
            Assert.True(s.Ok, s.Error);
            r = SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out List<StructuralRebarRule> bars, out _);
            return bars;
        }

        [Fact]
        public void Two_laps_split_a_layer_into_three_overlapping_pieces()
        {
            List<StructuralRebarRule> bars = Expand(Set("[ { 'at_mm': 2000, 'length_mm': 600 }, { 'at_ratio': 0.75, 'length_db': 40 } ]"), out SectionResult r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { "V1#h7#top#p1", "V1#h7#top#p2", "V1#h7#top#p3" }, bars.Select(b => b.Id).ToArray());
            Assert.Equal(40, bars[0].CurvesMm[0][0], 6); Assert.Equal(2300, bars[0].CurvesMm[1][0], 6);
            Assert.Equal(1700, bars[1].CurvesMm[0][0], 6); Assert.Equal(4500 + 320, bars[1].CurvesMm[1][0], 6);
            Assert.Equal(4500 - 320, bars[2].CurvesMm[0][0], 6); Assert.Equal(5960, bars[2].CurvesMm[1][0], 6);
            Assert.Equal(600, bars[0].LapNextMm.Value, 6); Assert.Equal("V1#h7#top#p2", bars[0].LapNextRuleId);
            Assert.Equal(640, bars[1].LapNextMm.Value, 6); Assert.Equal("V1#h7#top#p3", bars[1].LapNextRuleId);
            Assert.Null(bars[2].LapNextMm); Assert.Null(bars[2].LapNextRuleId);
        }

        // D4 (owner, 2026-10-09): a lap is NOT collinear. The next piece sits one bar diameter
        // away, in contact, toward the inside of the section unless the caller asks otherwise,
        // and the pieces alternate around the layer's line.
        [Fact]
        public void A_lapped_piece_moves_one_diameter_toward_the_inside()
        {
            List<StructuralRebarRule> bars = Expand(Set("[ { 'at_mm': 2000, 'length_mm': 600 }, { 'at_mm': 4500, 'length_mm': 600 } ]"), out SectionResult r);
            Assert.True(r.Ok, r.Why);
            double z0 = bars[0].CurvesMm[0][2];
            // The top layer: inside is down (-W). Piece 2 is one diameter (16) below, piece 3 back on the line.
            Assert.Equal(z0 - 16, bars[1].CurvesMm[0][2], 6); Assert.Equal(z0 - 16, bars[1].CurvesMm[1][2], 6);
            Assert.Equal(z0, bars[2].CurvesMm[0][2], 6);
            // Across the width nothing moves.
            Assert.Equal(bars[0].CurvesMm[0][1], bars[1].CurvesMm[0][1], 6);
            Assert.Equal(16, bars[0].LapNextAcrossMm.Value, 6); Assert.Equal(16, bars[1].LapNextAcrossMm.Value, 6);
            Assert.Null(bars[2].LapNextAcrossMm);
        }

        [Fact]
        public void The_bottom_layer_moves_up_and_the_direction_can_be_asked()
        {
            StructuralRequirementSet bottom = StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'bottom', 'face': 'bottom', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length',
                  'laps': [ { 'at_mm': 3000, 'length_mm': 600 } ] } ] } ] }"));
            List<StructuralRebarRule> up = Expand(bottom, out SectionResult r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(up[0].CurvesMm[0][2] + 16, up[1].CurvesMm[0][2], 6);
            // Asked outside on the top layer: up, into the cover - which leaves the cage, so refused.
            Expand(Set("[ { 'at_mm': 3000, 'length_mm': 600, 'toward': 'outside' } ]"), out SectionResult outside);
            Assert.Equal(SectionRules.CodeLapOffsetNoRoom, outside.Code);
            // Explicit inside is the default spelled out.
            List<StructuralRebarRule> inside = Expand(Set("[ { 'at_mm': 3000, 'length_mm': 600, 'toward': 'inside' } ]"), out SectionResult ri);
            Assert.True(ri.Ok, ri.Why);
            Assert.Equal(inside[0].CurvesMm[0][2] - 16, inside[1].CurvesMm[0][2], 6);
        }

        [Fact]
        public void The_laps_of_one_layer_name_one_direction()
        {
            Assert.False(Set("[ { 'at_mm': 2000, 'length_mm': 600, 'toward': 'inside' }, { 'at_mm': 4500, 'length_mm': 600, 'toward': 'outside' } ]").Ok);
            Assert.False(Set("[ { 'at_mm': 2000, 'length_mm': 600, 'toward': 'up' } ]").Ok);
        }

        [Fact]
        public void Only_the_layer_ends_keep_their_hooks()
        {
            List<StructuralRebarRule> bars = Expand(Set("[ { 'at_mm': 3000, 'length_mm': 600 } ]", ends:
                ", 'start': { 'hook_type': 'H90' }, 'end': { 'hook_type': 'H90' }"), out SectionResult r);
            Assert.True(r.Ok, r.Why);
            // The first piece is laid from its lap back to its hook (Revit matches the one-hook shape that way round).
            Assert.Null(bars[0].Start.HookTypeId); Assert.Equal("H90", bars[0].End.HookTypeId);
            Assert.Null(bars[1].Start.HookTypeId); Assert.Equal("H90", bars[1].End.HookTypeId);
        }

        [Fact]
        public void A_layer_without_laps_keeps_its_id()
        {
            StructuralRequirementSet s = Set("[]");
            List<StructuralRebarRule> bars = Expand(s, out SectionResult r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal("V1#h7#top", bars.Single().Id);
            Assert.Null(bars.Single().LapNextMm);
        }

        [Fact]
        public void A_lap_past_the_bar_or_into_another_is_refused()
        {
            Expand(Set("[ { 'at_mm': 5800, 'length_mm': 600 } ]"), out SectionResult past);
            Assert.Equal(SectionRules.CodeLapOutside, past.Code);
            Expand(Set("[ { 'at_mm': 2000, 'length_mm': 600 }, { 'at_mm': 2400, 'length_mm': 600 } ]"), out SectionResult into);
            Assert.Equal(SectionRules.CodeLapsOverlap, into.Code);
            // The run is the layer's, not the host's: a baston of 1500 cannot be lapped at 2000.
            Expand(Set("[ { 'at_mm': 2000, 'length_mm': 400 } ]", "{ 'from': 'start', 'run_mm': 1500 }"), out SectionResult baston);
            Assert.Equal(SectionRules.CodeLapOutside, baston.Code);
        }

        [Fact]
        public void A_lap_states_one_station_and_one_length()
        {
            Assert.False(Set("[ { 'length_mm': 600 } ]").Ok);
            Assert.False(Set("[ { 'at_mm': 2000 } ]").Ok);
            Assert.False(Set("[ { 'at_mm': 2000, 'at_ratio': 0.3, 'length_mm': 600 } ]").Ok);
            Assert.False(Set("[ { 'at_mm': 2000, 'length_mm': 600, 'length_db': 40 } ]").Ok);
            Assert.False(Set("[ { 'at_mm': 2000, 'length_mm': -1 } ]").Ok);
            Assert.False(Set("[ { 'at_ratio': 1.2, 'length_mm': 600 } ]").Ok);
            // Staggering is a real detail this build does not lay; refused rather than ignored.
            Assert.False(Set("[ { 'at_mm': 2000, 'length_mm': 600, 'stagger': true } ]").Ok);
            Assert.False(Set("{ 'at_mm': 2000 }").Ok);
        }

        [Fact]
        public void A_perimeter_lap_reaches_both_faces()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [ { 'id': 'C1', 'host': { 'mark': 'C-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'corners', 'face': 'perimeter', 'count': 4, 'bar_type': 'N5', 'extend': 'host_length',
                  'laps': [ { 'at_mm': 3000, 'length_mm': 800 } ] } ] } ] }"));
            List<StructuralRebarRule> bars = Expand(s, out SectionResult r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(4, bars.Count);
            Assert.Equal(2, bars.Count(b => b.LapNextMm == 800));
        }

        [Fact]
        public void The_published_bastones_and_laps_example_loads_and_expands()
        {
            string dir = System.AppContext.BaseDirectory;
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "examples", "rebar-from-schedule")))
                dir = System.IO.Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            JObject example = JObject.Parse(System.IO.File.ReadAllText(
                System.IO.Path.Combine(dir, "examples", "rebar-from-schedule", "06-plan-bastones-and-laps.json")));
            StructuralRequirementSet s = StructuralRequirementSet.Load((JObject)example["arguments"]["requirement_set"]);
            Assert.True(s.Ok, s.Error);
            // The example reads its cover from the host: 40 mm here.
            SectionResult r = SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, 40.0, out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { "V102#h7#top-start", "V102#h7#top-end", "V102#h7#bottom#p1", "V102#h7#bottom#p2" },
                         bars.Select(b => b.Id).ToArray());
            Assert.Equal(40 * 16, bars[2].LapNextMm.Value, 6);
        }

        // ------------------------------------------------------------ the audit

        private static List<double[]> Line(double u0, double u1, double v = 0) =>
            new List<double[]> { new[] { u0, v, 0.0 }, new[] { u1, v, 0.0 } };

        [Fact]
        public void The_overlap_is_measured_along_the_bar()
        {
            Assert.Equal(600, RebarAuditRules.LapOverlapMm(Line(40, 2300), Line(1700, 4820)), 6);
            // Reversed pieces measure the same overlap.
            Assert.Equal(600, RebarAuditRules.LapOverlapMm(Line(2300, 40), Line(4820, 1700)), 6);
            // Apart, the overlap is negative: the gap.
            Assert.Equal(-100, RebarAuditRules.LapOverlapMm(Line(40, 1600), Line(1700, 4820)), 6);
        }

        [Fact]
        public void A_short_lap_is_an_error_and_a_full_one_is_silent()
        {
            var tol = new StructuralTolerances();
            JArray shortLap = RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, Line(40, 2200), Line(1700, 4820), tol);
            JObject f = (JObject)shortLap.Single();
            Assert.Equal(RebarFinding.LapInsufficient, (string)f["code"]);
            Assert.Equal(RebarSeverity.Error, (string)f["severity"]);
            Assert.Empty(RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, Line(40, 2300), Line(1700, 4820), tol));
            // Within the length tolerance is a full lap.
            Assert.Empty(RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, Line(40, 2299), Line(1700, 4820), tol));
        }

        [Fact]
        public void A_lap_is_measured_at_its_declared_distance_across()
        {
            var tol = new StructuralTolerances();
            // Declared one diameter (16) across: in contact passes, collinear and far do not.
            Assert.Empty(RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, 16, Line(40, 2300), Line(1700, 4820, 16), tol));
            JObject collinear = (JObject)RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, 16, Line(40, 2300), Line(1700, 4820), tol).Single();
            Assert.Equal(RebarFinding.LapInsufficient, (string)collinear["code"]);
            Assert.Equal(0, (double)collinear["across_mm"], 6);
            Assert.Equal(16, (double)collinear["declared_across_mm"], 6);
            Assert.Single(RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, 16, Line(40, 2300), Line(1700, 4820, 60), tol));
        }

        [Fact]
        public void A_lap_between_bars_apart_is_not_a_lap()
        {
            // Overlapping in length, 200 mm apart across the section: no contact, no lap.
            JArray far = RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, Line(40, 2300), Line(1700, 4820, 200), new StructuralTolerances());
            Assert.Equal(RebarFinding.LapInsufficient, (string)((JObject)far.Single())["code"]);
        }

        [Fact]
        public void Across_is_measured_between_the_lines_where_they_overlap()
        {
            Assert.Equal(16, RebarAuditRules.LapAcrossMm(Line(40, 2300), Line(1700, 4820, 16)), 6);
            Assert.Equal(0, RebarAuditRules.LapAcrossMm(Line(40, 2300), Line(1700, 4820)), 6);
        }

        [Fact]
        public void An_unreadable_piece_leaves_the_lap_unknown()
        {
            JObject f = (JObject)RebarAuditRules.CheckLap("V1#h7#top#p1", 11, 12, 600, null, Line(1700, 4820), new StructuralTolerances()).Single();
            Assert.Equal(RebarFinding.Unreadable, (string)f["code"]);
            Assert.Equal(RebarFinding.LapInsufficient, (string)f["about"]);
            Assert.Equal("unknown", (string)f["severity"]);
        }

        [Fact]
        public void Laps_are_checked_and_say_how_far()
        {
            Assert.Contains(RebarFinding.LapInsufficient, RebarFinding.All);
            Assert.DoesNotContain("lap_insufficient", RebarFinding.NotChecked);
            Assert.Contains("undeclared_lap", RebarFinding.NotChecked);
        }
    }
}

// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L7: a section layer spliced with a COUPLER instead of a lap - two
// collinear pieces that meet at the declared station, joined there by a coupler
// whose type is named by the caller - and cover set on the faces of a host whose
// outward normal is a declared vector.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionCouplerAndFaceCoverTests
    {
        private static StructuralRequirementSet Set(string laps, string ends = "")
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'coupled', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'hook_types': [ { 'id': 'H90', 'type_name': 'Standard - 90 deg.' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length',
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

        // --------------------------------------------------------------- parse

        [Fact]
        public void A_coupler_splice_names_its_type_and_no_length()
        {
            StructuralRequirementSet s = Set("[ { 'at_mm': 3000, 'coupler_type_name': 'Coupler - 16M' } ]");
            Assert.True(s.Ok, s.Error);
            StructuralLap lap = s.SectionRules.Single().Layers.Single().Laps.Single();
            Assert.Equal("Coupler - 16M", lap.CouplerTypeName);
            Assert.Null(lap.LengthMm);
            Assert.Null(lap.LengthDb);
        }

        [Theory]
        [InlineData("[ { 'at_mm': 3000, 'coupler_type_name': 'C', 'length_mm': 600 } ]")]
        [InlineData("[ { 'at_mm': 3000, 'coupler_type_name': 'C', 'length_db': 40 } ]")]
        [InlineData("[ { 'at_mm': 3000 } ]")]
        public void A_splice_is_an_overlap_or_a_coupler_never_both_and_never_neither(string laps)
        {
            StructuralRequirementSet s = Set(laps);
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Contains("coupler_type_name", s.Error);
        }

        [Fact]
        public void A_coupler_has_no_side_to_move_toward()
        {
            StructuralRequirementSet s = Set("[ { 'at_mm': 3000, 'coupler_type_name': 'C', 'toward': 'inside' } ]");
            Assert.False(s.Ok);
            Assert.Equal("section_rules['V1'].layers[0].laps[0].toward", s.Path);
        }

        [Fact]
        public void A_blank_coupler_type_is_refused()
        {
            StructuralRequirementSet s = Set("[ { 'at_mm': 3000, 'coupler_type_name': ' ' } ]");
            Assert.False(s.Ok);
            Assert.Equal("section_rules['V1'].layers[0].laps[0].coupler_type_name", s.Path);
        }

        [Fact]
        public void Couplers_and_overlap_laps_in_one_layer_are_refused()
        {
            StructuralRequirementSet s = Set("[ { 'at_mm': 2000, 'coupler_type_name': 'C' }, { 'at_mm': 4500, 'length_mm': 600 } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Contains("coupler", s.Error);
        }

        // ----------------------------------------------------------- expansion

        [Fact]
        public void A_coupler_splits_the_layer_into_two_pieces_that_both_start_at_the_station()
        {
            // MEASURED, Revit 2024 and 2026 (L7, P4): Revit seats a coupler at END 0 of both bars and
            // REVERSES a piece that offers it its end 1. So the piece before the station is declared
            // from the station back to the layer's start, and the layer's start hook goes on its END.
            List<StructuralRebarRule> bars = Expand(Set("[ { 'at_mm': 2000, 'coupler_type_name': 'C16' } ]",
                ", 'start': { 'hook_type': 'H90' }, 'end': { 'hook_type': 'H90' }"), out SectionResult r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { "V1#h7#top#p1", "V1#h7#top#p2" }, bars.Select(b => b.Id).ToArray());
            Assert.Equal(2000, bars[0].CurvesMm[0][0], 6); Assert.Equal(40, bars[0].CurvesMm[1][0], 6);
            Assert.Equal(2000, bars[1].CurvesMm[0][0], 6); Assert.Equal(5960, bars[1].CurvesMm[1][0], 6);
            Assert.Equal(bars[0].CurvesMm[0][1], bars[1].CurvesMm[0][1], 9);
            Assert.Equal(bars[0].CurvesMm[0][2], bars[1].CurvesMm[0][2], 9);
            Assert.Equal("C16", bars[0].CouplerNextTypeName); Assert.Equal("V1#h7#top#p2", bars[0].CouplerNextRuleId);
            Assert.Equal(0, bars[0].CouplerNextEnd);
            Assert.True(bars[0].CoupledAtStart); Assert.True(bars[1].CoupledAtStart);
            Assert.Null(bars[1].CouplerNextRuleId);
            Assert.All(bars, b => Assert.Null(b.LapNextMm));
            // Only the layer's own ends keep its hooks: a coupled end carries no hook.
            Assert.Null(bars[0].Start.HookTypeId); Assert.Equal("H90", bars[0].End.HookTypeId);
            Assert.Null(bars[1].Start.HookTypeId); Assert.Equal("H90", bars[1].End.HookTypeId);
        }

        [Fact]
        public void A_layer_with_two_couplers_has_a_middle_piece_coupled_at_both_of_its_ends()
        {
            // Stage 1b, lot 5: the first piece runs from its station back (its coupler at its end 0, measured),
            // the middle piece forward - coupled at its start (end 0) to the first and at its end (end 1) to the
            // last - and the last forward. Whether Revit honours end 1 when end 0 is taken is measured live.
            List<StructuralRebarRule> bars = Expand(Set("[ { 'at_mm': 2000, 'coupler_type_name': 'C16' }, { 'at_mm': 4500, 'coupler_type_name': 'C16' } ]"), out SectionResult r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { "V1#h7#top#p1", "V1#h7#top#p2", "V1#h7#top#p3" }, bars.Select(b => b.Id).ToArray());
            Assert.Equal(2000, bars[0].CurvesMm[0][0], 6); Assert.Equal(40, bars[0].CurvesMm[1][0], 6);
            Assert.Equal(2000, bars[1].CurvesMm[0][0], 6); Assert.Equal(4500, bars[1].CurvesMm[1][0], 6);
            Assert.Equal(4500, bars[2].CurvesMm[0][0], 6); Assert.Equal(5960, bars[2].CurvesMm[1][0], 6);
            Assert.Equal(0, bars[0].CouplerNextEnd); Assert.Equal("V1#h7#top#p2", bars[0].CouplerNextRuleId);
            Assert.True(bars[1].CoupledAtStart); Assert.Equal(1, bars[1].CouplerNextEnd); Assert.Equal("V1#h7#top#p3", bars[1].CouplerNextRuleId);
            Assert.True(bars[2].CoupledAtStart); Assert.Null(bars[2].CouplerNextRuleId);
        }

        [Fact]
        public void A_coupler_at_the_end_of_the_bar_is_outside_it()
        {
            Expand(Set("[ { 'at_mm': 30, 'coupler_type_name': 'C16' } ]"), out SectionResult r);
            Assert.Equal(SectionRules.CodeLapOutside, r.Code);
        }

        [Fact]
        public void Two_couplers_at_one_station_leave_no_piece_between_them()
        {
            Expand(Set("[ { 'at_mm': 3000, 'coupler_type_name': 'C16' }, { 'at_mm': 3000, 'coupler_type_name': 'C16' } ]"), out SectionResult r);
            Assert.Equal(SectionRules.CodeLapsOverlap, r.Code);
        }

        // --------------------------------------------------------------- audit

        private static JObject Observed(long id, long couplerAt0, long couplerAt1, bool readable = true) => new JObject
        {
            ["id"] = id,
            ["terminations"] = new JArray(
                new JObject { ["end"] = 0, ["coupler_id"] = couplerAt0, ["coupler_readable"] = readable },
                new JObject { ["end"] = 1, ["coupler_id"] = couplerAt1, ["coupler_readable"] = readable })
        };

        [Fact]
        public void The_audit_finds_the_coupler_on_both_bars_and_says_nothing()
        {
            JArray f = RebarAuditRules.CheckCoupler("V1#h7#top#p1", Observed(11, -1, 500), Observed(12, 500, -1), "C16", "C16");
            Assert.Empty(f);
            // The first piece declared reversed: its coupler is at ITS end 0 (measured convention).
            JArray g = RebarAuditRules.CheckCoupler("V1#h7#top#p1", Observed(11, 500, -1), Observed(12, 500, -1), "C16", "C16", 0);
            Assert.DoesNotContain(g.OfType<JObject>(), o => (string)o["severity"] != "info");
        }

        [Fact]
        public void A_coupler_missing_at_either_bar_is_missing_coupler()
        {
            JArray a = RebarAuditRules.CheckCoupler("p1", Observed(11, -1, -1), Observed(12, 500, -1), "C16", null);
            Assert.Equal(RebarFinding.MissingCoupler, (string)a.OfType<JObject>().Single()["code"]);
            Assert.Equal(RebarSeverity.Error, (string)a.OfType<JObject>().Single()["severity"]);
            JArray b = RebarAuditRules.CheckCoupler("p1", Observed(11, -1, 500), Observed(12, -1, -1), "C16", "C16");
            Assert.Equal(RebarFinding.MissingCoupler, (string)b.OfType<JObject>().Single()["code"]);
            // Two couplers, one on each bar, are not one coupler joining them.
            JArray c = RebarAuditRules.CheckCoupler("p1", Observed(11, -1, 500), Observed(12, 501, -1), "C16", "C16");
            Assert.Equal(RebarFinding.MissingCoupler, (string)c.OfType<JObject>().Single()["code"]);
        }

        [Fact]
        public void A_coupler_of_another_type_is_named_as_such()
        {
            JArray f = RebarAuditRules.CheckCoupler("p1", Observed(11, -1, 500), Observed(12, 500, -1), "C16", "C20");
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.CouplerTypeDiffers, (string)one["code"]);
            Assert.Equal("C16", (string)one["expected"]);
            Assert.Equal("C20", (string)one["observed"]);
        }

        [Fact]
        public void A_coupler_that_could_not_be_read_is_unknown_not_missing()
        {
            JArray f = RebarAuditRules.CheckCoupler("p1", Observed(11, -1, -1, false), Observed(12, 500, -1), "C16", null);
            Assert.Equal(RebarSeverity.Unknown, (string)f.OfType<JObject>().Single()["severity"]);
            JArray t = RebarAuditRules.CheckCoupler("p1", Observed(11, -1, 500), Observed(12, 500, -1), "C16", null);
            Assert.Equal(RebarSeverity.Unknown, (string)t.OfType<JObject>().Single()["severity"]);
        }

        [Fact]
        public void A_missing_coupler_is_now_checked_and_an_incompatible_one_still_is_not()
        {
            Assert.DoesNotContain("missing_coupler", RebarFinding.NotChecked);
            Assert.Contains("coupler_incompatible", RebarFinding.NotChecked);
            Assert.Contains(RebarFinding.MissingCoupler, RebarFinding.All);
            Assert.Contains(RebarFinding.CouplerTypeDiffers, RebarFinding.All);
        }

        // ------------------------------------------------------- per-face cover

        private static StructuralRequirementSet Cover(string face) => StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'faces', 'version': '1' },
  'cover_rules': [ { 'id': 'bottom', 'host': { 'category': 'OST_StructuralFoundation' }, 'face': " + face + @",
                     'cover_type_name': 'Cast against earth', 'distance_mm': 75 } ] }"));

        [Fact]
        public void A_cover_rule_names_a_face_by_its_outward_normal()
        {
            StructuralRequirementSet s = Cover("{ 'normal': [ 0, 0, -2 ] }");
            Assert.True(s.Ok, s.Error);
            StructuralCoverRule r = s.CoverRules.Single();
            Assert.Equal(StructuralCoverRule.FaceNormal, r.Face);
            Assert.Equal(new[] { 0.0, 0, -1 }, r.FaceNormalMm);
        }

        [Fact]
        public void Common_is_still_common()
        {
            StructuralRequirementSet s = Cover("'common'");
            Assert.True(s.Ok, s.Error);
            Assert.Equal(StructuralCoverRule.FaceCommon, s.CoverRules.Single().Face);
            Assert.Null(s.CoverRules.Single().FaceNormalMm);
        }

        [Theory]
        [InlineData("'top'", StructuralRequirementSet.CodeUnknownValue)]
        [InlineData("{ 'normal': [ 0, 0, 0 ] }", StructuralRequirementSet.CodeGeometry)]
        [InlineData("{ 'normal': [ 0, 0 ] }", StructuralRequirementSet.CodeGeometry)]
        [InlineData("{ }", StructuralRequirementSet.CodeGeometry)]
        [InlineData("{ 'normal': [ 0, 0, -1 ], 'name': 'bottom' }", StructuralRequirementSet.CodeUnknownKey)]
        [InlineData("7", StructuralRequirementSet.CodeUnknownValue)]
        public void A_face_named_any_other_way_is_refused(string face, string code)
        {
            StructuralRequirementSet s = Cover(face);
            Assert.False(s.Ok);
            Assert.Equal(code, s.Code);
            Assert.StartsWith("cover_rules['bottom'].face", s.Path);
        }

        [Theory]
        [InlineData(0, 0, -1, true)]
        [InlineData(0, 0.0087, -0.99996, true)]   // half a degree off
        [InlineData(0, 0.035, -0.9994, false)]    // two degrees off
        [InlineData(0, 0, 1, false)]
        [InlineData(1, 0, 0, false)]
        public void A_face_is_matched_to_the_declared_normal_within_the_angle_tolerance(double x, double y, double z, bool match) =>
            Assert.Equal(match, CoverFaceRules.Matches(new[] { x, y, z }, new[] { 0.0, 0, -1 }, 1.0));

        [Fact]
        public void A_face_whose_normal_is_unknown_never_matches() =>
            Assert.False(CoverFaceRules.Matches(null, new[] { 0.0, 0, -1 }, 1.0));
    }
}

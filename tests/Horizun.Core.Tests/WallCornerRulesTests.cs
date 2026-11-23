using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // field cycle video review 2026-10-24, A3/A6: "un muro llega a un punto y el otro sigue sin encajar".
    public class WallCornerRulesTests
    {
        private const double Mm = 1 / 304.8;
        private static readonly double Tol = 50 * Mm;

        [Theory]
        [InlineData(15, 1, false, true)]
        [InlineData(15, 1, true, true)]
        [InlineData(30, 2, true, true)]
        [InlineData(30, 2, false, false)]
        [InlineData(150, 1, true, false)]
        [InlineData(0, 1, true, false)]
        [InlineData(15, 0, true, false)]
        public void Thin_finish_candidate_needs_explicit_width_and_layers_but_no_name(double widthMm, int layers, bool allFinish, bool expected)
            => Assert.Equal(expected, WallCornerRules.ThinFinishCandidate(widthMm * Mm, layers, allFinish));

        [Fact]
        public void A_finish_end_measured_on_a_linked_core_is_not_an_open_corner()
        {
            var host = W(1, 0, 0, 6000, 0);
            var finish = W(2, 3000, 2000, 3000, 40);
            Assert.Single(WallCornerRules.Find(new[] { host, finish }, Tol));
            finish.LinkedCoreAtEnd = true;
            Assert.Empty(WallCornerRules.Find(new[] { host, finish }, Tol));
            // Contact at the other end must not mask the real gap.
            finish.LinkedCoreAtEnd = false;
            finish.LinkedCoreAtStart = true;
            Assert.Single(WallCornerRules.Find(new[] { host, finish }, Tol));
        }

        [Fact]
        public void A_fix_must_not_move_the_supported_end_of_its_L_partner()
        {
            var a = W(1, 0, 0, 4000, 0);
            var b = W(2, 4000, 30, 4000, 3000);
            b.LinkedCoreAtStart = true;
            OpenCorner remaining = WallCornerRules.Find(new[] { a, b }, Tol).Single();
            Assert.Equal(1, remaining.WallId);
            Assert.Equal("report_only", remaining.Action);
            Assert.False(remaining.ToX.HasValue);
            Assert.Contains("linked core", remaining.Reason);
        }

        [Fact]
        public void Linked_core_termination_does_not_clear_other_storeys_or_other_ends()
        {
            var host = W(1, 0, 0, 6000, 0);
            var supported = W(2, 3000, 2000, 3000, 40);
            var open = W(3, 5000, 2000, 5000, 40);
            supported.LinkedCoreAtEnd = true;
            OpenCorner remaining = WallCornerRules.Find(new[] { host, supported, open }, Tol).Single();
            Assert.Equal(3, remaining.WallId);
        }

        [Fact]
        public void Moving_one_leg_is_not_a_closed_L_when_the_other_end_reopens()
        {
            Assert.False(WallCornerRules.MeetingClosed(true, false, true));
            Assert.True(WallCornerRules.MeetingClosed(true, true, true));
            Assert.True(WallCornerRules.MeetingClosed(true, false, false));
            Assert.False(WallCornerRules.MeetingClosed(false, true, true));
            var partner = W(2, 4000, 75, 4000, 3000);
            Assert.Equal(0, WallCornerRules.NearestEnd(partner, 4000 * Mm, 0));
            Assert.True(WallCornerRules.EndInMeeting(W(1, 0, 0, 4000, 0), partner, 4000 * Mm, 75 * Mm, 4000 * Mm, 0, 100 * Mm));
            partner.ZMin = 20; partner.ZMax = 30;
            Assert.False(WallCornerRules.EndInMeeting(W(1, 0, 0, 4000, 0), partner, 4000 * Mm, 75 * Mm, 4000 * Mm, 0, 100 * Mm));
        }

        private static WallSegment W(long id, double x0, double y0, double x1, double y1) => new WallSegment
        {
            Id = id, X0 = x0 * Mm, Y0 = y0 * Mm, X1 = x1 * Mm, Y1 = y1 * Mm, ZMin = 0, ZMax = 10
        };

        [Fact]
        public void An_L_corner_with_a_30_mm_gap_moves_both_ends_to_the_crossing()
        {
            var a = W(1, 0, 0, 4000, 0);          // along x, ends at x=4000
            var b = W(2, 4000, 30, 4000, 3000);    // along y, starts 30 mm short
            var found = WallCornerRules.Find(new List<WallSegment> { a, b }, Tol);
            OpenCorner cb = found.Single(c => c.WallId == 2);
            Assert.Equal("L", cb.Kind);
            Assert.Equal("extend", cb.Action);
            Assert.Equal(4000, cb.ToX.Value / Mm, 3);
            Assert.Equal(0, cb.ToY.Value / Mm, 3);
            Assert.Equal(30, cb.MoveFeet / Mm, 3);
            // a's end sits on b's line extension within the tolerance: it is open too, and already at the crossing
            OpenCorner ca = found.Single(c => c.WallId == 1);
            Assert.Equal(1, ca.End);
            Assert.Equal(WallCornerRules.ClosedByOther, ca.Action);
        }

        [Fact]
        public void A_T_corner_short_of_the_wall_extends_to_its_line()
        {
            var host = W(1, 0, 0, 6000, 0);
            var stub = W(2, 3000, 2000, 3000, 40);   // stops 40 mm before the host line
            OpenCorner c = WallCornerRules.Find(new List<WallSegment> { host, stub }, Tol).Single();
            Assert.Equal(2, c.WallId);
            Assert.Equal("T", c.Kind);
            Assert.Equal(1, c.End);
            Assert.Equal(0, c.ToY.Value / Mm, 3);
        }

        [Fact]
        public void A_joined_end_is_not_open()
        {
            var host = W(1, 0, 0, 6000, 0);
            var stub = W(2, 3000, 2000, 3000, 40);
            stub.JoinedAtEnd.Add(1);
            Assert.Empty(WallCornerRules.Find(new List<WallSegment> { host, stub }, Tol));
        }

        [Fact]
        public void A_disallowed_join_on_the_line_is_allowed_not_moved()
        {
            var host = W(1, 0, 0, 6000, 0);
            var stub = W(2, 3000, 2000, 3000, 0);
            stub.JoinAllowedAtEnd = false;
            OpenCorner c = WallCornerRules.Find(new List<WallSegment> { host, stub }, Tol).Single();
            Assert.Equal("allow_join", c.Action);
            Assert.False(c.ToX.HasValue);
        }

        [Fact]
        public void Parallel_walls_and_walls_on_other_storeys_make_no_corner()
        {
            var a = W(1, 0, 0, 4000, 0);
            var b = W(2, 4020, 0, 8000, 0);        // collinear, 20 mm apart
            Assert.Empty(WallCornerRules.Find(new List<WallSegment> { a, b }, Tol));
            var c = W(3, 4000, 30, 4000, 3000); c.ZMin = 20; c.ZMax = 30;
            Assert.Empty(WallCornerRules.Find(new List<WallSegment> { a, c }, Tol));
        }

        [Fact]
        public void A_gap_beyond_the_tolerance_is_not_a_corner()
        {
            var a = W(1, 0, 0, 4000, 0);
            var b = W(2, 4000, 120, 4000, 3000);
            Assert.Empty(WallCornerRules.Find(new List<WallSegment> { a, b }, Tol));
        }

        [Fact]
        public void A_skewed_end_whose_crossing_is_far_is_reported_not_moved()
        {
            // a shallow 8 degree wall ending 40 mm from the host: its line crosses the host ~285 mm away
            var host = W(1, 0, 0, 6000, 0);
            var skew = W(2, 1000, 40 + 2000 * 0.1405, 3000, 40);
            OpenCorner c = WallCornerRules.Find(new List<WallSegment> { host, skew }, Tol).Single();
            Assert.Equal("report_only", c.Action);
            Assert.Contains("design gap", c.Reason);
        }

        [Fact]
        public void Corners_needs_walls_and_fix_corners_a_document()
        {
            Assert.Null(JoinGeometryRules.Validate(new JObject { ["operation"] = "corners", ["element_ids"] = new JArray(1, 2) }));
            Assert.Contains("target_document", JoinGeometryRules.Validate(new JObject { ["operation"] = "fix_corners", ["element_ids"] = new JArray(1, 2) }));
            Assert.Null(JoinGeometryRules.Validate(new JObject { ["operation"] = "fix_corners", ["target_document"] = "m", ["element_ids"] = new JArray(1, 2), ["tolerance"] = 30 }));
            Assert.Contains("pairs is not applicable", JoinGeometryRules.Validate(new JObject { ["operation"] = "corners", ["pairs"] = new JArray(new JArray(1, 2)) }));
            Assert.Contains("element_ids", JoinGeometryRules.Validate(new JObject { ["operation"] = "corners" }));
            Assert.Contains("tolerance", JoinGeometryRules.Validate(new JObject { ["operation"] = "corners", ["element_ids"] = new JArray(1, 2), ["tolerance"] = -1 }));
            Assert.Contains("not applicable", JoinGeometryRules.Validate(new JObject { ["operation"] = "join", ["element_ids"] = new JArray(1, 2), ["tolerance"] = 5 }));
        }

        [Fact]
        public void Auto_priority_takes_one_level_and_requires_an_explicit_priority()
        {
            Assert.Contains("priority", JoinGeometryRules.Validate(new JObject { ["operation"] = "auto_priority", ["target_document"] = "m", ["level_id"] = 311 }));
            Assert.Null(JoinGeometryRules.Validate(new JObject { ["operation"] = "auto_priority", ["target_document"] = "m", ["level_id"] = 311,
                ["priority"] = new JArray("structural_framing", "structural_column") }));
            Assert.Contains("level_id", JoinGeometryRules.Validate(new JObject { ["operation"] = "auto_priority", ["target_document"] = "m" }));
            Assert.Contains("target_document", JoinGeometryRules.Validate(new JObject { ["operation"] = "auto_priority", ["level_id"] = 311 }));
            Assert.Contains("not a class", JoinGeometryRules.Validate(new JObject { ["operation"] = "auto_priority", ["target_document"] = "m", ["level_id"] = 3,
                ["priority"] = new JArray("beam") }));
            Assert.Contains("not applicable", JoinGeometryRules.Validate(new JObject { ["operation"] = "auto_priority", ["target_document"] = "m", ["level_id"] = 3,
                ["element_ids"] = new JArray(1, 2) }));
            // a column through a beam: the column cuts; a beam through a structural slab: the beam cuts
            var pr = JoinGeometryRules.PriorityOf(null);
            Assert.Equal(1, JoinGeometryRules.Cutter("structural_column", "structural_framing", pr));
            Assert.Equal(1, JoinGeometryRules.Cutter("structural_framing", "floor_structural", pr));
        }
    }
}

// horizun_wall_finish without Revit. field cycle ARQ (2026-10-24, A1/A5): ARQ walls stood on the EST
// cores of a link and counted the concrete twice; nothing typed matched them or placed the finish.
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallFinishRulesTests
    {
        private const double F = 1.0 / 304.8;
        private static double[] P(double x, double y) => new[] { x * F, y * F };

        private static JObject Create() => new JObject
        {
            ["target_document"] = "ARQ", ["operation"] = "create_finish_layers", ["wall_ids"] = new JArray(1, 2), ["link_instance_id"] = 900,
            ["interior_type_id"] = 30, ["original_wall"] = "delete_if_unhosted"
        };

        [Fact]
        public void Create_needs_a_finish_type_and_the_fate_of_the_original_said()
        {
            Assert.Null(WallFinishRules.Validate(Create()));
            var r = Create(); r.Remove("original_wall");
            Assert.Contains("never defaulted", WallFinishRules.Validate(r));
            r = Create(); r.Remove("interior_type_id");
            Assert.Contains("exterior_type_id and/or interior_type_id", WallFinishRules.Validate(r));
            r = Create(); r.Remove("link_instance_id");
            Assert.Contains("link_instance_id", WallFinishRules.Validate(r));
        }

        [Fact]
        public void Match_core_is_a_read_and_takes_no_finish_fields()
        {
            var r = new JObject { ["operation"] = "match_core", ["wall_ids"] = new JArray(1), ["link_instance_id"] = 900 };
            Assert.Null(WallFinishRules.Validate(r));
            r["original_wall"] = "keep";
            Assert.Contains("original_wall", WallFinishRules.Validate(r));
            r = new JObject { ["operation"] = "match_core", ["wall_ids"] = new JArray(1), ["link_instance_id"] = 900, ["min_overlap"] = 1.5 };
            Assert.Contains("min_overlap", WallFinishRules.Validate(r));
        }

        [Fact]
        public void The_core_under_an_arq_wall_is_parallel_near_and_overlapping()
        {
            // ARQ wall along X from 0 to 4000, exterior normal +Y.
            var cores = new List<CoreSegment>
            {
                new CoreSegment { Id = 7, A = P(-500, 20), B = P(3500, 20), Width = 150 * F, ZMin = 0, ZMax = 10 },   // 3500/4000 overlap, 20 mm off
                new CoreSegment { Id = 8, A = P(0, 400), B = P(4000, 400), Width = 150 * F, ZMin = 0, ZMax = 10 },    // too far
                new CoreSegment { Id = 9, A = P(0, 0), B = P(0, 4000), Width = 150 * F, ZMin = 0, ZMax = 10 }          // perpendicular
            };
            CoreMatch m = WallFinishRules.Match(P(0, 0), P(4000, 0), new double[] { 0, 1 }, cores, 150 * F, 0.6, 0, 10);
            Assert.Null(m.Refusal);
            Assert.Equal(7, m.CoreId);
            Assert.Equal(20, m.OffsetFeet / F, 6);
            Assert.Equal(0, m.From / F, 6);
            Assert.Equal(3500, m.To / F, 6);
            Assert.Equal(0.875, m.Ratio, 6);
        }

        [Fact]
        public void An_unmatched_wall_says_why()
        {
            var far = new List<CoreSegment> { new CoreSegment { Id = 8, A = P(0, 400), B = P(4000, 400), Width = 150 * F, ZMin = 0, ZMax = 10 } };
            Assert.Contains("beyond max_offset_mm", WallFinishRules.Match(P(0, 0), P(4000, 0), new double[] { 0, 1 }, far, 150 * F, 0.6, 0, 10).Refusal);
            var short1 = new List<CoreSegment> { new CoreSegment { Id = 8, A = P(0, 0), B = P(1000, 0), Width = 150 * F, ZMin = 0, ZMax = 10 } };
            Assert.Contains("overlaps only 25 %", WallFinishRules.Match(P(0, 0), P(4000, 0), new double[] { 0, 1 }, short1, 150 * F, 0.6, 0, 10).Refusal);
            Assert.Contains("parallel", WallFinishRules.Match(P(0, 0), P(4000, 0), new double[] { 0, 1 }, new List<CoreSegment>(), 150 * F, 0.6, 0, 10).Refusal);
        }

        [Fact]
        public void A_finish_sits_half_the_core_plus_half_itself_off_the_core_axis()
            => Assert.Equal(75 + 7.5, WallFinishRules.FinishOffset(150, 15), 9);

        [Fact]
        public void Plan_identical_cores_on_other_storeys_cannot_win_the_match()
        {
            var cores = new[] {
                new CoreSegment { Id = 1, A = P(0, 0), B = P(4000, 0), ZMin = 0, ZMax = 8 },
                new CoreSegment { Id = 2, A = P(0, 0), B = P(3500, 0), ZMin = 8, ZMax = 16 }
            };
            var match = WallFinishRules.Match(P(0, 0), P(4000, 0), new double[] { 0, 1 }, cores, 150 * F, .6, 8, 16);
            Assert.Null(match.Refusal);
            Assert.Equal(2, match.CoreId);
            Assert.NotNull(WallFinishRules.Match(P(0, 0), P(4000, 0), new double[] { 0, 1 }, cores, 150 * F, .6, 16, 24).Refusal);
            Assert.False(WallFinishRules.VerticalOverlap(8, 16, double.NaN, 24));
            Assert.False(WallFinishRules.VerticalOverlap(8, 16, 0, 8));
            Assert.True(WallFinishRules.VerticalOverlap(8, 16, 12, 24));
        }
    }
}

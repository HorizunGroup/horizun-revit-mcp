// horizun_audit_support without Revit. field cycle (video 2026-10-24, F3/A7/H4/E1): a floating roof, a
// missing slab, pipes in the air and EST "corrido" against ARQ were all found by looking at a video.
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SupportAuditRulesTests
    {
        [Fact]
        public void Each_mode_takes_its_own_fields()
        {
            Assert.Null(SupportAuditRules.Validate(new JObject { ["mode"] = "floating", ["categories"] = new JArray("OST_Roofs") }));
            Assert.Contains("categories", SupportAuditRules.Validate(new JObject { ["mode"] = "floating" }));
            Assert.Contains("level_id or room_ids", SupportAuditRules.Validate(new JObject { ["mode"] = "coverage_gaps" }));
            Assert.Contains("link_instance_id", SupportAuditRules.Validate(new JObject { ["mode"] = "model_offset" }));
            Assert.Contains("grid_mm", SupportAuditRules.Validate(new JObject { ["mode"] = "model_offset", ["link_instance_id"] = 1, ["grid_mm"] = 500 }));
            Assert.Contains("mode must be", SupportAuditRules.Validate(new JObject { ["mode"] = "everything" }));
        }

        private static readonly double[] Slab = { 0, 0, -0.5, 10, 10, 0 };

        [Fact]
        public void Support_solid_bounds_ignore_auxiliary_family_box_and_fallback_when_empty()
        {
            double[] family = { 0, 0, -300, 1, 1, 2100 };
            double[] solid = { 0, 0, 900, 1, 1, 2100 };
            double[] wall = { 0, 0, 0, 1, 1, 3000 };
            bool fallback;
            double[] selected = SupportAuditRules.ElementBox(new[] { solid }, family, out fallback);
            Assert.False(fallback);
            Assert.Equal(900, selected[2]);
            Assert.True(SupportAuditRules.Supports(wall, selected, 0, 0));
            Assert.Same(family, SupportAuditRules.ElementBox(Array.Empty<double[]>(), family, out fallback));
            Assert.True(fallback);
        }

        [Fact]
        public void Support_ground_assumption_only_applies_to_floors_at_lowest_level()
        {
            Assert.True(SupportAuditRules.OnGround(true, -300, 0));
            Assert.False(SupportAuditRules.OnGround(true, 1500, 0));
            Assert.False(SupportAuditRules.OnGround(false, -300, 0));
            Assert.False(SupportAuditRules.OnGround(true, -300, null));
            Assert.False(SupportAuditRules.Supports(Slab, new double[] { 0, 0, 1500, 1, 1, 1800 }, 50, 0));
        }

        [Fact]
        public void Foundation_at_ground_is_assumed_supported_but_elevated_foundation_is_a_finding()
        {
            double[] footing = { 0, 0, -500, 1500, 1500, 0 };
            double[] elevated = { 0, 0, 1500, 1500, 1500, 2000 };
            Assert.True(SupportAuditRules.OnGround(true, footing[2], 0));
            Assert.False(SupportAuditRules.OnGround(true, elevated[2], 0));
            Assert.False(SupportAuditRules.Supports(Slab, elevated, 50, 0));
        }

        [Fact]
        public void Slab_audit_accepts_structural_filters_and_excludes_other_modes_fields()
        {
            Assert.Null(SupportAuditRules.Validate(new JObject { ["mode"] = "slab_supports", ["level_id"] = 5 }));
            Assert.Null(SupportAuditRules.Validate(new JObject { ["mode"] = "slab_supports", ["categories"] = new JArray("OST_Floors") }));
            Assert.Contains("categories", SupportAuditRules.Validate(new JObject { ["mode"] = "slab_supports", ["categories"] = new JArray("OST_Roofs") }));
            Assert.Contains("above_tolerance_mm", SupportAuditRules.Validate(new JObject { ["mode"] = "slab_supports", ["above_tolerance_mm"] = 1 }));
            Assert.True(SupportAuditRules.Supports(Slab, new double[] { 2, 2, 0.05, 4, 4, 0.3 }, 0.05, 0));
            Assert.False(SupportAuditRules.Supports(Slab, new double[] { 2, 2, 0.06, 4, 4, 0.3 }, 0.05, 0));
        }

        [Fact]
        public void A_roof_resting_on_a_slab_is_supported_and_a_floating_one_is_not()
        {
            Assert.True(SupportAuditRules.Supports(Slab, new double[] { 2, 2, 0, 4, 4, 0.3 }, 0.1, 0));
            Assert.True(SupportAuditRules.Supports(Slab, new double[] { 2, 2, -0.2, 4, 4, 0.3 }, 0.1, 0));   // embedded
            Assert.False(SupportAuditRules.Supports(Slab, new double[] { 2, 2, 3, 4, 4, 3.3 }, 0.1, 0));
            Assert.False(SupportAuditRules.Supports(Slab, new double[] { 20, 20, 0, 24, 24, 0.3 }, 0.1, 0)); // not over it
            Assert.Equal(3, SupportAuditRules.GapBelow(new List<double[]> { Slab }, new double[] { 2, 2, 3, 4, 4, 3.3 }).Value, 9);
            Assert.Null(SupportAuditRules.GapBelow(new List<double[]> { Slab }, new double[] { 20, 20, 3, 24, 24, 3.3 }));
        }

        [Fact]
        public void A_pipe_hanging_from_the_slab_above_counts_only_when_asked()
        {
            double[] above = { 0, 0, 3, 10, 10, 3.2 };
            double[] pipe = { 2, 2, 2.6, 4, 2.1, 2.7 };
            Assert.False(SupportAuditRules.Supports(above, pipe, 0.05, 0));
            Assert.True(SupportAuditRules.Supports(above, pipe, 0.05, 0.6));
        }

        [Fact]
        public void A_model_shifted_by_a_vector_is_recovered_from_its_grid_offsets()
        {
            // grids along X (normal +Y) and along Y (normal -X), shifted by (0.05, -0.02)
            var normals = new List<double[]> { new double[] { 0, 1 }, new double[] { 0, 1 }, new double[] { -1, 0 } };
            var offsets = new List<double> { -0.02, -0.02, -0.05 };
            double[] t = SupportAuditRules.Translation(normals, offsets);
            Assert.Equal(0.05, t[0], 9);
            Assert.Equal(-0.02, t[1], 9);
            Assert.Null(SupportAuditRules.Translation(new List<double[]> { new double[] { 0, 1 }, new double[] { 0, 1 } }, new List<double> { 0.1, 0.1 }));
        }

        [Fact]
        public void Percentiles_interpolate()
        {
            Assert.Equal(2, SupportAuditRules.Percentile(new List<double> { 3, 1, 2 }, 0.5), 9);
            Assert.Equal(2.9, SupportAuditRules.Percentile(new List<double> { 1, 2, 3 }, 0.95), 9);
            Assert.True(double.IsNaN(SupportAuditRules.Percentile(new List<double>(), 0.5)));
        }
    }
}

// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Stranded-profile detection: a wall slid ALONG its own sketch plane is
// correctable by translation; a wall slid ACROSS its own face carried its
// location line off that plane, and no translation can follow it there.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallSketchDriftRulesTests
    {
        // A wall running along X at Y=0; its sketch's plane is the vertical plane
        // through that same line (normal along Y).
        private static readonly Vec3 PlaneOrigin = new Vec3(0, 0, 0);
        private static readonly Vec3 PlaneNormal = new Vec3(0, 1, 0);

        [Fact]
        public void Aligned_wall_and_sketch_are_not_stranded()
        {
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(0, 0, 0), wallLineEnd: new Vec3(5000, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(5000, 0, 3000));
            Assert.False(r.Stranded);
        }

        [Fact]
        public void A_join_gap_of_twelve_and_a_half_mm_is_not_drift_calibrated_on_a_real_model()
        {
            // Measured 2026-09-26: 64 walls on a real model, every one at exactly 12.5 mm in plane.
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(12.5, 0, 0), wallLineEnd: new Vec3(5012.5, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(5000, 0, 3000));
            Assert.False(r.Stranded);
        }

        [Fact]
        public void Wall_slid_along_its_own_run_is_stranded_but_correctable()
        {
            // The wall now runs from 2000 to 7000; the sketch's footprint is still at 0..5000.
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(2000, 0, 0), wallLineEnd: new Vec3(7000, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(5000, 0, 3000));
            Assert.True(r.Stranded);
            Assert.True(r.Correctable);
            Assert.True(r.PerpendicularOffsetMm < WallSketchDriftRules.DefaultToleranceMm);
            Assert.Equal(2000, r.CorrectionVectorMm.X, 3);
            Assert.Equal(0, r.CorrectionVectorMm.Y, 3);
        }

        [Fact]
        public void Wall_slid_sideways_off_its_own_plane_is_stranded_and_not_correctable()
        {
            // The wall now sits at Y=1500 (moved across its own face); the sketch's plane
            // is still the old one at Y=0.
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(0, 1500, 0), wallLineEnd: new Vec3(5000, 1500, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(5000, 0, 3000));
            Assert.True(r.Stranded);
            Assert.False(r.Correctable);
            Assert.True(r.PerpendicularOffsetMm > WallSketchDriftRules.DefaultToleranceMm);
            Assert.Contains("redrawn", r.Reason);
        }

        // Backlog #31: drift was measured between the CENTRES of the wall and the profile, so a
        // profile edited to run past one end of the wall read as drift - and realign_wall_sketch
        // moved it, then verified with the same metric. The ends are compared now.
        [Fact]
        public void A_profile_that_runs_past_one_end_is_an_edit_not_a_drift()
        {
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(0, 0, 0), wallLineEnd: new Vec3(5000, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(6000, 0, 3000));
            Assert.False(r.Stranded, r.Reason);
            Assert.Equal(0, r.CorrectionVectorMm.X, 3);
        }

        [Fact]
        public void A_protruding_profile_on_a_moved_wall_is_stranded_and_no_translation_is_chosen()
        {
            // Both ends off the wall's, by different amounts: moved AND edited, or a profile
            // nobody can realign by one vector. Refused, not guessed.
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(2000, 0, 0), wallLineEnd: new Vec3(7000, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(6000, 0, 3000));
            Assert.True(r.Stranded);
            Assert.False(r.Correctable);
            Assert.Equal(0, r.CorrectionVectorMm.X, 3);
        }

        [Fact]
        public void A_wall_whose_direction_runs_backwards_measures_the_same()
        {
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(7000, 0, 0), wallLineEnd: new Vec3(2000, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(5000, 0, 3000));
            Assert.True(r.Stranded);
            Assert.True(r.Correctable);
            Assert.Equal(2000, r.CorrectionVectorMm.X, 3);
        }

        [Fact]
        public void Drift_inside_tolerance_is_not_reported_as_stranded()
        {
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, PlaneNormal,
                wallLineStart: new Vec3(1, 0, 0), wallLineEnd: new Vec3(5001, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(5000, 0, 3000),
                toleranceMm: 2.0);
            Assert.False(r.Stranded);
        }

        [Fact]
        public void A_degenerate_plane_normal_is_reported_rather_than_dividing_by_zero()
        {
            var r = WallSketchDriftRules.Evaluate(
                PlaneOrigin, new Vec3(0, 0, 0),
                wallLineStart: new Vec3(0, 0, 0), wallLineEnd: new Vec3(5000, 0, 0),
                sketchFootprintMin: new Vec3(0, 0, 0), sketchFootprintMax: new Vec3(5000, 0, 3000));
            Assert.False(r.Stranded);
            Assert.False(r.Correctable);
            Assert.NotNull(r.Reason);
        }
    }
}

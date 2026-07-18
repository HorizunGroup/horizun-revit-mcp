// -----------------------------------------------------------------------------
// Horizun — proves the arithmetic behind horizun_quantities' refusal to pick a
// number. This is the anti-"volume lie" logic: given two measured volumes, does
// it correctly flag them as disagreeing, and by how much.
//
// The numbers below are the ONES WE MEASURED on a real model: a beam's Volume
// parameter said 23.1065 m3 while its solid geometry and material takeoff both
// said 40.3562 m3 — a 42.7% gap on the quantity you bill. A tool that read only
// the parameter would have billed 57% of the concrete poured. These tests pin
// that the reconciliation catches it.
//
// HorizunReconcile is Revit-free by design, so it is compile-included and tested
// without loading any Autodesk assembly.
// -----------------------------------------------------------------------------
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class HorizunReconcileTests
    {
        // ---- the measured lie ----

        [Fact]
        public void The_measured_beam_gap_is_flagged_as_disagreement()
        {
            var cmp = HorizunReconcile.Compare(23.1065, 40.3562);   // parameter vs geometry
            Assert.False(cmp.Agree);
            Assert.Equal(42.7, cmp.DifferencePct);                  // the number we reported
        }

        [Fact]
        public void Sources_that_match_agree_at_zero_percent()
        {
            // The walls we built agreed exactly — the tool must NOT invent a scandal.
            var cmp = HorizunReconcile.Compare(31.2, 31.2);
            Assert.True(cmp.Agree);
            Assert.Equal(0.0, cmp.DifferencePct);
            Assert.Equal(0.0, cmp.Difference);
        }

        // ---- the tolerance boundary ----

        [Fact]
        public void Just_inside_tolerance_agrees()
        {
            // 0.5 / 100.5 = 0.497% < 1%
            Assert.True(HorizunReconcile.Compare(100.0, 100.5, 0.01).Agree);
        }

        [Fact]
        public void Just_outside_tolerance_disagrees()
        {
            // 2 / 102 = 1.96% > 1%
            Assert.False(HorizunReconcile.Compare(100.0, 102.0, 0.01).Agree);
        }

        // ---- the edge that hides gaps: zero on one side ----

        [Fact]
        public void Zero_on_both_sides_does_not_divide_by_zero()
        {
            var cmp = HorizunReconcile.Compare(0.0, 0.0);
            Assert.True(cmp.Agree);
            Assert.Equal(0.0, cmp.DifferencePct);
        }

        [Fact]
        public void Zero_on_one_side_is_a_full_disagreement_not_a_flattered_zero()
        {
            // A parameter reading 0 while geometry reads 5 is the WORST case, not agreement.
            var cmp = HorizunReconcile.Compare(0.0, 5.0);
            Assert.False(cmp.Agree);
            Assert.Equal(100.0, cmp.DifferencePct);
        }

        // ---- units: metric at the boundary ----

        [Fact]
        public void Cubic_feet_convert_to_cubic_metres()
        {
            Assert.Equal(0.0283168466, HorizunReconcile.ToM3(1.0), 10);
            Assert.Equal(2.8316846, HorizunReconcile.ToM3(100.0), 6);
        }

        // ---- verified: intent is never evidence ----

        [Fact]
        public void One_intended_eight_actual_is_not_verified()
        {
            // Asked to code ONE beam, the model reports 8 carrying the keynote (type write).
            Assert.False(HorizunReconcile.Verified(intended: 1, actual: 8));
        }

        [Fact]
        public void Seven_hundred_fifty_eight_intended_zero_actual_is_not_verified()
        {
            // The purge that reported 758 deleted having deleted nothing.
            Assert.False(HorizunReconcile.Verified(intended: 758, actual: 0));
        }

        [Fact]
        public void A_match_is_verified()
        {
            Assert.True(HorizunReconcile.Verified(intended: 51, actual: 51));
        }
    }
}

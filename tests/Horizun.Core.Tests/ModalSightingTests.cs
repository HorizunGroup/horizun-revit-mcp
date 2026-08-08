// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// When a probed modal dialog may be DECLARED (story 5.19).
//
// The stakes on each side are asymmetric. Declaring too eagerly fail-fasts a
// queued request over a dialog Interference was already cancelling - a false
// refusal. Declaring too lazily is the measured failure: three health calls at
// 600 s each behind one "New Project" dialog. The rule that balances them is
// continuity - the SAME dialog, consecutive probes - and continuity is exactly
// what these tests break in every way a live Revit will not on demand.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ModalSightingTests
    {
        private const string Dialog = "'New Project' [#32770]";

        [Fact]
        public void One_sighting_declares_nothing()
        {
            // A single sighting can be a dialog Interference is auto-cancelling,
            // caught in the instant before it goes.
            var s = new ModalSighting();

            Assert.Null(s.Observe(Dialog));
        }

        [Fact]
        public void The_same_dialog_on_consecutive_probes_is_declared_at_the_threshold()
        {
            var s = new ModalSighting();

            string declared = null;
            for (int i = 0; i < ModalSighting.ConsecutiveSightingsToDeclare; i++)
                declared = s.Observe(Dialog);

            Assert.Equal(Dialog, declared);
        }

        [Fact]
        public void A_gap_starts_the_count_over()
        {
            // Seen, gone, seen again is two dialogs (or one being dismissed and
            // re-raised) - not one dialog persisting. Continuity is the evidence.
            var s = new ModalSighting();

            s.Observe(Dialog);
            s.Observe(null);
            string afterGap = null;
            for (int i = 0; i < ModalSighting.ConsecutiveSightingsToDeclare - 1; i++)
                afterGap = s.Observe(Dialog);

            Assert.Null(afterGap);
        }

        [Fact]
        public void A_different_dialog_starts_the_count_over()
        {
            var s = new ModalSighting();

            s.Observe(Dialog);
            s.Observe(Dialog);
            Assert.Null(s.Observe("'Save File' [#32770]"));
        }

        [Fact]
        public void Once_declared_it_stays_declared_while_the_dialog_persists()
        {
            // The waiting loop breaks on the first declaration, but the rule must
            // not flap if a caller observes one slice longer.
            var s = new ModalSighting();
            for (int i = 0; i < ModalSighting.ConsecutiveSightingsToDeclare; i++)
                s.Observe(Dialog);

            Assert.Equal(Dialog, s.Observe(Dialog));
        }

        [Fact]
        public void No_modal_ever_seen_never_declares()
        {
            var s = new ModalSighting();

            Assert.Null(s.Observe(null));
            Assert.Null(s.Observe(""));
            Assert.Null(s.Observe(null));
        }

        [Fact]
        public void The_threshold_outlives_a_single_probe_instant()
        {
            // The declaration threshold must be at least 2, or the auto-cancel
            // instant becomes declarable and the false refusal returns. Encoded as
            // a test so a future tuning cannot lower it below the reason it exists.
            Assert.True(ModalSighting.ConsecutiveSightingsToDeclare >= 2);
            Assert.True(ModalSighting.ProbeSliceMs >= 250);
        }
    }
}

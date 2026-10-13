// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// With every ordinary pipe connection held by a queued command, a cancellation or
// status control message must still get through (audit 2026-10-08).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PipeAdmissionTests
    {
        [Fact]
        public void Control_messages_still_connect_when_every_ordinary_slot_is_held()
        {
            var admission = new PipeAdmission(2, 1);
            Assert.Equal(PipeAdmission.Slot.Ordinary, admission.TryAdmit());
            Assert.Equal(PipeAdmission.Slot.Ordinary, admission.TryAdmit());

            PipeAdmission.Slot reserved = admission.TryAdmit();
            Assert.Equal(PipeAdmission.Slot.ControlOnly, reserved);
            Assert.True(PipeAdmission.Allows(reserved, "__horizun_cancel_queued"));
            Assert.True(PipeAdmission.Allows(reserved, "__horizun_request_status"));
            Assert.False(PipeAdmission.Allows(reserved, "horizun_model_scan"));   // the reserve is not a back door

            Assert.Equal(PipeAdmission.Slot.None, admission.TryAdmit());
            admission.Release(reserved);
            Assert.Equal(PipeAdmission.Slot.ControlOnly, admission.TryAdmit());
        }

        [Fact]
        public void An_ordinary_slot_carries_anything()
        {
            Assert.True(PipeAdmission.Allows(PipeAdmission.Slot.Ordinary, "horizun_health"));
            Assert.True(PipeAdmission.Allows(PipeAdmission.Slot.Ordinary, "__horizun_cancel_job"));
            Assert.False(PipeAdmission.Allows(PipeAdmission.Slot.None, "__horizun_cancel_job"));
        }

        [Fact]
        public void Refusals_are_answered_only_up_to_their_own_bound()
        {
            var admission = new PipeAdmission(1, 1, 2);
            Assert.True(admission.TryTakeRefusal());
            Assert.True(admission.TryTakeRefusal());
            Assert.False(admission.TryTakeRefusal());   // past the bound: closed unanswered, never a blocked listener
            admission.ReleaseRefusal();
            Assert.True(admission.TryTakeRefusal());
        }
    }
}

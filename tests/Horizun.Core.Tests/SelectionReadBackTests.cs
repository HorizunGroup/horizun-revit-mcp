// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// What Revit's selection holds after SetReferences, read back BY REFERENCE.
// MEASURED 2026-10-09 (Revit 2026): after selecting an element inside a link,
// Selection.GetElementIds() returns NOTHING - not the link instance - so a re-read
// by element id could never verify a linked selection, and the 6.12 refusal turned
// every one of them into an error.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SelectionReadBackTests
    {
        private static SelectionWanted Host(long id) => new SelectionWanted { ElementId = id };
        private static SelectionWanted Linked(long id, long link) => new SelectionWanted { ElementId = id, LinkInstanceId = link };
        private static SelectionHeld HostRef(long id) => new SelectionHeld { ElementId = id };
        private static SelectionHeld LinkRef(long link, long id) => new SelectionHeld { ElementId = link, LinkedElementId = id };

        [Fact]
        public void A_linked_element_held_by_reference_is_verified_individually()
        {
            SelectionVerdict v = SelectionReadBack.Verify(new[] { Linked(55, 900) }, new[] { LinkRef(900, 55) });
            Assert.True(v.HostElementsVerified);
            Assert.True(v.LinkInstancesVerified);
            Assert.True(v.LinkedElementsVerified);
            Assert.True(v.AllVerified);
        }

        [Fact]
        public void The_same_link_holding_another_element_is_not_this_selection()
        {
            SelectionVerdict v = SelectionReadBack.Verify(new[] { Linked(55, 900) }, new[] { LinkRef(900, 56) });
            Assert.True(v.LinkInstancesVerified);
            Assert.False(v.LinkedElementsVerified);
            Assert.False(v.AllVerified);
        }

        [Fact]
        public void A_host_element_and_a_linked_one_are_both_checked()
        {
            SelectionVerdict v = SelectionReadBack.Verify(new[] { Host(12), Linked(55, 900) }, new[] { HostRef(12), LinkRef(900, 55) });
            Assert.True(v.AllVerified);
            SelectionVerdict missingHost = SelectionReadBack.Verify(new[] { Host(12), Linked(55, 900) }, new[] { LinkRef(900, 55) });
            Assert.False(missingHost.HostElementsVerified);
            Assert.False(missingHost.AllVerified);
        }

        [Fact]
        public void A_host_id_equal_to_a_linked_id_does_not_stand_in_for_it()
        {
            // The defect the composite identity exists for: the same number in the host.
            SelectionVerdict v = SelectionReadBack.Verify(new[] { Linked(55, 900) }, new[] { HostRef(55) });
            Assert.False(v.LinkedElementsVerified);
            Assert.False(v.AllVerified);
        }

        [Fact]
        public void Nothing_read_back_verifies_nothing()
        {
            SelectionVerdict v = SelectionReadBack.Verify(new[] { Linked(55, 900) }, new List<SelectionHeld>());
            Assert.False(v.AllVerified);
            Assert.Equal(0, v.HeldCount);
        }

        // MEASURED 2026-10-10, Revit 2026: a linked element selected by reference (what
        // horizun_navigate leaves) and then its link unloaded or reloaded kills Revit a few
        // seconds later (0xC0000005 in RevitDB.dll, main thread); the same reload with nothing
        // selected is fine. A load-state change drops the references into that link first.
        [Fact]
        public void A_link_change_drops_only_the_references_into_that_link()
        {
            var held = new List<SelectionHeld>
            {
                new SelectionHeld { ElementId = 900, LinkedElementId = 55 },   // inside the link that changes
                new SelectionHeld { ElementId = 900, LinkedElementId = 56 },
                new SelectionHeld { ElementId = 901, LinkedElementId = 55 },   // inside ANOTHER link
                new SelectionHeld { ElementId = 900 },                         // the link instance itself
                new SelectionHeld { ElementId = 12 }                           // a host element
            };
            List<SelectionHeld> keep = SelectionReadBack.KeepForLinkChange(held, new[] { 900L }, out int dropped);
            Assert.Equal(2, dropped);
            Assert.Equal(new[] { (901L, 55L), (900L, -1L), (12L, -1L) }, keep.Select(h => (h.ElementId, h.LinkedElementId)).ToArray());
            List<SelectionHeld> none = SelectionReadBack.KeepForLinkChange(new List<SelectionHeld>(), new[] { 900L }, out int zero);
            Assert.Empty(none); Assert.Equal(0, zero);
        }
    }
}

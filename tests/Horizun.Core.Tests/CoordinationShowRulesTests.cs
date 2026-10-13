// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The show verdict needs the view, the section box and every override
// (audit 2026-10-08, 6.10).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CoordinationShowRulesTests
    {
        [Fact]
        public void A_view_without_its_colours_or_its_box_is_not_shown()
        {
            Assert.True(CoordinationShowRules.Shown(true, true, 12, 12));
            Assert.False(CoordinationShowRules.Shown(true, true, 12, 0));    // every override refused
            Assert.False(CoordinationShowRules.Shown(true, true, 12, 11));
            Assert.False(CoordinationShowRules.Shown(true, false, 12, 12));  // box not active
            Assert.False(CoordinationShowRules.Shown(false, true, 12, 12));
            Assert.False(CoordinationShowRules.Shown(true, true, 0, 0));     // nothing painted proves nothing
        }
    }
}

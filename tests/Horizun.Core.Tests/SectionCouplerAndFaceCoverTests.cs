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


        private static SectionHostFrame Frame() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
            UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
        };





















        // --------------------------------------------------------------- audit

        private static JObject Observed(long id, long couplerAt0, long couplerAt1, bool readable = true) => new JObject
        {
            ["id"] = id,
            ["terminations"] = new JArray(
                new JObject { ["end"] = 0, ["coupler_id"] = couplerAt0, ["coupler_readable"] = readable },
                new JObject { ["end"] = 1, ["coupler_id"] = couplerAt1, ["coupler_readable"] = readable })
        };









        [Fact]
        public void A_missing_coupler_is_now_checked_and_an_incompatible_one_still_is_not()
        {
            Assert.DoesNotContain("missing_coupler", RebarFinding.NotChecked);
            Assert.Contains("coupler_incompatible", RebarFinding.NotChecked);
            Assert.Contains(RebarFinding.MissingCoupler, RebarFinding.All);
            Assert.Contains(RebarFinding.CouplerTypeDiffers, RebarFinding.All);
        }












    }
}

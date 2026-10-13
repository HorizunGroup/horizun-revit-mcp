// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// An OPEN bar with a hook: Revit's as-declared form carries the hooked end on along
// the bar - MEASURED live (acero 3.0 L2, Revit 2024): a single-leg crosstie in 10M
// with a 135 degree hook at its start read 4.748 mm longer at that end, on the bar's
// own line, and unchanged at its 90 degree end. The same thing D1 accepted for a
// closed stirrup's first corner, and accepted the same way: only at a hooked end,
// only along the bar, within one bar diameter, published as info.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarHookEndExtensionTests
    {
        private static List<double[]> Want() => new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 500, 0, 0 } };

        private static JArray Compare(List<double[]> got, bool hookStart, bool hookEnd)
        {
            var f = new JArray();
            RebarAuditRules.CompareOpenHookedGeometry(f, "T", 1, Want(), got, 2, 0, hookStart, hookEnd, 9.5);
            return f;
        }

        private static List<string> Codes(JArray f) => f.OfType<JObject>().Select(o => (string)o["code"]).ToList();

        [Fact]
        public void A_hooked_start_carried_on_half_a_diameter_agrees_and_says_so()
        {
            JArray f = Compare(new List<double[]> { new[] { -4.748, 0, 0 }, new[] { 500.248, 0, 0 } }, true, true);
            Assert.DoesNotContain(RebarFinding.GeometryDiffers, Codes(f));
            JObject info = f.OfType<JObject>().Single(o => (string)o["code"] == RebarFinding.HookEndExtension);
            Assert.Equal("info", (string)info["severity"]);
        }

        [Fact]
        public void The_same_extension_at_an_end_with_no_hook_is_a_difference() =>
            Assert.Contains(RebarFinding.GeometryDiffers,
                Codes(Compare(new List<double[]> { new[] { -4.748, 0, 0 }, new[] { 500.0, 0, 0 } }, false, true)));

        [Fact]
        public void A_hooked_end_moved_sideways_is_a_difference() =>
            Assert.Contains(RebarFinding.GeometryDiffers,
                Codes(Compare(new List<double[]> { new[] { 0, 4.748, 0 }, new[] { 500.0, 4.748, 0 } }, true, true)));

        [Fact]
        public void More_than_a_diameter_is_a_difference() =>
            Assert.Contains(RebarFinding.GeometryDiffers,
                Codes(Compare(new List<double[]> { new[] { -15.0, 0, 0 }, new[] { 500.0, 0, 0 } }, true, true)));

        [Fact]
        public void A_hooked_end_cut_short_is_a_difference() =>
            Assert.Contains(RebarFinding.GeometryDiffers,
                Codes(Compare(new List<double[]> { new[] { 4.748, 0, 0 }, new[] { 500.0, 0, 0 } }, true, true)));
    }
}

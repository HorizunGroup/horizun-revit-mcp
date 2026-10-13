// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Audit 2026-10-08: a profile threshold given as a JSON number was turned into
// text with the CURRENT culture before an invariant parse, so on a Spanish
// machine 1.5 became "1,5" and the whole profile was refused as "no numeric value".
// -----------------------------------------------------------------------------
using System.Globalization;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AccessRulesCultureTests
    {
        private static JObject Profile(JToken value) => new JObject
        {
            ["name"] = "test profile",
            ["source"] = "synthetic, for tests",
            ["thresholds"] = new JObject
            {
                ["maximum_ramp_slope_percent"] = new JObject { ["value"] = value, ["comparison"] = "max" }
            }
        };

        [Theory]
        [InlineData("en-US")]
        [InlineData("es-CO")]
        public void A_fractional_threshold_reads_the_same_in_every_culture(string culture)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                AccessProfile p = AccessRules.ReadProfile(Profile(new JValue(8.33)), out string error);
                Assert.Null(error);
                Assert.NotNull(p);
                AccessProfile s = AccessRules.ReadProfile(Profile(new JValue("8.33")), out error);
                Assert.Null(error);
                Assert.NotNull(s);
                Assert.Null(AccessRules.ReadProfile(Profile(new JValue(true)), out error));
                Assert.NotNull(error);
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }
    }
}

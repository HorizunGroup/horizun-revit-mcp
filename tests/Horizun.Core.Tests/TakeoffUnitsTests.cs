// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A measured parameter that is not Length/Area/Volume is converted to the declared
// unit or refused - never sent in internal units under the declared label
// (audit 2026-10-08, 6.3).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TakeoffUnitsTests
    {
        [Theory]
        [InlineData("m", "meters")]
        [InlineData("mm", "millimeters")]
        [InlineData("kg", "kilograms")]
        [InlineData("t", "tonnes")]
        [InlineData("kn", "kilonewtons")]
        [InlineData("kg/m", "kilograms_per_meter")]
        public void Known_units_resolve(string normalised, string canonical) =>
            Assert.Equal(canonical, TakeoffUnits.CanonicalOf(normalised));

        [Theory]
        [InlineData("und")]
        [InlineData("gl")]
        [InlineData("")]
        [InlineData(null)]
        public void Unknown_units_do_not_resolve(string normalised) =>
            Assert.Null(TakeoffUnits.CanonicalOf(normalised));

        [Fact]
        public void A_bar_length_declared_in_an_unknown_unit_is_refused_not_labelled()
        {
            string why = TakeoffUnits.RefusalFor("Total Bar Length", "ml", null, false, "autodesk.spec.aec.structural:reinforcementLength-2.0.0");
            Assert.NotNull(why);
            Assert.Contains("internal units", why);
            Assert.Contains("'ml'", why);
        }

        [Fact]
        public void A_unit_of_the_wrong_kind_is_refused()
        {
            string why = TakeoffUnits.RefusalFor("Total Bar Length", "kg", "kilograms", false, "reinforcementLength");
            Assert.Contains("does not measure it", why);
        }

        [Fact]
        public void A_known_unit_valid_for_the_spec_converts()
        {
            Assert.Null(TakeoffUnits.RefusalFor("Total Bar Length", "m", "meters", true, "reinforcementLength"));
        }
    }
}

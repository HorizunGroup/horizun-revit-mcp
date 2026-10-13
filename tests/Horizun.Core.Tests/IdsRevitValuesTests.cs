// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The IDS pre-check compares what the IFC will say: SI numbers, TRUE/FALSE
// (audit 2026-10-08, 6.4).
// -----------------------------------------------------------------------------
using System.Globalization;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IdsRevitValuesTests
    {
        [Fact]
        public void A_yes_no_reads_as_the_ids_boolean()
        {
            Assert.Equal("TRUE", IdsRevitValues.YesNo(1));
            Assert.Equal("FALSE", IdsRevitValues.YesNo(0));
            Assert.True(IdsRestriction.Satisfies(IdsRevitValues.YesNo(1), new IdsValue { Simple = "TRUE" }).Satisfied);
        }

        [Fact]
        public void A_converted_length_is_invariant_under_a_comma_culture_and_meets_an_si_bound()
        {
            CultureInfo was = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("es-CO");
                string text = IdsRevitValues.Number(3.0);           // a 3 m wall, after conversion
                Assert.Equal("3", text);
                Assert.Equal("0.3048", IdsRevitValues.Number(0.3048));
                var bound = new IdsValue { MaxInclusive = 3.5 };
                bound.Patterns.Clear();
                Assert.True(IdsRestriction.Satisfies(text, bound).Satisfied);
                // what the raw feet would have said, the failure this replaces
                Assert.False(IdsRestriction.Satisfies(IdsRevitValues.Number(9.84251968503937), bound).Satisfied);
            }
            finally { CultureInfo.CurrentCulture = was; }
        }

        [Fact]
        public void Metres_are_tried_first()
        {
            Assert.Equal("meters", IdsRevitValues.SiCandidates[0]);
        }
    }
}

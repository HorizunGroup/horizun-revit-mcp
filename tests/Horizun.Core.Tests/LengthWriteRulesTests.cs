using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LengthWriteRulesTests
    {
        [Theory]
        [InlineData("0 mm", 0)]
        [InlineData("2450 mm", 2450)]
        [InlineData("4900 mm", 4900)]
        public void Parsed_length_expectation_uses_the_converted_request_instead_of_display_text(string text, double mm)
        {
            var expected = LengthWriteRules.ConvertedExpectation(text, mm / 304.8);
            Assert.True((bool)expected["readable"]);
            Assert.Equal("Double", (string)expected["storage"]);
            Assert.Equal(mm / 304.8, (double)expected["value"], 12);
            Assert.Equal(text, (string)expected["requested_text"]);
            Assert.Contains("before Set", (string)expected["expectation_source"]);
        }
        [Fact]
        public void Field_cycle_152_point_4_is_read_as_feet_and_warned_about()
        {
            // values {b: 152.4} meant 152.4 mm; Revit stored 152.4 FEET = 46.45 m.
            var row = LengthWriteRules.Interpret("b", 152.4);
            Assert.Equal(152.4, (double)row["number_is_raw_feet"], 9);
            Assert.Equal(46452.0, (double)row["equals_mm"], 0);
            string warning = (string)row["warning"];
            Assert.Contains("FEET", warning);
            Assert.Contains("46.45 m", warning);
            Assert.Contains("\"152.4 mm\"", warning);
            Assert.Contains("0.5", warning); // the feet value that really is 152.4 mm
        }

        [Fact]
        public void A_plausible_size_has_no_warning()
        {
            Assert.Null(LengthWriteRules.MagnitudeWarning("h", 1.5));          // 457.2 mm
            Assert.Null(LengthWriteRules.Interpret("h", 1.5)["warning"]);
            Assert.Null(LengthWriteRules.MagnitudeWarning("h", 0));
            Assert.Null(LengthWriteRules.MagnitudeWarning("h", 98.0));          // 29.9 m, still a long span
        }

        [Fact]
        public void A_tiny_nonzero_size_warns_that_it_may_be_metres_or_millimetres()
        {
            string warning = LengthWriteRules.MagnitudeWarning("b", 0.0002); // 0.06 mm
            Assert.NotNull(warning);
            Assert.Contains("0.06", warning);
            Assert.Contains("\"0.5 m\"", warning);
        }

        [Fact]
        public void Negative_lengths_are_judged_by_magnitude()
        {
            Assert.NotNull(LengthWriteRules.MagnitudeWarning("offset", -152.4));
        }
    }
}

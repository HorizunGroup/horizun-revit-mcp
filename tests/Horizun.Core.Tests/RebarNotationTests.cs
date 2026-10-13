// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// F3: the bar notation of a detail schedule, read as a pure function. What it does
// not recognise is refused with a code; nothing is guessed.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarNotationTests
    {
        private static RebarNotation.Result Ok(string text)
        {
            RebarNotation.Result r = RebarNotation.Parse(text);
            Assert.True(r.Ok, text + " -> " + r.Code + ": " + r.Why);
            return r;
        }

        private static void Refused(string text, string code)
        {
            RebarNotation.Result r = RebarNotation.Parse(text);
            Assert.False(r.Ok, text + " was accepted");
            Assert.Equal(code, r.Code);
            Assert.False(string.IsNullOrWhiteSpace(r.Why));
        }

        // Backlog #56: a zone count past an int threw OverflowException out of the parser
        // instead of refusing with a code.
        [Theory]
        [InlineData("E#3 99999999999@0.10 R@0.20")]
        [InlineData("E#3 2147483648@0.10")]
        public void A_zone_count_past_any_int_is_refused_with_a_code(string text)
            => Refused(text, RebarNotation.CodeBadNumber);

        [Fact]
        public void Four_number_five_bars()
        {
            RebarNotation.Result r = Ok("4#5");
            Assert.Equal(RebarNotation.KindLongitudinal, r.Kind);
            RebarNotation.Bar b = r.Bars.Single();
            Assert.Equal(4, b.Count);
            Assert.Equal(RebarNotation.SystemBarNumber, b.System);
            Assert.Equal(5, b.Size);
            Assert.Null(r.LengthMm);
        }

        [Fact]
        public void A_length_in_metres_is_read_in_millimetres()
        {
            RebarNotation.Result r = Ok("4#5 L=6.20");
            Assert.Equal(6200, r.LengthMm.Value, 6);
            Assert.Equal(6200, Ok("4#5 L=6,20").LengthMm.Value, 6);   // es-CO decimal comma
            Assert.Equal(6200, Ok("4 #5  L = 6.20").LengthMm.Value, 6);
        }

        [Fact]
        public void Groups_join_with_plus()
        {
            RebarNotation.Result r = Ok("2#4+1#5");
            Assert.Equal(new[] { (2, 4), (1, 5) }, r.Bars.Select(b => (b.Count, b.Size)).ToArray());
            Assert.Equal(new[] { (2, 4), (1, 5) }, Ok("2#4 + 1#5").Bars.Select(b => (b.Count, b.Size)).ToArray());
        }

        [Fact]
        public void Diameters_in_millimetres()
        {
            RebarNotation.Result r = Ok("4Ø16");
            RebarNotation.Bar b = r.Bars.Single();
            Assert.Equal(4, b.Count);
            Assert.Equal(RebarNotation.SystemDiameterMm, b.System);
            Assert.Equal(16, b.Size);
            Assert.Equal(16, Ok("4ø16").Bars.Single().Size);
            Assert.Equal(new[] { 12, 16 }, Ok("2Ø12+2Ø16").Bars.Select(x => x.Size).ToArray());
        }

        [Fact]
        public void A_stirrup_at_one_spacing()
        {
            RebarNotation.Result r = Ok("E#3 c/0.10");
            Assert.Equal(RebarNotation.KindStirrup, r.Kind);
            Assert.Equal(3, r.Bars.Single().Size);
            Assert.Equal(1, r.Bars.Single().Count);
            Assert.Equal(100, r.SpacingMm.Value, 6);
            Assert.Empty(r.Zones);
            Assert.Equal(100, Ok("E#3 c/0,10").SpacingMm.Value, 6);
            Assert.Equal(150, Ok("EØ10 c/0.15").SpacingMm.Value, 6);
        }

        [Fact]
        public void A_stirrup_by_zones_with_the_rest()
        {
            RebarNotation.Result r = Ok("E#3 1@0.05 10@0.10 R@0.20");
            Assert.Null(r.SpacingMm);
            Assert.Equal(3, r.Zones.Count);
            Assert.Equal(1, r.Zones[0].Count); Assert.Equal(50, r.Zones[0].SpacingMm, 6); Assert.False(r.Zones[0].Rest);
            Assert.Equal(10, r.Zones[1].Count); Assert.Equal(100, r.Zones[1].SpacingMm, 6);
            Assert.True(r.Zones[2].Rest); Assert.Null(r.Zones[2].Count); Assert.Equal(200, r.Zones[2].SpacingMm, 6);
            Assert.Equal(200, Ok("E#3 1@0,05 10@0,10 R@0,20").Zones[2].SpacingMm, 6);
        }

        [Fact]
        public void The_reply_is_json_with_its_source()
        {
            var j = RebarNotation.Parse("E#3 1@0.05 R@0.20").ToJson();
            Assert.Equal("E#3 1@0.05 R@0.20", (string)j["source"]);
            Assert.Equal("stirrup", (string)j["kind"]);
            Assert.Equal(2, ((Newtonsoft.Json.Linq.JArray)j["zones"]).Count);
            var bad = RebarNotation.Parse("4x#5").ToJson();
            Assert.False((bool)bad["ok"]);
            Assert.Equal(RebarNotation.CodeUnrecognized, (string)bad["code"]);
        }

        [Fact]
        public void What_it_does_not_recognise_is_refused_by_code()
        {
            Refused("", RebarNotation.CodeEmpty);
            Refused("   ", RebarNotation.CodeEmpty);
            Refused("4x#5", RebarNotation.CodeUnrecognized);
            Refused("4#", RebarNotation.CodeUnrecognized);
            Refused("#5", RebarNotation.CodeUnrecognized);
            Refused("4#5 L=", RebarNotation.CodeUnrecognized);
            Refused("4#5 L=6.2.0", RebarNotation.CodeBadNumber);
            Refused("4#5 L=1,234", RebarNotation.CodeBadNumber);          // a decimal or a thousands group: not guessed
            Refused("0#5", RebarNotation.CodeZeroCount);
            Refused("4#5 L=0", RebarNotation.CodeNotPositive);
            Refused("E#3 c/0.10 1@0.05", RebarNotation.CodeMixedSpacing);
            Refused("E#3 R@0.20 10@0.10", RebarNotation.CodeRestNotLast);
            Refused("E#3 R@0.20 R@0.10", RebarNotation.CodeRestNotLast);
            Refused("E#3", RebarNotation.CodeNoSpacing);
            Refused("E#3 L=2.0", RebarNotation.CodeUnrecognized);
            Refused("4#5 c/0.10", RebarNotation.CodeUnrecognized);
            Refused("4#1", RebarNotation.CodeBarOutOfRange);
            Refused("4#19", RebarNotation.CodeBarOutOfRange);
            Refused("4Ø3", RebarNotation.CodeBarOutOfRange);
            Refused("4Ø60", RebarNotation.CodeBarOutOfRange);
            Refused("4#5+", RebarNotation.CodeUnrecognized);
            Refused("2#4+1Ø16", RebarNotation.CodeMixedSystems);
        }
    }
}

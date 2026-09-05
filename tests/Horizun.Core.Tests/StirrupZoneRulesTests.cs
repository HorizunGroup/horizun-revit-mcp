// -----------------------------------------------------------------------------
// "10 at 100 over the first metre each end, 200 in the middle" - the thing an
// engineer actually writes. These pin the arithmetic that turns it into three
// bar sets, and every place it refuses instead of choosing.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StirrupZoneRulesTests
    {
        private static StirrupZoneRequest Zone(string name, double? length, string layout,
                                               double? spacing = null, int? number = null,
                                               bool first = true, bool last = true)
        {
            return new StirrupZoneRequest
            {
                Name = name,
                LengthMm = length,
                Layout = new RebarLayoutRequest
                {
                    Layout = layout,
                    SpacingMm = spacing,
                    Number = number,
                    IncludeFirstBar = first,
                    IncludeLastBar = last
                }
            };
        }

        // -------------------------------------------------------- it lays out

        [Fact]
        public void ThreeZonesLieEndToEndAlongTheSpan()
        {
            var zones = new List<StirrupZoneRequest>
            {
                // THE ZONE BEFORE A BOUNDARY GIVES UP ITS LAST BAR (ADR-003 item 12:
                // Revit keeps a suppressed FIRST bar on a spacing-driven array).
                Zone("start", 1000, RebarLayout.MaximumSpacing, 100, last: false),
                Zone("middle", null, RebarLayout.MaximumSpacing, 200, last: false),
                Zone("end", 1000, RebarLayout.MaximumSpacing, 100)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 50, 50, null, 10);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(3, r.Zones.Count);
            Assert.Equal(5900, r.UsableSpanMm);

            Assert.Equal(50, r.Zones[0].StartMm);
            Assert.Equal(1050, r.Zones[0].EndMm);
            Assert.Equal(1050, r.Zones[1].StartMm);
            Assert.Equal(3900, r.Zones[1].LengthMm);     // 5900 - 1000 - 1000
            Assert.Equal(4950, r.Zones[2].StartMm);
            Assert.Equal(5950, r.Zones[2].EndMm);
        }

        [Fact]
        public void EveryBarStationIsMeasuredFromTheStartOfTheSpan()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 400, RebarLayout.MaximumSpacing, 200, last: false),
                Zone("rest", null, RebarLayout.MaximumSpacing, 500)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(1400, zones, false, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new List<double> { 0, 200, 400 }, r.Zones[0].AbsolutePositionsMm);
            // the second zone is 1000 long from 400, at most 500 apart: 400, 900, 1400
            Assert.Equal(400, r.Zones[1].StartMm);
            Assert.Equal(new List<double> { 400, 900, 1400 }, r.Zones[1].AbsolutePositionsMm);
        }

        [Fact]
        public void TheTotalIsTheBarsNotTheBarPositions()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 400, RebarLayout.MaximumSpacing, 200, last: false),  // 3 positions, 2 bars
                Zone("rest", null, RebarLayout.MaximumSpacing, 500)                 // 3 positions, 3 bars
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(1400, zones, false, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(3, r.Zones[0].Layout.NumberOfBarPositions);
            Assert.Equal(2, r.Zones[0].Layout.Quantity);
            Assert.Equal(3, r.Zones[1].Layout.NumberOfBarPositions);
            Assert.Equal(3, r.Zones[1].Layout.Quantity);
            Assert.Equal(5, r.TotalBars);
        }

        // ---------------------------------------------------------- symmetry

        [Fact]
        public void SymmetricMirrorsTheFirstZoneAtTheFarEnd()
        {
            var zones = new List<StirrupZoneRequest>
            {
                // The ends zone gives up the bar that touches the middle; the middle
                // gives up ITS last bar against the mirror, which keeps both of its
                // own (a suppressed first bar is not honoured by Revit - item 12).
                Zone("ends", 1000, RebarLayout.MaximumSpacing, 100, last: false),
                Zone("middle", null, RebarLayout.MaximumSpacing, 200, last: false)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, true, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(3, r.Zones.Count);
            Assert.Equal("ends", r.Zones[0].Name);
            Assert.Equal("ends_mirrored", r.Zones[2].Name);
            Assert.Equal(1000, r.Zones[2].LengthMm);
            Assert.Equal(5000, r.Zones[2].StartMm);
            Assert.Equal(r.Zones[0].Layout.NumberOfBarPositions, r.Zones[2].Layout.NumberOfBarPositions);
            // the mirror keeps BOTH its ends whatever the original declared
            Assert.True(r.Zones[0].Layout.IncludeFirstBar);
            Assert.False(r.Zones[0].Layout.IncludeLastBar);
            Assert.True(r.Zones[2].Layout.IncludeFirstBar);
            Assert.True(r.Zones[2].Layout.IncludeLastBar);
        }

        [Fact]
        public void AMiddleThatKeepsItsLastBarCollidesWithTheMirrorsFirst()
        {
            // The mirror never suppresses, so the boundary before it is the
            // middle's to give up - and this is the refusal that says so.
            var zones = new List<StirrupZoneRequest>
            {
                Zone("ends", 1000, RebarLayout.MaximumSpacing, 100, last: false),
                Zone("middle", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, true, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeBarsCoincide, r.Code);
            Assert.Contains("ends_mirrored", r.Why);
        }

        [Fact]
        public void ASuppressedFirstBarOnAMaximumSpacingZoneIsRefusedByTheLayout()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.MaximumSpacing, 100),
                Zone("middle", null, RebarLayout.MaximumSpacing, 200, first: false),
                Zone("end", 1000, RebarLayout.MaximumSpacing, 100)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeLayoutRefused, r.Code);
            Assert.Contains("LAST bar of the zone before", r.Why);
        }

        [Fact]
        public void AZoneWithBothEndsOffIsRefusedByTheLayout()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.MaximumSpacing, 100),
                Zone("middle", null, RebarLayout.MaximumSpacing, 200, first: false, last: false),
                Zone("end", 1000, RebarLayout.MaximumSpacing, 100)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeLayoutRefused, r.Code);
            Assert.Contains("both false", r.Why);
        }

        [Fact]
        public void SymmetricWithAnEndZoneAlreadyDeclaredIsRefused()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.MaximumSpacing, 100),
                Zone("middle", null, RebarLayout.MaximumSpacing, 200),
                Zone("end", 1000, RebarLayout.MaximumSpacing, 100)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, true, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeSymmetricConflict, r.Code);
        }

        [Fact]
        public void SymmetricNeedsALengthOnTheZoneItMirrors()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("a", null, RebarLayout.MaximumSpacing, 100),
                Zone("b", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, true, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeSymmetricConflict, r.Code);
        }

        // ---------------------------------------------------------- it refuses

        [Fact]
        public void TwoZonesWithoutALengthIsRefusedBecauseTheRestCannotMeanTwoThings()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("a", null, RebarLayout.MaximumSpacing, 100),
                Zone("b", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeTwoRemainders, r.Code);
            Assert.Contains("'a'", r.Why);
            Assert.Contains("'b'", r.Why);
        }

        [Fact]
        public void ZonesLongerThanTheSpanAreRefusedRatherThanShortened()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 4000, RebarLayout.MaximumSpacing, 100),
                Zone("end", 4000, RebarLayout.MaximumSpacing, 100)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeZonesTooLong, r.Code);
            Assert.Contains("Nothing here shortens", r.Why);
        }

        [Fact]
        public void ARemainderZoneWithNothingLeftIsRefused()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 3000, RebarLayout.MaximumSpacing, 100),
                Zone("end", 3000, RebarLayout.MaximumSpacing, 100),
                Zone("middle", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeRemainderEmpty, r.Code);
        }

        [Fact]
        public void AZoneTooShortForItsLayoutIsRefusedByTheLayoutItself()
        {
            // 100 mm clear between 10 mm bars is a 110 mm pitch; a 50 mm zone
            // cannot hold two bars at that pitch, so the layout says so.
            var zones = new List<StirrupZoneRequest>
            {
                Zone("tiny", 50, RebarLayout.MinimumClearSpacing, 100),
                Zone("rest", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeLayoutRefused, r.Code);
            Assert.Contains("'tiny'", r.Why);
        }

        [Fact]
        public void ALayoutThatDerivesAnExtentLongerThanItsZoneIsRefused()
        {
            // 10 at 200 is 1800 long; the zone is 1000
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.NumberWithSpacing, 200, 10),
                Zone("rest", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeLayoutLongerThanZone, r.Code);
            Assert.Contains("runs into the next zone", r.Why);
        }

        [Fact]
        public void ANumberWithSpacingLayoutThatFitsIsLeftAlone()
        {
            // 5 at 200 is 800 long inside a 1000 zone: the stirrups simply stop
            var zones = new List<StirrupZoneRequest>
            {
                // 800 long inside 1000: no boundary bar to give up on either side
                Zone("start", 1000, RebarLayout.NumberWithSpacing, 200, 5),
                Zone("rest", null, RebarLayout.MaximumSpacing, 300)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(800, r.Zones[0].Layout.ArrayLengthMm);
            Assert.Equal(5, r.Zones[0].Layout.Quantity);
            Assert.Equal(800, r.Zones[0].AbsolutePositionsMm.Last());
        }

        [Fact]
        public void TwoZonesRepeatingANameAreRefusedBecauseEachBecomesItsOwnSet()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("ends", 1000, RebarLayout.MaximumSpacing, 100),
                Zone("ends", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeNameRepeated, r.Code);
        }

        [Fact]
        public void NoZonesAtAllIsRefusedRatherThanInvented()
        {
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, new List<StirrupZoneRequest>(), false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeNoZones, r.Code);
            Assert.Equal(StirrupZoneRules.CodeNoZones, StirrupZoneRules.Plan(6000, null, false, 0, 0, null, 10).Code);
        }

        // ------------------------------------------------- bars in the same place

        [Fact]
        public void TwoZonesEndingAndStartingOnTheSameStationIsRefused()
        {
            // both include their boundary bar: 1000 is the last of zone one and the
            // first of zone two. One line on the drawing, two bars in the schedule.
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.MaximumSpacing, 100),
                Zone("rest", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeBarsCoincide, r.Code);
            Assert.Contains("two bars in the quantities", r.Why);
        }

        [Fact]
        public void TurningOffOneOfTheTwoBoundaryBarsResolvesIt()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.MaximumSpacing, 100, last: false),
                Zone("rest", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
        }

        [Fact]
        public void ADeclaredMinimumBetweenZonesIsEnforced()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.MaximumSpacing, 200, last: false),
                Zone("rest", null, RebarLayout.MaximumSpacing, 200)
            };
            // zone one stops at 800 (its bar at 1000 is switched off), zone two starts at 1000
            StirrupZoneResult fine = StirrupZoneRules.Plan(6000, zones, false, 0, 0, 150, 10);
            Assert.True(fine.Ok, fine.Why);
            Assert.Equal(200, fine.ClosestBetweenZonesMm);
            Assert.Equal("start -> rest", fine.ClosestBetweenZonesWhere);

            StirrupZoneResult tight = StirrupZoneRules.Plan(6000, zones, false, 0, 0, 250, 10);
            Assert.False(tight.Ok);
            Assert.Equal(StirrupZoneRules.CodeBarsTooClose, tight.Code);
        }

        // ------------------------------------------------------- bad numbers

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0)]
        [InlineData(-100)]
        public void ASpanThatIsNotAPositiveFiniteLengthIsRefused(double span)
        {
            var zones = new List<StirrupZoneRequest> { Zone("all", null, RebarLayout.MaximumSpacing, 200) };
            StirrupZoneResult r = StirrupZoneRules.Plan(span, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeSpanNotUsable, r.Code);
        }

        [Theory]
        [InlineData(double.NaN, 0)]
        [InlineData(0, double.NaN)]
        [InlineData(-1, 0)]
        [InlineData(0, -1)]
        public void OffsetsThatAreNotFiniteAndNonNegativeAreRefused(double start, double end)
        {
            var zones = new List<StirrupZoneRequest> { Zone("all", null, RebarLayout.MaximumSpacing, 200) };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, start, end, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeOffsetsNotUsable, r.Code);
        }

        [Fact]
        public void OffsetsThatSwallowTheWholeSpanAreRefused()
        {
            var zones = new List<StirrupZoneRequest> { Zone("all", null, RebarLayout.MaximumSpacing, 200) };
            StirrupZoneResult r = StirrupZoneRules.Plan(1000, zones, false, 600, 600, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeOffsetsNotUsable, r.Code);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(0)]
        [InlineData(-500)]
        public void AZoneLengthThatIsNotPositiveAndFiniteIsRefused(double length)
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("bad", length, RebarLayout.MaximumSpacing, 100),
                Zone("rest", null, RebarLayout.MaximumSpacing, 200)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeZoneNotPositive, r.Code);
        }

        // ------------------------------------------------------ what it says

        [Fact]
        public void ZonesThatDoNotFillTheSpanSayHowMuchCarriesNoStirrups()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("start", 1000, RebarLayout.MaximumSpacing, 100, last: false),
                Zone("end", 1000, RebarLayout.MaximumSpacing, 100)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
            Assert.Contains("carries no stirrups", r.Why);
            Assert.Contains("4000 mm", r.Why);
        }

        [Fact]
        public void EveryRefusalCodeItCanReturnIsPublished()
        {
            Assert.Contains(StirrupZoneRules.CodeBarsCoincide, StirrupZoneRules.AllCodes);
            Assert.Contains(StirrupZoneRules.CodeLayoutLongerThanZone, StirrupZoneRules.AllCodes);
            Assert.Equal(StirrupZoneRules.AllCodes.Length, StirrupZoneRules.AllCodes.Distinct().Count());
        }

        [Fact]
        public void TheZoneLengthIsGivenToTheLayoutThatNeedsItAndNotToTheOneThatDerivesIt()
        {
            var maxSpacing = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100 };
            Assert.Equal(1500, StirrupZoneRules.ForZone(maxSpacing, 1500, 10).ArrayLengthMm);

            var numberWith = new RebarLayoutRequest
            {
                Layout = RebarLayout.NumberWithSpacing, Number = 5, SpacingMm = 200
            };
            Assert.Null(StirrupZoneRules.ForZone(numberWith, 1500, 10).ArrayLengthMm);

            var single = new RebarLayoutRequest { Layout = RebarLayout.Single };
            Assert.Null(StirrupZoneRules.ForZone(single, 1500, 10).ArrayLengthMm);
        }

        [Fact]
        public void TheModelDiameterWinsOverWhateverTheDeclarationCarries()
        {
            var clear = new RebarLayoutRequest { Layout = RebarLayout.MinimumClearSpacing, SpacingMm = 100 };
            Assert.Equal(20, StirrupZoneRules.ForZone(clear, 1000, 20).BarDiameterMm);

            // THIS ASSERTION USED TO SAY THE OPPOSITE - that a declared diameter is
            // not overwritten - and it was pinning a defect. The requirement-set
            // parser ALWAYS seeds the nominal diameter from bar_types, so "declared"
            // is never absent in practice and the model diameter never got through.
            // ADR-003 measured what that costs: minimum_clear_spacing counted with
            // the nominal diameter predicts 9 positions where Revit builds 8, and
            // the verified apply then reports a correct set as a failure. The plain
            // reinforcement path already overwrites for exactly this reason.
            clear.BarDiameterMm = 12;
            Assert.Equal(20, StirrupZoneRules.ForZone(clear, 1000, 20).BarDiameterMm);

            // and when the model will not say, the declaration is all there is
            Assert.Equal(12, StirrupZoneRules.ForZone(clear, 1000, 0).BarDiameterMm);
        }
    }
}

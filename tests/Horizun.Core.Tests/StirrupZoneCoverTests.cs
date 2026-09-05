// -----------------------------------------------------------------------------
// A stirrup zone that knows its host's cover.
//
// MEASURED (ADR-003 item 7): Revit clamps a hosted array to the host's cover
// plus the bar's model radius at each end, whatever the declaration says. These
// pin the arithmetic that plans zones INSIDE that clamp - so what the plan
// predicts is what Revit draws - and the refusals that stop it computing with a
// number it does not have. The prediction itself is proved only by the apply's
// post-commit comparison; nothing here claims otherwise.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StirrupZoneCoverTests
    {
        private static StirrupZoneRequest Zone(string name, double? length, double spacing,
                                               bool first = true, bool last = true)
        {
            return new StirrupZoneRequest
            {
                Name = name,
                LengthMm = length,
                Layout = new RebarLayoutRequest
                {
                    Layout = RebarLayout.MaximumSpacing,
                    SpacingMm = spacing,
                    IncludeFirstBar = first,
                    IncludeLastBar = last
                }
            };
        }

        /// <summary>"1 m at 100 each end, 200 in the middle", the middle owning neither boundary.</summary>
        private static List<StirrupZoneRequest> Schedule()
        {
            return new List<StirrupZoneRequest>
            {
                Zone("start", 1000, 100, last: false),
                Zone("middle", null, 200, last: false),
                Zone("end", 1000, 100)
            };
        }

        private static StructuralStirrupZoneRule Rule(StructuralStirrupZoneCover cover, double? span = 6000)
        {
            return new StructuralStirrupZoneRule
            {
                Id = "B1",
                BarTypeId = "s10",
                ProfileMm = new List<double[]>
                {
                    new double[] { 0, -102, 48 }, new double[] { 0, 102, 48 },
                    new double[] { 0, 102, 552 }, new double[] { 0, -102, 552 }
                },
                Closed = true,
                AlongMm = new double[] { 1, 0, 0 },
                SpanMm = span,
                Cover = cover,
                Zones = Schedule()
            };
        }

        private static double Last(List<double> xs)
        {
            return xs[xs.Count - 1];
        }

        // ------------------------------------------------ nothing changes without it

        [Fact]
        public void WithoutACoverBlockThePlanIsExactlyWhatItWasBefore()
        {
            StirrupZoneResult before = StirrupZoneRules.Plan(6000, Schedule(), false, 50, 50, null, 10);
            StirrupZoneResult after = StirrupZoneRules.Plan(6000, Schedule(), false, 50, 50, null, 10, null, null);
            Assert.True(after.Ok, after.Why);
            Assert.False(after.PredictedFromHostCover);
            Assert.Equal(0, after.ClampEachEndMm);
            Assert.Equal(6000, after.CoverUsableSpanMm);
            Assert.Equal(before.UsableSpanMm, after.UsableSpanMm);
            for (int i = 0; i < before.Zones.Count; i++)
                Assert.Equal(before.Zones[i].AbsolutePositionsMm, after.Zones[i].AbsolutePositionsMm);

            List<StructuralRebarRule> made;
            StirrupZoneRules.Expand(Rule(null), 6000, 10, 25.4, out made);
            Assert.All(made, r => Assert.Null(r.CoverPrediction));
        }

        // ------------------------------------------------------- the arithmetic

        [Fact]
        public void TheClampIsCoverPlusRadiusAtBothEnds()
        {
            // cover 25, bar 10 -> 30 in from each end; zones lay out on 5940
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, Schedule(), false, 0, 0, null, 10, 25, "declared");
            Assert.True(r.Ok, r.Why);
            Assert.True(r.PredictedFromHostCover);
            Assert.Equal(30, r.ClampEachEndMm, 9);
            Assert.Equal(5, r.BarRadiusMm, 9);
            Assert.Equal(5940, r.CoverUsableSpanMm, 9);
            Assert.Equal(5940, r.UsableSpanMm, 9);
            Assert.Equal("declared", r.CoverSource);

            Assert.Equal(30, r.Zones[0].StartMm, 9);                        // the first stirrup sits at the clamp
            Assert.Equal(30, r.Zones[0].AbsolutePositionsMm[0], 9);
            Assert.Equal(1030, Last(r.Zones[0].AbsolutePositionsMm), 9);
            Assert.Equal(3940, r.Zones[1].LengthMm, 9);                     // 5940 - 1000 - 1000
            Assert.Equal(5970, Last(r.Zones[2].AbsolutePositionsMm), 9);    // 6000 - 30: the last sits at the clamp too
        }

        [Fact]
        public void DeclaredOffsetsAreMeasuredFromTheUsableSpansEnds()
        {
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, Schedule(), false, 50, 20, null, 10, 25, "declared");
            Assert.True(r.Ok, r.Why);
            Assert.Equal(50, r.StartOffsetMm);   // reported as declared, not as clamp + offset
            Assert.Equal(20, r.EndOffsetMm);
            Assert.Equal(80, r.Zones[0].AbsolutePositionsMm[0], 9);          // 30 clamp + 50 declared
            Assert.Equal(5950, Last(r.Zones[2].AbsolutePositionsMm), 9);     // 6000 - 30 - 20
            Assert.Equal(5940, r.CoverUsableSpanMm, 9);
            Assert.Equal(5870, r.UsableSpanMm, 9);                           // less the two declared offsets
        }

        [Theory]
        [InlineData(0, 12, 6000, 6)]          // zero cover: the radius alone
        [InlineData(40, 20, 8000, 50)]
        [InlineData(25.4, 12, 5000, 31.4)]    // the wall the live harness measured
        [InlineData(50, 32, 12000, 66)]
        public void SeveralCoversAndDiametersLandTheEndsAtTheClamp(double cover, double dia, double span, double clamp)
        {
            StirrupZoneResult r = StirrupZoneRules.Plan(span, Schedule(), false, 0, 0, null, dia, cover, "host");
            Assert.True(r.Ok, r.Why);
            Assert.Equal(clamp, r.ClampEachEndMm, 9);
            Assert.Equal(clamp, r.Zones[0].AbsolutePositionsMm[0], 9);
            Assert.Equal(span - clamp, Last(r.Zones[2].AbsolutePositionsMm), 9);
            Assert.Equal(span - 2 * clamp, r.CoverUsableSpanMm, 9);
            // every station of every zone lies inside the clamp
            foreach (StirrupZonePlan z in r.Zones)
                foreach (double s in z.AbsolutePositionsMm)
                    Assert.InRange(s, clamp - 1e-9, span - clamp + 1e-9);
        }

        [Fact]
        public void TheBarCountFollowsTheShorterSpan()
        {
            // 25 cover, 10 bar: the middle is 3940 at a maximum of 200 -> 20 gaps, 21
            // positions, 20 bars once its last station is handed to the end zone
            // (the start zone gives up ITS last bar at the first boundary).
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, Schedule(), false, 0, 0, null, 10, 25, "declared");
            Assert.True(r.Ok, r.Why);
            Assert.Equal(11, r.Zones[0].Layout.NumberOfBarPositions);
            Assert.Equal(10, r.Zones[0].Layout.Quantity);
            Assert.Equal(21, r.Zones[1].Layout.NumberOfBarPositions);
            Assert.Equal(20, r.Zones[1].Layout.Quantity);
            Assert.Equal(11, r.Zones[2].Layout.NumberOfBarPositions);
            Assert.Equal(11, r.Zones[2].Layout.Quantity);
            Assert.Equal(41, r.TotalBars);
            Assert.Equal(197, r.Zones[1].Layout.ResultingSpacingMm.Value, 9);
        }

        [Fact]
        public void ARemainderAtAnExactMultipleDoesNotGainABar()
        {
            // 6080 less 40 at each end is 6000; the middle is exactly 4000 at 200:
            // 20 gaps, 21 positions - not 22. One millimetre more and it is 22.
            StirrupZoneResult exact = StirrupZoneRules.Plan(6080, Schedule(), false, 0, 0, null, 40, 20, "declared");
            Assert.True(exact.Ok, exact.Why);
            Assert.Equal(4000, exact.Zones[1].LengthMm, 9);
            Assert.Equal(21, exact.Zones[1].Layout.NumberOfBarPositions);
            Assert.Equal(200, exact.Zones[1].Layout.ResultingSpacingMm.Value, 9);

            StirrupZoneResult over = StirrupZoneRules.Plan(6081, Schedule(), false, 0, 0, null, 40, 20, "declared");
            Assert.True(over.Ok, over.Why);
            Assert.Equal(22, over.Zones[1].Layout.NumberOfBarPositions);
        }

        [Fact]
        public void TheEndZonesStillOwnTheBoundaryStationsInsideTheClamp()
        {
            // the zone before each boundary gives up its last bar, so the boundary
            // stations stay unique whatever the clamp does to the absolute stations.
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, Schedule(), false, 0, 0, null, 12, 30, "declared");
            Assert.True(r.Ok, r.Why);
            double clamp = 36;
            Assert.Equal(clamp + 1000, Last(r.Zones[0].AbsolutePositionsMm), 9);
            Assert.DoesNotContain(r.Zones[0].AbsoluteBarPositionsMm, s => System.Math.Abs(s - (clamp + 1000)) < 1e-9);
            Assert.Equal(clamp + 1000, r.Zones[1].AbsoluteBarPositionsMm[0], 9);
            Assert.True(r.ClosestBetweenZonesMm.Value > 0);
        }

        [Fact]
        public void SymmetricZonesMirrorInsideTheClamp()
        {
            var zones = new List<StirrupZoneRequest>
            {
                Zone("end", 1000, 100, last: false),
                Zone("middle", null, 200, last: false)
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, true, 0, 0, null, 10, 25, "host");
            Assert.True(r.Ok, r.Why);
            Assert.Equal(3, r.Zones.Count);
            Assert.Equal("end_mirrored", r.Zones[2].Name);
            Assert.Equal(30, r.Zones[0].AbsolutePositionsMm[0], 9);
            Assert.Equal(5970, Last(r.Zones[2].AbsolutePositionsMm), 9);
            Assert.Equal(4970, r.Zones[2].StartMm, 9);
        }

        // ---------------------------------------------------------- refusals

        [Fact]
        public void ACoverThatLeavesNoSpanIsRefusedByName()
        {
            StirrupZoneResult r = StirrupZoneRules.Plan(1000, Schedule(), false, 0, 0, null, 12, 495, "declared");
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeCoverLeavesNoSpan, r.Code);
            Assert.Contains("501 mm", r.Why);
            Assert.Empty(r.Zones);
        }

        [Fact]
        public void ACoverThatReachesExactlyHalfTheSpanIsRefusedToo()
        {
            // 494 + 6 = 500 at each end is the whole 1000: nothing left, not "zero left".
            StirrupZoneResult r = StirrupZoneRules.Plan(1000, Schedule(), false, 0, 0, null, 12, 494, "declared");
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeCoverLeavesNoSpan, r.Code);
        }

        [Fact]
        public void ACoverThatLeavesLessThanTheZonesNeedIsTheOrdinaryTooLongRefusal()
        {
            // 2400 less 30 at each end is 2340; the two 1000 end zones fit and the
            // remainder is 340, enough for a middle that owns neither boundary
            // station. 2000 less 30 each end is 1940 and the declared zones no
            // longer fit.
            StirrupZoneResult fits = StirrupZoneRules.Plan(2400, Schedule(), false, 0, 0, null, 10, 25, "declared");
            Assert.True(fits.Ok, fits.Why);
            StirrupZoneResult r = StirrupZoneRules.Plan(2000, Schedule(), false, 0, 0, null, 10, 25, "declared");
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeZonesTooLong, r.Code);
            Assert.Contains("1940 mm", r.Why);
        }

        [Fact]
        public void ACoverBlockWithoutABarDiameterIsRefusedRatherThanComputedWithZero()
        {
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, Schedule(), false, 0, 0, null, 0, 25, "declared");
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeCoverNeedsDiameter, r.Code);
        }

        [Fact]
        public void ANegativeOrNonFiniteCoverIsRefused()
        {
            Assert.Equal(StirrupZoneRules.CodeCoverNotUsable,
                StirrupZoneRules.Plan(6000, Schedule(), false, 0, 0, null, 10, -1, "declared").Code);
            Assert.Equal(StirrupZoneRules.CodeCoverNotUsable,
                StirrupZoneRules.Plan(6000, Schedule(), false, 0, 0, null, 10, double.NaN, "declared").Code);
        }

        [Fact]
        public void HostSourceWithoutAReadableHostCoverIsRefusedByName()
        {
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(
                Rule(new StructuralStirrupZoneCover { Source = StructuralStirrupZoneCover.SourceHost }),
                6000, 10, null, out made);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeHostCoverUnknown, r.Code);
            Assert.Empty(made);
            Assert.Contains("cover_rule", r.Why);
        }

        [Fact]
        public void TheNewCodesArePublishedAndDistinct()
        {
            Assert.Contains(StirrupZoneRules.CodeCoverLeavesNoSpan, StirrupZoneRules.AllCodes);
            Assert.Contains(StirrupZoneRules.CodeCoverNeedsDiameter, StirrupZoneRules.AllCodes);
            Assert.Contains(StirrupZoneRules.CodeCoverNotUsable, StirrupZoneRules.AllCodes);
            Assert.Contains(StirrupZoneRules.CodeHostCoverUnknown, StirrupZoneRules.AllCodes);
            Assert.Equal(StirrupZoneRules.AllCodes.Length, StirrupZoneRules.AllCodes.Distinct().Count());
        }

        // --------------------------------------------------------- expansion

        [Fact]
        public void HostSourceTakesTheCoverTheResolverRead()
        {
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(
                Rule(new StructuralStirrupZoneCover { Source = StructuralStirrupZoneCover.SourceHost }),
                6000, 12, 25.4, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(25.4, r.CoverMm.Value, 9);
            Assert.Equal("host", r.CoverSource);
            Assert.Equal(31.4, r.ClampEachEndMm, 9);
        }

        [Fact]
        public void DeclaredSourceIgnoresWhateverTheHostSays()
        {
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(
                Rule(new StructuralStirrupZoneCover { Source = StructuralStirrupZoneCover.SourceDeclared, DistanceMm = 40 }),
                6000, 12, 25.4, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(40, r.CoverMm.Value, 9);
            Assert.Equal(46, r.ClampEachEndMm, 9);
        }

        [Fact]
        public void EveryExpandedRuleCarriesThePredictionAndIsMovedByTheClamp()
        {
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(
                Rule(new StructuralStirrupZoneCover { Source = StructuralStirrupZoneCover.SourceDeclared, DistanceMm = 25 }),
                6000, 10, null, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(3, made.Count);
            foreach (StructuralRebarRule rule in made)
            {
                Assert.NotNull(rule.CoverPrediction);
                Assert.Equal("declared", rule.CoverPrediction.Source);
                Assert.Equal(25, rule.CoverPrediction.CoverMm, 9);
                Assert.Equal(5, rule.CoverPrediction.BarRadiusMm, 9);
                Assert.Equal(30, rule.CoverPrediction.ClampEachEndMm, 9);
                Assert.Equal(6000, rule.CoverPrediction.HostSpanMm, 9);
                Assert.Equal(5940, rule.CoverPrediction.UsableSpanMm, 9);
                Assert.Equal(new double[] { 1, 0, 0 }, rule.CoverPrediction.Along);
            }
            // the first zone's profile is the declared outline moved by the clamp along x
            Assert.Equal(30, made[0].CurvesMm[0][0], 9);
            Assert.Equal(30, made[0].CoverPrediction.ZoneStartMm, 9);
            Assert.Equal(1030, made[0].CoverPrediction.ZoneEndMm, 9);
            Assert.Equal("start", made[0].CoverPrediction.ZoneName);
            Assert.Equal(4970, made[2].CurvesMm[0][0], 9);
            Assert.Equal(5970, made[2].CoverPrediction.ZoneEndMm, 9);
        }

        [Fact]
        public void TheProfileIsNotMovedAcrossTheSection()
        {
            // The cover moves the zones ALONG the host. The outline itself is declared
            // in model coordinates and stays exactly where it was in y and z.
            List<StructuralRebarRule> made;
            StirrupZoneRules.Expand(
                Rule(new StructuralStirrupZoneCover { Source = StructuralStirrupZoneCover.SourceDeclared, DistanceMm = 25 }),
                6000, 10, null, out made);
            Assert.Equal(-102, made[0].CurvesMm[0][1], 9);
            Assert.Equal(48, made[0].CurvesMm[0][2], 9);
            Assert.Equal(552, made[0].CurvesMm[2][2], 9);
        }

        [Fact]
        public void TheExpandedLayoutsCountTheSameBarsThePlanDid()
        {
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(
                Rule(new StructuralStirrupZoneCover { Source = StructuralStirrupZoneCover.SourceDeclared, DistanceMm = 25 }),
                6000, 10, null, out made);
            for (int i = 0; i < made.Count; i++)
            {
                RebarLayoutPlan p = RebarLayoutRules.Resolve(made[i].Layout);
                Assert.True(p.Ok, p.Error);
                Assert.Equal(r.Zones[i].Layout.NumberOfBarPositions, p.NumberOfBarPositions);
                Assert.Equal(r.Zones[i].Layout.Quantity, p.Quantity);
            }
        }
    }
}

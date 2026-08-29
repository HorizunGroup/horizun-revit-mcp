// -----------------------------------------------------------------------------
// One containment answer for a whole SET. The plan, the apply and the audit all
// call this, so the interesting cases are the ones where a set as a whole is
// worse than any single bar looks - and the ones where it must refuse.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarContainmentTests
    {
        // 4000 along X, 300 wide on Y, 600 deep on Z.
        private static HostMesh Beam()
        {
            return HostMesh.Box(new double[] { 0, -150, 0 }, new double[] { 4000, 150, 600 });
        }

        // A stirrup in the YZ plane at x = 0, 40 mm cover all round for an 8 mm radius.
        private static List<double[]> Stirrup()
        {
            return new List<double[]>
            {
                new double[] { 0, -102, 48 },
                new double[] { 0, 102, 48 },
                new double[] { 0, 102, 552 },
                new double[] { 0, -102, 552 },
                new double[] { 0, -102, 48 }
            };
        }

        private static List<double[]> StirrupAt(double x)
        {
            var outp = new List<double[]>();
            foreach (double[] p in Stirrup()) outp.Add(new[] { x, p[1], p[2] });
            return outp;
        }

        private static readonly double[] AlongX = { 1, 0, 0 };

        [Fact]
        public void ASetOfStirrupsInsideTheBeamIsInside()
        {
            var offsets = new List<double> { 100, 500, 900, 1300 };
            SetContainment c = RebarContainment.Check(Beam(), Stirrup(), offsets, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, c.Word);
            Assert.True(c.Evaluated);
            Assert.Equal(4, c.PositionsTested);
            Assert.Equal(4, c.PositionsInside);
            Assert.Empty(c.NotInsidePositions);
        }

        [Fact]
        public void OneBarOfTheSetOutsideMakesTheWholeSetNotInside()
        {
            // the last position marches past the end of the beam
            var offsets = new List<double> { 100, 1500, 3000, 4200 };
            SetContainment c = RebarContainment.Check(Beam(), Stirrup(), offsets, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.CompletelyOutside, c.Word);
            Assert.Equal(4, c.PositionsTested);
            Assert.Equal(3, c.PositionsInside);
            Assert.Equal(new List<int> { 3 }, c.NotInsidePositions);
        }

        [Fact]
        public void TheSetTakesTheWorstAnswerOfItsBarsNotTheCommonest()
        {
            // three fine, one short of cover
            var tight = new List<double[]>
            {
                new double[] { 0, -122, 48 },
                new double[] { 0, 122, 48 },
                new double[] { 0, 122, 552 },
                new double[] { 0, -122, 552 },
                new double[] { 0, -122, 48 }
            };
            SetContainment good = RebarContainment.Check(
                Beam(), Stirrup(), new List<double> { 100, 500, 900 }, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, good.Word);

            SetContainment bad = RebarContainment.Check(
                Beam(), tight, new List<double> { 100, 500, 900 }, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.InsideCoverViolated, bad.Word);
            Assert.Equal(20.0, bad.WorstCoverShortfallMm, 3);
        }

        [Fact]
        public void NegativeOffsetsMarchTheOtherWayAndAreMeasuredThere()
        {
            // MEASURED on Revit 2026: with bars_on_normal_side false the offsets come
            // back negative. From x = 2000 that is INSIDE; from x = 100 it is not.
            var atMiddle = new List<double[]>();
            foreach (double[] p in Stirrup()) atMiddle.Add(new[] { 2000.0, p[1], p[2] });
            SetContainment inside = RebarContainment.Check(
                Beam(), atMiddle, new List<double> { 0, -400, -800 }, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, inside.Word);

            SetContainment outside = RebarContainment.Check(
                Beam(), Stirrup(), new List<double> { 0, -400, -800 }, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.CompletelyOutside, outside.Word);
        }

        [Fact]
        public void NoOffsetsAtAllIsOneBarRatherThanNone()
        {
            SetContainment c = RebarContainment.Check(Beam(), StirrupAt(2000), null, AlongX, 8, 40, 1, 25);
            Assert.Equal(1, c.PositionsTested);
            Assert.Equal(SolidContainment.Inside, c.Word);
        }

        // --------------------------------------------------------- it refuses

        [Fact]
        public void NoBoundaryIsNotEvaluableRatherThanInside()
        {
            SetContainment c = RebarContainment.Check(null, Stirrup(), new List<double> { 0 }, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, c.Word);
            Assert.False(c.Evaluated);
            Assert.Contains("not a pass", c.Why);
        }

        [Fact]
        public void ADistributionDirectionThatIsNotAVectorIsRefusedWhenThereIsMoreThanOneBar()
        {
            SetContainment c = RebarContainment.Check(
                Beam(), Stirrup(), new List<double> { 0, 400 }, new double[] { 0, 0, 0 }, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, c.Word);

            // ... and is harmless for a single bar, which does not move
            SetContainment one = RebarContainment.Check(
                Beam(), StirrupAt(2000), new List<double> { 0 }, new double[] { 0, 0, 0 }, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, one.Word);
        }

        [Fact]
        public void ANonFiniteOffsetIsRefusedRatherThanSkipped()
        {
            SetContainment c = RebarContainment.Check(
                Beam(), Stirrup(), new List<double> { 0, double.NaN }, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, c.Word);
            Assert.Equal(1, c.WorstPosition);
        }

        [Fact]
        public void AnEmptyCentrelineIsRefused()
        {
            SetContainment c = RebarContainment.Check(
                Beam(), new List<double[]>(), new List<double> { 0 }, AlongX, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, c.Word);
        }

        // ------------------------------------------------------- the vocabulary

        [Fact]
        public void EveryContainmentWordMapsToOneFindingCodeAndOneSeverity()
        {
            Assert.Null(RebarContainment.FindingCodeFor(SolidContainment.Inside));
            Assert.Null(RebarContainment.SeverityFor(SolidContainment.Inside));
            Assert.Equal(RebarFinding.CoverViolated,
                RebarContainment.FindingCodeFor(SolidContainment.InsideCoverViolated));
            Assert.Equal(RebarFinding.BarPartiallyOutsideHost,
                RebarContainment.FindingCodeFor(SolidContainment.PartiallyOutside));
            Assert.Equal(RebarFinding.BarOutsideHost,
                RebarContainment.FindingCodeFor(SolidContainment.CompletelyOutside));
            Assert.Equal(RebarFinding.ContainmentNotEvaluable,
                RebarContainment.FindingCodeFor(SolidContainment.NotEvaluable));

            Assert.Equal("error", RebarContainment.SeverityFor(SolidContainment.PartiallyOutside));
            Assert.Equal("error", RebarContainment.SeverityFor(SolidContainment.InsideCoverViolated));
            Assert.Equal("unknown", RebarContainment.SeverityFor(SolidContainment.NotEvaluable));
        }

        [Fact]
        public void AnUnknownWordThrowsInBothDirectionsRatherThanReturningNull()
        {
            Assert.Throws<ArgumentException>(() => RebarContainment.FindingCodeFor("mostly_fine"));
            Assert.Throws<ArgumentException>(() => RebarContainment.SeverityFor("mostly_fine"));
        }

        [Fact]
        public void EveryCodeItCanEmitIsInThePublishedFindingVocabulary()
        {
            foreach (string w in SolidContainment.AllWords)
            {
                string code = RebarContainment.FindingCodeFor(w);
                if (code == null) continue;
                Assert.Contains(code, RebarFinding.All);
            }
        }

        // --------------------------------------------------------------- json

        [Fact]
        public void TheJsonSaysHowItWasMeasuredAndWhatItFound()
        {
            SetContainment c = RebarContainment.Check(
                Beam(), Stirrup(), new List<double> { 100, 4200 }, AlongX, 8, 40, 1, 25);
            JObject o = c.ToJson();
            Assert.Equal(SolidContainment.CompletelyOutside, (string)o["containment"]);
            Assert.Equal(2, (int)o["positions_tested"]);
            Assert.Equal(1, (int)o["positions_inside"]);
            Assert.NotNull(o["how_measured"]);
            Assert.NotNull(o["worst_point_mm"]);
            Assert.Equal(1, ((JArray)o["positions_not_inside"])[0].Value<int>());
        }

        [Fact]
        public void AnApproximatedBoundaryIsDeclaredAndSaysWhichWayItErrs()
        {
            HostMesh m = Beam();
            m.AnyCurvedFace = true;
            m.ChordToleranceMm = 6.096;
            JObject o = RebarContainment.Check(m, Stirrup(), new List<double> { 100 }, AlongX, 8, 40, 1, 25).ToJson();
            Assert.True((bool)o["boundary_is_approximated"]);
            Assert.Contains("never better", (string)o["approximation_means"]);
        }

        [Fact]
        public void ExplainSaysSomethingUsefulForEveryWord()
        {
            foreach (string w in SolidContainment.AllWords)
            {
                var c = new SetContainment { Word = w, Why = "because" };
                string text = RebarContainment.Explain(c);
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
            Assert.Contains("not measured", RebarContainment.Explain(null));
        }

        // ---------------------------------------------------------------- unit

        [Fact]
        public void UnitNormalisesAndRefusesWhatIsNotADirection()
        {
            double[] u = RebarContainment.Unit(new double[] { 0, 4000, 0 });
            Assert.Equal(0.0, u[0], 9);
            Assert.Equal(1.0, u[1], 9);
            Assert.Null(RebarContainment.Unit(new double[] { 0, 0, 0 }));
            Assert.Null(RebarContainment.Unit(new double[] { double.NaN, 1, 0 }));
            Assert.Null(RebarContainment.Unit(null));
            Assert.Null(RebarContainment.Unit(new double[] { 1, 0 }));
        }
    }
}

// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The anchored length a rule declared, against the length its bars actually have
// inside that element (acero 3.0, L1). Measured on the declared centreline by the
// plan and on the centreline Revit drew - hooks included - after the commit.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AnchorageCheckTests
    {
        private static HostMesh Footing() => HostMesh.Box(new double[] { -800, -800, -500 }, new double[] { 800, 800, 0 });
        private static HostMesh Column() => HostMesh.Box(new double[] { -200, -200, 0 }, new double[] { 200, 200, 3000 });

        private static HostMesh Union()
        {
            string why;
            HostMesh u = HostMesh.Union(new[] { Column(), Footing() }, out why);
            Assert.True(u != null, why);
            return u;
        }

        // Host first (the column, id 7), then into (the footing, id 50).
        private static readonly long[] Ids = { 7, 50 };

        private static List<double[]> Dowel(double bottomZ) =>
            new List<double[]> { new double[] { 150, 150, bottomZ }, new double[] { 150, 150, 600 } };

        private static List<StructuralAnchorage> Wants(double mm) =>
            new List<StructuralAnchorage> { new StructuralAnchorage { ElementId = 50, MinLengthMm = mm } };

        [Fact]
        public void AnEmbedmentThatReachesTheDeclaredLengthIsMet()
        {
            List<AnchorageResult> r = AnchorageCheck.Evaluate(Union(), Ids, Wants(400), Dowel(-420), null, null, 2, false, false);
            Assert.Equal(AnchorageCheck.Met, r[0].Verdict);
            Assert.Equal(420, r[0].MeasuredMm.Value, 6);
            Assert.Equal(50, r[0].ElementId);
        }

        [Fact]
        public void AShortEmbedmentIsShortWithTheNumbers()
        {
            List<AnchorageResult> r = AnchorageCheck.Evaluate(Union(), Ids, Wants(500), Dowel(-420), null, null, 2, false, false);
            Assert.Equal(AnchorageCheck.Short, r[0].Verdict);
            Assert.Contains("420", r[0].Why);
            Assert.Contains("500", r[0].Why);
        }

        [Fact]
        public void WithinTheToleranceIsMet() =>
            Assert.Equal(AnchorageCheck.Met,
                AnchorageCheck.Evaluate(Union(), Ids, Wants(421.5), Dowel(-420), null, null, 2, false, false)[0].Verdict);

        [Fact]
        public void AShortStraightLengthWithAHookDeclaredIsDecidedAfterTheCommit()
        {
            // The plan sees the declared centreline only; the hook Revit adds is measured later.
            List<AnchorageResult> r = AnchorageCheck.Evaluate(Union(), Ids, Wants(500), Dowel(-420), null, null, 2, false, true);
            Assert.Equal(AnchorageCheck.AfterCommit, r[0].Verdict);
        }

        [Fact]
        public void AfterTheCommitTheHookIsInTheCentrelineAndShortIsShort()
        {
            List<AnchorageResult> r = AnchorageCheck.Evaluate(Union(), Ids, Wants(500), Dowel(-420), null, null, 2, true, true);
            Assert.Equal(AnchorageCheck.Short, r[0].Verdict);
        }

        [Fact]
        public void TheShortestBarOfTheSetDecides()
        {
            // Bars at y = 150 and y = 190; the footing stops at y = 180, so the second bar has no
            // length in it at all, and the set is anchored as well as that bar.
            string why;
            HostMesh u = HostMesh.Union(new[] { Column(), HostMesh.Box(new double[] { -800, -800, -500 }, new double[] { 800, 180, 0 }) }, out why);
            Assert.NotNull(u);
            List<AnchorageResult> r = AnchorageCheck.Evaluate(u, Ids, Wants(400),
                Dowel(-420), new List<double> { 0, 40 }, new double[] { 0, 1, 0 }, 2, false, false);
            Assert.Equal(AnchorageCheck.Short, r[0].Verdict);
            Assert.Equal(0, r[0].MeasuredMm.Value, 6);
        }

        [Fact]
        public void NoUnionIsNotMeasuredRatherThanMet()
        {
            List<AnchorageResult> r = AnchorageCheck.Evaluate(null, Ids, Wants(400), Dowel(-420), null, null, 2, false, false);
            Assert.Equal(AnchorageCheck.NotMeasured, r[0].Verdict);
            Assert.Null(r[0].MeasuredMm);
        }

        [Fact]
        public void AnElementTheUnionDoesNotHoldIsNotMeasured()
        {
            var wants = new List<StructuralAnchorage> { new StructuralAnchorage { ElementId = 99, MinLengthMm = 400 } };
            Assert.Equal(AnchorageCheck.NotMeasured,
                AnchorageCheck.Evaluate(Union(), Ids, wants, Dowel(-420), null, null, 2, false, false)[0].Verdict);
        }

        [Fact]
        public void NothingDeclaredIsNothingChecked() =>
            Assert.Empty(AnchorageCheck.Evaluate(Union(), Ids, new List<StructuralAnchorage>(), Dowel(-420), null, null, 2, false, false));
    }
}

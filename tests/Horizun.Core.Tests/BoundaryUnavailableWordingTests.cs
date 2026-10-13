// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// MEASURED on a real model (Revit 2024): a bar declared into a beam, two round
// columns and two footings came back containment_not_evaluable with only "the
// host boundary was not available, so containment was not measured" - twice in
// one sentence, and without saying WHICH element's boundary was missing or WHY.
// The refusal has to name the element and carry the diagnosis, once.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class BoundaryUnavailableWordingTests
    {
        private static List<double[]> Bar() =>
            new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 } };

        private static int Count(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

        [Fact]
        public void AnOpenElementIsNamedWithItsDiagnosis()
        {
            HostMesh open = HostMesh.Box(new double[] { -200, -200, 0 }, new double[] { 200, 200, 3000 });
            open.Triangles.RemoveAt(0);
            MeshDiagnosis d = SolidContainment.Diagnose(open);
            Assert.False(d.Usable);

            string why = SolidContainment.UnusableBoundaryWhy(794950, d);
            Assert.Contains("element 794950", why);
            Assert.Contains("11 usable triangle(s)", why);
            Assert.Contains("0 zero-area", why);
            Assert.Contains("3 edge(s)", why);
        }

        [Fact]
        public void TheSetRefusalCarriesTheElementAndSaysItOnce()
        {
            string boundaryWhy = RebarContainment.UnionElementUnreadableWhy(794954,
                "element 794954 has geometry but no solid with volume - there is no boundary to be inside of.");
            SetContainment c = RebarContainment.Check(null, Bar(), false, null, null, 8, null, 2, 25, boundaryWhy);

            Assert.Equal(SolidContainment.NotEvaluable, c.Word);
            Assert.Contains("794954", c.Why);
            Assert.Contains("no solid with volume", c.Why);
            Assert.DoesNotContain("the host boundary was not available", c.Why);

            string said = RebarContainment.Describe(c);
            Assert.Equal(1, Count(said, "no solid with volume"));
            Assert.Equal(1, Count(said, "Unknown is not a pass"));
        }

        [Fact]
        public void WithNoReasonGivenTheGenericSentenceAppearsOnce()
        {
            SetContainment c = RebarContainment.Check(null, Bar(), false, null, null, 8, null, 2, 25, null);
            Assert.Equal(1, Count(RebarContainment.Describe(c), "the host boundary was not available"));
        }

        [Fact]
        public void AMeasuredAnswerStillCarriesItsReason()
        {
            HostMesh beam = HostMesh.Box(new double[] { -100, -150, -200 }, new double[] { 1100, 150, 200 });
            SetContainment c = RebarContainment.Check(beam,
                new List<double[]> { new double[] { 0, 0, 195 }, new double[] { 1000, 0, 195 } },
                false, null, null, 8, null, 2, 25, null);
            Assert.Equal(SolidContainment.PartiallyOutside, c.Word);
            string said = RebarContainment.Describe(c);
            Assert.Contains("mm of steel is outside the host", said);
            Assert.Contains(c.Why, said);
        }
    }
}

// -----------------------------------------------------------------------------
// A stirrup stretched from 220 square to 300 square keeps its bar type, its host,
// its quantity, its array length and its shape id. Before CompareGeometry the
// audit compared all five and agreed. These are the cases it now catches, and
// the ones it must NOT invent - a corner Revit filleted is not a difference.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarGeometryAuditTests
    {
        private static List<double[]> Square(double half)
        {
            return new List<double[]>
            {
                new double[] { 0, -half, -half },
                new double[] { 0, half, -half },
                new double[] { 0, half, half },
                new double[] { 0, -half, half },
                new double[] { 0, -half, -half }
            };
        }

        private static List<string> Codes(JArray f)
        {
            return f.OfType<JObject>().Select(x => (string)x["code"]).ToList();
        }

























        // ------------------------------------------------------------- parsing

        [Fact]
        public void PointsOfReadsAWellFormedArrayAndRefusesAnythingElse()
        {
            var ok = JArray.Parse("[[1,2,3],[4,5,6.5]]");
            List<double[]> parsed = RebarAuditRules.PointsOf(ok);
            Assert.Equal(2, parsed.Count);
            Assert.Equal(6.5, parsed[1][2]);

            Assert.Null(RebarAuditRules.PointsOf(JArray.Parse("[[1,2]]")));
            Assert.Null(RebarAuditRules.PointsOf(JArray.Parse("[[1,2,\"x\"]]")));
            Assert.Null(RebarAuditRules.PointsOf(JArray.Parse("[]")));
            Assert.Null(RebarAuditRules.PointsOf(null));
            Assert.Null(RebarAuditRules.PointsOf(new JObject()));
        }













        // D1, option A (owner, measured 4.75 mm alike in Revit 2024-2027): a stirrup declared
        // CLOSED with a hook at both ends comes back from Revit's as-declared form OPEN at its
        // first corner, the corner moved half the stirrup's diameter. Accepted only there and
        // only within one stirrup diameter, published as data, never absorbed elsewhere.
        private static List<double[]> HookedSquare(double half, double gap)
        {
            return new List<double[]>
            {
                new double[] { 0, -half + gap, -half },
                new double[] { 0, half, -half },
                new double[] { 0, half, half },
                new double[] { 0, -half, half },
                new double[] { 0, -half, -half + gap }
            };
        }




    }
}

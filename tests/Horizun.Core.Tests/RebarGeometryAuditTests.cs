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

        // ------------------------------------------------------- it stays quiet

        [Fact]
        public void AnIdenticalCentrelineProducesNoFinding()
        {
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, Square(110), Square(110), true, 1.0, 0);
            Assert.Empty(f);
        }

        [Fact]
        public void ADeclarationWithNoCentrelineIsNotAFinding()
        {
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, null, Square(110), true, 1.0, 0);
            Assert.Empty(f);
        }

        [Fact]
        public void ACornerRevitFilletedIsNotADifference()
        {
            // The declaration draws a sharp corner; Revit draws an arc of radius R,
            // which leaves the declared corner 0.41 R off the path. With the bend
            // allowance that is agreement; without it, it is a false alarm on every
            // stirrup ever built.
            double r = 64;
            var drawn = new List<double[]>
            {
                new double[] { 0, -110, -110 + r },
                new double[] { 0, -110 + r * 0.293, -110 + r * 0.293 },  // the arc, roughly
                new double[] { 0, -110 + r, -110 },
                new double[] { 0, 110, -110 },
                new double[] { 0, 110, 110 },
                new double[] { 0, -110, 110 },
                new double[] { 0, -110, -110 + r }
            };
            double allowance = 0.4143 * r;

            var withAllowance = new JArray();
            RebarAuditRules.CompareGeometry(withAllowance, "R1", 7, Square(110), drawn, true, 1.0, r * 2);
            Assert.Empty(withAllowance);

            var without = new JArray();
            RebarAuditRules.CompareGeometry(without, "R1", 7, Square(110), drawn, true, 1.0, 0);
            Assert.Contains(RebarFinding.GeometryDiffers, Codes(without));
            Assert.True(allowance > 20);   // the allowance is not a rounding error
        }

        // ------------------------------------------------- it catches the reshape

        [Fact]
        public void AStirrupStretchedToTheSameLengthIsCaught()
        {
            // 220 square and 300x140 have the same perimeter. Every other property
            // the audit compares - type, host, quantity, array length, shape id and
            // total steel - is unchanged.
            var declared = Square(110);
            var stretched = new List<double[]>
            {
                new double[] { 0, -150, -70 },
                new double[] { 0, 150, -70 },
                new double[] { 0, 150, 70 },
                new double[] { 0, -150, 70 },
                new double[] { 0, -150, -70 }
            };
            Assert.Equal(RebarPlanRules.CentrelineLengthMm(declared, false),
                         RebarPlanRules.CentrelineLengthMm(stretched, false), 6);

            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, declared, stretched, true, 1.0, 0);
            Assert.Contains(RebarFinding.GeometryDiffers, Codes(f));
        }

        [Fact]
        public void AMovedIntermediatePointIsCaught()
        {
            var declared = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { 1000, 0, 0 },
                new double[] { 2000, 0, 0 }
            };
            var moved = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { 1000, 90, 0 },
                new double[] { 2000, 0, 0 }
            };
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, declared, moved, false, 1.0, 0);
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.GeometryDiffers);
            Assert.Equal("error", (string)one["severity"]);
        }

        [Fact]
        public void AZigzagThroughTheSameEndPointsIsCaught()
        {
            // Comparing each point to the nearest VERTEX of the other polyline
            // would pass this; comparing to the nearest point of the PATH does not.
            var straight = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { 2000, 0, 0 }
            };
            var zigzag = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { 500, 60, 0 },
                new double[] { 1000, -60, 0 },
                new double[] { 1500, 60, 0 },
                new double[] { 2000, 0, 0 }
            };
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, straight, zigzag, false, 1.0, 0);
            Assert.Contains(RebarFinding.GeometryDiffers, Codes(f));
        }

        // -------------------------------------------------------- the reversal

        [Fact]
        public void ABarDrawnEndForEndIsReportedAsReversedRatherThanAsAShapeDifference()
        {
            var declared = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { 500, 0, 0 },
                new double[] { 2000, 300, 0 }
            };
            var reversed = new List<double[]>(declared);
            reversed.Reverse();

            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, declared, reversed, false, 1.0, 0);
            Assert.Single(f);
            Assert.Equal(RebarFinding.GeometryReversed, (string)((JObject)f[0])["code"]);
            Assert.Equal("error", (string)((JObject)f[0])["severity"]);
        }

        [Fact]
        public void AClosedShapeIsNotAccusedOfBeingReversed()
        {
            // A closed loop drawn the other way round passes through exactly the
            // same points and has no start to be at the wrong end.
            var declared = Square(110);
            var other = new List<double[]>(declared);
            other.Reverse();
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, declared, other, true, 1.0, 0);
            Assert.DoesNotContain(RebarFinding.GeometryReversed, Codes(f));
        }

        // ------------------------------------------------------------ the plane

        [Fact]
        public void AStirrupLyingInADifferentPlaneIsNamedAsSuch()
        {
            var declared = Square(110);
            // the same square, turned 30 degrees about the Z axis
            double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
            var turned = declared.Select(p => new[] { p[0] * c - p[1] * s, p[0] * s + p[1] * c, p[2] })
                                 .ToList();
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, declared, turned, true, 1.0, 0);
            Assert.Contains(RebarFinding.PlaneDiffers, Codes(f));
        }

        [Fact]
        public void AStraightBarHasNoPlaneAndIsNotAccusedOfHavingTheWrongOne()
        {
            var a = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 2000, 0, 0 } };
            var b = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 2000, 0, 0 } };
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, a, b, false, 1.0, 0);
            Assert.DoesNotContain(RebarFinding.PlaneDiffers, Codes(f));
            Assert.Null(RebarPlanRules.BestFitNormal(a));
        }

        // ------------------------------------------------------- unreadable

        [Fact]
        public void ACentrelineTheModelWouldNotGiveIsUnknownRatherThanAgreement()
        {
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, Square(110), null, true, 1.0, 0);
            // An unknown is published under `unreadable`, with `about` naming what
            // could not be read - the house convention, so a reader never has to
            // tell "different" from "not looked at" by reading a severity field.
            Assert.Single(f);
            Assert.Equal(RebarFinding.Unreadable, (string)((JObject)f[0])["code"]);
            Assert.Equal(RebarFinding.GeometryDiffers, (string)((JObject)f[0])["about"]);
            Assert.Equal("unknown", (string)((JObject)f[0])["severity"]);
        }

        [Fact]
        public void ANonFinitePointIsRefusedRatherThanCompared()
        {
            var bad = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { double.NaN, 0, 0 }
            };
            var good = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 100, 0, 0 } };
            var f = new JArray();
            RebarAuditRules.CompareGeometry(f, "R1", 7, good, bad, false, 1.0, 0);
            Assert.Single(f);
            Assert.Equal("unknown", (string)((JObject)f[0])["severity"]);
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

        [Fact]
        public void TheBendAllowanceComesFromTheStyleTheRuleDeclares()
        {
            var expectedStandard = new JObject { ["style"] = StructuralStyle.Standard };
            var expectedStirrup = new JObject { ["style"] = StructuralStyle.StirrupTie };
            var observed = new JObject
            {
                ["bar_type"] = new JObject
                {
                    ["standard_bend_diameter_mm"] = 100.0,
                    ["stirrup_tie_bend_diameter_mm"] = 40.0
                }
            };
            Assert.Equal(0.4143 * 50, RebarAuditRules.BendAllowanceMm(expectedStandard, observed), 6);
            Assert.Equal(0.4143 * 20, RebarAuditRules.BendAllowanceMm(expectedStirrup, observed), 6);
        }

        [Fact]
        public void ABendDiameterTheModelWillNotGiveMeansNoAllowanceRatherThanAGuess()
        {
            var expected = new JObject { ["style"] = StructuralStyle.Standard };
            Assert.Equal(0, RebarAuditRules.BendAllowanceMm(expected, new JObject()));
            Assert.Equal(0, RebarAuditRules.BendAllowanceMm(expected, new JObject
            {
                ["bar_type"] = new JObject { ["standard_bend_diameter_mm"] = JValue.CreateNull() }
            }));
        }

        // ----------------------------------------------------- the distance itself

        [Fact]
        public void TheDistanceToAPathIsToTheNearestPointOfItNotItsNearestVertex()
        {
            var path = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 } };
            Assert.Equal(0.0, RebarAuditRules.DistanceToPath(new double[] { 500, 0, 0 }, path, false), 9);
            Assert.Equal(30.0, RebarAuditRules.DistanceToPath(new double[] { 500, 30, 0 }, path, false), 9);
            Assert.Equal(100.0, RebarAuditRules.DistanceToPath(new double[] { 1100, 0, 0 }, path, false), 9);
        }

        [Fact]
        public void AClosedPathIncludesTheSegmentThatShutsIt()
        {
            var triangle = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { 100, 0, 0 },
                new double[] { 50, 100, 0 }
            };
            // A point beside the closing edge is ON the closed path and 43.x mm from
            // the open one.
            double closed = RebarAuditRules.DistanceToPath(new double[] { 25, 50, 0 }, triangle, true);
            double open = RebarAuditRules.DistanceToPath(new double[] { 25, 50, 0 }, triangle, false);
            Assert.True(closed < 1e-9);
            Assert.True(open > 20);
        }

        [Fact]
        public void TheAngleToleranceFollowsTheDeclaredLengthToleranceAndTheBarsOwnReach()
        {
            // A looser length tolerance is a looser angle, at any size.
            Assert.True(RebarAuditRules.AngleToleranceDegrees(1.0, 1000) <
                        RebarAuditRules.AngleToleranceDegrees(10.0, 1000));
            Assert.True(RebarAuditRules.AngleToleranceDegrees(1.0, 1000) > 0);

            // And the SAME length tolerance is a wider angle on a smaller bar,
            // because the lever it acts over is shorter. A fixed one-metre lever
            // made 1 mm five times stricter than declared on a 220 mm stirrup.
            Assert.True(RebarAuditRules.AngleToleranceDegrees(1.0, 220) >
                        RebarAuditRules.AngleToleranceDegrees(1.0, 4000));
        }

        [Fact]
        public void TheReachOfAPolylineIsMeasuredFromItsOwnCentroid()
        {
            var open = new List<double[]>
            {
                new double[] { 0, -110, -110 }, new double[] { 0, 110, -110 },
                new double[] { 0, 110, 110 }, new double[] { 0, -110, 110 }
            };
            // corners of a 220 square are 110*sqrt(2) from its centre
            Assert.Equal(110 * System.Math.Sqrt(2), RebarAuditRules.Reach(open), 6);

            // Square() repeats its first point to close the loop, which pulls the
            // centroid off centre - a real property of the polyline, not a bug, and
            // the reach is measured from where the points actually are.
            Assert.Equal(132 * System.Math.Sqrt(2), RebarAuditRules.Reach(Square(110)), 6);
            Assert.Equal(0, RebarAuditRules.Reach(null));
            Assert.Equal(0, RebarAuditRules.Reach(new List<double[]>()));
        }
    }
}

// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The built arc's extent is measured with its ORIENTED sweep, not |a1 - a0|, which
// reads a 270 degree arc as 90 (audit 2026-10-08, lote 7).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ArcExtentSweepTests
    {
        private const double R = 5000.0;

        private static CadCandidate Drawn270() => new CadCandidate
        {
            Arc = new CadArcFact("a1", new CadPoint(0, 0, 0), R, new CadPoint(R, 0, 0), new CadPoint(0, -R, 0),
                                 new CadPoint(-R, 0, 0), "A-WALL", 12, 5.0)
        };

        private static CadAuditSubject Built(double? sweep) => new CadAuditSubject
        {
            Geometry = new List<CadPoint> { new CadPoint(R, 0, 0), new CadPoint(0, -R, 0) },
            ArcCentre = new CadPoint(0, 0, 0), ArcRadiusMm = R, ArcSweepRadians = sweep
        };

        [Fact]
        public void A_270_degree_arc_built_as_drawn_has_no_extent_difference()
        {
            Assert.Equal(0.0, CadAuditRules.ArcExtentDifference(Drawn270(), Built(1.5 * Math.PI)).Value, 6);
            // Without the built sweep (an older reading), the endpoints are read the way that agrees
            // with the drawn orientation instead of |a1 - a0| = 90 degrees.
            Assert.Equal(0.0, CadAuditRules.ArcExtentDifference(Drawn270(), Built(null)).Value, 6);
        }

        [Fact]
        public void The_minor_arc_built_through_the_same_ends_is_reported()
        {
            double diff = CadAuditRules.ArcExtentDifference(Drawn270(), Built(0.5 * Math.PI)).Value;
            Assert.Equal(Math.PI * R, diff, 3);
        }
    }
}

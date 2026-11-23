// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// Interpretation, pinned. These assert the thing that makes the DWG program
// defensible: that a reading which cannot be justified is REFUSED rather than
// built, and that every score ships with the measurement behind it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadInterpretationTests
    {
        private const string Hash = "sha-of-the-drawing";



        private static string WallRule(double minConfidence = 0.0, string layers = "[\"A-WALL*\"]") =>
            @"[{ 'id': 'walls', 'precedence': 10, 'layers': LAYERS, 'produces': 'wall',
                 'category': 'OST_Walls', 'family_type': 'Basic Wall: Generic - 200mm',
                 'height_mm': 3000, 'min_confidence': CONF,
                 'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500,
                               'min_overlap_mm': 300, 'min_overlap_fraction': 0.5 } }]"
              .Replace('\'', '"').Replace("LAYERS", layers)
              .Replace("CONF", minConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer) =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

































        [Fact]
        public void A_narrower_pairing_alongside_but_OUTSIDE_the_band_stays_its_own_wall()
        {
            var outerA = new CadDoubleLine(new CadPoint(0, 0), new CadPoint(6000, 0), 352.4, "L", 6000, 1, 0, 0, 1);
            var insideIt = new CadDoubleLine(new CadPoint(0, -39.7), new CadPoint(6000, -39.7), 247.7, "L", 6000, 1, 0, 2, 3);
            var besideIt = new CadDoubleLine(new CadPoint(0, 900), new CadPoint(6000, 900), 247.7, "L", 6000, 1, 0, 4, 5);
            var crossingElsewhere = new CadDoubleLine(new CadPoint(20000, 0), new CadPoint(26000, 0), 100, "L", 6000, 1, 0, 6, 7);

            Assert.True(CadTopologyRules.IsInnerBoundaryOf(insideIt, outerA, 2, 1));
            Assert.False(CadTopologyRules.IsInnerBoundaryOf(besideIt, outerA, 2, 1));
            Assert.False(CadTopologyRules.IsInnerBoundaryOf(crossingElsewhere, outerA, 2, 1));
            // and never the other way round: the wider one is not inside the narrower
            Assert.False(CadTopologyRules.IsInnerBoundaryOf(outerA, insideIt, 2, 1));
        }












    }
}

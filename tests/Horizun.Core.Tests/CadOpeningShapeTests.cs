// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE RING IS MEASURED BEFORE IT IS DESCRIBED.
//
// The slab-opening arm carried a comment saying that a rectangle converts exactly
// and anything else does not, "because approximating it would cut a hole the
// drawing does not show" - and then took the bounding box of whatever arrived.
//
// A 300 mm circular penetration became a 300x300 mm square: 27% more slab removed
// than was drawn, through a floor somebody has to stand on. An L-shaped riser had
// the corner that should stay solid cut out of it. The plan said `rectangular`,
// the create row agreed, and the verification agreed with the create row - three
// answers in perfect agreement with each other and none of them with the drawing.
//
// Revit's own typed opening takes a rectangle OR a circle, so both are now
// converted exactly and everything else is refused, which is what the comment
// always claimed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadOpeningShapeTests
    {
        private static CadRequirementSet Set()
        {
            return CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'holes', 'version': '1.0.0', 'title': 'Holes' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 2.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'opening',
                          'category': 'OST_ShaftOpening', 'level': 'Level 1',
                          'geometry': { 'from': 'closed_loops', 'min_area_mm2': 1000 } }]
            }".Replace('\'', '"')));
        }

        private static List<CadSegment> Chain(params CadPoint[] ring)
        {
            var segs = new List<CadSegment>();
            for (int i = 0; i < ring.Length; i++)
                segs.Add(new CadSegment(ring[i], ring[(i + 1) % ring.Length], "A-HOLE"));
            return segs;
        }

        private static List<CadSegment> Rectangle(double x0, double y0, double x1, double y1)
        {
            return Chain(new CadPoint(x0, y0), new CadPoint(x1, y0),
                         new CadPoint(x1, y1), new CadPoint(x0, y1));
        }

        /// <summary>A circle as a DWG carries one: a closed run of chords.</summary>
        private static List<CadSegment> Circle(double cx, double cy, double radius, int sides = 24)
        {
            var ring = new List<CadPoint>();
            for (int i = 0; i < sides; i++)
            {
                double a = 2 * Math.PI * i / sides;
                ring.Add(new CadPoint(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
            }
            return Chain(ring.ToArray());
        }

        private static JObject FirstRow(List<CadSegment> segs)
        {
            CadRequirementSet set = Set();
            CadInterpretation r = CadInterpretationRules.Interpret(segs, set, "sha");
            CadConversionPlan plan = CadConversionPlanRules.Plan(r, set, "fp", true);
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");
            return requests.Count == 0 ? null : (JObject)((JArray)requests[0]["elements"])[0];
        }

        [Fact]
        public void A_rectangle_converts_exactly()
        {
            JObject row = FirstRow(Rectangle(1000, 2000, 3000, 5000));

            Assert.Equal("rectangular", (string)row["shape"]);
            Assert.Equal(2000.0, (double)row["width"], 1);
            Assert.Equal(3000.0, (double)row["height"], 1);
        }

        [Fact]
        public void A_CIRCLE_is_built_as_a_circle_and_not_as_the_square_around_it()
        {
            // The defect: a 300 mm penetration came out as a 300x300 mm square,
            // 27% more slab removed than the drawing shows.
            JObject row = FirstRow(Circle(5000, 5000, 150));

            Assert.NotNull(row);
            Assert.Equal("circular", (string)row["shape"]);
            Assert.Equal(300.0, (double)row["diameter"], 0);
            Assert.Null(row["width"]);
        }

        [Fact]
        public void An_L_SHAPED_ring_is_refused_rather_than_squared_off()
        {
            // The bounding box of an L includes the corner that must stay solid.
            // There is no typed opening for this shape, and inventing one by
            // cutting the box is the approximation the comment always disowned.
            JObject row = FirstRow(Chain(
                new CadPoint(0, 0), new CadPoint(4000, 0), new CadPoint(4000, 2000),
                new CadPoint(2000, 2000), new CadPoint(2000, 4000), new CadPoint(0, 4000)));

            Assert.Null(row);
        }

        [Fact]
        public void A_ROTATED_rectangle_is_refused_too()
        {
            // Its bounding box is bigger than it is in both directions, so the
            // hole cut would be larger than the one drawn on every side.
            JObject row = FirstRow(Chain(
                new CadPoint(0, 1000), new CadPoint(1000, 0),
                new CadPoint(3000, 2000), new CadPoint(2000, 3000)));

            Assert.Null(row);
        }

        [Fact]
        public void A_TRIANGLE_inside_a_box_does_not_pass_as_that_box()
        {
            // Every vertex sits on a corner of the bounding box - three of the
            // four - so a check that only asked "is every point on a corner"
            // would call this a rectangle.
            JObject row = FirstRow(Chain(
                new CadPoint(0, 0), new CadPoint(4000, 0), new CadPoint(0, 3000)));

            Assert.Null(row);
        }

        [Fact]
        public void A_rectangle_whose_SIDE_WAS_SPLIT_is_still_a_rectangle()
        {
            // A ring is a chain of drawn segments, and a drawing splits a side
            // whenever anything touched it - a dimension witness, a trimmed line,
            // an earlier edit. Refusing that would refuse most real drawings.
            JObject row = FirstRow(Chain(
                new CadPoint(0, 0), new CadPoint(2000, 0), new CadPoint(4000, 0),
                new CadPoint(4000, 3000), new CadPoint(0, 3000)));

            Assert.Equal("rectangular", (string)row["shape"]);
            Assert.Equal(4000.0, (double)row["width"], 1);
            Assert.Equal(3000.0, (double)row["height"], 1);
        }

        [Fact]
        public void A_square_is_not_mistaken_for_a_circle()
        {
            // Its four corners are all equidistant from the centre, which is what
            // makes "every vertex the same distance out" too weak a test on its own.
            JObject row = FirstRow(Rectangle(0, 0, 2000, 2000));

            Assert.Equal("rectangular", (string)row["shape"]);
        }
    }
}

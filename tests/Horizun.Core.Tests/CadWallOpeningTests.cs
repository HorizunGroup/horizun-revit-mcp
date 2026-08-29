// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A HOLE IN A WALL IS A THIRD KIND OF HOLE.
//
// `opening` cuts one floor. `shaft` cuts every floor between two storeys. Neither
// is a hole in a wall, which is cut into the ONE wall it is hosted in between two
// HEIGHTS - and a plan drawing carries neither of them. It shows where the hole is
// along the wall and says nothing at all about how high it starts or stops.
//
// So the rule supplies both, the way `height_mm` already supplies a wall's height,
// and refuses without them. That refusal is the point of this file: a hole at a
// height nobody chose is invisible in the plan it was drawn on, and would be found
// by somebody standing in the building with a tape measure.
//
// The backlog scoped this as the other half of 8.4 and the phase that closed 8.4
// closed only the slab half.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallOpeningTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static string SetJson(string produces, string extra)
        {
            return @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'holes', 'version': '1.0.0', 'title': 'Holes' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'PRODUCES',
                          'category': 'OST_SWallRectOpening'EXTRA,
                          'geometry': { 'from': 'closed_loops', 'min_area_mm2': 100 } }]
            }".Replace('\'', '"').Replace("PRODUCES", produces).Replace("EXTRA", extra);
        }

        private static CadRequirementSet Set(string extra = ", 'sill_height_mm': 900, 'head_height_mm': 2100",
                                             string produces = "wall_opening")
        {
            return CadRequirementSet.Load(JObject.Parse(SetJson(produces, extra)));
        }

        private static CadRequirementSetException Refused(string produces, string extra)
        {
            return Assert.Throws<CadRequirementSetException>(
                () => CadRequirementSet.Load(JObject.Parse(SetJson(produces, extra))));
        }

        /// <summary>A closed rectangle - how a drawing says "a hole here".</summary>
        private static List<CadSegment> Ring(double x0, double y0, double x1, double y1)
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(x0, y0), new CadPoint(x1, y0), "A-HOLE"),
                new CadSegment(new CadPoint(x1, y0), new CadPoint(x1, y1), "A-HOLE"),
                new CadSegment(new CadPoint(x1, y1), new CadPoint(x0, y1), "A-HOLE"),
                new CadSegment(new CadPoint(x0, y1), new CadPoint(x0, y0), "A-HOLE")
            };
        }

        private static JObject FirstRow(CadRequirementSet set, List<CadSegment> segs)
        {
            CadInterpretation r = CadInterpretationRules.Interpret(segs, set, Sha);
            CadConversionPlan plan = CadConversionPlanRules.Plan(r, set, "fp", true);
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");
            return requests.Count == 0 ? null : (JObject)((JArray)requests[0]["elements"])[0];
        }

        // ------------------------------------------------------- it is its own kind

        [Fact]
        public void A_wall_opening_is_its_OWN_kind_and_not_a_flavour_of_the_slab_one()
        {
            JObject row = FirstRow(Set(), Ring(1000, 0, 2000, 200));

            Assert.Equal("wall_opening", (string)row["kind"]);
            Assert.Null(row["shape"]);     // that belongs to the slab opening
            Assert.Null(row["center"]);
        }

        [Fact]
        public void A_requirement_set_can_now_ASK_for_one_at_all()
        {
            // It could not before: wall_opening existed in create_elements and no
            // produces value reached it, so a hole in a wall was a direct call and
            // never a conversion.
            Assert.Contains("wall_opening", CadConversionPlanRules.CreateKinds);
        }

        // --------------------------------------------------------- the two heights

        [Fact]
        public void It_carries_the_two_HEIGHTS_the_drawing_cannot_give()
        {
            JObject row = FirstRow(Set(), Ring(1000, 0, 2000, 200));

            Assert.Equal(900.0, (double)row["corner_1"][2], 3);
            Assert.Equal(2100.0, (double)row["corner_2"][2], 3);
        }

        [Fact]
        public void And_the_two_corners_span_the_ring_the_drawing_DID_give()
        {
            JObject row = FirstRow(Set(), Ring(1000, 0, 2000, 200));

            Assert.Equal(1000.0, (double)row["corner_1"][0], 3);
            Assert.Equal(2000.0, (double)row["corner_2"][0], 3);
        }

        [Fact]
        public void A_rule_that_names_only_ONE_height_is_refused()
        {
            CadRequirementSetException e = Refused("wall_opening", ", 'sill_height_mm': 900");
            Assert.Contains("head_height_mm", e.Message);
            Assert.Contains("says nothing about how high", e.Message);
        }

        [Fact]
        public void A_rule_that_names_NEITHER_is_refused_rather_than_defaulted()
        {
            // A default would be a hole at a height nobody chose, and it would look
            // entirely correct in the plan it was drawn on.
            CadRequirementSetException e = Refused("wall_opening", "");
            Assert.Contains("sill_height_mm", e.Message);
        }

        [Fact]
        public void A_head_at_or_below_the_sill_is_refused()
        {
            CadRequirementSetException e = Refused(
                "wall_opening", ", 'sill_height_mm': 2100, 'head_height_mm': 900");
            Assert.Contains("no height and cut nothing", e.Message);
        }

        [Fact]
        public void Only_a_wall_opening_may_declare_that_pair()
        {
            // On anything else the two numbers reach a builder that ignores them,
            // and sit in the set reading as a decision somebody made.
            CadRequirementSetException e = Refused("wall", ", 'sill_height_mm': 900, 'head_height_mm': 2100");
            Assert.Contains("sill_height_mm", e.Message);
            Assert.Contains("no such pair", e.Message);
        }

        // ------------------------------------------- a wall that is not on an axis

        /// <summary>A hole L long and W across, drawn ALONG a wall at this angle.</summary>
        private static List<CadSegment> Aligned(double degrees, double lengthMm = 1000, double widthMm = 200)
        {
            double t = degrees * System.Math.PI / 180.0;
            double ux = System.Math.Cos(t), uy = System.Math.Sin(t);
            double nx = -uy, ny = ux;
            var ring = new List<CadPoint>();
            foreach (var ab in new[] { new[] { -lengthMm / 2, -widthMm / 2 }, new[] { lengthMm / 2, -widthMm / 2 },
                                       new[] { lengthMm / 2, widthMm / 2 }, new[] { -lengthMm / 2, widthMm / 2 } })
                ring.Add(new CadPoint(ab[0] * ux + ab[1] * nx, ab[0] * uy + ab[1] * ny));

            var segs = new List<CadSegment>();
            for (int i = 0; i < ring.Count; i++)
                segs.Add(new CadSegment(ring[i], ring[(i + 1) % ring.Count], "A-HOLE"));
            return segs;
        }

        /// <summary>How far the emitted corners span ALONG a wall at this angle - which is the hole Revit cuts.</summary>
        private static double SpanAlong(JObject row, double degrees)
        {
            double t = degrees * System.Math.PI / 180.0;
            double dx = (double)row["corner_2"][0] - (double)row["corner_1"][0];
            double dy = (double)row["corner_2"][1] - (double)row["corner_1"][1];
            return System.Math.Abs(dx * System.Math.Cos(t) + dy * System.Math.Sin(t));
        }

        [Fact]
        public void A_hole_on_a_wall_that_is_not_on_an_axis_is_still_the_size_it_was_drawn()
        {
            // MEASURED, and the reason this test exists: the first version emitted
            // the ring's BOUNDING BOX diagonal with a comment claiming that worked
            // for a wall in any direction. It does not. A 1000 mm hole came out as
            // 1200 mm at +45 degrees, 500 mm at -30, and EXACTLY ZERO at -45, where
            // that diagonal is perpendicular to the wall. Every check downstream
            // agreed with it, because they all compare the model against the plan.
            foreach (double angle in new[] { 0.0, 15.0, 30.0, 45.0, -15.0, -30.0, -45.0, -60.0, 90.0 })
            {
                JObject row = FirstRow(Set(), Aligned(angle));
                Assert.True(row != null, "no row at " + angle + " degrees");
                Assert.Equal(1000.0, SpanAlong(row, angle), 0);
            }
        }

        [Fact]
        public void A_ring_that_is_not_a_rectangle_is_refused_rather_than_squared_off()
        {
            // A hole cut to a bounding box takes out the corner an L-shaped one
            // leaves solid, and Revit's rectangular opening cannot cut an L.
            JObject row = FirstRow(Set(), new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(4000, 0), "A-HOLE"),
                new CadSegment(new CadPoint(4000, 0), new CadPoint(4000, 2000), "A-HOLE"),
                new CadSegment(new CadPoint(4000, 2000), new CadPoint(2000, 2000), "A-HOLE"),
                new CadSegment(new CadPoint(2000, 2000), new CadPoint(2000, 4000), "A-HOLE"),
                new CadSegment(new CadPoint(2000, 4000), new CadPoint(0, 4000), "A-HOLE"),
                new CadSegment(new CadPoint(0, 4000), new CadPoint(0, 0), "A-HOLE")
            });

            Assert.Null(row);
        }

        // -------------------------------------------------------------- the host

        [Fact]
        public void It_says_WHAT_it_needs_to_be_hosted_in_and_where_to_look()
        {
            // Which wall cannot be answered here - this layer has no document - so
            // the row states the kind of host and the point to resolve it from.
            JObject row = FirstRow(Set(), Ring(1000, 0, 2000, 200));

            Assert.Equal("wall", (string)row["hosted_on"]);
            Assert.NotNull(row["host_point"]);
            Assert.Equal(1500.0, (double)row["host_point"][0], 3);
            Assert.Equal(100.0, (double)row["host_point"][1], 3);
        }

        [Fact]
        public void The_host_point_sits_at_the_RING_s_height_and_not_at_the_sill()
        {
            // Which wall a hole belongs to is a question in PLAN, and the host
            // search measures a 3D distance to the wall's location curve. Carrying
            // the sill here put the point 900 mm above that curve, so a ring drawn
            // dead on a wall came back host_too_far by exactly the sill height -
            // and the refusal blamed the drawing.
            JObject row = FirstRow(Set(), Ring(1000, 0, 2000, 200));

            Assert.Equal(0.0, (double)row["host_point"][2], 3);
            Assert.Equal(900.0, (double)row["corner_1"][2], 3);
        }

        [Fact]
        public void Permission_to_cut_a_load_bearing_wall_travels_only_when_the_set_gave_it()
        {
            JObject silent = FirstRow(Set(), Ring(1000, 0, 2000, 200));
            Assert.Null(silent["allow_structural"]);

            JObject given = FirstRow(
                Set(", 'sill_height_mm': 900, 'head_height_mm': 2100, 'allow_structural': true"),
                Ring(1000, 0, 2000, 200));
            Assert.True((bool)given["allow_structural"]);
        }

        // ------------------------------------------------------------ still verifiable

        [Fact]
        public void Every_kind_this_adds_is_still_re_readable_after_the_commit()
        {
            // The same coupling that caught shaft and room_separator: a kind a set
            // can ask for and the verification switch does not know builds happily
            // and reports itself unverified.
            Assert.Contains("wall_opening", CadConversionPlanRules.CreateKinds);
        }
    }
}

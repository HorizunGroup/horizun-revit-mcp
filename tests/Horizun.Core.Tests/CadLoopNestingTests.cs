// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// FLOORS WITH HOLES, AND ROOMS.
//
// Two defects lived here for a long time and neither could be seen from the
// outside, because both failed LOUDLY at create time rather than building
// something wrong:
//
//   the profile was emitted as a FLAT array of points where
//   horizun_create_elements reads an array OF LOOPS, so every floor, ceiling and
//   roof this plan ever produced was refused;
//
//   and a room went through the profile arm, while create_elements places a room
//   by a POINT and ignores profile entirely.
//
// The tests below are what stops either coming back, and what pins the two
// judgements that are easy to get subtly wrong: which ring is a hole, and where
// the inside of a room actually is.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadLoopNestingTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static CadRequirementSet Set(string produces = "floor", string category = "OST_Floors")
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'slabs', 'version': '1.0.0', 'title': 'Slabs' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'slabs', 'precedence': 10, 'layers': ['A-SLAB*'], 'produces': 'PRODUCES',
                          'category': 'CATEGORY',
                          'geometry': { 'from': 'closed_loops' } }]
            }".Replace('\'', '"').Replace("PRODUCES", produces).Replace("CATEGORY", category);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer = "A-SLAB") =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

        /// <summary>A closed rectangle as four segments.</summary>
        private static IEnumerable<CadSegment> Rect(double x0, double y0, double x1, double y1,
                                                    string layer = "A-SLAB")
        {
            yield return Seg(x0, y0, x1, y0, layer);
            yield return Seg(x1, y0, x1, y1, layer);
            yield return Seg(x1, y1, x0, y1, layer);
            yield return Seg(x0, y1, x0, y0, layer);
        }

        // ------------------------------------------------------------- nesting

        [Fact]
        public void A_ring_inside_another_is_a_HOLE_not_a_second_slab()
        {
            var segs = new List<CadSegment>();
            segs.AddRange(Rect(0, 0, 10000, 8000));         // the slab
            segs.AddRange(Rect(3000, 3000, 5000, 5000));    // a shaft through it

            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(), Sha);
            CadCandidate c = Assert.Single(r.Candidates);
            Assert.Single(c.Holes);
            // The area that will EXIST: 80 m2 outline less the 4 m2 shaft.
            Assert.Equal(76_000_000.0, c.AreaMm2.Value, 0);
            Assert.Contains(c.Assumptions, a => a.Contains("read as a hole"));
        }

        [Fact]
        public void A_hole_is_wound_the_OTHER_WAY_from_its_outer_ring()
        {
            // Revit reads a ring's direction as the statement of whether it adds
            // material or removes it. A hole wound the same way as its outer ring
            // is a second slab standing in the opening.
            var segs = new List<CadSegment>();
            segs.AddRange(Rect(0, 0, 10000, 8000));
            segs.AddRange(Rect(3000, 3000, 5000, 5000));

            CadCandidate c = CadInterpretationRules.Interpret(segs, Set(), Sha).Candidates.Single();
            Assert.True(SignedArea(c.Geometry) > 0, "the outer ring must read counter-clockwise");
            Assert.True(SignedArea(c.Holes[0]) < 0, "a hole must read clockwise");
        }

        [Fact]
        public void TWO_holes_in_one_slab_are_both_carried()
        {
            var segs = new List<CadSegment>();
            segs.AddRange(Rect(0, 0, 20000, 10000));
            segs.AddRange(Rect(2000, 2000, 4000, 4000));
            segs.AddRange(Rect(12000, 5000, 15000, 8000));

            CadCandidate c = CadInterpretationRules.Interpret(segs, Set(), Sha).Candidates.Single();
            Assert.Equal(2, c.Holes.Count);
            Assert.Equal(200_000_000.0 - 4_000_000.0 - 9_000_000.0, c.AreaMm2.Value, 0);
        }

        [Fact]
        public void A_ring_inside_a_HOLE_is_an_island_and_its_own_element()
        {
            // Depth two: slab, shaft, and a column base standing in the shaft. The
            // island is NOT a hole in a hole - it is a thing.
            var segs = new List<CadSegment>();
            segs.AddRange(Rect(0, 0, 20000, 20000));        // slab
            segs.AddRange(Rect(5000, 5000, 15000, 15000));  // shaft
            segs.AddRange(Rect(8000, 8000, 10000, 10000));  // island in the shaft

            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(), Sha);
            Assert.Equal(2, r.Candidates.Count);
            CadCandidate slab = r.Candidates.OrderByDescending(x => x.AreaMm2 ?? 0).First();
            CadCandidate island = r.Candidates.OrderBy(x => x.AreaMm2 ?? 0).First();
            Assert.Single(slab.Holes);
            Assert.Empty(island.Holes);
            Assert.Equal(4_000_000.0, island.AreaMm2.Value, 0);
        }

        [Fact]
        public void Two_slabs_SIDE_BY_SIDE_are_two_slabs_neither_a_hole()
        {
            var segs = new List<CadSegment>();
            segs.AddRange(Rect(0, 0, 5000, 5000));
            segs.AddRange(Rect(9000, 0, 14000, 5000));

            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(), Sha);
            Assert.Equal(2, r.Candidates.Count);
            Assert.All(r.Candidates, c => Assert.Empty(c.Holes));
        }

        // ------------------------------------------------------- the room point

        [Fact]
        public void A_rectangular_room_gets_a_point_inside_it()
        {
            var segs = new List<CadSegment>(Rect(0, 0, 6000, 4000));
            CadCandidate c = CadInterpretationRules.Interpret(segs, Set("room", "OST_Rooms"), Sha)
                .Candidates.Single();
            Assert.NotNull(c.InteriorPoint);
            Assert.True(CadTopologyRules.ContainsPoint(c.Geometry, c.InteriorPoint.Value),
                        "the point must be inside the ring it belongs to");
        }

        [Fact]
        public void An_L_SHAPED_room_gets_a_point_inside_it_and_not_its_centroid()
        {
            // The whole reason InteriorOf exists. This L's centroid is in the
            // notch - outside the room - and a room placed there lands in the
            // corridor next door.
            var pts = new List<CadPoint>
            {
                new CadPoint(0, 0), new CadPoint(10000, 0), new CadPoint(10000, 2000),
                new CadPoint(2000, 2000), new CadPoint(2000, 10000), new CadPoint(0, 10000)
            };
            var segs = new List<CadSegment>();
            for (int i = 0; i < pts.Count; i++)
                segs.Add(new CadSegment(pts[i], pts[(i + 1) % pts.Count], "A-SLAB"));

            CadCandidate c = CadInterpretationRules.Interpret(segs, Set("room", "OST_Rooms"), Sha)
                .Candidates.Single();
            Assert.NotNull(c.InteriorPoint);
            Assert.True(CadTopologyRules.ContainsPoint(c.Geometry, c.InteriorPoint.Value));

            var centroid = new CadPoint(c.Geometry.Average(p => p.X), c.Geometry.Average(p => p.Y));
            Assert.False(CadTopologyRules.ContainsPoint(c.Geometry, centroid),
                         "this fixture is pointless unless the centroid really is outside");
        }

        [Fact]
        public void The_interior_point_avoids_the_HOLES_too()
        {
            // A doughnut whose centroid is in the middle of its own hole.
            var segs = new List<CadSegment>();
            segs.AddRange(Rect(0, 0, 10000, 10000));
            segs.AddRange(Rect(2000, 2000, 8000, 8000));

            CadCandidate c = CadInterpretationRules.Interpret(segs, Set(), Sha).Candidates.Single();
            Assert.NotNull(c.InteriorPoint);
            Assert.True(CadTopologyRules.ContainsPoint(c.Geometry, c.InteriorPoint.Value));
            Assert.False(CadTopologyRules.ContainsPoint(c.Holes[0], c.InteriorPoint.Value),
                         "a point in the hole is not inside the slab");
        }

        // -------------------------------------------------- what the plan emits

        [Fact]
        public void The_plan_emits_a_profile_of_LOOPS_outer_first_then_holes()
        {
            var segs = new List<CadSegment>();
            segs.AddRange(Rect(0, 0, 10000, 8000));
            segs.AddRange(Rect(3000, 3000, 5000, 5000));

            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(), Sha);
            CadConversionPlan plan = CadConversionPlanRules.Plan(r, Set(), "fp", false);
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");
            JObject row = (JObject)((JArray)requests[0]["elements"])[0];

            var profile = (JArray)row["profile"];
            Assert.Equal(2, profile.Count);
            // EVERY entry is an array of points - the shape Loops() reads. The
            // flat form this replaced was refused at create time, every time.
            Assert.All(profile, loop => Assert.IsType<JArray>(loop));
            Assert.All(profile, loop => Assert.All((JArray)loop, p => Assert.Equal(3, ((JArray)p).Count)));
        }

        [Fact]
        public void The_plan_emits_a_POINT_for_a_room_and_no_profile()
        {
            var segs = new List<CadSegment>(Rect(0, 0, 6000, 4000));
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set("room", "OST_Rooms"), Sha);
            CadConversionPlan plan = CadConversionPlanRules.Plan(r, Set("room", "OST_Rooms"), "fp", false);
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");
            JObject row = (JObject)((JArray)requests[0]["elements"])[0];

            Assert.NotNull(row["point"]);
            Assert.Null(row["profile"]);
            Assert.Equal(3, ((JArray)row["point"]).Count);
        }

        private static double SignedArea(List<CadPoint> ring)
        {
            double twice = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                CadPoint a = ring[i], b = ring[(i + 1) % ring.Count];
                twice += a.X * b.Y - b.X * a.Y;
            }
            return twice / 2.0;
        }
    }
}

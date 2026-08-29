// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Grid crossings, column dedup and beam spans - the arithmetic somebody's
// structure stands on. Millimetres in the helpers, feet in the rules.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StructuralLayoutRulesTests
    {
        private const double Ft = 304.8;

        private static GridSegment G(string name, long id, double x1, double y1, double x2, double y2)
            => new GridSegment { Name = name, ElementId = id, X1 = x1 / Ft, Y1 = y1 / Ft, X2 = x2 / Ft, Y2 = y2 / Ft };

        [Fact]
        public void A_two_by_two_grid_has_four_crossings_in_deterministic_order()
        {
            var grids = new List<GridSegment>
            {
                G("A", 1, 0, 0, 0, 10000), G("B", 2, 6000, 0, 6000, 10000),
                G("1", 3, -1000, 2000, 8000, 2000), G("2", 4, -1000, 8000, 8000, 8000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            Assert.Equal(4, crossings.Count);
            // Ordered by grid pair, then along the first grid.
            Assert.Equal("A", crossings[0].GridA);
            Assert.Equal(2000 / Ft, crossings[0].Y, 6);
            Assert.Equal(8000 / Ft, crossings[1].Y, 6);
        }

        [Fact]
        public void Parallel_grids_cross_nowhere()
        {
            var grids = new List<GridSegment> { G("A", 1, 0, 0, 0, 10000), G("B", 2, 6000, 0, 6000, 10000) };
            Assert.Empty(StructuralLayoutRules.Intersections(grids));
        }

        [Fact]
        public void A_crossing_beyond_a_grids_drawn_extent_is_not_a_place_that_grid_names()
        {
            // The horizontal grid stops at x=5000; the vertical grid at x=6000 would
            // cross its EXTENSION, which the drawing does not show.
            var grids = new List<GridSegment> { G("1", 1, 0, 2000, 5000, 2000), G("B", 2, 6000, 0, 6000, 10000) };
            Assert.Empty(StructuralLayoutRules.Intersections(grids));
        }

        [Fact]
        public void An_existing_column_suppresses_its_crossing_by_distance_not_by_name()
        {
            var grids = new List<GridSegment>
            {
                G("A", 1, 0, 0, 0, 10000), G("1", 2, -1000, 2000, 8000, 2000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            var existing = new List<double[]> { new[] { 0.003 / 1, 2000 / Ft } }; // ~1 mm off in x (feet)
            existing[0][0] = 1.0 / Ft; // 1 mm from the crossing
            StructuralLayoutRules.DedupColumns(crossings, existing,
                out List<GridIntersection> place, out List<GridIntersection> present);
            Assert.Empty(place);
            Assert.Single(present);
        }

        [Fact]
        public void Two_crossings_at_one_physical_spot_place_one_column()
        {
            // Three grids through one point: three pairwise crossings, one column.
            var grids = new List<GridSegment>
            {
                G("A", 1, -5000, 0, 5000, 0), G("B", 2, 0, -5000, 0, 5000), G("C", 3, -5000, -5000, 5000, 5000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            Assert.Equal(3, crossings.Count);
            StructuralLayoutRules.DedupColumns(crossings, null,
                out List<GridIntersection> place, out List<GridIntersection> present);
            Assert.Single(place);
            Assert.Equal(2, present.Count);
        }

        [Fact]
        public void Beam_spans_join_consecutive_crossings_along_one_grid()
        {
            var grids = new List<GridSegment>
            {
                G("1", 10, 0, 0, 18000, 0),
                G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000), G("C", 3, 18000, -1000, 18000, 1000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            StructuralLayoutRules.BeamSpans(crossings, "1", 10, null, 100 / Ft,
                out List<BeamSpan> spans, out int existing, out int shortOnes);
            Assert.Equal(2, spans.Count);
            Assert.Equal(0, existing); Assert.Equal(0, shortOnes);
            Assert.Equal("A", spans[0].FromCrossing);
            Assert.Equal("B", spans[0].ToCrossing);
            Assert.Equal("B", spans[1].FromCrossing);
            Assert.Equal("C", spans[1].ToCrossing);
        }

        [Fact]
        public void An_existing_beam_suppresses_its_span_and_is_counted()
        {
            var grids = new List<GridSegment>
            {
                G("1", 10, 0, 0, 12000, 0),
                G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000), G("C", 3, 12000, -1000, 12000, 1000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            var existingMid = new List<double[]> { new[] { 3000 / Ft, 0.0 } }; // the A-B span's midpoint
            StructuralLayoutRules.BeamSpans(crossings, "1", 10, existingMid, 100 / Ft,
                out List<BeamSpan> spans, out int existing, out int shortOnes);
            Assert.Single(spans);
            Assert.Equal(1, existing);
            Assert.Equal("B", spans[0].FromCrossing);
        }

        [Fact]
        public void A_span_shorter_than_the_minimum_is_omitted_and_counted()
        {
            var grids = new List<GridSegment>
            {
                G("1", 10, 0, 0, 12000, 0),
                G("A", 1, 0, -1000, 0, 1000), G("A2", 2, 20, -1000, 20, 1000), G("C", 3, 12000, -1000, 12000, 1000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            StructuralLayoutRules.BeamSpans(crossings, "1", 10, null, 100 / Ft,
                out List<BeamSpan> spans, out _, out int shortOnes);
            Assert.Single(spans);       // A2 -> C survives
            Assert.Equal(1, shortOnes); // A -> A2 (20 mm) is named, not silently dropped
        }
    }
}

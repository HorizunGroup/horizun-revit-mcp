// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Four causes behind one Revit warning shape, told apart from bounding boxes
// alone: exact duplicate, one element inside another, a measured vertical
// overlap, and a stranded profile - which pre-empts box classification
// entirely, since it is the more actionable, upstream fact.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WarningRootCauseRulesTests
    {
        private static ElementBoxFact Box(long id, string cat, long typeId,
                                          double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
                                          bool stranded = false) =>
            new ElementBoxFact { Id = id, Category = cat, TypeId = typeId, MinX = minX, MinY = minY, MinZ = minZ, MaxX = maxX, MaxY = maxY, MaxZ = maxZ, StrandedProfile = stranded };

        [Fact]
        public void Two_identical_floors_are_an_exact_duplicate()
        {
            var a = Box(1, "Floors", 500, 0, 0, 0, 5000, 5000, 300);
            var b = Box(2, "Floors", 500, 0, 0, 0, 5000, 5000, 300);
            WarningRootCause c = WarningRootCauseRules.Classify(new List<ElementBoxFact> { a, b });
            Assert.Equal(WarningRootCauseCauses.ExactDuplicate, c.Cause);
        }

        [Fact]
        public void A_small_floor_fully_inside_a_big_one_is_contained()
        {
            var big = Box(1, "Floors", 500, 0, 0, 0, 10000, 10000, 300);
            var small = Box(2, "Floors", 501, 1000, 1000, 0, 2000, 2000, 300);
            WarningRootCause c = WarningRootCauseRules.Classify(new List<ElementBoxFact> { big, small });
            Assert.Equal(WarningRootCauseCauses.Contained, c.Cause);
        }

        [Fact]
        public void Two_floors_sharing_100mm_of_elevation_are_a_measured_vertical_overlap()
        {
            var lower = Box(1, "Floors", 500, 0, 0, 0, 5000, 5000, 300);
            var upper = Box(2, "Floors", 600, 0, 0, 200, 5000, 5000, 500);
            WarningRootCause c = WarningRootCauseRules.Classify(new List<ElementBoxFact> { lower, upper });
            Assert.Equal(WarningRootCauseCauses.VerticalOverlap, c.Cause);
            Assert.NotNull(c.OverlapMm);
            Assert.Equal(100, c.OverlapMm.Value, 3);
        }

        [Fact]
        public void A_stranded_wall_is_reported_as_the_cause_even_if_boxes_would_read_as_contained()
        {
            var strandedWall = Box(1, "Walls", 700, 0, 0, 0, 5000, 200, 3000, stranded: true);
            var other = Box(2, "Walls", 700, 0, 0, 0, 5000, 200, 3000);
            WarningRootCause c = WarningRootCauseRules.Classify(new List<ElementBoxFact> { strandedWall, other });
            Assert.Equal(WarningRootCauseCauses.StrandedProfile, c.Cause);
        }

        [Fact]
        public void Disjoint_footprints_are_unclassified_not_forced_into_a_bucket()
        {
            var a = Box(1, "Floors", 500, 0, 0, 0, 1000, 1000, 300);
            var b = Box(2, "Floors", 500, 5000, 5000, 0, 6000, 6000, 300);
            WarningRootCause c = WarningRootCauseRules.Classify(new List<ElementBoxFact> { a, b });
            Assert.Equal(WarningRootCauseCauses.Unclassified, c.Cause);
        }

        [Fact]
        public void More_than_two_elements_without_a_stranded_one_is_unclassified()
        {
            var a = Box(1, "Floors", 500, 0, 0, 0, 1000, 1000, 300);
            var b = Box(2, "Floors", 500, 0, 0, 0, 1000, 1000, 300);
            var c3 = Box(3, "Floors", 500, 0, 0, 0, 1000, 1000, 300);
            WarningRootCause c = WarningRootCauseRules.Classify(new List<ElementBoxFact> { a, b, c3 });
            Assert.Equal(WarningRootCauseCauses.Unclassified, c.Cause);
        }
    }
}

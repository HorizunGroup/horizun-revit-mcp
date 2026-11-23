using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;
using static Horizun.Revit.Core.WallSlabRules;

namespace Horizun.Core.Tests
{
    public class WallSlabRulesTests
    {
        [Fact]
        public void Equal_height_edge_probes_can_find_different_slabs_at_both_ends()
        {
            var wall = new[] { new VerticalSpan(0, 100) };
            var probesRead = new List<int>();
            var result = MeasureStation(new[] { 0, 1, -1, 2 }, point =>
                point == 2 ? Array.Empty<VerticalSpan>() : wall, point =>
                {
                    probesRead.Add(point);
                    SlabHit[] hits = point == 0 ? Array.Empty<SlabHit>() :
                        point == 1 ? new[] { new SlabHit(12, new VerticalSpan(100, 120)) } :
                        new[] { new SlabHit(13, new VerticalSpan(-20, 0)) };
                    return (hits, Array.Empty<long>());
                }, 5);

            Assert.Equal(new[] { 0, 1, -1 }, probesRead);
            Assert.Equal(1, result.Crown.Value.Point);
            Assert.Equal(-1, result.Base.Value.Point);
            Assert.Equal(12L, result.Crown.Value.Contact.Value.Hit.Id);
            Assert.Equal(13L, result.Base.Value.Contact.Value.Hit.Id);
            Assert.Equal(Relation.Ok, result.Crown.Value.Contact.Value.Relation);
            Assert.Equal(Relation.Ok, result.Base.Value.Contact.Value.Relation);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        public void Station_prefers_an_in_tolerance_gap_to_deep_penetration(int first, int second)
        {
            var wall = new[] { new VerticalSpan(0, 100) };
            var result = MeasureStation(new[] { first, second }, _ => wall, point =>
                (new[]
                {
                    new SlabHit(12, point == 0 ? new VerticalSpan(80, 120) : new VerticalSpan(101, 120)),
                    new SlabHit(13, point == 0 ? new VerticalSpan(-20, 20) : new VerticalSpan(-20, -1))
                }, Array.Empty<long>()), 5);

            Assert.Equal(1, result.Crown.Value.Point);
            Assert.Equal(1, result.Base.Value.Point);
            Assert.Equal(Relation.Ok, result.Crown.Value.Contact.Value.Relation);
            Assert.Equal(Relation.Ok, result.Base.Value.Contact.Value.Relation);
            Assert.Equal(-1d, result.Crown.Value.Contact.Value.FaceDelta);
            Assert.Equal(-1d, result.Base.Value.Contact.Value.FaceDelta);
        }

        [Theory]
        [InlineData(-1, true)]
        [InlineData(7, true)]
        [InlineData(8, false)]
        [InlineData(null, true)]
        public void A_floor_without_bounds_blocks_only_potentially_affected_measurements(int? floorOption, bool affected)
        {
            var wall = new[] { new VerticalSpan(0, 100) };
            var result = MeasureStation(new[] { 0, 1 }, _ => wall, point =>
                (point == 0 ? new[] { new SlabHit(12, new VerticalSpan(100, 120)) } : Array.Empty<SlabHit>(),
                 MayAffectProbe(floorOption, 7, null) ? new[] { 99L } : Array.Empty<long>()), 5);

            Assert.False(MayAffectProbe(floorOption, 7, false));
            if (affected)
            {
                Assert.Equal(new[] { 99L }, result.UnreadableFloors);
                Assert.Null(result.Crown);
                Assert.Null(result.Base);
            }
            else
            {
                Assert.Empty(result.UnreadableFloors);
                Assert.Equal(Relation.Ok, result.Crown.Value.Contact.Value.Relation);
                Assert.Null(result.Base.Value.Contact);
            }
        }

        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(420, 2, 2)]
        [InlineData(999, 2, 1)]
        [InlineData(1000, 2, 0)]
        public void Wall_geometry_is_measured_only_for_the_requested_page(int offset, int size, int count)
        {
            var measured = new List<int>();
            var rows = MeasurePage(Enumerable.Range(0, 1000), offset, size, wall =>
            {
                measured.Add(wall);
                return wall;
            });
            Assert.Equal(Enumerable.Range(offset, count), measured);
            Assert.Equal(measured.ToArray(), rows);
        }

        [Theory]
        [InlineData(99, 0, 1)]
        [InlineData(100, 0, 0)]
        [InlineData(101, 0, -1)]
        [InlineData(94, 1, 6)]
        [InlineData(106, 2, -6)]
        [InlineData(125, 2, -25)]
        [InlineData(126, 3, -26)]
        public void Classifies_both_ends_with_the_same_tolerance(int end, int relation, int signedGap)
        {
            var crown = Nearest(new[] { new VerticalSpan(0, end) }, new[] { new SlabHit(1, new VerticalSpan(100, 120)) }, true, 5);
            var foot = Nearest(new[] { new VerticalSpan(-end, 0) }, new[] { new SlabHit(1, new VerticalSpan(-120, -100)) }, false, 5);
            Assert.Equal((Relation)relation, crown.Value.Relation);
            Assert.Equal((Relation)relation, foot.Value.Relation);
            Assert.Equal((double)-signedGap, crown.Value.FaceDelta);
            Assert.Equal(crown.Value.FaceDelta, foot.Value.FaceDelta);
        }

        [Fact]
        public void A_floor_at_the_base_is_not_a_crown_candidate()
        {
            var wall = new[] { new VerticalSpan(0, 3000) };
            var floor = new[] { new SlabHit(1, new VerticalSpan(-200, 0)) };
            Assert.Null(Nearest(wall, floor, true, 5));
            Assert.Equal(Relation.Ok, Nearest(wall, floor, false, 5).Value.Relation);
        }

        [Fact]
        public void A_slab_entirely_beyond_the_opposite_end_is_not_embedded()
        {
            var wall = new[] { new VerticalSpan(120, 124) };
            var floor = new[] { new SlabHit(1, new VerticalSpan(90, 120)) };
            Assert.Null(Nearest(wall, floor, true, 5));
            Assert.Equal(Relation.Ok, Nearest(wall, floor, false, 5).Value.Relation);
        }

        [Fact]
        public void A_cut_in_the_wall_ray_does_not_count_as_a_through_contact()
        {
            var wall = Merge(new[] { new VerticalSpan(0, 2400), new VerticalSpan(2700, 3100), new VerticalSpan(3100, 3200) });
            var floor = new[] { new SlabHit(1, new VerticalSpan(2450, 2650)) };
            Assert.Equal(2, wall.Count);
            Assert.Equal(3200d, wall[1].Top);
            Assert.Null(Nearest(wall, floor, true, 5));
        }

        [Fact]
        public void Chooses_nearest_floor_and_resolves_equal_hits_by_id()
        {
            var wall = new[] { new VerticalSpan(0, 100) };
            var floors = new[] { new SlabHit(1, new VerticalSpan(130, 150)), new SlabHit(3, new VerticalSpan(109, 125)),
                                 new SlabHit(2, new VerticalSpan(109, 125)) };
            Contact contact = Nearest(wall, floors, true, 5).Value;
            Assert.Equal(Relation.Gap, contact.Relation);
            Assert.Equal(-9d, contact.FaceDelta);
            Assert.Equal(2L, contact.Hit.Id);
        }
    }
}

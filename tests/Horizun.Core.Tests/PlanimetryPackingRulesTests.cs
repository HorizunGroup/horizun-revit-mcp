using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PlanimetryPackingRulesTests
    {
        private static PackingItem Item(string key, double width, double height)
            => new PackingItem { Key = key, Width = width, Height = height };

        [Fact]
        public void Packs_from_upper_left_and_preserves_requested_priority()
        {
            PackingResult r = PlanimetryPackingRules.Pack(
                PlanBox.FromCorners(0, 0, 100, 100), new PlanBox[0],
                new[] { Item("A", 30, 20), Item("B", 40, 20) }, 10, 5, 0.001);

            Assert.True(r.Ok, r.Error);
            Assert.Equal(2, r.Placements.Count);
            Assert.Equal("A", r.Placements[0].Key);
            Assert.Equal(25, r.Placements[0].CenterX, 6);
            Assert.Equal(80, r.Placements[0].CenterY, 6);
            Assert.Equal("B", r.Placements[1].Key);
            Assert.Equal(65, r.Placements[1].CenterX, 6);
            Assert.Equal(80, r.Placements[1].CenterY, 6);
        }

        [Fact]
        public void Fixed_obstacle_is_never_overlapped_and_gap_is_honoured()
        {
            PackingResult r = PlanimetryPackingRules.Pack(
                PlanBox.FromCorners(0, 0, 100, 100),
                new[] { PlanBox.FromCorners(0, 70, 50, 100) },
                new[] { Item("A", 30, 20) }, 0, 5, 0.001);

            Assert.True(r.Ok, r.Error);
            PlanBox placed = r.Placements[0].Box;
            Assert.False(PlanimetryGeometry.Overlaps(placed, PlanBox.FromCorners(0, 70, 50, 100), 0.001));
            Assert.True(PlanimetryGeometry.Separation(placed, PlanBox.FromCorners(0, 70, 50, 100)) >= 4.999);
        }

        [Fact]
        public void One_item_that_cannot_fit_refuses_the_whole_plan()
        {
            PackingResult r = PlanimetryPackingRules.Pack(
                PlanBox.FromCorners(0, 0, 40, 40), new PlanBox[0],
                new[] { Item("fits", 10, 10), Item("does-not", 50, 10) }, 0, 0, 0.001);

            Assert.False(r.Ok);
            Assert.Empty(r.Placements);
            Assert.Contains("does-not", r.Error);
        }

        [Fact]
        public void Unreadable_obstacle_is_not_treated_as_empty_space()
        {
            PackingResult r = PlanimetryPackingRules.Pack(
                PlanBox.FromCorners(0, 0, 100, 100),
                new[] { PlanBox.Unreadable }, new[] { Item("A", 10, 10) }, 0, 0, 0.001);

            Assert.False(r.Ok);
            Assert.Contains("unreadable", r.Error);
        }

        [Fact]
        public void Same_input_produces_the_same_centres()
        {
            var items = new[] { Item("A", 32, 12), Item("B", 18, 22), Item("C", 25, 16) };
            PackingResult a = PlanimetryPackingRules.Pack(PlanBox.FromCorners(-10, -20, 90, 80),
                new[] { PlanBox.FromCorners(20, 50, 40, 80) }, items, 3, 2, 0.001);
            PackingResult b = PlanimetryPackingRules.Pack(PlanBox.FromCorners(-10, -20, 90, 80),
                new[] { PlanBox.FromCorners(20, 50, 40, 80) }, items, 3, 2, 0.001);

            Assert.True(a.Ok && b.Ok);
            Assert.Equal(a.Placements.Count, b.Placements.Count);
            for (int i = 0; i < a.Placements.Count; i++)
            {
                Assert.Equal(a.Placements[i].CenterX, b.Placements[i].CenterX, 12);
                Assert.Equal(a.Placements[i].CenterY, b.Placements[i].CenterY, 12);
            }
        }
    }
}

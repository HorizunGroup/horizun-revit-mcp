using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CycleFivePlanimetryTests
    {
        [Fact]
        public void Dense_parallel_heads_are_separated_and_second_plan_is_idempotent()
        {
            double[][] centers = { new[] { 0d, 0d }, new[] { 1d, 0d }, new[] { 2d, 0d }, new[] { 3d, 0d } };
            double[][] directions = centers.Select(_ => new[] { 0d, 1d }).ToArray();
            bool[] movable = { true, true, true, true };
            double[] offsets = PlanimetryOpsRules.BubbleOffsets(centers, directions, movable, 8, 2, 200);
            Assert.Equal(6, PlanimetryOpsRules.BubbleOverlaps(centers, 8));
            var after = centers.Select((c, i) => new[] { c[0], c[1] + offsets[i] }).ToArray();
            Assert.Equal(0, PlanimetryOpsRules.BubbleOverlaps(after, 8));
            Assert.Equal(3, offsets.Count(x => x > 0));
            Assert.All(PlanimetryOpsRules.BubbleOffsets(after, directions, movable, 8, 2, 200), x => Assert.Equal(0, x));
        }

        [Fact]
        public void Fixed_heads_are_obstacles_and_insufficient_travel_is_refused()
        {
            double[][] centers = { new[] { 0d, 0d }, new[] { 1d, 0d } };
            double[][] directions = { new[] { 0d, -1d }, new[] { 0d, 1d } };
            Assert.Equal(new[] { 0d, 10d }, PlanimetryOpsRules.BubbleOffsets(centers, directions, new[] { false, true }, 8, 2, 20));
            Assert.Throws<ArgumentException>(() => PlanimetryOpsRules.BubbleOffsets(centers, directions, new[] { false, true }, 8, 2, 5));
        }

        [Theory]
        [InlineData("ends", "invalid")]
        [InlineData("bubble_diameter_mm", 0)]
        [InlineData("max_offset_mm", -1)]
        public void Stagger_validates_its_fields(string field, object value)
        {
            var request = new JObject { ["target_document"] = "synthetic", ["operation"] = "stagger_grid_bubbles", ["view_id"] = 1, [field] = JToken.FromObject(value) };
            Assert.NotNull(PlanimetryOpsRules.Validate(request));
        }

        [Fact]
        public void Point_room_phase_is_schema_error_even_when_guidance_mentions_readback()
        {
            string text = EnclosedRules.PlacementFieldsProblem(new JObject { ["phase_id"] = 2 });
            Assert.Equal(ErrorCodes.EsquemaInvalido, ErrorCodes.Classify(text, null).Code);
        }

        [Fact]
        public void Unsupported_parameter_binding_is_a_revit_precondition()
            => Assert.Equal(ErrorCodes.PrecondicionNoCumplida, ErrorCodes.Classify("Category 'Room Separation' does not accept bound parameters. Nothing was written.", null).Code);
    }
}

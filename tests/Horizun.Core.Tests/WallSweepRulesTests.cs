// horizun_wall_sweep: what each operation accepts, and how a placed sweep is judged, without Revit.
// INFORME_P6 §6.7: there was no typed way to create a wall sweep, so the baseboard
// (HZ_WallSweep_Baseboard) had no checkable instance.
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallSweepRulesTests
    {
        private static JObject Place() => new JObject
        {
            ["target_document"] = "Casa_Arq", ["operation"] = "place", ["type_id"] = 12, ["wall_ids"] = new JArray(1, 2),
            ["height"] = 0, ["side"] = "interior"
        };

        [Fact]
        public void A_well_formed_place_passes()
            => Assert.Null(WallSweepRules.Validate(Place()));

        [Fact]
        public void Place_needs_the_side_said_not_guessed()
        {
            var r = Place(); r.Remove("side");
            Assert.Contains("side", WallSweepRules.Validate(r));
            r["side"] = "left";
            Assert.Contains("exterior or interior", WallSweepRules.Validate(r));
        }

        [Fact]
        public void Place_needs_walls_a_type_and_a_height()
        {
            var r = Place(); r.Remove("wall_ids");
            Assert.Contains("wall_ids", WallSweepRules.Validate(r));
            r = Place(); r["wall_ids"] = new JArray();
            Assert.Contains("wall_ids", WallSweepRules.Validate(r));
            r = Place(); r.Remove("type_id");
            Assert.Contains("type_id", WallSweepRules.Validate(r));
            r = Place(); r.Remove("height");
            Assert.Contains("height", WallSweepRules.Validate(r));
            r = Place(); r["wall_ids"] = new JArray(1, 1);
            Assert.Contains("twice", WallSweepRules.Validate(r));
        }

        [Fact]
        public void Measured_from_is_base_or_top()
        {
            var r = Place(); r["measured_from"] = "top";
            Assert.Null(WallSweepRules.Validate(r));
            r["measured_from"] = "middle";
            Assert.Contains("base or top", WallSweepRules.Validate(r));
        }

        [Fact]
        public void A_field_of_another_operation_is_refused_by_name()
        {
            var r = Place(); r["profile_id"] = 5;
            Assert.Contains("profile_id", WallSweepRules.Validate(r));
        }

        [Fact]
        public void Create_type_needs_a_source_a_name_and_a_profile()
        {
            var r = new JObject { ["target_document"] = "d", ["operation"] = "create_type", ["source_type_id"] = 3, ["new_name"] = "HZ_WallSweep_Baseboard", ["profile_id"] = 9 };
            Assert.Null(WallSweepRules.Validate(r));
            r["material_id"] = 44;
            Assert.Null(WallSweepRules.Validate(r));
            r.Remove("profile_id");
            Assert.Contains("profile_id", WallSweepRules.Validate(r));
            r["profile_id"] = 9; r["new_name"] = "  ";
            Assert.Contains("new_name", WallSweepRules.Validate(r));
        }

        [Fact]
        public void Read_takes_an_optional_wall_and_nothing_else()
        {
            Assert.Null(WallSweepRules.Validate(new JObject { ["operation"] = "read" }));
            Assert.Null(WallSweepRules.Validate(new JObject { ["operation"] = "read", ["element_id"] = 5 }));
            Assert.Contains("height", WallSweepRules.Validate(new JObject { ["operation"] = "read", ["height"] = 5 }));
            Assert.Contains("operation must be", WallSweepRules.Validate(new JObject { ["operation"] = "draw" }));
        }

        [Theory]
        [InlineData(0.0, 0.33, 0.0, true)]     // a baseboard sitting on the base: its box starts at the target height
        [InlineData(0.9, 1.2, 1.0, true)]      // a chair rail around 1.0
        [InlineData(2.0, 2.3, 1.0, false)]     // a sweep a metre off where it was asked
        public void The_sweep_body_must_stand_at_the_height_asked(double minZ, double maxZ, double target, bool ok)
            => Assert.Equal(ok, WallSweepRules.BodyAtHeight(minZ, maxZ, target, 0.5 / 304.8));
    }
}

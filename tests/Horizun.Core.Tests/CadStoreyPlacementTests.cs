using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// A PLAN DRAWING'S Z IS NOT A HEIGHT.
    ///
    /// MEASURED (dry run, class 4): a DWG linked into a plan of a level at +30 000 mm handed its geometry
    /// over at Z = 0, and horizun_plan_from_cad emitted 75 walls at start/end Z = 0. horizun_create_elements
    /// derives a wall's base offset as Z minus the level's elevation, so those walls would have been built
    /// with a base offset of -30 000 mm - on the storey underneath - and verified there.
    ///
    /// These fix the rule the plan now follows: an element resolved to a storey stands on that storey, at its
    /// elevation plus the rule's offset, and the consumer's own derivation then gives back exactly that offset.
    /// </summary>
    public class CadStoreyPlacementTests
    {
        // Revit's internal unit is the decimal foot: +30 000 mm is 98.4251968... ft.
        private const double LevelFeet = 30000.0 / 304.8;

        private static double LevelMm => CadUnits.FeetToMm(LevelFeet);

        private static JObject Wall(double z, double? offset = null)
        {
            var row = new JObject
            {
                ["kind"] = "wall",
                ["start"] = new JArray(1000.0, 2000.0, z),
                ["end"] = new JArray(6000.0, 2000.0, z),
                ["height"] = 3000.0,
                ["level_id"] = 275900L
            };
            if (offset.HasValue) row["offset"] = offset.Value;
            return row;
        }

        /// <summary>What horizun_create_elements does with a wall row: base offset = Z - level elevation.</summary>
        private static double DerivedBaseOffsetMm(JObject row) => ((JArray)row["start"])[2].Value<double>() - LevelMm;

        [Fact]
        public void The_level_is_thirty_metres_up_in_feet_as_Revit_keeps_it()
        {
            Assert.Equal(98.4252, LevelFeet, 4);
            Assert.Equal(30000.0, LevelMm, 6);
        }
















    }
}

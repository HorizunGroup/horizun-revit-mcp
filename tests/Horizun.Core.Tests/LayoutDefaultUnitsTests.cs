// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Annotation layout defaults are millimetres, converted to the caller's units
// (audit 2026-10-08, 6.17).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LayoutDefaultUnitsTests
    {
        private const double MmToFeet = 1.0 / 304.8, MToFeet = 1000.0 / 304.8, FtToFeet = 1.0;

        [Fact]
        public void A_default_offset_is_fifteen_millimetres_in_every_unit()
        {
            Assert.Equal(15.0, DeliveryLayoutRules.DefaultInUnits("offset", "model", MmToFeet), 9);
            Assert.Equal(0.015, DeliveryLayoutRules.DefaultInUnits("offset", "model", MToFeet), 9);   // was 15 m
            Assert.Equal(15.0 / 304.8, DeliveryLayoutRules.DefaultInUnits("offset", "model", FtToFeet), 9);
        }

        [Fact]
        public void Clearance_and_displacement_keep_their_millimetre_values()
        {
            Assert.Equal(0.010, DeliveryLayoutRules.DefaultInUnits("clearance", "model", MToFeet), 9);
            Assert.Equal(1.2, DeliveryLayoutRules.DefaultInUnits("max_displacement", "model", MToFeet), 9);
            Assert.Equal(30.0, DeliveryLayoutRules.DefaultInUnits("max_displacement", "paper", MmToFeet), 9);
        }
    }
}

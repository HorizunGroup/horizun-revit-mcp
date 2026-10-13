// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Millimetres per drawing unit, for every INSUNITS code the DXF reference defines
// (audit 2026-10-08, lote 7). 8 is MICROINCHES (2.54e-5 mm), 9 mils (0.0254 mm),
// 13 MICRONS (1e-3 mm), 12 nanometres (1e-6 mm), 17 gigametres (1e12 mm).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class InsUnitsTableTests
    {
        [Theory]
        [InlineData(1, 25.4)]                    // inches
        [InlineData(2, 304.8)]                   // feet
        [InlineData(3, 1609344.0)]               // miles
        [InlineData(4, 1.0)]                     // millimetres
        [InlineData(5, 10.0)]                    // centimetres
        [InlineData(6, 1000.0)]                  // metres
        [InlineData(7, 1e6)]                     // kilometres
        [InlineData(8, 2.54e-5)]                 // microinches
        [InlineData(9, 0.0254)]                  // mils
        [InlineData(10, 914.4)]                  // yards
        [InlineData(11, 1e-7)]                   // angstroms
        [InlineData(12, 1e-6)]                   // nanometres
        [InlineData(13, 1e-3)]                   // microns
        [InlineData(14, 100.0)]                  // decimetres
        [InlineData(15, 10000.0)]                // decametres
        [InlineData(16, 100000.0)]               // hectometres
        [InlineData(17, 1e12)]                   // gigametres
        [InlineData(18, 1.495978707e14)]         // astronomical units
        [InlineData(19, 9.4607304725808e18)]     // light years
        [InlineData(20, 3.0856775814913673e19)]  // parsecs
        [InlineData(21, 304.80060960121924)]     // US survey feet (1200/3937 m)
        [InlineData(22, 25.400050800101603)]     // US survey inches
        [InlineData(23, 914.4018288036577)]      // US survey yards
        [InlineData(24, 1609347.2186944373)]     // US survey miles
        public void Every_code_converts_as_the_DXF_reference_defines(int code, double mm)
        {
            double? got = CadDwgExtract.MmPerUnitOf(code);
            Assert.NotNull(got);
            Assert.Equal(mm, got.Value, mm * 1e-12);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(25)]
        [InlineData(-1)]
        public void Unitless_and_unknown_codes_are_refused(int code) => Assert.Null(CadDwgExtract.MmPerUnitOf(code));
    }
}

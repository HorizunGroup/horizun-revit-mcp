using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CoupledEndTests
    {
        // MEASURED, Revit 2024 and 2026 (acero L7, P4): two 16M pieces declared to meet at x = 199000
        // and coupled with CPL16M (56 mm long). Revit left the coupled end of the second piece at
        // 199012 - a 12 mm gap inside the coupler, which spans 198978..199034.
        private static readonly double[] BoxMin = { 198978, -113.05, 2436.95 }, BoxMax = { 199034, 113.05, 2458.95 };

        [Fact]
        public void A_coupled_end_seated_inside_the_coupler_is_trimmed_by_what_was_measured()
        {
            double t = CoupledEnd.TrimMm(new double[] { 199000, -102.05, 2447.95 }, new double[] { 201960, -102.05, 2447.95 },
                                         new double[] { 199012, -102.05, 2447.95 }, BoxMin, BoxMax, 2);
            Assert.Equal(12, t, 6);
        }

        [Fact]
        public void An_end_outside_the_coupler_is_not_excused()
        {
            Assert.True(CoupledEnd.TrimMm(new double[] { 199000, -102.05, 2447.95 }, new double[] { 201960, -102.05, 2447.95 },
                                          new double[] { 199040, -102.05, 2447.95 }, BoxMin, BoxMax, 2) < 0);
        }

        [Fact]
        public void An_end_off_the_declared_line_is_not_excused()
        {
            Assert.True(CoupledEnd.TrimMm(new double[] { 199000, -102.05, 2447.95 }, new double[] { 201960, -102.05, 2447.95 },
                                          new double[] { 199012, -92.05, 2447.95 }, BoxMin, BoxMax, 2) < 0);
        }

        [Fact]
        public void A_declared_station_outside_the_coupler_is_not_excused()
        {
            // The coupler sits somewhere else: whatever the bar did, it is not seated at the declared splice.
            Assert.True(CoupledEnd.TrimMm(new double[] { 198900, -102.05, 2447.95 }, new double[] { 201960, -102.05, 2447.95 },
                                          new double[] { 198990, -102.05, 2447.95 }, BoxMin, BoxMax, 2) < 0);
        }
    }
}

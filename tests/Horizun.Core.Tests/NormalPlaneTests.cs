using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NormalPlaneTests
    {
        // MEASURED, Revit 2024 (anonymized regression case, Paso 3): a column bar in a plane at 36 degrees, its points rounded to
        // 0.001 mm, made Rebar.CreateFromCurves fail with "An internal error has occurred"; the same bar at 0
        // degrees, where rounding keeps the points in the plane, was built.
        private static readonly double[] N = { -Math.Sin(Math.PI / 5), Math.Cos(Math.PI / 5), 0 };

        private static List<double[]> CBar(int decimals)
        {
            double c = Math.Cos(Math.PI / 5), s = Math.Sin(Math.PI / 5);
            var raw = new[] { new[] { 0.0, 0, 0 }, new[] { 127.0, 0, 0 }, new[] { 127.0, 0, 3000 }, new[] { 0.0, 0, 3000 } };
            var o = new List<double[]>();
            foreach (double[] p in raw)
                o.Add(new[] { Math.Round(p[0] * c, decimals), Math.Round(p[0] * s, decimals), Math.Round(p[2], decimals) });
            return o;
        }

        [Fact]
        public void Points_a_hair_off_their_normal_plane_are_squared_onto_it_and_the_move_is_said()
        {
            bool ok = RebarPlanRules.SquareToNormalPlane(CBar(3), N, 0.01, out List<double[]> squared, out double worst);
            Assert.True(ok);
            Assert.InRange(worst, 1e-6, 0.001);
            foreach (double[] p in squared)
                Assert.InRange(Math.Abs((p[0] - squared[0][0]) * N[0] + (p[1] - squared[0][1]) * N[1] + (p[2] - squared[0][2]) * N[2]), 0, 1e-9);
        }

        [Fact]
        public void Points_further_off_than_the_limit_are_not_moved()
        {
            List<double[]> bar = CBar(9);
            bar[2] = new[] { bar[2][0] + N[0] * 0.5, bar[2][1] + N[1] * 0.5, bar[2][2] };
            Assert.False(RebarPlanRules.SquareToNormalPlane(bar, N, 0.01, out List<double[]> squared, out double worst));
            // One point of four 0.5 mm off: the plane through their mean height leaves it 0.375 mm away.
            Assert.Equal(0.375, worst, 6);
            Assert.Null(squared);
        }
    }
}

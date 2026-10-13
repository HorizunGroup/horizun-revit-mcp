// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A section frame is measured off the host's own solid; a solid that does not
// fill its box is not a rectangular prism (refuerzo desde cartilla, F1.1).
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionFramesTests
    {
        /// <summary>A box from its 8 corners (a, b) in a frame (o, u, v, w), as 12 triangles.</summary>
        private static HostMesh Box(double[] o, double[] u, double[] v, double[] w, double[] lo, double[] hi)
        {
            var m = new HostMesh();
            for (int i = 0; i < 8; i++)
            {
                double a = (i & 1) == 0 ? lo[0] : hi[0], b = (i & 2) == 0 ? lo[1] : hi[1], c = (i & 4) == 0 ? lo[2] : hi[2];
                m.AddVertex(o[0] + a * u[0] + b * v[0] + c * w[0], o[1] + a * u[1] + b * v[1] + c * w[1], o[2] + a * u[2] + b * v[2] + c * w[2]);
            }
            int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            foreach (int[] q in faces) { m.AddTriangle(q[0], q[1], q[2]); m.AddTriangle(q[0], q[2], q[3]); }
            return m;
        }

        [Fact]
        public void A_beam_turned_in_plan_measures_its_own_width_and_depth()
        {
            double c = Math.Cos(0.5), s = Math.Sin(0.5);
            double[] u = { c, s, 0 }, w = { 0, 0, 1 }, v = { -s, c, 0 };
            HostMesh m = Box(new[] { 100.0, 200, 300 }, u, v, w, new[] { 0.0, -150, -500 }, new[] { 6000.0, 150, 0 });
            SectionHostFrame f = SectionFrames.Measure(7, new[] { 100.0, 200, 300 }, u, new[] { 0, 0, 1.0 }, m);
            Assert.Null(f.NotRectangular);
            Assert.Equal(6000, f.LengthMm, 6);
            Assert.Equal(300, f.WidthMm, 6);
            Assert.Equal(500, f.DepthMm, 6);
            Assert.Equal(-500, f.WMin, 6);
        }

        [Fact]
        public void On_an_inclined_beam_up_is_square_to_the_slope()
        {
            double a = 0.2;
            double[] u = { Math.Cos(a), 0, Math.Sin(a) }, w = { -Math.Sin(a), 0, Math.Cos(a) }, v = { 0, 1, 0 };
            HostMesh m = Box(new[] { 0.0, 0, 0 }, u, v, w, new[] { 0.0, -150, -500 }, new[] { 4000.0, 150, 0 });
            SectionHostFrame f = SectionFrames.Measure(8, new[] { 0.0, 0, 0 }, u, new[] { 0, 0, 1.0 }, m);
            Assert.Null(f.NotRectangular);
            Assert.Equal(500, f.DepthMm, 6);
            Assert.Equal(-Math.Sin(a), f.W[0], 9);
        }

        [Fact]
        public void A_section_turned_about_its_own_axis_is_not_read_as_a_prism()
        {
            // The beam's section rotated 20 degrees about U: in the frame of world up its
            // box is bigger than the solid, so it is refused rather than reinforced as b x h.
            double r = 20 * Math.PI / 180;
            double[] u = { 1, 0, 0 }, v = { 0, Math.Cos(r), Math.Sin(r) }, w = { 0, -Math.Sin(r), Math.Cos(r) };
            HostMesh m = Box(new[] { 0.0, 0, 0 }, u, v, w, new[] { 0.0, -150, -500 }, new[] { 4000.0, 150, 0 });
            SectionHostFrame f = SectionFrames.Measure(9, new[] { 0.0, 0, 0 }, u, new[] { 0, 0, 1.0 }, m);
            Assert.NotNull(f.NotRectangular);
            Assert.Contains("fills", f.NotRectangular);
        }

        [Fact]
        public void A_mesh_with_a_curved_face_is_not_a_prism()
        {
            HostMesh m = Box(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 10 });
            m.AnyCurvedFace = true;
            string why = SectionFrames.Measure(1, new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }, m).NotRectangular;
            Assert.StartsWith("it has a curved face", why);
        }

        [Fact]
        public void A_curved_section_that_is_not_round_says_what_was_measured()
        {
            // Live 2024 (acero L7): a revolved column was refused as "not circular" with nothing
            // to tell which test failed. The refusal now names the extents across and the spread
            // of the radii, so the next reader sees whether the section or the tolerance is wrong.
            HostMesh m = Box(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0.0, -250, -250 }, new[] { 2000.0, 250, 250 });
            m.AnyCurvedFace = true;
            string why = SectionFrames.Measure(1, new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }, m).NotRectangular;
            Assert.Contains("500 x 500 mm across", why);
            Assert.Contains("radii from 353.6 to 353.6 mm", why);
            Assert.Contains("slack 1 mm", why);
        }
    }
}

using System;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PixelCalibrationTests
    {
        private static readonly double[][] Points={new[]{0d,0d},new[]{10d,0d},new[]{0d,10d},new[]{3d,8d},new[]{7d,4d},new[]{9d,9d}};
        [Fact]
        public void Independent_anchors_validate_rotated_world_frame_and_pixel_origin()
        {
            var pixels=Points.Select(p=>new[]{100+20*p[0],400-20*p[1]}).ToArray();
            var result=PixelCalibration.Fit(Points,pixels,new[]{4d,5d,6d},new[]{0d,1d,0d},new[]{0d,0d,1d},1.5);
            Assert.Equal(20d,(double)result["pixels_per_foot"]);
            Assert.Equal(0d,(double)result["max_error_pixels"]);
            Assert.Equal(20d,(double)result["matrix_2x4"][0][1]);
            Assert.Equal(0d,(double)result["matrix_2x4"][0][3]);
            Assert.Equal(520d,(double)result["matrix_2x4"][1][3]);
        }
        [Fact]
        public void Perfect_fit_on_training_anchors_does_not_hide_holdout_drift()
        {
            var pixels=Points.Select(p=>new[]{20*p[0],-20*p[1]}).ToArray(); pixels[5][0]+=3;
            Assert.Throws<ArgumentException>(()=>PixelCalibration.Fit(Points,pixels,new double[3],new[]{1d,0d,0d},new[]{0d,1d,0d},1.5));
        }
        [Fact]
        public void Anisotropic_export_is_refused_even_with_zero_affine_residual()
        {
            var pixels=Points.Select(p=>new[]{20*p[0],-40*p[1]}).ToArray();
            Assert.Throws<ArgumentException>(()=>PixelCalibration.Fit(Points,pixels,new double[3],new[]{1d,0d,0d},new[]{0d,1d,0d},1.5));
        }
    }
}

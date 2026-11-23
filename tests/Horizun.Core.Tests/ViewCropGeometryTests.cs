// horizun_view_crop (FIELD FEEDBACK 2.1): crop frames, rotated views, margins and paper size, without Revit.
using System;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewCropGeometryTests
    {
        [Fact]
        public void Section_box_view_refusal_is_invalid_input_and_runner_records_prewrite_state()
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath()), "..", ".."));
            string crop = File.ReadAllText(Path.Combine(root, "src", "Horizun.Revit", "Commands", "ViewCropCommand.cs"));
            Assert.Contains("throw new ArgumentException(\"Section box operations require an orthographic 3D view. For a plan, use set_crop_box.\")", crop);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = new Document() } };
            var result = ModelEditRunner.Run(app, "{'operation':'set_section_box'}", "horizun_view_crop", "Crop",
                _ => null, (_, _, _) => throw new ArgumentException("Section box operations require an orthographic 3D view. For a plan, use set_crop_box."));
            Assert.False(result.Success);
            Assert.Contains("orthographic 3D view", result.Error);
            Assert.False((bool)result.Detail["write_started"]);
            Assert.False((bool)result.Detail["changes_applied"]);
            Assert.False(result.Fallback?.IsAllowed == true);
        }

        private static string SourcePath([CallerFilePath] string path = "") => path;
        private const double Mm = 1.0 / 304.8;

        private static CropFrame Rotated(double degrees, double ox, double oy)
        {
            double a = degrees * Math.PI / 180;
            return new CropFrame
            {
                Origin = new[] { ox, oy, 0.0 },
                X = new[] { Math.Cos(a), Math.Sin(a), 0 }, Y = new[] { -Math.Sin(a), Math.Cos(a), 0 }, Z = new[] { 0.0, 0, 1 }
            };
        }

        [Fact]
        public void A_point_survives_the_round_trip_through_a_rotated_frame()
        {
            CropFrame f = Rotated(30, 10, -4);
            double[] p = { 3.5, 7.25, 2 };
            double[] back = f.ToModel(f.ToLocal(p));
            for (int i = 0; i < 3; i++) Assert.Equal(p[i], back[i], 9);
        }

        [Fact]
        public void On_an_aligned_plan_the_model_rectangle_is_the_crop()
        {
            CropFrame f = Rotated(0, 100, 50);
            double[] target = ViewCropGeometry.TargetFromModelRect(f, new[] { 0.0, 0, 10, 5 }, 0);
            Assert.Equal(new[] { -100.0, -50, -90, -45 }, target.Select(v => Math.Round(v, 9)).ToArray());
            double[] back = ViewCropGeometry.InCallerTerms(f, target, true);
            Assert.True(ViewCropGeometry.Matches(new[] { 0.0, 0, 10, 5 }, back, 1e-9));
        }

        [Fact]
        public void On_a_plan_rotated_45_degrees_the_crop_contains_the_asked_rectangle()
        {
            CropFrame f = Rotated(45, 0, 0);
            double[] target = ViewCropGeometry.TargetFromModelRect(f, new[] { 0.0, 0, 10, 10 }, 0);
            double side = 10 * Math.Sqrt(2);
            Assert.Equal(side, target[2] - target[0], 9);
            Assert.Equal(side, target[3] - target[1], 9);
            // every corner of the asked rectangle is inside the crop
            foreach (var c in new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 0 }, new[] { 10.0, 10, 0 }, new[] { 0.0, 10, 0 } })
            {
                double[] l = f.ToLocal(c);
                Assert.InRange(l[0], target[0] - 1e-9, target[2] + 1e-9);
                Assert.InRange(l[1], target[1] - 1e-9, target[3] + 1e-9);
            }
        }

        [Fact]
        public void A_section_takes_coordinates_on_its_own_axes_independent_of_its_origin()
        {
            // a section looking north: right = +X, up = +Z
            var f = new CropFrame { Origin = new[] { 20.0, 5, 3 }, X = new[] { 1.0, 0, 0 }, Y = new[] { 0.0, 0, 1 }, Z = new[] { 0.0, -1, 0 } };
            double[] rect = { 0, 0, 12, 9 };
            double[] target = ViewCropGeometry.TargetFromViewAxes(f, rect);
            Assert.Equal(new[] { -20.0, -3, -8, 6 }, target);
            Assert.True(ViewCropGeometry.Matches(rect, ViewCropGeometry.InCallerTerms(f, target, false), 1e-9));
        }

        [Fact]
        public void A_margin_grows_every_side()
        {
            double[] b = ViewCropGeometry.Bounds(new[] { new[] { 1.0, 2 }, new[] { 4.0, -1 } }, 0.5);
            Assert.Equal(new[] { 0.5, -1.5, 4.5, 2.5 }, b);
            Assert.Throws<ArgumentException>(() => ViewCropGeometry.Bounds(new[] { new[] { 1.0, 2 } }, -1));
        }

        [Fact]
        public void Paper_size_is_model_size_over_scale()
        {
            Assert.Equal(60.0, ViewCropGeometry.PaperMm(3000 * Mm, 50), 9);
            Assert.Equal(30.0, ViewCropGeometry.PaperMm(3000 * Mm, 100), 9);
        }

        [Fact]
        public void The_tolerance_is_one_millimetre()
        {
            double[] a = { 0, 0, 1, 1 };
            Assert.True(ViewCropGeometry.Matches(a, new[] { 0.9 * Mm, 0, 1, 1 }, ViewCropGeometry.ToleranceFeet));
            Assert.False(ViewCropGeometry.Matches(a, new[] { 1.1 * Mm, 0, 1, 1 }, ViewCropGeometry.ToleranceFeet));
        }

        [Fact]
        public void Validate_accepts_each_operation_well_formed()
        {
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'read_section_box','view_id':5}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'section_box_active','view_id':5,'value':false}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'set_section_box','view_id':5,'min':[0,0,0],'max':[1,2,3],'keep_transform':true}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'read','view_id':5}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'set_crop_box','view_id':5,'min':[0,0],'max':[1000,500],'target_document':'A'}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'set_crop_from_elements','view_id':5,'element_ids':[1,2],'margin':300}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'set_crop_from_room','view_id':5,'room_id':9}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'set_annotation_crop_offsets','view_id':5,'offsets':[5,5,5,10]}")));
            Assert.Null(ViewCropGeometry.Validate(JObject.Parse("{'operation':'crop_visible','view_id':5,'value':false,'allow_unmarked_view':true}")));
        }

        [Theory]
        [InlineData("{'operation':'set_crop_box','view_id':5,'min':[10,0],'max':[0,5]}", "max must exceed")]
        [InlineData("{'operation':'set_crop_box','view_id':5,'min':[0,0]}", "min and max")]
        [InlineData("{'operation':'read'}", "view_id")]
        [InlineData("{'operation':'crop_active','view_id':5}", "value")]
        [InlineData("{'operation':'read','view_id':5,'margin':3}", "not applicable")]
        [InlineData("{'operation':'set_annotation_crop_offsets','view_id':5,'offsets':[1,5,5,5]}", "3.2")]
        [InlineData("{'operation':'zoom','view_id':5}", "operation must be")]
        [InlineData("{'operation':'set_section_box','view_id':5,'min':[0,0],'max':[1,2]}", "finite mm")]
        [InlineData("{'operation':'set_section_box','view_id':5,'min':[0,0,0],'max':[1,2,0]}", "all three")]
        [InlineData("{'operation':'section_box_active','view_id':5}", "value")]
        public void Validate_refuses_malformed_requests(string json, string expected)
            => Assert.Contains(expected, ViewCropGeometry.Validate(JObject.Parse(json)));
    }
}

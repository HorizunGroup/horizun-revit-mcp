using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Q90FinalRegressionTests
    {
        [WindowsTheory]
        [InlineData("pdf")]
        [InlineData("png")]
        [InlineData("dwg")]
        [InlineData("ifc")]
        [InlineData("fbx")]
        public void Export_directory_is_created_only_on_apply_and_reported(string extension)
        {
            string parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(parent, "sub");
            string output = Path.Combine(folder, "export." + extension);
            try
            {
                Assert.Throws<DirectoryNotFoundException>(() => new ExportDirectoryPlan(output, true));
                var plan = new ExportDirectoryPlan(output, false);
                var preview = AssumptionNotes.Stamp(new JObject(), new[] { plan.Assumption(false) });
                Assert.True(plan.NeedsCreation);
                Assert.False(Directory.Exists(parent));
                Assert.Contains("Will create", (string)preview["assumptions"][0]["what"]);
                const string tool = "horizun_export", document = "document";
                var confirmations = new ConfirmationStore(() => DateTime.UtcNow);
                string key = output + plan.Fingerprint;
                string token = confirmations.Issue(tool, document, key).Token;
                string Confirm(string planKey)
                {
                    var check = confirmations.Validate(token, tool, document, planKey);
                    return check.Ok ? null : check.State.ToString();
                }

                Assert.NotNull(
                    ExportDirectoryPlan.Apply(plan, () => Confirm(key + ";different-plan"),
                        () => throw new InvalidOperationException("Confirmation runs first."), out JObject rejected, out var rejectedGuard));
                Assert.Null(rejected);
                Assert.Null(rejectedGuard);
                Assert.False(Directory.Exists(parent));
                Assert.Equal("document changed",
                    ExportDirectoryPlan.Apply(plan, () => null, () => "document changed", out rejected, out rejectedGuard));
                Assert.Null(rejected);
                Assert.Null(rejectedGuard);
                Assert.False(Directory.Exists(parent));

                token = confirmations.Issue(tool, document, key).Token;
                var refusal = ExportDirectoryPlan.Apply(plan, () => Confirm(key), () => null, out JObject assumption, out var guard);
                using var exportDirectory = guard;
                Assert.Null(refusal);
                var applied = AssumptionNotes.Stamp(new JObject(), new[] { assumption });
                Assert.True(Directory.Exists(folder));
                Assert.True(applied.Value<bool>("has_assumptions"));
                Assert.Equal("SUPUESTO", (string)applied["assumptions"][0]["flag"]);
                Assert.Contains(folder, (string)applied["assumptions"][0]["what"]);
                Assert.Contains("require_existing_folder=true", (string)applied["assumptions"][0]["override"]);
                var existing = new ExportDirectoryPlan(output, true);
                Assert.NotEqual(plan.Fingerprint, existing.Fingerprint);
                Assert.Null(ExportDirectoryPlan.Apply<string>(existing, () => null, () => null, out assumption, out var existingGuard));
                Assert.Null(assumption);
                Assert.Null(existingGuard);
                Assert.False(File.Exists(output));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder);
                if (Directory.Exists(parent)) Directory.Delete(parent);
            }
        }

        [WindowsFact]
        public void Apply_refuses_a_destination_directory_that_arrived_after_planning()
        {
            string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string output = Path.Combine(folder, "export.pdf");
            var plan = new ExportDirectoryPlan(output, false);
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(output, "existing output");

                var error = Assert.Throws<IOException>(() =>
                    ExportDirectoryPlan.Apply<string>(plan, () => null, () => null, out _, out _));

                Assert.Contains("appeared after planning", error.Message);
                Assert.Equal("existing output", File.ReadAllText(output));
            }
            finally
            {
                File.Delete(output);
                Directory.Delete(folder);
            }
        }

        [WindowsFact]
        public void Apply_blocks_replacing_the_new_destination_or_its_parent_during_export()
        {
            string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string parent = Path.Combine(root, "parent");
            string folder = Path.Combine(parent, "destination");
            string movedFolder = Path.Combine(parent, "moved");
            string movedParent = Path.Combine(root, "moved");
            string output = Path.Combine(folder, "export.pdf");
            var plan = new ExportDirectoryPlan(output, false);
            try
            {
                var refusal = ExportDirectoryPlan.Apply<string>(plan, () => null, () => null, out _, out var guard);
                using (guard)
                {
                    Assert.Null(refusal);
                    Assert.True(Directory.Exists(folder));
                    Assert.ThrowsAny<IOException>(() => Directory.Move(folder, movedFolder));
                    Assert.ThrowsAny<IOException>(() => Directory.Move(parent, movedParent));
                    File.WriteAllText(output, "export");
                    Assert.Equal("export", File.ReadAllText(output));
                }

                Directory.Move(folder, movedFolder);
                Directory.Move(parent, movedParent);
                Assert.Equal("export", File.ReadAllText(Path.Combine(movedParent, "moved", "export.pdf")));
            }
            finally
            {
                foreach (string possibleParent in new[] { parent, movedParent })
                {
                    foreach (string name in new[] { "destination", "moved" })
                    {
                        string possibleFolder = Path.Combine(possibleParent, name);
                        if (!Directory.Exists(possibleFolder)) continue;
                        File.Delete(Path.Combine(possibleFolder, "export.pdf"));
                        Directory.Delete(possibleFolder);
                    }
                    if (Directory.Exists(possibleParent)) Directory.Delete(possibleParent);
                }
                if (Directory.Exists(root)) Directory.Delete(root);
            }
        }

        [Fact]
        public void Split_baseline_excludes_changes_caused_by_preparing_the_endpoints()
        {
            var vertices = new Dictionary<int, object>();
            var spans = new List<double[]>();
            double[][] before = null, after = null;
            ArchitecturalEditRules.InsertSlabSplit(0, 10, () => vertices,
                (editor, point) => editor.TryGetValue(point, out object vertex) ? vertex : null,
                _ => ArchitecturalEditRules.SlabEndpoint.Interior,
                (editor, point) =>
                {
                    editor[point] = new object();
                    if (point == 10) spans.Add(new[] { 0d, 5d });
                },
                (editor, start, end, boundary) => { },
                _ => throw new InvalidOperationException("Unexpected boundary chord."),
                beforeTrace: _ => before = spans.ToArray(), afterTrace: _ => after = spans.ToArray());

            double[][] committed = { new[] { 10d, 0d } };
            Assert.Equal(0d, ArchitecturalEditRules.NewCommittedSplitCoverage(before, after, committed, 0, 10));
            after = new[] { new[] { 0d, 5d }, new[] { 8d, 5d } };
            Assert.Equal(3d, ArchitecturalEditRules.NewCommittedSplitCoverage(before, after, committed, 0, 10));
        }

        [Fact]
        public void Orthographic_crop_read_reports_the_section_clipped_depth_in_the_public_crop()
        {
            double c = Math.Sqrt(0.5);
            var view = new CropFrame { X = new[] { c, 0, -c }, Z = new[] { c, 0, c } };
            var section = new CropFrame();
            double[] camera = { -60, -10, -1000, 60, 40, 0 };
            double[] clipped = { 0, 0, 0, 40, 30, 12 };
            var result = new JObject { ["crop"] = ViewCropGeometry.DescribeCrop(view, camera, false, 100) };

            ViewCropGeometry.DescribeSectionClippedCrop(result, view, camera, true, section, clipped, 100);

            JToken crop = result["crop"];
            double[] min = crop["model_min"].Values<double>().ToArray(), max = crop["model_max"].Values<double>().ToArray();
            double[] size = crop["size_mm"].Values<double>().ToArray();
            // The existing caller reconstructs depth using the diagonal in model space.
            double depth = Math.Sqrt(min.Zip(max, (a, b) => (a - b) * (a - b)).Sum() - size.Sum(n => n * n));
            Assert.InRange(depth, 52 * c * 304.8 - 1, 52 * c * 304.8 + 1);
            Assert.Equal("clipped_section_box_bounds", crop.Value<string>("source"));
            Assert.NotNull(result["camera_crop"]);
        }

        [Fact]
        public void Empty_section_intersection_keeps_camera_geometry_out_of_the_public_crop()
        {
            var frame = new CropFrame();
            double[] camera = { -10, -10, -1000, 10, 10, 0 };
            double[] section = { 20, 20, -50, 40, 40, 0 };
            JObject cameraCrop = ViewCropGeometry.DescribeCrop(frame, camera, false, 100);
            var result = new JObject { ["crop"] = cameraCrop };

            ViewCropGeometry.DescribeSectionClippedCrop(result, frame, camera, true, frame, section, 100);

            Assert.Equal(JTokenType.Null, result["crop"].Type);
            Assert.Equal(JTokenType.Null, result["effective_crop"].Type);
            Assert.Equal("empty", result.Value<string>("effective_crop_status"));
            Assert.True(JToken.DeepEquals(cameraCrop, result["camera_crop"]));
        }
    }
}

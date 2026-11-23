using System;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Horizun.Revit.Commands;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReliabilityR2Tests
    {
        [Theory]
        [InlineData(true, null, true, false)] // central
        [InlineData(true, true, true, false)] // local detached explicitly
        [InlineData(false, null, false, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, false, false, false)]
        public void Worksharing_decision_never_opens_a_connected_local_or_central(bool shared, bool? requested, bool expected, bool refused)
        {
            string error = BatchSessionRules.DetachDecision(shared, requested, out bool detached);
            Assert.Equal(expected, detached); Assert.Equal(refused, error != null);
            Assert.Equal(shared && !detached, BatchSessionRules.SaveDecision(shared, detached) != null);
        }

        internal static void Canonical(JObject payload, int count)
        {
            Assert.True(payload.Value<bool>("has_assumptions"));
            var rows = (JArray)payload["assumptions"]; Assert.Equal(count, rows.Count);
            foreach (JObject row in rows)
            {
                Assert.Equal("SUPUESTO", (string)row["flag"]);
                foreach (string key in new[] { "what", "why", "override" }) Assert.False(string.IsNullOrWhiteSpace((string)row[key]));
            }
        }

        [Fact]
        public void Actual_batch_row_reports_each_default_and_no_empty_assumptions()
        {
            var model = JObject.Parse("{'file_path':'D:/models/a.rvt'}");
            Canonical(ReliabilityDefaults.BatchRow(model, "2025", true), 3);
            model["expected_version"] = "2025"; model["close"] = "save"; model["detach"] = true;
            Assert.Null(ReliabilityDefaults.BatchRow(model, "2025", true)["assumptions"]);
        }

        [Fact]
        public void Actual_graphics_command_refusal_carries_top_level_canonical_defaults()
        {
            var request = JObject.Parse("{'operation':'copy_view_graphics'}");
            var result = ModelEditRunner.Run(null, request.ToString(), "horizun_view_visibility", "test", ViewVisibilityRules.Validate,
                (doc, r, scale) => throw new InvalidOperationException("Invalid request must never reach the model."));
            Assert.False(result.Success); Assert.Contains("SUPUESTO", result.Error); Canonical(result.Detail, 1);
        }

        [Fact]
        public void Group_members_stair_runs_and_railing_parts_count_shared_triangles_once_per_instance()
        {
            Assert.False(ReliabilityDefaults.ExportLeaf(true, false));
            Assert.False(ReliabilityDefaults.ExportLeaf(false, true));
            Assert.True(ReliabilityDefaults.ExportLeaf(false, false));
            var triangle = new[] { new[] { 0d, 0d, 0d }, new[] { 1d, 0d, 0d }, new[] { 0d, 1d, 0d } };
            var seen = new MeshTriangleSet();
            Assert.True(seen.Add(triangle));
            Assert.False(seen.Add(Enumerable.Reverse(triangle).ToArray()));
            Assert.False(seen.Add(new[] { triangle[1], triangle[2], triangle[0] }));
            Assert.False(seen.Add(new[] { new[] { -0d, 0d, -0d }, triangle[1], triangle[2] }));
            Assert.True(new MeshTriangleSet().Add(triangle)); // separate link instance
            foreach (string category in new[] { "OST_RvtLinks", "OST_Rooms", "OST_MEPSpaces", "OST_Areas", "OST_Lines", "OST_RoomSeparationLines" })
                Assert.False(ReliabilityDefaults.SolidCategory(category));
            Assert.True(ReliabilityDefaults.SolidCategory("OST_Walls"));
            Assert.True(ReliabilityDefaults.MeshContainer("Stairs"));
            Assert.True(ReliabilityDefaults.MeshContainer("Railing"));
            foreach (string child in new[] { "StairsRun", "StairsLanding", "StairsSupport" })
                Assert.True(ReliabilityDefaults.MeshSubelement("Stairs", child));
            foreach (string child in new[] { "TopRail", "HandRail", "RailingSupport" })
                Assert.True(ReliabilityDefaults.MeshSubelement("Railing", child));
            Assert.False(ReliabilityDefaults.MeshSubelement("Railing", "Wall"));
            Assert.True(ReliabilityDefaults.MeshCategoryOrder("OST_Stairs") < ReliabilityDefaults.MeshCategoryOrder("OST_StairsRuns"));
        }

        [Theory]
        [InlineData(.02, .0204, true)]
        [InlineData(.02, -.02, false)]
        [InlineData(.02, .021, false)]
        [InlineData(0, .0001, false)]
        public void Reconnected_pipe_keeps_slope_sign_and_tolerance(double before, double after, bool expected) =>
            Assert.Equal(expected, ReliabilityDefaults.SlopePreserved(before, after));

        [Fact]
        public void Batch_read_admission_also_obeys_real_settings()
        {
            using (new EnvGuard())
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-r2-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, root);
                System.IO.Directory.CreateDirectory(root);
                try
                {
                    System.IO.File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"full_write\",\"denied_tools\":[\"horizun_view_visibility\"]}");
                    var tool = Contract.Find("horizun_view_visibility");
                    Assert.True(BatchSessionRules.AdmitsRead(tool, JObject.Parse("{'operation':'graphics_report'}")));
                    Assert.False(Settings.IsToolAllowed(tool, out _));
                    System.IO.File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"full_write\"}");
                    Assert.True(Settings.IsToolAllowed(tool, out _));
                    Assert.False(BatchSessionRules.AdmitsRead(tool, JObject.Parse("{'operation':'copy_view_graphics','dry_run':true}")));
                }
                finally { System.IO.File.Delete(HorizunPaths.SettingsPath()); System.IO.Directory.Delete(root); }
            }
        }
    }
}

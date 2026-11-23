using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class WallFinishConversionTests
    {
        private static JObject Request() => new JObject { ["operation"] = "convert_to_finish", ["target_document"] = "ARQ",
            ["wall_ids"] = new JArray(1), ["link_instance_id"] = 2, ["finish_type_id"] = 3, ["thickness_mm"] = 15, ["side"] = "exterior" };
        [Fact] public void Conversion_requires_explicit_side_type_thickness_and_target()
        {
            Assert.Null(WallFinishRules.Validate(Request()));
            foreach (string key in new[] { "side", "finish_type_id", "thickness_mm", "target_document" })
            { var r = Request(); r.Remove(key); Assert.Contains(key, WallFinishRules.Validate(r)); }
            var wrong = Request(); wrong["original_wall"] = "delete_if_unhosted";
            Assert.Contains("not applicable", WallFinishRules.Validate(wrong));
        }
        [Theory]
        [InlineData(0)] [InlineData(-15)] [InlineData(101)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
        public void Invalid_thickness_has_no_plan(double value)
        { var r = Request(); r["thickness_mm"] = value; Assert.Contains("thickness_mm", WallFinishRules.Validate(r)); }
        [Fact] public void Requested_thickness_must_match_existing_type()
        {
            Assert.True(WallFinishRules.ThicknessMatches(15 / 304.8, 15));
            Assert.False(WallFinishRules.ThicknessMatches(150 / 304.8, 15));
            Assert.False(WallFinishRules.ThicknessMatches(double.NaN, 15));
        }
        [Fact] public void Opening_must_fit_length_and_vertical_extent_before_changing_host()
        {
            Assert.True(WallFinishRules.OpeningFits(2, 1, 4, 0, 2.1, 0, 2.4));
            Assert.False(WallFinishRules.OpeningFits(.2, 1, 4, 0, 2.1, 0, 2.4));
            Assert.False(WallFinishRules.OpeningFits(2, 1, 4, .9, 1.6, 0, 2.4));
            Assert.False(WallFinishRules.OpeningFits(2, double.NaN, 4, 0, 2.1, 0, 2.4));
        }
        private static string Repo()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit"))) d = d.Parent;
            Assert.NotNull(d); return d.FullName;
        }
        [Fact] public void In_place_path_never_creates_or_deletes_walls_and_verifies_hosts_and_geometry()
        {
            string code = File.ReadAllText(Path.Combine(Repo(), "src/Horizun.Revit/Commands/WallFinishCommand.Convert.cs"));
            Assert.DoesNotContain("Wall.Create(", code); Assert.DoesNotContain(".Delete(", code);
            Assert.Contains("w.ChangeTypeId(typeId)", code);
            Assert.Contains("new TransactionGroup(d,", code); Assert.Contains("Guard.RollBack(unit)", code);
            Assert.Contains("Guard.Commit(tx,", code); Assert.Contains("edit.ApplyWithTransactions", code);
            Assert.Contains("actualIds.SequenceEqual", code); Assert.Contains("i.Host?.Id != w.Id", code);
            Assert.Contains("OpeningClear(w, snapshot, lp.Point)", code); Assert.Contains("SharedWithCore(w, core, tf)", code);
            Assert.Contains("c.UniqueId", code); Assert.Contains("edit.VerifiedItems", code); Assert.Contains("edit.PartialFailures", code);
            string runner = File.ReadAllText(Path.Combine(Repo(), "src/Horizun.Revit/Commands/ModelEditRunner.cs"));
            foreach (string field in new[] { "finish_type_id", "thickness_mm", "side", "link_instance_id" }) Assert.Contains("\"" + field + "\"", runner);
        }
    }
}

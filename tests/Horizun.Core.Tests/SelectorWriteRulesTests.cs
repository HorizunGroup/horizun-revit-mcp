// horizun_write_params_verified selector + set, without Revit. field cycle ELE (2026-10-24, L6):
// 13,858 provenance writes went through Python because the tool took only explicit rows.
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SelectorWriteRulesTests
    {
        [Fact]
        public void Set_is_a_small_map_of_scalar_values()
        {
            Assert.Null(SelectorWriteRules.SetError(new JObject { ["HZ_AI_Source"] = "agent", ["HZ_AI_Status"] = "draft" }));
            Assert.Null(SelectorWriteRules.SetError(new JObject { ["Comments"] = null, ["Count"] = 3, ["Flag"] = true }));
            Assert.Contains("set is required", SelectorWriteRules.SetError(null));
            Assert.Contains("set is required", SelectorWriteRules.SetError(new JObject()));
            Assert.Contains("one value", SelectorWriteRules.SetError(new JObject { ["Mark"] = new JArray("a", "b") }));
            var big = new JObject();
            for (int i = 0; i < 11; i++) big["P" + i] = "x";
            Assert.Contains("at most 10", SelectorWriteRules.SetError(big));
        }

        [Fact]
        public void Every_matched_element_gets_every_parameter()
        {
            var set = new JObject { ["HZ_AI_Source"] = "ELE agent", ["HZ_AI_Status"] = "draft" };
            JArray rows = SelectorWriteRules.Expand(new long[] { 7, 8, 9 }, set, false, null, out int skipped);
            Assert.Equal(6, rows.Count);
            Assert.Equal(0, skipped);
            Assert.Equal(new long[] { 7, 7, 8, 8, 9, 9 }, rows.Select(r => (long)r["target_id"]).ToArray());
            Assert.All(rows, r => Assert.Equal(new[] { "target_id", "parameter", "value" }, ((JObject)r).Properties().Select(p => p.Name).ToArray()));
        }

        [Fact]
        public void Only_if_empty_skips_filled_cells_and_never_skips_an_unreadable_one()
        {
            var set = new JObject { ["HZ_AI_Status"] = "draft" };
            // 1 empty, 2 filled, 3 unreadable (null): the unreadable one is generated so its row names the reason.
            JArray rows = SelectorWriteRules.Expand(new long[] { 1, 2, 3 }, set, true,
                (id, name) => id == 1 ? true : id == 2 ? false : (bool?)null, out int skipped);
            Assert.Equal(1, skipped);
            Assert.Equal(new long[] { 1, 3 }, rows.Select(r => (long)r["target_id"]).ToArray());
        }

        [Fact]
        public void Too_many_targets_or_writes_refuse_the_whole_call()
        {
            Assert.Null(SelectorWriteRules.SizeError(6929, false, 2));
            Assert.Contains("never silently", SelectorWriteRules.SizeError(10000, true, 1));
            Assert.Contains("never silently", SelectorWriteRules.SizeError(10001, false, 1));
            Assert.Contains("20000", SelectorWriteRules.SizeError(9000, false, 3));
        }
    }
}

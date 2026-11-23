// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A read-only Python run rolls back like a rehearsal: its spatial headline must not
// say "The write is committed" about work that was never kept.
// -----------------------------------------------------------------------------
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NoCommitRulesTests
    {
        [Theory]
        [InlineData("{\"dry_run\":true}", true)]
        [InlineData("{\"read_only\":true}", true)]
        [InlineData("{\"dry_run\":false,\"read_only\":true}", true)]
        [InlineData("{\"dry_run\":false,\"read_only\":false}", false)]
        [InlineData("{}", false)]
        [InlineData("{\"read_only\":null}", false)]
        public void Only_a_rehearsal_or_a_read_only_run_leaves_nothing(string json, bool expected) =>
            Assert.Equal(expected, NoCommitRules.LeftNothing(JObject.Parse(json)));

        [Fact]
        public void No_data_means_it_cannot_be_said_that_nothing_was_left() => Assert.False(NoCommitRules.LeftNothing(null));

        [Fact]
        public void The_spatial_headline_asks_the_rule_and_not_only_dry_run()
        {
            string dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir, "src", "Horizun.Revit", "Core", "SpatialAfterWrite.cs"));
            Assert.Contains("SpatialCoherence.Headline(o, NoCommitRules.LeftNothing(data))", source);
            Assert.DoesNotContain("Headline(o, data.Value<bool?>(\"dry_run\") == true)", source);
        }
    }
}

using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// A PLAN IS APPLICABLE ONLY WHEN THE CORRESPONDENCE WAS SHOWN.
    ///
    /// The rule these fix is one-directional: four facts go in, and the only shape of the evidence that
    /// grants permission is "the record describes what is loaded AND the sources still hash as they did".
    /// The first live case of block 8 caught the opposite failure - a link nobody had touched read as
    /// changed, because the two fingerprints were taken with different parameters - and it was caught
    /// because it fell on the withholding side. These keep both directions honest.
    /// </summary>
    public class CadSourceCoherenceTests
    {
        private static JObject Record(string set = "set:AAA", string print = "fp:111", string file = "sha:host1")
            => new JObject
            {
                ["source_set_sha256"] = set,
                ["geometry_fingerprint"] = print,
                ["file_sha256"] = file,
                ["loaded_utc"] = "2026-09-20T00:00:00Z",
                ["by"] = "horizun_manage_cad_links add"
            };

        [Fact]
        public void The_record_describes_what_is_loaded_and_the_sources_have_not_moved()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", "fp:111", false);
            Assert.Equal(CadSourceCoherenceRules.Aligned, d.Value<string>("state"));
            Assert.True(d.Value<bool>("applicable"));
            Assert.Null(d["why"]);
        }

        [Fact]
        public void A_reference_revised_since_the_load_is_not_aligned_even_when_the_host_is_untouched()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), "set:BBB", "sha:host1", "fp:111", false);
            Assert.Equal(CadSourceCoherenceRules.NotAligned, d.Value<string>("state"));
            Assert.False(d.Value<bool>("applicable"));
            Assert.Equal("the_sources_changed_since_the_link_was_loaded", d.Value<string>("why"));
            // THE HOST'S OWN HASH DID NOT MOVE, and the reply says so: that is the whole case.
            Assert.False(d["differs"].Value<bool>("host_file_changed"));
            Assert.Equal("set:AAA", d["differs"].Value<string>("source_set_when_loaded"));
            Assert.Equal("set:BBB", d["differs"].Value<string>("source_set_now"));
            Assert.Contains("reload", d.Value<string>("remedy"));
        }

        [Fact]
        public void A_link_reloaded_outside_this_bridge_is_unknown_and_not_a_disagreement()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", "fp:999", false);
            Assert.Equal(CadSourceCoherenceRules.Unknown, d.Value<string>("state"));
            Assert.Equal("the_link_changed_after_this_bridge_recorded_it", d.Value<string>("why"));
            // It does NOT claim the sources disagree: what is loaded may well be current, and saying
            // otherwise would be the false alarm that made this rule worth testing.
            Assert.False(d.Value<bool>("applicable"));
            Assert.DoesNotContain("older issue", d.Value<string>("means"));
        }

        [Fact]
        public void No_record_at_all_is_unknown_not_aligned()
        {
            JObject d = CadSourceCoherenceRules.Decide(null, "set:AAA", "sha:host1", "fp:111", false);
            Assert.Equal(CadSourceCoherenceRules.Unknown, d.Value<string>("state"));
            Assert.Equal("no_record_of_loading_this_link", d.Value<string>("why"));
            Assert.False(d.Value<bool>("applicable"));
        }

        [Fact]
        public void A_set_that_could_not_be_identified_on_either_side_is_unknown()
        {
            Assert.Equal("the_source_set_could_not_be_identified",
                CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:111", false).Value<string>("why"));
            Assert.Equal("the_source_set_could_not_be_identified",
                CadSourceCoherenceRules.Decide(Record(set: null), "set:AAA", "sha:host1", "fp:111", false).Value<string>("why"));
        }

        [Fact]
        public void A_geometry_that_could_not_be_fingerprinted_is_unknown_and_says_which_it_was()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", null, false);
            Assert.Equal(CadSourceCoherenceRules.Unknown, d.Value<string>("state"));
            Assert.Equal("the_geometry_could_not_be_fingerprinted", d.Value<string>("why"));
        }

        [Fact]
        public void A_continued_snapshot_is_never_applicable_however_well_everything_else_lines_up()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", "fp:111", true);
            Assert.Equal(CadSourceCoherenceRules.Snapshot, d.Value<string>("state"));
            Assert.False(d.Value<bool>("applicable"));
            Assert.Contains("checked nothing", d.Value<string>("means"));
        }

        [Fact]
        public void Only_one_shape_of_the_evidence_grants_permission()
        {
            var cases = new[]
            {
                CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", "fp:111", false),   // the only yes
                CadSourceCoherenceRules.Decide(Record(), "set:BBB", "sha:host1", "fp:111", false),
                CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", "fp:999", false),
                CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", "fp:111", true),
                CadSourceCoherenceRules.Decide(null, "set:AAA", "sha:host1", "fp:111", false),
                CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:111", false),
                CadSourceCoherenceRules.Decide(Record(), "set:AAA", "sha:host1", null, false)
            };
            int yes = 0;
            foreach (JObject c in cases)
            {
                if (c.Value<bool>("applicable")) yes++;
                // every refusal names a remedy, because a state a caller cannot leave is a dead end
                if (!c.Value<bool>("applicable")) Assert.False(string.IsNullOrWhiteSpace(c.Value<string>("remedy")));
            }
            Assert.Equal(1, yes);
        }

        [Fact]
        public void A_host_file_that_changed_is_reported_as_changed()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), "set:BBB", "sha:host2", "fp:111", false);
            Assert.Equal(CadSourceCoherenceRules.NotAligned, d.Value<string>("state"));
            Assert.True(d["differs"].Value<bool>("host_file_changed"));
        }
    }
}

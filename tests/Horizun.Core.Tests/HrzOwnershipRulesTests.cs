// HRZ ownership mark (FIELD FEEDBACK 4.1): when the dispatcher stamps, and the gate of the restricted tools.
using System.IO;
using System.Runtime.CompilerServices;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HrzOwnershipRulesTests
    {
        [Fact]
        public void A_committed_write_is_stamped()
            => Assert.True(HrzOwnershipRules.ShouldStamp("horizun_manage_views", true, true, false));

        [Fact]
        public void A_rehearsal_a_failure_or_a_read_is_not()
        {
            Assert.False(HrzOwnershipRules.ShouldStamp("horizun_manage_views", true, true, true));
            Assert.False(HrzOwnershipRules.ShouldStamp("horizun_manage_views", true, false, false));
            Assert.False(HrzOwnershipRules.ShouldStamp("horizun_query_model", false, true, false));
        }

        [Fact]
        public void A_failed_write_still_marks_what_it_committed_before_failing()
        {
            // A typed write that commits a transaction and then raises leaves elements in the model:
            // they are ours and must carry the mark (4.2-a, universal mark). A script's are not (R2V M-1).
            Assert.False(HrzOwnershipRules.ShouldStampAfterFailure("horizun_execute_python", true));
            Assert.True(HrzOwnershipRules.ShouldStampAfterFailure("horizun_create_elements", true));
            Assert.False(HrzOwnershipRules.ShouldStampAfterFailure("horizun_query_model", false));
            Assert.False(HrzOwnershipRules.ShouldStampAfterFailure("horizun_document_session", true));
            Assert.False(HrzOwnershipRules.ShouldStampAfterFailure("horizun_undo", true));
            Assert.False(HrzOwnershipRules.ShouldStampAfterFailure(null, true));
        }

        [Fact]
        public void The_report_merges_into_a_failure_detail_without_losing_its_fields()
        {
            var detail = new Newtonsoft.Json.Linq.JObject { ["error_code"] = "python_excepcion" };
            var merged = HrzOwnershipRules.MergeIntoDetail(detail, HrzOwnershipRules.Report(2, 0, null));
            Assert.Equal("python_excepcion", (string)merged["error_code"]);
            Assert.Equal(2, (int)merged["ownership_stamp"]["stamped"]);
            Assert.Null(detail["ownership_stamp"]);
            Assert.Equal(1, (int)HrzOwnershipRules.MergeIntoDetail(null, HrzOwnershipRules.Report(1, 0, null))["ownership_stamp"]["stamped"]);
        }

        private static string Here([CallerFilePath] string here = "") => here;

        [Fact]
        public void The_dispatcher_hook_stamps_a_failed_call_and_reports_in_its_detail()
        {
            string own = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here()), "..", "..", "src", "Horizun.Revit", "Core", "HrzOwnership.cs"));
            Assert.Contains("ShouldStampAfterFailure(tool, mutating)", own);
            Assert.Contains("result.ReplaceDetail(HrzOwnershipRules.MergeIntoDetail(result.Detail, report))", own);
            Assert.DoesNotContain("!result.Success || changes == null", own);
        }

        [Theory]
        [InlineData("horizun_document_session")]
        [InlineData("horizun_undo")]
        [InlineData("horizun_open_document")]
        public void Sessions_syncs_and_undo_never_claim_what_they_bring_in(string tool)
            => Assert.False(HrzOwnershipRules.ShouldStamp(tool, true, true, false));

        [Fact]
        public void The_report_carries_stamped_skipped_and_error()
        {
            var r = HrzOwnershipRules.Report(3, 1, null, new[] { "internal (no category)" });
            Assert.Equal(3, (int)r["stamped"]);
            Assert.Equal(1, (int)r["skipped"]);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, r["error"].Type);
            Assert.Single(r["skip_reasons"]);
        }

        [Fact]
        public void The_gate_admits_marked_session_created_or_overridden_and_refuses_the_rest()
        {
            Assert.Null(HrzOwnershipRules.Gate("view", 5, true, false, false, "allow_unmarked_view"));
            Assert.Null(HrzOwnershipRules.Gate("view", 5, false, true, false, "allow_unmarked_view"));
            Assert.Null(HrzOwnershipRules.Gate("view", 5, false, false, true, "allow_unmarked_view"));
            string refusal = HrzOwnershipRules.Gate("view", 5, false, false, false, "allow_unmarked_view");
            Assert.Contains("allow_unmarked_view=true", refusal);
            Assert.Contains("Nothing was written", refusal);
        }
    }
}
